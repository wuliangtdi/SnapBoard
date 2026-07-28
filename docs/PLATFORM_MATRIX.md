# SnapBoard 平台支持矩阵

> 等级：完整、受限、计划中、不支持。只有完成对应系统实机验收后才能标记“完整”。

| 平台 | 目标版本/桌面 | 剪贴板监听 | 全局快捷键 | 自动粘贴 | 凭据存储 | 当前状态 |
| --- | --- | --- | --- | --- | --- | --- |
| Windows | Windows 11 x64 | 受限：原生适配器、delayed rendering、持久历史、检索、事件时来源快照及 PNG 已自动验证，外部应用矩阵未完成 | 受限：自定义按键录入、原生注册、冲突回滚和配置已自动验证，物理按键待验收 | 受限：Notepad/WinUI 已实机通过，管理员目标待验收 | Credential Manager 原生服务已验证 | Phase 1.4 自动验证完成，实机矩阵收口中 |
| macOS | macOS 26.2 arm64 已测；x64 待测 | 受限：原生适配器、APFS 持久历史、100,000 条检索与 10,000 次功能/AOT 资源压力已验证；完整桌面 UI 后台预算未满足且 8 小时未执行 | 受限：自定义录入、原生注册、冲突回滚、持久化和物理组合键已验证 | 受限：TextEdit 允许/拒绝状态已测，需辅助功能权限 | Keychain 原生服务已验证 | 共享历史通过；正式发布与环境矩阵进行中 |
| Ubuntu | GNOME X11 | 计划完整 | 计划完整 | 计划完整 | Secret Service | 三期 |
| Ubuntu/Fedora | GNOME Wayland | 配套扩展或受限 | 桌面接口决定 | 通常受限 | Secret Service | 三期高风险 |
| Fedora/KDE | Plasma Wayland | 待验证 | 待验证 | 可能受限 | KWallet/Secret Service | 三期 |
| Debian/Mint | X11 主流桌面 | 计划完整 | 计划完整 | 计划完整 | Secret Service | 三期 |

## Windows 11 验收

- [x] `AddClipboardFormatListener` 消息生命周期、取消和退出清理。
- [x] 剪贴板占用和延迟渲染重试：有限退避、取消和真实 delayed-rendering owner 已验证。
- [x] SQLite v5 迁移、WAL/外键/busy timeout、单写队列、损坏备份恢复、重启一致性和参数化显式投影。
- [x] 外部内容寻址 Blob、缩略图、引用计数、失败回滚以及延迟后台精确孤儿清理。
- [x] FTS5 中文/英文/代码检索、稳定分页、筛选、取消和 100,000 条生成数据性能验证。
- [x] 事件时 owner/foreground PID 快照、来源 EXE/AUMID/Package Family 持久化，以及 AppsFolder/传统 Shell 名称、真实图标和 GDI 资源释放自动验证。
- [x] Windows Credential Manager 密钥服务的新增、读取、覆盖、删除、不存在和拒绝状态。
- [~] 前台窗口恢复、任意支持键录入和真实全局快捷键冲突已验证；物理按键、多显示器与 DPI 实机待验收。
- [~] 普通权限应用与管理员应用之间的 UIPI 降级逻辑已实现，管理员目标实机待验收。
- [~] 单实例、第二实例激活、后台启动、按需窗口和退出清理已实测；托盘菜单点击与真实开机启动待验收。
- [~] `win-x64` Native AOT 本机 0 警告并实际启动；三次历史可见/窗口关闭指标和 19 分钟托盘样本已记录，最新隔离平台 10,000 次探针增长 7.38 MiB；完整桌面压力复测、GitHub Runner 与 8 小时增长待验证。

外部应用交互状态：Windows 11 打包版 Notepad 已通过文本复制；该进程明确加载 Microsoft.UI.Xaml，并通过纯文本写回、前台恢复和自动粘贴。Explorer 和浏览器仅准备了隔离生成数据，但本轮自动化焦点状态不可靠，没有形成可计系统剪贴板结果；管理员窗口、Office 和远程桌面未执行。详见 `docs/WINDOWS_CLIPBOARD_VALIDATION.md`。

