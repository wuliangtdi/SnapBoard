# macOS 剪贴板与桌面二期验收记录

> 最后更新：2026-07-28
> 环境：Mac mini，Apple M4 10 核，16 GB，macOS 26.2 (25C56)，arm64
> SDK：.NET SDK 10.0.302（由 `global.json` 锁定）
> 历史基线分支：`phase2/macos-completion`
> 当前验证分支：`phase2/macos-history-search-validation`，基线提交 `3be5faa5707c72d80dfc9d7fc01b81edeb9eb66e`

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
| TextEdit 文本复制 | 通过 | 真实 TextEdit 选择并复制后读取到 Text 和 RTF，格式含 `public.utf8-plain-text`、`public.rtf`；2026-07-29 增量复测的新记录以 `ForegroundWindowAtChange` 识别为“文本编辑”并显示原生图标 |
| Finder 文件复制 | 通过 | 在 Finder 复制仓库 `README.md`，读取到真实路径 `/Users/ozonect/CSharpProject/Test02/README.md`，格式含 `public.file-url`、`NSFilenamesPboardType` 和图标 TIFF；未把 `file:///.file/id=...` 当成 POSIX 路径 |
| Google Chrome HTML 复制 | 通过 | 在真实 Chrome 的 `example.com` 页面选择并复制，读取到 Text 和 HTML，格式含 `public.html`、`public.utf8-plain-text` 及 Chromium 自定义类型 |
| Safari HTML/RTF 复制 | 通过 | 在真实 Safari 的 `example.com` 页面选择并复制，读取到 Text、HTML、RTF 和 WebArchive 格式 |
| Preview 图片复制 | 通过 | 在 Preview 打开仓库 `snapboard-logo.png` 并复制，读取到 `public.png`、`public.tiff`；共享模型选择 `PortableNetworkGraphics`，没有伪装为 DIB |
| 命令行文本互操作 | 通过 | 当前 macOS shell 使用 `pbcopy` 写入生成文本，监听读取到 Text 和 `public.utf8-plain-text` 等文本格式，`DroppedEvents=0` |
| 可见 Terminal UI 选择复制 | 未完成 | 桌面控制工具因终端安全限制拒绝操作 Terminal；本轮只记录 `pbcopy` CLI 路径，不能声称 Terminal UI 复制通过 |
| 自动粘贴允许状态 | 通过 | `AXIsProcessTrusted` 与 `CGPreflightPostEventAccess` 均为允许；TextEdit 实际收到生成文本，返回 `PasteStatus=Pasted; Reason=None` |
| 目标应用恢复 | 通过 | 捕获 `com.apple.TextEdit` 后切换到 Finder，等待期间保持 Finder 前台；服务随后恢复 TextEdit 并发送 Command+V，TextEdit 实际出现 `SnapBoard restored TextEdit after Finder verified` |
| 辅助功能拒绝状态 | 通过 | 使用独立 ad-hoc 应用身份运行同一 AOT 探针，检测为 `AccessibilityPermissionGranted=False`；剪贴板写入成功，返回 `ManualPasteRequired; Reason=AccessibilityPermissionDenied` 和“已复制，请手动粘贴”，TextEdit 保持空白 |
| 来源应用识别 | 受限但符合设计 | NSPasteboard 不可靠暴露 owner 应用；现在只把检测到 `changeCount` 变化时的前台 PID 标记为 `ForegroundWindowAtChange`，读取前校验序列，名称/路径/PID 失效或切换竞争时返回 `Unknown`，不把结果表述为 clipboard owner |

桌面生命周期和快捷键使用最终 `osx-arm64` App Bundle 实测：

| 场景 | 结果 | 实际证据与限制 |
| --- | --- | --- |
| 原生菜单栏状态项 | 通过 | 状态项在窗口关闭后仍可见，菜单实际包含“打开闪剪 / 快速粘贴 / 暂停记录 / 设置... / 退出闪剪”；Template 图标在当前外观下显示，辅助功能树可访问 |
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

