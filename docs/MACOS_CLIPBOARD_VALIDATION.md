# macOS 剪贴板验收记录

> 日期：2026-07-27
> 环境：Mac mini，Apple M4 10 核，16 GB，macOS 26.2 (25C56)，arm64
> SDK：.NET SDK 10.0.302（由 `global.json` 锁定）
> 分支：`phase2/macos-clipboard`

## 1. 本轮范围

本轮只验收 Phase 2.1 的 macOS 原生剪贴板监听、读取、写回、目标应用恢复、自动粘贴和辅助功能权限降级。菜单栏、全局快捷键、登录启动、单实例、Keychain、应用签名、公证和正式安装包不在本轮完成范围，也未标记完成。

平台代码全部位于 `src/SnapBoard.Platform.MacOS`。Application 和 UI 不直接引用 AppKit、Objective-C Runtime、CoreFoundation、CoreGraphics 或 Accessibility API；Desktop 组合根只按 `OperatingSystem.IsMacOS()` 显式注册共享的 `IClipboardMonitor`、`IClipboardContentReader`、`IClipboardWriter` 和 `IAutomaticPasteService`。

## 2. 确定性测试

不访问真实剪贴板的测试覆盖：

- 监听生命周期、取消和轮询任务退出。
- `changeCount` 相邻去重以及 `NSInteger` 有符号溢出保持位模式。
- 100 ms 活跃、500 ms 空闲轮询退避及配置边界。
- 有界 Channel 队列溢出、保留最新事件和丢弃计数。
- 写回 changeCount 反馈抑制，轮询 tick 不触发正文读取。
- 来源无法可靠识别时固定降级为 `ClipboardSourceAccessStatus.Unknown`。
- PNG/TIFF 头部元数据解析，不把 PNG/TIFF 冒充 Windows DIB/DIBV5。
- 辅助功能拒绝、目标不存在、目标激活失败和事件创建失败的结构化降级。
- Desktop 组合根在 macOS 和 Windows 上保持显式注册及同实例语义。

## 3. macOS 原生自动测试

`MacOSClipboardNativeIntegrationTests` 使用真实 `NSPasteboard.generalPasteboard`，测试集合设置 `DisableParallelization=true`，避免并行测试相互覆盖系统剪贴板。原生自动测试覆盖：

- Text、HTML、RTF、PNG、TIFF、两个文件 URL 和 UTI 格式清单。
- 完整写回、纯文本写回以及共享图片编码往返。
- 反向域名类型 `com.wuliangtdi.snapboard.source.v1` 和每实例 nonce。
- 同一适配器自写事件抑制、不同适配器写入事件可见。
- macOS 拒绝 DIB 写入且不清空现有剪贴板，Windows 写入端继续只接受 DIB/DIBV5。

本轮 `SnapBoard.Platform.MacOS.Tests` 为 19/19 通过。全量解决方案共 52 项：47 项通过、5 项 Windows 原生集成因当前平台不是 Windows 而跳过，0 项失败。

## 4. 交互式桌面结果

所有写入内容均为本轮生成的测试文本或仓库内公开文件；探针默认只输出格式、布尔状态、路径和来源状态，不转储用户剪贴板历史。

| 场景 | 结果 | 实际证据与限制 |
| --- | --- | --- |
| TextEdit 文本复制 | 通过 | 真实 TextEdit 选择并复制后读取到 Text 和 RTF，格式含 `public.utf8-plain-text`、`public.rtf`；来源按 best effort 返回 `Unknown` |
| Finder 文件复制 | 通过 | 在 Finder 复制仓库 `README.md`，读取到真实路径 `/Users/ozonect/CSharpProject/Test02/README.md`，格式含 `public.file-url`、`NSFilenamesPboardType` 和图标 TIFF；未把 `file:///.file/id=...` 当成 POSIX 路径 |
| Google Chrome HTML 复制 | 通过 | 在真实 Chrome 的 `example.com` 页面选择并复制，读取到 Text 和 HTML，格式含 `public.html`、`public.utf8-plain-text` 及 Chromium 自定义类型 |
| Safari HTML/RTF 复制 | 通过 | 在真实 Safari 的 `example.com` 页面选择并复制，读取到 Text、HTML、RTF 和 WebArchive 格式 |
| Preview 图片复制 | 通过 | 在 Preview 打开仓库 `snapboard-logo.png` 并复制，读取到 `public.png`、`public.tiff`；共享模型选择 `PortableNetworkGraphics`，没有伪装为 DIB |
| 命令行文本互操作 | 通过 | 当前 macOS shell 使用 `pbcopy` 写入生成文本，监听读取到 Text 和 `public.utf8-plain-text` 等文本格式，`DroppedEvents=0` |
| 可见 Terminal UI 选择复制 | 未完成 | 桌面控制工具因终端安全限制拒绝操作 Terminal；本轮只记录 `pbcopy` CLI 路径，不能声称 Terminal UI 复制通过 |
| 自动粘贴允许状态 | 通过 | `AXIsProcessTrusted` 与 `CGPreflightPostEventAccess` 均为允许；TextEdit 实际收到生成文本，返回 `PasteStatus=Pasted; Reason=None` |
| 目标应用恢复 | 通过 | 捕获 `com.apple.TextEdit` 后切换到 Finder，等待期间保持 Finder 前台；服务随后恢复 TextEdit 并发送 Command+V，TextEdit 实际出现 `SnapBoard restored TextEdit after Finder verified` |
| 辅助功能拒绝状态 | 通过 | 使用独立 ad-hoc 应用身份运行同一 AOT 探针，检测为 `AccessibilityPermissionGranted=False`；剪贴板写入成功，返回 `ManualPasteRequired; Reason=AccessibilityPermissionDenied` 和“已复制，请手动粘贴”，TextEdit 保持空白 |
| 来源应用识别 | 受限但符合设计 | NSPasteboard 不可靠暴露 owner 应用，所有不能确定的样本返回 `Unknown`，未按前台窗口猜测来源 |

