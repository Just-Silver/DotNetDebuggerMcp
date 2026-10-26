<!-- 职责：对 `docs/reports/2026-10-26-disabled-control-misdiagnosis.md` 逐条结论的**独立复核**——重跑对照实验、读 WPF/FlaUI 源码、给出实测证据与判定（成立/推翻/未复现）；不写修复方案（由维护者决定），不重复原报告的现象叙述。 -->

# 复核：禁用控件误诊报告的三条结论是否成立

- **复核日期**：2026-10-26
- **被复核对象**：`docs/reports/2026-10-26-disabled-control-misdiagnosis.md`（下称「原报告」）
- **被测版本**：DotNetDebuggerMcp 2.5.0（`075f609`）
- **方法**：① 读 WPF / FlaUI / WPF-UI 源码核对异常来源；② 搭一次性 WPF 探针（`Wpf.Ui 4.3.0`），用本仓库 `ui_*` 工具重跑原报告的全部对照实验；③ 用独立 FlaUI 诊断程序打印异常类型与 HRESULT。
- **探针状态**：临时目录一次性产物，**未进仓库、已删除**，不属交付物。

## 1. 判定总表

| 原报告结论 | 复核判定 | 关键证据 |
|---|---|---|
| WPF-UI `NumberBox` 的 provider 不实现 ValuePattern | **推翻** | 探针中 `numberBox` 的 `ui_find` 报 `patterns=value,scroll,legacy`；`ui_input=123` 成功且 `ui_get value` 回读 `123`（§2） |
| `focus` 失败由 `IsEnabled=false` 引起 | **成立** | 禁用 TextBox 与禁用 Button 均复现同一句原文；启用对照全部成功（§2） |
| `OffscreenHint` 只指向「后台/不可见」造成误导 | **成立**（且比原报告更严重） | 同一次失败同时给「消息为空」+「去查 offscreen」，而 offscreen 恒为 False（§3） |
| 「`ui_input` 失败源于控件禁用」**未被证实** | **原报告过保守，可坐实（仅限空消息那一行）** | 空消息 = `ElementNotEnabledException`，其 `Message` 构造即为空（§4） |
| 选定相机后 `not implemented` 那次原因未查明 | **同意未查明，且已证伪「NumberBox 特有」** | 原生 NumberBox 无法复现；该消息可由任意抛 `NotImplementedException` 的 provider 产生（§5） |
| WPF-UI `NumberBox` 的 `ValueProperty` 默认 `UpdateSourceTrigger=LostFocus` | **成立** | `FrameworkPropertyMetadata(..., UpdateSourceTrigger.LostFocus)`（§6） |

## 2. 复现实验（启用 / 禁用 / NumberBox 三组对照）

探针窗口含：启用 `TextBox(plainBox)`、禁用 `TextBox(disabledBox)`、`Wpf.Ui.Controls.NumberBox(numberBox)`、启用/禁用 `Button` 各一。全部通过本仓库 `ui_*` 工具调用，**未改任何产品代码**。

| 控件 | `ui_get enabled` | `ui_input` | `ui_action focus` |
|---|---|---|---|
| plainBox（启用） | True | 成功（ValuePattern） | — |
| disabledBox（禁用） | **False** | `SetValue 调用失败：`（**消息为空**） | `Operation is not valid due to the current state of the object.` |
| numberBox（启用） | True | **成功**，回读 123 / 456 | — |
| enabledButton | True | — | 成功 |
| disabledButton | **False** | — | **与 disabledBox 完全相同的报错** |

两条关键结论：

1. **NumberBox 完全可驱动**。`ui_find` 报 `patterns=value,scroll,legacy`，写值成功并读回新值。原报告第 1 条结论彻底不成立。
2. **`focus` 的拒绝与控件类型无关**，只跟 `IsEnabled` 相关——用 `Button` 复现原报告 §2 的对照表，结论一致且更干净。

## 3. 错误文案的双重缺陷（原报告只说了一半）

禁用控件 `ui_input` 的实际返回：

```
SetValue 调用失败：；若为目标后台/不可见导致 provider 拒绝，可用 ui_get what=offscreen 确认，…
```

两个问题叠加：

- **冒号后面什么都没有**：真正的错误码与异常类型被丢掉（§4）。
- **引导指向错误方向**：`OffscreenHint` 被 `UiPatternDispatcher.Invoke` 无条件追加到**所有** pattern 调用失败上，而该控件 `ui_get offscreen` 实测为 `False`，与提示自相矛盾。

原报告只识别出第二点（误导方向），未识别第一点（错误码丢失）——而第一点才是「无法归因」的直接原因。

## 4. 空消息的根因（原报告未查明，本次坐实）

用独立 FlaUI 诊断程序直接打同一个禁用控件，打印原始异常：

```
ValuePattern.SetValue("123"): FlaUI.Core.Exceptions.ElementNotEnabledException
    msg='' hresult=0x80131500
    | inner=System.Runtime.InteropServices.COMException msg='0x80040200' hresult=0x80040200
```

链路完整闭合：

