# SnapBoard 性能与内存门槛

> 本文定义可重复的测量方法。任何“感觉不卡”或任务管理器单次截图都不能替代基准。

## 1. 发布预算

| 场景 | 目标 | 失败线 |
| --- | --- | --- |
| 托盘常驻、窗口关闭 10 分钟 | <= 80 MB | > 100 MB |
| 快速窗口、100 个文本项可见 | <= 100 MB | > 120 MB |
| 图片预览关闭 30 秒后 | 回落 <= 100 MB | 持续增长 |
| 空闲 CPU | < 0.3% | 持续 > 1% |
| 快速窗口暖启动 P95 | < 120 ms | > 250 ms |
| 采集到可搜索 P95 | < 100 ms | > 250 ms |
| 100,000 条文本搜索 P95 | < 80 ms | > 200 ms |
| 8 小时 10,000 次变化 | 增长 < 8 MB | 持续泄漏 |

## 2. 测试数据

- 100,000 条文本，平均 500 字符，混合中文、英文、代码、URL、路径和 JSON。
- 2,000 条 HTML/RTF。
- 1,000 张 0.5 MB 到 10 MB 图片；列表只读缩略图。
- 10,000 次连续剪贴板事件，包含相邻重复和突发复制。
- WebDAV 模拟 50 ms、200 ms 延迟，离线、超时、412、429、500 和损坏响应。

## 3. 平台指标

- Windows：Private Working Set、Private Bytes、Handle Count、GDI/User Objects。
- macOS：Physical Footprint、Resident Size、Energy Impact。
- Linux：PSS、RSS、文件描述符和 `/proc/<pid>/smaps_rollup`。

每次报告必须记录 Commit SHA、RID、AOT/JIT、操作系统、CPU、内存、采样工具、运行时长、数据规模和至少三次样本。

## 4. 基准分层

### 4.1 微基准

`SnapBoard.PerformanceTests` 使用 BenchmarkDotNet。只放纯算法、映射、哈希、分页和搜索解析等稳定输入，不用微基准替代真实 UI/数据库测量。

```bash
dotnet run --project tests/SnapBoard.PerformanceTests/SnapBoard.PerformanceTests.csproj \
  --configuration Release
```

### 4.2 数据库基准

- Microsoft.Data.Sqlite 连接创建和池化。
- 单条/批量写事务。
- 100,000 条分页和 FTS5 搜索。
- WAL checkpoint、迁移和崩溃恢复。
- 显式列映射的分配量。

### 4.3 UI 基准

- 纯 Avalonia、Avalonia + Ursa、最终窗口壳三组对比。
- 快速窗口首次打开和重复打开。
- 100 项虚拟化列表滚动。
- 大图片预览打开、关闭和内存回落。

## 5. 实现规则

- 列表分页和虚拟化，不把全部历史装入 ObservableCollection。
- UI、数据库、压缩、哈希、图片和网络不在同一线程串行执行。
- 大载荷流式处理，缓存同时限制条目数和总字节数。
- 原生句柄、Bitmap、Stream、SQLite 连接和 CancellationTokenSource 明确释放。
- 后台队列有界；超出容量时记录计数并采用明确合并策略。
- macOS 轮询 tick 只读取 `NSPasteboard.changeCount` 并写入轻量事件；正文读取、SQLite 和网络请求不得进入 tick。
- 空闲状态不使用高频轮询；WebDAV 默认 10 到 30 秒拉取。

## 6. 当前基线

- Release 构建：0 警告、0 错误。
- `osx-arm64` Native AOT：已成功发布。
- `win-x64` Native AOT：已在 Windows 11 x64 本机成功发布，0 个 AOT/裁剪警告。
- 依赖漏洞：当前直接与传递包未发现已知漏洞。

### 6.1 2026-07-26 命令中心可见窗口样本

测试环境：macOS 26.2、Apple Silicon、Release、窗口显示 10 条脱敏记录和代码预览。采样工具为 `ps` 与 `vmmap -summary`，单次样本只用于发现问题，不作为正式发布结论。

