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

压力测试没有死锁、写入/抽样读回失败、来源标记失败、反馈循环或队列丢弃。当时最终 RSS 比初始高 15.05 MiB、线程增加 2、文件描述符增加 2，因此按旧方法判为未满足 `< 8 MiB`；6.10 的复查已确认它混入 framework-dependent JIT/runtime 和诊断路径冷启动，修正后的 Native AOT Physical Footprint 预算通过。该历史样本保留用于说明验证方法缺陷，不能再作为 NSPasteboard 泄漏证据。

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

### 6.9 2026-07-28 Windows 打包应用来源与高频刷新收口

SQLite Schema v5 增加来源 AUMID、Package Family 和归属依据的显式投影后，再次导入 100,000 条、平均 554.7 字符的生成数据，耗时 31,113.58 ms，数据库为 515,645,440 字节。每组预热后执行 50 次，结果如下：

| 查询组 | P50 | P95 | 最大值 |
| --- | ---: | ---: | ---: |
| 中文选择性 | 0.82 ms | 0.99 ms | 2.03 ms |
| 英文选择性 | 2.18 ms | 3.02 ms | 7.49 ms |
| 代码选择性 | 1.00 ms | 1.45 ms | 2.57 ms |
| 中文宽查询 | 0.69 ms | 0.79 ms | 1.50 ms |
| 英文宽查询 | 1.87 ms | 2.58 ms | 5.22 ms |
| 代码宽查询 | 1.19 ms | 1.77 ms | 2.76 ms |
| 300 次总体 | 1.15 ms | 2.37 ms | 7.49 ms |

隔离 Windows 平台探针使用 100 次预热和 10,000 次事件：10,000/10,000 匹配，无关事件、反馈事件和两个 Channel 丢弃均为 0，busy 重试 270 次；耗时 245,624 ms，平均 CPU 0.06%，Private Bytes 从 19,759,104 增至 27,500,544 字节，增长 7,741,440 字节（7.38 MiB），句柄 302 -> 299。该探针满足 `< 8 MiB` 预算，但只覆盖平台监听/写入路径。

同轮定位到一个仍在监听真实剪贴板的旧 AOT 桌面进程：数据库仅约 4.1 MB，Private Bytes 已达到 7,242,338,304 字节。调用链显示每次保存都会发布 `HistoryChanged`，旧 UI 为每个事件向调度器排队一次完整 50 项查询、ViewModel 重建以及图标/缩略图加载。当前实现改为单个可复用 150 ms 定时器，只在静默期刷新；Headless 测试连续发送 10,000 次事件后，除初始查询外只发生一次刷新。受旧进程竞争污染的首轮探针在 8,010 次因 `ClipboardBusy` 停止，其 16.29 MiB 增长不作为干净基线；旧进程已通过自身 `--exit` 正常退出。

当前 `win-x64` self-contained Native AOT EXE 为 29,531,648 字节，SHA-256 `F712C3D312F0AE60AEFCF669001AC03ED279E11A51AA2EADA7DDA71BD7AD6E36`。发布输出无 AOT/裁剪警告，目录无 `coreclr.dll`/`clrjit.dll`；单次实际启动在 413.22 ms 创建主窗口，2 秒样本 Working Set 180,600,832 字节、Private Bytes 168,071,168 字节、句柄 1328，并通过 `--exit` 明确退出。该单次样本不替代 6.7 的三轮 PWS/资源数据。修复后的完整 AOT 桌面尚未使用隔离数据目录重跑 10,000 次端到端压力，8 小时长稳也未执行，不能把平台探针通过外推为整体内存门槛通过。

### 6.10 2026-07-28 macOS arm64 共享历史与检索复测

版本：`phase2/macos-history-search-validation`，基线提交 `3be5faa5707c72d80dfc9d7fc01b81edeb9eb66e`。主机为 Apple M4 10 核、16 GB、macOS 26.2 (25C56)、APFS PCIe SSD、.NET SDK 10.0.302；构建为 Release、`osx-arm64` self-contained Native AOT，发布输出无 AOT/裁剪警告。

