# SnapBoard 执行进度

> 最后更新：2026-07-31
> 当前阶段：来源应用图标跨设备同步阶段 A/B 已全部完成；Windows 与 macOS 新记录生成同一规范快照，并复用 SQLite v9、加密 Blob 和当前 v1 协议双向同步。共享 WebDAV 服务商迁移、跨平台更新与安装编排继续保持既有状态
> 本次目标状态：macOS TextEdit/Finder 原生采集、AppKit 主线程与缓存、Windows -> macOS/macOS -> Windows 像素往返、arm64/x64 Release 测试与 Native AOT 已验证；整个来源应用图标跨平台功能标记完成
> 总体状态：进行中
> 规则：只有代码、自动测试和目标平台验证同时满足时，功能才标记完成。

## 1. 总览

| 阶段 | 状态 | 当前结论 |
| --- | --- | --- |
| Phase 0 规划与决策 | 已完成 | 名称、MIT、三期平台、WebDAV 和同步范围已确认 |
| Phase 1.0 工程骨架 | 进行中 | 本机 Release 构建、测试和 macOS/Windows Native AOT 已通过；Ubuntu Build/Test、`linux-x64` AOT 和 Release Linux 产品包暂时注释，待补齐 Skia Linux 原生依赖后恢复 |
| Phase 1.1 AOT/内存基线 | 进行中 | 最终历史构建三轮可见峰值 PWS 155.74/155.33/138.97 MiB，关闭窗口后为 103.32/110.13/94.82 MiB；Private Bytes 为 136.59/135.54/127.82 MiB，内存门槛未完成 |
| Phase 1.2 UI 生命周期 | 进行中 | 单实例、后台启动、主/快速/设置窗口、自定义原生热键、暂停和退出已实现，用户可见应用名统一为“闪剪”；托盘点击、Windows 任务管理器实机显示、物理热键、多显示器/DPI、真实开机启动与 8 小时长稳待验收 |
| Phase 1.3 Windows 剪贴板 | 进行中 | delayed rendering、Notepad/WinUI、事件时来源身份、规范来源图标快照、注册 PNG 及 10,000 次功能压力通过；Codex/截图工具手动复核、完整桌面资源与外部应用矩阵未完成 |
| Phase 1.4 本地历史与检索 | 已完成 | SQLite v9、单写队列、恢复、CAS Blob、来源图标快照、PNG/TIFF 缩略图、FTS5、策略链及 100,000 条检索已验证；本次不回填或兼容旧数据 |
| Phase 1.5 快速粘贴体验 | 进行中 | 主/快速窗口优先按需显示持久化来源图标快照，本机解析与通用图标依次降级；数字快捷选择、标签编辑、搜索高亮与完整富预览待完成 |
| Phase 1.6-1.8 | 进行中 | 当前 v1 加密同步已加入来源图标 Blob 且远端仍为 `SnapBoard/v1`；SQLite v9、历史策略、真实 UI、服务商迁移及 macOS 恢复触发已落地，正式跨系统 App 与长期发布矩阵待完成 |
| Phase 2 macOS | 进行中 | 现有共享层可读取 Windows 同步来的来源图标快照；macOS 本机来源名称/原生图标解析已存在，但尚未注册为持久化快照提供器，阶段 B 实现与双向实机/AOT 验证待完成 |
| Phase 3 Linux | 未开始 | X11 与 Wayland 分级支持 |

## 2. Phase 1.0 检查表

### 已完成

- [x] 安装并锁定 .NET SDK 10.0.302。
- [x] 初始化 Git `main` 分支和 `SnapBoard.slnx`。
- [x] 添加 MIT 许可证、`.gitignore`、`.editorconfig` 和 NuGet 源配置。
- [x] 使用中央包管理锁定 Avalonia、CommunityToolkit、SQLite 和测试依赖。
- [x] 创建 10 个源码项目和 10 个测试/基准项目。
- [x] 建立 Domain、Application、Infrastructure、Platform、Sync、Desktop 依赖方向。
- [x] 添加架构测试，阻止 Domain/Application 反向依赖实现层。
- [x] 添加显式 DI 组合根，禁止程序集扫描。
- [x] 添加 Avalonia 快速窗口视觉壳和编译绑定。
- [x] 按选定的第 2 版方案完成双栏命令中心、品牌图标、筛选、搜索、预览和状态栏。
- [x] 使用 Material.Icons.Avalonia 3.0.2 统一操作图标，并完成轻量代码预览着色。
- [x] 接入 Avalonia.Headless.XUnit 12.1.0 与 Skia，覆盖真实 XAML 渲染和核心窗口交互。
- [x] 添加 Microsoft.Data.Sqlite 连接工厂。
- [x] 添加 WebDAV HTTPS 配置边界和首版同步类型。
- [x] 添加 System.Text.Json 源生成上下文。
- [x] 添加 GitHub Actions 三平台 Build/Test/AOT 和标签发布骨架。
- [x] 创建计划、进度、架构、安全、性能、测试、平台和同步文档。

### 待完成

- [ ] 在 GitHub 创建远程仓库并推送，验证所有 Actions Job。
- [ ] 在 Windows Runner 完成 `win-x64` Native AOT 发布。
- [x] 在 Windows 11 实机启动 `win-x64` AOT 壳并记录冷启动、Private Working Set、Private Bytes 和句柄。
- [ ] 完成 Ursa 与纯 Avalonia 的 A/B 基准，决定是否引入运行时依赖。
- [~] 已从现有透明品牌图生成 Windows 多尺寸 `.ico` 和 macOS 标准 `.icns`，并接入 EXE/App Bundle、标题栏、Dock/Finder 与托盘/Template 状态图标；用户可见名称及双平台产品元数据已统一为“闪剪”，Windows 任务管理器仍待实机复核，Developer ID 正式签名身份待配置。
- [ ] 优化可见窗口内存，完成纯 Avalonia、Material Icons、Ursa 和最终壳的可重复 A/B 测量。

## 3. 已验证基线

| 检查 | 结果 | 说明 |
| --- | --- | --- |
| NuGet restore | 通过 | 已启用锁文件和漏洞审计告警即错误 |
| Release build | 通过 | 本机 0 警告、0 错误 |
| 全量自动测试 | 通过 | 2026-07-31 Windows 当前代码共 495 项：473 项通过、22 项按 macOS 原生环境或外部 WebDAV 条件跳过、0 项失败；来源图标覆盖采集、SQLite、同步、Windows 原生和 Headless UI |
| macOS 存储与同步测试 | 通过 | macOS 原生项目 49/49 且无跳过；覆盖 APFS/POSIX mode/真实扩展 ACL/链接/卷/进程身份、真实大小写敏感 APFSX 路径关系、真实 Keychain 完整工作流、系统恢复原生事件源、legacy 启动、设置 modal/迁移事务和有状态双设备离线收敛 |
| `osx-arm64` Native AOT | 本机通过 | 64 位文件系统 ABI 修复后的主程序 34,573,888 字节，迁移器 8,326,528 字节，均为 arm64 Mach-O；无 CoreCLR/helper 托管配置，helper 无参数退出码 4，挂载 DMG 后隔离 bootstrap 启动及 `--exit` 通过。0 个 trim/AOT 分析告警；2 个 clang module-cache 调试信息告警来自 .NET 10.0.10 官方 Apple NativeAOT 静态库，已记录且未 suppression。正式签名/公证未完成 |
| `osx-x64` Native AOT | CI 通过 | 本机 Rosetta 预检和 GitHub `macos-15-intel` AOT 均通过；主程序与迁移器为 x86_64 Mach-O，无 CoreCLR/helper 托管配置，helper 无参数退出码 4。Intel 实体机交互验收仍待执行 |
| `win-x64` Native AOT | 本机通过 | 来源图标阶段 A 产物主程序 40,489,472 字节、独立迁移器 4,514,304 字节；0 个 trim/AOT 警告，迁移器无 `.dll`、`.deps.json` 或 `.runtimeconfig.json`；随机隔离数据根创建 v9 数据库、主窗口句柄非零并通过 `--exit` 以 0 退出 |
| `linux-x64` Native AOT | 暂停 | CI 矩阵暂时注释；恢复 `SkiaSharp.NativeAssets.Linux` 锁定依赖后再由 Ubuntu Runner 验证 |
| Windows 窗口/后台内存 | 未达标 | 最终 AOT 三次关闭窗口后 PWS 为 103.32/110.13/94.82 MiB，Private Bytes 为 136.59/135.54/127.82 MiB；19 分钟样本最终 PWS 88.29 MiB、Private Bytes 120.99 MiB，不能声称整体内存门槛通过 |
| macOS 窗口/后台内存 | 未达标 | AOT 平台探针 10,000 次增长 5.09 MiB、100,000 次增长 0.45 MiB，事件路径通过；完整桌面纯后台 41.4 MiB，首次开窗后关窗约 94-96 MiB，仍高于 80 MB 目标且有超过 100 MB 的历史波动；8 小时未执行 |

## 4. 重要发现

### 4.1 ORM 准入结果

`SqlSugarCoreNoDrive 5.1.4.216` 在官方整程序集 `rd.xml` 下因缺少可选数据库驱动而无法 Native AOT；`SqlSugarCoreNoDrive.Aot 5.1.4.186` 会带入多个无关驱动，并产生裁剪与 AOT 分析错误。项目没有压制这些错误，改用 Microsoft.Data.Sqlite 和显式 SQL。

### 4.2 SQLite 安全覆盖

Microsoft.Data.Sqlite 10.0.10 传递请求 `SQLitePCLRaw.bundle_e_sqlite3 2.1.11`，NuGet 审计将其关联到 CVE-2025-6965。仓库显式提升到 2.1.12，并通过 `SELECT sqlite_version()` 自动测试确保运行时 SQLite 不低于 3.50.2。

### 4.3 JSON 约束

同步协议只使用 System.Text.Json。所有协议 DTO 必须加入 `SyncJsonContext`，测试执行源生成上下文的序列化往返。Newtonsoft.Json 不得进入正式依赖图。

### 4.4 UI 与内存基线

第 2 版命令中心已按 1487 x 1058 参考画布完成视觉对照，最终报告见根目录 `design-qa.md`。Avalonia Headless 使用真实 Skia 渲染器产出稳定截图，不依赖宿主桌面和显示器缩放。

2026-07-27 完成 macOS 桌面生命周期与单实例所有权锁后重新发布最终 arm64 AOT，三次可见窗口峰值 Physical Footprint 为 205.63/206.16/205.44 MiB；关闭全部窗口并保持菜单栏进程 3 秒后为 107.81/107.53/107.64 MiB，Physical Footprint 回落 97.81/98.62/97.80 MiB。该短样本证明窗口可释放并后台常驻，但不是 10 分钟或 8 小时长稳，且仍高于 100 MB 目标。2026-07-26 的 152.5 MB 和 Phase 2.1 的约 195 MiB 数据仅保留为历史样本，不能替代本轮复测。

### 4.5 Windows 剪贴板与 AOT 基线

Windows 原生适配器使用独立 STA 线程、message-only window、`AddClipboardFormatListener`、`GetClipboardSequenceNumber`、有界 Channel 和有限退避。`WM_CLIPBOARDUPDATE` 到达时只快照 owner/foreground PID，读取相同序列时再解析 EXE、AUMID、Package Family 和归属依据；AppsFolder 为 Microsoft Store/MSIX 应用提供本地化名称和图标。真实 Windows 剪贴板集成测试覆盖 Unicode/ANSI Text、HTML、RTF、DIB/DIBV5、注册 PNG、File List、格式清单、自定义来源标记和反馈抑制；自动粘贴覆盖 UIPI 结构化降级、目标 HWND/PID 与发送前前台窗口二次校验，并校验 x64 `INPUT` ABI 为 40 字节。

真实 delayed-rendering owner 已通过 `WM_RENDERFORMAT` 完成按需渲染。Windows 11 打包版 Notepad 的交互式复制已由探针捕获，来源识别为 `Notepad`；同一应用已确认加载 `Microsoft.UI.Xaml.dll`，并通过指定 HWND 的纯文本写回、目标恢复和 `SendInput` 自动粘贴。最新隔离平台压力观察到 10,000/10,000 个自写事件，反馈和 Channel 丢弃均为 0，Private Bytes 增长 7.38 MiB。完整桌面曾因每个事件排队一次历史刷新出现严重内存放大，当前已合并为静默期单次刷新并通过 10,000 次 Headless 回归，但修复后的完整 AOT 压力尚未重跑。Explorer 真实复制来源和黄色文件夹 Shell 图标已在最终 AOT 主窗口复核；Codex/截图工具真实包身份和图标已自动验证，实际复制/截图仍待手动复核；浏览器、管理员窗口、Office 和远程桌面未完成。完整记录见 `docs/WINDOWS_CLIPBOARD_VALIDATION.md`。

### 4.6 macOS 剪贴板与权限基线

macOS 平台层使用 `NSPasteboard.generalPasteboard.changeCount`、可取消的 100 ms 活跃/500 ms 空闲退避轮询、有界 Channel 和实例 nonce 来源标记。原生读取支持纯文本、HTML、RTF、PNG、TIFF、文件 URL 与 UTI 清单；Finder 的 `file:///.file/id=...` 引用优先通过同时提供的 `NSFilenamesPboardType` 还原真实路径。写回、纯文本写回和自写事件抑制均由原生集成测试覆盖。

辅助功能允许状态下，TextEdit 自动粘贴以及“捕获 TextEdit -> 切换 Finder -> 恢复 TextEdit -> Command+V”均实机通过。独立 ad-hoc 应用身份的拒绝状态返回 `AccessibilityPermissionDenied`，剪贴板仍写入成功并显示“已复制，请手动粘贴”，TextEdit 未收到注入。来源应用识别固定按 best effort 处理，本轮样本均诚实降级为 `Unknown`。完整记录见 `docs/MACOS_CLIPBOARD_VALIDATION.md`。

### 4.7 Windows 自定义快捷键与设置页

设置页不再使用四项预设下拉框，改为点击后直接按组合键录入。Avalonia UI 只提交平台无关的修饰键和按键名称，Windows 平台层显式映射为原生虚拟键并补充 `MOD_NOREPEAT`；字母、数字、数字键盘、F1-F24、导航、浏览器、媒体和常用 OEM 标点已由确定性测试覆盖。无修饰键和不支持的主键会保留录入状态并给出提示，原生注册冲突会回滚并恢复界面显示。

设置窗口已复用主窗口的品牌图、白色表面、浅灰背景、蓝色主命令、图标和 6 px 圆角体系。Windows 主窗口通过 owned modal 打开设置，设置期间主窗口不接收输入；存储迁移确认和最终校验错误继续作为设置窗口的嵌套模态窗口，不使用跨应用全局置顶。Release XAML 构建、Headless/Skia 真实帧、窗口创建/重建、模态返回、自定义快捷键录入与应用均通过；JIT 和 Native AOT 设置窗口已实际启动。Windows 桌面截图组件因本机 D3D11 设备暂停错误 `0x887A0005` 未取得桌面合成截图，但 Headless PNG 已完成视觉复核；物理按键仍保留为交互验收项。

### 4.8 macOS 桌面生命周期、权限与发布

macOS 平台层新增每用户 `flock` 所有权锁与 Unix socket 命令通道、带确认的第二实例命令、Carbon 全局快捷键、ServiceManagement 登录启动、AppKit Template 状态项、Accessibility 权限服务、Security.framework Keychain 服务和窗口原生定位。Desktop 生命周期协调器只依赖平台抽象，主窗口、快速窗口和设置窗口按需创建、关闭释放并可重复打开；最后窗口关闭不退出应用，状态菜单可暂停/恢复记录并明确退出。AppKit 调用统一切入 Avalonia UI 线程，状态项及窗口原生对象使用成对 retain/release。

系统恢复监听通过 `IDesktopSystemEventService` 隔离平台代码：`NSWorkspaceDidWakeNotification` 报告系统唤醒，SystemConfiguration dynamic store 监听 `State:/Network/Global/.*`。两类信号只调用线程安全且会自动合并的 `SyncService.RequestSync()`，初始化失败时仍由周期检查兜底。原生 observer、dispatch queue、`UnmanagedCallersOnly` 回调、重复启动、释放后解绑和 Desktop 同步请求均有自动测试；真实合盖/唤醒及网络接口断开恢复尚未执行，不能由模拟通知外推。

实机已验证关闭全部窗口后进程和状态项继续存在、第二实例复用原进程并打开主窗口、三类窗口重复关闭/重建、菜单打开主/快速/设置窗口、暂停/恢复记录和菜单退出。默认 `Command+Shift+V` 及自定义 `Option+Control+A` 均由系统真实按键事件打开快速窗口；自定义配置重启后仍注册，最后恢复默认。快速窗口打开前保存目标应用，既有 TextEdit 恢复与自动粘贴结果继续有效。设置页仅显示 Command/Option/Control/Shift、登录启动、辅助功能和 Bundle 能力，不显示 Windows 术语。

稳定 Bundle ID 为 `com.wuliangtdi.snapboard`，标准 `.icns` 和浅色/深色 Template 状态图标已接入。最终 `osx-arm64` DMG 校验通过，挂载后的 App Bundle 实际后台启动并显示状态项，PKG 可展开；应用使用 Hardened Runtime ad-hoc 签名，PKG 未签名。当前钥匙串没有 Developer ID Application/Installer 身份，也未配置公证凭据，因此正式签名、Gatekeeper 接受和公证均未执行，不能标记完成。

macOS 现已在 Avalonia 和 SQLite 初始化前解析固定 bootstrap 与活动数据根，保留 `~/Library/Application Support/SnapBoard` legacy 数据，locator 损坏恢复与缺失自定义根均明确失败。平台存储服务使用原生文件身份、实际卷大小写语义、卷 UUID、POSIX mode 与扩展 ACL 检查 APFS 目录；Darwin `acl_get_entry` 的成功返回值和 `EINVAL` 遍历终止语义已由真实 ACL 验证，带其他主体 allow 条目的目录不会误报为私有。平台保守拒绝网络/移动/只读/未知卷和 iCloud/File Provider 根；进程启动、等待与停止使用 PID、启动时间、可执行路径和 UID 的完整身份。共享组合根在 macOS 注册真实同步、历史设置和存储迁移服务，设置窗口的存储、历史、同步与辅助功能区域全部可见并遵循 owner modal 及失败恢复顺序。

Darwin 的 x86_64 无后缀 `lstat`/`statfs` 使用旧 inode/statfs ABI，而托管结构对应现代 64 位布局；这会让 x64 AOT 把真实目录误判为非目录。平台互操作现显式调用两架构都导出的 `lstat64`/`statfs64`，arm64 原生测试与 x64 Rosetta 冷启动均通过。CI Build/Test 矩阵同时加入 `macos-15-intel`，使该存储原生测试在 Intel Runner 上执行，而不是只校验 x64 Mach-O。

Keychain 原生测试已覆盖 32 字节空间主密钥和包含 endpoint/root/user/password/certificate pin/loopback 的凭据包新增、读取、覆盖、删除、不存在与拒绝状态。已有空间重新配置改为先临时恢复并恒定时间比较主密钥，再用候选凭据验证远端，成功后才覆盖安全存储；错误恢复码、有效但不匹配的恢复材料及证书失败均逐字节保持既有主密钥、凭据和 SQLite 配置，后续同步仍可用。

### 4.9 Windows 安全存储迁移与加密同步

Windows 启动阶段现由 bootstrap 定位器解析活动数据根，SQLite 与 Blob 只使用当前解析结果。迁移由独立 Native AOT 迁移器执行，主程序先暂停并排空同步与剪贴板持久化，再建立数据库屏障；清单、卷身份、重解析点、空间、哈希、Schema、`quick_check`、启动确认和回滚均有边界检查。目标目录分别在选择时、用户确认后的 `PrepareMigrationAsync`、主程序退出后的迁移器复制前检查为空；最终准备校验失败时不生成迁移状态、不启动迁移器、不关闭主程序，并显示模态错误窗口，迁移器侧竞态兜底会保留后来出现的文件、回滚并重启原应用。Desktop 发布通过 `$(MSBuildProjectDirectory)` 与 `$(IntermediateOutputPath)` 计算迁移器中间目录，因此没有写死本机盘符或用户名。

SQLite Schema v7 新增同步空间、Outbox、Inbox、逐设备 Checkpoint、Blob staging 和逐设置键逻辑版本。历史新增、置顶、删除及 `history.capture`/`history.retention`/`sync.pollInterval` 设置与 Outbox 在同一写事务提交；`SyncService` 使用 single-flight、有界批次、动态轮询和暂停排空，远端只写加密元数据、不可变事件及 keyed Blob。Windows Credential Manager 分离保存内容主密钥与版本化、长度受限的完整 WebDAV 连接配置；SQLite 表结构不包含 URL、用户名或密码字段，恢复材料落盘前加密。设置页接入创建/加入、连接验证、证书指纹、恢复材料、记录类型、默认关闭的自动清理、后台检查频率和真实同步状态，密码及恢复码提交后清空。

WebDAV 客户端已覆盖 HTTPS/显式 loopback 例外、证书固定、同源同根重定向、条件写入、ETag、取消、有限重试、响应上限和严格 PROPFIND。精确 SHA-256 指纹允许自签名链错误，但证书缺失或主机名不匹配仍拒绝；DTD、外部实体、跨源 href、编码分隔符和路径逃逸也会被拒绝。自动化假远端已验证双设备创建/加入、8 MiB 加密 Blob、重复收发、墓碑、序号缺口、缺失 Checkpoint 安全重建、迁移暂停排空及服务商迁移故障恢复；Apache 2.4.62 标准 WebDAV 已完成真实双设备迁移。Nextcloud、Synology、设备撤销/密钥轮换、远端回收及正式跨系统 App 矩阵仍待验证。

### 4.10 WebDAV 服务商迁移

共享 Application 状态机实现 Draft 到 Completed 及全局 RollingBack/RolledBack，普通同步在上传前扫描旧端加密 intent；离线设备发现计划后先持久化阻断状态，再要求本机目标凭据。旧端一次性条件创建的 `terminal.enc` 在 `Completed` 与 `Rollback` 并发时裁决唯一赢家，目标端只镜像同一决定，陈旧参与设备不得生成相反终态。协调者只复制 metadata、不可变事件和 keyed Blob 的原始密文字节，同时在本机短暂解密副本验证认证标签、路径 descriptor、逐设备连续序号、ready 水位、Checkpoint 与 Blob 内容地址；目标端逐对象比较规范身份、长度和 SHA-256。相同对象幂等跳过，同路径不同密文阻断，旧端默认保留。