没有死锁、写入/抽样读回失败、来源标记失败、反馈循环或 Channel 丢弃。当时按 framework-dependent 进程的冷启动 RSS 判为超过 `< 8 MiB`；9.7 已确认该判定混入 JIT、分层编译和探针自身采样路径的首次初始化，不能作为 NSPasteboard 泄漏结论。Phase 2.1 的空闲 AOT 监听探针历史结果仍为平均 CPU 0.001%、44 次 interrupt wakeups、`DroppedEvents=0`，但不能替代完整应用数据。

## 8. 已知限制与待验收

- 登录启动服务和 App Bundle 能力已实现，当前状态为未启用；没有切换真实登录项或重新登录验证。
- 辅助功能状态、受限模式、用户触发入口和手动粘贴降级已完成；没有撤销当前权限并用同一稳定 Developer ID 身份重新授予。
- `CGEventPost` 本身没有下游消费回执；`Pasted` 表示权限预检通过、目标已恢复且事件已提交。TextEdit 的交互结果证明本轮允许路径实际消费成功。
- Finder 文件复制已通过；其他只提供 file-reference URL 且不提供 legacy 文件列表的应用仍需扩充矩阵。
- 当前只有一台 1920 x 1080 非 Retina 显示器；未执行睡眠唤醒、多 Space、多显示器、Retina、全屏应用、10 分钟常驻或 8 小时稳定性。
- 未执行可见 Terminal UI、Office、远程桌面或任何真实用户文档场景。
- `osx-x64` 已在后续 2026-07-29 验证中完成交叉 AOT 与 Rosetta 启动预检，但 Intel 匹配硬件/Runner 和通用应用仍未验证；GitHub Actions 的 macOS 构建/发布 Job 尚未实际运行。
- 本地 ad-hoc Bundle 和未签名 PKG 不能替代 Developer ID 签名、公证、staple、Gatekeeper 和真实安装升级/卸载验证。
- 10,000 次功能正确性和 Native AOT 平台探针 `< 8 MiB` 预算通过；首次开窗后的完整桌面后台 Physical Footprint 仍未稳定达到 `<= 80 MB` 目标，8 小时未执行，因此整体性能退出条件未完成。

## 9. 2026-07-28 合并后共享历史与检索复验

本节只记录 Windows 三个历史提交进入 `main` 后，在 macOS 目标机对共享能力的新增复验。未把 Windows 的 AUMID、Package Family、AppsFolder 或前台应用猜测移植到 macOS。验证前已执行 `fetch --prune`、`pull --ff-only`，并确认基线提交是当前 HEAD 的祖先。

### 9.1 APFS、迁移、恢复与 Blob

本机数据卷 `/dev/disk5s2` 为 999.8 GB PCIe SSD、APFS；测试临时目录和实际 `Application Support` 数据目录均位于该 APFS 数据卷。新增或扩展的集成测试实际覆盖：

- Schema v1-v5 分别新建、同版本重复迁移、再升级到当前版本并重复执行；v4 实际行升级到 v5 后，AUMID 与 Package Family 保持 NULL，来源归属为 Unknown。
- 每个连接的 WAL、外键和 busy timeout，损坏数据库及可用 WAL/SHM 的时间戳备份、重建、诊断和恢复后 CRUD。
- 重启后正文、标签、置顶、使用次数/最后使用时间、软删除/删除时间和设置一致；实际桌面重启另见 9.6。
- Blob 临时文件、原子移动、事务失败回滚、共享引用计数、删除、清空、过期和孤儿清理。启动返回时不等待目录扫描，过期临时文件与旧孤儿在后台处理，删除前仍按数据库完整相对路径复查。
- PNG 与 TIFF 原图字节保持不变，缩略图按需读取为 PNG，Blob 暂存目录无残留。Skia 不支持 TIFF 解码，因此 Infrastructure 新增 `BitMiracle.LibTiff.NET 2.4.660`，只在后台以 40,000,000 像素上限解码缩略图；损坏 TIFF 保留原图但不生成缩略图，不让异常中断持久化。