100,000 条、平均 554.7 字符的生成数据导入耗时 15,289.62 ms，数据库为 515,645,440 字节。150 次中文/英文/代码选择性查询总体 P50 0.46 ms、P95 1.04 ms、最大 1.72 ms；加入 150 次宽查询后的 300 次总体 P50 0.53 ms、P95 1.01 ms、最大 2.23 ms。所有查询都满足 P95 `< 80 ms` 和单次 `<= 200 ms`，该结论只适用于本机 APFS 与当前索引。

最终 App 主程序为 26,606,368 字节 arm64 Mach-O。三次独立启动分别为 1262.00/420.21/458.88 ms；可见窗口峰值 Physical Footprint 为 200.05/200.02/199.66 MiB，RSS 为 164.14/165.53/166.23 MiB。关闭全部窗口后的 Physical Footprint 为 100.05/100.19/100.19 MiB，RSS 为 164.61/164.78/164.56 MiB，Physical 分别回落 100.00/99.83/99.47 MiB；峰值线程 16、FD 45，平均 CPU 0.021%-0.022%。这些 3 秒样本确实略高于 100 MiB；后续根因 A/B 测得纯后台 41.4 MiB、首次开窗 165.0 MiB、关窗 3 秒约 94-96 MiB，说明主要差值来自首次 UI 加载后的框架、字体、托管堆和图形缓存。该基线仍高于 `<= 80 MB` 目标且有超过 100 MiB 的历史波动，因此整体内存门槛继续保持未通过。

原始 framework-dependent NSPasteboard 探针执行 100 次预热和 10,000 次写入时 RSS 为 61.17 -> 76.86 MiB；复查发现旧探针在预热进程/线程/FD 诊断路径前记录基线，并错误地用 RSS 代替 macOS 的 Physical Footprint，因而混入 JIT、分层编译和程序集按需加载。修正后，framework-dependent 版本把预热提高到 10,000 次时 Physical 增长 5.47 MiB；`osx-arm64` Native AOT 以 100 次预热执行 10,000 次时增长 5.09 MiB、FD 7 -> 7，以 100,000 次预热再执行 100,000 次时只增长 0.45 MiB、FD 仍为 7。四轮功能计数均为 0 失败，平台事件路径满足 `< 8 MiB` 预算。

完整 AOT 桌面同 PID 的 10,000 次事件前后 Physical 为 99,009,688 -> 99,042,456 字节，只增加 32 KiB；100 轮快速窗口打开/关闭没有单调增长，Lifetime Peak 保持 213.1 MiB、FD 始终 45，约 28 分钟混合压力后的低侵入样本为 95.1 MiB。该结果排除了按剪贴板事件或窗口周期线性泄漏，但不能替代 8 小时长稳，也不能把约 95 MiB 的 UI 后台基线宣称为达到 80 MiB 目标。

另一次完整桌面先加载真实外部应用历史和 10,000 次压力，再通过 `--close-windows` 保持菜单栏后台 12 分 23 秒。RSS 从 170,240 KiB（166.25 MiB）回落到 99,264 KiB（96.94 MiB），线程 15 -> 14，FD 51 -> 47，CPU 两端为 0.0%；但 `footprint` 从首个关闭后样本 138 MB 变为 139 MB，Lifetime Peak 256 MB。该样本满足至少 10 分钟的观察时长，但 Physical Footprint 明确失败；8 小时测试未执行。重启一致性与字段明细见 `docs/MACOS_CLIPBOARD_VALIDATION.md` 9.6。

### 6.11 2026-07-29 自动更新增量性能与内存对比

