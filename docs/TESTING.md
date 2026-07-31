# SnapBoard 测试策略

## 1. 本地质量门槛

```bash
dotnet restore SnapBoard.slnx --locked-mode
dotnet build SnapBoard.slnx --configuration Release --no-restore
dotnet test SnapBoard.slnx --configuration Release --no-build --no-restore
dotnet format SnapBoard.slnx --verify-no-changes --no-restore
dotnet list SnapBoard.slnx package --vulnerable --include-transitive --no-restore
```

合并前要求：NuGet 审计无漏洞警告、Release 构建 0 警告、全部测试通过。Native AOT 在目标操作系统 Runner 上单独发布，不能用某个平台的成功结果代替其他平台。

## 2. 测试项目

| 项目 | 责任 |
| --- | --- |
| Domain.Tests | 值对象、去重和纯领域规则 |
| Application.Tests | 用例、过滤责任链、队列和取消行为 |
| Infrastructure.Tests | SQLite、迁移、FTS5、Blob、配置和加密适配 |
| Platform.*.Tests | 原生格式解析、能力判断和平台边界 |
| Sync.WebDav.Tests | 路径、ETag、条件请求、兼容性和错误恢复 |
| Desktop.HeadlessTests | ViewModel、真实 XAML/Skia 渲染、窗口状态和核心交互 |
| Architecture.Tests | 程序集依赖方向和禁用依赖 |
| PerformanceTests | BenchmarkDotNet 微基准，不进入 `dotnet test` |

## 3. 测试类型

- 单元测试：无真实磁盘/网络，覆盖纯规则和边界值。
- SQLite 集成测试：每个测试独立临时目录，验证事务、迁移、FTS5 和恢复。
- WebDAV 合约测试：对模拟服务执行 MKCOL、PROPFIND、GET、条件 PUT 和失败重试。
- Headless UI：验证编译绑定、键盘导航、空/加载/错误状态和窗口尺寸。
- 平台实机：全局快捷键、托盘、焦点恢复、权限和自动粘贴必须人工加自动脚本联合验收。
- 长稳测试：8 小时、10,000 次变化、网络断开恢复和多次休眠唤醒。

Windows 剪贴板测试分为三层：

- 确定性测试：消息宿主生命周期、启动取消、序列去重、有限重试、队列溢出、来源标记、反馈抑制、`INPUT` ABI、UIPI 结果映射、发送前 HWND/PID 与前台窗口二次校验、后台第二实例不激活 UI、热键冲突回滚、自定义按键映射和开机启动配置。
- Windows 原生集成测试：真实系统剪贴板监听、Unicode/ANSI Text、HTML、RTF、DIB、File List、格式清单、来源进程、自写事件抑制、两个真实热键 message-only window 冲突和 CurrentUserOnly 单实例命名管道；测试集合禁用并行，非 Windows 自动跳过。
- 交互式桌面测试：外部应用复制、前台恢复、自动粘贴和权限边界。实际结果记录在 `docs/WINDOWS_CLIPBOARD_VALIDATION.md`，不能由 fake 或 Headless 测试替代。

Windows 探针提供两项可重复的补充验证：

```powershell
dotnet run --project tools/SnapBoard.WindowsClipboardProbe -c Release -- delayed-read
dotnet run --project tools/SnapBoard.WindowsClipboardProbe -c Release -- stress --warmup 1000 --events 10000 --timeout-seconds 600
```

`delayed-read` 创建真实隐藏剪贴板 owner，并在 `WM_RENDERFORMAT` 到达时才提交 Unicode 数据。`stress` 检查 10,000 次事件的死锁、正常自写事件丢失、反馈循环、Channel 丢弃、CPU、Private Bytes 和句柄变化；功能正确性与资源预算必须分别记录，不能因事件数通过而隐去内存增长失败。

macOS 剪贴板测试同样分为三层：

