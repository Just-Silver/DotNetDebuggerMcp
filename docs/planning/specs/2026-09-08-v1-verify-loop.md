# Spec · V1 一键复验闭环（debug_verify）

> 状态：**计划中（草案）**——编排层设计，复用设施已查证。实施前置：宿主 TODO V1 立项 + **依赖 W1/U1/V3 至少一项就绪**（V1 的断言用 W1 改值、触发用 U1 点击、复盘用 V3 时间线才完整；纯断点版可先行）。
> 关联：宿主 TODO V1；ROADMAP reverse-skill「调试-修复-重验证闭环」候选（本 spec 是其落地方案）；U1（触发源）/W1（实验改值）是其能力前提。

## 1. 背景与目标

agent 改完 bug 后缺**自证修复的最后一跳**：改码 → 重编译 → `debug_launch` 重启 → 重跑关键场景 → 断言行为变化。现状 agent 要自己拿 shell 编译、手动重跑、靠 `debug_output` 猜结果——任务"看似完成"但无验证闭环。

**V1 能力**：一条命令把「启动参数快照 + 场景动作序列 + 期望断言」跑成 **pass/fail**：
```
debug_verify <场景文件> → 自动：重编译(可选) → debug_launch(快照参数) → 执行场景步骤
                        → 每步断言 → 汇总 PASS/FAIL（含每步实际输出，供 agent 定位）
```

非目标：完整测试框架（不替代 xUnit/场景录制 GUI）；跨语言；无源码目标的"编译"步（编译只适用于 agent 有源码可改的场景，见 §5 取舍）。

## 2. 现状可复用设施（起草时已查证）

| 设施 | 现状 | V1 复用 |
|---|---|---|
| `debug_launch(commandLine, workingDirectory, environment)` | ✅ 启动参数全可复现（D 项完成） | 重启目标 |
| 断点/trace/run_to/条件断点/evaluate | ✅ 全工具 | 场景步骤原语 |
| `debug_wait`/`debug_state` | ✅ 等停点/查状态 | 步骤间同步 |
| `AgentActionLog`（Actions） | ✅ 轨迹日志 | 场景执行轨迹记录 |
| `ProcessOutputCapture` | ✅ 目标输出 | 输出断言源 |
| **重编译** | ❌ 无（agent 自拿 shell） | 新增 |
| **场景脚本格式** | ❌ 无 | 新增（核心设计） |
| **断言执行器** | ❌ 无 | 新增 |

## 3. 核心设计：场景脚本 + 断言模型

### 3.1 场景脚本格式（JSON，步骤数组）

```jsonc
{
  "name": "切换手自动复验",
  "target": {                          // debug_launch 快照（可复现启动）
    "commandLine": "CoreMes.exe",
    "workingDirectory": "",            // 空=exe 目录
    "environment": "ASPNETCORE_ENVIRONMENT=Development"
  },
  "build": {                           // 可选：先重编译（agent 有源码时）
    "project": "D:\\proj\\CoreMes.csproj",
    "configuration": "Debug"
  },
  "steps": [
    { "breakpoint": { "typeName": "CoreMes.Core.ApplicationContext", "memberName": "SwitchState", "hit": 1 } },
    { "continue": { "waitSeconds": 10 } },
    { "assert": { "kind": "breakpointHit", "breakpointIndex": 0 } },          // 断点命中
    { "ui": { "tool": "ui_invoke", "args": { "index": 5 } } },                // U1 触发（若已落地）
    { "wait": { "text": "自动", "timeoutSeconds": 10 } },                     // U1 ui_wait 状态确认
    { "assert": { "kind": "evaluate", "path": "ApplicationContext.State", "equals": "自动" } },
    { "output": { "contains": "切换完成", "stream": "err" } }                  // 日志断言
  ]
}
```

### 3.2 断言原语（v1 最小集）

| kind | 语义 | 数据源 |
|---|---|---|
| `breakpointHit` | 指定断点被命中 | debug_wait 停点 |
| `evaluate` | 表达式结果 equals/contains | P6 求值 |
| `output` | 目标输出含/不含子串 | ProcessOutputCapture |
| `state` | 会话到 Stopped/Exited | debug_state |
| `noException` | 期间无异常停点 | 事件 |

### 3.3 执行器（宿主侧 `debug_verify`）

编排 = 把场景步骤**翻译成现有 MCP 工具的内部调用序列**（直接调 DebugSessionService/Session 层，不走 MCP 往返——同一进程内复用）。每步记录：步骤名、调用摘要、结果摘要 → 进 AgentActionLog。断言失败即停（fail-fast），返回：
```
PASS: 5/5 步骤，断言 3/3 通过
FAIL: 步骤 3 continue 超时（10s 无停点）——断点未命中或代码路径未走到
```

### 3.4 关键设计决策：不做"录制"，先做"手写场景"
- **不做** UI 录制回放（agent 把动作录下来）——v1 是**结构化手写场景**（JSON 步骤），agent 根据反编译/已知流程写。录制是 v2 增强（从 AgentActionLog 自动生成场景骨架）。
- 理由：录制依赖动作与断点的精确时序绑定，v1 复杂；手写场景对 agent（能读代码）成本低且可控。

## 4. 分层设计
- **宿主新增 `Services/VerifyService`**（或 `ToolExecutor` 扩展）：场景解析 + 步骤翻译 + 断言执行 + 结果汇总。
- 步骤执行**复用现有 DebugSessionService/Session 方法**（同进程直调，非 MCP 往返）——断言需要"过程内等停点"细节，MCP 工具粒度太粗。
- 重编译：宿主起 `dotnet build <project> -c <config>` 子进程（**排空 stdout/stderr 纪律已有**，防管道阻塞）；失败返回编译错误摘要。
- 新工具 `debug_verify`：参数 `scenarioPath`（场景 JSON 文件路径，必填）。
- README 同步（新工具+场景格式示例）。

## 5. 待拍板（立项时决策）
1. **编译步是否 v1 含**：含 = 覆盖"agent 改源码→复验"主场景（但需目标有源码+本机 SDK）；不含 = 只做"重启+重跑+断言"（agent 自己先编译好）。**建议 v1 含**（否则闭环缺"改码"半环）。
2. **场景文件 vs 场景 JSON 字符串参数**：文件（可复用/可存仓库）vs 参数内联（MCP 参数过大）。倾向文件路径。
3. **断言失败后是否继续后续步骤**：fail-fast（推荐）vs 全跑完汇总。
4. **依赖 W1/U1 的程度**：纯断点+输出断言版可先行（不依赖 W1/U1）；改值断言（W1）与 UI 触发（U1）是增强断言源。

## 6. 验证方案
- **宿主单测**：场景解析（合法/非法穷尽）+ 断言执行器（各 kind 桩数据 pass/fail）。
- **宿主 e2e**：DebugTarget 造一个可复现 bug（如 Work 读错常量）→ 场景=断点→断言异常→ **改源码→debug_verify→PASS**（真闭环）。
- 编译失败路径、超时路径中文提示。

## 7. 依赖与工作量
- 依赖：W1/U1/V3 部分就绪（纯断点版可先行，估中）；`debug_launch` 可复现（✅）。
- 改动面：宿主（VerifyService + debug_verify 工具 + 场景格式），无 Engine/Session 改动。
- 难度：**中-大**（编排复杂度在"步骤翻译的完整性"与"错误分类"；格式先行简化）。
