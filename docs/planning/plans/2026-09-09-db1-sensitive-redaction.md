# 实施计划 · DB1 变量敏感脱敏层

> **For agentic workers:** REQUIRED SUB-SKILL: 用 superpowers:executing-plans 逐 Task 实施。Step 用 `- [ ]` 追踪。

**Goal:** 宿主读值渲染出口统一脱敏：按变量/字段名（归一化 exact-match）+ 按内容形态（凭据正则）双模式过滤，null/平凡值不动；debug_evaluate 表达式级绕过一并覆盖；命中给占位符 + 单次提示。

**Architecture:** 宿主渲染层新增内部静态 `SensitiveValueRedactor`（规则移植 microsoft/DebugMCP `secretRedaction.ts` 双模式全集，翻译为 C#），在三个读值出口挂接：`DebugInspectTool.RenderVariable`（递归，children 逐字段名天然覆盖嵌套路径）、`DebugEvaluateTool` 标量行 + children、`DebugSessionTool` trace 变量行。Engine/Session 零改动。

**Tech Stack:** C# net10.0、正则（`System.Text.RegularExpressions`，PEM/JWT 等模式同 DebugMCP）。

**Spec:** `docs/planning/specs/2026-09-08-db1-sensitive-redaction.md`（2026-09-09 已拍板：读值出口+表达式级、宿主层、Web 不同步）。

## Global Constraints（根 AGENTS.md + 宿主 AGENTS.md 铁律）

- 内部助手（非 MCP 工具）无参数约束；输出文案全中文。
- 改 MCP 工具**行为**（读值输出内容变化）属使用者可见变更——同批记 `CHANGELOG.md` `[Unreleased]`；根 README「动态调试」段一句话说明（debug_variables/debug_evaluate 输出会脱敏疑似凭据）。
- 宿主新增内部类文件在 `Tools/Debugger/` 下（`DebuggerTextRedactor` 语义命名空间 `DotNetDebuggerMcp.Tools.Debugger`）；只脱敏**渲染文本**，绝不改 Engine/Session 数据。
- 规则只做白名单判定（非启发式/机器学习），避免误伤：子串不匹配（防 `tokenCount` 误伤）；trivial/null 不动。

---

### Task 0: 工作区/分支准备（提交前确认）

- [ ] **Step 1**: 与用户确认实施分支策略。
- [ ] **Step 2**: `generate-testdata.ps1` 就绪 + Release build 基线绿。

---

### Task 1: SensitiveValueRedactor 助手 + 单测

**Files:**
- Create: `src/DotNetDebuggerMcp/Tools/Debugger/SensitiveValueRedactor.cs`
- Test: `tests/DotNetDebuggerMcp.Tests/SensitiveValueRedactorTests.cs`（纯内存，无 Collection 要求）

**Interfaces:**
- Consumes: 无（纯函数；规则清单来自 spec §2 = DebugMCP 全集）。
- Produces:
  - `internal const string Placeholder = "[已脱敏:疑似凭据]"`
  - `internal static bool IsSensitiveName(string name)`（归一化 exact-match：小写 + 去 `_`/`-`/空格）
  - `internal static bool LooksLikeSecret(string text)`（内容正则任中即 true）
  - `internal static (string Text, bool Redacted) Redact(string? name, string text)`（text 空/平凡/`Placeholder` 原样返回 false；命中名或内容返回 `Placeholder`）
  - `internal static bool IsSensitiveExpression(string expression)`（末段标识符归一化后在名单内）

- [ ] **Step 1: 写失败测试（四类）**

`SensitiveValueRedactorTests.cs`（`[Theory]`/`[InlineData]` 穷尽）：
```csharp
// 名匹配：password / API_KEY / api-key / accessToken / connString(connectionstring) → redacted
// 平凡不动：null/''/0/true/none → 原样返回且 Redacted==false
// 子串不误伤：tokenCount / cookieCount / passwordReset 非名单名 → 不脱敏
// 内容匹配：普通名变量值含 JWT(eyJ…) / PEM(-----BEGIN …PRIVATE KEY-----) / Bearer xxx / AKIA… → redacted
// 嵌套路径：RenderVariable 递归场景由出口测试覆盖；助手本身只判单名
// 表达式级：IsSensitiveExpression("Environment.GetEnvironmentVariable(\"API_KEY\")") 末段标识符 API_KEY 敏感——注：我们表达式子集无方法调用，evaluate 路径形如 "cfg.Token"/"envDict[0]" 末段 Token/environmentvariable 归一化名单判定
```
Run `dotnet test --project tests/DotNetDebuggerMcp.Tests/... --filter SensitiveValueRedactor`，Expected: FAIL（类型不存在）。

