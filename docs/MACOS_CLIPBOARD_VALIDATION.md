# macOS 剪贴板与桌面二期验收记录

> 日期：2026-07-27
> 环境：Mac mini，Apple M4 10 核，16 GB，macOS 26.2 (25C56)，arm64
> SDK：.NET SDK 10.0.302（由 `global.json` 锁定）
> 分支：`phase2/macos-completion`

## 1. 本轮范围

本记录保留 Phase 2.1 的真实剪贴板与外部应用结果，并补充 Phase 2 macOS 桌面生命周期、自定义全局快捷键、权限状态、Keychain、品牌、App Bundle、DMG/PKG 和发布链路验证。只把本机或自动测试实际通过的独立能力标记完成；登录启动交互、权限撤销后重新授权、Developer ID 签名、公证、Intel 和环境矩阵仍保持未完成。

原生代码全部位于 `src/SnapBoard.Platform.MacOS`。Application 和 UI 不直接引用 AppKit、Carbon、Objective-C Runtime、CoreFoundation、CoreGraphics、Accessibility、Security.framework 或 ServiceManagement；Desktop 只依赖平台抽象并在唯一组合根显式注册。生产 AppKit 操作通过 Avalonia 主线程调度器执行，跨 Objective-C 边界长期持有的状态项使用显式 retain/release，Carbon 热键、窗口原生对象、监听任务、单实例 socket 和菜单栏均有明确释放路径。

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
- `flock` 单实例所有权、命令编解码、确认超时、首实例监听前不可抢占、不完整客户端有界读取和资源释放。
- Carbon 字母/数字/功能键/导航键映射，Command/Option/Control/Shift 显示，注册冲突、失败回滚、恢复默认、持久化及已保存组合在启动冲突时回退默认。
- 主窗口、快速窗口、设置窗口按需创建/释放/重开，关闭窗口后台常驻，状态菜单命令、暂停/恢复监听和明确退出顺序。
- 辅助功能状态刷新不请求权限，只有显式用户命令可请求权限或打开系统设置；登录启动区分开发裸程序与正式 App Bundle。
- Keychain 名称/大小边界、状态映射、覆盖竞态和临时明文缓冲区清零。

## 3. macOS 原生自动测试

`MacOSClipboardNativeIntegrationTests` 使用真实 `NSPasteboard.generalPasteboard`，测试集合设置 `DisableParallelization=true`，避免并行测试相互覆盖系统剪贴板。原生自动测试覆盖：

- Text、HTML、RTF、PNG、TIFF、两个文件 URL 和 UTI 格式清单。
- 完整写回、纯文本写回以及共享图片编码往返。
- 反向域名类型 `com.wuliangtdi.snapboard.source.v1` 和每实例 nonce。
- 同一适配器自写事件抑制、不同适配器写入事件可见。
- macOS 拒绝 DIB 写入且不清空现有剪贴板，Windows 写入端继续只接受 DIB/DIBV5。
- `flock` 所有权与 Unix domain socket 首实例/第二实例确认；重复启动、监听开始前竞争和恶意不完整客户端不会产生第二主实例或永久阻塞监听器。
- Security.framework Keychain 临时密钥新增、读取、覆盖和删除。

最终 `SnapBoard.Platform.MacOS.Tests` 为 36/36 通过，`SnapBoard.Desktop.HeadlessTests` 为 21/21 通过。全量解决方案共 103 项：96 项通过、7 项 Windows 原生集成因当前平台不是 Windows 而跳过，0 项失败。

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

桌面生命周期和快捷键使用最终 `osx-arm64` App Bundle 实测：

