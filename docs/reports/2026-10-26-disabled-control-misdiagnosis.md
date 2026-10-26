<!-- 职责：一次 agent 使用 UI 自动化族时的误诊记录与证据——禁用控件的语义动作失败、错误文案的归因指向、ui_find 清单缺 enabled 字段三者的因果链与实测数据；不写修复方案（由维护者决定），不写 WPF-UI 数字框的其他坑（属被测应用侧）。 -->

# UIA 误诊报告：禁用控件被误判为「控件不支持」

- **报告日期**：2026-10-26
- **被测版本**：DotNetDebuggerMcp 2.5.0（`075f609 chore(release): 2.5.0`）
- **被测应用**：WPF / .NET 10 桌面应用（`Kvm.Toolkit.App`）
- **报告性质**：使用侧误诊记录。三次错误结论均由 agent 作出，其中一次被 MCP 的错误文案直接引向错误方向。

## 1. 现象

对同一个 `NumberBox` 内的 Edit 控件（手动调试页「点击点 X」）连续三次操作失败：

| 操作 | `enabled` 当时 | 返回 |
| --- | --- | --- |
| `ui_input(index=4, value="1920")`（相机未选） | `False` | `SetValue 调用失败：`（`ex.Message` 为空） |
| `ui_input(index=4, value="1920")`（选定相机后） | `True` | `SetValue 调用失败：The method or operation is not implemented.` |
| `ui_action(verb="focus", index=4)` | `False` | `Focus 调用失败：Operation is not valid due to the current state of the object.` |
| `ui_get(what="offscreen", index=4)` | — | `False` |

`offscreen` 为 `False`，与错误文案给出的排查方向不符，但当时没有继续追查。两行 `SetValue` 是同一调用在不同状态下的两次尝试，不能合并为同一原因。

## 2. 实测：`focus` 的触发条件是 `IsEnabled=false`

事后在**同一进程、同一控件类型**上做对照实验，只改 `enabled` 一个变量：

| 控件 | `ui_get what=enabled` | `ui_action verb=focus` |
| --- | --- | --- |
| 「暂停」（未运行时禁用） | `False` | `Focus 调用失败：Operation is not valid due to the current state of the object.` |
| 「运行」（始终可用） | `True` | `已 focus（Focus）` |

同一错误文案、同一控件类型（`Button`），差异只在 `enabled`。**该错误与控件是否为数字框、是否为 ComboBox 无关**——此结论仅就 `focus` 成立。

### 2.1 `ui_input` 的失败原因未能归一到同一结论

`ui_input` 在事故中留下两条**不同的**错误文本，且发生于不同 `enabled` 状态：

| 时刻 | `enabled` | 返回 |
| --- | --- | --- |
| 相机未选时 | `False` | `SetValue 调用失败：`（`ex.Message` 为空） |
| 选定相机后 | `True` | `SetValue 调用失败：The method or operation is not implemented.` |

同一控件在后续测试中写入成功：

- `已 input（ValuePattern），已失焦提交（Tab）`，随后 `ui_get what=value` 回读 `123`。

因此「数字框不支持 ValuePattern」这一当时结论不成立；但**「`ui_input` 失败源于控件禁用」同样未被证实**——`not implemented` 那次 `enabled` 为 `True`。该次失败的真实成因未查明，本报告不做归因。

## 3. 当时的三条错误结论

| # | 当时结论 | 与实测的偏差 |
| --- | --- | --- |
| 1 | 「WPF-UI `NumberBox` 的 UIA provider 不实现 ValuePattern，无法写值」 | 同一控件可正常写值（ValuePattern + 失焦提交） |
| 2 | 「`NumberBox` 拒绝 Focus，是控件实现限制」 | 同一控件启用后可 Focus；拒绝可由 `IsEnabled=false` 单独复现 |
| 3 | 「问题出在控件类型」 | 对照组为 `Button`，同样复现 `focus` 拒绝 |

第 1、2 条结论在事故后被作为「MCP 的已知限制」向用户陈述过一次。

## 4. 相关代码位置

### 4.1 错误文案的统一归因

`src/SharpSight.UiAutomation/UiPatternDispatcher.cs:29`

