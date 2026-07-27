# SnapBoard 执行进度

> 最后更新：2026-07-28
> 当前阶段：Phase 1 Windows 本地历史与检索已完成实现和自动验证；Windows 实机矩阵继续收口
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
| Phase 1.4 本地历史与检索 | 已完成 | SQLite v5、单写队列、恢复、CAS Blob、缩略图、FTS5、策略链及 100,000 条检索已通过 |
| Phase 1.5 快速粘贴体验 | 进行中 | 正式路径已接真实历史、虚拟化、分页、取消、按需缩略图、打包应用名称/图标及高频变化合并刷新；数字快捷选择、标签编辑、搜索高亮与完整富预览待完成 |
| Phase 1.6-1.8 | 未开始 | 下一阶段为 Windows 端到端加密 WebDAV 同步，之后才进入签名、安装包、自动更新和正式发布 |
| Phase 2 macOS | 进行中 | arm64 剪贴板、生命周期、菜单栏、自定义快捷键、Keychain 与本地 DMG/PKG 已验证；登录启动交互、Intel、Developer ID、公证和环境矩阵待完成 |
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
| 全量自动测试 | 通过 | Windows 11 x64 共 150 项：142 项通过、8 项 macOS 原生测试按平台跳过、0 项失败；Application 8/8、Infrastructure 19/19、Windows 52/52、Desktop Headless 28/28 |
| `osx-arm64` Native AOT | 通过 | App Bundle 内 arm64 Mach-O 为 24,430,144 字节；0 个 AOT/裁剪警告，裸产物与 DMG 挂载 Bundle 均已实际启动 |
| `win-x64` Native AOT | 本机通过 | 当前 Windows 11 x64 原生 EXE 29,531,648 字节；无 `coreclr.dll`/`clrjit.dll`；0 个 AOT/裁剪警告并实际启动、明确退出；Runner 待验证 |
| `linux-x64` Native AOT | 待验证 | 交由 Ubuntu Runner 验证 |
| Windows 窗口/后台内存 | 未达标 | 最终 AOT 三次关闭窗口后 PWS 为 103.32/110.13/94.82 MiB，Private Bytes 为 136.59/135.54/127.82 MiB；19 分钟样本最终 PWS 88.29 MiB、Private Bytes 120.99 MiB，不能声称整体内存门槛通过 |
| macOS 窗口/后台内存 | 未达标 | 最终 AOT 三次可见窗口峰值 Physical Footprint 205.63/206.16/205.44 MiB；关闭全部窗口 3 秒后为 107.81/107.53/107.64 MiB，仍高于 100 MB |

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

真实 delayed-rendering owner 已通过 `WM_RENDERFORMAT` 完成按需渲染。Windows 11 打包版 Notepad 的交互式复制已由探针捕获，来源识别为 `Notepad`；同一应用已确认加载 `Microsoft.UI.Xaml.dll`，并通过指定 HWND 的纯文本写回、目标恢复和 `SendInput` 自动粘贴。最新隔离平台压力观察到 10,000/10,000 个自写事件，反馈和 Channel 丢弃均为 0，Private Bytes 增长 7.38 MiB。完整桌面曾因每个事件排队一次历史刷新出现严重内存放大，当前已合并为静默期单次刷新并通过 10,000 次 Headless 回归，但修复后的完整 AOT 压力尚未重跑。Codex/截图工具真实包身份和图标已自动验证，实际复制/截图仍待手动复核；Explorer、浏览器、管理员窗口、Office 和远程桌面未完成。完整记录见 `docs/WINDOWS_CLIPBOARD_VALIDATION.md`。

### 4.6 macOS 剪贴板与权限基线

macOS 平台层使用 `NSPasteboard.generalPasteboard.changeCount`、可取消的 100 ms 活跃/500 ms 空闲退避轮询、有界 Channel 和实例 nonce 来源标记。原生读取支持纯文本、HTML、RTF、PNG、TIFF、文件 URL 与 UTI 清单；Finder 的 `file:///.file/id=...` 引用优先通过同时提供的 `NSFilenamesPboardType` 还原真实路径。写回、纯文本写回和自写事件抑制均由原生集成测试覆盖。

