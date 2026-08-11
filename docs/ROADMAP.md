# ROADMAP（远期待办）

> 记录「暂不做、以后再评估」的功能想法，防止丢失。当前迭代范围见 `CHANGELOG.md` 的 `[Unreleased]` 段。

## 远期可选：IL 方法体调用图（`ilspy_dependencies` 完整版）

**状态**：pending（v2.0 只做签名级引用，不做本项）

**要做什么**：在现有「签名级引用」（成员签名/基类/接口/特性中的内部类型引用）之上，增加**方法调用边**——扫描方法体 IL 的 `call`/`callvirt`/`newobj`/`ldftn` 等指令，提取程序集内部的调用关系，形成行为级调用图。

**为什么暂不做**（难点，详见 `docs/ilspymcp_建议.md` §4 与审查结论）：

1. **手写 IL 解码**：`MethodBodyBlock` 只给字节流，需自行解析全部 opcode operand（`switch`、`calli` 签名 token、`ldtoken` 等可变长度编码），写错即漏边/错边。
2. **引用解析链长**：`MemberRef` owner 可能是 `TypeRef`/`TypeDef`/`TypeSpec`；泛型实例化（`Dictionary<string, List<int>>`）需层层 `SignatureDecoder`；同程序集引用编译器通常发 `TypeRef`，「内部类型」判定要沿 ResolutionScope 链回溯。
3. **语义歧义**：`call`/`newobj`/`ldfld`/字段类型/签名类型/特性类型……每种都是不同的「引用」，噪声天差地别；一个类型 N 个方法都调 B，聚合成一条边还是 N 条？
4. **与源码观感脱节**：裸 IL 是元数据原始形态，与 ilspycmd 反编译的语法糖输出（如 `List<int>` 折叠）对不上。

**已确认可借鉴的方法论**（源自 codegraph 调研，见子代理结论）：
- 程序集边界过滤（外部引用天然丢弃，IL 的 metadata token 比名字匹配更精确、无歧义）；
- 窄化边类型（call/callvirt→calls、newobj→instantiates、继承→extends）；
- 按 (source, target, kind) 去重。

**未解问题（做之前要定）**：
- 调用图聚合粒度（方法级 vs 类型级）与输出体积控制（200 行截断/分页是否够）；
- 编译器生成物（闭包/迭代器类、泛型实例化、ValueTuple）的过滤规则——codegraph 的「名字存在性过滤」对 IL 无效，需自建（可复用 `CompilerGeneratedFilter`）；
- 是否与 hierarchy/signature 合并输出，还是独立工具。
