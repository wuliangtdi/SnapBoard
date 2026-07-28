# SnapBoard 执行进度

> 最后更新：2026-07-28
> 当前阶段：共享 WebDAV 服务商迁移、SQLite v8、双平台设置流程、Apache 实测及本机 `osx-arm64` AOT 已落地；正式跨系统 App、Nextcloud/Synology、Intel 与正式发布继续收口
> 总体状态：进行中
> 规则：只有代码、自动测试和目标平台验证同时满足时，功能才标记完成。

## 1. 总览

| 阶段 | 状态 | 当前结论 |
| --- | --- | --- |
| Phase 0 规划与决策 | 已完成 | 名称、MIT、三期平台、WebDAV 和同步范围已确认 |
| Phase 1.0 工程骨架 | 进行中 | 本机 Release 构建、测试和 macOS/Windows Native AOT 已通过；GitHub Runner 待验证 |
| Phase 1.1 AOT/内存基线 | 进行中 | 最终历史构建三轮可见峰值 PWS 155.74/155.33/138.97 MiB，关闭窗口后为 103.32/110.13/94.82 MiB；Private Bytes 为 136.59/135.54/127.82 MiB，内存门槛未完成 |
| Phase 1.2 UI 生命周期 | 进行中 | 单实例、后台启动、主/快速/设置窗口、自定义原生热键、暂停和退出已实现；托盘点击、物理热键、多显示器/DPI、真实开机启动与 8 小时长稳待验收 |
| Phase 1.3 Windows 剪贴板 | 进行中 | delayed rendering、Notepad/WinUI、事件时来源快照、注册 PNG 及 10,000 次功能压力通过；Codex/截图工具手动复核、完整桌面资源与外部应用矩阵未完成 |
| Phase 1.4 本地历史与检索 | 已完成 | SQLite v5、单写队列、恢复、CAS Blob、PNG/TIFF 缩略图、FTS5、策略链及 100,000 条检索已在 Windows/macOS 验证 |
| Phase 1.5 快速粘贴体验 | 进行中 | 正式路径已接真实历史、虚拟化、分页、取消、按需缩略图、打包应用名称/图标及高频变化合并刷新；数字快捷选择、标签编辑、搜索高亮与完整富预览待完成 |
| Phase 1.6-1.8 | 进行中 | 加密同步、SQLite v8、历史策略、真实 UI 和共享 WebDAV 服务商迁移已落地，Apache 标准 WebDAV 实测通过；Nextcloud/Synology、正式跨系统 App、设备撤销/密钥轮换、长期资源及发布待完成 |
| Phase 2 macOS | 进行中 | arm64 剪贴板、APFS 历史、存储迁移、Keychain 同步、共享服务商迁移、生命周期及 Native AOT 已验证；内存、8 小时、Intel、Developer ID、公证和正式跨系统设备矩阵待完成 |
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
- [~] 已从现有透明品牌图生成 Windows 多尺寸 `.ico` 和 macOS 标准 `.icns`，并接入 EXE/App Bundle、标题栏、Dock/Finder 与托盘/Template 状态图标；Developer ID 正式签名身份待配置。
- [ ] 优化可见窗口内存，完成纯 Avalonia、Material Icons、Ursa 和最终壳的可重复 A/B 测量。

## 3. 已验证基线

| 检查 | 结果 | 说明 |
| --- | --- | --- |
| NuGet restore | 通过 | 已启用锁文件和漏洞审计告警即错误 |
| Release build | 通过 | 本机 0 警告、0 错误 |
| 全量自动测试 | 通过 | macOS arm64 共 299 项：279 项通过、20 项 Windows 原生测试按平台跳过、0 项失败；Application 10/10、Infrastructure 91/91、WebDAV 35/35、macOS 48/48、Desktop Headless 51/51、Architecture 2/2；真实 Apache 用例已启用执行而非跳过 |
| macOS 存储与同步测试 | 通过 | macOS 原生项目 48/48 且无跳过；覆盖 APFS/权限/链接/卷/进程身份、真实 Keychain 完整工作流、legacy 启动、设置 modal/迁移事务和有状态双设备离线收敛 |
| `osx-arm64` Native AOT | 本机通过 | 服务商迁移构建主程序 34,740,768 字节，迁移器 8,356,952 字节，均为 arm64 Mach-O；无 CoreCLR/helper 托管配置，helper 无参数退出码 4，隔离 bootstrap 后台启动及 `--exit` 通过。0 个 trim/AOT 分析告警；2 个 clang module-cache 调试信息告警来自 .NET 10.0.10 官方 Apple NativeAOT 静态库，已记录且未 suppression。正式签名/公证未完成 |
| `win-x64` Native AOT | 本机通过 | 最新独立包主程序 37,527,552 字节、嵌套迁移器 4,512,768 字节；无 `coreclr.dll`/`clrjit.dll`，迁移器无框架依赖配置，0 个 AOT/裁剪警告；此前 AOT 设置窗口启动冒烟通过，本次因旧构建正在运行未重复启动，Runner 待验证 |
| `linux-x64` Native AOT | 待验证 | 交由 Ubuntu Runner 验证 |
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

