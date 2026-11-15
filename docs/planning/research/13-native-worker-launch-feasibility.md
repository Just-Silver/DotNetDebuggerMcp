<!-- 职责：记录「子会话 worker 是否能不依赖计划任务投送」的源码分析、权限边界、PoC 实测与结论；不写：产品实现步骤（见 SharpSight.ChildSession/AGENTS.md）与登录启动项隔离（见 research/12）。 -->

# 子会话 Worker 原生投送可行性（2026-11-16）

## 1. 问题

当前 worker 通过计划任务 `IRegisteredTask::RunEx(..., TASK_RUN_USE_SESSION_ID, childId, ...)` 投送进子会话（`src/SharpSight.ChildSession/Connection/ChildWorkerLauncher.cs`）。该路径可用但有两个副作用：

1. 每次 start 在系统里临时注册一个计划任务（无触发器，握手后即删）；
2. 计划任务接口没有 `CREATE_NO_WINDOW`，控制台子系统 worker 会带出一次可见终端窗口。

目标：评估能否改用**原生进程创建**（不注册计划任务），在**普通用户权限**下把 worker 放进目标 child 会话。

## 2. 参考实现：LocalRDP `src/session_launch.cpp`

上游 <https://github.com/charles-cty/LocalRDP> 的 `LaunchInSession` 是**三段式回退**，每段成功都必须通过 `AcceptLaunch` 校验：

```
AcceptLaunch(session_id, pid):
    用 ProcessIdToSessionId(pid) 核对
    不对 → TerminateProcess(pid) 并判定本次失败
```

| 顺序 | 策略 | 关键调用 | 依赖的特权 |
|---|---|---|---|
| 1 | 会话用户令牌 | `EnablePrivilege(SE_TCB / SE_ASSIGNPRIMARYTOKEN / SE_INCREASE_QUOTA)` → `WTSQueryUserToken(session)` → `CreateProcessAsUser`（`lpDesktop=winsta0\default`、`CreateEnvironmentBlock`） | LocalSystem + `SeTcbPrivilege` |
| 2 | 会话内同用户进程令牌 | 枚举 `WTSEnumerateProcessesW` 挑会话内进程（explorer 100 / userinit 80 / sihost 60 / RuntimeBroker 40，winlogon·csrss·smss·services·lsass 记 0；同用户 +20）→ `OpenProcess(PROCESS_CREATE_PROCESS\|QUERY_INFORMATION\|DUP_HANDLE)` → `OpenProcessToken(TOKEN_DUPLICATE\|QUERY\|ASSIGN_PRIMARY)` → `DuplicateTokenEx(TokenPrimary)` → `CreateProcessAsUser` | `SeIncreaseQuota`（令牌非「调用者令牌的受限版本」时还需 `SeAssignPrimaryToken`） |
| 3 | 父进程属性 | `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` + `CreateProcessW`，`lpDesktop` 留空以继承父进程的窗口站/桌面 | 只需对父进程句柄有 `PROCESS_CREATE_PROCESS` |

几处值得记住的实现细节：

- 第 2/3 段的父进程**必须**在目标会话内，且优先 `explorer.exe`；系统进程被显式排除（`ScoreProcess` 返回 0）。
- 第 3 段故意**不**写 `winsta0\default`，注释是「继承父进程的窗口站/桌面，不要把本会话的 winsta0 写进去」。
- 每次都**实际核对落地会话**，不符就杀进程；不做「创建成功即认为成功」的判断。
- 循环最多 8 次、每次间隔 400 ms，容忍子会话登录早期进程尚未就绪。

## 3. 官方语义核对

