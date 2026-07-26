# SnapBoard 执行进度

> 最后更新：2026-07-26
> 当前阶段：Phase 1.0 收尾，Phase 1.1/1.2 基线并行推进
> 总体状态：进行中
> 规则：只有代码、自动测试和目标平台验证同时满足时，功能才标记完成。

## 1. 总览

| 阶段 | 状态 | 当前结论 |
| --- | --- | --- |
| Phase 0 规划与决策 | 已完成 | 名称、MIT、三期平台、WebDAV 和同步范围已确认 |
| Phase 1.0 工程骨架 | 进行中 | 本机 Release 构建、测试和 macOS arm64 AOT 已通过 |
| Phase 1.1 AOT/内存基线 | 进行中 | AOT 可见窗口单次样本 152.5 MB，超过目标；Ursa/图标/UI 依赖 A/B 待执行 |
| Phase 1.2 UI 生命周期 | 进行中 | 第 2 版命令中心 UI、核心交互与真实 Headless/Skia 视觉测试已完成 |
| Phase 1.3 Windows 剪贴板 | 未开始 | 等待 Windows 11 实机和 GitHub Windows Runner |
| Phase 1.4-1.8 | 未开始 | 数据、搜索、快速粘贴、WebDAV 和发布待后续执行 |
| Phase 2 macOS | 未开始 | 复用领域、应用、存储和同步协议 |
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
- [ ] 在 Windows 11 实机启动空壳并记录 Private Working Set/Private Bytes。
- [ ] 完成 Ursa 与纯 Avalonia 的 A/B 基准，决定是否引入运行时依赖。
- [ ] 将当前 PNG 品牌图标转换为 Windows `.ico`、macOS `.icns`，补充应用标识和后续签名配置。
- [ ] 优化可见窗口内存，完成纯 Avalonia、Material Icons、Ursa 和最终壳的可重复 A/B 测量。

## 3. 已验证基线

| 检查 | 结果 | 说明 |
| --- | --- | --- |
| NuGet restore | 通过 | 已启用锁文件和漏洞审计告警即错误 |
| Release build | 通过 | 本机 0 警告、0 错误 |
| 单元/架构/Headless 测试 | 通过 | 18 项测试；其中 7 项 Desktop 测试覆盖 ViewModel、XAML 渲染、搜索、筛选和紧凑模式 |
| `osx-arm64` Native AOT | 通过 | 原生可执行文件约 23 MB；完整未剥离发布目录约 91 MB；0 个 AOT/裁剪警告 |
| `win-x64` Native AOT | 待验证 | 必须在 Windows Runner 构建 |
| `linux-x64` Native AOT | 待验证 | 交由 Ubuntu Runner 验证 |
| 可见窗口运行内存 | 未达标 | macOS arm64 AOT 单次样本 Physical Footprint 152.5 MB、峰值 195.2 MB；超过 100/120 MB 预算线 |

## 4. 重要发现

### 4.1 ORM 准入结果

`SqlSugarCoreNoDrive 5.1.4.216` 在官方整程序集 `rd.xml` 下因缺少可选数据库驱动而无法 Native AOT；`SqlSugarCoreNoDrive.Aot 5.1.4.186` 会带入多个无关驱动，并产生裁剪与 AOT 分析错误。项目没有压制这些错误，改用 Microsoft.Data.Sqlite 和显式 SQL。

### 4.2 SQLite 安全覆盖

Microsoft.Data.Sqlite 10.0.10 传递请求 `SQLitePCLRaw.bundle_e_sqlite3 2.1.11`，NuGet 审计将其关联到 CVE-2025-6965。仓库显式提升到 2.1.12，并通过 `SELECT sqlite_version()` 自动测试确保运行时 SQLite 不低于 3.50.2。

### 4.3 JSON 约束

同步协议只使用 System.Text.Json。所有协议 DTO 必须加入 `SyncJsonContext`，测试执行源生成上下文的序列化往返。Newtonsoft.Json 不得进入正式依赖图。

### 4.4 UI 与内存基线

第 2 版命令中心已按 1487 x 1058 参考画布完成视觉对照，最终报告见根目录 `design-qa.md`。Avalonia Headless 使用真实 Skia 渲染器产出稳定截图，不依赖宿主桌面和显示器缩放。

本次 macOS arm64 AOT 可见窗口样本为 152.5 MB Physical Footprint，明显高于项目目标。该结果不能解释为“已满足 100 MB”，Phase 1.1 必须继续拆分 UI 依赖、比较图标库与控件成本，并在托盘关闭窗口后重新建立三次以上稳定样本。当前没有实现托盘和窗口卸载，因此尚不能测量正式的“托盘常驻、窗口关闭”场景。

## 5. 下一执行顺序

1. 建立纯 Avalonia、Material Icons、Ursa 和最终壳的内存/启动 A/B 测量脚本。
2. 在 Windows 11 测量 Private Working Set、Private Bytes 和冷启动，确认平台差异。
3. 在 GitHub 上验证 Windows/macOS/Linux CI 与 AOT Job。
4. 完成 SQLite CRUD/FTS5/AOT 基准和托盘关闭窗口后的常驻样本。
5. 记录 Phase 1.1 ADR，随后继续单实例、托盘和 Windows 剪贴板监听器。

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

## 7. 更新规则

- 每完成一个退出条件，当天更新本文件和 `PLAN.md` 对应复选框。
- 测试失败、AOT 告警、性能超标和平台权限限制必须记录，不能只留在终端输出。
- GitHub Actions 结果应记录运行链接、Commit SHA、Runner 与 RID。
- 性能结果必须记录机器配置、构建类型、采样工具、时长和样本数据。