| 构建 | Physical Footprint | 峰值 | RSS | 产物 |
| --- | ---: | ---: | ---: | --- |
| `osx-arm64` Native AOT | 152.5 MB | 195.2 MB | 约 163.4 MB | 可执行文件约 23 MB，发布目录约 91 MB |
| Framework-dependent Release | 198.2 MB | 239.8 MB | 约 214.4 MB | 依赖已安装 .NET 运行时 |

结论：Native AOT 相比 Framework-dependent Release 明显降低本次样本内存，但可见窗口仍超过 100 MB 目标和 120 MB 失败线。此前 56 MB 的空壳数据不再代表当前完整命令中心，已被本节取代。

正式优化和复测必须至少包含：

- 纯 Avalonia 基线、Material Icons、Ursa 和最终 UI 壳四组 A/B。
- 窗口关闭后托盘常驻 10 分钟、窗口打开 100 条文本记录、图片预览关闭回落三种场景。
- Windows 11 的 Private Working Set/Private Bytes 和 macOS Physical Footprint 各三次样本。
- 检查字体、位图、图标字典、Skia 表面、窗口缓存和未释放 View/Binding 对象的增量。
- Ursa 增量：尚未测试，当前正式依赖图未引用 Ursa。

### 6.2 2026-07-26 Windows 11 x64 可见窗口样本

版本：`phase1/windows-clipboard` 本次提交工作树。构建为 Release、`win-x64`、self-contained Native AOT；产物 `SnapBoard.Desktop.exe` 为 26,828,288 字节，发布目录中不存在 `coreclr.dll` 或 `clrjit.dll`。主机为 Windows 11 Pro 10.0.28000、AMD Ryzen 9 7945HX、100,617,691,136 字节物理内存。

采样脚本为 `scripts/windows/Measure-SnapBoardProcess.ps1`。每次启动独立进程，以 `MainWindowHandle != 0` 作为“进程冷启动到主窗口”终点，随后通过 `Win32_PerfFormattedData_PerfProc_Process` 每秒采集 30 秒。该方法不是重启/干净 VM 后的 OS-cold，也不是托盘常驻测量。

```powershell
scripts/windows/Measure-SnapBoardProcess.ps1 `
  -ExecutablePath artifacts/publish/win-x64-aot-final/SnapBoard.Desktop.exe `
  -Runs 3 -SampleSeconds 30
```

| 轮次 | 启动到主窗口 | 峰值 Private Working Set | 峰值 Private Bytes | 峰值句柄 |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 489.82 ms | 184.38 MiB | 214.54 MiB | 1276 |
| 2 | 279.49 ms | 207.12 MiB | 239.21 MiB | 1275 |
| 3 | 280.25 ms | 218.20 MiB | 250.13 MiB | 1272 |

结论：本轮只验证可见主窗口，三次 Private Working Set 峰值均超过 120 MiB 失败线，不能宣称达到内存目标。当前尚未实现托盘和窗口卸载，因此没有“托盘常驻低于 100 MB”的有效样本；后续必须完成 UI 依赖 A/B、窗口关闭回落和 10 分钟托盘场景。

### 6.3 2026-07-27 macOS arm64 原生适配器可见窗口样本

版本：`phase2/macos-clipboard` 最终提交工作树。主机为 Mac mini（Apple M4，10 核，16 GB），`uname -m=arm64`，macOS 26.2 (25C56)，.NET SDK 10.0.302。构建为 Release、`osx-arm64`、self-contained Native AOT；产物是 23,906,928 字节的 arm64 Mach-O，剥离后发布目录约 91.68 MiB，发布输出为 0 个 AOT/裁剪警告。

采样脚本为 `scripts/macos/Measure-SnapBoardProcess.sh`。每轮启动独立 AOT 进程，通过 `CGWindowListCopyWindowInfo` 检测该 PID 的可见主窗口，以此记录启动终点；随后每秒调用公开的 `proc_pid_rusage(RUSAGE_INFO_V6)` 采样 10 秒。Physical Footprint、Resident Size、进程 CPU 时间、`ri_energy_nj` 和 interrupt wakeups 均来自同一进程计数器。该方法不是重启后的 OS-cold，也不等同于活动监视器的相对 Energy Impact 指数。

