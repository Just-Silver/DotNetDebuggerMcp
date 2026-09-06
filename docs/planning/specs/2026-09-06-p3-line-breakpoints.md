# Spec · P3 行断点：反编译视图行 + PDB 源码行

> 状态：**待评审**（2026-09-06 起草）。通过后动码；完成态回写本行。
> 关联：宿主 TODO P3（`src/DotNetDebuggerMcp/TODO.md`）；现状锚点均已在起草时核实（标注文件:行号以起草时为准）。

## 1. 背景与目标

agent 的坐标系是 MCP `decompile` 工具的输出（`行号<TAB>内容`）与目标原始源码（有 PDB 时），但 `debug_breakpoint_set` 只认 模块+方法 token+IL offset——agent 要先查 `signature` 拿 token 再换算，多一轮往返且易错。本 spec 给断点定位补两个行坐标系：

- **3a 反编译视图行**：`debug_breakpoint_set(typeName, line)`——agent「看到反编译输出的第 N 行，就在那行下断点」。
- **3b PDB 源码行**：`debug_breakpoint_set(sourcePath, line)`——目标带 PDB 时按原始源码文件+行下断点。

非目标（本 spec 不做）：无 PDB 程序集的「原始源码行」（没有映射依据）；反编译输出分页偏移（行号恒指完整文档坐标，分页只影响展示）。

## 2. 现状锚点（起草时已核实）

| 设施 | 位置 | 与本 spec 的关系 |
|---|---|---|
| `DocumentService.GetTypeDocument(assemblyPath, typeFullName)` | Decompiler/Document/DocumentService.cs | 产出反编译文本 + **IL→行**语句级映射（TextWriterTokenWriter+位置回写管线，探针实测确证勿改） |
| `DocumentService.GetIlStartForLine(doc, line)` | 同上 | **行→(MethodToken, IlStart)** 反向映射已存在（多覆盖取最小 IlOffset） |
| `DocumentStore.GetBreakpointTargetAtLine(doc, line)` | Web/Services/DocumentStore.cs | Web 断点红点同款语义：行无序列点时落所在方法首条语句——**3a 直接复用该算法**（从 Web 提升到 Decompiler/Document 层供宿主共享） |
| `MetadataNaming.FindTypes` / 歧义与相近名提示 | Decompiler/Metadata/MetadataNaming.cs | 3a 类型定位直接复用（全名精确、多候选歧义提示、0 候选附相近名——DecompileMember 同款模式） |
| `SymbolNameResolver` PDB 读取模式 | Engine/Engine/SymbolNameResolver.cs（`Path.ChangeExtension(modulePath, ".pdb")`） | 3b 的 PDB 定位同款（3b 实现放 Decompiler 层则复制该模式；Engine 不新增依赖） |
| `BreakpointManager.GetModulePath(moduleName)` / `TrackModule` | Engine/Engine/BreakpointManager.cs | 已加载模块短名→磁盘路径（读 DLL/PDB 用） |
| `debug_breakpoint_set` 现状 | 宿主 Tools/Debugger/DebugBreakpointTool.cs | moduleName+methodToken+ilOffset 三参；模块未加载→pending 待绑定 |

## 3. 步骤 0（动码前探针）：行号坐标基准

**问题**：agent 引用的行号来自 `decompile` 工具（`InProcessDecompiler.DecompileType` → `DecompileAsString`），而映射来自 `DocumentService`（显式走位置回写 writer）。两条管线若文本不一致，行号坐标就是错的。

**已知**：两者同为 `CSharpOutputVisitor` + `TextWriterTokenWriter`（缩进 `\t`）+ 相同默认 `CSharpFormattingOptions`，文本**预期**逐字节一致；`DecompileAsString` 的差异仅在「不回写 AST 位置」（影响映射不影响文本）。

**探针**：新增单测断言 `InProcessDecompiler.DecompileType(asm, type)` 代码段与 `DocumentService.GetTypeDocument(asm, type).Text` **逐字节相等**（TestSamples 至少 2 个类型 + DebugTarget.Compute）。

- 相等（预期）→ 行号坐标 = 两者通用，3a 直接接线；
- 不等 → 3a 的 `line` 语义定为 **DocumentService 坐标**，且把 `decompile`(typeName) 的文本渲染切到同款 writer 使 agent 所见与坐标一致（本 spec 内一并做，另行小步提交）。

## 4. 工具参数面（debug_breakpoint_set 三分支）

现有参数不动，新增可选参数（全部带默认值，Description 中文注明默认值与语义）：