实现提交为 `ddf59862f6909a1ebc870f262efd39f2f555df7b`，基线为
`c16dc9ae31b69d1842418c1ca79093a3afbb4736`。主机为 Mac mini（Apple M4 10 核、16 GiB）、
macOS 26.2 (25C56)、arm64、.NET SDK 10.0.302。两边都使用 Release、`osx-arm64`、
self-contained Native AOT、版本 0.2.0，并为每一轮传入不同的私有
`--storage-bootstrap-root`，不读取或修改用户真实历史。

基线发布目录约 148 MiB，主程序 34,757,344 字节；更新功能发布目录约 154 MiB，主程序
35,937,520 字节，增加 1,180,176 字节（3.40%）。最终主程序 SHA-256 为
`29741fe9c14af05517916d01bcc5018b479e5c165121b9fe665327802b75f795`。两边均为 arm64 Mach-O，
无 CoreCLR，0 个 trim/AOT 分析告警；两边都有相同的 2 个 .NET Apple NativeAOT 静态库
clang module-cache 调试信息告警。

测量脚本新增可选隔离存储根参数：

```bash
scripts/macos/Measure-SnapBoardProcess.sh \
  /path/to/SnapBoard.Desktop 3 5 5 /private/tmp/isolated-storage
```

三轮短样本如下。第一轮包含文件、字体和图形缓存冷启动，不等于重启系统后的 OS-cold；后两轮用于
观察同机暖启动。Physical Footprint、RSS、Lifetime Peak、线程、FD、CPU、能耗和 wakeups 都来自
同一 PID。

| 版本/轮次 | 启动 | 窗口 Physical | 后台 Physical | Lifetime Peak | 最大线程 | FD | 平均 CPU |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 基线 1 | 3740.90 ms | 198.06 MiB | 98.47 MiB | 201.27 MiB | 17 | 45 | 0.019% |
| 基线 2 | 684.33 ms | 198.03 MiB | 98.25 MiB | 201.49 MiB | 17 | 45 | 0.055% |
| 基线 3 | 718.38 ms | 197.55 MiB | 98.09 MiB | 200.99 MiB | 17 | 45 | 0.021% |
| 更新 1 | 691.26 ms | 198.36 MiB | 98.61 MiB | 201.91 MiB | 19 | 45 | 0.017% |
| 更新 2 | 744.60 ms | 197.86 MiB | 98.72 MiB | 201.88 MiB | 19 | 45 | 0.020% |
| 更新 3 | 717.43 ms | 197.72 MiB | 98.20 MiB | 197.95 MiB | 19 | 45 | 0.017% |

基线后两轮暖启动均值 701.36 ms，更新版后两轮为 731.02 ms，增加 4.2%。三轮窗口 Physical
平均值基线 197.88 MiB、更新版 197.98 MiB；后台分别为 98.27 MiB 和 98.51 MiB。FD 不变，
更新版因后台更新延迟任务多 2 个线程。差异很小，但样本数不足以作为无回归统计证明。

另各跑一轮 35 秒可见窗口加 5 秒后台，使更新版跨过首次自动检查的 30 秒延迟。基线为启动
718.93 ms、窗口 197.67 MiB、后台 99.39 MiB、Lifetime Peak 201.52 MiB、17 线程、45 FD；
更新版为启动 724.38 ms、窗口 197.53 MiB、后台 99.14 MiB、Lifetime Peak 201.58 MiB、19 线程、
45 FD。裸 AOT 不是 Velopack 已安装实例，因此该轮覆盖“检查后识别为不可更新”的路径，不会访问或
下载远端包。

最终重建后第一次运行另出现启动 3082.94 ms、窗口 251.92 MiB、后台 145.27 MiB、Lifetime Peak
255.63 MiB 的异常冷样本；随后三轮回到上表区间。该异常保留，不按离群点删除，也说明短测不能替代
长期资源门槛。此次没有执行真实网络下载、已安装版本替换、10 分钟/8 小时长稳或 Windows/Linux
测量；既有后台 `<= 80 MB` 目标和 8 小时门槛继续保持未完成。