| 场景 | 结果 | 实际证据与限制 |
| --- | --- | --- |
| 原生菜单栏状态项 | 通过 | 状态项在窗口关闭后仍可见，菜单实际包含“打开 SnapBoard / 快速粘贴 / 暂停记录 / 设置... / 退出 SnapBoard”；Template 图标在当前外观下显示，辅助功能树可访问 |
| 暂停与恢复记录 | 通过 | 点击“暂停记录”后同一菜单项变为“恢复记录”，恢复后重新显示“暂停记录”；Headless 测试同时验证暂停期间排空事件但不读取正文 |
| 关闭窗口后台常驻 | 通过 | 关闭主、快速、设置窗口后没有可见窗口，原 PID 和状态项继续存在；三类窗口均可由菜单/命令重新创建，重复关闭后再打开没有重复窗口 |
| 第二实例激活 | 通过 | 首实例后台运行时启动第二实例，命令经每用户 Unix socket 收到确认并打开首实例主窗口；没有第二个持久 SnapBoard 进程 |
| 默认全局快捷键 | 通过 | 系统真实发送 `Command+Shift+V` 后快速窗口打开 |
| 自定义全局快捷键 | 通过 | 设置页直接录入并应用 `Option+Control+A`，系统真实发送同一组合后快速窗口打开；UI 使用 macOS 修饰键名称 |
| 快捷键持久化 | 通过 | 以 `--background` 重启后自定义组合仍注册并可打开快速窗口，最后通过设置页恢复 `Command+Shift+V` 默认值 |
| 快捷键冲突/回滚 | 自动测试通过 | fake Carbon registrar 验证冲突、注册失败回滚和持久化失败回滚；没有为本轮占用另一个真实系统级快捷键制造冲突 |
| 快速窗口焦点上下文 | 通过 | 快速窗口打开前保存目标应用；既有“TextEdit -> Finder -> 恢复 TextEdit -> Command+V”实机结果证明自动粘贴路径恢复正确，关闭快速窗口不会把 SnapBoard 自身覆盖为目标 |
| 明确退出 | 通过 | 菜单“退出 SnapBoard”和第二实例 `--exit` 都终止进程；后台启动后 `--exit` 不先创建主窗口 |
| 登录启动 | 未做交互验收 | App Bundle 中 ServiceManagement 能力可用，当前状态为未启用；为避免未经确认修改系统登录项，没有切换开关或重新登录 |
| 显示器与窗口定位 | 部分通过 | 当前单台 1920 x 1080 非 Retina 显示器上窗口创建/重建和可见工作区约束通过；没有多显示器或 Retina 硬件，未执行多 Space 和全屏应用场景 |

## 5. 权限、Keychain 与应用身份

- 最终 App Bundle 固定 `CFBundleIdentifier=com.wuliangtdi.snapboard`；设置页正确识别正式 Bundle 与开发裸程序，裸程序不宣称支持登录启动，也不把其 TCC 身份当成正式发布身份。
- 当前最终 App Bundle 的 `AXIsProcessTrusted` 和事件发布预检均为已授权，设置页显示已授权、非受限模式。Phase 2.1 的独立 ad-hoc 身份拒绝路径仍是有效实测证据。
- 状态刷新不会弹出 TCC；只有设置页用户主动命令会请求权限或打开“隐私与安全性 > 辅助功能”。本轮没有点击请求/跳转，也没有撤销当前权限，因此同一稳定签名身份重新授权仍未验证。
- 权限拒绝、目标撤销或注入失败时，自动粘贴服务保持剪贴板写入成功并返回手动粘贴提示；拒绝路径已由独立身份在 TextEdit 实测，目标/注入失败映射由自动测试覆盖。
- `MacOSKeychainSecretStore` 使用 Service `com.wuliangtdi.snapboard`。本机临时二进制密钥新增、读取、覆盖和删除通过，测试结束已删除条目；没有将凭据写入 plist、JSON 或仓库。

## 6. Native AOT、品牌与发布质量门槛

执行并通过：

```bash
dotnet restore SnapBoard.slnx --locked-mode
dotnet build SnapBoard.slnx --configuration Release --no-restore
dotnet test SnapBoard.slnx --configuration Release --no-build --no-restore
dotnet format SnapBoard.slnx --verify-no-changes --no-restore
dotnet list SnapBoard.slnx package --vulnerable --include-transitive --no-restore
./scripts/macos/Package-SnapBoard.sh \
  --runtime osx-arm64 --version 0.2.0 --build-number 2
```

结果：

- Release build：0 警告、0 错误。
- 自动测试：96 项通过、7 项 Windows 原生测试按平台跳过、0 项失败。
- 格式校验：通过，无需改写。
- NuGet 审计：直接和传递依赖均未发现已知漏洞。
- AOT：0 个 AOT/裁剪警告。
- 产物：App Bundle 主程序为 24,430,144 字节 arm64 Mach-O；`lipo` 确认为非通用 arm64，`otool` 只列出系统库，包内没有 `libcoreclr` 或 `libhostfxr`。
- 品牌：标准 `.icns` 包含 16/32/128/256/512 点及 Retina 对应尺寸；App Bundle、Dock/Finder、窗口资源和 Template 状态图标均已接线，没有重新设计 Logo。
- Bundle：`com.wuliangtdi.snapboard`，版本 0.2.0、构建 2、最低 macOS 13.0；`codesign --verify --deep --strict` 通过，签名 flags 含 Hardened Runtime。
- 本地签名：仅 ad-hoc，无 Team ID；本地验证 entitlement 为 `disable-library-validation`。PKG 未签名，公证状态为 skipped，不能视为正式发布签名。
- 安装介质：DMG 为 20 MiB、PKG 为 18 MiB；`hdiutil verify` 通过，挂载 DMG 后 App Bundle 实际以后台模式启动并显示状态项，再通过 `--exit` 正常退出和卸载。
- 校验和：DMG `51626d951b1091798af8fa2143008ae41eb8f5620ff0c393d638eb265fdb581b`；PKG `496e5c5c9fc1363390c0d6a43cb05ee52dc911409e57163285632d87d0409fcc`。
- 启动：最终 AOT 产物独立启动三次，每次均由 CoreGraphics 检测到该 PID 的主窗口后采样。