macOS 的来源边界由 Application、Infrastructure 和 Desktop 组合根共同回归：PID、进程名、可执行路径、AUMID、Package Family 全部为 NULL，访问状态与归属类型均为 Unknown；macOS 不注册 Windows 来源图标解析器，主窗口稳定显示“未知来源”和通用应用图标。

### 9.2 检索、高频刷新与 UI

在本机重新生成 100,000 条、平均 554.7 字符的中文、英文、C#、JSON、URL 和路径混合数据。导入耗时 15,289.62 ms，数据库 515,645,440 字节；每页只投影 50 条摘要，不读取正文、原图或缩略图。

| 查询组 | P50 | P95 | 最大值 |
| --- | ---: | ---: | ---: |
| 中文选择性 | 0.39 ms | 0.70 ms | 1.02 ms |
| 英文选择性 | 0.89 ms | 1.22 ms | 1.72 ms |
| 代码选择性 | 0.42 ms | 0.68 ms | 0.79 ms |
| 中文宽查询 | 0.26 ms | 0.46 ms | 0.58 ms |
| 英文宽查询 | 0.67 ms | 0.98 ms | 1.03 ms |
| 代码宽查询 | 0.57 ms | 1.10 ms | 2.23 ms |
| 150 次目标查询总体 | 0.46 ms | 1.04 ms | 1.72 ms |
| 300 次全部查询总体 | 0.53 ms | 1.01 ms | 2.23 ms |

P95 低于 80 ms 目标，所有样本低于 200 ms 失败线。自动测试同时覆盖特殊字符、筛选、稳定游标分页、取消、旧查询代际隔离、每页增量加载、虚拟化和图片按需解码；连续发布 10,000 次 `HistoryChanged` 后，除初始查询外只在 150 ms 静默期触发一次刷新。

真实 AOT 主窗口从空库开始观察到以下持久结果：

| 输入 | 数据库/主窗口结果 | 来源 |
| --- | --- | --- |
| TextEdit 生成中英文文本 | 新增文本/RTF 历史，重开主窗口可见 | Unknown |
| Finder 复制仓库 `README.md` | 新增历史；`clipboard_files` 保存完整 POSIX 路径，同时保存 Finder 提供的 TIFF 图标 | Unknown |
| Safari `example.com` | 新增 Text/HTML/RTF 历史 | Unknown |
| Chrome IANA Example Domains | 新增 Text/HTML 历史 | Unknown |
| Preview PNG | 新增 1254 x 1254 PNG Blob 与缩略图 | Unknown |
| Preview 同像素 TIFF | 命中同一图片记录，`capture_count` 从 1 增至 2，未制造重复历史 | Unknown |
| `pbcopy` 特殊字符文本 | 新增文本历史并可检索 | Unknown |

主窗口真实历史、图片缩略图和增量更新已可见。锁屏发生前未完成本轮快速窗口的可见真实历史复核；快速窗口真实 XAML、选择、共享 ViewModel 分页/取消/按需图片路径由 Headless 测试通过，但本节不把它替代为交互通过。

### 9.3 Native AOT、资源与 10,000 次压力

`osx-arm64` self-contained Native AOT 发布和 App Bundle 启动通过，发布输出 0 个 AOT/裁剪警告。最终 App 主程序为 26,606,368 字节 arm64 Mach-O，包内无 `coreclr`、`hostfxr` 或 `clrjit`。三次独立进程样本如下：

| 轮次 | 启动 | 可见 Physical | 可见 RSS | 后台 Physical | 后台 RSS | 回落 | Lifetime Peak | 线程 | FD | CPU | 能耗 | Wakeups |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1262.00 ms | 200.05 MiB | 164.14 MiB | 100.05 MiB | 164.61 MiB | 100.00 MiB | 200.09 MiB | 16 | 45 | 0.022% | 54.746 mJ | 419 |
| 2 | 420.21 ms | 200.02 MiB | 165.53 MiB | 100.19 MiB | 164.78 MiB | 99.83 MiB | 203.61 MiB | 16 | 45 | 0.021% | 54.257 mJ | 409 |
| 3 | 458.88 ms | 199.66 MiB | 166.23 MiB | 100.19 MiB | 164.56 MiB | 99.47 MiB | 200.02 MiB | 16 | 45 | 0.022% | 57.772 mJ | 409 |