SQLite v8 只保存计划 ID、epoch、远端指纹、阶段、水位和进度，不保存 endpoint、root、用户名、密码或证书。每台设备在 Credential Manager/Keychain 中使用独立 source/target 暂存槽；提交前校验 active 仍等于 source，写入 target 后读回验证，失败可恢复 source。目标密码提交后从 ViewModel 清空，不进入迁移 DTO、SQLite 或远端控制标记。缺失 Checkpoint 行只从序号自 1 连续的 Inbox 原子重建最后序号与事件 ID，存在缺口时明确失败；迁移 ready 水位仍对照远端最大序号和事件 ID。共享设置页显示当前服务、设备就绪/离线/提交状态、对象/字节进度和可恢复错误，继续及回滚均使用 owner modal。

## 5. 下一执行顺序

1. 从包含本提交的最新 main 在 macOS 环境实施 `docs/SOURCE_APPLICATION_ICON_SYNC_REQUIREMENTS.md` 阶段 B，只接入本机快照生成，不重做共享协议或兼容旧数据。
2. 使用正式 Windows 与 macOS App 执行来源图标双向同步，以及既有服务商迁移的离线恢复、部分提交恢复和回滚矩阵；共享测试不能替代双机门槛。
3. 使用 Nextcloud 与 Synology 执行认证、路径、ETag、配额、限流、重试和损坏响应矩阵；Apache 标准 WebDAV 已通过。
4. 完成设备撤销、密钥轮换、远端 Checkpoint/Blob 安全回收，并在真实合盖/唤醒及网络接口断开恢复场景验收已实现的立即同步触发。
5. 在新构建上手动复核 Chrome、Edge、微信、Codex、截图工具和 Store 应用的真实复制来源图标，并用隔离数据目录重跑完整 AOT 桌面压力与长稳。
6. 在对应硬件补齐 Windows ARM64、macOS Intel 和 Linux 验证；不得从当前 Windows x64 结果外推。

## 6. 2026-07-26 执行记录：第 2 版命令中心

```text
日期：2026-07-26
阶段/任务：Phase 1.2 第一轮视觉基线与 Headless 窗口测试
状态：[x] 视觉与交互基线完成；[~] 内存优化继续
完成内容：
  - 落地双栏剪贴板命令中心、品牌区、搜索、类型筛选、排序、预览和状态栏。
  - 加入 10 条脱敏模拟记录，打通搜索、筛选、选择、删除、置顶、排序、同步和紧凑模式状态。
  - 使用 Material Icons 统一图标，并实现不依赖编辑器引擎的只读代码着色预览。
  - 增加 Avalonia 12 Headless/Skia 真实窗口测试和 1487 x 1058 稳定截图。
  - 完成参考稿与实现截图同输入视觉对照，design-qa 最终通过。
变更文件：
  - src/SnapBoard.Desktop/App.axaml
  - src/SnapBoard.Desktop/Views/MainWindow.axaml(.cs)
  - src/SnapBoard.Desktop/ViewModels/*
  - src/SnapBoard.Desktop/Controls/SyntaxHighlightedCodeView.*
  - tests/SnapBoard.Desktop.HeadlessTests/*
  - docs/design/*
验证命令：
  - dotnet build SnapBoard.slnx -c Release --no-restore
  - dotnet test SnapBoard.slnx -c Release --no-build --no-restore
  - dotnet format SnapBoard.slnx --verify-no-changes --no-restore
  - dotnet list SnapBoard.slnx package --vulnerable --include-transitive
  - dotnet publish src/SnapBoard.Desktop/SnapBoard.Desktop.csproj -c Release -r osx-arm64 --self-contained true -p:PublishAot=true --no-restore
性能数据：
  - AOT 可执行文件约 23 MB，发布目录约 91 MB。
  - AOT 可见窗口 Physical Footprint 152.5 MB，峰值 195.2 MB，未达到预算。
发现的问题：
  - 当前窗口可见状态的内存高于失败线，不能进入 Phase 1.1 完成状态。
  - 托盘、窗口关闭释放和 Windows 实机指标尚未实现或采样。
下一步：
  - 做 UI 依赖与图标库 A/B，建立 Windows 可重复内存脚本并实现窗口关闭后的常驻测量。
```

## 7. 2026-07-26 执行记录：Windows 剪贴板一期

```text
日期：2026-07-26
阶段/任务：Phase 1.3 Windows 剪贴板适配器与 Windows 实机基线
状态：[~] 核心适配器完成；兼容性矩阵与 10,000 次长稳退出条件未完成
完成内容：
  - 新增 Windows 剪贴板监听、读取、写回、纯文本写回和自动粘贴平台端口。
  - 使用独立 STA 消息线程、隐藏消息窗口和 AddClipboardFormatListener。
  - 增加序列去重、有界队列、有限退避、来源识别、来源标记和反馈抑制。
  - 支持 Unicode/ANSI Text、HTML、RTF、DIB/DIBV5、CF_HDROP 和格式清单。
  - 自动粘贴在高完整性/未知权限/SendInput 失败时返回“已复制，请手动粘贴”。
  - 新增 Windows 探针和可重复性能采样脚本。
验证结果：
  - .NET SDK 10.0.302；locked restore 通过。
  - Release build 0 警告、0 错误；全量 38 项测试通过。
  - dotnet format --verify-no-changes 通过；NuGet 直接/传递漏洞为 0。
  - win-x64 Native AOT 生成原生 EXE，0 个 AOT/裁剪警告。
  - Windows 11 打包版记事本交互复制通过；其他外部应用场景未完成。
性能数据：
  - 3 次进程冷启动到主窗口：489.82/279.49/280.25 ms。
  - 30 秒可见窗口峰值 PWS：184.38/207.12/218.20 MiB。
  - 峰值 Private Bytes：214.54/239.21/250.13 MiB；句柄：1276/1275/1272。
  - 当前没有托盘和窗口卸载，以上数据不能表述为托盘常驻内存。
```

## 8. 2026-07-27 执行记录：macOS 原生剪贴板一期

```text
日期：2026-07-27
阶段/任务：Phase 2.1 macOS 原生剪贴板适配器、写回与自动粘贴权限闭环
状态：[~] 当时核心平台能力完成；当时菜单栏、设置引导、Keychain、Intel 与发布链路未完成，后续状态见第 10 节
完成内容：
  - 新增显式 AppKit/Objective-C Runtime/CoreGraphics/Accessibility 互操作，不引入 Xamarin.Mac、MAUI、Mac Catalyst 或运行时程序集扫描。
  - 使用 NSPasteboard changeCount、有界 Channel、可取消轮询和 100/500 ms 退避；轮询 tick 不读取正文、不访问 SQLite/网络。
  - 支持 Text、HTML、RTF、PNG、TIFF、文件 URL、UTI 清单、完整写回、纯文本写回和实例 nonce 反馈抑制。
  - 保存并恢复目标 NSRunningApplication，在辅助功能允许时发送 Command+V；拒绝或失败时结构化降级为“已复制，请手动粘贴”。
  - Desktop 组合根按 OperatingSystem.IsMacOS() 显式注册四个共享端口，并保留 Windows 注册与测试。
验证结果：
  - .NET SDK 10.0.302；arm64；macOS 26.2 (25C56)；locked restore 通过。
  - Release build 0 警告、0 错误；全量 52 项中 47 项通过、5 项 Windows 原生测试按平台跳过。
  - macOS 测试 19/19；dotnet format --verify-no-changes 通过；NuGet 直接/传递漏洞为 0。
  - osx-arm64 Native AOT 生成 arm64 Mach-O，0 个 AOT/裁剪警告，实际启动三次并检测到主窗口。
  - TextEdit、Finder、Safari、Chrome、Preview、pbcopy CLI、允许/拒绝辅助功能状态均记录真实结果；可见 Terminal UI 复制未完成。
性能数据：
  - 三次启动到可见窗口：3974.77/1288.41/919.26 ms。
  - 峰值 Physical Footprint：194.78/194.74/194.94 MiB；峰值 RSS：162.11/162.55/162.08 MiB。
  - 10 秒窗口采样平均 CPU：0.014/0.015/0.017%；能耗增量：26.541/25.098/33.354 mJ。
  - AOT 监听探针 10 秒计量平均 CPU 0.001%、能耗增量 1.360 mJ、44 次 interrupt wakeups、DroppedEvents=0。
限制：
  - 没有菜单栏常驻和窗口卸载，不能声称常驻内存低于 100 MB；当前可见窗口样本明确超标。
  - 未验证 osx-x64、睡眠唤醒、多 Space、多显示器、全屏、Office、远程桌面、签名、公证或 Keychain。
```

## 9. 2026-07-27 执行记录：Windows 生命周期与剪贴板收口

```text
日期：2026-07-27
阶段/任务：Phase 1.2 Windows 桌面生命周期与 Phase 1.3 剪贴板收口
状态：[~] 已完成核心实现和可自动化验证；外部应用矩阵、管理员目标和长稳资源门槛未完成
完成内容：
  - 新增每用户单实例、CurrentUserOnly 命名管道激活和后台/快速/设置/退出命令。
  - 新增托盘生命周期、按需窗口创建与释放、暂停记录、原生全局热键、冲突回滚、恢复默认、HKCU 开机启动和 Per-Monitor V2 定位。
  - Windows EXE、标题栏、任务栏和托盘统一使用透明多尺寸品牌图标，主窗口设置入口改为与现有工具栏一致的轮廓齿轮。
  - 自动粘贴增加恢复目标端口和 SendInput 前 HWND/PID 二次校验；保持 macOS 四个平台端口构建通过。
  - Windows 探针新增真实 delayed-rendering owner、指定 HWND 纯文本粘贴和 10,000 次压力模式。
验证结果：
  - .NET SDK 10.0.302；locked restore、Release build、dotnet format --verify-no-changes 均通过。
  - 全量 79 项中 74 项通过、5 项仅限 macOS 原生环境的测试跳过；Windows 37/37，Desktop Headless 14/14。
  - NuGet 直接与传递依赖漏洞为 0。
  - win-x64 Native AOT 为 27,897,856 字节，0 个 AOT/裁剪警告，并实际创建带品牌图标的 `SnapBoard - 闪剪` 主窗口。
  - 第二实例激活、后台启动、快速窗口、设置窗口和明确退出在 Windows 11 同一进程实测通过。
  - delayed rendering 读取通过；Notepad/WinUI 纯文本写回、目标恢复和自动粘贴通过。
  - 三轮 10,000 次均观察到 10,000/10,000 个自写事件，反馈循环和 Channel 丢弃均为 0。
性能数据：
  - 启动到主窗口：606.58/459.96/445.34 ms；窗口卸载：35.90/48.84/35.12 ms。
  - 可见窗口峰值 PWS：84.47/85.12/109.50 MiB；Private Bytes：181.98/182.56/200.71 MiB。
  - 窗口关闭后最终 PWS：66.22/66.86/130.38 MiB；Private Bytes：130.67/140.51/180.64 MiB。
  - 窗口关闭后平均 CPU：0.000/0.000/0.000%；最终句柄：1264/1268/1289。
限制：
  - 10,000 次测试 Private Bytes 增长 15.56/8.43/15.12 MiB，未满足 8 MiB 预算。
  - 当前只有 30 个、至少相隔 1 秒的窗口关闭样本，不等于 10 分钟托盘或 8 小时长稳；第三轮 PWS 反向增长 20.88 MiB，Private Bytes 仍高于 100 MiB，不能声称常驻低于 100 MB。
  - 托盘菜单点击、物理热键、多显示器/DPI、真实开机启动、Explorer、浏览器、管理员窗口、Office 和远程桌面未完成。
```

## 10. 2026-07-27 执行记录：macOS 桌面生命周期与发布验证

```text
日期：2026-07-27
阶段/任务：Phase 2 macOS 桌面生命周期、自定义快捷键、权限、Keychain 与发布链路
状态：[~] 核心实现和 arm64 本机验证完成；正式签名/公证、Intel 与环境矩阵未完成
完成内容：
  - 实现单实例、第二实例激活、后台常驻、三类窗口按需创建/释放、状态菜单、暂停和明确退出。
  - 实现 Carbon 自定义快捷键直接录入、冲突回滚、默认恢复和持久化；UI 使用 macOS 修饰键名称。
  - 实现设置页辅助功能状态/受限模式、用户触发的授权入口、ServiceManagement 登录启动能力和 Keychain 密钥服务。
  - 从现有透明品牌图生成标准 ICNS，接入 App Bundle/Dock/Finder/窗口图标和 Template 状态图标。
  - 增加 arm64/x64 独立 CI/Release 路径、Hardened Runtime、Developer ID/公证门槛及 DMG/PKG 脚本。
验证结果：
  - .NET SDK 10.0.302；locked restore、Release build、format 校验、漏洞审计均通过。
  - 全量 103 项：96 项通过、7 项 Windows 原生测试按平台跳过、0 项失败；macOS 36/36、Desktop Headless 21/21。
  - osx-arm64 Native AOT 为 24,430,144 字节 arm64 Mach-O，0 个 AOT/裁剪警告；裸产物和 DMG 内 App Bundle 均实际启动。
  - 菜单栏、关闭窗口后台常驻、第二实例、自定义快捷键及重启持久化、暂停/恢复、三类窗口重开和明确退出实测通过。
  - 当前辅助功能权限显示已授权；独立 ad-hoc 拒绝降级沿用 Phase 2.1 实测。Keychain 临时密钥新增/读取/删除通过。
  - 10,000 次原生事件：Write/Read/Marker/Feedback/Dropped 均为 0 失败；RSS 61.36 -> 76.41 MiB，线程 18 -> 20，FD 50 -> 52。
性能数据：
  - 启动到主窗口：764.83/627.65/565.32 ms。
  - 可见峰值 Physical Footprint：205.63/206.16/205.44 MiB；RSS：170.50/170.25/170.36 MiB。
  - 关闭窗口 3 秒后 Physical Footprint：107.81/107.53/107.64 MiB；RSS：172.25/171.86/172.03 MiB。
  - 平均 CPU：0.023/0.026/0.023%；能耗：85.471/101.381/97.013 mJ；wakeups：406/415/419。
发布结果：
  - DMG/PKG 本机生成；DMG 校验及挂载启动通过；Bundle 使用 ad-hoc Hardened Runtime 签名，PKG 未签名，公证跳过。
限制：
  - 未实测登录启动开关/重新登录、权限撤销后重授予、睡眠唤醒、多 Space、多显示器、Retina、全屏、Office、远程桌面或可见 Terminal UI。
  - 主机只有一台 1920 x 1080 非 Retina 显示器；该轮后台 Physical Footprint 超过 100 MB。该轮 framework-dependent 探针的资源增长判定已由 2026-07-28 Native AOT Physical Footprint 复查纠正，平台事件路径实际满足严格预算。
  - 本机没有 Developer ID 身份/公证凭据，未执行正式签名、公证或 Gatekeeper 接受验证；osx-x64 未发布、未启动。
```

## 11. 2026-07-27 执行记录：Windows 本地历史与检索

```text
日期：2026-07-27
阶段/任务：Phase 1.4 本地历史、检索和策略；Phase 1.5 真实数据接入；Windows 密钥服务
状态：[x] 实现与自动验证完成；[~] 资源预算及外部应用/硬件矩阵未完成
完成内容：
  - 建立 SQLite Schema v1-v4 和可重复迁移；启用 WAL、外键、busy timeout、quick_check、损坏备份和诊断恢复。
  - 所有写事务进入有界单写 Channel；读查询使用短生命周期连接和显式投影，Application/UI 不暴露 SQLite 类型。
  - 保存内容类型、来源、时间、SHA-256、置顶、标签、使用次数、删除状态、格式和文件引用；重启一致性由集成测试覆盖。
  - 原图和超过 64 KiB 的载荷使用外部内容寻址 Blob；数据库只存相对路径/元数据/引用计数，缩略图为 320 x 180 PNG。
  - 临时文件落盘后原子移动，再提交数据库引用；回滚、共享引用、删除、清空、过期和精确孤儿清理由测试覆盖。
  - 孤儿目录扫描不阻塞启动：初始化后延迟 2 分钟后台运行，24 小时宽限，每批 32 个，删除前按完整相对路径复查数据库。
  - FTS5 覆盖中文、英文、代码、特殊字符、空/超长查询、取消、稳定分页以及类型/来源/时间/标签/置顶筛选。
  - 实现相邻去重、置顶、标签、软删除、清空、条数/时间/磁盘容量策略和应用/敏感格式/载荷大小责任链。
  - 正式 UI 接入 Application 查询用例，每页 50 条、VirtualizingStackPanel、搜索取消/代际保护及图片按需解码。
  - 历史摘要带出已持久化的来源 EXE 路径；Windows 在后台解析版本资源/本地化别名，以 Shell/GDI 提取并缓存真实应用图标，UI 只接收平台无关 BGRA 像素。
  - Windows Credential Manager 实现 IPlatformSecretStore，使用 LibraryImport，覆盖新增、读取、覆盖、删除、不存在、拒绝和无效输入。
验证结果：
  - .NET SDK 10.0.302；locked restore、Release build、format 校验及直接/传递 NuGet 漏洞检查通过。
  - 全量 143 项：135 项通过、8 项 macOS 原生测试跳过、0 项失败；64 次真实 Shell 图标重复提取未观察到超预算 GDI Object 增长。
  - win-x64 self-contained Native AOT EXE 29,512,192 字节，0 个 AOT/裁剪警告，无 coreclr/clrjit，476.56 ms 创建主窗口并明确退出。
  - 100,000 条、平均 554.7 字符生成数据导入 27,701.92 ms；300 次混合搜索 P95 2.32 ms、最大 7.15 ms。
  - 三轮最终 AOT：启动 473.19/390.58/403.63 ms；关闭后 PWS 103.32/110.13/94.82 MiB；Private Bytes 136.59/135.54/127.82 MiB。
限制：
  - 最新 10,000 次事件功能通过，但 Private Bytes 增长 8.46 MiB，仍高于严格的 8 MiB 预算；8 小时测试未执行。
  - 19 分钟托盘样本完成，不能替代 8 小时；最终 Private Bytes 120.99 MiB，整体内存门槛未完成。
  - Explorer、Chrome/Edge/Firefox、物理热键、托盘菜单、真实 HKCU、多显示器/混合 DPI、睡眠唤醒和管理员窗口本轮未形成可计结果。
  - Office 与远程桌面未执行；Windows ARM64、macOS 和 Linux 未由本轮结果推断通过。
  - 来源图标构建已实际启动，但当前桌面会话的 Windows Graphics Capture 报 D3D11 `0x887A0005`，GDI 窗口捕获为黑帧，因此没有把现有历史的视觉截图标记为通过。
```

## 12. 2026-07-28 执行记录：Windows 打包应用来源与图片路径

```text
日期：2026-07-28
阶段/任务：Phase 1.3 来源识别；Phase 1.4 Schema v5；Phase 1.5 高频历史刷新
状态：[x] 实现与自动验证完成；[~] 外部应用手动复核及完整桌面资源复测未完成
完成内容：
  - 在 WM_CLIPBOARDUPDATE 时快照 owner/foreground PID，读取阶段只在序列一致时使用事件快照，并记录明确归属依据。
  - 来源模型和 SQLite Schema v5 保存 EXE、AUMID、Package Family 与归属依据，Application/UI 不依赖 Win32 或 SQLite 类型。
  - Microsoft Store/MSIX 应用优先从 shell:AppsFolder/<AUMID> 解析本地化名称和图标；传统桌面应用保留版本资源/Shell 降级。
  - Windows 图片读取优先 DIBV5、DIB，再读取注册 PNG；写回端支持注册 PNG，PNG 元数据读取不引入新图片依赖。
  - HistoryChanged 使用单个可复用 150 ms 定时器合并刷新，避免每次采集都重建 50 个列表项并重复加载图标/缩略图。
验证结果：
  - .NET SDK 10.0.302；locked restore、Release build、format 校验及直接/传递 NuGet 漏洞检查通过。
  - 全量 150 项：142 项通过、8 项 macOS 原生测试跳过、0 项失败；Windows 52/52、Infrastructure 19/19、Application 8/8、Desktop Headless 28/28。
  - 当前运行 Codex 进程的真实 AUMID/Package Family、Codex 与截图工具 AppsFolder 图标像素、注册 PNG 往返及 GDI 释放测试通过。
  - 10,000 次连续 HistoryChanged 除初始查询外只触发一次刷新；隔离平台 10,000 次事件匹配 10,000/10,000，Private Bytes 增长 7.38 MiB，句柄 302 -> 299。
  - 100,000 条 Schema v5 数据导入 31,113.58 ms；300 次混合搜索 P95 2.37 ms、最大 7.49 ms。
  - win-x64 self-contained Native AOT EXE 29,531,648 字节，SHA-256 F712C3D312F0AE60AEFCF669001AC03ED279E11A51AA2EADA7DDA71BD7AD6E36；0 个 AOT/裁剪警告，无 coreclr/clrjit，413.22 ms 创建主窗口并明确退出。
限制：
  - 用户报告的 Codex 复制和截图工具截图发生在修复前；新构建尚未手动复核这两个完整工作流。
  - 旧 AOT 桌面进程在竞争样本中达到 7.24 GB Private Bytes，定位为逐事件全量 UI 刷新；修复已有自动回归，但完整 AOT 桌面尚未用隔离数据目录重跑 10,000 次。
  - 既有三次启动/关闭性能数据继续有效；本次仅增加一次 AOT 冒烟，不替代三次样本。8 小时长稳未执行。
  - Explorer、Chrome/Edge/Firefox、物理热键、托盘菜单、真实 HKCU、多显示器/混合 DPI、睡眠唤醒和管理员窗口未形成新结果。
  - Office 与远程桌面未执行；Windows ARM64、macOS 和 Linux 未由本轮结果推断通过。
```

## 13. 2026-07-28 执行记录：macOS 共享历史与检索

