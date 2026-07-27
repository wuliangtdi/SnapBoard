# SnapBoard 执行进度

> 最后更新：2026-07-27
> 当前阶段：Phase 1.2/1.3 Windows 收口进行中；macOS 原生剪贴板适配器已合入 `main`
> 总体状态：进行中
> 规则：只有代码、自动测试和目标平台验证同时满足时，功能才标记完成。

## 1. 总览

| 阶段 | 状态 | 当前结论 |
| --- | --- | --- |
| Phase 0 规划与决策 | 已完成 | 名称、MIT、三期平台、WebDAV 和同步范围已确认 |
| Phase 1.0 工程骨架 | 进行中 | 本机 Release 构建、测试和 macOS/Windows Native AOT 已通过；GitHub Runner 待验证 |
| Phase 1.1 AOT/内存基线 | 进行中 | Windows AOT 可见窗口峰值 PWS 84.47-109.50 MiB；窗口关闭后最终 PWS 66.22/66.86/130.38 MiB，Private Bytes 130.67-180.64 MiB，释放行为不稳定且尚未达标 |
| Phase 1.2 UI 生命周期 | 进行中 | 单实例、后台启动、主/快速/设置窗口、原生热键、暂停和退出已实现；托盘点击、多显示器/DPI、真实开机启动与 8 小时长稳待验收 |
| Phase 1.3 Windows 剪贴板 | 进行中 | delayed rendering、Notepad/WinUI 写回与自动粘贴及 10,000 次功能压力通过；外部应用矩阵和资源增长预算未完成 |
| Phase 1.4-1.8 | 未开始 | 数据、搜索、快速粘贴、WebDAV 和发布待后续执行 |
| Phase 2 macOS | 进行中 | arm64 原生监听、格式读写、目标恢复、自动粘贴及权限降级已合入 `main`；桌面生命周期与发布链路待完成 |
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
- [ ] 将当前 PNG 品牌图标转换为 Windows `.ico`、macOS `.icns`，补充应用标识和后续签名配置。
- [ ] 优化可见窗口内存，完成纯 Avalonia、Material Icons、Ursa 和最终壳的可重复 A/B 测量。

## 3. 已验证基线

| 检查 | 结果 | 说明 |
| --- | --- | --- |
| NuGet restore | 通过 | 已启用锁文件和漏洞审计告警即错误 |
| Release build | 通过 | 本机 0 警告、0 错误 |
| 全量自动测试 | 通过 | Windows 11 共 69 项：64 项通过、5 项仅限 macOS 原生环境的测试跳过；Windows 项目 29/29，Desktop Headless 12/12 |
| `osx-arm64` Native AOT | 通过 | arm64 Mach-O 可执行文件 23,906,928 字节，剥离后发布目录约 91.68 MiB；0 个 AOT/裁剪警告并实际启动三次 |
| `win-x64` Native AOT | 本机通过 | Windows 11 x64 原生 EXE 27,700,736 字节；无 `coreclr.dll`/`clrjit.dll`；0 个 AOT/裁剪警告并实际启动；Runner 待验证 |
| `linux-x64` Native AOT | 待验证 | 交由 Ubuntu Runner 验证 |
| Windows 窗口/后台内存 | 未达标 | AOT 三次可见窗口峰值 PWS 84.47/85.12/109.50 MiB；关闭窗口后最终 PWS 66.22/66.86/130.38 MiB，最终 Private Bytes 130.67/140.51/180.64 MiB，不能声称常驻低于 100 MB |

## 4. 重要发现

### 4.1 ORM 准入结果

`SqlSugarCoreNoDrive 5.1.4.216` 在官方整程序集 `rd.xml` 下因缺少可选数据库驱动而无法 Native AOT；`SqlSugarCoreNoDrive.Aot 5.1.4.186` 会带入多个无关驱动，并产生裁剪与 AOT 分析错误。项目没有压制这些错误，改用 Microsoft.Data.Sqlite 和显式 SQL。

### 4.2 SQLite 安全覆盖

Microsoft.Data.Sqlite 10.0.10 传递请求 `SQLitePCLRaw.bundle_e_sqlite3 2.1.11`，NuGet 审计将其关联到 CVE-2025-6965。仓库显式提升到 2.1.12，并通过 `SELECT sqlite_version()` 自动测试确保运行时 SQLite 不低于 3.50.2。

### 4.3 JSON 约束

同步协议只使用 System.Text.Json。所有协议 DTO 必须加入 `SyncJsonContext`，测试执行源生成上下文的序列化往返。Newtonsoft.Json 不得进入正式依赖图。

### 4.4 UI 与内存基线

第 2 版命令中心已按 1487 x 1058 参考画布完成视觉对照，最终报告见根目录 `design-qa.md`。Avalonia Headless 使用真实 Skia 渲染器产出稳定截图，不依赖宿主桌面和显示器缩放。