- 确定性测试：剪贴板生命周期、`changeCount` 去重/溢出、轮询退避、队列溢出、取消、反馈抑制、来源降级、PNG/TIFF、权限失败映射，以及单实例命令/确认、窗口生命周期、状态菜单、暂停、退出、主线程调度、快捷键映射/冲突/回滚/持久化/默认恢复、登录启动能力和权限状态。
- macOS 原生自动测试：真实 `NSPasteboard.generalPasteboard` 的 Text、HTML、RTF、PNG、TIFF、两个文件 URL、UTI 清单、完整写回、非法 DIB 拒绝、跨适配器事件和自写事件抑制；`flock` 所有权、真实 Unix socket 确认、首实例监听前不可抢占与不完整客户端超时；Keychain 临时密钥新增、读取、覆盖和删除。集合使用 `DisableParallelization=true`，非 macOS 自动跳过。
- 交互式桌面测试：TextEdit、Finder、Safari、Chrome、Preview、目标应用恢复、Command+V、辅助功能允许/拒绝、菜单栏、关闭窗口后台常驻、第二实例、自定义物理快捷键和明确退出。实际结果记录在 `docs/MACOS_CLIPBOARD_VALIDATION.md`；`pbcopy` CLI 结果不能冒充可见 Terminal UI 复制。

macOS 10,000 次原生事件功能压力测试使用：

```bash
dotnet run --project tools/SnapBoard.MacOSClipboardProbe -c Release --no-build -- \
  stress --events 10000 --warmup 100 --read-interval 250
```

探针并行运行真实监听器，检查每次写入、定期读回、来源标记、反馈事件、Channel 丢弃、Physical Footprint、RSS、线程和文件描述符。`dotnet run` 的 framework-dependent 冷启动会包含 JIT、分层编译和程序集按需加载，只用于功能结果；正式 `< 8 MiB` 资源预算必须在目标机发布并运行 Native AOT 探针：

```bash
dotnet publish tools/SnapBoard.MacOSClipboardProbe/SnapBoard.MacOSClipboardProbe.csproj \
  -c Release -r osx-arm64 --self-contained true -p:PublishAot=true
tools/SnapBoard.MacOSClipboardProbe/bin/Release/net10.0/osx-arm64/publish/SnapBoard.MacOSClipboardProbe \
  stress --events 10000 --warmup 100 --read-interval 250
```

探针会先预热自身的资源采样路径，再通过 `proc_pid_rusage(RUSAGE_INFO_V6)` 读取 Physical Footprint；RSS 只保留为诊断数据。事件功能通过与资源预算必须分别判定，不能用零错误掩盖增长，也不能把 framework-dependent 冷启动外推为 Native AOT 泄漏。

## 4. 测试数据安全

测试样本只能使用生成数据，禁止把真实剪贴板历史、WebDAV 密码、恢复码和真实令牌提交到仓库。测试失败输出正文时必须截断并脱敏。

## 5. 当前限制

Avalonia.Headless.XUnit 12.1.0 要求 xUnit v3。`SnapBoard.Desktop.HeadlessTests` 已独立切换到 xUnit 3.2.2，仓库其余测试继续使用 xUnit 2.9.3；项目文件显式移除继承的 v2 引用，避免同一测试程序集混用两个主版本。

当前 Desktop Headless 共 29 项测试，覆盖：