```bash
scripts/macos/Measure-SnapBoardProcess.sh \
  artifacts/publish/osx-arm64-aot-final-20260727/SnapBoard.Desktop 3 10
```

| 轮次 | 启动到可见窗口 | 峰值 Physical Footprint | 峰值 RSS | Lifetime Peak | 平均 CPU | 能耗增量 | Interrupt wakeups |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 3974.77 ms | 194.78 MiB | 162.11 MiB | 198.64 MiB | 0.014% | 26.541 mJ | 327 |
| 2 | 1288.41 ms | 194.74 MiB | 162.55 MiB | 198.75 MiB | 0.015% | 25.098 mJ | 315 |
| 3 | 919.26 ms | 194.94 MiB | 162.08 MiB | 198.72 MiB | 0.017% | 33.354 mJ | 330 |

轮询开销另用同一份最终代码发布的 AOT 探针测量，避免把尚未接入桌面生命周期的监听器与普通窗口空闲混为一谈。探针实际执行 `WatchAsync` 12 秒，在中间 10 秒计量窗口内无剪贴板变化：平均 CPU 0.001%、能耗增量 1.360 mJ、44 次 interrupt wakeups、最终 Physical Footprint 6.41 MiB、RSS 27.17 MiB，`DroppedEvents=0`。该数据验证轮询退避开销，不代表完整桌面应用内存。

当时结论：轮询 CPU 满足空闲预算，但完整可见窗口三次 Physical Footprint 均约 195 MiB，明确超过 120 MB 失败线。第一轮启动 3.97 秒也必须继续调查；该阶段尚未实现菜单栏常驻和窗口卸载，因此没有有效后台样本。桌面生命周期完成后的数据已由 6.6 节取代，2026-07-26 的单次旧样本同样只保留为历史记录。

### 6.4 2026-07-27 Windows 11 x64 生命周期收口样本

版本：`phase1/windows-completion` 最终代码工作树。主机为 Windows 11 Pro 10.0.28000、AMD Ryzen 9 7945HX、100,617,691,136 字节物理内存，.NET SDK 10.0.302。构建为 Release、`win-x64`、self-contained Native AOT；`SnapBoard.Desktop.exe` 为 27,700,736 字节，发布目录不存在 `coreclr.dll` 或 `clrjit.dll`，发布输出为 0 个 AOT/裁剪警告。

`scripts/windows/Measure-SnapBoardProcess.ps1` 每轮启动独立 AOT 进程，以主窗口句柄出现记录启动终点；可见窗口与窗口关闭阶段各采集 30 个至少相隔 1 秒的样本，CIM 查询耗时计入实际墙钟时间，最后通过第二实例 `--exit` 请求干净退出。CPU 已按逻辑处理器数归一化。该数据是三次应用冷启动，不是重启系统或干净 VM 后的 OS-cold，也不是 10 分钟或 8 小时长稳结果。

```powershell
scripts/windows/Measure-SnapBoardProcess.ps1 `
  -ExecutablePath C:\path\to\SnapBoard.Desktop.exe `
  -Runs 3 -SampleSeconds 30 -ClosedSampleSeconds 30
```

| 轮次 | 启动到主窗口 | 窗口卸载 | 可见峰值 PWS | 可见峰值 Private Bytes | 可见平均 CPU | 可见峰值句柄 | 关闭后最终 PWS | 关闭后最终 Private Bytes | 关闭后平均 CPU | 最终句柄 | PWS 回落 |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 606.58 ms | 35.90 ms | 84.47 MiB | 181.98 MiB | 0.016% | 1267 | 66.22 MiB | 130.67 MiB | 0.000% | 1264 | 18.25 MiB |
| 2 | 459.96 ms | 48.84 ms | 85.12 MiB | 182.56 MiB | 0.012% | 1271 | 66.86 MiB | 140.51 MiB | 0.000% | 1268 | 18.26 MiB |
| 3 | 445.34 ms | 35.12 ms | 109.50 MiB | 200.71 MiB | 0.094% | 1287 | 130.38 MiB | 180.64 MiB | 0.000% | 1289 | -20.88 MiB |

