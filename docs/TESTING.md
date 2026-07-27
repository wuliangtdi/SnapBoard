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

- 确定性测试：消息宿主生命周期、启动取消、序列去重、有限重试、队列溢出、来源标记、反馈抑制、`INPUT` ABI、UIPI 结果映射、发送前 HWND/PID 与前台窗口二次校验、后台第二实例不激活 UI、热键冲突回滚和开机启动配置。
- Windows 原生集成测试：真实系统剪贴板监听、Unicode/ANSI Text、HTML、RTF、DIB、File List、格式清单、来源进程、自写事件抑制、两个真实热键 message-only window 冲突和 CurrentUserOnly 单实例命名管道；测试集合禁用并行，非 Windows 自动跳过。
- 交互式桌面测试：外部应用复制、前台恢复、自动粘贴和权限边界。实际结果记录在 `docs/WINDOWS_CLIPBOARD_VALIDATION.md`，不能由 fake 或 Headless 测试替代。

Windows 探针提供两项可重复的补充验证：

```powershell
dotnet run --project tools/SnapBoard.WindowsClipboardProbe -c Release -- delayed-read
dotnet run --project tools/SnapBoard.WindowsClipboardProbe -c Release -- stress --warmup 1000 --events 10000 --timeout-seconds 600
```

`delayed-read` 创建真实隐藏剪贴板 owner，并在 `WM_RENDERFORMAT` 到达时才提交 Unicode 数据。`stress` 检查 10,000 次事件的死锁、正常自写事件丢失、反馈循环、Channel 丢弃、CPU、Private Bytes 和句柄变化；功能正确性与资源预算必须分别记录，不能因事件数通过而隐去内存增长失败。

macOS 剪贴板测试同样分为三层：

- 确定性测试：生命周期、`changeCount` 相邻去重与有符号溢出、100/500 ms 轮询退避、队列溢出、取消、实例 nonce 反馈抑制、来源 `Unknown` 降级、PNG/TIFF 元数据和辅助功能拒绝/激活/注入失败映射。
- macOS 原生自动测试：真实 `NSPasteboard.generalPasteboard` 的 Text、HTML、RTF、PNG、TIFF、两个文件 URL、UTI 清单、完整写回、非法 DIB 拒绝、跨适配器事件和自写事件抑制；集合使用 `DisableParallelization=true`，非 macOS 自动跳过。
- 交互式桌面测试：TextEdit、Finder、Safari、Chrome、Preview、目标应用恢复、Command+V 以及辅助功能允许/拒绝。实际结果记录在 `docs/MACOS_CLIPBOARD_VALIDATION.md`；`pbcopy` CLI 结果不能冒充可见 Terminal UI 复制。

## 4. 测试数据安全

测试样本只能使用生成数据，禁止把真实剪贴板历史、WebDAV 密码、恢复码和真实令牌提交到仓库。测试失败输出正文时必须截断并脱敏。

## 5. 当前限制

Avalonia.Headless.XUnit 12.1.0 要求 xUnit v3。`SnapBoard.Desktop.HeadlessTests` 已独立切换到 xUnit 3.2.2，仓库其余测试继续使用 xUnit 2.9.3；项目文件显式移除继承的 v2 引用，避免同一测试程序集混用两个主版本。

当前 Desktop Headless 共 12 项测试，覆盖：

- 默认命令中心数据与选择状态。
- 搜索、类型筛选、删除和紧凑模式 ViewModel 行为。
- 1487 x 1058 真实 Skia 窗口渲染和稳定截图。
- 从渲染窗口输入搜索文本、激活代码筛选和切换紧凑模式。
- Desktop 组合根在 macOS/Windows 上将四个剪贴板端口显式注册为同一个平台适配器实例。
- 快速窗口真实 XAML 渲染、设置窗口关闭后重新创建、后台第二实例不激活主窗口，以及暂停记录时持续排空 100 个事件但不读取正文、恢复后继续读取。

2026-07-27 在 Windows 11 x64、.NET SDK 10.0.302 上执行全量 69 项测试：64 项通过、5 项仅限 macOS 原生环境的测试跳过、0 项失败。Windows 平台项目 29/29，macOS 平台项目 14 项确定性测试通过、5 项原生测试跳过，Desktop Headless 12/12。`dotnet format --verify-no-changes` 和直接/传递 NuGet 漏洞检查均通过。托盘菜单点击、物理热键、多显示器/DPI、真实开机启动、管理员目标和完整外部应用矩阵仍需交互验收。