- `CreateProcessAsUser`：「typically the calling process must have `SE_INCREASE_QUOTA_NAME` and **may require** `SE_ASSIGNPRIMARYTOKEN_NAME` if the token is not assignable」；只有当 `hToken` 是**调用者主令牌的受限版本**时才免 `SeAssignPrimaryToken`。进程落在**令牌自带的 session**（`TokenSessionId`），改它需要 `SeTcbPrivilege`。
- `bInheritHandles=TRUE` 时「必须在与调用方相同的 session 创建进程」——跨会话投送必须 `FALSE`（我们的 IPC 走命名管道，不依赖句柄继承，满足该约束）。
- `lpDesktop=NULL` 时「新进程继承其父进程的桌面与窗口站」；要交互必须 `winsta0\default` 且该窗口站/桌面 DACL 允许令牌对应用户。
- `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS`：「新进程**继承**指定父进程的句柄、设备映射、处理器亲和性、优先级、配额、**进程令牌**与 Job 对象」；句柄只需 `PROCESS_CREATE_PROCESS`。**文档未承诺它改变新进程的 session**。
- `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` 常量值 = `ProcThreadAttributeValue(0, FALSE, FALSE, FALSE)` = **`0x00020000`**（`ProcThreadAttributeParentProcess = 0` + `PROC_THREAD_ATTRIBUTE_INPUT`；用 ReactOS `winbase.h` 与 `PSBits/FakeParentWithConsole.c` 双向核对）。

## 4. 本机权限边界

账户 `yin\13178` **属于本机 Administrators 组**（`net localgroup Administrators` 可见 `13178`），但当前 MCP 宿主进程是 **UAC 过滤后的 Medium 令牌**——`whoami /groups` 中 `BUILTIN\Administrators` 标为 “Group used for deny only”，因此该令牌里：

```
Medium Mandatory Level
无 SeAssignPrimaryToken / SeIncreaseQuota / SeTcb / SeImpersonate / SeDebug
```

即：下面的失败是「**进程未提权**」造成的，**不是**「账户没有管理员权限」。worker/holder 由宿主创建、继承宿主令牌，所以宿主不提权时它们同样没有这些特权。

UAC 提权后的对照（同机实测，High 完整性）：

```
SeIncreaseQuotaPrivilege   Disabled   ← 存在（CreateProcessAsUser 会临时启用）
SeImpersonatePrivilege     Enabled    ← 存在
SeAssignPrimaryToken       —— 不存在（本机默认策略下 Administrators 未持有）
SeTcb                      —— 不存在
```

提权**并不能**解锁第 2 段：`CreateProcessAsUser` 在「令牌不是调用者主令牌的受限版本」时必须 `SeAssignPrimaryToken`，实测仍返回 1314（见 §6.1）。

## 5. PoC 与实测

PoC 放在既有夹具，可复现：`tests/Fixtures/ChildSessionTarget/`（`native-launch` / `report-out` 两个模式）。

```
ChildSessionTarget.exe native-launch <目标会话> <exe> [参数...]
```

它做四件事：挑会话内同用户进程作父进程 → 记录普通启动（control）、父进程属性、`CreateProcessAsUser`、`WTSQueryUserToken` 各自结果 → 每个成功创建的进程都用 `ProcessIdToSessionId` 核对会话，不符即终止 → 被投送进程自己写 JSON 回报 `session`。

实测（child 会话 16，父会话 1，未提权 Medium 令牌）：

| 路径 | 结果 | 说明 |
|---|---|---|
| control（普通 `CreateProcessW`） | ✅ 创建成功，**session=1** | 证明参数/结构无误 |
| `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS`（父=会话 16 的 explorer.exe） | ✅ 创建成功，**session=1**，`sessionMatches=false` | **只伪造父子关系与继承属性，不迁移会话** |
| 同上（父=自身进程） | ✅ 创建成功，**session=1** | 同上，排除目标句柄权限因素 |
| 属性列表探针（`MITIGATION_POLICY`） | ✅ 创建成功 | 证明属性列表管路本身可用 |
| 复制会话内进程令牌 + `CreateProcessAsUser` | ❌ **1314 `ERROR_PRIVILEGE_NOT_HELD`** | 缺 `SeIncreaseQuota`/`SeAssignPrimaryToken` |
| `WTSQueryUserToken(16)` | ❌ **1314** | 需要 LocalSystem + `SeTcb` |
| 被投送进程自报 | `{"session":1,...}` | 与 API 结果一致 |