```csharp
internal const string OffscreenHint =
    "；若为目标后台/不可见导致 provider 拒绝，可用 ui_get what=offscreen 确认，必要时先 ui_action verb=windowstate windowstate=normal 还原最小化窗口。";
```

该常量被两处异常包装引用（同文件 `:345` 滚动、`:353` 通用包装）：

```csharp
catch (Exception ex) { throw new UiException($"{op} 调用失败：{ex.Message}{OffscreenHint}"); }
```

`OffscreenHint` 只覆盖「后台/不可见」一种成因，未提及 `IsEnabled=false`。本次事故中 `offscreen` 实测为 `False`，排查被引向窗口状态，而真实原因是控件禁用。

### 4.2 `focus` 的分派判据

`src/SharpSight.UiAutomation/UiPatternDispatcher.cs` 的 `ChooseByCapability`：

```csharp
case "focus":
    // 窗口 Focus 会抢前台（spec §9）——有 WindowPattern 视为窗口，引导改走 windowstate。
    if (caps.Window) throw new UiException("目标是窗口，focus 会抢前台；…");
    return new ChosenAction(verb, UiActionKind.Focus, "Focus", direction, lines, windowstate);
```

纯决策函数只依据 `WindowPattern` 能力快照，不读取 `IsEnabled`；禁用态的拒绝发生在执行阶段，随后被 `:353` 的统一包装捕获。

### 4.3 `ui_find` 清单不输出 `enabled`

`src/DotNetDebuggerMcp/Tools/Debugger/UiTools.cs:64`

```csharp
sb.Append($"[{el.Index}] {el.Type} Name={name} AutoId={autoId} patterns={patterns} Rect=({el.Rect})");
```

清单行包含 `Type / Name / AutoId / patterns / Rect`，不含 `enabled`。本次事故中，ui_find 输出未体现目标控件禁用；`enabled` 只能靠额外调用 `ui_get what=enabled` 逐项获取。

## 5. 因果链

`focus` 的链路（实测闭环）：

1. 应用未选择采集设备 → 依赖帧尺寸的输入框 `IsEnabled=false`
2. `ui_find` 清单不显示 `enabled`，该状态不可见
3. 对禁用控件调用 `ui_action(focus)` → provider 拒绝
4. 异常被 `:353` 包装，附加 `OffscreenHint`（只指向「后台/不可见」）
5. 按提示查 `ui_get what=offscreen` → `False`，与提示矛盾
6. 缺乏解释时，agent 转向猜测控件实现差异，得出「数字框不支持 UIA」的错误结论
7. 该结论被当作 MCP 限制对外陈述

其中第 2、4 步是 MCP 侧可观测性缺口，第 6 步是 agent 侧的方法问题（无对照实验即下结论）。第 3 步仅对 `focus` 完成实测；`ui_input` 的 `not implemented` 那次不在此链路内（见 §2.1）。

## 6. 影响面

- 本次事故中，错误结论被写进给用户的说明，用户据此认为 MCP 存在控件兼容缺陷。
- 排查耗时约 15 轮工具调用，其中大部分在错误方向上。
- `ui_get what=enabled` 在事故过程中被调用过并返回 `False`（相机未选时），该证据当时未被采信。

## 7. 复现步骤

环境：任意 WPF 应用，含一个 `IsEnabled=false` 的控件与一个可用控件。

```
1. ui_find(process=P, type=Button)          → 记下禁用控件 index
2. ui_get(process=P, what=enabled, index=N) → False
3. ui_action(process=P, verb=focus, index=N)
   → "Focus 调用失败：Operation is not valid due to the current state of the object.；若为目标后台/不可见…"
4. ui_get(process=P, what=offscreen, index=N) → False   （与提示方向矛盾）
5. 对 enabled=True 的控件重复 3 → 成功
```

## 8. 附：本次事故中仍然成立的独立结论

以下结论与本报告的错误归因无关，证据来源不同（源码而非 UIA 行为），记录以免被一并推翻：

- WPF-UI `NumberBox` 注册 `ValueProperty` 时把 `UpdateSourceTrigger` 默认值设为 `LostFocus`（`Wpf.Ui/Controls/NumberBox/NumberBox.cs` 的 `FrameworkPropertyMetadata`），只写 `Mode=TwoWay` 时需失焦才写回视图模型。
