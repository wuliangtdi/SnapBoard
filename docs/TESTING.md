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

- 确定性测试：消息宿主生命周期、启动取消、序列去重、有限重试、队列溢出、来源标记、反馈抑制、`INPUT` ABI 和 UIPI 结果映射。
- Windows 原生集成测试：真实系统剪贴板监听、Unicode/ANSI Text、HTML、RTF、DIB、File List、格式清单、来源进程和自写事件抑制；测试集合禁用并行，非 Windows 自动跳过。
- 交互式桌面测试：外部应用复制、前台恢复、自动粘贴和权限边界。实际结果记录在 `docs/WINDOWS_CLIPBOARD_VALIDATION.md`，不能由 fake 或 Headless 测试替代。

macOS 剪贴板测试同样分为三层：

- 确定性测试：生命周期、`changeCount` 相邻去重与有符号溢出、100/500 ms 轮询退避、队列溢出、取消、实例 nonce 反馈抑制、来源 `Unknown` 降级、PNG/TIFF 元数据和辅助功能拒绝/激活/注入失败映射。
- macOS 原生自动测试：真实 `NSPasteboard.generalPasteboard` 的 Text、HTML、RTF、PNG、TIFF、两个文件 URL、UTI 清单、完整写回、非法 DIB 拒绝、跨适配器事件和自写事件抑制；集合使用 `DisableParallelization=true`，非 macOS 自动跳过。
- 交互式桌面测试：TextEdit、Finder、Safari、Chrome、Preview、目标应用恢复、Command+V 以及辅助功能允许/拒绝。实际结果记录在 `docs/MACOS_CLIPBOARD_VALIDATION.md`；`pbcopy` CLI 结果不能冒充可见 Terminal UI 复制。

## 4. 测试数据安全

测试样本只能使用生成数据，禁止把真实剪贴板历史、WebDAV 密码、恢复码和真实令牌提交到仓库。测试失败输出正文时必须截断并脱敏。

## 5. 当前限制

Avalonia.Headless.XUnit 12.1.0 要求 xUnit v3。`SnapBoard.Desktop.HeadlessTests` 已独立切换到 xUnit 3.2.2，仓库其余测试继续使用 xUnit 2.9.3；项目文件显式移除继承的 v2 引用，避免同一测试程序集混用两个主版本。

当前 Desktop Headless 共 8 项测试，覆盖：

- 默认命令中心数据与选择状态。
- 搜索、类型筛选、删除和紧凑模式 ViewModel 行为。
- 1487 x 1058 真实 Skia 窗口渲染和稳定截图。
- 从渲染窗口输入搜索文本、激活代码筛选和切换紧凑模式。
- Desktop 组合根在 macOS/Windows 上将四个剪贴板端口显式注册为同一个平台适配器实例。

2026-07-27 在 macOS arm64 上执行全量 52 项测试：47 项通过，5 项 Windows 原生集成按平台跳过，0 项失败。macOS 平台项目 19/19，Windows 平台项目 16 项确定性测试通过、5 项原生测试跳过，Desktop Headless 8/8。菜单栏、原生全局快捷键、睡眠唤醒、多 Space、多显示器、Intel、签名/公证以及完整外部应用矩阵仍需目标环境补齐。