- 默认命令中心数据与选择状态。
- 搜索、类型筛选、删除和紧凑模式 ViewModel 行为。
- 1487 x 1058 真实 Skia 窗口渲染和稳定截图。
- 从渲染窗口输入搜索文本、激活代码筛选和切换紧凑模式。
- Desktop 组合根在 macOS/Windows 上将四个剪贴板端口显式注册为同一个平台适配器实例。
- 快速窗口真实 XAML 渲染、设置窗口 640 x 520 真实帧与关闭后重新创建、自定义快捷键录入与应用、后台第二实例不激活主窗口，以及暂停记录时持续排空 100 个事件但不读取正文、恢复后继续读取。
- macOS 三类窗口按需创建/关闭/重建、状态菜单命令、关闭窗口后台常驻、第二实例激活、暂停/恢复、资源释放、退出顺序和已保存快捷键启动冲突后回退默认。
- 设置页 macOS Command/Option/Control/Shift 术语、直接组合键录入、辅助功能状态/受限模式、登录启动能力、恢复默认和无 Windows 专属文字。
- 正式 ViewModel 每页增量加载、搜索取消后的代际隔离、真实内容写回请求，以及采集协调器持久化失败后继续消费后续事件。

2026-07-27 在 Windows 11 x64、.NET SDK 10.0.302 上执行的历史基线为全量 79 项：74 项通过、5 项仅限 macOS 原生环境的测试跳过、0 项失败；Windows 平台项目 37/37、Desktop Headless 14/14。

2026-07-27 在 macOS 26.2 arm64、.NET SDK 10.0.302 上执行最终代码：全量 103 项中 96 项通过、7 项 Windows 原生测试按平台跳过、0 项失败。项目分布为 Application 1、Architecture 2、Domain 1、Infrastructure 1、Linux 1、Windows 30 通过/7 跳过、Sync 3、macOS 36、Desktop Headless 21。locked restore、Release build、`dotnet format --verify-no-changes` 和直接/传递 NuGet 漏洞检查均通过。菜单栏、物理快捷键、第二实例和窗口后台常驻已交互验收；登录启动真实开关/重新登录、权限撤销后重授予、睡眠唤醒、多 Space、多显示器、Retina、全屏、Office、远程桌面和可见 Terminal UI 仍待验收。

## 6. Windows 本地历史与检索验证

2026-07-28 在 Windows 11 x64、.NET SDK 10.0.302 上执行 `phase1/windows-history-search` 当前代码：全量共 150 项，142 项通过、8 项 macOS 原生测试按平台跳过、0 项失败。项目分布为 Application 8、Architecture 2、Domain 1、Infrastructure 19、Linux 1、macOS 28 通过/8 跳过、Windows 52、Sync 3、Desktop Headless 28。locked restore、Release build、`dotnet format --verify-no-changes`、直接/传递 NuGet 漏洞检查和 `win-x64` Native AOT 均通过。

新增自动验证覆盖：

- Schema v1-v5 首次迁移、逐版本升级、重复初始化、事务回滚、WAL/外键/busy timeout 和 SQLite 安全版本下限；v5 增加 AUMID、Package Family 和来源归属依据。
- 数据库损坏的时间戳备份、重新建库、诊断结果和恢复后 CRUD。
- 历史 CRUD、相邻去重、重启后历史/置顶/标签/设置一致性、使用次数、软删除、清空及条数/时间/容量保留策略。
- FTS5 中文、英文、代码、特殊字符、空查询、1,024 字符限制、取消、稳定分页以及类型/来源/时间/标签/置顶筛选。
- Blob 临时文件、原子移动、事务失败回滚、图片外置、320 x 180 缩略图、共享引用计数、删除/清空和精确相对路径孤儿清理；初始化返回时旧孤儿仍保留，证明目录扫描不在启动关键路径。
- 应用黑名单、密码管理器、敏感/临时格式、仅文本规则、载荷大小限制、饱和加法以及保存成功但保留策略待重试的语义。
- Windows Credential Manager 的真实新增/读取/覆盖/删除/不存在往返，以及拒绝、无效名称和超限输入的确定性状态。
- 正式历史 UI 的分页增量加载、旧搜索取消、图片按需加载、普通/纯文本写回请求，以及 10,000 次连续 `HistoryChanged` 只合并为一次静默期刷新。
- 剪贴板事件时 owner/foreground PID 传递、序列一致性归属、来源 EXE/AUMID/Package Family 重启投影、ViewModel 身份转发、Codex/截图工具真实 AppsFolder 图标像素、注册 `PNG` 往返，以及绕过缓存连续 64 次提取后的 GDI Object 计数。