| 参数 | 类型/默认 | 分支 |
|---|---|---|
| moduleName / methodToken / ilOffset | 现状不变 | **token 分支**：methodToken 非空（现有语义，唯一支持 pending 待绑定的分支） |
| typeName | string = "" | **3a 反编译行分支**：typeName 非空且 methodToken 为空；类型全名（list_types/decompile 输出同格式） |
| sourcePath | string = "" | **3b PDB 源码行分支**：sourcePath 非空且 methodToken/typeName 为空；源文件路径（绝对或相对，末段匹配） |
| line | int = 0 | 3a/3b 的行号（1-based，指完整文档坐标）；分支内必填 |

分支判定：`methodToken` 非空 → token；否则 `sourcePath` 非空 → 3b；否则 `typeName` 非空 → 3a；否则提示「请提供 methodToken 或 typeName+line 或 sourcePath+line」。`moduleName`：token 分支必填（现状）；3a/3b **可选**——省缺时跨已加载模块解析（见 §5/§6），多模块命中报歧义。

## 5. 3a 反编译视图行

1. 定位模块：moduleName 给定 → `GetModulePath`（未加载/未知 → 报错「行断点需模块已加载；未加载模块请用 token 分支（支持待绑定）」）；省缺 → 遍历已加载模块，对每个模块读元数据 `FindTypes(typeFullName)`（复用 MetadataNaming），唯一命中即用，多命中 → 歧义提示列出 模块+全名（DecompileMember 模式），0 命中 → 未找到 + 相近名。
2. `GetTypeDocument(modulePath, fullName)` → doc（反编译失败按其 Error 中文提示返回）。
3. `GetBreakpointTargetAtLine(doc, line)`（提升到 Document 层的 Web 同款算法）→ (token, ilOffset)；null → 提示「行 {line} 无法定位到语句（不在任何方法区间）」+ 建议用 methodToken 分支。
4. `SetBreakpointAsync(moduleName, token, ilOffset)` → 现有返回文案（附「位置：类型 {typeName} 第 {line} 行 → {module}!0x{token:x8}+{ilOffset}」）。

语义注记：行落在方法体但没有独立序列点（如大括号/签名行）→ 落该方法**首条语句**（Web 红点同款，返回文案注明「落于方法首语句」）；一行多条语句取最小 IlOffset。

## 6. 3b PDB 源码行

1. 定位模块：同 §5.1 的省缺遍历/显式给定（未加载同样报错）。
2. 新组件 `SourceLineResolver`（放 **Decompiler/Document/**，纯 SRM，复用 SymbolNameResolver 的 `ChangeExtension(".pdb")` 模式）：
   - PDB 缺失 → 报错「模块 {m} 无 PDB，无法按源码行定位（可用 typeName+line 反编译行坐标）」；
   - 遍历 PDB Documents 建立文档名索引（归一化：`/`→`\`、OrdinalIgnoreCase；支持绝对路径、相对路径与仅末段 `xxx.cs` 匹配，多文档末段同名 → 歧义提示列全名）；
   - 命中文档 → 遍历 MethodDebugInformation.SequencePoints，收集该文档上的方法与行覆盖区间：
     - 存在 `StartLine == line` 的序列点 → 取（多方法同行取 (token, offset) 序最小者）；
     - 否则取「覆盖 line 的方法」中 **≥ line 的最近序列点**（落点语义同 §5.4，文案注明实际落点行）；
     - 无覆盖 → 未找到提示。
3. 输出与 pending 语义同 §5.4（源码行断点同样要求模块已加载；无 pending）。

## 7. 测试计划

- **探针测试**（§3）：文本逐字节相等。
- **Decompiler 层单测**（新增，无 ICorDebug）：`GetBreakpointTargetAtLine` 提升后回归（Web 侧改引用后其现有测试继续绿）；`SourceLineResolver`：DebugTarget.pdb（脚本产出）定位 `Compute`/`Work`/`ThrowIfZero` 行 → token+offset 正确；末段匹配/歧义/未找到/无 PDB 四类语义。
- **Session 集成**：launch DebugTarget → 3a typeName+line（Compute 某语句行）→ continue → 命中且栈顶方法为 Compute；3b sourcePath（Program.cs 路径）同断言。
- **宿主 e2e**：DebugMcpToolsTests 加 3a 闭环一条（参数省缺 moduleName 的跨模块解析也覆盖）。
- **回归**：Web 断点红点管线（改为引用提升后的公共实现）；全量 build+测试+Client。

## 8. 工作量与顺序

步骤0 探针（0.5）→ 公共层提升+3a（0.5）→ 3b SourceLineResolver（1）→ 宿主工具面+README/CHANGELOG（0.5）→ 三层测试收尾（0.5），合计 ≈ 3 人日。顺序即依赖序；3a/3b 任一受阻不阻塞另一交付。
