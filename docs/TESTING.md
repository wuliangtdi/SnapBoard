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

macOS 10,000 次原生事件压力测试使用：

```bash
dotnet run --project tools/SnapBoard.MacOSClipboardProbe -c Release --no-build -- \
  stress --events 10000 --warmup 100 --read-interval 250
```

探针并行运行真实监听器，检查每次写入、定期读回、来源标记、反馈事件、Channel 丢弃、RSS、线程和文件描述符。事件功能通过与 `< 8 MiB` 资源预算分别判定，不能用零错误掩盖资源增长。

## 4. 测试数据安全

测试样本只能使用生成数据，禁止把真实剪贴板历史、WebDAV 密码、恢复码和真实令牌提交到仓库。测试失败输出正文时必须截断并脱敏。

## 5. 当前限制

Avalonia.Headless.XUnit 12.1.0 要求 xUnit v3。`SnapBoard.Desktop.HeadlessTests` 已独立切换到 xUnit 3.2.2，仓库其余测试继续使用 xUnit 2.9.3；项目文件显式移除继承的 v2 引用，避免同一测试程序集混用两个主版本。

当前 Desktop Headless 共 26 项测试，覆盖：

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

2026-07-27 在 Windows 11 x64、.NET SDK 10.0.302 上执行 `phase1/windows-history-search` 最终代码：全量共 143 项，135 项通过、8 项 macOS 原生测试按平台跳过、0 项失败。项目分布为 Application 7、Architecture 2、Domain 1、Infrastructure 18、Linux 1、macOS 28 通过/8 跳过、Windows 48、Sync 3、Desktop Headless 27。locked restore、Release build、`dotnet format --verify-no-changes`、直接/传递 NuGet 漏洞检查和 `win-x64` Native AOT 均通过。

新增自动验证覆盖：

- Schema v1-v4 首次迁移、逐版本升级、重复初始化、事务回滚、WAL/外键/busy timeout 和 SQLite 安全版本下限。
- 数据库损坏的时间戳备份、重新建库、诊断结果和恢复后 CRUD。
- 历史 CRUD、相邻去重、重启后历史/置顶/标签/设置一致性、使用次数、软删除、清空及条数/时间/容量保留策略。
- FTS5 中文、英文、代码、特殊字符、空查询、1,024 字符限制、取消、稳定分页以及类型/来源/时间/标签/置顶筛选。
- Blob 临时文件、原子移动、事务失败回滚、图片外置、320 x 180 缩略图、共享引用计数、删除/清空和精确相对路径孤儿清理；初始化返回时旧孤儿仍保留，证明目录扫描不在启动关键路径。
- 应用黑名单、密码管理器、敏感/临时格式、仅文本规则、载荷大小限制、饱和加法以及保存成功但保留策略待重试的语义。
- Windows Credential Manager 的真实新增/读取/覆盖/删除/不存在往返，以及拒绝、无效名称和超限输入的确定性状态。
- 正式历史 UI 的分页增量加载、旧搜索取消、图片按需加载和普通/纯文本写回请求。
- 来源 EXE 路径重启投影、ViewModel 单次异步元数据解析、微信/企业微信本地化回退、真实 Shell 图标像素，以及绕过缓存连续 64 次提取后的 GDI Object 计数。

100,000 条检索场景使用生成数据，命令为：

```powershell
dotnet run --project tests/SnapBoard.PerformanceTests/SnapBoard.PerformanceTests.csproj `
  --configuration Release --no-build --no-restore -- history-search
```

来源路径投影接入后重新执行该场景：导入 100,000 条平均 554.7 字符的混合数据耗时 27,701.92 ms，分别测量中文、英文、代码的选择性与宽查询，各 50 次，共 300 次；总体 P95 2.32 ms、最大 7.15 ms。性能测试只输出计数、耗时和大小，不打印正文。它不是 `dotnet test` 的一部分，必须单独执行。

Windows 原生探针最新样本为 100 次预热和 10,000 次事件，事件匹配 10,000/10,000，反馈和 Channel 丢弃为 0；Private Bytes 增长 8.46 MiB，严格资源预算失败。功能与资源结论必须继续分开；8 小时长稳未执行。
