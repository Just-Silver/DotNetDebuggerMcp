<!-- 职责：说明独立 GUI 交互 PoC 的构建、脚本/CLI、护栏与清理，面向运行实验者；不写：机制选型全文或验收结果（见 research/07、08）。 -->

# NativeInputSpike

一次性实验，不进入 slnx/CI，不代表产品 Runtime。优先复用宿主 Debug 构建与现有 `SharpSight.Capture`；没有自包含发布、额外 Runtime 安装或生产 `src/` 改动。

机制/后台消息结果：[research/07 §6](../../../docs/planning/research/07-desktop-isolation-options.md#6-2026-11-14-不干扰用户的-gui-交互复核)。Child Session 结果：[research/08 §6](../../../docs/planning/research/08-rdp-child-session-spike.md#6-2026-11-14-原生输入补充-poc)。

## 构建和运行

仓库根 PowerShell：

```powershell
$script = 'tests/Fixtures/NativeInputSpike/native-input-spike.ps1'
pwsh -NoProfile -File $script -Action Build
pwsh -NoProfile -File $script -Action Probe
```

产物：`tests/TestData/NativeInputSpike/build2/bin/NativeInputSpike/debug/NativeInputSpike.exe`。直接 exe CLI 同样可用。

### 后台窗口消息实验（无系统设置修改）

```powershell
$exe = 'tests/TestData/NativeInputSpike/build2/bin/NativeInputSpike/debug/NativeInputSpike.exe'
$root = Join-Path $PWD ('tests/TestData/NativeInputSpike/background-' + [Guid]::NewGuid().ToString('N'))
& $exe background $root
```

只启动自建的非激活窗口，向其 HWND 发消息；普通区域计数/拖拽、严格前台区域、文本/快捷键及截图分别记录。父会话 worker 拒绝 `real` 请求，不能在此模式下试探真实输入。结果为原始 JSON，不把消息成功标成物理输入成功。

### Child Session 实验（需明确授权）

```powershell
pwsh -NoProfile -File $script -Action Enable   # 一次 UAC，明确修改系统 child-session 开关
pwsh -NoProfile -File $script -Action Start    # 保留连接窗口；必要时在系统界面正常认证
pwsh -NoProfile -File $script -Action Launch   # 登录后投送 worker；临时计划任务随即删除
pwsh -NoProfile -File $script -Action Request -Operation status
pwsh -NoProfile -File $script -Action Request -Operation real
pwsh -NoProfile -File $script -Action Request -Operation commit
pwsh -NoProfile -File $script -Action Request -Operation unicode
pwsh -NoProfile -File $script -Action Request -Operation shortcut
pwsh -NoProfile -File $script -Action Request -Operation capture
pwsh -NoProfile -File $script -Action Exercise -Seconds 60
pwsh -NoProfile -File $script -Action Stop
# 只有确认没有其他使用者、且实验前原值为关闭时，才恢复：
pwsh -NoProfile -File $script -Action Disable
```

- `real` 只在 child 内将目标还原/激活，再用系统 SendInput 移动、点击、拖拽和键码输入；不是测试“保持隐藏/最小化目标也能命中”。
- `commit` 向当前 child 焦点发送 Enter 提交 IME，再测 Ctrl+K；`unicode` 走 SendInput/VK_PACKET 文本，`shortcut` 测 Ctrl+F6。**这些补充命令应紧接 real，在自有测试窗仍为 child 前台时使用**；不能把 IME composition 未提交当作文本已完成。
- `hide/minimize/show/messages/capture/status` 可通过 `Request -Operation` 调用。最小化外层客户端与最小化 child 内目标是不同实验，不应混记。
- `Exercise` 自动启动父侧只读观察器；观察器仅计数本轮 `dwExtraInfo` 标记，不记录用户普通按键内容；光标/前台采样不替代人工同时操作反馈。
- 每次 `Exercise` 的报告写入实例下新的 `run-<随机标识>/`，避免覆盖历史观测；输出 `reportDirectory`。发生失败也保留摘要，不能只读 `allMachineChecksPassed` 就宣称人工验收完成。
- 通过 `-Instance <报告目录>` 选择指定实验实例；不要求经过 MCP。当前 CLI 为 PoC 控制面，**尚未实现任意脚本远程执行、完整任务取消/恢复或正式常驻 Runtime**。

## 安全与失败口径

- 不调用 SwitchDesktop/BlockInput，不安装驱动，不改密码、凭据委派、Hello、证书或安全策略；不关闭 IME。
- 真实输入 worker 必须明确标为 child，实际 Session 不同于父且非 0，Desktop 与 OpenInputDesktop 匹配；父端请求还校验 PID+创建时间。不是抵御同用户恶意进程的安全边界。
- 测试只操作自有 child 和自建窗口。子会话仍共享宿主文件/注册表/用户配置，不是沙箱；请不要在其中打开敏感应用或交互操作生产硬件。
- `Stop` 核对父会话、当前 child、worker PID/创建时间后注销本轮 child，再关闭匹配 holder；身份不符直接拒绝，不使用进程名批量 kill。worker 已自行退出等情况下需人工核对，不提供猜测式自动清理。
- 关闭连接窗口只是断开，不应当作注销。`Disable` 是单独显式命令，不随 CLI 退出自动执行；正常构建前先停止自有 worker，避免输出二进制被占用。
- 当前只在自建 WinForms 测试目标上验证；目标框架、COM/Windows App SDK、专用硬件和多应用兼容性按结果文档列为未测，不能由这个探针外推。
