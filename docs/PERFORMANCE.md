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

结论：轮询 CPU 满足当前空闲预算，但完整可见窗口三次 Physical Footprint 均约 195 MiB，明确超过 120 MB 失败线。第一轮启动 3.97 秒也必须继续调查。当前尚未实现菜单栏常驻和窗口卸载，因此没有“常驻低于 100 MB”的有效样本；2026-07-26 的单次旧样本只能作为历史数据，不能替代本轮三次复测。

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