窗口关闭后的 Physical Footprint 明显回落，但三轮均略高于 100 MiB 失败线，内存门槛仍未通过。完整桌面保持运行时又执行真实 NSPasteboard 探针：

```text
Events=10000; Warmup=100; DurationMs=2044.52;
WriteFailures=0; ReadFailures=0; MarkerFailures=0;
FeedbackEvents=0; DroppedEvents=0;
InitialRssMiB=61.17; PeakRssMiB=76.20; FinalRssMiB=76.86;
Threads=17->21; FileDescriptors=49->51
```

探针功能正确性通过；这里的 framework-dependent 冷启动 RSS 增长不能单独判定平台泄漏，修正后的 Physical Footprint 与 Native AOT 对照见 9.7。桌面进程在压力前后保持同一 PID，RSS 约 162.1 -> 163.7 MiB、线程 16 -> 17、FD 51 -> 51，数据库条数 6 -> 8；这里只证明完整桌面存活、收敛保存预热末项与事件末项，不把 10,000 个快速变化误述为 10,000 条历史。

关闭全部窗口后已开始独立 10 分钟菜单栏常驻样本；最终时长与资源见 9.6。8 小时测试未执行。

### 9.4 构建、测试与包

交接要求的六条命令全部通过：locked restore；Release build 0 警告/0 错误；全量 159 项中 144 项通过、15 项 Windows 原生测试按平台跳过、0 项失败；format 无改动；直接/传递 NuGet 漏洞为 0；100,000 条检索结果为 PASS。

本地生成 0.2.0 (build 3) App/DMG/PKG。DMG 为 21,989,704 字节，`hdiutil verify` 通过，SHA-256 为 `103ddd65b902d3d8b5294651340015cd9c5b883a2228166475b6cf0637273e09`；PKG 为 20,200,493 字节，SHA-256 为 `d0114f27948e1667965d2695a9052c627a5623d175df1c3373310c2f5c4af085`。

`codesign --verify --deep --strict` 只证明本地 ad-hoc Bundle 结构有效；签名 flags 为 `adhoc,runtime`、无 Team ID，PKG 明确为 `no signature`，`spctl` 拒绝。钥匙串返回 0 个有效代码签名身份，因此 Developer ID Application/Installer、正式 entitlement、签名 PKG、公证、staple、Gatekeeper 接受、安装升级/卸载和 GitHub macOS Runner 均未完成，不能把本地产物当成正式发布结果。

### 9.5 明确未完成或受限的项目

- `osx-x64` 未在 Intel 或对应 Runner 发布和启动，不能从 M4 推断。
- 当前只有单台 1920 x 1080 非 Retina 显示器；睡眠唤醒、多 Space、多显示器、Retina 和全屏应用未执行。
- 登录启动真实启用/重新登录未执行；没有稳定 Developer ID 身份，辅助功能权限撤销后以同一稳定签名重新授权也未执行。
- 可见 Terminal UI 被桌面安全策略拒绝操作；Office 未安装；发现 UU 远程客户端但未启动，因此 Office 与远程桌面均不标记通过。
- 10 分钟菜单栏样本只覆盖短期常驻，8 小时长稳明确未执行。

### 9.6 菜单栏常驻与真实重启

01:57:32 通过应用自己的第二实例 `--close-windows` 命令关闭全部窗口，同一 AOT PID 继续运行；02:09:55 结束样本，关闭窗口阶段持续 12 分 23 秒。开始时 RSS 170,240 KiB（166.25 MiB）、15 线程、51 FD；结束时 RSS 99,264 KiB（96.94 MiB）、14 线程、47 FD，CPU 两端均为 0.0%。首个关闭后 `footprint` 样本为 138 MB，结束为 139 MB，Lifetime Peak 256 MB。RSS、线程和 FD 有回落，但 Physical Footprint 未低于 100 MB，因此 10 分钟时长完成、内存目标失败。