实机已验证关闭全部窗口后进程和状态项继续存在、第二实例复用原进程并打开主窗口、三类窗口重复关闭/重建、菜单打开主/快速/设置窗口、暂停/恢复记录和菜单退出。默认 `Command+Shift+V` 及自定义 `Option+Control+A` 均由系统真实按键事件打开快速窗口；自定义配置重启后仍注册，最后恢复默认。快速窗口打开前保存目标应用，既有 TextEdit 恢复与自动粘贴结果继续有效。设置页仅显示 Command/Option/Control/Shift、登录启动、辅助功能和 Bundle 能力，不显示 Windows 术语。

稳定 Bundle ID 为 `com.wuliangtdi.snapboard`，标准 `.icns` 和浅色/深色 Template 状态图标已接入。最终 `osx-arm64` DMG 校验通过，挂载后的 App Bundle 实际后台启动并显示状态项，PKG 可展开；应用使用 Hardened Runtime ad-hoc 签名，PKG 未签名。当前钥匙串没有 Developer ID Application/Installer 身份，也未配置公证凭据，因此正式签名、Gatekeeper 接受和公证均未执行，不能标记完成。

macOS 现已在 Avalonia 和 SQLite 初始化前解析固定 bootstrap 与活动数据根，保留 `~/Library/Application Support/SnapBoard` legacy 数据，locator 损坏恢复与缺失自定义根均明确失败。平台存储服务使用原生文件身份、实际卷大小写语义、卷 UUID、POSIX mode 与扩展 ACL 检查 APFS 目录，保守拒绝网络/移动/只读/未知卷和 iCloud/File Provider 根；进程启动、等待与停止使用 PID、启动时间、可执行路径和 UID 的完整身份。共享组合根在 macOS 注册真实同步、历史设置和存储迁移服务，设置窗口的存储、历史、同步与辅助功能区域全部可见并遵循 owner modal 及失败恢复顺序。

Keychain 原生测试已覆盖 32 字节空间主密钥和包含 endpoint/root/user/password/certificate pin/loopback 的凭据包新增、读取、覆盖、删除、不存在与拒绝状态。已有空间重新配置改为先临时恢复并恒定时间比较主密钥，再用候选凭据验证远端，成功后才覆盖安全存储；错误恢复码、有效但不匹配的恢复材料及证书失败均逐字节保持既有主密钥、凭据和 SQLite 配置，后续同步仍可用。

### 4.9 Windows 安全存储迁移与加密同步

Windows 启动阶段现由 bootstrap 定位器解析活动数据根，SQLite 与 Blob 只使用当前解析结果。迁移由独立 Native AOT 迁移器执行，主程序先暂停并排空同步与剪贴板持久化，再建立数据库屏障；清单、卷身份、重解析点、空间、哈希、Schema、`quick_check`、启动确认和回滚均有边界检查。目标目录分别在选择时、用户确认后的 `PrepareMigrationAsync`、主程序退出后的迁移器复制前检查为空；最终准备校验失败时不生成迁移状态、不启动迁移器、不关闭主程序，并显示模态错误窗口，迁移器侧竞态兜底会保留后来出现的文件、回滚并重启原应用。Desktop 发布通过 `$(MSBuildProjectDirectory)` 与 `$(IntermediateOutputPath)` 计算迁移器中间目录，因此没有写死本机盘符或用户名。

