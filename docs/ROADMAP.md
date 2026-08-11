# ROADMAP（远期待办）

> 记录「暂不做、以后再评估」的功能想法，防止丢失。当前迭代范围见 `CHANGELOG.md` 的 `[Unreleased]` 段。

> 已完成项已移出本清单：**IL 方法体调用图**已实现为 `ilspy_call_graph` 工具（见 CHANGELOG.md `[Unreleased]`）。实现中化解了原记难点：IL 解码用 ECMA-335 操作数跳表 + `PEReader.GetMethodBody`（免手写全量解码器）、内部类型判定走 MethodDef/MethodSpec 直判 + MemberRef 沿 ResolutionScope 回溯、编译器生成 target/source 复用 `CompilerGeneratedFilter` 过滤。

