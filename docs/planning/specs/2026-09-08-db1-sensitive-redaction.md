# Spec · DB1 变量敏感脱敏层

> 状态：**已立项**（2026-09-09 拍板）——覆盖范围 = 读值渲染出口（debug_variables 全量/children、debug_evaluate 标量+children、带变量渲染的 trace/停点上下文）+ 表达式级绕过；Web 监视器不同步（宿主层即可）。实施计划见 `docs/planning/plans/2026-09-09-db1-sensitive-redaction.md`，规格冻结。
> 关联：宿主 TODO DB1；参照 microsoft/DebugMCP `src/utils/secretRedaction.ts`（本地 `../../Externals/DebuggerExternals/DebugMCP/`，MIT，只借鉴设计不抄整体代码）；配套 DB2（按名白名单）缩小暴露面。

## 1. 背景与目标

agent 读到的敏感值（api_key/password/token/JWT/PEM/连接串等）会**跨信任边界进 LLM 模型**，现状读值输出零脱敏。目标：宿主读值渲染出口统一走脱敏管线——**按变量/字段名** + **按内容形态**双模式过滤，null/平凡值不动（「为什么我的 token 是 null」仍可调）。

非目标：目标自身控制台输出（debug_output/timeline log 行）v1 不脱敏（目标自打内容，整行替换破坏调试）；异常 Message 不脱敏（异常文本通常非凭据，避免误伤诊断）；Web 监视器展示不脱敏（本地人类观看席，2026-09-09 拍板）；Engine/Session 层不脱敏（渲染时脱敏，纯能力层不感知展示策略）。

## 2. 规则（对齐 DebugMCP 双模式，2026-09-08 已实读）

| 模式 | 规则 | 说明 |
|---|---|---|
| **按名** | `SENSITIVE_NAMES` 全集（Key/Secret/Password/Token/Credentials/Auth/ConnectionString/厂商专属拼写，约 90 项） | **归一化 exact-match**：小写 + 去 `_`/`-`/空格分隔符（`API_KEY`/`api-key`/`apiKey` → `apikey`）。**非子串**——子串会把 `tokenCount`/`cookieCount` 误伤成不可调 |
| **按内容** | `SECRET_VALUE_PATTERNS` 全集（PEM/JWT/AWS key id/GitHub/Slack/Google/OpenAI/Stripe/npm/GitLab/Bearer/连接串 `Key=…`） | 变量名叫 `x` 也可能是 secret；内容形态匹配即脱敏 |
| **例外** | `TRIVIAL_VALUES`（''/none/null/undefined/nan/true/false/0/-1/[]/{} 等） | null/空/平凡值**不动**（`null` 值保留可调） |
| **表达式级** | `isSensitiveExpression`：表达式文本最后一个标识符归一化后在名单内 → 脱敏 | 防平凡绕过：`os.environ`/`process.env.API_KEY` 经 debug_evaluate 读回同名 secret（等价 .NET：`Environment.GetEnvironmentVariable` 结果 / 字典路径末段名） |

## 3. 架构与落点

- **宿主渲染层**新增内部静态助手 `SensitiveValueRedactor`（`Tools/Debugger/`）：`Redact(name, text) → string`（命中返回占位符；未命中原样）。占位符 + 单次提示（命中时输出顶部注明「存在 N 个脱敏值——疑似凭据，用类型/长度/null 判断而非读原始值」）。
- **出口挂接**：`DebugInspectTool.RenderVariable`（递归渲染，逐变量名天然覆盖嵌套路径 `config.Credentials[0].Token`——children 自带字段名，每层按自身名匹配）+ `DebugEvaluateTool` 结果标量/children + trace/停点上下文中所有 `name=display` 变量行。Engine/Session 零改动。
- 递归语义：与 DebugMCP「父对象名不敏感不整体脱敏、直接检查每个字段」等价——我们渲染已逐行展开 children，字段行按其自身名判定。

## 4. 验证

- 宿主单测四类：**名匹配**（password/apiKey/token 等）/ **内容匹配**（JWT/PEM/Bearer 赋给普通名变量）/ **嵌套路径**（对象 children 深层字段名敏感）/ **求值绕过**（debug_evaluate 表达式末段标识符敏感 → 脱敏）。
- e2e：DebugTarget 增含凭据字段的对象在断点观察，debug_variables 返回占位符 + 顶部提示。
- 回归护栏：占位符/提示文案断言（V4 语料测试预留挂接）。
