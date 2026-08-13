# JSON 格式化输出可行性验证报告

分支：`feat/json-output-verification`，日期：2026-08-13

## 验证目标

评估将 MCP 工具标准输出从「当前纯文本行号格式」改为 JSON 结构化输出的：
1. **可行性**——结构设计是否成立、信息是否无损
2. **token 量级**——相对现状的 token 开销增量

## 测量方法

- 取真实输出样本：用 Debug 版 CLI 对 `ILSpyMcp.TestSamples.dll` 生成 6 类代表性输出（反编译 200 行/50 行、list_types、signature、decompile_member、call_graph），另构造含引号/反斜杠/中文注释的合成样本
- token 计数用 OpenAI `cl100k_base`（tiktoken 0.13.0），与主流 agent 上下文计费口径一致
- 逐行解析当前格式（`行号\t内容`，`---` 以上为头部），转多种 JSON 方案后比较 token 数

## 结论 1：可行性成立

- **round-trip 无损**：`{line, content}` 行数组 JSON 解析回 `行号\t内容` 与原始完全一致（含引号、反斜杠、中文注释）
- 结构清晰：行号与内容分离，agent 可直接按 `line` 字段定位引用，无需解析 `\t` 前缀
- 头部元数据（程序集/目标/总行数/当前输出/剩余）可并入顶层字段，与行数组并存

## 结论 2：token 量级（紧凑 JSON，`separators=(',',':')` + `ensure_ascii=False`）

| 样本 | 当前纯文本 | 紧凑 JSON | 增量 |
| ---- | ---- | ---- | ---- |
| 反编译 200 行 | 3001 | 4518 | +50.5% |
| 反编译 50 行 | 751 | 1068 | +42.2% |
| list_types 648 类 | 3095 | 4035 | +30.4% |
| signature | 116 | 55 | -52.6% |
| decompile_member | 142 | 101 | -28.9% |
| call_graph | 125 | 58 | -53.6% |
| 合成含转义/中文 100 行 | 1842 | 2477 | +34.5% |

**规律**：
- 反编译类（多行、行内重复结构）JSON 开销 **+30%~50%**，主要来自每行的 `"line":N,"content":` 键名与引号
- 元数据类（signature/hierarchy/dependencies/call_graph 短行）JSON **反而更省**（-30%~-50%）——当前纯文本每行只有单字段，JSON 去掉头部信息块后净减少
- 短行反编译（decompile_member 等）也多不亏

## 陷阱（必须避免）

| 方案 | 增量 | 说明 |
| ---- | ---- | ---- |
| `ensure_ascii=True`（中文 `\uXXXX` 转义） | **+110%** | 中文字符每字符 6 字节，token 爆炸；实现必须 `ensure_ascii=False` |
| pretty 打印（`indent=2`） | **+104%** | 缩进/换行翻倍；必须紧凑分隔符 |

## 可选降本方案

- **纯内容数组（去行号）**：+5.4%（含转义样本）——若 agent 不需要行号引用可最省，但丧失行定位能力，与当前「行号标注」卖点冲突，不推荐
- **单行大字符串**：+14.4%——结构最简但无结构化字段，收益有限

## 建议

1. **元数据类工具（signature/hierarchy/dependencies/call_graph/list_types）适合改 JSON**：token 反而下降，且行号定位价值低
2. **反编译类工具（decompile/decompile_member）改 JSON 需接受 +30~50% 开销**：收益是行号成为结构化字段、agent 无需解析 `\t` 前缀；是否值得取决于 agent 对行号字段的直接消费需求
3. 若实施：必须用紧凑分隔符 + `ensure_ascii=False`，禁用 pretty 与 unicode 转义，否则 token 翻倍
4. 头部信息块并入顶层字段（`assembly`/`target`/`totalLines`/`current`/`remaining`），与行数组 `lines` 并存

## 附：验证脚本

测量脚本位于 `%TEMP%\opencode\measure_json*.py`（本次验证用，未入库）。