```text
日期：2026-07-28
阶段/任务：Phase 1.4/1.5 共享历史能力在 macOS arm64 复验
状态：[x] 持久化、检索、来源边界、外部应用和 arm64 AOT 功能通过；[~] 性能、正式发布和环境矩阵未完成
完成内容：
  - 在 APFS 上补齐 Schema v1-v5 分别新建/重复迁移、v4→v5 行升级、重启后使用次数/软删除和 macOS Unknown 来源投影。
  - PNG/TIFF 原图继续使用 CAS Blob；新增受限纯托管 TIFF 缩略图解码，覆盖损坏 TIFF、临时文件、原子替换和按需加载。
  - macOS 不注册 Windows 来源解析器；AUMID、Package Family、PID/名称/路径保持 NULL，UI 显示通用图标和“未知来源”。
  - TextEdit、Finder README、Safari、Chrome、Preview PNG/同像素 TIFF 去重及 pbcopy 均确认进入真实持久历史。
验证结果：
  - locked restore、Release build、format、NuGet 漏洞检查通过；全量 159 项中 144 通过、15 个 Windows 原生测试跳过、0 失败。
  - 100,000 条导入 15,289.62 ms；150 次目标查询 P95 1.04 ms、最大 1.72 ms；300 次总体 P95 1.01 ms、最大 2.23 ms。
  - 连续 10,000 次 HistoryChanged 只在静默期刷新一次；真实 NSPasteboard 10,000 次写入/抽样读回/标记/丢弃均 0 失败，完整桌面保持存活；修正后的 arm64 Native AOT 探针 Physical 增长 5.09 MiB、FD 7 -> 7，100,000 次计量阶段增长 0.45 MiB。
  - osx-arm64 Native AOT 0 告警并实际启动；三次启动 1262.00/420.21/458.88 ms。
  - DMG 校验通过；Bundle 仅 ad-hoc、PKG 未签名且 spctl 拒绝，未把本地产物标记为正式发布。
限制：
  - 原 framework-dependent 探针的 15.69 MiB RSS 增长已定位为 JIT/分层编译及诊断路径冷启动假阳性；Native AOT 事件路径满足 <8 MiB 预算。完整桌面纯后台为 41.4 MiB，首次开窗后关窗约 94-96 MiB，100 轮快速窗口不单调增长，但仍未达到 <=80 MiB 目标且历史样本曾超过 100 MiB。
  - 关闭窗口后菜单栏保持 12 分 23 秒，RSS 166.25 -> 96.94 MiB、线程 15 -> 14、FD 51 -> 47，但 Physical 138 -> 139 MB，10 分钟时长完成而内存目标失败。
  - 8 小时、osx-x64/Intel、登录启动重新登录、同一稳定签名权限重授予、睡眠/Space/多显示器/Retina/全屏未执行。
  - Terminal UI 被安全策略拒绝；Office 未安装；远程客户端未启动。
  - Developer ID Application/Installer、公证、staple、Gatekeeper 接受、安装升级/卸载和 GitHub macOS Runner 未完成。
```

## 14. 2026-07-28 执行记录：Windows 安全存储迁移与加密 WebDAV 同步

```text
日期：2026-07-28
阶段/任务：Phase 1.6 Windows 安全存储迁移、端到端加密 WebDAV 同步和 SQLite v7
状态：[x] Windows 本机实现、自动测试、性能及 win-x64 AOT 通过；[~] 真实服务、长期资源和跨平台矩阵未完成
基线：phase1/windows-sync 从 origin/main 5cf0e40c98f227be307d6863e4f236ed8c4ff67f 创建
完成内容：
  - 新增 bootstrap 数据目录定位器、StorageInstanceId、路径/卷验证、迁移状态机、独立迁移器、复制校验、原子切换、启动确认和回滚；设置页接入目录选择、空间检查及迁移生命周期。
  - SQLite 升级到 Schema v7；历史新增、置顶、删除和同步设置与 Outbox 原子提交，新增 Inbox、逐设备 Checkpoint、Blob staging、逐设置键逻辑版本、序号缺口保护和确定性冲突处理。
  - 新增版本化同步 DTO、源生成 JSON、HKDF-SHA256、AES-256-GCM、keyed-blob-id、Argon2id 恢复材料，以及 Windows Credential Manager 主密钥/完整连接配置分离存储；SQLite v7 不含 URL、用户名或密码字段。
  - 新增受限 WebDAV 客户端和 ISyncRemoteSession 适配器；实现不可变事件/Blob、严格 PROPFIND、同源重定向、证书固定、有限重试和稳定错误分类；精确指纹可接受自签名链但不接受主机名不匹配。
  - Desktop 组合根接入真实 SyncService；设置页支持创建/加入空间、恢复材料、连接验证和手动同步，主窗口只显示真实同步状态；存储迁移会暂停并排空同步。
  - 新增 `history.capture` 与 `history.retention` 动态设置：内容类型默认全开，自动清理默认关闭；设置使用加密事件逐键 LWW 同步。当时清理固定跳过置顶项并产生跨设备删除墓碑，现已由第 38 节改为默认保留且可配置。
  - 新增 `sync.pollInterval` 动态设置：缺省使用 5 分钟，设置页可选 30 秒到 1 小时，修改后当前调度器立即生效并通过加密事件同步到其他设备；本地变化始终立即触发同步。
  - WebDAV 表单统一 40 px、6 px 圆角输入样式；创建/加入、同步频率、证书指纹、恢复码、空间 ID、密钥版本和恢复材料均有用途说明，创建结果显示并可复制空间 ID、打开恢复文件目录。
  - 主窗口搜索区随窗口宽度自适应；Windows 来源图标的空 Shell 结果不再进入长期缓存，具备路径或 AUMID 的列表项会进行一次有限重试。
  - 本地数据目录选择会在确认目标为空、位于固定磁盘且不含重解析点后，将宽 ACL 收紧为当前用户私有权限并复检；验证失败原因不再隐藏。设置窗口阻止主窗口输入，有效目标使用模态确认说明退出、迁移和自动重启，取消保持原位置；确认后再次检查空目录，失败时使用模态错误窗口说明程序和已有内容均未改变。Windows 发布只保留 Native AOT 迁移器 EXE，清除框架依赖版的 `.deps.json` 和 `.runtimeconfig.json`。
验证结果：
  - locked restore、Release build、format 和 NuGet 直接/传递漏洞检查通过；构建 0 警告、0 错误。
  - 全量 244 项：236 项通过、8 项 macOS 原生测试按平台跳过、0 项失败；Application 10/10、WebDAV 29/29、Infrastructure 63/63、Windows 57/57、Desktop Headless 42/42。
  - SQLite v6 重新生成 100,000 条数据耗时 26,782.90 ms；目标查询 P95 2.67 ms，300 次总体 P95 2.33 ms、最大 5.79 ms，结果 PASS。
  - 最新 `artifacts/publish/win-x64-storage-migration-final-check-aot` 为 win-x64 Native AOT，0 个裁剪/AOT 告警；主程序 37,527,552 字节，SHA-256 556445E7214C4A93AFC987974A67950CEE1A4F92EA8675758396AE6AC3559A6D；迁移器 4,512,768 字节，SHA-256 3D51FCD67935BF91262FF2CEDC8B481C4BDC0D49DDCFEBD866B88D4CD1E9B9FF；无 `coreclr.dll`/`clrjit.dll` 和迁移器框架依赖配置，迁移器独立启动按无参数契约返回退出码 4。旧发布目录主程序仍由用户进程占用，本次未强制关闭或重复启动主程序。
  - 隔离 bootstrap 根目录下后台启动约 565.52 ms，创建定位器和 270,336 字节 SQLite；同步设置窗口实际创建，两个场景均响应 --exit 正常退出且未强杀。
  - 700 x 720 设置页、500 x 340 迁移确认和 500 x 350 最终校验错误 Headless/Skia 帧完成视觉复核；目录、说明和命令均无重叠或页脚遮挡。历史策略与同步表单底部、同步频率、恢复码、空间 ID、密钥版本及恢复路径也保持原有视觉验证结果。
  - 最终 AOT 主窗口实测企业微信、QQ、PixPin 与 Explorer 来源图标；Explorer 由蓝色占位符恢复为黄色文件夹图标。
限制：
  - 本轮未连接 Nextcloud、Synology 或标准 WebDAV 实例；当前双设备收敛结果来自有状态假远端，不能替代真实服务矩阵。
  - 设备撤销、密钥轮换、远端 Checkpoint/Blob 安全回收、OPTIONS/DELETE、系统唤醒及网络恢复触发未完成。
  - 修复后的完整 AOT 桌面 10,000 次剪贴板压力、三次资源采样、10 分钟托盘和 8 小时长稳未执行，不能标记发布门槛通过。
  - Windows ARM64、macOS 同协议/Keychain 固定向量、macOS Intel、Linux 和 GitHub Runner 未由 Windows x64 本机结果推断通过。
```

## 15. 2026-07-28 执行记录：macOS 安全存储迁移与加密同步对等

```text
日期：2026-07-28
阶段/任务：阶段 A，macOS 存储迁移、Keychain 同步接线、Native AOT helper 与打包对等
状态：[x] Apple Silicon 本机实现与自动验证完成；[~] Intel、正式签名/公证、真实跨系统双机和真实 WebDAV 服务未完成
Commit SHA: 03bd473a69863892ef2c39dcf4feed9ce812b3d2（feat(macos): add storage and sync parity）
macOS 版本与构建号: macOS 26.2 (25C56)
Mac 型号/CPU: Mac mini (Mac16,10)，Apple M4 10 核，16 GB
.NET SDK: 10.0.302
RID: osx-arm64
Release build 警告/错误: 0/0
全量测试总数/通过/跳过/失败: 268/248/20/0；20 项均为 Windows 原生测试
macOS 平台测试: 48/48，0 跳过
Desktop Headless 测试: 48/48，0 跳过
主程序大小与 SHA-256: 33,828,880；F9E038E0C3269C09ECF13EE1C9185D591208734FC6B3336A8540ECFDF01F775
迁移器大小与 SHA-256: 8,356,952；8D70F90B47B808A7702E46526043249DF24B6FDF13BD94CCE1ADE7C562534DCD
file 输出: 主程序与迁移器均为 Mach-O 64-bit executable arm64；迁移器无参数退出码 4；无 helper DLL/deps/runtimeconfig 和 CoreCLR/hostfxr
codesign/spctl/notary 结果: helper strict 与 Bundle deep/strict 通过；adhoc + Hardened Runtime；spctl rejected（预期，非 Developer ID）；PKG unsigned；notary skipped；DMG checksum valid
legacy 升级结果: 自动测试通过，既有 Application Support 数据根优先于新空 data 根，不创建空库遮蔽历史
成功迁移结果: 共享迁移器自动测试通过，数据库/Blob/恢复材料校验后切换并保留可识别源备份
最终非空竞态结果: 选择后与主程序退出后两层自动测试通过；后来出现的目标内容保持不变，迁移不开始或回滚
回滚结果: 缺失/不匹配启动确认自动回滚 locator、隔离目标并恢复源目录
Windows <-> macOS 同步结果: 同一共享协议/加密实现的有状态双设备矩阵在 macOS 通过；正式 Windows 与 macOS 应用双机未执行，不能视为真实跨系统验收
macOS <-> macOS 同步结果: 有状态双设备创建/加入、独立凭据、离线双向新增、置顶/标签冲突、Tombstone、保留策略和设置同步通过；两台正式 App 实机未执行
AOT 告警说明: 0 个 trim/AOT 分析告警；链接时 2 个 module-cache 调试信息告警可追溯到 Microsoft.NETCore.App.Runtime.NativeAOT.osx-arm64 10.0.10 官方 Apple 加密静态库携带的 -gmodules 信息，未 suppression，不影响代码生成或运行时依赖验证
未完成限制: osx-x64/Intel 匹配硬件或 Runner、Developer ID Application/Installer、notary/staple/Gatekeeper 接受、真实 Windows 双机、真实 Nextcloud/Synology/Apache WebDAV、Retina/大小写敏感 APFS/睡眠唤醒/网络恢复及完整手工迁移流程未执行
```

## 16. 2026-07-28 执行记录：WebDAV 服务商迁移

```text
日期：2026-07-28
阶段/任务：阶段 B，共享 WebDAV 服务商迁移协议、状态机、UI 与真实服务验证
状态：[x] 共享实现、自动故障矩阵、Apache 标准 WebDAV 和 osx-arm64 AOT 通过；[~] 正式 Windows <-> macOS App、Nextcloud/Synology 与 Intel 未完成
主分支基线：开发前已将 main 更新到 f6c1ffaa88f33d2b452f3707729f42388f6bb5f6
分支：codex/webdav-provider-migration
ADR Commit SHA: 5753905（docs(adr): define WebDAV provider migration）
实现 Commit SHA: dc824e9e00141eb4b9b44cde03bd4e6c97334a67（feat(sync): add coordinated WebDAV provider migration）
终态仲裁修复 Commit SHA: 9b6c336d693bbbac75b890fd1f6ee906cedaceb4（fix(sync): arbitrate provider migration terminal state）
完成内容：
  - 新增版本化 ProviderMigration DTO、SyncJsonContext 源生成登记、严格远端布局及加密控制标记。
  - 共享 SyncService 实现多设备 ready/freeze/mirror/verify/commit/completed 与全局 rollback；旧端 `terminal.enc` 使用不可变条件创建裁决 Completed/Rollback 唯一终态，普通上传先检查迁移 intent，Failed 状态保持 fail-closed。
  - 按规范顺序复制 metadata、事件和 Blob 原始密文；校验认证标签、descriptor、连续序号、ready 水位、Checkpoint、Blob 内容地址、目标身份/长度/SHA-256；相同对象幂等跳过，冲突对象阻断。
  - 平台安全存储新增计划级 source/target 凭据槽、读回验证、幂等提交和回滚；不同设备账号/密码互不写入迁移协议或覆盖。
  - SQLite 升级到 v8，仅持久化计划、epoch、远端指纹、设备水位、进度和稳定诊断码，不含 endpoint/root/用户名/密码/证书字段。
  - Windows/macOS 共用设置 ViewModel 与界面：当前服务、设备状态、对象/字节进度、参与设备本机凭据、继续和回滚；确认窗口使用 owner modal，密码提交后清空。
自动验证：
  - locked restore、Release build、format、git diff 检查和 NuGet 直接/传递漏洞审计通过；构建 0 警告、0 错误。
  - 全量 299 项：279 项通过、20 项 Windows 原生测试按 macOS 平台跳过、0 项失败；Infrastructure 91/91、WebDAV 35/35、Desktop Headless 51/51、macOS 48/48、Application 10/10、Architecture 2/2。
  - 服务商迁移端到端矩阵 18/18：双设备不同凭据、离线恢复、复制中断后续传、目标同名冲突、intent/commit/terminal/completed 单边写入修复、完成后陈旧参与设备回滚竞争、认证/权限/限流/瞬态/协议/证书/响应过大分类、序号缺口、Blob 内容地址破坏、部分设备提交、全局回滚、Failed 上传阻断、空空间、Tombstone、设置和 Checkpoint。
真实 WebDAV：
  - macOS 自带 Apache 2.4.62 启动两个独立 loopback WebDAV 端点，使用两个不同 Basic Auth 账号；两个客户端创建/加入同一空间后从 A 迁到 B。
  - 迁移前后按规范对象身份和原始密文字节计算的 SHA-256 完全一致，真实端点上的 metadata 与包含 Tombstone 的事件集保留；Blob、设置和本地 Checkpoint 由同一状态机的有状态故障矩阵覆盖。
  - 完成后新增事件只改变目标端，旧端密文哈希保持不变；旧端未自动删除。真实集成用例在本轮全量测试中执行并通过，不是环境缺失跳过。
UI/AOT：
  - 700 x 720 输入与 WaitingForDeviceAcks 状态 Headless/Skia 帧完成复核，设备 ID、水位、Ready/Offline、进度和操作按钮无重叠；继续/回滚模态所有权测试通过。
  - osx-arm64 主程序 34,740,768 字节，SHA-256 FEEC958B6D45ED8B1C2DA9EF1F19207155A9F7107D55281341BB7B3C8B7F2AF4。
  - osx-arm64 迁移器 8,356,952 字节，SHA-256 619CCE098B4B2FFFC5C42BBC834CC1FF67A5753D555D60B41CC532936E0F33F2。
  - 两者均为 arm64 Mach-O；无 CoreCLR/hostfxr 和 helper DLL/deps/runtimeconfig，helper 无参数退出码 4；隔离规范 bootstrap 的 AOT 后台启动及 --exit 通过。
  - 0 个 trim/AOT 分析告警；2 个已解释 clang module-cache 调试信息告警仍来自 .NET 10.0.10 官方 Apple NativeAOT 静态库，未 suppression。
限制：
  - 正式 Windows App 发起/macOS App 提交及反向、真实双机离线恢复与回滚未执行；共享状态机双设备测试不能替代该门槛。
  - Nextcloud、Synology、真实 TLS 证书固定/配额服务矩阵及 osx-x64/Intel 未执行；本轮真实 Apache 使用显式允许的 loopback HTTP 开发例外。
  - 设备撤销尚未实现，因此永久离线设备必须继续阻塞迁移；第一版只允许原发起设备恢复协调，不允许其他设备接管。
  - Developer ID、正式签名/公证、长期资源和 8 小时稳定性门槛不由本次共享功能验证外推为完成。
```

## 17. 2026-07-29 执行记录：WebDAV 迁移恢复与发布证据收口

```text
日期：2026-07-29
阶段/任务：阶段 B 本机可验证门槛复核，补齐协调者重启、旧 epoch、配额/ETag 与明文泄漏证据
状态：[x] 本机自动化、Apache、osx-arm64 AOT 和开发包链路通过；[~] 正式双机、外部服务、Intel 与发布身份仍未完成
主分支复核：开发前及本轮均已 fetch；main 与 origin/main 一致为 f6c1ffaa88f33d2b452f3707729f42388f6bb5f6
分支：codex/webdav-provider-migration
测试增强 Commit SHA: 007e4e2e6342463cdbc96482365a985870537429（test(sync): strengthen provider migration recovery gates）
新增证据：
  - 双设备镜像在成功复制首个密文对象后注入失败，销毁并重建 SyncService；新实例从同一 SQLite 和安全凭据计划槽恢复 PlanId、阶段、对象数与字节进度，随后幂等完成并保持两端密文哈希一致。
  - 完成 epoch 1 后，在当前权威远端注入使用合法空间密钥和新 PlanId 加密、但复用旧 epoch 的 intent；第二次迁移以 provider-migration-epoch-reused 拒绝，active credentials 保持当前权威端。
  - WebDAV 507 Insufficient Storage 映射为可恢复 Transient，Application 现有 Transient 故障矩阵证明失败时不提交目标凭据；超过 256 字节的不可信 ETag 被降级为不可用，迁移正确性继续由认证标签、长度和密文字节 SHA-256 决定。
  - 目标远端所有数据/控制对象扫描未出现用户名、密码、恢复码、剪贴板文本或 HTML 探针；两台设备的 SQLite、WAL、SHM 原始字节扫描未出现 endpoint、root、用户名、密码或恢复码。迁移表仍只保存指纹、epoch、水位、进度和稳定诊断码。
自动验证：
  - locked restore、Release build、format、git diff 检查和 NuGet 直接/传递漏洞审计通过；构建 0 警告、0 错误。
  - 全量 303 项：283 项通过、20 项 Windows 原生测试按 macOS 平台跳过、0 项失败；Infrastructure 92/92、WebDAV 38/38、Desktop Headless 51/51、macOS 48/48、Application 10/10、Architecture 2/2。
  - 服务商迁移端到端矩阵 19/19；其中 macOS 自带 Apache 2.4.62 双 loopback 端点、双账号真实用例在本轮全量测试中启用并通过。临时实例、测试凭据和数据已删除，18731/18732 无监听。
  - 128 MiB 临时 APFSX 镜像经 diskutil 确认为可写 Case-sensitive APFS；以该卷作为 TMPDIR 运行 PathRelationUsesVolumeCaseAndFileIdentity 通过，MixedCase 与 mixedcase 判为 Unrelated。卷已卸载，镜像已删除。
osx-arm64 开发包：
  - 输出目录 artifacts/macos-provider-migration-final-20260729 受 .gitignore 排除，不进入仓库。
  - 主程序 34,740,768 字节，SHA-256 C7133C6416271BC787437240EB5893712BC35E4C19424384AF59A0058AAE9B27；迁移器 8,356,952 字节，SHA-256 D4057FC268880D0223B917A5E220C8AC6A3BB0F4332EFE203B68B8056F9CC882。
  - 两者均为 arm64 Mach-O；无 CoreCLR/hostfxr 和 helper DLL/deps/runtimeconfig，helper 无参数退出码 4。App 及嵌套原生文件 codesign strict/deep 验证通过。
  - DMG 30,285,190 字节，SHA-256 968E9FF9791C0E58B2AAB1A667B3C60E260A32D99ED18D8719C208B9CD894E22，hdiutil CRC 验证通过；PKG 27,013,866 字节，SHA-256 DCBA4080FCE1041CAE72ABF2B70FFD388D3EDC18E65BC0A739FB94C29E55D568。
  - 规范 /private/tmp 隔离 bootstrap 下，打包 App 后台启动、第二实例 --exit 和主实例退出均返回 0；经 /tmp 符号链接的路径按安全策略拒绝。
  - 0 个 trim/AOT 分析告警；2 个已解释 clang module-cache 调试信息告警仍来自 .NET 10.0.10 官方 Apple NativeAOT 静态库，未 suppression。
限制：
  - 当前 App 为 ad-hoc Hardened Runtime 签名，PKG 无签名，notary skipped，spctl rejected；Developer ID Application/Installer、notary/staple 和 Gatekeeper 接受未执行。
  - osx-x64/Intel、正式 Windows <-> macOS 双向发起/离线恢复/回滚、两台正式 macOS App、Nextcloud、Synology、真实 TLS 固定与真实配额服务矩阵未执行。
  - Retina、睡眠唤醒、网络恢复、多 Space/多显示器、完整手工迁移和 8 小时长稳仍按既有未完成项保留。
```

## 18. 2026-07-29 执行记录：macOS 系统恢复触发与最终本机包验证