窗口关闭后第 1、2 轮 PWS 分别回落 18.25/18.26 MiB，最终为 66.22/66.86 MiB；第 3 轮先回落，约 30 秒后 PWS、Private Bytes 和句柄同时上升，最终 PWS 比可见峰值高 20.88 MiB。三轮最终 Private Bytes 均为 130.67-180.64 MiB。这里的“30 秒”参数实际生成 30 个至少相隔 1 秒的样本，CIM 查询开销会延长墙钟时间；它仍不是 10 分钟或 8 小时长稳。资源释放存在真实波动，因此不能声称“托盘常驻低于 100 MB”，后续仍需定位第三轮增长、做 10 分钟托盘、8 小时长稳和 UI 依赖 A/B。

同一最终代码的三轮 10,000 次剪贴板压力测试均完成 10,000/10,000 个自写事件，反馈事件与 Channel 丢弃均为 0，平均 CPU 为 0.07%/0.07%/0.05%，句柄变化为 +14/+2/-11；Private Bytes 增长分别为 15.56/8.43/15.12 MiB，三轮都未满足严格的 `< 8 MiB` 预算，资源增长门槛保持未完成。

### 6.5 2026-07-27 自定义快捷键与设置页 AOT 冒烟

在 `phase1/windows-completion` 的自定义快捷键与设置页样式变更上，使用 .NET SDK 10.0.302 重新发布 `win-x64` self-contained Native AOT。`SnapBoard.Desktop.exe` 为 27,746,304 字节，发布输出为 0 个 AOT/裁剪警告；原生 EXE 已实际打开标题为 `SnapBoard 设置` 的窗口，并通过第二实例 `--exit` 正常结束。

本次只验证新增 UI 与快捷键映射没有破坏 Native AOT，不重新采集启动、内存、CPU 和句柄，因此 6.4 的三轮性能样本仍是当前正式基线，不能用本节冒烟替代，也不能据此声称托盘常驻低于 100 MB。

### 6.6 2026-07-27 macOS arm64 桌面生命周期最终样本

版本：`phase2/macos-completion` 最终代码工作树。主机为 Mac mini（Apple M4，10 核，16 GB）、macOS 26.2 (25C56)、arm64、.NET SDK 10.0.302。构建为 Release、`osx-arm64`、self-contained Native AOT；App Bundle 主程序为 24,430,144 字节 arm64 Mach-O，发布输出为 0 个 AOT/裁剪警告。

更新后的 `scripts/macos/Measure-SnapBoardProcess.sh` 每轮启动独立 AOT 进程，以 CoreGraphics 检测主窗口作为启动终点，采样可见窗口 10 秒；随后通过第二实例 `--close-windows` 关闭主/快速/设置窗口，确认没有该 PID 的可见窗口后继续采样菜单栏后台状态 3 秒，最后发送 `--exit`。Physical Footprint、RSS、Lifetime Peak、线程、文件描述符、进程 CPU、`ri_energy_nj` 和 interrupt wakeups 均来自同一 PID。该短样本验证窗口释放路径，不等于 10 分钟常驻、8 小时长稳或重启系统后的 OS-cold。

```bash
scripts/macos/Measure-SnapBoardProcess.sh \
  artifacts/macos/SnapBoard.app/Contents/MacOS/SnapBoard 3 10 3
```

| 轮次 | 启动到主窗口 | 可见峰值 Physical | 可见峰值 RSS | 后台 Physical | 后台 RSS | Physical 回落 | Lifetime Peak | 最大线程 | 最大 FD | 平均 CPU | 能耗 | Wakeups |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 764.83 ms | 205.63 MiB | 170.50 MiB | 107.81 MiB | 172.25 MiB | 97.81 MiB | 209.75 MiB | 15 | 45 | 0.023% | 85.471 mJ | 406 |
| 2 | 627.65 ms | 206.16 MiB | 170.25 MiB | 107.53 MiB | 171.86 MiB | 98.62 MiB | 209.67 MiB | 15 | 45 | 0.026% | 101.381 mJ | 415 |
| 3 | 565.32 ms | 205.44 MiB | 170.36 MiB | 107.64 MiB | 172.03 MiB | 97.80 MiB | 206.19 MiB | 15 | 45 | 0.023% | 97.013 mJ | 419 |