100,000 条检索场景使用生成数据，命令为：

```powershell
dotnet run --project tests/SnapBoard.PerformanceTests/SnapBoard.PerformanceTests.csproj `
  --configuration Release --no-build --no-restore -- history-search
```

Schema v5 来源身份投影接入后重新执行该场景：导入 100,000 条平均 554.7 字符的混合数据耗时 31,113.58 ms，分别测量中文、英文、代码的选择性与宽查询，各 50 次，共 300 次；总体 P95 2.37 ms、最大 7.49 ms。性能测试只输出计数、耗时和大小，不打印正文。它不是 `dotnet test` 的一部分，必须单独执行。

Windows 原生探针最新干净样本为 100 次预热和 10,000 次事件，事件匹配 10,000/10,000，反馈和 Channel 丢弃为 0；Private Bytes 增长 7.38 MiB，满足该隔离探针的 `< 8 MiB` 预算。测试期间同时发现旧 AOT 桌面进程因每个历史事件都排队全量刷新而增长到 7.24 GB Private Bytes；当前代码已合并刷新并有 10,000 次 Headless 回归测试，但尚未用隔离数据目录重新执行完整 AOT 桌面端到端压力。功能、平台探针和完整桌面资源结论必须继续分开；8 小时长稳未执行。

## 7. macOS 共享历史与检索验证

2026-07-28 在 macOS 26.2 arm64、Apple M4、APFS 和 .NET SDK 10.0.302 上执行 `phase2/macos-history-search-validation`：全量 159 项中 144 项通过、15 项 Windows 原生测试按平台跳过、0 项失败。项目分布为 Application 9、Architecture 2、Domain 1、Infrastructure 26、Linux 1、macOS 36、Windows 37 通过/15 跳过、Sync 3、Desktop Headless 29。locked restore、Release build、format、NuGet 漏洞审计和 `osx-arm64` Native AOT 均通过。

本轮在共享测试层新增或强化：

- Schema v1-v5 分别新建并重复迁移，v4 实际行升级 v5 后 Windows 身份列保持 NULL/Unknown。
- 重启后标签、置顶、使用次数/时间、软删除/时间和设置一致；损坏备份恢复、WAL、外键、busy timeout 和重复初始化继续通过。
- PNG/TIFF 原图、缩略图、临时文件、原子替换、回滚、共享引用、删除/清空/过期及精确孤儿复查；损坏 TIFF 保留原图并安全跳过缩略图。
- macOS 未注册 Windows 来源解析器，Unknown 来源保持通用图标且 UI 无 Windows 专属术语。
- 正式 ViewModel 的分页、筛选、取消、旧查询隔离、按需图片，以及 10,000 次 `HistoryChanged` 静默期一次刷新。

100,000 条独立性能命令导入耗时 15,289.62 ms；150 次目标查询 P95 1.04 ms、最大 1.72 ms，300 次全部查询 P95 1.01 ms、最大 2.23 ms，满足 `< 80 ms` 和 `<= 200 ms`。真实外部应用、AOT、资源与未执行项见 `docs/MACOS_CLIPBOARD_VALIDATION.md`；性能命令不是 `dotnet test` 的一部分，发布验证必须单独运行。

## 8. Windows 来源应用图标跨设备同步验证

2026-07-31 在 Windows 11 x64、.NET SDK 10.0.302 上完成阶段 A。同步协议保持 v1，远端目录保持 `SnapBoard/v1`，没有旧载荷兼容或远端空间迁移代码。自动测试重点覆盖：

- Windows EXE、Explorer 和已安装 AppsFolder 应用产生固定 32 x 32、stride 128、4096 字节 BGRA 快照，HICON/GDI 资源释放和空缓存重试保持有效。
- 图标提供器首次为空只重试一次；异常、无身份或非规范像素不阻断剪贴板正文保存。
- SQLite v9 字段、SHA-256 Blob 去重、重启读取、相邻重复只补缺失图标、事务失败清理、损坏 Blob 拒绝，以及软删除、清空、保留期和远端墓碑的引用释放。
- 当前源生成 JSON 的图标描述符往返；非法媒体类型、长度、格式版本、尺寸或 stride 被拒绝。
- Outbox 引用图标 Blob；同步服务先处理 Blob 再处理事件。两份独立数据目录的端到端测试不传源 EXE 路径，目标目录仍读到逐字节相同的图标，删除墓碑后本地图标引用消失。
- 主窗口和快速窗口共用的 ViewModel 优先读取持久化快照；本地安装路径不同不会覆盖同步快照，快照不可读时回退本机解析器且不崩溃。

发布级命令全部通过：

```powershell
dotnet restore SnapBoard.slnx --locked-mode
dotnet format SnapBoard.slnx --verify-no-changes --no-restore
dotnet build SnapBoard.slnx --configuration Release --no-restore
dotnet test SnapBoard.slnx --configuration Release --no-build --no-restore
dotnet publish src/SnapBoard.Desktop/SnapBoard.Desktop.csproj `
  -c Release -r win-x64 --self-contained true --no-restore -p:PublishAot=true
```

