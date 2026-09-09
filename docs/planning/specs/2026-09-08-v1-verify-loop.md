# Spec · V1 一键复验闭环（debug_verify）

> 状态：**已立项**（2026-09-09 拍板）——① build 为**可选字段**，编译产物**自动定位**（build 成功后 `dotnet msbuild -getProperty:TargetPath` 拿产物启动，agent 不写路径；`target.commandLine` 写 exe 文件名+参数，无 build 时支持完整路径/PATH）；② 场景载体=**文件路径**（scenarioPath）；③ **fail-fast**（断言失败即停）；④ 依赖策略=**ui.*/改值步骤类型预留**、v1 e2e 用纯断点+输出+evaluate 断言版先行（evaluate 走 P6 读值，不依赖 W1/U1）。实施计划见 `docs/planning/plans/2026-09-09-v1-verify-loop.md`，规格冻结。
> 关联：宿主 TODO V1；ROADMAP reverse-skill「调试-修复-重验证闭环」候选（本 spec 是其落地方案）；U1（触发源）/W1（实验改值）是其能力前提——步骤类型预留、执行按依赖就绪度排期。

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
    // 产物由 verify 自动拿：dotnet msbuild -getProperty:TargetPath（bin\<配置>\<TFM>\<AssemblyName>.exe），agent 不写路径
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
- 重编译：宿主起 `dotnet build <project> -c <config>` 子进程（**排空 stdout/stderr 纪律已有**，防管道阻塞）；**产物自动定位**：build 成功后 `dotnet msbuild <project> -p:Configuration=<config> -getProperty:TargetPath` 取产物绝对路径（SDK 现算，agent 不查路径/TFM）；`target.commandLine` 写「exe 文件名 + 参数」（不写路径），文件名与产物不一致 → 中文提示；build 失败返回编译错误摘要（stderr 尾部+退出码）即停，**绝不启动旧产物**；**无 build 字段**时 commandLine 支持完整路径或 PATH 内命令（维持现语义）。
- 新工具 `debug_verify`：参数 `scenarioPath`（场景 JSON 文件路径，必填）。
- README 同步（新工具+场景格式示例）。

## 5. 拍板记录（2026-09-09 用户拍板）

1. **编译步**：v1 含 **可选 build**（场景写 build 字段才编译）；**产物自动定位**——build 成功后 verify 用 `dotnet msbuild <project> -p:Configuration=<config> -getProperty:TargetPath` 拿产物绝对路径并启动（agent 不写路径/不查 TFM）；`target.commandLine` 写「exe 文件名+参数」（无 build 字段时支持完整路径/PATH 命令）；文件名与产物不一致中文提示；build 失败即停返错误摘要（绝不启动旧产物）。
2. **场景载体**：**文件路径**（`debug_verify(scenarioPath)`，.json 文件可存仓库复用；V2 录制产出同格式文件承接）。
3. **失败策略**：**fail-fast**——断言失败即停，返回失败步骤与实际输出。
4. **依赖策略**：场景步骤类型**预留** ui.*（触发，依赖 U1）/改值（前置条件，依赖 W1）/复盘（v3 debug_timeline 关联）——运行时依赖未就绪则明确报「步骤类型依赖未就绪」；v1 验收与先行 = 纯断点+output+evaluate 断言（evaluate 走 P6 读值，不依赖 W1/U1）。

## 6. 验证方案
- **宿主单测**：场景解析（合法/非法穷尽）+ 断言执行器（各 kind 桩数据 pass/fail）。
- **宿主 e2e**：DebugTarget 造一个可复现 bug（如 Work 读错常量）→ 场景=断点→断言异常→ **改源码→debug_verify→PASS**（真闭环）。
- 编译失败路径、超时路径中文提示。

## 7. 依赖与工作量
- 依赖：W1/U1/V3 部分就绪（纯断点版可先行，估中）；`debug_launch` 可复现（✅）。
- 改动面：宿主（VerifyService + debug_verify 工具 + 场景格式），无 Engine/Session 改动。
- 难度：**中-大**（编排复杂度在"步骤翻译的完整性"与"错误分类"；格式先行简化）。