```text
日期：2026-07-29
阶段/任务：补齐 macOS 系统唤醒/网络变化立即同步触发，并复核当前代码的并行测试与 AOT 包结构
状态：[x] 原生事件源、同步接线、本机自动化、Apache 与 osx-arm64 开发包通过；[~] 物理恢复场景及外部发布门槛仍未完成
主分支复核：开发前已 fetch；main 与 origin/main 一致为 f6c1ffaa88f33d2b452f3707729f42388f6bb5f6
分支：codex/webdav-provider-migration
系统事件 Commit SHA: 78ba456f7747df97f1b914b3f5c594a3b6ead22d（feat(macos): sync after wake and network changes）
连接池修复 Commit SHA: cced21540f8af7355ea0cc379221bdb7b0dffdbe（fix(sqlite): isolate connection pool cleanup）
完成内容：
  - Platform.Abstractions 新增 IDesktopSystemEventService；macOS 使用 NSWorkspaceDidWakeNotification 与 SystemConfiguration State:/Network/Global/.*，Desktop 将两类信号合并为 SyncService.RequestSync()。
  - NSWorkspace observer 与 dynamic store/dispatch queue 均有显式启动、重复启动保护、回调异常边界和释放路径；初始化失败不阻断应用，周期同步继续兜底。
  - 原生测试真实注册两个事件源，投递进程内 NSWorkspace 通知，并通过同一 UnmanagedCallersOnly ABI 探针验证 dynamic store 句柄到托管事件的路由；释放后两条路径均不再发布。Headless 测试验证每个事件各触发一次同步请求并在协调器释放后解绑。
  - 离线双设备新增、冲突更新、删除和保留策略的最终收敛用例通过；恢复网络时的系统事件只加速触发，不改变 Outbox、幂等或周期兜底语义。
  - 并行全解最初暴露安全扫描读取活动 SQLite 文件及全局 ClearAllPools 关闭其他测试连接的竞态；扫描改为先停止 SyncService/写队列形成稳定落盘快照，生产恢复/迁移与测试上下文改为仅清理当前连接字符串对应的池。
自动验证：
  - Release build 0 警告、0 错误；format、git diff 检查通过。安全落盘扫描连续 5/5 通过，修复后的本地并行全解连续 2/2 通过。
  - 启用 Apache 2.4.62 双 loopback 端点和双账号后，Infrastructure 92/92；最终全量 304 项：284 项通过、20 项 Windows 原生测试按 macOS 平台跳过、0 项失败。
  - macOS 平台 49/49、Desktop Headless 51/51、WebDAV 38/38、Application 10/10、Architecture 2/2；临时 Apache、账号、远端数据均已删除，18731/18732 无监听。
osx-arm64 开发包：
  - 输出目录 artifacts/macos-system-events-final-20260729 受 .gitignore 排除，不进入仓库。
  - 主程序 34,573,888 字节，SHA-256 31E3D8CAB43F3323C5EB1E7E9B2A68D42656767CE4FD769CC4BE05E473500CF5；迁移器 8,326,528 字节，SHA-256 A341EF11EE2BEF223175AF6C112C2AF5B2B2A3C479E6C17605A9377F9B0DED2F。
  - 两者均为 arm64 Mach-O；无 CoreCLR/hostfxr 和 helper DLL/deps/runtimeconfig，helper 无参数退出码 4。App 及嵌套原生文件 codesign strict/deep 通过。
  - DMG 30,289,627 字节，SHA-256 E348080A32BF00A03D935A1B90D2C76E5BCB226B3C008B1C89930A8A58294B71，CRC 有效，挂载根含 SnapBoard.app 与 Applications -> /Applications。
  - PKG 27,019,279 字节，SHA-256 B4FF7F59049628372C8226F66A70978F69A822E0001E5BA48781B467D18C0DF0；payload 含主程序、helper、Info.plist、icns/Template 资源，PackageInfo identifier 为 com.wuliangtdi.snapboard，install-location 为 /Applications。
  - 私有 /private/tmp 隔离 bootstrap 下，当前 AOT App 后台启动、第二实例 --exit 和主实例退出均返回 0，证明系统事件原生初始化可在 AOT 桌面进程运行；临时数据已删除。
  - 0 个 trim/AOT 分析告警；2 个已解释 clang module-cache 调试信息告警仍来自 .NET 10.0.10 官方 Apple NativeAOT 静态库，未 suppression。
限制：
  - 本轮自动化没有执行真实合盖/系统睡眠，也没有切断 Wi-Fi、以太网或其他实际网络接口；只能确认原生注册、回调、同步请求与 AOT 初始化，物理恢复时序仍须手工验收。
  - 当前 App 为 ad-hoc Hardened Runtime 签名，PKG 无签名，notary skipped；Developer ID Application/Installer、notary/staple 与 Gatekeeper 接受未执行。
  - osx-x64/Intel、正式 Windows <-> macOS 双向发起/离线恢复/回滚、两台正式 macOS App、Nextcloud、Synology、真实 TLS 固定与真实配额服务矩阵仍未执行。
  - Retina、多 Space/多显示器、完整手工迁移、8 小时长稳及既有内存目标仍未完成。
```

## 19. 2026-07-29 执行记录：macOS 扩展 ACL 安全边界复核

```text
日期：2026-07-29
阶段/任务：按原始 macOS 对等清单复核私有目录 ACL 与迁移进程完整身份
状态：[x] Darwin ACL ABI、安全修复、原生测试、Apache 全解及 osx-arm64 开发包通过；[~] 外部设备与发布门槛保持未完成
Commit SHA: 172ba8f（fix(macos): enforce extended ACL privacy）
发现与修复：
  - 实际 APFS 目录加入 group:everyone allow list 后，Darwin acl_get_entry 成功返回 0，最后一个条目后返回 -1/EINVAL；原实现将 0 错误当作遍历结束，可能把含扩展 allow ACL 的 0700 目录误报为当前用户私有。
  - 枚举逻辑改为读取成功条目并仅将 EINVAL 视为正常结束；任何扩展 allow 条目均使 IsPrivateToCurrentUser=false，其他原生错误继续明确失败。
  - 原生测试证明允许处理的空目录会同时清除扩展 ACL 和 group/other mode；非空用户目标拒绝收紧，ACL 条目数、mode、内容和目录时间戳保持不变。
  - 进程身份测试新增伪造 Mach-O 路径，WaitForProcessExitAsync 与 StopProcessAsync 均拒绝；既有 PID、启动时间和 UID 校验继续通过。
自动验证：
  - Release build 0 警告、0 错误；format 与 git diff 检查通过。
  - macOS 原生项目 49/49、0 跳过；启用 Apache 2.4.62 双 loopback 端点和双账号后，全量 304 项：284 项通过、20 项 Windows 原生测试按平台跳过、0 项失败。
  - 临时 Apache、账号、锁文件和远端数据已删除，18731/18732 无监听。
osx-arm64 开发包：
  - 输出目录 artifacts/macos-acl-final-20260729 受 .gitignore 排除，不进入仓库。
  - 主程序 34,573,888 字节，SHA-256 CFC093FDFA2467193AC985486B5CFE2349543764595782A1B01AF7038CC3EB54；迁移器 8,326,528 字节，SHA-256 0819E984FF3E95BF003EAE447C9D4B0273076902FF2D3D0A215EA960CF6D0BF6。
  - 两者均为 arm64 Mach-O；无 CoreCLR/hostfxr 和 helper DLL/deps/runtimeconfig，helper 无参数退出码 4，codesign strict/deep 通过。
  - DMG 30,290,132 字节，SHA-256 711F868481127F7CA2A9D3A4D0D66F1B74BC6248C62E6C8DFDA14ED9C4EE9198，CRC 有效，含 SnapBoard.app 与 Applications -> /Applications。
  - PKG 27,019,391 字节，SHA-256 362BCB93FCE4169377FAE9A47D2EEA827440DB6348CDC81C34D4DA392C07A5A9；payload、com.wuliangtdi.snapboard、/Applications 安装位置和 root 授权要求均复核通过。
  - 私有 /private/tmp bootstrap 下，AOT App 启动、第二实例 --exit 和主实例退出均返回 0；挂载、展开和 bootstrap 临时目录已删除。
  - 0 个 trim/AOT 分析告警；2 个已解释 clang module-cache 调试信息告警仍来自 .NET 10.0.10 官方 Apple NativeAOT 静态库，未 suppression。
限制：
  - osx-x64/Intel、正式 Windows <-> macOS App 双机、两台正式 macOS App、Nextcloud/Synology、真实 TLS/配额、Developer ID/公证、物理睡眠与网络恢复、Retina/多显示器、8 小时长稳和既有内存目标仍未完成。
```

## 20. 2026-07-29 执行记录：Checkpoint 恢复与大 Blob 迁移收口

```text
日期：2026-07-29
阶段/任务：按原始 macOS 对等清单补齐服务商迁移的大 Blob 与缺失 Checkpoint 恢复证据
状态：[x] 安全恢复、8 MiB Blob、真实 Apache 全解及当前 osx-arm64 开发包通过；[~] 外部设备与正式发布门槛保持未完成
开发基线：开发前 main 与 origin/main 均为 f6c1ffaa88f33d2b452f3707729f42388f6bb5f6
Commit SHA: ec3eb59（fix(sync): rebuild missing checkpoints safely）
发现与修复：
  - Checkpoint 行丢失后，读取端会回到序号 0；既有 Inbox 仍含已应用事件，直接重放会撞上唯一约束并被误报为本地持久化失败。
  - EnsureRemoteDevice 的同一 SQLite 写事务现只在 Checkpoint 缺失时统计 Inbox；记录必须从序号 1 开始且 count/min/max 证明无间隙，随后以最后一条已验证事件的序号和 EventId 重建。存在缺口或非法 EventId 时事务回滚，不推断、不跳过事件。
  - 重建后的 ETag 保守置空；普通同步从已应用序号继续，服务商迁移 ready 水位继续用远端最大序号和 EventId 复核。
  - 服务商迁移主路径载荷从 70 KiB 提升到 8 MiB，并新增两设备在冻结前丢失本地 Checkpoint 后重建、收敛、完成迁移及源/目标主密文哈希相等的端到端覆盖。
自动验证：
  - 相关测试 27 项：26 项通过、1 项真实 WebDAV 环境跳过；Release build 0 警告、0 错误；dotnet format 检查 342 个文件、0 个改动。
  - 未注入外部服务时全量 307 项：286 项通过、20 项 Windows 原生测试和 1 项真实 WebDAV 测试跳过、0 项失败。
  - 启用 Apache 2.4.62 双 loopback 端点和双账号后，全量 307 项：287 项通过、20 项 Windows 原生测试按平台跳过、0 项失败；Infrastructure 95/95。
  - 临时 Apache、账号、锁文件和远端数据已删除，18731/18732 无监听。
osx-arm64 开发包：
  - 输出目录 artifacts/macos-checkpoint-final-20260729 受 .gitignore 排除，不进入仓库。
  - 主程序 34,573,888 字节，SHA-256 433AAB88C87F9B76AD2EDC9D367C7CCE6E73584DBDF5E623DC4BBEB1F75ED076；迁移器 8,326,528 字节，SHA-256 F7E9474ABB428810252F18F5144CEB452CA18B7141951F7F10AB7038122DBA8E。
  - 两者均为 arm64 Mach-O；无 CoreCLR/hostfxr 和 helper DLL/deps/runtimeconfig，helper 无参数退出码 4，codesign strict/deep 通过。
  - DMG 30,290,820 字节，SHA-256 0E96CCC36974FB4586E5BDBED43F532315C6550D8619A07252BE71E681B676BD，CRC 有效，根目录仅含 SnapBoard.app 与 Applications -> /Applications。
  - PKG 27,020,455 字节，SHA-256 EE2A601D4721BAFFA74DC2348BD4EE9F5CCF4307ACB78A2DFBAAF4BEE33EAE9C；17 项 payload、com.wuliangtdi.snapboard、/Applications 安装位置和 root 授权要求均复核通过。
  - 私有 /private/tmp bootstrap 下，AOT App 启动、第二实例 --exit 和主实例退出均返回 0；挂载、展开和 bootstrap 临时目录已删除。
  - 0 个 trim/AOT 分析告警；2 个已解释 clang module-cache 调试信息告警仍来自 .NET 10.0.10 官方 Apple NativeAOT 静态库，未 suppression。
限制：
  - App 仍为 ad-hoc Hardened Runtime 签名，PKG 未签名，notary skipped；Developer ID Application/Installer、notary/staple 与 Gatekeeper 接受未执行。
  - osx-x64/Intel、正式 Windows <-> macOS App 双机、两台正式 macOS App、Nextcloud/Synology、真实 TLS/配额、物理睡眠与网络恢复、Retina/多显示器、8 小时长稳和既有内存目标仍未完成。
```

## 21. 2026-07-29 执行记录：Intel 文件系统 ABI 与双架构 AOT 预检

```text
日期：2026-07-29
阶段/任务：补齐 macOS x86_64 文件系统 ABI，执行 arm64 原生包与 x64 Rosetta 启动预检
状态：[x] ABI 修复、本机双架构 AOT 预检、真实 Apache 全解及 arm64 开发包通过；[~] Intel Runner 与正式发布门槛保持未完成
开发基线：开发前 main 与 origin/main 均为 f6c1ffaa88f33d2b452f3707729f42388f6bb5f6
Commit SHA: 0bbd9d4（fix(macos): use 64-bit filesystem ABI on Intel）
环境：macOS 26.2 (25C56)，Apple M4 arm64，16 GiB，.NET SDK 10.0.302
发现与修复：
  - 干净 checkout 的 osx-x64 AOT 可生成正确 Mach-O，但 Rosetta 冷启动把已存在目录误判为非目录。
  - 托管 MacOSFileStatus/MacOSFileSystemStatus 使用现代 64 位 Darwin 布局；x86_64 的无后缀 lstat/statfs 符号仍使用旧 inode/statfs ABI，Mode 等字段偏移不一致。
  - LibraryImport 现显式调用 lstat64/statfs64；这两个入口在 arm64/x86_64 都导出且与托管结构一致。arm64 原生目录/卷测试与 x64 Rosetta 冷启动均通过。
  - CI Build/Test 矩阵加入 macos-15-intel，使 49 项 macOS 平台测试在 Intel Runner 上实际执行；既有 x64 Native AOT job 继续校验主程序与 helper。
自动验证：
  - 全新 checkout locked restore 通过；Release build 0 警告、0 错误；dotnet format 检查 342 个文件、0 个改动；macOS 原生项目 49/49。
  - 未注入外部服务时全量 307 项：286 项通过、20 项 Windows 原生测试和 1 项真实 WebDAV 测试跳过、0 项失败。
  - 启用 Apache 2.4.62 双 loopback 端点和双账号后，全量 307 项：287 项通过、20 项 Windows 原生测试按平台跳过、0 项失败；Infrastructure 95/95。
  - 临时 Apache、账号、锁、日志、远端数据、DMG 挂载点和 bootstrap 已删除，18731/18732 无监听。
osx-x64 Rosetta 预检：
  - 输出目录 artifacts/osx-x64-rosetta-abi64-20260729 受 .gitignore 排除，不进入仓库。
  - 主程序 35,727,960 字节，SHA-256 8EBB4AD0080DBCD42549DE1B7D89F66AAF579883A8A52D722150B63239ACD41B；迁移器 8,554,488 字节，SHA-256 3660B83538BDA663A20A771F48C59EEFDAE7A78B41BBF1C41216CDD7D394B780。
  - 两者均为 x86_64 Mach-O；无 CoreCLR/hostfxr 和 helper DLL/deps/runtimeconfig，helper 无参数退出码 4。
  - Rosetta 下私有 bootstrap 后台启动成功，根/bootstrap/data 均为 0700；第二实例 --exit 和主实例退出均返回 0。
  - 这是同机 Rosetta 预检，不等价于 Intel 匹配硬件/Runner 测试或打包结果。
osx-arm64 最终开发包：
  - 输出目录 artifacts/macos-abi64-final-20260729 受 .gitignore 排除，不进入仓库。
  - 主程序 34,573,888 字节，SHA-256 CBE826B2A850625C8D03829AA283F0D19A345150EA5A80A2566FD366E8C70186；迁移器 8,326,528 字节，SHA-256 CF330DF4D0E2ED72CA5499707F160A29A86D78D4F292E64C698213F158A84C94。
  - 两者均为 arm64 Mach-O；无 CoreCLR/hostfxr 和 helper DLL/deps/runtimeconfig，helper 无参数退出码 4，codesign strict/deep 通过。
  - DMG 30,290,823 字节，SHA-256 01A98C67867594EF7B7B0CE2618851F605FFCD9F60698C328C964FF98E7D2C19，CRC 有效，根目录仅含 SnapBoard.app 与 Applications -> /Applications；从挂载镜像隔离启动及退出通过。
  - PKG 27,020,393 字节，SHA-256 30E6B1CF37CC7A0842E0F74575288AEDB9ABC2355FFB7AC4214490C181B35F67；PackageInfo 为 17 项 payload、com.wuliangtdi.snapboard、/Applications 和 root 授权。
  - 0 个 trim/AOT 分析告警；2 个已解释 clang module-cache 调试信息告警仍来自 .NET 10.0.10 官方 Apple NativeAOT 静态库，未 suppression。
限制：
  - App 仍为 Hardened Runtime ad-hoc 签名，PKG 未签名，notary skipped；本机无 Developer ID Application/Installer 或公证凭据，Gatekeeper 正式接受未执行。
  - 分支尚未推送，新增的 macos-15-intel CI 与既有 arm64/x64 发布 Job 未远程运行，因此不记录虚构的 Runner 链接或结果。
  - 正式 Windows <-> macOS App 双机、两台正式 macOS App、Nextcloud/Synology、真实 TLS/配额、物理睡眠与网络恢复、Retina/多显示器、8 小时长稳和既有内存目标仍未完成。
目标关闭说明：
  - 用户确认上述剩余项目均属于当前不具备条件的实际验证。本开发目标按“代码实现、自动测试、当前可用环境验证和限制记录完整”关闭。
  - 关闭本开发目标不改变这些条目的待验证状态，也不等同于 macOS 正式发布、跨设备产品验收或 PLAN.md 的最终发布退出条件通过。
```

## 22. 2026-07-29 执行记录：用户可见名称与 Windows 图标资源复核

```text
日期：2026-07-29
阶段/任务：统一双平台用户可见应用名，并复核 Windows 可执行文件图标与产品资源
状态：[x] 展示代码、产品元数据、自动测试、Headless 渲染和当前环境 PE 资源检查通过；[~] Windows Native AOT 最终 EXE 与任务管理器显示待实机验证
开发基线：开发前已 fetch；main、origin/main 与 FETCH_HEAD 均为 f6c1ffaa88f33d2b452f3707729f42388f6bb5f6
分支：codex/webdav-provider-migration（变更前 HEAD 78b2491111ebfa0880f011ff53ca53a15a16389c）
完成内容：
  - 主窗口、快速窗口和设置窗口的系统标题统一为“闪剪”；主界面及设置页品牌区不再显示英文标识，首次启动模拟记录也不再展示英文品牌。
  - Windows 托盘菜单/提示、macOS 菜单栏菜单/提示、文件选择器和存储迁移提示统一使用“闪剪”。
  - Desktop 的 AssemblyTitle、Product 和 Description 设为“闪剪”；macOS CFBundleDisplayName/CFBundleName 设为“闪剪”。
  - 内部程序集名、SnapBoard.Desktop.exe、SnapBoard.app 内部可执行文件、根命名空间、Bundle ID、数据路径和协议标识保持不变；Windows 任务管理器“详细信息”页仍预期显示 SnapBoard.Desktop.exe。
图标与名称资源验证：
  - Windows 源图标 snapboard.ico 为 9 尺寸 ICO；Desktop 项目继续通过 ApplicationIcon 嵌入，不新增或替换品牌素材。
  - 在 macOS 上交叉生成的 win-x64 PE GUI 产物保留 SnapBoard.Desktop.exe 文件名，并同时包含 RT_ICON、RT_GROUP_ICON、RT_VERSION 和 RT_MANIFEST。
  - 供 Windows Native AOT 使用的托管 Win32 资源模块中 FileDescription=闪剪、ProductName=闪剪，且包含图标组；.NET 10 Native AOT 构建目标会将该模块作为 --win32resourcemodule 输入。
  - macOS Info.plist 模板通过 plutil lint；最终 Windows Native AOT 产物仍必须在 Windows Runner/实机重新核对资源、Explorer 图标和任务管理器“进程”页名称。
自动验证：
  - Release build 0 警告、0 错误；Desktop Headless 52/52，通过主/快速/设置窗口标题、程序集显示元数据、内部程序集名和图标非空断言。
  - 全量 308 项：287 项通过、20 项 Windows 原生测试及 1 项真实 WebDAV 测试按当前环境跳过、0 项失败。
  - osx-arm64 Native AOT 主程序与迁移器均生成 arm64 Mach-O；0 个 trim/AOT 分析告警，仍只有 2 个既有且已解释的 .NET 10.0.10 Apple 静态库 clang module-cache 调试信息告警。
  - 主窗口 Headless 实际渲染帧已检查，单行“闪剪”品牌区没有文字重叠、溢出或异常空白。
性能说明：
  - 本次只修改静态文案、程序集/Bundle 元数据和既有图标资源引用，没有新增依赖、后台任务、对象缓存或运行时热路径，因此未新增性能压测或内存采样。
  - 既有内存、启动、10,000 次压力与 8 小时长稳门槛不因本次名称调整而改变；正式发布仍必须沿用当前性能验证清单。
限制：
  - 当前主机为 macOS，不能把交叉构建和资源检查表述为 Windows Native AOT 实机或任务管理器验收。
  - Windows 默认“进程”页目标显示为“闪剪”；“详细信息”页按已确认方案继续显示内部文件名 SnapBoard.Desktop.exe。
```

## 23. 2026-07-29 执行记录：签名的多源自动更新

