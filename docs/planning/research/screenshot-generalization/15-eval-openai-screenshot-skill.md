# 15 · OpenAI Codex `screenshot` skill 评估（openai/skills 27.7k★）

> **来源等级：B｜官方仓库网页读取**（未本地 clone）。仓库 `openai/skills`（Codex 技能目录，27,668★，`license` 字段为 null、目录内含 `LICENSE.txt`），commit `49f948f`（2026-06-24）。
> 读取对象：`skills/.curated/screenshot/SKILL.md`（7,754 B）、`scripts/take_screenshot.ps1`（4,947 B）；目录另有 `scripts/take_screenshot.py`、`ensure_macos_permissions.sh`、`macos_*.swift`。
> **结论：不满足本项目三目标。** 它是 **Codex agent 技能＝Markdown 指令 + 各 OS 脚本**，不是库/不是 MCP 工具；只覆盖目标①的极小一部分，对②图像经济、③语义桥几乎为零。仅可作为「工具优先原则 / 保存位置策略 / 参数互斥」的**高星官方佐证**。

## ① 它是什么

- **形态**：`SKILL.md`（触发描述 + 使用说明） + `scripts/` 下每 OS 一个抓取脚本（macOS：Swift/Python；Linux：Python 包装 `scrot`/`gnome-screenshot`/ImageMagick `import`；Windows：PowerShell）。**没有库、没有 MCP server、没有服务端进程**。
- **触发语**（`SKILL.md:description`）：显式要求桌面/系统截图，或「tool-specific capture capabilities are unavailable」时兜底。
- **返回形态**：**保存到磁盘并回传路径**（原文「Always report the saved file path」）；**无内联图像内容**（不是 MCP image content）。
- **平台差异**：`--app`/`--window-name`/`--list-windows` 仅 macOS；Linux 用 `--active-window`/`--window-id`；Windows 只有 region / active window / window handle。

## ② Windows 脚本能力面（`take_screenshot.ps1` 实测）

| 参数 | 取值 | 说明 |
|---|---|---|
| `-Path` | 路径 | 显式保存路径（支持 `~`、环境变量、目录则补默认文件名） |
| `-Mode` | `default`/`temp` | 默认目录或临时目录 |
| `-Format` | `png`/`jpg`/`jpeg`/`bmp` | 仅这四种；**无 quality 参数** |
| `-Region` | `x,y,w,h` | 区域（校验 w/h>0） |
| `-ActiveWindow` | switch | `GetForegroundWindow` |
| `-WindowHandle` | int | 指定 hwnd |

实现：`[System.Windows.Forms.SystemInformation]::VirtualScreen`（全屏，虚拟桌面并集）→ `GetWindowRect`（窗口，**含边框/阴影，未处理 DPR**）→ `Graphics.CopyFromScreen` → `bitmap.Save`。三参数互斥，冲突直接抛错（`Choose either ...`）。

## ③ 对照本项目三目标（差距表）

| 需求 | 支持 | 说明 |
|---|---|---|
| ① 全屏 / 虚拟桌面 | ✅ | `VirtualScreen` |
| ① 多显示器**逐屏选择** | ❌ | Windows 只有虚拟桌面单图；macOS 才「一屏一文件」 |
| ① 前台窗口 | ✅ | `-ActiveWindow` |
| ① hwnd | ✅ | `-WindowHandle`（无 `IsWindow`/`GA_ROOT` 校验） |
| ① 窗口标题 / 应用名 | ❌ | Windows 无（macOS `--app`/`--window-name` 才有） |
| ① 客户区 vs 整窗 | ❌ | 直用 `GetWindowRect`（含阴影/边框） |
| ① 元素级（UIA） | ❌ | — |
| ① 坐标空间 / `origin+scale` | ❌ | 无任何坐标元数据回传 |
| ① 旧帧护栏 | ❌ | — |
| ② webp / quality | ❌ | 固定 png（Windows 可 jpg/bmp，无质量旋钮） |
| ② 双轴降采样 | ❌ | 原尺寸输出 |
| ② 灰度 | ❌ | — |
| ② diff / 无变化检测 | ❌ | — |
| ② by-ref / 内联 | ❌ | 始终落盘返回路径 |
| ③ OCR | ❌ | — |
| ③ UIA 文本树 | ❌ | — |
| ③ Set-of-Marks | ❌ | — |

**覆盖度**：目标① ≈ 局部（约 1/4），目标② = 0，目标③ = 0。

## ④ 值得借鉴（高星/官方佐证）

1. **「工具优先」原则**（`SKILL.md` Tool priority 段）：优先专用工具（Figma MCP / Playwright / agent-browser），OS 级抓取兜底 → **佐证本项目「结构/UIA 优先、像素兜底」的取向**，可作高星官方外部依据。
2. **保存位置优先级**：显式 path > OS 默认截图目录（Windows 为 `Pictures\Screenshots`）> 临时目录（agent 自身检查用）→ 可参考本色 `filePath` 默认值语义与「自身检查图落临时目录」的文案。
3. **参数互斥显式报错**：`-Region`/`-ActiveWindow`/`-WindowHandle` 互斥，冲突抛错 → 正对应本项目已定的「不兼容组合显式报错而非静默丢参」。
4. **`GetWindowRect` 直用**（含阴影、未处理 DPR）→ **反证**本项目选 `DWMWA_EXTENDED_FRAME_BOUNDS` 的价值（`01`）。
5. **多显示器跨平台语义差异**（macOS 一屏一文件 vs Windows/Linux 虚拟桌面单图）→ 印证本项目 Windows 走「虚拟屏并集」的取向。
6. **始终回传保存路径** → 与本项目「>2MB 落盘并返回路径」的双轨一致。

## ⑤ 不采纳

- 以「落盘返回路径」作**唯一**返回形态（本项目默认内联 image content，超限才落盘）。
- 无降采样/格式质量/灰度/diff/OCR/UIA/SoM（本项目三目标要补的全部内容）。
- 无坐标元数据、无旧帧护栏。
- 仅 Windows 的 `CopyFromScreen` 即 GDI BitBlt 口径（本项目已有 WGC→PrintWindow→BitBlt 更完整链路与回退原因回传）。

## ⑥ 评估结论

**不满足需求**——它是 Codex 生态里一个**最小化的跨平台截图技能**，价值在「agent 用哪种外壳（skill vs MCP）」与「工具优先/保存位置/参数互斥」几条**工程约定**上，不构成参数面设计依据。
可作为 `04-prior-art.md` 的一条**高星官方佐证**（替换原 0–32 星同类），但**不作为设计依据**。

## 附：来源

- 仓库：`https://github.com/openai/skills`（27,668★；`license=null`；HEAD commit `49f948faa9258a0c61caceaf225e179651397431`，2026-06-24）。
- `skills/.curated/screenshot/SKILL.md`。
- `skills/.curated/screenshot/scripts/take_screenshot.ps1`。
- 未读取（仅知存在）：`scripts/take_screenshot.py`、`scripts/ensure_macos_permissions.sh`、`scripts/macos_*.swift`。