### 6.12 2026-07-29 macOS 来源身份与原生图标增量复测

主机为 Mac mini（Apple M4 10 核、16 GiB）、macOS 26.2 (25C56)、arm64、.NET SDK
10.0.302。构建为 Release、`osx-arm64` self-contained Native AOT，使用隔离
`--storage-bootstrap-root`，不读取既有历史。来源实现每次检测到外部 `changeCount` 变化只增加一次
`NSWorkspace.frontmostApplication` PID 查询；序列匹配的读取才解析 `NSRunningApplication`。App Bundle
图标固定为 32 x 32 BGRA（4 KiB），以 256 项为缓存上限，理论像素上限约 1 MiB；空结果不缓存。

最终 `.app` 使用 `Measure-SnapBoardProcess.sh` 执行三轮 5 秒可见窗口加 3 秒关窗后台短测：

| 轮次 | 启动 | 窗口 Physical | 后台 Physical | 回落 | Lifetime Peak | 最大线程 | FD | 平均 CPU |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1600.92 ms | 205.59 MiB | 106.47 MiB | 99.12 MiB | 205.97 MiB | 20 | 45 | 0.024% |
| 2 | 833.09 ms | 205.39 MiB | 106.25 MiB | 99.14 MiB | 209.67 MiB | 18 | 45 | 0.023% |
| 3 | 865.14 ms | 205.86 MiB | 106.39 MiB | 99.47 MiB | 205.89 MiB | 19 | 45 | 0.023% |

后两轮暖启动均值 849.12 ms，窗口 Physical 三轮均值 205.61 MiB，后台均值 106.37 MiB，
FD 始终 45。空闲 CPU 和 FD 没有显示新增失控，但后台 Physical 明确超过 100 MiB 失败线，
因此本轮内存门槛未通过。该短样本与 6.11 的构建内容、系统缓存和采样时点不同，不能把约 8 MiB
差值单独归因于本次来源功能；也不能替代 10 分钟或 8 小时长稳。

同一代码的 Native AOT 平台探针先预热 10,000 次，再计量 10,000 次写入，耗时 1965.02 ms。
写入、抽样读回、来源标记、反馈事件和 Channel 丢弃均为 0 失败；Physical Footprint
13.63 -> 17.64 MiB，增长 4.02 MiB，RSS 35.16 -> 39.92 MiB，线程 12 -> 13，FD 7 -> 7，
满足平台探针 `< 8 MiB` 预算。该探针为同适配器自写，反馈保护会在来源 PID 查询前抑制事件，
所以它证明既有写回/监听资源路径未回归，不把它伪装成外部来源压力。

外部来源路径由真实 TextEdit 新记录验证：列表和详情显示“文本编辑”及原生图标；平台测试同时断言
相同 Bundle 第二次解析复用同一缓存元数据，不重复分配 4 KiB 像素。当前没有执行 10,000 次外部
应用切换、256 个不同 App Bundle、8 小时长稳或多应用矩阵；完整 UI 后台 `<= 80 MB` 目标继续未完成。

最终主程序 35,862,528 字节，SHA-256
`77cb788aaeb26f15cb0e92eafd081bed1d5ec16ab0c442cd9ef1102e84e4407c`；DMG 30,847,110 字节，
SHA-256 `bc1afb313d204fcfc1c163f8243ccdaeeb4535d290b88e163265dd4e7de0ee07`；PKG
27,545,801 字节，SHA-256 `20e139792f2f2409e93ec459f2cf863a224df51a7a1135af44c2618b3a2dbbad`。
三者来自当前代码，主程序为 arm64 Mach-O，App Bundle `codesign --deep --strict` 通过；0 个
trim/AOT 分析告警，仍有 2 个既有且已解释的 .NET Apple NativeAOT 静态库 clang module-cache
调试信息告警。包仍为 ad-hoc/未公证开发包。
