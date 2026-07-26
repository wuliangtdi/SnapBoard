# SnapBoard 平台支持矩阵

> 等级：完整、受限、计划中、不支持。只有完成对应系统实机验收后才能标记“完整”。

| 平台 | 目标版本/桌面 | 剪贴板监听 | 全局快捷键 | 自动粘贴 | 凭据存储 | 当前状态 |
| --- | --- | --- | --- | --- | --- | --- |
| Windows | Windows 11 x64 | 计划完整 | 计划完整 | 计划完整，受 UIPI 限制 | DPAPI/Credential Locker | 一期进行中 |
| macOS | 当前受支持 macOS arm64/x64 | 计划完整 | 需辅助功能权限 | 需辅助功能权限 | Keychain | 二期 |
| Ubuntu | GNOME X11 | 计划完整 | 计划完整 | 计划完整 | Secret Service | 三期 |
| Ubuntu/Fedora | GNOME Wayland | 配套扩展或受限 | 桌面接口决定 | 通常受限 | Secret Service | 三期高风险 |
| Fedora/KDE | Plasma Wayland | 待验证 | 待验证 | 可能受限 | KWallet/Secret Service | 三期 |
| Debian/Mint | X11 主流桌面 | 计划完整 | 计划完整 | 计划完整 | Secret Service | 三期 |

## Windows 11 验收

- AddClipboardFormatListener 消息生命周期。
- 剪贴板占用和延迟渲染重试。
- 多显示器、DPI、前台窗口恢复和全局快捷键冲突。
- 普通权限应用与管理员应用之间的 UIPI 降级提示。
- 托盘、开机启动、单实例和退出清理。
- `win-x64` Native AOT、Private Working Set 和句柄长期增长。

## macOS 验收

- NSPasteboard changeCount 轮询周期与空闲 CPU。
- 辅助功能权限被拒绝、撤销和重新授予。
- 多 Space、多显示器、全屏应用和焦点恢复。
- 签名、公证、Keychain 和 `osx-arm64`/`osx-x64` 包。

## Linux 验收

- X11 selection 所有权变化和应用退出时内容持久化。
- Wayland 桌面、Portal 和扩展能力检测。
- `.deb`、`.rpm`、AppImage 的依赖、托盘和自启动差异。
- Secret Service/KWallet 不可用时禁止明文落盘，并给出明确配置状态。

## 发布标注

Release Notes 必须列出每个桌面环境的支持等级。不能只写“支持 Linux”而隐藏 Wayland 的剪贴板或自动粘贴限制。