```text
日期：2026-07-29
阶段/任务：Windows、macOS 与 Linux 分架构自动更新、GitHub/官方多源和发布签名
状态：[x] 代码、自动测试、本机 AOT、ad-hoc App/DMG/PKG、Velopack 包与 feed 签名验证完成；[~] 正式安装升级与远程发布待实际环境验证
开发基线：c16dc9ae31b69d1842418c1ca79093a3afbb4736（main）
实现提交：ddf59862f6909a1ebc870f262efd39f2f555df7b
分支：codex/automatic-updates
环境：macOS 26.2 (25C56)，Apple M4 arm64，16 GiB，.NET SDK 10.0.302，Velopack 1.2.0
实现内容：
  - 新增独立 SnapBoard.Update.Velopack 适配项目；Velopack bootstrap 在 Avalonia 和单实例初始化前运行，禁用启动时静默套用更新。
  - 稳定版/测试版 feed 按 win/osx/linux、x64/arm64 隔离；自动模式合并已配置官方源与 GitHub，单源失败可回退，同版本文件名/SHA-256/长度不一致立即阻断。
  - 每个 releases.<channel>.json 必须通过 ECDSA P-256/SHA-256 DER 签名；客户端只内置公钥，完整包继续使用签名 feed 内 SHA-256 和长度校验。
  - 设置页新增自动检查、稳定版/测试版、自动/GitHub/官方来源、手动检查、下载进度和安装并重启；设置只保存在本机，不进入 WebDAV 同步。
  - 首次自动检查延迟 30 秒，之后每 12 小时；安装前先暂停并排空同步、暂停剪贴板采集，安排退出后替换，安排失败则恢复原状态。
  - 旧的手工压缩包/DMG 没有 Velopack 安装元数据时明确显示不可自动更新，需先用新版 Setup/AppImage 安装一次。
发布与密钥：
  - 仓库只提交 packaging/updates/update-signing-public.pem；本地私钥位于仓库外且权限为 0600，其他电脑和普通用户只需要客户端内置公钥。
  - GitHub Release job 从 SNAPBOARD_UPDATE_SIGNING_PRIVATE_KEY_PEM Secret 创建临时 0600 私钥文件；脚本先校验私钥与仓库公钥匹配，再签名并复验所有 feed。
  - vpk 由 .config/dotnet-tools.json 锁定为 1.2.0。Release workflow 为 Windows、Linux 和 macOS 两个 RID 生成架构唯一包名、安装包、便携包和 feed。
  - Apple Developer ID/公证 Secret 采用全有或全无校验；全部缺失时只生成明确的 ad-hoc/未签名开发包。Windows 代码签名尚未配置。
自动验证：
  - locked restore 通过；Release build 0 警告、0 错误；dotnet format、git diff --check、release.yml YAML 解析和两个 shell 脚本语法检查通过。
  - 全量 329 项：308 项通过、20 项 Windows 原生测试及 1 项真实 WebDAV 测试按当前环境跳过、0 项失败；更新专项 16/16，Application 设置与 Desktop Headless 覆盖同时通过。
  - Headless/Skia 700 x 720 设置页截图已复核，更新区控件、状态、按钮和滚动布局无重叠或溢出。
  - 本机最终 osx-arm64 App Bundle、DMG 和 PKG 生成；Bundle ad-hoc codesign deep/strict 通过，Display Name/Name 为“闪剪”，内部可执行文件保持 SnapBoard。
  - Velopack 0.2.0 生成 full nupkg 31,432,622 字节、Portable.zip 30,409,334 字节、未签名 Setup.pkg 30,407,852 字节；319 字节 feed 的 72 字节 DER 签名由仓库公钥 Verified OK。
AOT 与性能：
  - main 基线主程序 34,757,344 字节、发布目录约 148 MiB；最终主程序 35,937,520 字节、约 154 MiB，增加 1,180,176 字节（3.40%），SHA-256 29741FE9C14AF05517916D01BCC5018B479E5C165121B9FE665327802B75F795。
  - 两边均为 arm64 Native AOT Mach-O、无 CoreCLR、0 个 trim/AOT 分析告警；仅有相同的 2 个已解释 Apple NativeAOT 静态库 module-cache 调试信息告警。
  - 隔离数据根三轮暖样本：基线暖启动均值 701.36 ms，更新版 731.02 ms；窗口 Physical 平均 197.88/197.98 MiB，后台 98.27/98.51 MiB，FD 均为 45，线程 17/19。
  - 35 秒可见加 5 秒后台样本跨过自动检查延迟：基线/更新版后台 Physical 为 99.39/99.14 MiB，Lifetime Peak 为 201.52/201.58 MiB。最终重建首轮另出现 251.92 MiB 窗口、145.27 MiB 后台异常冷样本，已保留在 PERFORMANCE.md，不把短测外推为长期通过。
限制与关闭口径：
  - 当前官方自建更新 URL 尚未部署；普通构建自动模式只使用 GitHub。未来启用官方源必须提供固定 HTTPS 基地址并执行真实故障切换测试。
  - 没有在 Windows 已安装版本、Developer ID 正式 macOS App、Linux 发行版或 GitHub Runner 上执行旧版本 -> 新版本下载、退出替换、重启与失败恢复；这些均是实际发布验证，不由本机包生成替代。
  - 本机没有 Windows 代码签名证书或 macOS Developer ID；SmartScreen、Gatekeeper、公证、staple 与正式安装提示未验证。应用级 feed 签名已通过，但不能替代系统代码签名。
  - 没有执行真实远端下载、10 分钟/8 小时长稳或更新期间数据库迁移；当前功能不修改 Schema，未来不可逆迁移必须先实现备份/恢复门槛。
  - 用户已确认当前不具备上述实际环境。本开发目标按“代码、自动测试、当前可用环境验证和限制记录完整”关闭，不等于正式发布门槛通过。
```

## 24. 2026-07-29 执行记录：同步设置层级与快捷搜索视觉统一

```text
日期：2026-07-29
阶段/任务：调整 WebDAV 迁移入口顺序，并统一快捷搜索与现有闪剪设计系统
状态：[x] XAML、Headless 行为/边界测试、默认与最小尺寸截图及 Release build 通过
开发基线：76fd2c7ea8b24c7a5caab847053c5f001a530d6c（main / origin/main）
分支：codex/refine-sync-and-quick-search
完成内容：
  - 设置页同步顺序改为后台频率 -> 创建新空间/加入现有空间与配置表单 -> 保存并验证 -> 迁移已有空间。
  - 迁移区仍只在已有同步空间时显示；原迁移字段、x:Name、命令、确认窗口、凭据处理和共享状态机均未改变。
  - 迁移标题改为“迁移已有空间”，明确空间 ID、密钥和历史保持不变，仅更换 WebDAV 服务。
  - 快捷搜索复用闪剪 logo、Surface、搜索框、历史列表选中态、图标底板、分隔线及 primary/secondary command；保留虚拟化、键盘、双击、粘贴和窗口生命周期。
自动与视觉验证：
  - Desktop Headless 53/53；新增迁移区域必须位于保存并验证按钮之后的坐标断言。
  - 全量 330 项：309 项通过、20 项 Windows 原生测试及 1 项真实 WebDAV 测试按当前环境跳过、0 项失败。
  - 快捷搜索默认 680 x 480 和最小 560 x 380 均完成 Headless/Skia 实渲染；品牌、搜索、来源、状态及两个粘贴按钮无重叠、溢出或视口外内容。
  - Desktop Release build 0 警告、0 错误；dotnet format --verify-no-changes 与 git diff --check 通过。
  - osx-arm64 Native AOT 通过，0 个 trim/AOT 分析告警；仍只有 2 个已解释的 .NET Apple NativeAOT 静态库 module-cache 调试信息告警。
限制：
  - 本次为 Avalonia 内容层和信息层级调整，没有改变 Windows/macOS 系统标题栏；真实 Windows 字体缩放、macOS Retina 与多显示器仍沿用既有实际验收项。
  - 未修改同步协议、SQLite、平台安全存储或 WebDAV 迁移执行逻辑，因此不以此次截图替代真实双设备迁移验证。
```

## 25. 2026-07-29 执行记录：Windows 快速窗口双击快捷键与全屏保护

```text
日期：2026-07-29
阶段/任务：docs/QUICK_WINDOW_SHORTCUT_AND_FULLSCREEN_REQUIREMENTS.md 第 13.4 节阶段 A（Windows 与共享层）
状态：[x] Windows 与共享层实现、自动验证、win-x64 Native AOT 和当前 Windows 实机验证完成；[ ] macOS 原生阶段 B 待从最新 main 继续
开发基线：c8f449cf25ad8ead2dfbd1eade65b2b19fec9202（最低要求基线）；阶段 A 起点 `fffa342`
开发分支：codex/settings-sidebar-navigation，已快进合入 `main`
实现内容：
  - Platform.Abstractions 增加 Primary/Double 两槽模型、来源事件、本机设置快照和前台窗口 Normal/Maximized/FullScreen/Unknown/Unavailable 语义；现有单次快捷键接口继续保留，避免破坏 macOS 编译边界。
  - 共享 DoubleHotKeyPressStateMachine 使用单调 TimeProvider；第一次只进入等待，第二次完成后立即复位，超时后的当前按键成为下一轮第一次，重复按键、配置变化、窗口保护、捕获、退出和显式入口都会清理待定状态。
  - Windows 使用两个固定槽位及备用 ID 原子替换 RegisterHotKey；两槽均带 MOD_NOREPEAT，相同组合拒绝，冲突和旧 ID 注销失败完整回滚，清除 Double 不影响 Primary，陈旧 WM_HOTKEY ID 不再触发。
  - HKCU\Software\SnapBoard\Desktop 以当前格式版本 2 保存两槽、保护范围和两个默认开启的保护开关；版本 1 或其他非当前配置整组拒绝并按版本 2 默认值初始化，不读取或迁移开发期 GlobalHotKey 值，也不生成同步事件。
  - WindowsForegroundWindowStateService 使用 IsZoomed、窗口样式、DWM 扩展边框、MonitorFromWindow/GetMonitorInfo、可见/最小化/遮蔽/桌面与进程身份检查；按前台窗口所在显示器识别独占/原生全屏和无边框全屏，标准最大化保留 Maximized 分类，并排除 SnapBoard 自身窗口，不读取标题、游戏名、文档路径或剪贴板内容。
  - 快捷键保护只处理 Primary/Double 全局来源；托盘、应用按钮、单实例命令和 --quick 走显式入口。记录保护在 IClipboardContentReader.ReadAsync 前判断；Manual、ForegroundProtection、StorageMigration、UpdateInstallation 使用可组合位标记，清除单一原因不会释放其他原因。
  - 设置页保留滚动布局和 settings-toggle，新增“连按两次快捷键打开快速窗口”、完整按键录入、应用/清除、保护范围分段选择器、两个保护开关及状态文案；默认范围为“仅全屏（推荐）”，未提供第二槽默认键、预设列表或修饰键菜单。macOS 显示未实现能力，不注册虚假服务或恒定 Normal 空实现。
自动验证：
  - dotnet restore SnapBoard.slnx --locked-mode 通过；dotnet format --verify-no-changes 通过；Release build 0 警告、0 错误。
  - 当前全量 399 项：380 项通过、19 项按平台/外部服务条件跳过、0 项失败；Windows Platform 88/88，Desktop Headless 89/89。
  - 双击状态机、双 RegisterHotKey 来源/冲突/清除/回滚、MOD_NOREPEAT 长按、HKCU 持久化/重启、五态前台检测、多显示器/自身排除、默认 Maximized 放行与严格范围保护、样式查询失败返回 Unknown 并默认放行、ReadAsync 前保护、SQLite/Blob/Outbox 零增长、全部暂停组合及显式入口放行均有专项断言。
  - SettingsWindow Headless 覆盖精确标题、默认未设置、默认“仅全屏（推荐）”、无预设/修饰键菜单、两个默认开启的 settings-toggle、滚动布局和状态文案。
Windows 实机验证：
  - Native AOT 隔离实例中打开设置页，目视确认 702 x 752 窗口沿用现有风格、滚动布局、第二槽默认“未设置”、精确文案和两个默认开启开关；录入 Ctrl+Shift+K 后真实注册成功。
  - 两个 1920 x 1080 显示器上分别验证普通窗为 Normal、IsZoomed 最大化窗为 Maximized、无边框窗和动态全屏窗为 FullScreen，SnapBoard 自身窗返回 IsSnapBoard；第二显示器全屏保护命中。
  - 普通窗中第一下 Double 不打开、第二下只打开一个快速窗口；默认范围下最大化前台允许 Primary/Double，整屏全屏抑制；严格范围下最大化也抑制；全屏下 --quick 仍打开；连续重复 KeyDown 模拟长按不能构成第二次，释放并重新完整按两次后正常打开。
  - 原生状态探针以隔离 Chrome 验证 Maximized、默认范围放行且严格范围保护；以本地生成的 ffplay 测试画面验证 FullScreen 在默认范围保护。隔离进程已停止，探针目录仍位于系统 Temp，未进入仓库且未读取现有浏览器会话。
  - 手动暂停状态经真实 Windows 前台检测器进入和离开全屏后仍保留，自动保护只增删 ForegroundProtection；清除 Manual 后读取恢复。AOT 进程退出并以同一隔离数据根重启后，已保存的 Double 重新注册并再次通过完整探针。
Native AOT：
  - win-x64 self-contained PublishAot 通过，0 个未解释 trim/AOT 警告。
  - SnapBoard.Desktop.exe 40,080,384 字节，SHA-256 D6E536B26DA493E95E8DB7111F6DDC90F4E1167AE6489CA7DCC78E960591E76D；可用全新隔离数据目录启动完整“闪剪”主窗口并保持响应，既有单实例命令、退出和重启探针继续通过。
  - SnapBoard.StorageMigrator.exe 4,513,280 字节，SHA-256 6BA410FDE682FA95ADC136D59439E08DB1FBEDD2B9EA088AE2BCFC0654711FCA，保持独立 AOT；无参数退出码 4，发布目录中没有对应 .dll、.deps.json 或 .runtimeconfig.json。
限制：
  - 长按通过 Windows 注入的按下/重复/释放序列验证，不等同于人工按住物理键盘；手动暂停组合通过真实协调器和真实前台窗口切换验证，不等同于人工点击主窗口按钮。对应 UI 命令另由 Headless 测试覆盖。
  - 本阶段未实现或宣称 macOS 原生两槽注册、前台全屏检测或保护成功；整个跨平台功能仍保持未完成。
```

## 26. 2026-07-29 执行记录：macOS 来源应用与系统身份修正

```text
日期：2026-07-29
阶段/任务：修复 macOS 历史来源应用、Dock 名称和应用菜单名称
状态：[x] 代码、自动测试、真实 TextEdit、新版 .app 身份和本机 arm64 Native AOT 包通过；[~] 多应用矩阵与长期资源仍待实际条件
开发基线：15c7f01（开发前已同步 main / origin/main）
分支：codex/fix-macos-identity-and-source
实现内容：
  - 未被自写抑制的 NSPasteboard changeCount 变化只额外快照一次前台 PID；读取相同序列时通过 NSRunningApplication 解析本地化名称和可执行路径，归属依据明确保存为 ForegroundWindowAtChange。
  - NSPasteboard 不可靠提供 clipboard owner；后台脚本、快速切换、PID 失效或序列不匹配继续 Unknown，不以当前前台应用回填旧记录。
  - .app 来源图标通过 NSWorkspace 在平台主线程提取并固定光栅化为 32 x 32 BGRA；256 项有界缓存避免列表刷新重复原生解析，空图标不缓存。
  - 启动前设置 macOS 进程显示名并阻止 Avalonia 恢复默认名；框架完成菜单构建后把应用菜单首项与子菜单标题设为“闪剪”。内部程序集名 SnapBoard.Desktop 和包内可执行文件 SnapBoard 保持不变。
实际验证：
  - 真实 TextEdit 新建测试文稿并复制生成文本，历史列表与详情显示“文本编辑”和原生 TextEdit 图标；无重叠或异常占位图。
  - 裸开发进程的应用菜单辅助功能树显示“闪剪”。正式 SnapBoard.app 的窗口标题、应用菜单、CFBundleDisplayName、CFBundleName 和 NSRunningApplication.localizedName 均为“闪剪”，Bundle ID 为 com.wuliangtdi.snapboard；Dock/应用切换器读取该运行时名称。
  - 裸 dotnet run 不属于 App Bundle，Dock 仍可能使用内部可执行文件名 SnapBoard.Desktop；正式支持路径必须从 .app 启动。
自动与发布验证：
  - macOS 平台测试 53/53，Desktop Headless 89/89；来源 PID、缺失 PID、序列门控、Unknown 降级、Bundle 路径、原生图标像素和缓存复用均有断言。
  - Release build 0 警告/0 错误；全量 403 项中 380 项通过、22 项 Windows 原生测试及 1 项真实 WebDAV 测试按当前条件跳过、0 项失败；macOS 平台专项 53/53、Desktop Headless 89/89。format 与 diff check 通过。
  - osx-arm64 Native AOT App/DMG/PKG 已从当前代码重新生成；主程序 35,862,528 字节、SHA-256 77cb788aaeb26f15cb0e92eafd081bed1d5ec16ab0c442cd9ef1102e84e4407c，为 arm64 Mach-O；codesign --deep --strict 通过，0 个 trim/AOT 分析告警，仍只有 2 个既有且已解释的 .NET Apple 静态库 clang module-cache 调试信息告警。
性能与内存：
  - 前台 PID 查询只发生在实际 changeCount 变化上，不进入空闲轮询；来源名称/路径只在序列匹配的正文读取时解析。图标固定为 4 KiB/项，缓存上限 256 项，像素上限约 1 MiB。
  - 三轮最终 AOT 5 秒窗口 + 3 秒后台短测：窗口 Physical 205.59/205.39/205.86 MiB，后台 106.47/106.25/106.39 MiB，平均 CPU 0.024%/0.023%/0.023%，FD 均为 45；后台超过 100 MiB 失败线，内存门槛未通过。
  - Native AOT 平台探针预热 10,000 次并计量 10,000 次：1965.02 ms，写入/读取/标记/反馈/丢弃均 0 失败，Physical 13.63 -> 17.64 MiB（+4.02 MiB），FD 7 -> 7。该同适配器自写探针不经过外部来源 PID 路径；真实 TextEdit 和缓存测试覆盖该增量能力。
  - 短测和平台探针不替代既有 10 分钟、8 小时或完整外部应用压力门槛，也不改变完整 UI 后台内存仍未达到 <=80 MB 目标的结论。完整口径见 docs/PERFORMANCE.md 6.12。
视觉复核：
  - 最终 arm64 SnapBoard.app 的实际截图无重叠或溢出，新记录显示“文本编辑”及原生图标；窗口与应用菜单均为“闪剪”。独立设计评估结论 PASS，无阻塞视觉问题。
限制：
  - TextEdit 已形成真实证据；Finder、浏览器、截图工具、后台 CLI、复制后立即切换应用以及旧历史回填不宣称全部准确。来源字段是最佳努力前台归因，不是 NSPasteboard owner。
  - 当前包仍为 ad-hoc 签名、PKG 未签名且未公证；Developer ID、Gatekeeper 正式接受、Intel Runner、多显示器/Retina、物理睡眠/断网和 8 小时长稳继续待实际条件。
```

## 27. 2026-07-29 执行记录：Double 单键录入与全局滚动条主题

```text
日期：2026-07-29
阶段/任务：修正 Windows Double 槽录入规则并统一桌面滚动条视觉
状态：[x] Windows 与共享语义、自动验证、渲染验证和 win-x64 Native AOT 完成；[ ] macOS 原生双槽阶段仍待实施
开发基线：e13c2723dc309abb73e957fbbcccdc79f30d81fa（开发前已同步 main / origin/main）
实现内容：
  - ITwoSlotGlobalHotKeyService 增加按槽位创建手势的共享语义；Primary 保留至少一个平台修饰键的要求，Double 允许单个受支持的非修饰主键，或可选修饰键与一个主键组成的组合键。
  - Windows 裸键 Double 仍由 RegisterHotKey 注册并强制包含 MOD_NOREPEAT；没有引入全局键盘 Hook，也不支持需要监听全部输入的 A+B 等多个普通主键同时组合。
  - HKCU 当前格式版本仍为 2，序列化字段结构没有变化；Primary 与 Double 使用各自校验规则，裸键 Double 可持久化并在重启后恢复，不增加任何旧格式兼容或迁移代码。
  - SettingsViewModel 按槽位调用平台创建语义；Double 录入提示改为“请按下一个按键或组合键，Esc 取消”，辅助文案明确单次快捷键需要修饰键、连按两次快捷键可使用单个按键。
  - App 级 Avalonia 主题统一覆盖主窗口、快速窗口、设置页和其他滚动区域：10 px 轨道、圆角滑块、现有中性色与强调色状态，并移除 Fluent 原生上下/左右箭头。
自动与视觉验证：
  - dotnet format SnapBoard.slnx --verify-no-changes --no-restore 通过；Release build 0 警告、0 错误。
  - 全量 406 项：384 项通过、22 项按当前平台或外部服务条件跳过、0 项失败；Windows Platform 89/89，Desktop Headless 91/91。
  - 新增裸键 Double 创建、MOD_NOREPEAT、原生双槽注册、本机设置重启恢复、ViewModel 录入/应用，以及快速窗口 10 px、无箭头、圆角滑块的确定性断言；Primary 无修饰键拒绝测试继续通过。
  - Headless 真实 Skia 截图确认快速窗口与设置页显示细圆角主题滑块且无箭头；主窗口共用同一 App 级主题。win-x64 AOT 隔离实例已启动完整“闪剪”主窗口并保持响应。
Native AOT：
  - win-x64 self-contained PublishAot 最终通过，0 个未解释 trim/AOT 警告。
  - SnapBoard.Desktop.exe 40,379,904 字节，SHA-256 A320164453B07CF7721EC3306208E167C5D8B417FA6A4B41AB0EB70E8643C4FF。
  - SnapBoard.StorageMigrator.exe 4,513,280 字节，SHA-256 4F87768D5E4543F7EFEA31999593A87BB8F2FE8E040846C51B9DB3E26C378729，保持独立 AOT；发布目录中没有对应 .dll、.deps.json 或 .runtimeconfig.json。
限制：
  - Windows/macOS 原生热键 API 均以一个非修饰主键为注册单位；多个普通主键同时组成的 A+B 式全局组合不在范围内，以保持“不监听所有用户键盘输入”的约束。
  - 本次没有实现或宣称 macOS 原生两槽快捷键、前台全屏检测或保护成功；整个跨平台功能仍保持未完成。
```

## 28. 2026-07-29 执行记录：Windows 两槽任意单键与修饰键录入