全量共 495 项：473 项通过、22 项按 macOS 原生环境或外部 WebDAV 条件跳过、0 项失败。Native AOT 输出 0 个 trim/AOT 警告；`SnapBoard.Desktop.exe` 为 40,489,472 字节，`SnapBoard.StorageMigrator.exe` 为 4,514,304 字节。迁移器没有 `.dll`、`.deps.json` 或 `.runtimeconfig.json` sidecar，无参数退出码为 4。主程序使用随机隔离数据根创建 v9 数据库和非零主窗口句柄，并通过 `--exit` 以 0 退出，临时目录随后删除。

本轮没有逐项人工操作 Chrome、Edge、微信、Codex、截图工具和 Store 应用，也没有两份正式安装之间的可视同步验收；这些仍是 Windows 阶段的实机限制。macOS 只验证共享项目继续编译，不能据此推断本机快照生成完成；阶段 B 必须在 macOS 环境执行原生采集、双向同步和 Native AOT。

## 9. macOS 来源应用图标跨设备同步验证

2026-07-31 在 macOS 26.2 (25C56)、Apple M4 arm64、.NET SDK 10.0.302 上完成阶段 B，开发基线为 `6d2c240d043c07dfb95897b2b4adce6a8642271d`。协议保持 v1，远端目录保持 `SnapBoard/v1`，没有新增兼容、迁移或双协议代码。

- macOS 组合根把既有 `MacOSClipboardSourceApplicationMetadataResolver` 同实例注册为元数据解析器和图标提供器；`NSWorkspace`/App Bundle 访问仍经过 `IPlatformMainThreadDispatcher`，并复用原有 256 项有界缓存。
- 原生测试直接读取系统 TextEdit 与 Finder Bundle，验证固定 32 x 32、stride 128、4096 字节 BGRA 预乘 Alpha、非空像素、AppKit 调度边界以及元数据/图标缓存复用。
- 双设备端到端测试在当前 v1 载荷上分别执行 Windows -> macOS 与 macOS -> Windows，目标端没有来源可执行路径，宽、高、stride 和 4096 字节像素逐字节一致；图标继续复用加密 Blob，删除墓碑和引用生命周期保持有效。
- arm64 AOT App Bundle 使用隔离数据根真实采集 TextEdit 与 Finder。数据库记录的来源路径分别为 `/System/Applications/TextEdit.app/Contents/MacOS/TextEdit` 和 `/System/Library/CoreServices/Finder.app/Contents/MacOS/Finder`，归属依据均为 `ForegroundWindowAtChange`；图标 Blob 媒体类型为 `application/vnd.snapboard.source-icon-bgra32`，大小均为 4096 字节，文件 SHA-256 与数据库键一致，AOT 主窗口显示对应来源名称和持久化图标。
- arm64 SDK 与通过 Rosetta 运行的官方 x64 SDK 都完成 locked restore、Release build 和全量测试。两轮均为 469 项通过、26 项按 Windows 原生环境或外部 WebDAV 条件跳过、0 项失败；x64 Host 报告 `Architecture: x64`、`RID: osx-x64`，build 为 0 警告、0 错误。
- `osx-arm64` 与使用 x64 SDK 原生发布的 `osx-x64` self-contained Native AOT 均通过 `Verify-NativePublish.sh`；桌面主程序和迁移器分别为 arm64/x86_64 Mach-O，迁移器无参数退出码均为 4，未发现 CoreCLR、hostfxr 或迁移器托管 sidecar。