本机 `security find-identity -v -p codesigning` 返回 0 个有效身份，未提供 Developer ID Application/Installer 或公证 Keychain profile。因此正式签名、公证、staple 与 Gatekeeper 接受没有执行。脚本和 GitHub Actions 已配置 arm64/x64 分离、locked restore、正式签名/公证门槛，但远程工作流尚未运行。本轮没有在 Intel 机器或 `osx-x64` Runner 上发布或启动，不能用 arm64 结果推断 x64 通过。

## 7. 性能与压力结果

桌面 AOT 三次独立进程、每次可见窗口采样 10 秒，再关闭全部窗口并采样菜单栏后台状态 3 秒：

| 轮次 | 启动 | 可见峰值 Physical | 可见峰值 RSS | 后台 Physical | 后台 RSS | Physical 回落 | Lifetime Peak | CPU | 能耗 | Wakeups | 线程/FD 峰值 |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 764.83 ms | 205.63 MiB | 170.50 MiB | 107.81 MiB | 172.25 MiB | 97.81 MiB | 209.75 MiB | 0.023% | 85.471 mJ | 406 | 15 / 45 |
| 2 | 627.65 ms | 206.16 MiB | 170.25 MiB | 107.53 MiB | 171.86 MiB | 98.62 MiB | 209.67 MiB | 0.026% | 101.381 mJ | 415 | 15 / 45 |
| 3 | 565.32 ms | 205.44 MiB | 170.36 MiB | 107.64 MiB | 172.03 MiB | 97.80 MiB | 206.19 MiB | 0.023% | 97.013 mJ | 419 | 15 / 45 |

关闭窗口后的 Physical Footprint 确实回落，但三轮后台仍为 107.53-107.81 MiB，高于 100 MB 失败线；3 秒样本也不是 10 分钟或 8 小时长稳。当前完整可见窗口约 205-206 MiB，同样未达到目标。RSS 未随窗口同步回落，因此报告同时保留 Physical Footprint 和 RSS，不能挑选较低指标宣称达标。详细方法见 `docs/PERFORMANCE.md`。

真实剪贴板压力测试连续执行 100 次预热和 10,000 次写入，结果如下：

```text
Events=10000; Warmup=100; DurationMs=2070.96;
WriteFailures=0; ReadFailures=0; MarkerFailures=0;
FeedbackEvents=0; DroppedEvents=0;
InitialRssMiB=61.36; PeakRssMiB=75.75; FinalRssMiB=76.41;
Threads=18->20; FileDescriptors=50->52
```

没有死锁、写入/抽样读回失败、来源标记失败、反馈循环或 Channel 丢弃。但 RSS 增长 15.05 MiB、线程 +2、文件描述符 +2，不满足 `< 8 MiB` 资源预算；单次两秒级结果不能证明 8 小时稳定。Phase 2.1 的空闲 AOT 监听探针历史结果仍为平均 CPU 0.001%、44 次 interrupt wakeups、`DroppedEvents=0`，但不能替代完整应用数据。

## 8. 已知限制与待验收

- 登录启动服务和 App Bundle 能力已实现，当前状态为未启用；没有切换真实登录项或重新登录验证。
- 辅助功能状态、受限模式、用户触发入口和手动粘贴降级已完成；没有撤销当前权限并用同一稳定 Developer ID 身份重新授予。
- `CGEventPost` 本身没有下游消费回执；`Pasted` 表示权限预检通过、目标已恢复且事件已提交。TextEdit 的交互结果证明本轮允许路径实际消费成功。
- Finder 文件复制已通过；其他只提供 file-reference URL 且不提供 legacy 文件列表的应用仍需扩充矩阵。
- 当前只有一台 1920 x 1080 非 Retina 显示器；未执行睡眠唤醒、多 Space、多显示器、Retina、全屏应用、10 分钟常驻或 8 小时稳定性。
- 未执行可见 Terminal UI、Office、远程桌面或任何真实用户文档场景。
- `osx-x64`、通用应用和 Intel 未验证；GitHub Actions 的 macOS 构建/发布 Job 尚未实际运行。
- 本地 ad-hoc Bundle 和未签名 PKG 不能替代 Developer ID 签名、公证、staple、Gatekeeper 和真实安装升级/卸载验证。
- 10,000 次功能正确性通过，但 RSS、线程和文件描述符有一次性增长；后台 Physical Footprint 仍超过 100 MB，性能退出条件未完成。