调试过程中踩到的坑（对后续复现有用）：`PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` 的值会以**指针**形式存进属性列表，官方要求该值存活到属性列表销毁；如果把句柄放在托管栈的临时变量里再传 `ref`，函数返回后栈槽失效，`CreateProcessW` 会报 `ERROR_INVALID_HANDLE(6)`。PoC 改为把句柄副本放进 `AllocHGlobal` 后才得到上面这组真实结果。

## 6. 结论

**在未提权的宿主进程（UAC 过滤的 Medium 令牌）下，不依赖计划任务把进程投送进 child 会话不可行；提权（High）后同样不可行。** 三条路各自被不同机制挡住：

1. `WTSQueryUserToken` —— 权限模型上就要求 LocalSystem + `SeTcb`（提权到管理员也不够）；
2. `CreateProcessAsUser` —— 需要调用方持有 `SeIncreaseQuota`（必要时还有 `SeAssignPrimaryToken`），未提权令牌没有；
3. `PARENT_PROCESS` —— 官方只承诺继承属性，**不改变 session**；实测确认新进程仍落在调用方会话（与权限无关，提权也不会变）。

因此维持现状：**`RunEx` 仍是唯一不需要提权的投送方式**，worker 控制台窗口继续用「启动即 `FreeConsole`」处理（见 spec §5.5）。

### 6.1 提权后仍然不行（本机已实测）

本账户在 Administrators 组，可用 UAC 提权；提权后（High 完整性，PoC 自报 `"elevated": true`）重跑 PoC 的结果仍是：

| 路径 | High 令牌结果 |
|---|---|
| `WTSQueryUserToken` | ❌ 1314（要 LocalSystem + `SeTcb`，管理员不够） |
| 复制 child 会话内令牌 + `CreateProcessAsUser` | ❌ **1314** |
| `PARENT_PROCESS` | ⚠️ 仍落在调用方会话（与权限无关） |

原因：本机策略下管理员令牌**不含 `SeAssignPrimaryToken`**（`whoami /priv` 提权后可见 `SeIncreaseQuota`/`SeImpersonate`，但没有 `SeAssignPrimaryToken`/`SeTcb`）。`CreateProcessAsUser` 只有在「令牌是调用者主令牌的受限版本」时才免该特权；我们要用的令牌来自**另一个会话**的同用户进程，不可能满足这个条件。

同族 API 也堵死：`CreateProcessWithTokenW` 只需 `SeImpersonate`（提权后有），但官方明确「**调用方进程始终运行在调用方会话，而不是令牌里的会话；要在令牌会话里创建进程必须用 `CreateProcessAsUser`**」。

因此本机要打通原生投送，只剩三条路，都不在允许范围内：

1. 用 `secpol`/`Secedit` 把 `SeAssignPrimaryToken` 授予该账户 —— 属**修改安全策略**，任务明确禁止；
2. 以 **LocalSystem** 服务运行（`SeTcb` + `SeAssignPrimaryToken` 齐全）—— 属新增系统服务/提权，任务明确禁止；
3. **`RunEx` 计划任务** —— 现状，无需任何特权。

结论：**不需要再实现「提权快路径」**。原先把「提权即可」当作前提是错判，已由本机 High 令牌实测推翻。

## 7. 明确边界（避免误归因）

本结论**与 Steam / Clash Verge 等登录启动项在 child 会话里被重复触发无关**。后者是 child 会话登录时运行用户 Run/启动项与 `LogonTrigger` 计划任务导致的，属 `research/12-child-session-startup-isolation.md` 的范围；换 worker 投送方式既不增加也不减少这些启动项。即使将来实现「提权重连投送」，也不会消除它们。

## 8. 复现

```powershell
# 前提：已有活动 child 会话（例如 child_session start 后取 identity.childSessionId）
tests/TestData/ChildSessionTarget/ChildSessionTarget.exe native-launch <childId> `
    tests/TestData/ChildSessionTarget/ChildSessionTarget.exe report-out "$env:TEMP\poc.json"
Get-Content "$env:TEMP\poc.json"
```

注意：PoC 会真实创建进程；`sessionMatches=false` 的进程会被立即终止，matched 的进程执行完 `report-out` 自行退出。
