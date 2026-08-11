# ilspymcp 建议（基于源码，agent 视角）

> 记录日期：2026-08-11
> 场景：反编译 CommonUtils.dll 生成结构拓扑图
> 源码：`D:\Code\Projects\Libraries\ILSpyMcp`（net10.0，子进程调 ilspycmd，PEReader 做元数据层）

## 0. 前提约束（建议都遵循它）

默认 200 行是刻意设计：大 dll 反编译输出可达几万行，全量返回会塞满 agent 上下文。`DefaultMaxLines = 200`（OutputFormatter.cs:19）、`lines` 单次上限 `LinesRangeMax = 500`（:24）不应放宽。因此下述建议的核心是：**让 agent 用更少的往返、更低的噪声拿到结构信息**。

---

## 1. `decompile` 提供「仅成员签名」模式

**agent 遇到的问题**：面对大类型（SystemInfo 1144 行 / StringExtension 557 行），首 200 行往往是字段和静态初始化，方法签名在中后段。agent 只能反复 `lines="N-M"` 猜着拉，往返多且低效。

**建议**：给 `decompile` 加可选参数（如 `signatureOnly=true`），在 Metadata 层（复用 `MemberResolver` 的 PEReader 模式，MemberResolver.cs:29）枚举该类型全部成员，**每成员一行签名**输出，例如：

```
static HiPerfTimer StartNew() / static double Execute(Action) / void Start() / double Stop() / ...
```

- 单成员一行，SystemInfo 全部成员约 40~60 行，远小于 200 行预算，不触发上下文保护；
- agent 一次调用拿全 API 地图，再按需 `decompile_member` 或 `lines` 精确拉单个方法体；
- 纯 PEReader 秒回、可缓存，不依赖 ilspycmd。

## 2. `decompile_member`：结果标注归属 + 排除访问器

**agent 遇到的问题**：
1. 子串匹配 + 忽略大小写（MemberResolver.cs:43），搜 `Get` 会连 `get_X` 属性访问器一起命中，噪声大；
2. `ExecuteMergedAsync` 把多个成员体直接拼接、无分隔（ToolPipeline.cs:152），agent 看到的是"散落的方法体片段"，无法判断每段属于哪个方法。

**建议**：
1. 合并输出时在每个成员体前插入分隔头，如 `=== 成员: Start (0x06000005) ===`（名字与 token 在 `MemberMatch` 中已有，MemberResolver.cs:12），行号连续；
2. 匹配阶段排除 `get_`/`set_`/`add_`/`remove_` 访问器，或提供 `includeAccessors` 开关；
3. 可选：`FindMembers` 目前只搜方法（`type.GetMethods()`，MemberResolver.cs:40），若语义是"成员"应可扩展覆盖字段/属性/事件，由参数选择。

## 3. `list_types`：默认过滤编译器生成类型

**agent 遇到的问题**：`ListTypesTool` 直接透传 `ilspycmd -l`（ListTypesTool.cs:58），`<Module>`、`<>c`、`<PrivateImplementationDetails>`、`<WaitAsync>d__4` 等编译器生成类型占 CommonUtils 输出近 1/3，污染 agent 的视图。

**建议**：用 Metadata 层自行枚举 TypeDefinition 替换 `-l` 透传（或加 `filter=user|all` 参数）：
- 按 `TypeAttributes` 分类（class/interface/struct/enum）比 `-l` 更可控；
- 跳过 `<Module>`、`<PrivateImplementationDetails>`、名称含 `<>`/`__` 的编译器生成类型；
- 纯 PEReader 秒回、可缓存，不依赖 ilspycmd 输出格式。

## 4. 新增依赖/继承查询工具

**agent 遇到的问题**：构建拓扑图时全部靠人工比对反编译文本来提取继承与引用关系，量大且易漏。

**建议**：新增工具（如 `ilspy_deps` / `ilspy_hierarchy`），继续用 PEReader 静态分析：
- **继承**：TypeDefinition 的 `Extends` + `InterfaceImpl` → "X 继承 Y / 实现 I" 及反向"谁继承 X"；
- **引用**：方法体 `MethodBody` 里的 `MemberRef`/`TypeRef`/`MethodSpec` → "X 引用了哪些内部类型"（只统计本程序集内部，避免 BCL 噪声）；
- 输出复用现有行号/分页/头部块格式，agent 一次调用即可绘制拓扑。