## 5. Native AOT 与质量门槛

执行并通过：

```bash
dotnet restore SnapBoard.slnx --locked-mode
dotnet build SnapBoard.slnx --configuration Release --no-restore
dotnet test SnapBoard.slnx --configuration Release --no-build --no-restore
dotnet format SnapBoard.slnx --verify-no-changes --no-restore
dotnet list SnapBoard.slnx package --vulnerable --include-transitive --no-restore
dotnet publish src/SnapBoard.Desktop/SnapBoard.Desktop.csproj \
  --configuration Release --runtime osx-arm64 --self-contained true \
  -p:PublishAot=true -p:StripSymbols=true
```

结果：

- Release build：0 警告、0 错误。
- 格式校验：通过，无需改写。
- NuGet 审计：直接和传递依赖均未发现已知漏洞。
- AOT：0 个 AOT/裁剪警告。
- 产物：23,906,928 字节的 arm64 Mach-O；`lipo` 确认为非通用 arm64；发布目录约 91.68 MiB。
- 启动：最终 AOT 产物实际启动三次，每次均由 CoreGraphics 检测到该 PID 的可见主窗口后再采样。

本轮没有在 Intel 机器或 `osx-x64` Runner 上执行，因此不能用 arm64 结果推断 x64 通过。

## 6. 性能结果

桌面 AOT 三次独立进程、每次可见窗口后采样 10 秒：

| 轮次 | 启动到可见窗口 | 峰值 Physical Footprint | 峰值 Resident Size | 平均 CPU | 能耗增量 |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 3974.77 ms | 194.78 MiB | 162.11 MiB | 0.014% | 26.541 mJ |
| 2 | 1288.41 ms | 194.74 MiB | 162.55 MiB | 0.015% | 25.098 mJ |
| 3 | 919.26 ms | 194.94 MiB | 162.08 MiB | 0.017% | 33.354 mJ |

实际运行 `WatchAsync` 的 AOT 探针另测 12 秒，在中间 10 秒无变化计量窗口内平均 CPU 0.001%、能耗增量 1.360 mJ、44 次 interrupt wakeups，`DroppedEvents=0`。详细方法和 Lifetime Peak 数据见 `docs/PERFORMANCE.md`。

当前完整可见窗口约 195 MiB Physical Footprint，明确未达到内存目标。菜单栏常驻和窗口卸载尚未实现，因此本轮没有“窗口关闭后常驻低于 100 MB”的有效场景，不能作此声明。2026-07-26 的旧单次样本仅是历史数据。

## 7. 已知限制与待验收

- Desktop 已注册 macOS 平台服务，但剪贴板历史持久化用例和菜单栏生命周期尚未接线，主窗口运行不等于后台常驻完成。
- 辅助功能状态检测和手动粘贴降级已完成；设置页入口、跳转系统设置、同一签名身份撤销后重新授予仍待实现。
- `CGEventPost` 本身没有下游消费回执；`Pasted` 表示权限预检通过、目标已恢复且事件已提交。TextEdit 的交互结果证明本轮允许路径实际消费成功。
- Finder 文件复制已通过；其他只提供 file-reference URL 且不提供 legacy 文件列表的应用仍需扩充矩阵。
- 未执行睡眠唤醒、多 Space、多显示器、全屏应用、8 小时稳定性或 10,000 次连续变化测试。
- 未执行可见 Terminal UI、Office、远程桌面或任何真实用户文档场景。
- 未验证 `osx-x64`、通用应用、Keychain、应用 Bundle 标识、签名、Hardened Runtime、公证、DMG/PKG 或 GitHub Release。
