# 2026-09-10 SensitiveValueRedactor 实现审查报告

> **处置（2026-09-10，主会话）**：本报告 10 项发现已逐条对当前代码核验，**全部属实**；P0-1 + P1-1..P1-4 + P2-1/P2-2 + P3-1/P3-2 + P3-3 已由 commit `561d5e0` 修复（scoped re-review PASS 10/10）；P1-5 按建议仅加注释不改行为；Web/CLI `-dbg` 仍为人类面不脱敏（D26 记录「停点 message 现按内容形态脱敏」覆盖原 D17 例外）。本文件保留为审查记录。

> **审查对象**：`src/DotNetDebuggerMcp/Services/SensitiveValueRedactor.cs`（DB1 敏感脱敏助手）及其读值出口。
> **对照基线**：上游 `../../Externals/DebuggerExternals/DebugMCP/src/utils/secretRedaction.ts` 与 `secretRedaction.test.ts`。
> **范围声明**：**Web 展示面（`DebugVarRow.razor` / `DebugViewService`）已排除**——按产品定位 Web 是人类观看席，**有意不脱敏**，不作为问题。
> **状态**：待主会话审查/修复。**下文未改动任何代码**。
> **总体结论**：核心算法方向正确，无 ReDoS；以下为需要修的偏差与缺口。

---

## 0. 结论摘要

| 编号 | 严重度 | 问题 | 位置 | 建议 |
|---|---|---|---|---|
| P0-1 | 高 | 停点 `message` 未过内容判定 | `DebugSessionTool.cs:210` | 本批修 |
| P1-1 | 中 | `IgnoreCase` 正则缺 `CultureInvariant` | `SensitiveValueRedactor.cs:62-63` | 本批修 |
| P1-2 | 中 | `Unwrap` 漏反引号 | `SensitiveValueRedactor.cs:91-97` | 本批修 |
| P1-3 | 中 | `Normalize` 只去 ASCII 空格 | `SensitiveValueRedactor.cs:75-76` | 本批修 |
| P1-4 | 中 | `IsSensitiveName` 缺 null 守卫 | `SensitiveValueRedactor.cs:71-72` | 本批修 |
| P1-5 | 低 | C# `\b` 与 JS 语义差异（CJK） | 多处 `\b` | 记录，不建议盲改 |
| P2-1 | 低 | 表达式只取最后标识符（潜在绕过） | `SensitiveValueRedactor.cs:117-125` | 注释说明前提 |
| P2-2 | 低 | `Notice` 文案与出口能力不符 | `SensitiveValueRedactor.cs:17` | 文案/出口决定 |
| P3-1 | 中 | `debug_variables` 双倍遍历/双倍正则 | `DebugInspectTool.cs:179-233` | 本批修 |
| P3-2 | 低 | `Normalize` LINQ 高频分配 | `SensitiveValueRedactor.cs:76` | 随 P1-3 顺带 |
| P3-3 | 低 | 缺性能护栏测试 | `SensitiveValueRedactorTests.cs` | 本批补 |

**做得对的地方（勿回退）**：
- 未直译上游 `/g` + `lastIndex` 状态机，改用无状态 `IsMatch`——线程安全正确。
- 加了 placeholder 幂等短路（`Redact`/`RedactExpression` 首行），比上游更稳。
- trivial 优先、每节点独立判定、只脱渲染不动 Engine/Session 数据——设计正确。
- 正则无 ReDoS：PEM 字符类 `[A-Za-z0-9+/=\s]` 不含 `-`，与 `-----END` 无歧义；`sk-` 无可嵌套量词。

---

## P0-1 停点 `message` 未过内容判定（建议本批修）

- **位置**：`src/DotNetDebuggerMcp/Tools/Debugger/DebugSessionTool.cs:206-212`（`StopText`），关键行 `:210`：
  ```csharp
  if (!string.IsNullOrEmpty(stop.Message)) text += $" message=\"{stop.Message}\"";
  ```
- **影响出口**：`debug_state`（`:124`）、`debug_wait`、`debug_continue`（`DebugControlTool.cs:76`）都会带出该 message。
- **问题**：异常消息常内嵌连接串/Token（如 `... Password=...`、`Authorization: Bearer ...`），此处完全不做内容正则过滤。对比 `$exception` 变量走 `RenderVariable` 是脱敏的，行为不一致。
- **建议修法**（`name=null` 时只跑内容正则）：
  ```csharp
  if (!string.IsNullOrEmpty(stop.Message))
  {
      var (msg, _) = SensitiveValueRedactor.Redact(null, stop.Message);
      text += $" message=\"{msg}\"";
  }
  ```