- [ ] **Step 2: 实现**

`SensitiveValueRedactor.cs` 核心（规则全集=DebugMCP 名单/正则逐条翻译，C# 正则注意 `\b` 语义一致）：
```csharp
internal static class SensitiveValueRedactor
{
    internal const string Placeholder = "[已脱敏:疑似凭据]";
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.Ordinal) { /* spec §2 全集：apikey/apisecret/secret/password/passwd/pwd/token/credential/auth/otp/cookie/sessionid/connectionstring/connstr/… 厂商拼写 */ };
    private static readonly Regex[] SecretPatterns = { /* PEM/JWT/AWS(AKIA…)/ghp_/github_pat_/xox…/AIza…/sk-(…)/sk_live_/npm_/glpat-/Bearer…/(AccountKey|SharedAccessSignature|Password|Pwd)=… */ };

    internal static bool IsSensitiveName(string name) =>
        name.Length > 0 && SensitiveNames.Contains(Normalize(name));
    internal static string Normalize(string name) =>
        string.Concat(name.ToLowerInvariant().Where(c => c is not ('_' or '-' or ' ')));

    private static readonly string[] TrivialValues = { "", "none", "null", "nil", "undefined", "nan", "true", "false", "0", "-1", "[]", "{}", "()", "empty", "<empty>" };
    internal static bool IsTrivial(string text) => TrivialValues.Contains(Unwrap(text).ToLowerInvariant());

    private static string Unwrap(string text)
    {
        var t = text.Trim();
        while (t.Length >= 2 && ((t[0] == '\'' && t[^1] == '\'') || (t[0] == '"' && t[^1] == '"')))
            t = t[1..^1].Trim();
        return t;
    }

    internal static (string Text, bool Redacted) Redact(string? name, string text)
    {
        if (string.IsNullOrEmpty(text) || IsTrivial(text)) return (text, false);
        if (!string.IsNullOrEmpty(name) && IsSensitiveName(name)) return (Placeholder, true);
        if (SecretPatterns.Any(p => p.IsMatch(text))) return (Placeholder, true);
        return (text, false);
    }

    internal static bool IsSensitiveExpression(string expression)
    {
        // 表达式子集无方法调用/括号——末段标识符 = 最后一个非 `]`/`.` 边界标识符；用正则抓全部标识符取最后
        // 标识符体含 `-`（对齐 DebugMCP `[A-Za-z_][A-Za-z0-9_-]*`）
        var ids = Regex.Matches(expression, "[A-Za-z_][A-Za-z0-9_-]*").Select(m => m.Value).ToArray();
        return ids.Length > 0 && IsSensitiveName(ids[^1]);
    }
}
```
> 实现注：`SecretPatterns` 的 C# 正则与 TS 逐条等价——TS `/[A-Za-z0-9+/=\s]*/`、`(?:\b…)` 均已对齐；编译期 `RegexOptions.Compiled` 可选；单测覆盖即正确性基准。

- [ ] **Step 3: 运行测试 + 提交**

Run 定向测试过 + 宿主既有编译。`git commit -m "feat: SensitiveValueRedactor 敏感脱敏助手（名+内容双模式，DB1）"`

---

### Task 2: 出口挂接（读值渲染三处）+ CHANGELOG/README