当前已安装 Codex 与截图工具的真实 AUMID、Package Family、AppsFolder 本地化名称/图标像素及资源释放测试通过，注册 `PNG` 格式往返也通过。新构建上的 Codex 实际复制和截图工具实际截图尚未手动复核；当前桌面会话的 D3D11 捕获返回 `0x887A0005` 且 GDI 窗口帧为黑色，因此未把最终视觉效果标记为实机截图通过。

## macOS 验收

- [x] `NSPasteboard.changeCount` 生命周期、去重、取消、有界队列、反馈抑制和 100/500 ms 退避；AOT 监听探针平均 CPU 0.001%，`DroppedEvents=0`。
- [x] Text、HTML、RTF、PNG、TIFF、文件 URL、UTI 清单、完整写回和纯文本写回。
- [x] APFS 上 SQLite v1-v5/重复迁移/损坏恢复/重启一致性、CAS Blob、PNG/TIFF 原图与缩略图、后台精确孤儿清理，以及中文/英文/代码分页筛选取消和 100,000 条检索。
- [x] 来源无法可靠识别时，PID/名称/路径/AUMID/Package Family 保持 NULL，归属保持 Unknown；macOS 不注册 AppsFolder/Windows 来源图标解析器，UI 使用通用图标。
- [x] TextEdit 目标捕获、切换到 Finder 后恢复目标并发送 Command+V。
- [~] 辅助功能允许与独立应用身份拒绝已实测；设置页状态、受限模式和仅由用户触发的系统设置入口已实现，撤销后同一稳定身份重试和重新授予待实测。
- [~] TextEdit、Finder、Safari、Chrome、Preview PNG/同像素 TIFF 去重和 `pbcopy` 已确认进入持久历史；可见 Terminal UI 被安全策略阻止，Office 未安装，远程客户端未启动。
- [x] 每用户单实例、带确认的第二实例激活、关闭窗口后台常驻、三类窗口重建、Template 状态菜单、暂停/恢复和明确退出。
- [x] 默认 `Command+Shift+V`、自定义 `Option+Control+A`、重启后持久化、冲突失败回滚和恢复默认。
- [~] ServiceManagement 登录启动与 App Bundle 能力检测已实现，当前状态为未启用；真实启用/禁用及重新登录待用户确认后验收。
- [x] Keychain 临时密钥新增、读取和删除通过原生验证，平台抽象可供后续同步复用。
- [ ] 睡眠唤醒、多 Space、多显示器、Retina 和全屏应用；当前主机仅一台 1920 x 1080 非 Retina 显示器，未从该环境外推。
- [~] `osx-arm64` Native AOT、DMG/PKG 和实际启动通过；`osx-x64` 主程序与 helper 已交叉发布并在 Rosetta 下完成隔离存储启动和退出，匹配 Intel Runner 仍待验证。AOT 平台探针 10,000 次事件 Physical 增长 5.09 MiB、FD 不变，100,000 次计量阶段增长 0.45 MiB。完整桌面首次开窗后关窗约 94-96 MiB，未达到 80 MiB 目标且历史三轮样本为 100.05/100.19/100.19 MiB。Bundle 仅 ad-hoc、PKG 未签名且 `spctl` 拒绝；Developer ID、公证、staple、安装升级/卸载、远程 Runner 和 8 小时长稳待验证。

详细证据、性能样本和未验证项见 `docs/MACOS_CLIPBOARD_VALIDATION.md`。

## Linux 验收

- X11 selection 所有权变化和应用退出时内容持久化。
- Wayland 桌面、Portal 和扩展能力检测。
- `.deb`、`.rpm`、AppImage 的依赖、托盘和自启动差异。
- Secret Service/KWallet 不可用时禁止明文落盘，并给出明确配置状态。

## 发布标注

Release Notes 必须列出每个桌面环境的支持等级。不能只写“支持 Linux”而隐藏 Wayland 的剪贴板或自动粘贴限制。