```text
日期：2026-07-29
阶段/任务：按用户确认放开 Windows Primary/Double 两槽录入限制
状态：[x] Windows 与共享语义、自动验证和 win-x64 Native AOT 完成；[ ] macOS 原生双槽阶段仍待实施
开发基线：659f8233f56e17817d432e0dc5bfda3cc90266a8（开发前 main 与 origin/main 一致）
后续纠正：本节当时只验证 RegisterHotKey 返回成功，没有验证 WM_HOTKEY 实际投递；主修饰键标志被移除会形成“保存成功但永不触发”的死绑定，最终修复与完整证据见第 29 节。
实现内容：
  - Windows Primary 和 Double 统一允许普通单键、单个 Ctrl/Alt/Shift/Win、只含修饰键的组合，以及修饰键加普通主键；现有 Primary 默认值保持不变，Double 仍默认未设置。
  - 设置页在修饰键 KeyUp 时完成单键/纯修饰键组合录入；若松开前继续按普通键，则录入常规组合。两个录入器统一提示“请按下一个按键或组合键，Esc 取消”，辅助文案不再要求 Primary 必须包含修饰键。
  - 当时错误地从 RegisterHotKey 标志中移除了主虚拟键对应的修饰标志；第 29 节已改为注册时保留、仅在显示名称中去重。所有 Windows 绑定仍强制 MOD_NOREPEAT。
  - Windows 当前 HKCU 持久化格式继续使用版本 2，序列化结构未变化；校验规则改为两槽一致，不增加旧格式兼容、迁移或字段补救代码，也不生成同步事件。
  - 没有引入全局键盘 Hook 或监听全部输入。RegisterHotKey 无法表达的 A+B 式多个普通主键同时组合仍不支持。
自动验证：
  - dotnet restore --locked-mode、dotnet format --verify-no-changes、Release build 均通过；build 为 0 警告、0 错误。
  - 全量 417 项：395 项通过、22 项按当前平台或外部服务条件跳过、0 项失败；Windows Platform 97/97，Desktop Headless 94/94。
  - Windows 快捷键/本机设置定向用例 35/35，Headless 快捷键录入用例 20/20；覆盖普通单键、单修饰键、纯修饰键组合、常规组合、两槽 MOD_NOREPEAT、持久化重启恢复和精确界面文案。
  - Windows 实机当时只确认 VK_SHIFT、VK_CONTROL、VK_MENU、VK_LWIN、VK_RWIN 在无用户修饰标志时 RegisterHotKey 返回成功并可注销；随后证明这种注册不会投递 WM_HOTKEY，因此该条不能作为功能通过证据。
Native AOT：
  - win-x64 self-contained PublishAot 通过，0 个未解释 trim/AOT 警告。
  - SnapBoard.Desktop.exe 40,385,536 字节，SHA-256 683DE41E5C24608019BFC32F2007467D9F26CC8E926C92BAAE9E2749AE22CAFA。
  - SnapBoard.StorageMigrator.exe 4,513,280 字节，SHA-256 5B6A6F441EE97C08E74FF98B7E685998B1637535A7FC5CB816AC33E83C8AFB98，保持独立 AOT；发布目录中没有对应 .dll、.deps.json 或 .runtimeconfig.json。
限制：
  - 纯修饰键组合遵循录入顺序，最后按下的键是原生触发主键；被 Windows 保留或已由其他程序注册的按键仍会按既有冲突路径拒绝，并保留原两槽状态。
  - 多个普通主键同时组成的 A+B 式全局组合需要监听全部键盘输入，不在本功能范围内。
  - 本次没有实现或宣称 macOS 原生两槽快捷键、前台全屏检测或保护成功；整个跨平台功能仍保持未完成。
```

## 29. 2026-07-29 执行记录：Windows 修饰键主键原生投递修复

```text
日期：2026-07-29
阶段/任务：完整修复 Ctrl/Alt/Shift/Win 作为 Windows 两槽主键时保存成功但不触发的问题
状态：[x] Windows 原生注册、持久化、录入语义、自动验证和 win-x64 Native AOT 完成；[ ] macOS 原生双槽阶段仍待实施
开发基线：fa6765de6fc6ed3761285727a1415d955d2d6e73（开发前 main 与 origin/main 一致）
根因与修复：
  - 旧实现为了避免显示名称重复，从 RegisterHotKey 的 fsModifiers 中删除了主虚拟键对应的修饰标志。例如 Alt 被保存为 MOD_NOREPEAT + VK_MENU；Windows 会接受注册，但实际按 Alt 不投递 WM_HOTKEY。
  - 当前实现将“原生注册标志”和“显示名称去重”分开：Ctrl/Alt/Shift/左或右 Win 作为主键时，同时保留对应 MOD_CONTROL/MOD_ALT/MOD_SHIFT/MOD_WIN、MOD_NOREPEAT 和原始 vk；只在界面显示文本中去掉重复修饰键名称。
  - 本机设置当前格式版本升为 3。版本 1、2、未知版本或缺少主键必需修饰标志的配置整组拒绝并重置为版本 3 默认值；按已确认策略不读取、转换、迁移或字段级补救旧配置，也不生成同步事件。
  - 仍只使用 RegisterHotKey，没有增加全局键盘 Hook，也不监听其他用户键盘输入；普通单键、单个修饰键、纯修饰键组合和常规组合继续由同一两槽模型处理。
自动与原生验证：
  - dotnet restore --locked-mode、dotnet format --verify-no-changes 和 Release build 均通过；build 为 0 警告、0 错误。
  - 全量 421 项：399 项通过、22 项按当前平台或外部服务条件跳过、0 项失败；Windows Platform 101/101，Desktop Headless 94/94。
  - Windows 原生 message-only window 分别注册并真实收到 LeftCtrl、LeftAlt、LeftShift、LeftWin、RightWin 和 Ctrl+Shift 的 WM_HOTKEY；不再把 RegisterHotKey 返回成功当作投递证据。
  - 原生 Alt 长按序列包含初次 KeyDown 和多次重复 KeyDown，只产生一次触发；KeyUp 后再次按下才产生第二次触发，证明 MOD_NOREPEAT 对修饰键主键同样有效。
  - 平台测试覆盖无效旧表示拒绝、两槽持久化重启恢复和正确注册标志；Headless 覆盖单修饰键与纯修饰键组合的录入、应用及显示去重。既有双击时序、冲突原子回滚、全屏保护、记录前置保护和暂停原因组合测试继续全量通过。
Native AOT：
  - win-x64 self-contained PublishAot 通过，0 个未解释 trim/AOT 警告。
  - SnapBoard.Desktop.exe 40,386,048 字节，SHA-256 92111BD9FC7BBF62EAC4CB5D92881C49C0AD070B4ED2B5C06A7947E01F8E4589。
  - SnapBoard.StorageMigrator.exe 4,513,280 字节，SHA-256 EFF6E6B01A8C6B9463759C960EAAD889EEB538D566192309526E837FC8104485，保持独立 AOT；发布目录中没有对应 .dll、.deps.json 或 .runtimeconfig.json。
限制：
  - 原生消息和长按由 Windows 输入注入驱动真实 RegisterHotKey 消息窗口，不替代不同物理键盘驱动的人工长按复核；新 AOT 实例已留给当前 Windows 用户直接验收。
  - 多个普通主键同时组成的 A+B 式组合仍无法由 RegisterHotKey 表达，且根据“不监听全部键盘输入”的安全边界不以全局 Hook 实现。
  - 本次没有实现或宣称 macOS 原生两槽快捷键、前台全屏检测或保护成功；整个跨平台功能仍保持未完成。
```

## 30. 2026-07-30 执行记录：macOS 快速窗口双击快捷键与全屏保护

```text
日期：2026-07-30
阶段/任务：docs/QUICK_WINDOW_SHORTCUT_AND_FULLSCREEN_REQUIREMENTS.md 13.4 阶段 B，macOS 追平
状态：[~] macOS 原生实现、自动测试、当前 M4 实机窗口矩阵和双架构 AOT 已完成；[ ] 物理键盘长按、物理多显示器、Retina 与应用内按钮实机证据仍待匹配条件，整个跨平台功能不标记完成
开发基线：541712d8d82b0a6bf62e61d42700589732fbd61f（开发前 main 与 origin/main 一致）
分支：codex/macos-quick-window-fullscreen-protection
环境：macOS 26.2 (25C56)，Apple M4 arm64，单台 1920 x 1080、backingScaleFactor=1 的非 Retina 显示器，.NET SDK 10.0.302

实现内容：
  - MacOSGlobalHotKeyService 实现 ITwoSlotGlobalHotKeyService，继续提供旧单槽接口兼容；读取 NSEvent.doubleClickInterval 并复用共享 QuickWindowHotKeyController/DoubleHotKeyPressStateMachine，没有新增 macOS 业务状态机。
  - MacOSHotKeyRegistrar 使用签名固定且 ID=1/2 的 Primary/Double Carbon 注册，按来源发布 press；release 只维护对应槽 held 状态，后续未释放 press 标记为 repeat。冲突、重复组合、清除、先注册新键再释放旧键和失败回滚均由原生边界测试覆盖，只监听两个注册 ID，不使用全局键盘 Hook。
  - MacOSDesktopLocalSettingsService 通过 NSUserDefaults 当前格式版本 1 整组保存 PrimaryHotKey、DoubleHotKey、ForegroundProtectionScope、DisableHotKeysWhenProtected 和 PauseClipboardCaptureWhenProtected；Primary 默认 Command+Shift+V，Double 默认空，范围默认仅全屏，两个保护开关默认开启。版本键最后提交；不读取、迁移或字段补救 GlobalHotKeyV1 及其他开发期表示。
  - MacOSForegroundWindowStateService 通过 NSWorkspace 前台 PID、无弹窗 AXIsProcessTrusted、AX focused/main window 位置/尺寸/AXFullScreen/可用时的 AXZoomed、CGWindow 元数据及 NSScreen frame/visibleFrame 判定 Normal、Maximized、FullScreen、Unknown、Unavailable。使用点坐标和当前窗口最大交叠显示器，支持负坐标与 backingScaleFactor，不申请 Screen Recording，不读取窗口标题、游戏名、文档路径或剪贴板正文，并在权限/原生失败时 Unknown/Unavailable 默认放行。
  - macOS 组合根注册同一份本机设置、两槽快捷键与前台服务；生命周期启动两槽，所有热键来源进入共享控制器，保护只拦截 Primary/Double。菜单栏、MainViewModel 显式命令和单实例 --quick 继续调用 ShowExplicitly。
  - ClipboardCaptureCoordinator 在 IClipboardContentReader.ReadAsync 前查询同一前台保护服务；Manual、ForegroundProtection、StorageMigration、UpdateInstallation 继续使用独立位。菜单栏新增只读状态行和 tooltip，区分“用户已暂停记录”“全屏保护中，暂不记录”“内部维护中，暂不记录”。
  - Windows 平台项目没有代码改动；整个解决方案和 Windows 平台测试保持通过。

自动验证：
  - git fetch/switch/pull/rev-parse 和功能分支创建按要求执行；基线精确为 541712d8d82b0a6bf62e61d42700589732fbd61f。
  - dotnet restore SnapBoard.slnx --locked-mode 通过；dotnet format SnapBoard.slnx --no-restore 与 --verify-no-changes 通过；Release build 0 警告、0 错误。
  - 全量 459 项：434 项通过、25 项按平台/外部服务条件跳过、0 项失败。分项目为 Application 17/17、Architecture 2/2、Sync.WebDav 38/38、Linux 1/1、Windows 77 通过/24 跳过、Domain 4/4、Update 16/16、Desktop Headless 102/102、Infrastructure 94 通过/1 跳过、macOS Platform 83/83。
  - 第一次全量运行中，一个未修改的共享 Headless 用例曾触发 5 秒瞬时超时；该用例定向复跑和 Desktop Headless 102/102 随即通过，之后两次完整解决方案运行均为上述 434 通过/25 跳过/0 失败，未为掩盖该次超时修改产品代码或放宽断言。
  - macOS Platform 覆盖两槽 ID/来源、冲突/清除/回滚、press/release repeat 标记、普通键与修饰键主键映射、NSUserDefaults 默认/当前格式/整组拒绝/旧键不读取、五态窗口分类、负坐标多显示器、scale=2、权限/原生失败及自身排除。
  - Desktop Headless 覆盖 Primary/Double 时序、repeat 不能完成第二次、默认最大化放行/严格范围拦截、进入保护清理待定 Double、菜单/应用命令/--quick 显式放行、手动暂停与前台/内部原因组合；共享 ClipboardCaptureCoordinator 测试继续证明保护在 ReadAsync 前生效。
  - git diff --check 通过；源代码审计未出现 AXTitle、kCGWindowName、CGRequestScreenCaptureAccess、CGPreflightScreenCaptureAccess、CGEventTap、键盘 Hook 或 Windows 平台文件差异。

真实 macOS 窗口与权限验证：
  - 受信任进程中 AXIsProcessTrusted=True。真实 TextEdit 普通窗口返回 Normal；zoomed 后 AX/CG 边界为 0,30,1920,975，匹配 visibleFrame 并返回 Maximized；原生全屏 Space 边界为 0,0,1920,1080、AXFullScreen=True，返回 FullScreen。
  - 使用直接调用生产 MacOSForegroundWindowStateService 的临时探针完成三轮完整多 Space 往返：TextEdit 原生全屏 Space 始终返回 FullScreen、WindowId=2536、PID=79477；实际激活另一个普通桌面 Space 上的 ChatGPT 后始终返回 Normal、WindowId=4106、PID=30536；每轮返回 TextEdit 后仍为同一 FullScreen 窗口，最终退出全屏时 AXFullScreen=False、边界 210,77,586,488。
  - 临时原生 AppKit 无边框窗口覆盖当前显示器 0,0,1920,1080，AXFullScreen=False，几何兜底返回 FullScreen；真实 QuickTime 本地全屏视频为 0,0,1920,1080、AXFullScreen=True，返回 FullScreen。
  - 独立未获辅助功能授权的临时 .app 身份返回 AXIsProcessTrusted=False，服务返回 Unknown/AccessibilityPermissionDenied；没有调用授权请求 API。SnapBoard 自身 PID 在权限查询前排除。
  - 生产 MacOSGlobalHotKeyService 真实 Carbon 注册 Option+Control+V 返回 Registered/OSStatus 0；使用 Carbon CreateEvent/SetEventParameter/SendEventToEventTarget 向已安装处理器投递 Pressed、Pressed、Pressed、Released、Pressed、Released，回调得到 repeat 序列 false、true、true、false，共享 QuickWindowHotKeyController 的 Shows=1。两次 repeat 均未完成 Double 的第二次触发，只有 release 后的新 press 完成；该证据覆盖原生 Carbon 回调边界，但不替代物理键盘长按。
  - TextEdit 原生全屏期间，arm64 Native AOT 后台实例菜单栏 tooltip 和只读项实际显示“全屏保护中，暂不记录”。菜单栏“快速粘贴”和第二实例 --quick 均打开名为“闪剪”的快速窗口，辅助功能边界为 620,164、680 x 512；证明显式入口不受快捷键保护影响。
  - 同一保护状态下点击“暂停记录”后菜单项变为“恢复记录”；退出全屏并发生下一次 pasteboard changeCount 后，状态变为“用户已暂停记录”，没有被 ForegroundProtection 清除覆盖；点击恢复后变为“正在记录”。
  - 临时原生探针、未授权 .app、AOT 输出和全部隔离 bootstrap 已从工作区移走；本轮使用的 /tmp 与 /private/tmp 目录已移入废纸篓，可恢复且不进入仓库。

Native AOT：
  - osx-arm64 self-contained PublishAot 通过：SnapBoard.Desktop 36,169,776 字节，SHA-256 70708382D45B615D0A0D9779B0CEB2C1CDEEA8A59DCD03657E960C45F5B5CC06；SnapBoard.StorageMigrator 8,356,968 字节，SHA-256 92E43EAB7C4A70EB5B8609C43C9E289645B096E01AF8E3156BF34C838BD0A0C2。
  - osx-x64 同机交叉 PublishAot 与 Rosetta 预检通过：SnapBoard.Desktop 37,178,376 字节，SHA-256 F7F5E4AE7E435A826D87E68229F9A371C2D15376DB01EACB58AA37D1D2D6B301；SnapBoard.StorageMigrator 8,554,488 字节，SHA-256 A687B6898CD49CF9D1E0AD9A2AC317A4FE378B743C1BCD6A4CA229C73FB5798C。
  - scripts/macos/Verify-NativePublish.sh 对两个 RID 均通过：主程序/helper 分别为 arm64 与 x86_64 Mach-O，helper 无参数退出码 4；发布目录没有 CoreCLR/hostfxr 或 helper 的 dll/deps/runtimeconfig。两个 RID 的主程序均以私有 0700 bootstrap 冷启动，第二实例 --exit 与主实例退出均返回 0。
  - 两个 RID 都是 0 个 trim/AOT 分析告警；每个 RID 仍有 2 条已解释的 .NET 10.0.10 Apple NativeAOT 静态库 clang module-cache 调试信息告警，未 suppression，不是本次代码引入的裁剪/AOT 告警。

未覆盖与边界：
  - 当前会话没有物理键盘设备：hidutil 的 Generic Desktop/Keyboard 匹配为空，IORegistry 没有 IOHIDKeyboard 或 AppleHIDKeyboardEventDriverV2。平台边界、Headless 和上述 Carbon EventRef 链已证明 repeat 不会完成 Double；System Events/CGEvent 和私有 HID event dispatch 的正向对照均为 0。IOHIDUserDevice 官方 SDK 头文件要求受限 com.apple.developer.hid.virtual.device entitlement，未授权创建返回 NULL，伪造 entitlement 的 ad-hoc 二进制被 AMFI 拒绝，因此不把合成尝试写成真实长按通过。物理长按仍是完成定义缺口。
  - 测试开始时主机只有一台产品标识为 virt、scale=1 的 1920 x 1080 非 Retina 虚拟显示器。负坐标、当前窗口所在显示器选择和 scale=2 由平台测试覆盖，不能外推为物理多显示器/Retina 实机通过。一次隔离 CGVirtualDisplay 探索曾让 WindowServer 暂时报告两个 2560 x 1440、逻辑 1280 x 720、scale=2 的 NSScreen，但生产服务当次返回 Unknown/BoundsUnavailable，故不计入通过；headless WindowServer 未在探针退出后移除这两个临时显示器，当前已把它们镜像并恢复为单一逻辑 1920 x 1080、scale=1，彻底移除仍需经用户授权重登或重启 WindowServer。
  - 菜单栏和 --quick 已在真实 AOT 保护期通过；MainViewModel 应用内显式命令由 Headless 直接执行，当前没有形成应用内按钮实机点击证据。Safari/Chrome、真实游戏和 Intel 匹配硬件/Runner 也未在本轮覆盖。
  - 手动暂停组合实测中，为推进 pasteboard changeCount 而尝试原样回写 NSPasteboardItem，AppKit 在 clearContents 后拒绝复用旧 item；当时系统剪贴板内容被清空且测试进程无法恢复。该副作用不影响仓库、数据库或产品实现，但已向当前用户明确说明，后续不再采用该方法。
  - 因上述真实输入/硬件/UI 缺口，阶段 B 代码可合入并供后续匹配环境验收，但不满足文档的整个跨平台完成定义，PLAN.md 与本节均保持 [~]。
```

## 31. 2026-07-30 执行记录：macOS 纯修饰键快捷键与应用内入口修复

```text
日期：2026-07-30
阶段/任务：补齐阶段 B 实机验收发现的纯 Option 不触发和应用内快速窗口入口缺口
状态：[x] 代码、自动测试、前后台原生事件、arm64/x64 Native AOT 与应用内按钮实点完成；[ ] 物理键盘长按、物理多显示器和 Retina 仍按第 30 节保留为完成定义缺口
开发基线：687febdca117e22b389a665ec9cda0e144b21f28（开发前 main 与 origin/main 一致，且包含第 30 节阶段 B 实现）
分支：codex/macos-modifier-only-hotkey-fix
用户配置：NSUserDefaults DoubleHotKey=16385|58|Option

根因与实现：
  - 设置录入、NSUserDefaults 校验和两槽共享状态机都已接受纯 Option；实际缺陷在 MacOSHotKeyRegistrar：Carbon RegisterEventHotKey 可以返回成功，但单独按下/松开修饰键不会稳定产生 HotKeyPressed/Released，因此“保存成功”不能证明物理事件可用。
  - 普通主键继续使用签名固定、ID=1/2 的两组 Carbon Hot Key。修饰键作为主键时，Carbon 注册仍保留对应槽和冲突语义，但从 Carbon modifier mask 去掉主修饰键本身；实际完整触发由状态变化边沿补齐。
  - SnapBoard 活动时仅安装 AppKit 本地 flagsChanged monitor，非活动时仅安装 Carbon kEventRawKeyModifiersChanged monitor；回调只查询已配置修饰键及四组平台修饰键当前状态。没有订阅 RawKeyDown、RawKeyUp、RawKeyRepeat、普通键、字符或文本，没有使用 CGEventTap/全局键盘 Hook，也没有新增持续 Timer 轮询。
  - 每槽分别维护 held/armed。只有主修饰键从未按下变为按下、其他修饰键与配置精确匹配并再次松开，才发布一次 IsRepeat=false；长按和重复状态变化不重复发布，额外修饰键拒绝，Primary/Double 重叠组合仍按来源隔离，清除槽会清空边沿状态。
  - AppKit block 使用显式原生布局和 UnmanagedCallersOnly 回调，返回原 NSEvent，不消费或改写应用键盘流；Carbon modifier 回调返回 eventNotHandledErr。处理器、block descriptor、GCHandle 和两个 Carbon 注册均在主线程生命周期内释放，JIT 正常退出和两架构 AOT 均已通过。
  - 主窗口新增可见 WindowOpen 图标按钮，直接绑定既有 MainViewModel.OpenQuickWindowCommand；没有新增第二套窗口打开流程。Headless 用例验证按钮命令和“打开快速窗口”提示，arm64 AOT 主窗口实际点击验证窗口数从 1 增至 2。

自动验证：
  - dotnet restore SnapBoard.slnx --locked-mode、dotnet format --verify-no-changes、Release build 全部通过；build 为 0 警告、0 错误。
  - 全量 468 项：443 项通过、25 项按当前平台/外部服务条件跳过、0 项失败。分项目为 Application 17/17、Architecture 2/2、Domain 4/4、Linux 1/1、Windows 77 通过/24 跳过、Update 16/16、Sync.WebDav 38/38、Infrastructure 94 通过/1 跳过、Desktop Headless 103/103、macOS Platform 91/91。
  - macOS 新增测试覆盖主修饰键不重复进入 Carbon mask、完整按下/松开只发布一次、组合顺序、额外修饰键拒绝、两槽重叠来源隔离和清除槽；桌面新增测试覆盖可见按钮绑定显式应用命令。
  - Windows 生产项目无代码差异，Windows Platform 既有 77 项在 macOS 通过、24 项仅因目标平台跳过；整个解决方案依赖边界保持成立。

真实 macOS 与 AOT 证据：
  - Release JIT 中，SnapBoard 前台和后台均得到窗口数 1 -> 1 -> 2：第一次完整 Option 只进入等待，第二次才创建一个快速窗口。长按 Option 1 秒期间、松开后超过双击时间以及连续两次 Control+Option 后均保持 1 个窗口；说明系统重复和不匹配组合不会完成 Double。
  - 同一 JIT 进程运行约 2 分钟后的 ps 空闲样本为 0.0% CPU；实现没有此前 8 ms Timer 方案的持续约 1.5%-2.4% 空转开销。正常 Command+Q 退出成功，没有 block/Carbon 回调释放崩溃。
  - osx-arm64 Native AOT 后台纯 Option 同样为 1 -> 1 -> 2，快速窗口为 680 x 512；关闭后从真实主窗口点击新增按钮，窗口数从 1 变为 2。该按钮、菜单栏和 --quick 均继续走显式 ShowExplicitly 路径，不受全局快捷键保护拦截。
  - osx-x64 Native AOT 在 Rosetta 中前台和后台纯 Option 均为 1 -> 1 -> 2，并可正常退出。当前证据验证 x64 产物在 Apple Silicon/Rosetta 上运行，不等同 Intel 物理机验收。

Native AOT：
  - osx-arm64 Desktop 36,169,856 字节，SHA-256 4F11DD7A2B531F8EC1AC5557849059D506EB9AEC0AF6BF00451BD42ED804CEA4；独立 StorageMigrator 8,356,968 字节，SHA-256 D164169CD5793E9006E597F64D888303DB34CFF13D56FDC75538FAE39769F326。
  - osx-x64 Desktop 37,186,640 字节，SHA-256 4C90381E606BF36DB8EA63E1A65007A3BFE3E62245715344301667D8F2E190E9；独立 StorageMigrator 8,554,488 字节，SHA-256 404D19CD043EDD8AE66288323F8207025C8BB7D0A03733EDBC060E979999107C。
  - scripts/macos/Verify-NativePublish.sh 对两个 RID 均通过：主程序/helper 为正确 arm64/x86_64 Mach-O，helper 无参数退出码 4，发布目录没有 CoreCLR、hostfxr、deps.json 或 runtimeconfig.json。
  - 两个 RID 均为 0 个 trim/AOT 分析告警。每个 Desktop 发布仍有 2 条已解释的 .NET 10.0.10 Apple NativeAOT 静态库 clang module-cache 调试信息告警；没有 suppression，也不是本次互操作代码产生的 trim/AOT 告警。

剩余限制：
  - 当前环境没有可用于人工验收的物理键盘设备；本节 Option、长按和组合均由 System Events 产生完整系统按下/松开事件，足以验证真实 AppKit/Carbon/AOT 回调链，但不冒充不同 HID 驱动下的物理长按证据。
  - 物理多显示器、Retina、真实游戏和 Intel 物理机仍未新增证据，继续沿用第 30 节限制；因此整个跨平台功能在 PLAN.md 中保持 [~]，不提前标记完成。
  - data/、凭据、临时数据库、AOT 输出和 docs/MACOS_PARITY_IMPLEMENTATION_CHECKLIST.md 均不进入提交。
```