**Files:**
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugInspectTool.cs`（`RenderVariable:124`）
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugEvaluateTool.cs`（`:44` 标量行 + children 已走 RenderVariable）
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugSessionTool.cs`（trace 变量行 `:169`）
- Modify: `tests/TestData/generate-testdata.ps1`（Step 0：DebugTarget 增敏感字段）
- Modify: `CHANGELOG.md`、`README.md`
- Test: `tests/DotNetDebuggerMcp.Tests/DebugMcpToolsTests.cs`

- [ ] **Step 0: 测试数据（generate-testdata.ps1，2026-09-09 审查修正补步）**

`$dbgSrc` 的 `class Bag`（脚本内最后一个类型——字段表末尾追加不位移既有字段/方法 token）**尾部**追加敏感字段；`Main` 的 `bag` 分支初始化器赋真实凭据形值。**勿在 `A`/`S` 之间插字段**（既有 `Assert.Contains("A, S")` 可用字段清单断言不破）；**不改其它方法体/逻辑，仅 bag 分支初始化器增补两字段赋值**（`Password`/`Token` 不赋值则空串属 trivial、不会触发脱敏，e2e 失效）。重跑脚本。
```csharp
public class Bag
{
    public int A;
    public string S = "";
    // DB1 e2e 敏感字段（append，不进 A/S 之间）
    public string Password = "";
    public string Token = "";
}
// Main bag 分支：
//   WorkBag(new Bag { A = 7, S = "sx", Password = "hunter2", Token = "Bearer eyJhbGciOiJIUzI1NiJ9.e30.abc" }, 5);
```

- [ ] **Step 1: RenderVariable 递归脱敏 + 计数提示**

`DebugInspectTool.cs`：`RenderVariable` 改为值经 `Redact`；顶层调用（DebugVariables `:112`）统计命中数并在返回头部附提示：
```csharp
internal static string RenderVariable(DebugVariable v, int depth)
{
    var indent = new string(' ', depth * 2);
    var (valueText, _) = SensitiveValueRedactor.Redact(v.Name, v.Value.Display);
    var line = $"{indent}{v.Name ?? $"slot{v.Slot}"} = {valueText}";
    if (v.Value.Children is not { } children) return line;
    foreach (var c in children) line += Environment.NewLine + RenderVariable(c, depth + 1);
    return line;
}
```
`DebugVariables`（`:106`）：渲染前先数命中——遍历变量树（递归收集 `v.Name + v.Value.Display`），`count` 写入返回头：`…（{count} 个值疑似凭据已脱敏——用类型/长度/null 判断，勿读原始值）`。注意 RenderVariable 单行命中即替换——**统计与渲染两次遍历各判一次，纯函数无副作用，成本可忽略**。**与 DB2 白名单同批落地时**：计数放白名单过滤**之后**（只数可见命中值，避免提示含未展示项）。

- [ ] **Step 2: debug_evaluate 标量/children 脱敏**

`DebugEvaluateTool.DebugEvaluate`（`:44`）最终形态（2026-09-09 审查修正，无占位残句）：
1. `IsSensitiveExpression(expression)` 命中 → 值整行换占位符，置命中标志；
2. 未命中表达式级，再对 `result.Display` 走 `LooksLikeSecret`；
3. 命中（表达式级或内容级）时值行同段附**单次提示**「（疑似凭据已脱敏——用类型/长度/null 判断，勿读原始值）」（与 debug_variables 同文案）；children 走 `RenderVariable`（Step 1 已脱敏，其提示由出口头带出）。
> 表达式级判定原则（DebugMCP 同款）：路径末段名（如 `cfg.Token` 末段 `Token`）敏感即脱敏，防「换个变量名读同一 secret」绕过。

- [ ] **Step 3: trace/停点上下文变量行脱敏**

`DebugSessionTool.cs` trace 渲染循环（`:159-169` 一带，含 `debug_wait` 与 `debug_state` 共用）：每变量行 `v.Name`/`v.Display` 经 `Redact`，命中在轨迹段头部附 `（含已脱敏值）`。

- [ ] **Step 4: CHANGELOG + README + 端到端**

`CHANGELOG.md` `[Unreleased]`：记「debug_variables/debug_evaluate 读值输出对疑似凭据（api key/password/token/JWT/PEM/Bearer/连接串等按变量名或内容形态）自动脱敏」。README「动态调试」段一句。端到端（`DebugMcpToolsTests`）：DebugTarget 断点观察含 `Password="…"`/JWT 值字段 → debug_variables/debug_evaluate 返回含 `[已脱敏:疑似凭据]` 且顶部提示；普通字段原样。Run 定向 + 宿主全量。`git commit -m "feat: 读值出口敏感脱敏挂接（debug_variables/evaluate/trace，DB1，同步 CHANGELOG/README）"`

---

## 收尾

- [ ] Release build + 宿主全量单测 + Client 端到端。
- [ ] 核对 `src/DotNetDebuggerMcp/TODO.md` DB1 状态；`docs/planning/specs/README.md` 收录 DB1 spec 行。

## Self-Review

- **Spec 覆盖**：双模式规则（Task1）、trivial/null 不动（Task1）、表达式级（Task1+Task2 Step2）、嵌套路径（children 递归经 RenderVariable）、占位符+单次提示（Task2 Step1）、读值出口全（debug_variables/evaluate/trace）。非目标确认未越界（控制台输出/异常 Message/Web 均不动）。
- **占位符**：规则全集 = spec §2 显式引用 DebugMCP 全集并注「逐条翻译」——Task1 单测为正确性基准，无 TBD。
- **类型一致**：`Redact(name,text)`/`IsSensitiveExpression`/`Placeholder` Task1 定义、Task2 三出口消费。
- **风险点已标**：子串不匹配防误伤；统计与渲染两次遍历纯函数无副作用；正则与 TS 等价性由单测锁。