## 5. `IL_xxxx: Unknown result type` 提示（低优先级）

**agent 遇到的问题**：反编译文本中夹杂 `//IL_xxxx` 注释，agent 可能误当源码。这是 **ilspycmd** 对动态类型解析不完整时的产物，非 ilspymcp 可消除。

**建议**：头部信息块检测 `//IL_` 标记并追加提示（如"反编译含 IL 未解析提示，可能为动态类型，仅供结构参考"），避免误读。改动小。

---

## 6. 其他源码观察（可选优化）

- `ExecuteMergedAsync` 串行 foreach 子进程（ToolPipeline.cs:155），多匹配时 N 次串行 ilspycmd；可并行化 + `WhenAll`（同 key 缓存仍生效），或限制一次匹配上限（如 >20 个仅返回成员清单、不反编译），防止 agent 误发宽泛查询拉爆输出。
- `decompile_member` 无匹配时仅返回文案；可从 `MemberResolver` 顺带返回相近成员名（如前缀匹配），帮助 agent 纠正拼写后一次命中。

---

## 7. 工具拆分：单一职责、参数瘦身（建议的核心）

**agent 遇到的问题**：工具参数越多，agent 的决策负担越大——要理解每个参数语义、记住组合约束（如 `project` 与 `typeName` 的互斥、`lines` 何时可用），还要为低频修饰参数填值，容易漏填/误用。当前 4 个工具的参数分布是失衡的：

| 工具 | 参数数 | 问题 |
|---|---|---|
| `ilspy_decompile` | 5 | `lines`/`languageVersion` 是横切参数，低频 |
| `ilspy_decompile_member` | 6 | 最多，`lines` 几乎用不上 |
| `ilspy_list_types` | 4 | 尚可 |
| `ilspy_decompile_to_dir` | 7 | 混了 full/project/single 三种模式 |

**建议**：遵循「一个工具只做一件事」拆分，**每个工具 2~4 个参数、必填 1~2 个**；低频修饰参数要么独立成工具、要么直接移除，不塞进主工具。推荐目标形态（10 个工具）：

- `ilspy_decompile`（`assembly, typeName`）— 反编译单类型
- `ilspy_decompile_member`（`assembly, typeName, memberName`）— 按名反编译成员
- `ilspy_list_types`（`assembly, list`）— 列类型
- `ilspy_read_lines`（`assembly, typeName, lines`）— 承接 `decompile`/`decompile_member` 里被移除的 `lines`，按行翻页
- `ilspy_decompile_to_dir`（`assembly, outputDir`）— 全量写盘
- `ilspy_decompile_to_project`（`assembly, outputDir`）— 项目形式写盘
- `ilspy_decompile_type`（`assembly, outputDir, typeName`）— 单类型写盘
- `ilspy_signature`（`assembly, typeName`）— 仅成员签名（第 1 条建议的正解：与其给 `decompile` 加 `signatureOnly` 参数，不如独立成工具，更符合拆分原则）
- `ilspy_hierarchy`（`assembly, typeName`）— 继承/接口关系（第 4 条）
- `ilspy_dependencies`（`assembly, typeName`）— 内部引用关系（第 4 条）

配套取舍（倾向）：
- **`languageVersion` 整体移除**：3 个工具里出现但使用频率极低，默认 ilspycmd 默认版本即可；
- **`timeoutSeconds` 保留**：作为安全网保留，默认 30s，agent 通常不传；
- **`list_types` 保留 `lines`**（4 参可接受）：list 结果可上百行（TestSamples 602 行）必须能翻页，而 `read_lines` 按 `typeName` 定位缓存、无法定位 list 结果，故 list 自带 `lines` 最合理；
- 新元数据工具（signature/hierarchy/dependencies）不经 ilspycmd、秒回，天然可轻量（只留 `assembly, typeName`）。