- **测试点**：构造带 `message` 的 `StopContext`，断言含 `Password=`/Bearer 形态时输出占位符。
- **备注**：`DebugCliRunner.cs:103` 的 CLI `-dbg` 同样裸打印 `v.Value.Display`，但属本机手动验证路径（人类向），同 Web 归为「有意不脱敏」，不列入必修。

---

## P1 相对上游的移植偏差

### P1-1 `IgnoreCase` 正则缺 `CultureInvariant`（建议本批修）

- **位置**：`SensitiveValueRedactor.cs:62-63`（`Bearer` 与连接串两条带 `RegexOptions.IgnoreCase`）。
- **证据**（本机 net10.0 实测；结论：.NET 正则 `IgnoreCase` 在**构造时固化 `CurrentCulture`**）：
  ```
  constructed@tr-TR:                 I~i=False   SIGNATURE~Signature=False
  constructed@zh-CN, matched@tr-TR:  I~i=True
  ```
- **问题**：上游 JS `/i` 与 locale 无关；C# 在土耳其语环境启动时，`SharedAccessSignature` 的全大写 `SIGNATURE` 会漏匹配。
- **建议修法**：两条正则追加 `RegexOptions.CultureInvariant`。
- **备注**：影响面窄（仅 `Signature` 含 `i`），但零成本防御。单测因静态字段在类初始化即固化 culture，不易直接覆盖；本项可只改代码。

### P1-2 `Unwrap` 漏反引号