关闭窗口后 Physical Footprint 明显回落，但三轮可见窗口为 205.44-206.16 MiB，后台为 107.53-107.81 MiB，仍超过 100 MB 失败线。3 秒观察期不足以判断是否会继续回落；在完成至少 10 分钟和 8 小时采样前，不能声称常驻低于 100 MB。RSS 在窗口关闭后没有同步回落，说明 RSS 不能替代 Physical Footprint 判断 macOS 可回收内存。

同一最终代码使用真实 `NSPasteboard.generalPasteboard` 连续执行 100 次预热和 10,000 次纯文本写入，每 250 次读回正文及来源标记，同时运行监听器检查反馈事件和 Channel 丢弃：

```text
Events=10000; Warmup=100; DurationMs=2070.96;
WriteFailures=0; ReadFailures=0; MarkerFailures=0;
FeedbackEvents=0; DroppedEvents=0;
InitialRssMiB=61.36; PeakRssMiB=75.75; FinalRssMiB=76.41;
Threads=18->20; FileDescriptors=50->52
```

压力测试没有死锁、写入/抽样读回失败、来源标记失败、反馈循环或队列丢弃，但最终 RSS 比初始高 15.05 MiB，线程增加 2、文件描述符增加 2，未满足 `< 8 MiB` 严格资源预算。单次两秒级压力结果不能证明 8 小时稳定，后续必须区分一次性 AppKit/.NET 初始化与持续增长。

### 6.7 2026-07-27 Windows 11 x64 历史、检索与最终 AOT

版本：`phase1/windows-history-search` 最终代码工作树。主机为 Windows 11 Pro 10.0.28000、AMD Ryzen 9 7945HX、100,617,691,136 字节物理内存，.NET SDK 10.0.302。最终构建为 Release、`win-x64`、self-contained Native AOT；`SnapBoard.Desktop.exe` 为 29,425,152 字节（28.06 MiB），SHA-256 为 `D2C307AAEA12BDEFC5DA4FEDC986CDD292C37DC3C83F92049F91D5ED3B677FF0`，发布目录不存在 `coreclr.dll` 或 `clrjit.dll`，发布输出没有 AOT/裁剪警告。

100,000 条测试数据完全由程序生成，平均正文 554.7 字符，混合中文、英文、C#、JSON、URL 和路径。批量导入耗时 27,960.26 ms，数据库为 512,282,624 字节（488.55 MiB）。每类查询预热后执行 50 次选择性查询和 50 次宽查询；每页只投影 50 条摘要，不读取完整正文、原图或缩略图。

| 查询组 | P50 | P95 | 最大值 |
| --- | ---: | ---: | ---: |
| 中文选择性 | 0.75 ms | 0.83 ms | 1.70 ms |
| 英文选择性 | 1.94 ms | 2.58 ms | 3.63 ms |
| 代码选择性 | 0.79 ms | 0.98 ms | 1.60 ms |
| 中文宽查询 | 0.46 ms | 0.56 ms | 1.25 ms |
| 英文宽查询 | 1.37 ms | 1.70 ms | 3.10 ms |
| 代码宽查询 | 1.29 ms | 1.84 ms | 2.17 ms |
| 300 次总体 | 1.16 ms | 2.06 ms | 3.63 ms |

总体 P95 低于 80 ms 目标且最大值低于 200 ms 失败线。该结论只适用于本机生成数据和当前索引；不代表其他硬盘、Windows ARM64、macOS 或 Linux。

最终二进制使用 `Measure-SnapBoardProcess.ps1` 重新执行三轮 10 秒可见窗口和 10 秒关闭窗口采样。每轮为独立进程启动，通过第二实例 `--exit` 干净退出；这不是重启系统后的 OS-cold，也不是长稳测试。