SQLite Schema v7 新增同步空间、Outbox、Inbox、逐设备 Checkpoint、Blob staging 和逐设置键逻辑版本。历史新增、置顶、删除及 `history.capture`/`history.retention`/`sync.pollInterval` 设置与 Outbox 在同一写事务提交；`SyncService` 使用 single-flight、有界批次、动态轮询和暂停排空，远端只写加密元数据、不可变事件及 keyed Blob。Windows Credential Manager 分离保存内容主密钥与版本化、长度受限的完整 WebDAV 连接配置；SQLite 表结构不包含 URL、用户名或密码字段，恢复材料落盘前加密。设置页接入创建/加入、连接验证、证书指纹、恢复材料、记录类型、默认关闭的自动清理、后台检查频率和真实同步状态，密码及恢复码提交后清空。

WebDAV 客户端已覆盖 HTTPS/显式 loopback 例外、证书固定、同源同根重定向、条件写入、ETag、取消、有限重试、响应上限和严格 PROPFIND。精确 SHA-256 指纹允许自签名链错误，但证书缺失或主机名不匹配仍拒绝；DTD、外部实体、跨源 href、编码分隔符和路径逃逸也会被拒绝。自动化假远端已验证双设备创建/加入、加密事件与 Blob、重复收发、墓碑、序号缺口、迁移暂停排空及服务商迁移故障恢复；Apache 2.4.62 标准 WebDAV 已完成真实双设备迁移。Nextcloud、Synology、设备撤销/密钥轮换、远端回收及正式跨系统 App 矩阵仍待验证。

### 4.10 WebDAV 服务商迁移

共享 Application 状态机实现 Draft 到 Completed 及全局 RollingBack/RolledBack，普通同步在上传前扫描旧端加密 intent；离线设备发现计划后先持久化阻断状态，再要求本机目标凭据。旧端一次性条件创建的 `terminal.enc` 在 `Completed` 与 `Rollback` 并发时裁决唯一赢家，目标端只镜像同一决定，陈旧参与设备不得生成相反终态。协调者只复制 metadata、不可变事件和 keyed Blob 的原始密文字节，同时在本机短暂解密副本验证认证标签、路径 descriptor、逐设备连续序号、ready 水位、Checkpoint 与 Blob 内容地址；目标端逐对象比较规范身份、长度和 SHA-256。相同对象幂等跳过，同路径不同密文阻断，旧端默认保留。

SQLite v8 只保存计划 ID、epoch、远端指纹、阶段、水位和进度，不保存 endpoint、root、用户名、密码或证书。每台设备在 Credential Manager/Keychain 中使用独立 source/target 暂存槽；提交前校验 active 仍等于 source，写入 target 后读回验证，失败可恢复 source。目标密码提交后从 ViewModel 清空，不进入迁移 DTO、SQLite 或远端控制标记。共享设置页显示当前服务、设备就绪/离线/提交状态、对象/字节进度和可恢复错误，继续及回滚均使用 owner modal。

## 5. 下一执行顺序

1. 使用正式 Windows 与 macOS App 执行双向发起、离线恢复、部分提交恢复及回滚矩阵；共享状态机测试不能替代该双机门槛。
2. 使用 Nextcloud 与 Synology 执行认证、路径、ETag、配额、限流、重试和损坏响应矩阵；Apache 标准 WebDAV 已通过。
3. 完成设备撤销、密钥轮换、远端 Checkpoint/Blob 安全回收，以及系统唤醒和网络恢复触发。
4. 在新构建上手动复核 Codex 文字复制、截图工具图片/来源，并用隔离数据目录重跑完整 AOT 桌面 10,000 次压力、三次资源采样和 8 小时长稳。
5. 在对应硬件补齐 Windows ARM64、macOS 同协议/Keychain、macOS Intel 和 Linux 验证；不得从当前 Windows x64 结果外推。
6. 上述发布门槛完成后再进入 Windows 签名、安装包、自动更新和正式发布。

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
  - 新增 `history.capture` 与 `history.retention` 动态设置：内容类型默认全开，自动清理默认关闭；设置使用加密事件逐键 LWW 同步，清理跳过置顶项并产生跨设备删除墓碑。
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

## 17. 更新规则

- 每完成一个退出条件，当天更新本文件和 `PLAN.md` 对应复选框。
- 测试失败、AOT 告警、性能超标和平台权限限制必须记录，不能只留在终端输出。
- GitHub Actions 结果应记录运行链接、Commit SHA、Runner 与 RID。
- 性能结果必须记录机器配置、构建类型、采样工具、时长和样本数据。