## 32. 2026-07-30 执行记录：Linux CI 暂停与 macOS Intel 时序修复

```text
日期：2026-07-30
阶段/任务：处理 GitHub Actions 30509956040 的 Ubuntu 与 macos-15-intel 失败
状态：[x] Linux CI 入口按用户要求暂时注释；[x] Intel 两个时序失败已修复；[x] GitHub macos-15-intel Runner 复核通过
根因与实现：
  - Ubuntu 的 Infrastructure 图片测试缺少 libSkiaSharp.so；SnapBoard.Infrastructure 的锁文件只有 Win32/macOS 原生资产。按当前范围不补 Linux 包，CI 中 ubuntu-latest Build/Test、linux-x64 Native AOT 和 Release Linux 产品包均保留为注释，Linux 不记为验证通过。
  - MacOSSingleInstanceCoordinator.StartListening 原先通过 Task.Run 延迟启动 AcceptAsync；慢 Intel Runner 上第二实例可能在服务循环被调度前耗尽短重试。现直接启动异步 accept 循环，使 StartListening 返回时监听已进入等待状态，仍保持异步、可取消和有界通知协议。
  - HistoryChangeBurstIsCoalescedIntoOneReload 原先固定等待 300 ms；Intel Runner 高负载下 timer/UI 队列尚未完成。测试现等待第二次查询实际发生，再调用 WaitForIdleAsync 并保留“总计只能两次查询”的严格断言。
验证：
  - macOS Platform Release 测试 91/91；RuntimeMainViewModelTests 7/7。
  - SecondaryInstanceNotifiesPrimaryWithBoundedCommand 连续 12 轮通过；HistoryChangeBurstIsCoalescedIntoOneReload 连续 12 轮通过。
  - macOS 平台项目 Release build 0 警告、0 错误；完整解决方案 443 项通过、25 项按平台条件跳过、0 项失败。
  - 本机 `osx-x64` Native AOT 发布通过：主程序 37,186,632 字节（SHA-256 `1d4fdd3ef544a237d0464ad5d6cfdcd6bb17287835f82ec9fa1053d6e6268e0e`），独立 StorageMigrator 8,554,488 字节（SHA-256 `8d9e3c6cf9bd0115cab759da91d611696ad38be470dba9630007d66df2920320`）；两者均为 x86_64 Mach-O，helper 无参数退出码 4，发布目录无 CoreCLR/hostfxr。仅有 2 条已解释的 .NET Apple NativeAOT clang module-cache 调试信息警告，无未解释 trim/AOT 警告。
  - GitHub Actions [run 30511261556](https://github.com/wuliangtdi/SnapBoard/actions/runs/30511261556)（commit `912e660`）全绿：Windows/macOS ARM/Intel Build/Test 和 `win-x64`/`osx-arm64`/`osx-x64` Native AOT 均成功；macos-15-intel 共 468 项，其中 443 项通过、25 项按平台条件跳过，两个原失败用例均通过。
限制：
  - 本机为 Apple Silicon；GitHub macos-15-intel Runner 已通过自动测试，但仍不替代 Intel 实体机上的物理键盘、窗口和多显示器交互验收。
  - Linux 失败被显式暂停而非修复；恢复 Linux CI 前必须补齐并锁定 Linux Skia 原生资产，再重新执行 Build/Test 与 Native AOT。
```

## 33. 2026-07-30 执行记录：首次公开 Release 与更新签名密钥轮换

```text
日期：2026-07-30
阶段/任务：轮换遗失的更新签名私钥并发布首个公开版本 v0.1.0
状态：[x] 新密钥、客户端信任根、GitHub Secret、远端 CI、Release 打包和公开附件验证完成；[~] Apple/Windows 系统代码签名与已安装版本升级仍待正式凭据和旧版本环境
发布提交：1828f4da8629bb8df062a01910eb0d8dd8dfa023
标签：v0.1.0
Release：https://github.com/wuliangtdi/SnapBoard/releases/tag/v0.1.0

密钥轮换：
  - 原 P-256 私钥在仓库、GitHub Actions Secrets、本机 Keychain 和环境变量中均不存在，且不能从已提交公钥恢复；用户明确授权在首次公开 Release 前轮换。
  - 新私钥保存在仓库外的维护者专用目录，目录权限 0700、私钥权限 0600；只把私钥内容写入 GitHub Actions Secret SNAPBOARD_UPDATE_SIGNING_PRIVATE_KEY_PEM，没有提交、打印或复制到构建产物。
  - 新公钥 SubjectPublicKeyInfo SHA-256 为 25a66ae09889984b953aa3cb541384eafb18c796e10253ae75a5db5184a922a5。packaging PEM 与 UpdateEndpointOptions 客户端内置常量同步更新，并新增测试直接比较两者，避免发布端与客户端信任根再次漂移。
  - scripts/updates/Sign-UpdateFeeds.sh 使用新私钥完成本地签名和公钥验签；从公开 Release 下载的 win-x64、osx-arm64、osx-x64 三份 releases.*.json 及签名也全部 Verified OK。

本地验证：
  - dotnet restore SnapBoard.slnx --locked-mode、dotnet format SnapBoard.slnx --verify-no-changes --no-restore、Release build 均通过；build 为 0 警告、0 错误。
  - 全量 469 项：444 项通过、25 项按平台或外部服务条件跳过、0 项失败；Update 专项因新增公钥一致性测试增至 17/17。
  - git diff --check 和发布脚本的公私钥指纹匹配检查通过。

GitHub 验证：
  - CI run 30512399402 全绿：Windows、macOS ARM、macOS Intel Build/Test 以及 win-x64、osx-arm64、osx-x64 Native AOT 六个 job 均成功。
  - Release run 30512409589 全绿：osx-arm64 3m07s、osx-x64 3m48s、win-x64 4m10s；最终 job 成功下载汇总产物、签名并复验三个更新 feed、创建公开 Release。
  - Release 共 29 个附件。主要用户产物为 SnapBoard-win-x64.zip 78,990,783 字节、Windows Setup.exe 33,561,692 字节、osx-arm64 DMG 31,166,463 字节/PKG 27,846,681 字节、osx-x64 DMG 33,014,727 字节/PKG 29,596,647 字节；GitHub 为每个附件记录 SHA-256 digest。
  - 两套 macOS 包均包含 App/DMG/PKG、Velopack Portable.zip/full.nupkg 和架构独立 feed；Windows 包包含 Setup.exe、Portable.zip、full.nupkg、独立 Native AOT ZIP 和校验和。Linux 产品包按已确认范围未构建、未上传。

限制：
  - 仓库尚未配置 Apple Developer ID、Apple 公证凭据或 Windows 代码签名证书。当前 macOS 包是 ad-hoc/未签名 PKG，Gatekeeper 可能阻止直接打开；Windows 也可能显示 SmartScreen 警告。这些附件可用于当前测试发布，不能描述为经过操作系统发行身份认证的正式签名包。
  - Release 尚未生成 SBOM；旧安装版到 v0.1.0 的下载、退出替换和重启升级没有执行。应用级更新 feed 签名已验证，但不能替代上述系统签名和实际升级矩阵。
  - Linux CI 和产品包仍因 Linux Skia 原生资产缺失而暂停，不计入 v0.1.0 支持范围。
```

## 34. 2026-07-30 执行记录：Windows 可选择目录 MSI

```text
日期：2026-07-30
阶段/任务：在保留 Velopack 一键 Setup.exe 的同时，新增可选择安装范围和安装位置的 Windows MSI
状态：[x] MSI 后处理、发布工作流、本地结构验证、正负向 UI、全量测试和 win-x64 Native AOT 完成；[x] `v0.1.1` 已触发远程 Release 并公开上传 MSI
开发基线：3055988fbf7186d34b2ef46b2df73ae64d4f6513（开发前 main 与 origin/main 一致）
分支：codex/windows-selectable-installer

实现内容：
  - Windows Release 的 vpk pack 继续生成一键 Setup.exe，同时启用 --msi 和 --instLocation Either，允许用户选择“仅为当前用户安装”或“为所有用户安装”。
  - Velopack 1.2.0 的 Either 模板只有安装范围选择，没有安装目录入口。新增 Customize-VelopackMsi.ps1，在未签名 MSI 的事务中接入原生 BrowseDlg、只读最终路径和“浏览”按钮；用户选择父目录，最终应用根固定为 <父目录>\SnapBoard，避免把 D:\SoftWare 等共享目录直接交给卸载清理。
  - 默认当前用户路径为 LocalAppData\Programs\SnapBoard，所有用户路径为 ProgramFiles64Folder\SnapBoard；VELOPACK_INSTALLDIR 仍表示调用方提供的精确最终路径，并会锁定浏览按钮。
  - 修复目录选择阶段的三个 MSI 问题：PathEdit 间接绑定导致 2343 空路径、错误 Control_Next 链导致 2810，以及 RustValidatePath 与 InvalidDirDlg 在同一次按钮事件中读取旧值造成有效 D 盘路径假报错。当前在欢迎页、安装范围变化和 BrowseDlg 关闭前完成真实路径校验，主页面“下一步”只读取已完成的校验结果。
  - Verify-VelopackMsi.ps1 检查固定 Velopack 表结构、两种安装范围、父目录隔离、只读直接路径绑定、命令行路径锁、控件顺序、三条预校验链、有效/无效结果分支及 ALLUSERS=2；Velopack 模板漂移会让 Release 直接失败。
  - Release 将最终包统一命名为 SnapBoard-win-x64-Installer.msi，生成同名 .sha256 并沿既有 artifact glob 上传。手动 workflow_dispatch 的无效默认版本 0.0.0-dev 同步修正为 Velopack 接受的 0.0.1-dev。

自动与产物验证：
  - dotnet restore --locked-mode、dotnet format --verify-no-changes 和 Release build 通过；build 为 0 警告、0 错误。
  - 首次全量测试中，正在运行的已安装 SnapBoard 占用纯修饰键全局快捷键，两个 RegisterHotKey 原生用例按预期返回 Conflict；通过该实例自己的 --exit 正常退出后，Windows Platform 101/101，全量 469 项为 447 项通过、22 项按当前平台或外部服务条件跳过、0 项失败。
  - win-x64 self-contained PublishAot 通过，0 个未解释 trim/AOT 告警。SnapBoard.Desktop.exe 为 40,388,096 字节，SHA-256 dfca9798d8742fc312960140f6288df6a4260b424b67be8e4d9efe33db03ba97；隔离 bootstrap 冷启动和第二实例 --exit 通过。
  - SnapBoard.StorageMigrator.exe 为 4,513,280 字节，SHA-256 311ed8b657306dd7a742686d619b055ca9216fccb993d11cb0af103c471157dc；无参数退出码 4，发布目录没有该迁移器的 .dll、.deps.json 或 .runtimeconfig.json。
  - 锁定 Velopack 1.2.0 使用真实 AOT 目录同时生成 Setup.exe 和 MSI。最终 SnapBoard-win-x64-Installer.msi 为 27,594,752 字节，SHA-256 06fd8fcc57ddb85b9c2273fd885cee7844c8b1f7bb08c13e651cd6398f4b31a0；原一键 Setup.exe 仍存在。

Windows UI 验证：
  - 最终真实 MSI 选择 D:\SoftWare 后，日志中的最终路径为 D:\SoftWare\SnapBoard\，RustValidatePath 返回有效，InvalidDirDlg 为 0 次，并进入“已准备好安装”页；测试在 InstallInitialize 前终止，没有执行安装。
  - 负向传入 VELOPACK_INSTALLDIR=\\invalid\share\SnapBoard 时，Windows Installer 明确提示路径无效并阻止推进，证明没有为消除假报错而关闭无效路径保护。

限制：
  - `v0.1.1` 已公开上传 `SnapBoard-win-x64-Installer.msi` 及校验和；详细远程证据见第 35 节。
  - 仓库没有 Windows Authenticode 证书，当前 Setup.exe/MSI 仍未签名，可能触发 SmartScreen；后续加入签名时必须在 MSI 表后处理之后签名。
  - 本轮没有自动安装或卸载任何版本，也没有修改或删除用户数据；为释放 RegisterHotKey 测试冲突，仅通过现有安装实例的 --exit 正常退出。Data/ 和 docs/MACOS_PARITY_IMPLEMENTATION_CHECKLIST.md 保持未跟踪且不进入提交。
```

## 35. 2026-07-31 执行记录：发布 v0.1.1

```text
日期：2026-07-31
阶段/任务：修复 Windows 发布门槛竞态并公开发布 v0.1.1
状态：[x] 代码修复、本地验证、主 CI、双架构 macOS/Windows 打包、更新 feed 签名和公开附件审计完成；[~] 系统代码签名与已安装版跨版本升级仍待真实凭据/环境
发布提交：31ec20e48746769e10a50ead8c17b3ce435f36aa
标签：v0.1.1
Release：https://github.com/wuliangtdi/SnapBoard/releases/tag/v0.1.1

发布门槛修复：
  - 原 main CI run 30535506679 及失败 job 重跑都在 WindowsClipboardNativeIntegrationTests.SelfWriteDoesNotProduceMonitorEvent 失败；通知在 17 ms 内到达，确认不是 500 ms 窗口内的偶发外部剪贴板事件。
  - 根因是 SetClipboardData 可在 writer 把新序列号写入 ClipboardFeedbackGuard 之前向消息线程投递 WM_CLIPBOARDUPDATE。原生通知现同时快照 clipboard owner HWND，adapter 在序列号逻辑前精确过滤自己的消息窗口。
  - 新单元测试确认自己 HWND 被抑制，而同进程的另一 HWND 仍正常发布；原生“两 adapter 互相监听”行为不回退。

本地验证：
  - dotnet restore SnapBoard.slnx --locked-mode、dotnet format --verify-no-changes、Release build 和 git diff --check 通过；build 0 警告、0 错误。
  - 全量 470 项：445 项通过、25 项按平台或外部服务条件跳过、0 项失败。
  - osx-arm64 Native AOT 主程序 36,169,856 字节，独立 StorageMigrator 8,356,968 字节；均为 arm64 Mach-O，helper 无参数退出码 4。无 trim/AOT 分析告警；Desktop 只有 2 条已解释的 Apple NativeAOT clang module-cache 调试信息警告。

GitHub 验证：
  - 主 CI run 30599181462（commit 31ec20e）六个 job 全绿：Windows 共 470 项，448 通过/22 条件跳过，Windows Platform 102/102；macos-15-intel 共 470 项，445 通过/25 条件跳过；win-x64、osx-arm64、osx-x64 Native AOT 全部通过。
  - 两个 macOS AOT job 各只有 2 条同类 clang module-cache 调试信息警告，Windows 无告警；三个 RID 均无 IL2xxx/IL3xxx 或其他未解释 trim/AOT 告警。
  - Release run 30599467309 全绿：osx-arm64 2m25s、osx-x64 5m26s、win-x64 4m22s，最终签名/发布 job 41s。Windows 实际执行并通过可选目录 MSI 定制与验证。

公开产物：
  - Release 为非草稿、非预发布，共 31 个附件且全部 uploaded。Windows MSI 为 27,590,656 字节（SHA-256 02992acbfbbfebb9c2248735ad6663c98da3ff20045ae0a219d266b94f279b37），Setup.exe 33,562,441 字节，独立 Native AOT ZIP 78,990,522 字节。
  - osx-arm64 DMG/PKG 为 30,899,970/27,846,716 字节；osx-x64 DMG/PKG 为 33,022,809/29,596,659 字节。两个架构均同时包含 Velopack Portable.zip、full.nupkg 和 Setup.pkg。
  - 从公开 Release 重新下载 win-x64、osx-arm64、osx-x64 三份 releases.*.json 和签名，使用 packaging/updates/update-signing-public.pem 独立验签均为 Verified OK；每份 feed 只指向版本 0.1.1 与对应 RID 的 full.nupkg。

限制：
  - Apple Developer ID/公证凭据和 Windows Authenticode 证书仍未配置；macOS 导入身份与公证步骤按预期跳过，Windows 仍可能显示 SmartScreen 警告。
  - 尚未在已安装 v0.1.0 上执行下载、退出替换、重启和数据保留的 v0.1.1 升级矩阵；Release 仍未生成 SBOM。
  - Linux Build/Test、Native AOT 和产品包继续按已确认范围暂停。data/ 测试材料保持未跟踪，未删除也未进入任何提交或发布产物。
```

## 36. 2026-07-31 执行记录：收藏筛选与 Windows 数据目录 ACL 修复

```text
日期：2026-07-31
阶段/任务：主窗口与快速窗口收藏筛选；修复现有用户自有目录因缺少 WRITE_OWNER 被错误拒绝
状态：[x] 代码、自动测试、Headless 视觉验证和 win-x64 Native AOT 完成；[~] D:\ProgramData\SnapBoard_Data 已收紧权限，实际数据迁移因用户停止界面自动化而未执行
开发基线：7ab3331b2e83a7b3d3efb70031b75e09bc310e1d（开发前 main 与 origin/main 一致）

实现内容：
  - 主窗口现有“全部、文本、图片、代码、链接”筛选后新增“收藏”，快速窗口增加同语义的紧凑筛选栏；两处复用 MainViewModel 和 ClipboardHistoryQuery.IsPinned，不增加数据库字段、同步事件或第二套查询流程。
  - 收藏按钮根据当前记录显示实心/空心星标和“收藏/取消收藏”提示；在收藏筛选中取消收藏会立即移除该记录并保留操作反馈。
  - WindowsStoragePlatformService 对现有目录先验证所有者。当前用户、SYSTEM 或 Administrators 已拥有目录时只收紧 DACL，不再无条件重写所有者；不可信所有者仍必须成功接管后才可使用。最终私有性复检同时校验可信所有者与访问规则。
  - 设置页区分“无法收紧所选目录权限”和“目录权限仍可能暴露数据”，避免把所有失败都描述为目录本身不可用。

验证：
  - dotnet restore SnapBoard.slnx --locked-mode、dotnet format SnapBoard.slnx --verify-no-changes --no-restore、Release build 和 git diff --check 通过；build 0 警告、0 错误。
  - 全量 474 项：452 项通过、22 项按当前平台或外部服务条件跳过、0 项失败；Windows Platform 103/103，Desktop Headless 106/106。
  - Headless 真实 Skia 截图覆盖主窗口、680 x 480 快速窗口和 560 x 380 最小快速窗口；六个筛选项均完整可见，列表、滚动区域和底栏无重叠。
  - win-x64 self-contained PublishAot 通过，0 个未解释 trim/AOT 警告。SnapBoard.Desktop.exe 为 40,415,744 字节；独立 SnapBoard.StorageMigrator.exe 为 4,513,792 字节，且没有 .dll、.deps.json 或 .runtimeconfig.json sidecar。
  - Windows 原生回归用例复现“当前用户拥有、继承 Authenticated Users Modify、没有 WRITE_OWNER”的目录并验证收紧成功。D:\ProgramData\SnapBoard_Data 已确认是空的非重解析点，并已收紧为当前用户、SYSTEM 和 Administrators 完全控制。

限制：
  - 用户在自动操作设置窗口迁移流程时按下 Esc，Computer Use 随即停止；本轮没有声称数据已经迁移。目标目录仍为空，需由用户在应用内重新选择并确认迁移。
  - Data/ 和 docs/MACOS_PARITY_IMPLEMENTATION_CHECKLIST.md 保持未跟踪且不进入提交。
```

## 37. 2026-07-31 执行记录：修复 Windows 数据目录迁移尾部路径回滚