2026-07-27 重新发布最终 macOS arm64 AOT 后，三次可见窗口峰值 Physical Footprint 为 194.78/194.74/194.94 MiB，Lifetime Peak 为 198.64/198.75/198.72 MiB。2026-07-26 的 152.5 MB 单次数据仅保留为历史样本，不能替代本轮复测。结果明显高于项目目标，不能解释为“已满足 100 MB”；当前没有实现菜单栏常驻和窗口卸载，因此仍不能测量正式的“托盘常驻、窗口关闭”场景。

### 4.5 Windows 剪贴板与 AOT 基线

Windows 原生适配器使用独立 STA 线程、message-only window、`AddClipboardFormatListener`、`GetClipboardSequenceNumber`、有界 Channel 和有限退避。真实 Windows 剪贴板集成测试覆盖 Unicode/ANSI Text、HTML、RTF、DIB、File List、格式清单、来源进程、自定义来源标记和反馈抑制；自动粘贴覆盖 UIPI 结构化降级、目标 HWND/PID 与发送前前台窗口二次校验，并校验 x64 `INPUT` ABI 为 40 字节。

真实 delayed-rendering owner 已通过 `WM_RENDERFORMAT` 完成按需渲染。Windows 11 打包版 Notepad 的交互式复制已由探针捕获，来源识别为 `Notepad`；同一应用已确认加载 `Microsoft.UI.Xaml.dll`，并通过指定 HWND 的纯文本写回、目标恢复和 `SendInput` 自动粘贴。三轮 10,000 次压力测试均观察到 10,000/10,000 个自写事件，反馈事件和 Channel 丢弃均为 0；但 Private Bytes 增长超过 8 MiB 预算。Explorer、浏览器、管理员窗口、Office 和远程桌面未完成，不能标记为通过。完整记录见 `docs/WINDOWS_CLIPBOARD_VALIDATION.md`。

### 4.6 macOS 剪贴板与权限基线

macOS 平台层使用 `NSPasteboard.generalPasteboard.changeCount`、可取消的 100 ms 活跃/500 ms 空闲退避轮询、有界 Channel 和实例 nonce 来源标记。原生读取支持纯文本、HTML、RTF、PNG、TIFF、文件 URL 与 UTI 清单；Finder 的 `file:///.file/id=...` 引用优先通过同时提供的 `NSFilenamesPboardType` 还原真实路径。写回、纯文本写回和自写事件抑制均由原生集成测试覆盖。

辅助功能允许状态下，TextEdit 自动粘贴以及“捕获 TextEdit -> 切换 Finder -> 恢复 TextEdit -> Command+V”均实机通过。独立 ad-hoc 应用身份的拒绝状态返回 `AccessibilityPermissionDenied`，剪贴板仍写入成功并显示“已复制，请手动粘贴”，TextEdit 未收到注入。来源应用识别固定按 best effort 处理，本轮样本均诚实降级为 `Unknown`。完整记录见 `docs/MACOS_CLIPBOARD_VALIDATION.md`。

## 5. 下一执行顺序

1. 补齐 Windows 托盘菜单点击、物理热键、多显示器/DPI、真实开机启动和 8 小时后台常驻验收。
2. 在隔离测试数据下完成 Explorer、Chrome/Edge/Firefox，并在用户明确允许后验证管理员窗口/UIPI；Office 和远程桌面只记录实际执行结果。
3. 分析 10,000 次压力测试的 Private Bytes 增长，完成 10 分钟与 8 小时资源增长门槛。
4. 进入 Phase 1.4 SQLite Schema、迁移、单写队列、Blob、FTS5 和策略链。
5. 在 GitHub 上验证 Windows/macOS/Linux CI 与 AOT Job，并继续保留 macOS Intel、签名、公证和桌面生命周期待办。

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
状态：[~] 核心平台能力完成；菜单栏、设置引导、Keychain、Intel 与发布链路未完成
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
  - 自动粘贴增加恢复目标端口和 SendInput 前 HWND/PID 二次校验；保持 macOS 四个平台端口构建通过。
  - Windows 探针新增真实 delayed-rendering owner、指定 HWND 纯文本粘贴和 10,000 次压力模式。
验证结果：
  - .NET SDK 10.0.302；locked restore、Release build、dotnet format --verify-no-changes 均通过。
  - 全量 69 项中 64 项通过、5 项仅限 macOS 原生环境的测试跳过；Windows 29/29，Desktop Headless 12/12。
  - NuGet 直接与传递依赖漏洞为 0。
  - win-x64 Native AOT 为 27,700,736 字节，0 个 AOT/裁剪警告，无 CoreCLR/JIT 文件，并由性能脚本实际启动与退出。
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

## 10. 更新规则

- 每完成一个退出条件，当天更新本文件和 `PLAN.md` 对应复选框。
- 测试失败、AOT 告警、性能超标和平台权限限制必须记录，不能只留在终端输出。
- GitHub Actions 结果应记录运行链接、Commit SHA、Runner 与 RID。
- 性能结果必须记录机器配置、构建类型、采样工具、时长和样本数据。