1. WPF `TextBoxAutomationPeer.SetValue` 首句即 `if(!IsEnabled()) throw new ElementNotEnabledException()`（`TextBoxAutomationPeer.cs:84-89`）。
2. UIA 把该异常转成 HRESULT `0x80040200`（`UIA_E_ELEMENTNOTENABLED`）。
3. FlaUI `Com.ConvertException` 按码表映射回 `FlaUI.Core.Exceptions.ElementNotEnabledException`（`Com.cs:119-121`）。
4. FlaUI 该系列异常**全部**用 `base(String.Empty, innerException)` 构造 → `Message` 恒为空串；真实错误码只在 `InnerException.HResult` 里。
5. 本仓库 `UiPatternDispatcher.Invoke` 只取 `ex.Message` → 最终文案变成「SetValue 调用失败：」。

结论：**空消息不是「provider 没报错」，而是 MCP 丢弃了唯一可用于归因的字段。** 因此原报告 §2.1「同样未被证实」可以收回到「已证实」——空消息那一行对应的就是控件禁用。

## 5. `not implemented` 那次：无法归因，但已证伪「NumberBox 特有」

- 原生 `Wpf.Ui 4.3.0` NumberBox（含禁用后重新启用）**未能复现**该报错。
- 该文本有唯一来源组合：`E_NOTIMPL (0x80004001)` 的系统消息，或 `NotImplementedException` 的默认消息——两者字符串完全相同，**仅凭文案无法区分**。
- 用探针里自定义的 peer（`SetValue` 抛 `NotImplementedException`）可 100% 复现同一文案，证明**任意 provider 抛该方法都会产生这句**，不需要是 NumberBox：

```
ValuePattern.SetValue("123"): System.NotImplementedException
    msg='The method or operation is not implemented.' hresult=0x80004001
```

- 已排除的候选：provider 缺 `IValueProvider`（源码已否）、元素已销毁（那是 `ELEMENTAVAILABLE` 码）、只读（抛 `ElementNotEnabledException`）。
- 剩余未验证假设：那次 `index=4` 解析到的不是 NumberBox 自身 peer，而是经 MSAA/代理桥暴露的中间元素；或被测应用自置了 peer。**在两份源码里都找不到证据，需在原始环境复现才能定论。**

## 6. 附带确认（独立于误诊链）

WPF-UI `NumberBox` 注册 `ValueProperty` 时把 `UpdateSourceTrigger` 默认值设为 `LostFocus`：

```csharp
// Wpf.Ui/Controls/NumberBox/NumberBox.cs
new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
    OnValueChanged, null, false, UpdateSourceTrigger.LostFocus)
```

即只写 `Mode=TwoWay` 时，必须失焦才写回 ViewModel——这与本仓库 `ui_input` 默认 `commit=true` 的设计正好对齐。原报告 §8 的这条独立结论**成立**。

## 7. 复核给出的净结论

1. 原报告**方向正确**：`focus` 失败由禁用导致、`OffscreenHint` 造成误导、`ui_find` 不显示 `enabled`——三条均经实测成立。
2. 原报告**低估了自己**：它能证明的比它敢写的多一条——空消息即 `ElementNotEnabledException`。
3. 原报告**最想知道的**（`not implemented` 归因）仍未解决，但范围已收窄：与 NumberBox 无关，需要原始环境复现。
4. 真正的可观测性缺陷有两处，不在 agent 侧：**异常信息只剩 `ex.Message`**（错误码/类型全丢）与**单一成因提示被无条件追加**。

## 8. 后续处置（2026-10-26 已实施）

已按上述 4 条落地（`[Unreleased]`，见 `CHANGELOG.md`）：

| 缺陷 | 处置 | 落点 |
|---|---|---|
| 错误码/信息丢失 | 新增 `UiErrorDescriber`：HRESULT → 中文成因 + 异常类型 + 错误码 | `src/SharpSight.UiAutomation/UiErrorDescriber.cs` |
| 引导方向单一 | 按成因分流（禁用→查 `enabled`、失效→重新 `ui_find`、未实现→换驱动路径），未识别才退回原提示 | 同上 + `UiPatternDispatcher.Invoke`/`ExecuteScroll` |
| `enabled` 不可见 | `ui_find` 对禁用控件追加 `[禁用]` 标记 | `UiElementLocator` + `UiTools.cs` |

**刻意未做**：动作前按 `IsEnabled` 前置拒绝。`IsEnabled` 与 `IsReadOnly` 同源存在 MSAA/代理桥误报风险（本仓库已有 `IsReadOnlyPattern` 的相关教训），误报会把本来可用的控件挡在门外——从「失败后猜错」退化成「根本不让试」。错误码透出后，失败本身已足以准确定性问题。

一处复核中发现的坑，供后续维护参考：**`InvalidOperationException` 的默认 `HResult` 恰为 `0x80131509`，与 `UIA_E_INVALIDOPERATION` 同值**，因此「无法识别成因」的兜底分支不能用它做样本（会被判成「当前状态不允许」）。