辅助功能允许状态下，TextEdit 自动粘贴以及“捕获 TextEdit -> 切换 Finder -> 恢复 TextEdit -> Command+V”均实机通过。独立 ad-hoc 应用身份的拒绝状态返回 `AccessibilityPermissionDenied`，剪贴板仍写入成功并显示“已复制，请手动粘贴”，TextEdit 未收到注入。来源应用识别固定按 best effort 处理，本轮样本均诚实降级为 `Unknown`。完整记录见 `docs/MACOS_CLIPBOARD_VALIDATION.md`。

### 4.7 Windows 自定义快捷键与设置页

设置页不再使用四项预设下拉框，改为点击后直接按组合键录入。Avalonia UI 只提交平台无关的修饰键和按键名称，Windows 平台层显式映射为原生虚拟键并补充 `MOD_NOREPEAT`；字母、数字、数字键盘、F1-F24、导航、浏览器、媒体和常用 OEM 标点已由确定性测试覆盖。无修饰键和不支持的主键会保留录入状态并给出提示，原生注册冲突会回滚并恢复界面显示。

设置窗口已复用主窗口的品牌图、白色表面、浅灰背景、蓝色主命令、图标和 6 px 圆角体系。Release XAML 构建、640 x 520 Headless/Skia 真实帧、创建/重建、自定义快捷键录入与应用均通过；JIT 和 Native AOT 设置窗口已实际启动。Windows 桌面截图组件因本机 D3D11 设备暂停错误 `0x887A0005` 未取得桌面合成截图，但 Headless PNG 已完成视觉复核；物理按键仍保留为交互验收项。

### 4.8 macOS 桌面生命周期、权限与发布

macOS 平台层新增每用户 `flock` 所有权锁与 Unix socket 命令通道、带确认的第二实例命令、Carbon 全局快捷键、ServiceManagement 登录启动、AppKit Template 状态项、Accessibility 权限服务、Security.framework Keychain 服务和窗口原生定位。Desktop 生命周期协调器只依赖平台抽象，主窗口、快速窗口和设置窗口按需创建、关闭释放并可重复打开；最后窗口关闭不退出应用，状态菜单可暂停/恢复记录并明确退出。AppKit 调用统一切入 Avalonia UI 线程，状态项及窗口原生对象使用成对 retain/release。

实机已验证关闭全部窗口后进程和状态项继续存在、第二实例复用原进程并打开主窗口、三类窗口重复关闭/重建、菜单打开主/快速/设置窗口、暂停/恢复记录和菜单退出。默认 `Command+Shift+V` 及自定义 `Option+Control+A` 均由系统真实按键事件打开快速窗口；自定义配置重启后仍注册，最后恢复默认。快速窗口打开前保存目标应用，既有 TextEdit 恢复与自动粘贴结果继续有效。设置页仅显示 Command/Option/Control/Shift、登录启动、辅助功能和 Bundle 能力，不显示 Windows 术语。

稳定 Bundle ID 为 `com.wuliangtdi.snapboard`，标准 `.icns` 和浅色/深色 Template 状态图标已接入。最终 `osx-arm64` DMG 校验通过，挂载后的 App Bundle 实际后台启动并显示状态项，PKG 可展开；应用使用 Hardened Runtime ad-hoc 签名，PKG 未签名。当前钥匙串没有 Developer ID Application/Installer 身份，也未配置公证凭据，因此正式签名、Gatekeeper 接受和公证均未执行，不能标记完成。

## 5. 下一执行顺序

1. 在新构建上手动复核 Codex 文字复制、截图工具图片/来源，并用隔离数据目录重跑完整 AOT 桌面 10,000 次压力及未完成的真实应用/硬件矩阵。
2. 进入 `phase1/windows-sync`，实现端到端加密 WebDAV 同步；不得上传 SQLite/WAL 文件或明文凭据。
3. 同步阶段完成后再进入 Windows 签名、安装包、自动更新和正式发布。
4. 在对应硬件和签名环境补齐 Windows ARM64、macOS Intel、Linux、8 小时长稳与各平台未完成的实机场景；不得从当前 Windows x64 结果外推。

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
  - 主机只有一台 1920 x 1080 非 Retina 显示器；后台 Physical Footprint 仍超过 100 MB，10,000 次资源增长也未满足严格预算。
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

## 13. 更新规则

- 每完成一个退出条件，当天更新本文件和 `PLAN.md` 对应复选框。
- 测试失败、AOT 告警、性能超标和平台权限限制必须记录，不能只留在终端输出。
- GitHub Actions 结果应记录运行链接、Commit SHA、Runner 与 RID。
- 性能结果必须记录机器配置、构建类型、采样工具、时长和样本数据。
