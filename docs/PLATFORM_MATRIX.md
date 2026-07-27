# SnapBoard 平台支持矩阵

> 等级：完整、受限、计划中、不支持。只有完成对应系统实机验收后才能标记“完整”。

| 平台 | 目标版本/桌面 | 剪贴板监听 | 全局快捷键 | 自动粘贴 | 凭据存储 | 当前状态 |
| --- | --- | --- | --- | --- | --- | --- |
| Windows | Windows 11 x64 | 受限：原生适配器与 delayed rendering 已验证，外部应用矩阵未完成 | 受限：原生注册、冲突回滚和配置已验证，物理按键待验收 | 受限：Notepad/WinUI 已实机通过，管理员目标待验收 | DPAPI/Credential Locker | 一期 Phase 1.2/1.3 收口中 |
| macOS | macOS 26.2 arm64 已测；x64 待测 | 受限：原生适配器已实现，长稳与桌面生命周期待验收 | 计划完整 | 受限：TextEdit 允许/拒绝状态已测，需辅助功能权限 | Keychain 计划中 | 二期 Phase 2.1 进行中 |
| Ubuntu | GNOME X11 | 计划完整 | 计划完整 | 计划完整 | Secret Service | 三期 |
| Ubuntu/Fedora | GNOME Wayland | 配套扩展或受限 | 桌面接口决定 | 通常受限 | Secret Service | 三期高风险 |
| Fedora/KDE | Plasma Wayland | 待验证 | 待验证 | 可能受限 | KWallet/Secret Service | 三期 |
| Debian/Mint | X11 主流桌面 | 计划完整 | 计划完整 | 计划完整 | Secret Service | 三期 |

## Windows 11 验收

- [x] `AddClipboardFormatListener` 消息生命周期、取消和退出清理。
- [x] 剪贴板占用和延迟渲染重试：有限退避、取消和真实 delayed-rendering owner 已验证。
- [~] 前台窗口恢复和真实全局快捷键冲突已验证；物理按键、多显示器与 DPI 实机待验收。
- [~] 普通权限应用与管理员应用之间的 UIPI 降级逻辑已实现，管理员目标实机待验收。
- [~] 单实例、第二实例激活、后台启动、按需窗口和退出清理已实测；托盘菜单点击与真实开机启动待验收。
- [~] `win-x64` Native AOT 本机 0 警告并实际启动；三次可见/窗口关闭后的 PWS、Private Bytes、CPU 和句柄已记录，GitHub Runner、10 分钟与 8 小时增长待验证。

外部应用交互状态：Windows 11 打包版 Notepad 已通过文本复制；该进程明确加载 Microsoft.UI.Xaml，并通过纯文本写回、前台恢复和自动粘贴。浏览器、Explorer 文件复制、管理员窗口、Office 和远程桌面尚未通过。详见 `docs/WINDOWS_CLIPBOARD_VALIDATION.md`。

## macOS 验收

- [x] `NSPasteboard.changeCount` 生命周期、去重、取消、有界队列、反馈抑制和 100/500 ms 退避；AOT 监听探针平均 CPU 0.001%，`DroppedEvents=0`。
- [x] Text、HTML、RTF、PNG、TIFF、文件 URL、UTI 清单、完整写回和纯文本写回。
- [x] TextEdit 目标捕获、切换到 Finder 后恢复目标并发送 Command+V。
- [~] 辅助功能允许与独立应用身份拒绝已实测；系统设置入口、撤销后同一身份重试和重新授予待完成。
- [~] Finder、Safari、Chrome、Preview 和 `pbcopy` CLI 已实测；可见 Terminal UI、Office 和远程桌面未验证。
- [ ] 菜单栏、全局快捷键、登录启动、单实例、多 Space、多显示器、全屏应用和睡眠唤醒。
- [~] `osx-arm64` Native AOT 0 告警并实际启动；`osx-x64`、签名、公证、Keychain 和正式安装包待完成。

详细证据、性能样本和未验证项见 `docs/MACOS_CLIPBOARD_VALIDATION.md`。

## Linux 验收

- X11 selection 所有权变化和应用退出时内容持久化。
- Wayland 桌面、Portal 和扩展能力检测。
- `.deb`、`.rpm`、AppImage 的依赖、托盘和自启动差异。
- Secret Service/KWallet 不可用时禁止明文落盘，并给出明确配置状态。

## 发布标注

Release Notes 必须列出每个桌面环境的支持等级。不能只写“支持 Linux”而隐藏 Wayland 的剪贴板或自动粘贴限制。