```text
日期：2026-07-31
阶段/任务：修复选择带尾部目录分隔符的 Windows 数据目录后迁移必然回滚
状态：[x] 根因、代码修复、回归测试、完整测试和 Windows Native AOT 验证完成
开发基线：ecbd206fcdad7c4f6ccda44b5c42712be930373c（开发前 main 与 origin/main 一致）

根因：
  - Windows 文件夹选择器将目标写成 D:\ProgramData\SnapBoard_Data\；StorageMigrationExecutor.GetStagingDirectory 直接对带尾分隔符的路径调用 GetDirectoryName/GetFileName，结果把 .staging-* 目录建到了目标目录内部。
  - 随后的“目标必须仍为空”安全复检必然抛出 StorageMetadataException，统一错误码为 verification-failed；locatorSwitched=false，所以回滚后原目录继续生效。

修复：
  - StorageManagementService 在生成迁移计划前使用 Path.TrimEndingDirectorySeparator 规范化目标路径。
  - StorageMigrationExecutor 对清单中的源/目标路径再次规范化，并在计算暂存目录时保留防御性规范化，兼容已经生成的带尾分隔符清单。
  - 新增管理服务 manifest 规范化测试，以及直接带尾分隔符 manifest 的完整迁移测试。

验证：
  - dotnet format SnapBoard.slnx --verify-no-changes --no-restore 通过；Release build 0 警告、0 错误。
  - 全量 476 项：454 项通过、22 项按当前平台或外部服务条件跳过、0 项失败；Infrastructure 存储迁移相关测试 12/12。
  - win-x64 self-contained PublishAot 独立输出通过，0 个未解释 trim/AOT 警告。SnapBoard.Desktop.exe 为 40,415,744 字节，SnapBoard.StorageMigrator.exe 为 4,514,304 字节；迁移器无 .dll、.deps.json 或 .runtimeconfig.json sidecar。

Windows 实机迁移：
  - 使用修复后的 AOT 从原 C 盘数据根迁移到 D:\ProgramData\SnapBoard_Data，迁移 ID 为 m-1f443da5de94489bbe6f120c87e1d645；约 1 秒内完成并由新主进程确认启动。
  - 最终状态为 Completed，locatorSwitched=true、startupAcknowledged=true、errorCode 为空；定位文件中的目标路径已去除尾分隔符。
  - 目标和回滚备份的 SQLite integrity_check 均为 ok，逻辑计数一致：52 条记录、20 个 Blob、0 个 Outbox；目标包含 24 个文件、20 个 Blob，共 30,190,114 字节。
  - 原目录已保留为 C:\Users\ozonechen\AppData\Local\SnapBoard\data.backup-m-1f443da5de94489bbe6f120c87e1d645，未删除回滚备份。

限制：
  - 当前公开 v0.1.1 安装包不包含本修复；上述实机验证使用本地最新 AOT 产物。
```

## 38. 2026-07-31 执行记录：收藏清理保护与纯时间顺序

```text
日期：2026-07-31
阶段/任务：自动清理默认保留收藏；主窗口与快速窗口取消收藏排序优先级
状态：[x] 共享设置、SQLite、设置 UI、自动测试、Windows Native AOT 与本机清理完成
开发基线：7aa9b0a63fcc550a654111baa0ff2dc3ca2e26ba（开发前 main 与 origin/main 一致）

实现内容：
  - history.retention 新增 PreserveFavorites，默认开启并继续通过现有加密设置事件逐键同步；设置页“自动清理历史”下新增 settings-toggle，允许用户明确选择是否让收藏内容参与自动清理。
  - ClipboardRetentionPolicy 与 SQLite 保留期候选集接入该选项。默认策略排除收藏；关闭保护后，收藏与普通记录共同参与天数、数量和容量限制，并产生原有 Delete 墓碑。
  - 普通查询删除 is_pinned 排序和游标分段；FTS 查询删除“收藏阶段/非收藏阶段”两阶段分页，统一按 search_order_key 的时间顺序分页。收藏仍保留为独立筛选条件和记录属性。
  - 当前数据库创建脚本的活动顺序索引改为 is_deleted + captured_at_utc + id，FTS 顺序索引不再包含 is_pinned。按产品决定不实现旧设置或旧数据库格式兼容。
  - 同步协议和 PLAN 已更新为“自动清理默认关闭；开启时默认保留收藏，可由用户关闭保护”。

验证：
  - dotnet restore SnapBoard.slnx --locked-mode、dotnet format SnapBoard.slnx --verify-no-changes --no-restore 和 Release build 通过；build 0 警告、0 错误。
  - 全量 481 项：459 项通过、22 项按当前平台或外部服务条件跳过、0 项失败；Infrastructure 101/102（1 条外部 WebDAV 跳过），Desktop Headless 106/106。
  - 回归覆盖默认保留收藏、关闭后收藏参与年龄/数量/容量清理、选项持久化与重启恢复、false 值跨设备同步、普通列表双向时间排序、普通/FTS 收藏筛选跨页不重不漏，以及设置页默认状态/样式。
  - win-x64 self-contained PublishAot 通过，0 个未解释 trim/AOT 警告。SnapBoard.Desktop.exe 为 40,420,352 字节，独立 SnapBoard.StorageMigrator.exe 为 4,514,304 字节；迁移器无 .dll、.deps.json 或 .runtimeconfig.json sidecar。
  - AOT 使用独立 bootstrap 数据目录启动后进程可响应、主窗口句柄非零，并通过 --exit 以 0 退出；验证产物随后按用户要求删除。

本机清理：
  - 已通过产品码 {EA45DF46-E76D-4DD1-9BC6-B8284F9C7104} 卸载 0.1.1 MSI；HKCU/HKLM 卸载登记、D:\SoftWare\SnapBoard、桌面/开始菜单快捷方式均已消失。
  - 已永久删除 D:\ProgramData\SnapBoard_Data、C:\Users\ozonechen\AppData\Local\SnapBoard（含 bootstrap 与迁移回滚备份）、HKCU\Software\SnapBoard、SnapBoard 开机启动值，以及仓库 artifacts/ 和 %TEMP% 下的 SnapBoard 测试残留。
  - 复查结果为 0 个 SnapBoard 进程、0 个安装登记、0 个已知数据/配置/快捷方式/启动项残留；未发现 com.wuliangtdi.snapboard/ Credential Manager 条目。

限制：
  - 本轮按明确产品决定不保留或迁移任何旧本机数据；下次安装将创建全新数据库和设置。
  - AOT 冒烟在独立数据目录执行，尚未对待 GitHub Actions 生成的新 MSI 做安装后人工界面验收。
  - Data/ 和 docs/MACOS_PARITY_IMPLEMENTATION_CHECKLIST.md 保持未跟踪且不进入提交。
```

## 39. 2026-07-31 执行记录：来源应用图标跨设备同步阶段 A

```text
日期：2026-07-31
阶段/任务：来源应用图标跨设备同步的 Windows 与共享层
状态：[x] Windows 原生采集、共享持久化/同步/UI、自动测试与 win-x64 Native AOT 完成；[ ] macOS 本机快照生成待实施
开发基线：9da2f80（开发前 main 与 origin/main 一致）

实现内容：
  - 新增平台无关的 32 x 32 BGRA8888 预乘 Alpha 快照模型和提供器端口。Windows 复用既有 Shell/AppsFolder 解析器，不建立第二套应用识别逻辑；首次空结果只重试一次，失败不阻断正文保存。
  - SQLite 当前格式为 v9。每条记录最多引用一个 4096 字节来源图标 Blob，并保存格式版本、宽、高和 stride；相同像素按 SHA-256 去重。相邻重复只在旧记录缺图标时补入，删除、清空、自动清理、远端墓碑和事务回滚均维护精确引用计数。
  - 当前同步协议仍为 v1，远端仍为 SnapBoard/v1。SyncClipboardItemPayload 直接增加可空图标描述符，复用 keyed Blob ID、AES-256-GCM、先 Blob 后事件、下载暂存和原子应用流程；没有旧 JSON 解析、双协议分支、远端目录迁移或历史回填。
  - 主窗口和快速窗口通过同一 ViewModel 路径按需读取持久化快照。验证快照优先于本机路径解析；快照缺失、损坏或不可读时才使用本机解析器和通用图标。
  - Desktop 只在 Windows 注册 IClipboardSourceApplicationIconProvider。macOS 共享层可以显示 Windows 同步快照，但本机新记录生成快照未注册，整个跨平台功能未标记完成。

验证：
  - dotnet restore SnapBoard.slnx --locked-mode、dotnet format SnapBoard.slnx --verify-no-changes --no-restore 和 Release build 通过；build 0 警告、0 错误。
  - 全量 495 项：473 项通过、22 项按 macOS 原生环境或外部 WebDAV 条件跳过、0 项失败。项目分布为 Application 19、Architecture 2、Domain 4、Infrastructure 110 通过/1 跳过、Linux 1、macOS 70 通过/21 跳过、Windows 103、WebDAV 39、Update 17、Desktop Headless 108。
  - 回归覆盖规范快照采集、一次重试和异常降级；v9 schema；Blob 去重、重启、相邻补入、损坏拒绝、软删除/清空/自动清理；当前 JSON 往返和非法描述符；Outbox/下载/墓碑；不同本机路径的双设备像素保持；快照优先和本机回退。
  - win-x64 self-contained PublishAot 通过，0 个 trim/AOT 警告。SnapBoard.Desktop.exe 为 40,489,472 字节，SHA-256 1098C9EA99E78DC4604BD116DCCCF5E67FE5005C6C9D45CD540C80DD039EB21A；SnapBoard.StorageMigrator.exe 为 4,514,304 字节，SHA-256 A42ACB8E78BEEE72B5A8895FF62EB81B98797512F5037CA19437BC08967CC014。
  - 迁移器没有 .dll、.deps.json 或 .runtimeconfig.json，独立运行无参数退出码为 4。主程序使用随机隔离数据根启动，主窗口句柄非零、创建一个 v9 SQLite 数据库，并通过 --exit 以 0 退出；临时目录已删除。

限制：
  - Chrome、Edge、微信、Codex、截图工具和 Store 应用的真实复制及两份正式安装间同步尚未逐项人工操作；现有自动测试和真实 Shell 图标测试不能替代该矩阵。
  - macOS 本机快照生成、macOS -> Windows 往返和 macOS Native AOT 属于阶段 B，必须从合入本阶段后的最新 main 在 macOS 环境继续。
  - 本次不生成或提交 Data/、临时数据库、构建产物、恢复材料、凭据或 docs/MACOS_PARITY_IMPLEMENTATION_CHECKLIST.md。
```

## 40. 2026-07-31 执行记录：来源应用图标跨设备同步阶段 B

```text
日期：2026-07-31
阶段/任务：来源应用图标跨设备同步的 macOS 原生采集与双向验证
状态：[x] macOS 本机快照、双向像素往返、实机 TextEdit/Finder、osx-arm64/osx-x64 Release 与 Native AOT 完成；整个跨平台来源应用图标功能完成
开发基线：6d2c240d043c07dfb95897b2b4adce6a8642271d（开发前 main 与 origin/main 一致）

实现内容：
  - MacOSClipboardSourceApplicationMetadataResolver 同时实现元数据解析器和 IClipboardSourceApplicationIconProvider；CaptureAsync 复用同一次 NSWorkspace/App Bundle 解析与 256 项有界缓存，不建立第二套识别逻辑。
  - Desktop macOS 组合根将两个端口别名到同一解析器单例。AppKit 访问继续经过 IPlatformMainThreadDispatcher，输出沿用现有固定 32 x 32、stride 128、4096 字节 BGRA8888 预乘 Alpha 格式。
  - macOS 快照直接进入既有 ClipboardCaptureCoordinator、SQLite v9、内容寻址 Blob、引用计数、加密同步和主/快速窗口消费路径；没有兼容层、迁移代码、历史回填或平台专用持久化。
  - 当前同步协议继续为 v1，远端根目录继续为 SnapBoard/v1。来源 EXE/App Bundle 路径仍只用于本机解析，不进入同步载荷。
  - Windows 生产解析、采集和组合根语义未修改；仅在共享组合根回归测试中继续断言 Windows 元数据与图标端口保持同一实例。

自动验证：
  - arm64 SDK 和官方 x64 SDK（Rosetta）均执行 locked restore、Release build/test；两轮都是 469 项通过、26 项按 Windows 原生环境或外部 WebDAV 条件跳过、0 项失败。x64 Host 明确为 Architecture x64、RID osx-x64，build 0 警告、0 错误。
  - macOS 原生测试直接解析系统 TextEdit/Finder，验证规范尺寸、非空像素、AppKit InvokeAsync 边界以及 ResolveAsync/CaptureAsync 的缓存复用。
  - 双设备端到端测试先执行 Windows -> macOS，再执行 macOS -> Windows；目标设备来源可执行路径均为 null，宽、高、stride 和 4096 字节像素完全一致，远端 Blob 数量、删除墓碑和引用生命周期保持正确。
  - 持久化快照优先测试改为 AvaloniaFact，在真实 Headless 平台初始化下继续证明同步快照优先于本机解析器；生产降级行为未改变。

macOS 实机：
  - 环境为 Mac mini Apple M4、macOS 26.2 (25C56)、arm64。arm64 AOT App Bundle 使用随机隔离数据根启动，未访问默认用户数据库。
  - TextEdit 唯一文本记录保存“文本编辑”、/System/Applications/TextEdit.app/Contents/MacOS/TextEdit、ForegroundWindowAtChange 和图标哈希 74482fd5a867827b325b951a1dee89aee1042d0e3e922a9a3fd6a7ded38f7f7b。
  - Finder 复制 data/SOURCE_APPLICATION_ICON_SYNC_REQUIREMENTS.md 的记录保存“访达”、/System/Library/CoreServices/Finder.app/Contents/MacOS/Finder、ForegroundWindowAtChange 和图标哈希 7d73b7da2611bcd2d63026bb3c20346f6db226d4af8b6b5c4b8955d8a5b0f6f8。
  - 两个图标 Blob 都是 application/vnd.snapboard.source-icon-bgra32、4096 字节；落盘 SHA-256 与数据库键一致，AOT 主窗口可见对应来源名称与图标。

Native AOT：
  - osx-arm64：SnapBoard.Desktop 36,252,640 字节，SHA-256 d5921f9fc43ddf6d454c6120b696e168a371588dac4c6d6abe62395ceef894d8；SnapBoard.StorageMigrator 8,356,968 字节，SHA-256 eec02eccbe5b4a9b1a104101371bbf61d970d70dafa4a80ff605e7087d179e04。
  - osx-x64：由 x64 SDK 在 Rosetta 下原生发布；SnapBoard.Desktop 37,281,072 字节，SHA-256 9943361f4fb24d4cd65736f29123b0708c32a2328d35e08e9e6a61b55ef4dceb；SnapBoard.StorageMigrator 8,554,496 字节，SHA-256 3a4ce9ed359da687b08317a5c2935321a2ef55fbba1416e120e3252a6724e67d。
  - Verify-NativePublish.sh 确认两端主程序和迁移器均为对应架构 Mach-O，迁移器无参数退出码为 4，未发现 CoreCLR、hostfxr 或迁移器托管 sidecar。两个 RID 均无 trim/AOT 警告。
  - 链接阶段各有两条官方 .NET Apple NativeAOT 静态库的 clang module-cache 调试信息警告（Foundation 与 _SwiftConcurrencyShims .pcm 不存在）；只影响调试信息，属于仓库既有已解释警告。

限制：
  - x64 Release 与 AOT 使用 Apple Silicon 上的 Rosetta x64 SDK 和实际 x86_64 进程，不冒充 Intel 匹配硬件。
  - 当前阶段的双向协议、格式与像素往返由两份隔离存储自动验证；正式 Windows 与 macOS 两台安装经真实 WebDAV 的人工可视矩阵仍属于更广泛的跨平台验收限制。
  - Data/、临时数据库、构建产物、恢复材料和凭据不进入提交。
```

## 41. 2026-07-31 执行记录：修复来源应用图标导致数据目录迁移回滚

```text
日期：2026-07-31
阶段/任务：修复 SQLite v9 来源应用图标 Blob 引用校验遗漏
状态：[x] 根因、共享修复、回归测试、完整测试、三平台 GitHub CI 和 v0.1.3 Release 完成
开发基线：fb9ceccf0b42978aa3da8badeff6f96a77013a4b（开发前 main 与 origin/main 一致）

根因与修复：
  - SQLite v9 的 content_blobs.ref_count 同时统计正文表示、缩略图和来源应用图标引用；StorageDatabaseVerifier 迁移后复检只统计前两类。
  - 只要历史中存在来源应用图标，复制后的数据库就会被误判为引用计数不一致，迁移以 verification-failed 回滚，locatorSwitched 保持 false。
  - 校验查询现已计入 clipboard_items.source_application_icon_blob_hash；没有改变 schema、同步协议、迁移状态机或平台接口。
  - 新增完整迁移回归，分别验证一个引用和三条记录共享同一个图标 Blob；迁移完成后逐条读取像素，并直接确认目标库 ref_count 精确等于引用数。

验证：
  - dotnet restore SnapBoard.slnx --locked-mode、dotnet format SnapBoard.slnx --verify-no-changes --no-restore 和 Release build 通过；build 0 警告、0 错误。
  - 全量 497 项：475 项通过、22 项按当前平台或外部 WebDAV 条件跳过、0 项失败；Infrastructure 112 项通过、1 项跳过。
  - win-x64 self-contained PublishAot 通过，0 个 trim/AOT 警告。SnapBoard.Desktop.exe 为 40,489,472 字节，SHA-256 C68A71E0C5B84F2049409A4D68C2DEEFA3A5D2E55C4DDECA5984EBDE3E4F7F33；SnapBoard.StorageMigrator.exe 为 4,514,816 字节，SHA-256 870802D98A7C998DCBEA4AD566D743E406DC68DF2A22E7FA659B423D3B93CF21。
  - 迁移器没有 .dll、.deps.json 或 .runtimeconfig.json sidecar，独立无参数运行退出码为 4。
  - GitHub CI run 30625363781 在提交 9e60b3c 上完成且六个 job 全绿：Windows、macOS arm64、macOS Intel 的构建/测试，以及 win-x64、osx-arm64、osx-x64 Native AOT 均成功。
  - v0.1.3 Release run 30625720554 四个 job 全绿；公开 Release 为非草稿、非预发布，共 31 个附件且全部 uploaded。Windows 可选目录 MSI、Native AOT ZIP、两个 macOS 架构的 DMG/PKG、Velopack 包和已签名更新源均已生成。

限制：
  - 当前已安装 v0.1.2 不包含本修复；尚未在本机安装公开 v0.1.3 并重新执行界面迁移。没有修改或删除现有数据、回滚状态与 D:\ProgramData\SnapBoard_Data。
  - Data/ 和 docs/MACOS_PARITY_IMPLEMENTATION_CHECKLIST.md 保持未跟踪且不进入提交。
```

## 42. 2026-08-01 执行记录：Avalonia 与构建依赖升级

```text
日期：2026-08-01
阶段/任务：升级 UI 依赖、重新生成锁文件并核查全部直接依赖与 GitHub Actions
状态：[x] Windows 本机验证、GitHub 三平台 CI、三个 RID Native AOT 与 v0.1.4 Release 完成
开发基线：072cf40a12ebafbd90b80620b839cd7ab481d1b5（开发前 main 与 origin/main 一致）

升级内容：
  - Avalonia、Avalonia.Desktop、Avalonia.Fonts.Inter、Avalonia.Themes.Fluent 和 Avalonia.Headless.XUnit 从 12.1.0 升级到 12.1.1。
  - Irihi.Ursa 和 Irihi.Ursa.Themes.Semi 的中央版本从 2.1.0 升级到 2.2.0；当前没有项目引用这两个包，因此不伪造运行时/UI 验证结论。
  - 使用 --force-evaluate 重新解析全解决方案锁定依赖；只有 Desktop、Desktop.HeadlessTests 和 PerformanceTests 三份锁文件发生实际版本/哈希变化，其余文件不提交换行噪声。
  - CI 和 Release 的 actions/checkout 从 v6 升级到 v7，actions/setup-dotnet 从 v5 升级到 v6；upload-artifact v7、download-artifact v8 保持当前版本。
  - Windows 原生按键和剪贴板用例共享真实桌面资源。解决方案并行运行可跨测试进程争用，CI 测试命令改为 --maxcpucount:1；测试数量、过滤条件和用例内容均未改变。

其他依赖核查：
  - 全部直接 NuGet 包、.NET SDK、仓库本地工具和 GitHub Actions 均已检查。SDK 10.0.302、Velopack 1.2.0 以及除下述两项外的直接包均为当前稳定版。
  - SkiaSharp 4.151.0 暂不升级：Avalonia.Skia 12.1.1 仍依赖 3.119.4，直接跨主版本会使渲染原生资产脱离上游组合。
  - SQLitePCLRaw.bundle_e_sqlite3 3.0.5 暂不升级：Microsoft.Data.Sqlite 10.0.10 仍依赖 2.1.11 系列，仓库当前 2.1.12 已是同系列更新；跨主版本会改变 SQLite 原生/AOT 链路。

验证：
  - dotnet restore SnapBoard.slnx --locked-mode、dotnet format SnapBoard.slnx --verify-no-changes --no-restore 和 Release build 通过；build 0 警告、0 错误。
  - 串行完整测试共 497 项：475 项通过、22 项按 macOS 原生环境或外部 WebDAV 条件跳过、0 项失败；Desktop Headless 108/108、Windows 103/103。
  - 直接与传递 NuGet 漏洞审计为 0。
  - win-x64 self-contained PublishAot 通过，0 个 trim/AOT 警告。SnapBoard.Desktop.exe 为 40,494,080 字节，SHA-256 18908B3A38F7029915BF82131528E987406833C80C6E0807A758027CA29DC202；SnapBoard.StorageMigrator.exe 为 4,514,816 字节，SHA-256 BD216E5137DE61EEC20A1B605ED86C47CD52D15E59BB8CEC595CB1388F9C77E1。
  - 迁移器没有 .dll、.deps.json 或 .runtimeconfig.json sidecar，无参数退出码为 4。主程序使用随机隔离 bootstrap 根启动，主窗口句柄非零、创建一个 v9 SQLite 数据库，第二实例 --exit 与主实例均以 0 退出。
  - GitHub CI run 30696518333 在提交 16088f0 上完成；Windows、macOS arm64、macOS Intel 的构建/测试，以及 win-x64、osx-arm64、osx-x64 Native AOT 六个 job 最终全绿。首次 macOS Intel 尝试中 SQLite FTS 取消时序用例因查询先完成而未抛取消异常，只重跑失败 job 后通过，没有修改代码或标签。
  - v0.1.4 Release run 30696534024 四个 job 全绿；公开 Release 为非草稿、非预发布，共 31 个附件且全部 uploaded。Windows 可选目录 MSI、Native AOT ZIP、两个 macOS 架构的 DMG/PKG、Velopack 包和已签名更新源均已生成。

限制：
  - 首次 macOS Intel 测试尝试中 SQLite FTS 取消时序用例暴露性能时序偶发风险；失败 job 原样重跑后通过，但仍保留该风险记录。
  - 本轮不提交 artifacts/、Data/ 或 docs/MACOS_PARITY_IMPLEMENTATION_CHECKLIST.md。
```

## 43. 更新规则

- 每完成一个退出条件，当天更新本文件和 `PLAN.md` 对应复选框。
- 测试失败、AOT 告警、性能超标和平台权限限制必须记录，不能只留在终端输出。
- GitHub Actions 结果应记录运行链接、Commit SHA、Runner 与 RID。
- 性能结果必须记录机器配置、构建类型、采样工具、时长和样本数据。