| 轮次 | 启动 | 窗口卸载 | 可见峰值 PWS | 可见峰值 Private | 可见 CPU | 可见句柄 | 关闭后 PWS | 关闭后 Private | 后台 CPU | 最终句柄 | PWS 回落 |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 473.19 ms | 46.11 ms | 155.74 MiB | 189.16 MiB | 0.022% | 1276 | 103.32 MiB | 136.59 MiB | 0.000% | 1288 | 52.41 MiB |
| 2 | 390.58 ms | 40.44 ms | 155.33 MiB | 187.99 MiB | 0.019% | 1276 | 110.13 MiB | 135.54 MiB | 0.000% | 1288 | 45.20 MiB |
| 3 | 403.63 ms | 38.22 ms | 138.97 MiB | 188.12 MiB | 0.022% | 1278 | 94.82 MiB | 127.82 MiB | 0.000% | 1290 | 44.15 MiB |

另一次参数为 600 个关闭窗口样本的托盘测量实际持续 1,152.83 秒（19 分 12.83 秒，CIM 查询开销计入墙钟）。PWS 从关闭阶段峰值 94.44 MiB 回落到 88.29 MiB，Private Bytes 从 150.97 MiB 回落到 120.99 MiB；句柄从 1289 到 1295（峰值 1298），关闭阶段平均 CPU 为 0.000%，并通过 `--exit` 结束且无残留进程。该长样本采于最终密钥缓冲清零、采集错误语义和固定 UI 诊断码小改动之前，但期间没有调用这些路径；最终二进制已由上面的三轮重新验证。它满足“至少 10 分钟观测”的时长，不等于 8 小时长稳，也不能因该次 PWS 低于 100 MiB 而忽略仍为 120.99 MiB 的 Private Bytes。最终三轮的关闭后 PWS 又出现 94.82-110.13 MiB 波动，进一步说明短样本不能替代长稳门槛。

最新 Windows 原生压力样本为 100 次预热和 10,000 次事件：10,000/10,000 匹配，反馈事件、监听器丢弃和写入器丢弃均为 0，句柄 297 -> 294，平均 CPU 0.07%；Private Bytes 从 18,776,064 增至 27,643,904 字节，增长 8,867,840 字节（8.46 MiB）。功能正确性通过，但严格的 `< 8 MiB` 资源预算未通过，必须继续定位；8 小时测试未执行。

### 6.8 2026-07-27 Windows 来源应用图标增量复测

历史摘要增加已持久化 `source_executable_path` 的显式投影后，重新导入 100,000 条、平均 554.7 字符的生成数据耗时 27,701.92 ms，数据库为 512,323,584 字节。300 次搜索总体 P50 1.00 ms、P95 2.32 ms、最大 7.15 ms，仍通过 P95 `< 80 ms` 和最大值 `<= 200 ms` 门槛；选择性查询 150 次总体 P95 为 2.43 ms。

Windows 平台在后台使用版本资源和 `SHGetFileInfoW` 解析显示名/图标，最多缓存 256 个 32 x 32 BGRA 图标，并通过 4 槽有界 Channel 限制 Shell/磁盘并发。串行原生测试预热后使用独立解析器重复提取 64 次，并通过 `GetGuiResources` 比较 GDI Object，增长未超过允许的 2 个测量波动；`HICON`、DIB Section、memory DC 和 screen DC 的释放路径均被真实调用覆盖。

增量最终 `win-x64` self-contained Native AOT EXE 为 29,512,192 字节，SHA-256 `19DE8DE500DF7D76ACDA68AF60D0A0799AD9C93E1E44407C257CA05C30AB653D`，发布输出无 AOT/裁剪警告；主窗口句柄在 476.56 ms 内出现，`--exit` 后退出码 0 且无残留进程。本次只做单次启动和 GDI 专项复测，没有用它替代 6.7 的三轮进程资源、19 分钟托盘或 10,000 次剪贴板样本。当前桌面会话的 Windows Graphics Capture 因 D3D11 `0x887A0005` 失败，GDI 窗口捕获为黑帧，未形成可计视觉截图。