随后使用 `--exit` 干净结束 PID 25766，并以同一 App Bundle `--background` 启动 PID 28621。重启前后实际数据库聚合完全一致：11 条历史、1 个文件路径、`capture_count` 总和 13、11 条 Unknown 来源，Finder `README.md` 完整路径仍为 1 条；`journal_mode=wal`、`quick_check=ok`。这证明当时版本的真实外部应用内容和 Unknown 来源投影跨进程重启保持一致；2026-07-29 新增的最佳努力来源只作用于后续采集，不反向猜测或改写这批历史。标签、置顶、使用次数、软删除和设置的非零状态由 9.1 的独立 APFS 集成测试验证；实际桌面样本这些字段均为零，未伪造非零交互结果。

重启后系统仍处于锁屏，无法补做快速窗口可见历史和全屏场景；它们继续保持自动测试通过/交互未完成的分级结论。

### 9.7 资源未达标原因复查

原压力探针只读取 `Process.WorkingSet64`，并在首次调用线程和 `/dev/fd` 采样 API 之前记录基线。这样会把 framework-dependent 进程的 JIT、分层编译、程序集按需加载和探针自身诊断路径算成 NSPasteboard 增长；同时也违背本项目在 macOS 以 Physical Footprint 为正式内存指标的规则。探针现已先预热诊断路径，再通过 `proc_pid_rusage(RUSAGE_INFO_V6)` 同时记录 Physical Footprint 和 RSS。

| 探针 | 预热/计量事件 | Physical 初始/最终 | 增长 | RSS 初始/最终 | FD | 结论 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| framework-dependent | 100 / 10,000 | 29.61 / 44.55 MiB | 14.94 MiB | 61.94 / 78.17 MiB | 50 -> 52 | 冷运行时混入，不能归因于剪贴板 |
| framework-dependent | 10,000 / 10,000 | 42.72 / 48.19 MiB | 5.47 MiB | 75.62 / 81.92 MiB | 50 -> 52 | 预热后内存预算通过 |
| Native AOT arm64 | 100 / 10,000 | 9.22 / 14.31 MiB | 5.09 MiB | 30.67 / 36.50 MiB | 7 -> 7 | `< 8 MiB` 预算通过 |
| Native AOT arm64 | 100,000 / 100,000 | 17.33 / 17.78 MiB | 0.45 MiB | 39.14 / 40.05 MiB | 7 -> 7 | 高事件量无持续增长 |

四轮均为写入、抽样读回、来源标记、反馈事件和队列丢弃 0 失败。线程增加来自异步监听和线程池首次扩容；AOT 两轮 FD 均保持 7，100,000 次计量阶段只增长 0.45 MiB。因此，原先的约 15 MiB 结果是验证方法的假阳性，NSPasteboard 事件路径没有观察到按事件线性泄漏。

完整 AOT 桌面另做同 PID A/B：纯后台启动 Physical 为 41.4 MiB；首次显示主窗口为 165.0 MiB；关闭 3 秒后回落到约 94-96 MiB，主窗口 IOSurface 从约 14.1 MiB 降至约 0.1 MiB。10,000 次真实事件前后 Physical 为 99,009,688 -> 99,042,456 字节，仅增加 32 KiB。连续 100 轮快速窗口打开/关闭期间关窗样本在约 95.9-107.8 MiB 间波动但不随轮次单调增长，Lifetime Peak 保持 213.1 MiB、FD 始终 45；约 28 分钟混合压力后的低侵入样本为 95.1 MiB。受签名调试限制，`leaks` 只能读取受限内存范围，在该范围报告 0 leak。

`vmmap` 显示首次 UI 使用后主要留下 Avalonia/AppKit、字体、托管堆和图形驱动的已提交/压缩缓存；窗口表面本身能够释放。这个结果解释了“纯后台很低、开窗后关窗仍接近 100 MiB”的差异，但不能据此把完整桌面判为通过：`<= 80 MB` 目标仍未达到，历史 3 秒和 12 分钟样本也确实曾超过 100 MB。当前结论是“平台事件资源预算通过，完整桌面 UI 后台基线仍未达目标且存在系统波动”；8 小时长稳仍明确未执行。

