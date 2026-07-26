# SnapBoard（闪剪）

SnapBoard 是一个使用 .NET 10 和 Avalonia 12 构建的跨平台桌面剪贴板管理器。项目采用 MIT 许可证，按 Windows、macOS、Linux 三期推进，并通过 WebDAV 进行端到端加密的多设备同步。

> 当前状态：Phase 1.0 收尾、Phase 1.1/1.2 基线进行中。第 2 版命令中心 UI 与 Headless 交互测试已完成；剪贴板监听、历史存储和完整同步尚未实现。

![SnapBoard 第 2 版命令中心](docs/design/snapboard-command-center-implementation.png)

## 技术基线

- .NET SDK 10.0.302，Native AOT 作为正式发布路径。
- Avalonia 12.1.0 + CommunityToolkit.Mvvm 8.4.2。
- Material.Icons.Avalonia 3.0.2，统一桌面操作图标。
- Microsoft.Data.Sqlite 10.0.10 + SQLite FTS5，不使用运行时反射型 ORM。
- System.Text.Json 源生成协议上下文，不使用 Newtonsoft.Json。
- WebDAV 不同步 SQLite 文件，只同步加密的不可变事件分片和 Blob。
- 常驻运行内存发布目标低于 100 MB。

当前 macOS arm64 AOT 可见窗口的单次 Physical Footprint 为 152.5 MB，尚未达到目标；这项指标已记录到 `docs/PERFORMANCE.md`，不会作为完成项跳过。

## 本地构建

```bash
dotnet restore SnapBoard.slnx
dotnet build SnapBoard.slnx --configuration Release --no-restore
dotnet test SnapBoard.slnx --configuration Release --no-build --no-restore
dotnet run --project src/SnapBoard.Desktop/SnapBoard.Desktop.csproj
```

本机 Native AOT 示例：

```bash
dotnet publish src/SnapBoard.Desktop/SnapBoard.Desktop.csproj \
  --configuration Release \
  --runtime osx-arm64 \
  --self-contained true \
  -p:PublishAot=true \
  -o artifacts/publish/osx-arm64
```

Windows 使用 `win-x64`，Linux 使用 `linux-x64`。Native AOT 必须在目标操作系统的 Runner 上构建，仓库中的 GitHub Actions 已配置对应矩阵。

## 代码边界

- `src/SnapBoard.Domain`：不依赖 UI、数据库、网络和操作系统。
- `src/SnapBoard.Application`：用例与端口，只依赖领域和抽象。
- `src/SnapBoard.Infrastructure`：SQLite、文件、配置、加密等实现。
- `src/SnapBoard.Platform.*`：Windows、macOS、Linux 原生能力适配器。
- `src/SnapBoard.Sync.*`：稳定协议契约和 WebDAV 传输。
- `src/SnapBoard.Desktop`：Avalonia UI、ViewModel、生命周期和显式 DI 组合根。
- `tests`：单元、架构、Headless 和性能测试。

## 文档

- [完整计划](PLAN.md)
- [执行进度](docs/PROGRESS.md)
- [需求范围](docs/REQUIREMENTS.md)
- [总体架构](docs/ARCHITECTURE.md)
- [性能门槛](docs/PERFORMANCE.md)
- [安全设计](docs/SECURITY.md)
- [测试策略](docs/TESTING.md)
- [平台矩阵](docs/PLATFORM_MATRIX.md)
- [同步协议](docs/SYNC_PROTOCOL.md)
- [UI 规范](docs/UI_GUIDELINES.md)
- [设计 QA](design-qa.md)

## 许可证

[MIT](LICENSE)
