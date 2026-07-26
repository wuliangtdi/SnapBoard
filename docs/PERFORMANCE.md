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
- 空闲状态不使用高频轮询；WebDAV 默认 10 到 30 秒拉取。

## 6. 当前基线

- Release 构建：0 警告、0 错误。
- `osx-arm64` Native AOT：已成功发布。
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