- **位置**：`SensitiveValueRedactor.cs:91-97`。
- **证据**：上游 `unwrap` 剥 `'` / `"` / `` ` `` 三种；C# 只剥两种。实测 `` `None` `` → 平凡判定 `false`。
- **建议修法**：条件补 `` (t[0]=='`' && t[^1]=='`') ``。

### P1-3 `Normalize` 只去 ASCII 空格

- **位置**：`SensitiveValueRedactor.cs:75-76`。上游 `.replace(/[\s_-]/g,'')`；C# 只去 `' '`（不去 tab/换行）。
- **建议修法**：`Where(c => c != '_' && c != '-' && !char.IsWhiteSpace(c))`（与 `\s` 语义对齐，并顺带解决 P3-2）。

### P1-4 `IsSensitiveName` 缺 null 守卫

- **位置**：`SensitiveValueRedactor.cs:71-72`，参数声明 `string name` 但首行 `name.Length`。
- **现状**：当前调用点都有 guard（`Redact` 的 `!IsNullOrEmpty`、`IsSensitiveExpression` 的 `last` 非空），不会 NRE；但上游接受 `null/undefined`，签名应防御。
- **建议修法**：
  ```csharp
  internal static bool IsSensitiveName(string? name)
      => !string.IsNullOrEmpty(name) && SensitiveNames.Contains(Normalize(name));
  ```

### P1-5 C# `\b` 与 JS 语义差异

- **位置**：多处 `\b` 边界（如 `:53` AWS、`:58` sk- 等）。
- **证据**：C# `\b` Unicode-aware（CJK 算 word char），JS `\b` ASCII。实测 `键AKIAIOSFODNN7EXAMPLE` 在 C# **不匹配**（上游会匹配）。
- **建议**：影响极小（密钥前通常有分隔符）。严格对齐可用 `RegexOptions.ECMAScript`，但它会同时改变 `\d`/`\s`/`\w` 语义，属较大行为变更——**不建议盲改**，建议在文件注释标注此已知差异。

---

## P2 设计局限（继承上游，记录即可，非必改）

### P2-1 表达式只取最后标识符（潜在绕过）

- **位置**：`SensitiveValueRedactor.cs:117-125`。
- **问题**：`cfg.Token.ToString()` / `cfg.Token.Length` 末段是 `ToString`/`Length`，会绕过表达式级判定。
- **现状**：`debug_evaluate` 文法不支持方法调用、`debug_set`/verify 的 path 都是 `PathNode`，**当前不可达**。
- **建议**：注释写明「该语义仅在文法不支持方法调用时成立」的前提，防将来放开调用时埋洞。

### P2-2 `Notice` 文案与出口能力不符

- **位置**：`SensitiveValueRedactor.cs:17`。
- **问题**：文案让 agent「用类型/长度/null 判断」，但 `debug_variables` 出口脱敏后只剩 `name = [已脱敏…]`，**无类型、无长度**（仅 `debug_evaluate` 给类型）。提示在变量出口无法执行。
- **建议**：改文案（去掉「长度」）或给 `debug_variables` 脱敏行补类型信息。**产品/UX 决定**。
- **注意**：`Notice` 是跨出口共享字面量（根 `AGENTS.md` 已列），改动集中在此常量。

---

## P3 性能 / 测试（建议本批修）

### P3-1 `debug_variables` 双倍遍历 + 双倍正则

- **位置**：`DebugInspectTool.cs:179/201` 调 `CountRedacted`，`:180/202` 又调 `RenderVariable`；`CountRedacted` 见 `:229-235`。
- **问题**：同一变量树被完整遍历两遍、13 条正则跑两遍；且两处逻辑将来易漂移（顶部计数与实际脱敏行不一致）。
- **建议**：合并为单次递归：
  ```csharp
  internal static string RenderVariable(DotNetDebugger.Engine.Models.DebugVariable v, int depth)
      => RenderVariable(v, depth, out _);

  private static string RenderVariable(DotNetDebugger.Engine.Models.DebugVariable v, int depth, out int redacted)
  {
      var (valueText, hit) = SensitiveValueRedactor.Redact(v.Name, v.Value.Display);
      redacted = hit ? 1 : 0;
      var line = $"{new string(' ', depth * 2)}{v.Name ?? $"slot{v.Slot}"} = {valueText}";
      if (v.Value.Children is { } children)
          foreach (var c in children)
          {
              line += Environment.NewLine + RenderVariable(c, depth + 1, out var childHit);
              redacted += childHit;
          }
      return line;
  }
  ```
  保留 2 参重载给 `DebugEvaluateTool.cs:52`、`DebugObjectTool.cs:56` 复用；`BuildVariablesLines` 改调 3 参版本并删除 `CountRedacted`。

### P3-2 `Normalize` 高频 LINQ 分配

- **位置**：`SensitiveValueRedactor.cs:76`。每个变量每节点都会调用。随 P1-3 改为手写循环即可。

### P3-3 缺性能护栏测试

- **上游有**：`secretRedaction.test.ts:139-154`（adversarial 输入 `<1s` 两条），C# 未移植。
- **建议**：补两条护栏防回归（当前经分析无 ReDoS，但固化测试更稳）。

---

## 测试补充清单（`tests/DotNetDebuggerMcp.Tests/SensitiveValueRedactorTests.cs`）

- [ ] 反引号平凡值：`` `None` `` / `` `null` `` → 不脱敏（P1-2）
- [ ] `Normalize` 空白：`"api\tkey"` / `"api\nkey"` → 敏感（P1-3）
- [ ] `IsSensitiveName(null)` → `false`（P1-4）
- [ ] `RedactExpression` 的 trivial 分支：`RedactExpression("cfg.Token","null")` → 原样不脱敏
- [ ] `CountRedacted` 与 `RenderVariable` 一致性（同一棵树，命中数 == 输出占位符个数；P3-1 后应天然成立）
- [ ] 性能护栏两条（长 identifier run / 反斜杠巨串，`<1s`）
- [ ] `StopText` 的 message 内容脱敏（P0-1）

---

## 建议修复顺序

1. **P0-1**（`stop.Message`）——安全缺口。
2. **P1-1 / P1-2 / P1-3 / P1-4**——低风险一行改动 + 单测。
3. **P3-1**（合并双遍历）——重构，注意保留 2 参重载。
4. **P3-3** 测试护栏；**P1-5 / P2-1 / P2-2** 记录性说明或文案决定。

## 验证命令（改后）

```bash
dotnet build -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj
dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -- --filter-class "DotNetDebuggerMcp.Tests.SensitiveValueRedactorTests"
```

> 版本纪律（根 `AGENTS.md`）：内部行为修正是否记入 `CHANGELOG.md` `[Unreleased]` 由发布节奏决定；`Notice` 若改动属跨出口共享字面量，集中改 `SensitiveValueRedactor.Notice`。