AOT 产物证据：

| RID | 文件 | 字节 | SHA-256 |
| --- | --- | ---: | --- |
| osx-arm64 | SnapBoard.Desktop | 36,252,640 | `d5921f9fc43ddf6d454c6120b696e168a371588dac4c6d6abe62395ceef894d8` |
| osx-arm64 | SnapBoard.StorageMigrator | 8,356,968 | `eec02eccbe5b4a9b1a104101371bbf61d970d70dafa4a80ff605e7087d179e04` |
| osx-x64 | SnapBoard.Desktop | 37,281,072 | `9943361f4fb24d4cd65736f29123b0708c32a2328d35e08e9e6a61b55ef4dceb` |
| osx-x64 | SnapBoard.StorageMigrator | 8,554,496 | `3a4ce9ed359da687b08317a5c2935321a2ef55fbba1416e120e3252a6724e67d` |

两个 RID 均没有 trim/AOT 警告。链接阶段各出现两条来自官方 .NET Apple NativeAOT 静态库的 clang module-cache 调试信息警告（Foundation 与 `_SwiftConcurrencyShims` 的 `.pcm` 不存在）；它们只影响调试信息，仓库已有同类记录，不影响原生文件、启动或校验结论。x64 测试与 AOT 在 Apple Silicon 的 Rosetta x64 运行时执行，不冒充 Intel 匹配硬件；更广泛的两台正式安装、真实 WebDAV 和可视 UI 双机矩阵仍属于整体验收限制，不改变本阶段当前协议的双向像素往返结论。

## 10. 数据目录迁移的来源应用图标引用验证

2026-07-31 在 Windows 11 x64、.NET SDK 10.0.302 上修复 SQLite v9 迁移复检遗漏。`content_blobs.ref_count` 必须同时等于正文表示、缩略图和 `clipboard_items.source_application_icon_blob_hash` 的引用总数；只校验前两类会使包含来源应用图标的有效数据库以 `verification-failed` 回滚。

`StorageMigrationExecutorTests.MigratesSourceApplicationIconBlobReferences` 通过完整复制、迁移器进程确认和目标库复检，覆盖一个图标引用及三条记录共享一个图标 Blob。测试同时断言迁移状态为 `Completed`、目标记录的 4096 字节像素保持不变，且目标库引用计数分别为 1 和 3。该测试只使用共享 SQLite 与伪平台服务，会进入 GitHub 的 Windows、Apple Silicon macOS 和 Intel macOS 测试矩阵。

本轮 locked restore、format、Release build 和完整测试通过：全量 497 项中 475 项通过、22 项按平台或外部服务条件跳过、0 项失败。`win-x64` Native AOT 为 0 个 trim/AOT 警告；桌面主程序和独立迁移器均生成，迁移器无托管 sidecar且无参数退出码为 4。macOS 两个 RID 的 Native AOT 仍必须由提交后的 GitHub 对应 Runner 验证。