## 10. 2026-07-29 双架构文件系统 ABI 补验

在 macOS 26.2 (25C56)、Apple M4、16 GiB、.NET SDK 10.0.302 上，从提交 `0bbd9d4` 的干净 checkout 执行 locked restore，并分别发布 `osx-arm64` 与 `osx-x64` Native AOT。x64 首次预检暴露 Darwin 无后缀 `lstat`/`statfs` 在 Intel ABI 下仍使用旧结构布局，导致真实目录被误判；改为两架构共享的 `lstat64`/`statfs64` 后，macOS 平台原生测试 49/49 通过。

`osx-x64` 主程序 35,727,960 字节，SHA-256 `8ebb4ad0080dbcd42549de1b7d89f66aaf579883a8a52d722150b63239acd41b`；迁移器 8,554,488 字节，SHA-256 `3660b83538bda663a20a771f48c59eefdae7a78b41bbf1c41216cdd7d394b780`。两者均为 x86_64 Mach-O，无 CoreCLR 或 helper 托管配置，helper 无参数退出码为 4；Rosetta 下的隔离 bootstrap 冷启动、根/bootstrap/data `0700` 权限、第二实例 `--exit` 和主实例退出均返回 0。该结果是 x64 预检，不能替代 Intel 匹配硬件/Runner。

最终 `osx-arm64` 开发包主程序 34,573,888 字节，SHA-256 `cbe826b2a850625c8d03829aa283f0d19a345150ea5a80a2566fd366e8c70186`；迁移器 8,326,528 字节，SHA-256 `cf330df4d0e2ed72ca5499707f160a29a86d78d4f292e64c698213f158a84c94`。DMG 30,290,823 字节，SHA-256 `01a98c67867594ef7b7b0ce2618851f605ffcd9f60698c328c964ff98e7d2c19`；PKG 27,020,393 字节，SHA-256 `30e6b1cf37cc7a0842e0f74575288aedb9abc2355ffb7ac4214490c181b35f67`。DMG CRC、挂载后启动、`codesign --deep --strict`、PKG 17 项 payload、Bundle ID 与 `/Applications` 安装位置通过；Bundle 仍为 Hardened Runtime ad-hoc，PKG 无签名，公证跳过。

Release build 为 0 警告/0 错误，format 检查 342 个文件且无改动；启用两个 Apache 2.4.62 loopback WebDAV 端点后，全量 307 项中 287 项通过、20 项 Windows 原生测试按平台跳过、0 项失败。CI Build/Test 矩阵已加入 `macos-15-intel`，但远程工作流尚未运行；Developer ID、公证、Gatekeeper、Intel 实机、8 小时和多显示器等限制不变。

## 11. 2026-07-29 来源应用与系统显示名称补验

- 监听器仅在未被自写反馈抑制的 `changeCount` 变化上查询一次前台 PID；正文读取时必须再次匹配同一序列，避免把陈旧 PID 绑定到更新后的剪贴板。
- `NSRunningApplication` 提供最佳努力的本地化名称与可执行路径；`.app` 路径交给 `NSWorkspace` 读取原生图标，并转成固定 32 x 32 BGRA。图标结果使用 256 项有界缓存，空结果不缓存以保留瞬态失败重试。
- 真实 TextEdit 复制的新记录在主窗口中显示“文本编辑”和 TextEdit 原生图标；来源依据为 `ForegroundWindowAtChange`。直接后台写入、复制后在一个轮询周期内切换应用、应用退出或路径不可用时仍可能 Unknown 或只保留名称，这是已记录的协议限制。
- 裸 `dotnet run` 不是 macOS 应用包，Dock 可能继续使用内部可执行文件名 `SnapBoard.Desktop`；支持的发布启动方式是 `.app`。最终包的 `CFBundleDisplayName`/`CFBundleName`、运行时 `NSRunningApplication.localizedName`、窗口标题和应用菜单首项均实测为“闪剪”，Bundle ID 为 `com.wuliangtdi.snapboard`。
- 旧历史记录不会通过当前前台应用反推来源；只有修复后新采集且序列匹配的记录会写入最佳努力身份。
