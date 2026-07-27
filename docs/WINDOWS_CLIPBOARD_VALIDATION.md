# Windows 剪贴板验收记录

> 日期：2026-07-27
> 环境：Windows 11 Pro 10.0.28000 x64，.NET SDK 10.0.302
> 分支：`phase1/windows-completion`

## 1. 自动测试

`SnapBoard.Platform.Windows.Tests` 共 29 项并全部通过，区分纯逻辑测试和真实 Windows 原生集成测试。覆盖监听器生命周期、取消、序列号去重、有限退避、队列溢出、Unicode/ANSI Text、HTML、RTF、DIB/DIBV5、File List、格式清单、来源进程、自定义来源标记、反馈抑制、`INPUT` ABI、UIPI 结构化降级、SendInput 前 HWND/PID 与前台窗口二次校验、热键冲突回滚、两个真实 message-only window 的注册冲突、开机启动配置和 CurrentUserOnly 单实例命名管道。

`SnapBoard.Desktop.HeadlessTests` 共 12 项并全部通过，其中新增快速窗口真实 XAML 渲染、设置窗口关闭后重新创建、后台第二实例不激活主窗口，以及暂停记录时持续排空 100 个轻量事件但不读取正文、恢复后继续读取。完整解决方案共 69 项：64 项通过、5 项仅限 macOS 原生环境的测试跳过、0 项失败。

自动测试不会向用户当前前台应用注入真实粘贴快捷键，也不会修改用户真实的开机启动设置，因此不能替代交互式桌面验收。

## 2. 探针与压力结果

真实 delayed-rendering owner 创建隐藏消息窗口，先以空句柄声明 `CF_UNICODETEXT`，收到 `WM_RENDERFORMAT` 后才提交正文。结果为：

```text
DelayedRendering=Passed; ReadStatus=Success; Reason=None; TextMatched=True
```

三轮 10,000 次压力测试的功能正确性均通过：无死锁，10,000 个正常自写事件均被观察到，反馈循环和监听器/写入器 Channel 丢弃均为 0。资源增长预算单独判定为失败。

| 轮次 | Warmup | 自写事件 | 无关事件 | Busy 重试 | 反馈事件 | Channel 丢弃 | 平均 CPU | Private Bytes 增长 | 句柄变化 | `< 8 MiB` |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 1 | 0 | 10,000/10,000 | 0 | 159 | 0 | 0 | 0.07% | 15.56 MiB | +14 | 未通过 |
| 2 | 128 | 10,000/10,000 | 0 | 176 | 0 | 0 | 0.07% | 8.43 MiB | +2 | 未通过 |
| 3 | 1,000 | 10,000/10,000 | 1 | 171 | 0 | 0 | 0.05% | 15.12 MiB | -11 | 未通过 |

因此，Phase 1.3 的“无死锁、正常事件不丢失、不产生无限自复制”功能退出条件已验证，但 8 MiB 资源增长门槛和 8 小时长稳仍未完成。

## 3. Windows 桌面生命周期实机结果

| 场景 | 结果 | 实际证据/限制 |
| --- | --- | --- |
| 单实例与第二实例激活 | 通过 | 主实例运行时分别启动 `--settings`、`--quick` 和默认命令，窗口在同一 PID 中打开，进程数保持 1 |
| 后台启动与明确退出 | 通过 | `--background` 启动时不显示主窗口；`--exit` 通过命名管道请求主实例干净退出 |
| 主/快速/设置窗口生命周期 | 通过 | 三类窗口均实际显示；Headless 验证设置窗口关闭后可重新创建，窗口关闭后解除 DataContext 和事件订阅 |
| 全局快捷键冲突 | 自动原生通过 | 两个真实 message-only window 注册同一 F24 组合键，第二个返回冲突且释放后可恢复；未执行物理按键唤起 |
| 托盘菜单 | 未完成 | 托盘对象和菜单已初始化，后台进程可常驻；桌面截图设备异常导致本轮未可靠点击托盘菜单，不能标记交互通过 |
| 多显示器/DPI | 未完成 | Per-Monitor V2、监视器工作区和缩放定位已实现；本轮没有多显示器/DPI 实机矩阵 |
| 开机启动 | 自动确定性通过 | HKCU Run 命令生成、启用、禁用由 fake registry 验证；未修改当前用户真实开机启动项 |

## 4. 外部应用交互结果

所有操作只使用生成的测试文本。探针只输出序列号、来源、格式名称和布尔状态，不输出已有剪贴板正文。

| 场景 | 目标 | 结果 | 实际证据/限制 |
| --- | --- | --- | --- |
| 文本复制监听 | Windows 打包版 Notepad 11.2605.34.0 | 通过 | 生成文本复制后捕获 `Sequence=6388`、`Source=Notepad`、`SourceAccess=Identified`、`CF_UNICODETEXT`，`DroppedEvents=0` |
| 普通窗口纯文本写回与自动粘贴 | Windows 打包版 Notepad | 通过 | 指定有效 HWND 后 `TargetValid=True`、`PasteStatus=Pasted`；UI Automation 读取到完整生成文本 `SnapBoard explicit target auto paste 2026-07-27` |
| UWP/WinUI | Windows 打包版 Notepad | 通过（文本路径） | 目标进程实际加载 `Microsoft.UI.Xaml.dll`、`Microsoft.UI.Xaml.Controls.dll` 和 `Microsoft.UI.Xaml.Internal.dll`；已验证文本写回、恢复和自动粘贴，不代表其他 UWP/WinUI 应用或图片格式 |
| Explorer 单/多文件和目录复制 | 文件资源管理器 | 未完成 | 自动集成已验证 `CF_HDROP`；实机准备生成文件后 Explorer 复用现有标签页并检测到用户输入，按安全要求停止，临时文件已清理，未产生可计结果 |
| Chrome/Edge/Firefox 正文、HTML、地址栏和图片 | 浏览器 | 未完成 | 本轮桌面截图设备异常且输入控制不可可靠执行；没有把插件或虚拟剪贴板结果计为系统剪贴板通过 |
| 管理员窗口/UIPI | 高完整性目标 | 未执行 | 未得到本轮单独的提升许可，也未操作 UAC；仅有确定性测试验证 `HigherIntegrityTarget` 降级为“已复制，请手动粘贴” |
| Office | Word/Excel/PowerPoint | 未执行 | 未操作现有用户文档，不推断 Office 兼容性 |
| 远程桌面 | RDP 会话 | 未执行 | 未操作现有远程会话，不推断远程剪贴板兼容性 |

## 5. 待验收

- 物理全局快捷键、托盘菜单点击、真实 HKCU 开机启动、多显示器、混合 DPI、睡眠唤醒和 8 小时后台常驻。
- Explorer 的单文件、多文件和目录 `CF_HDROP`。
- Chrome/Edge/Firefox 的正文、地址栏、HTML、图片复制与写回。
- 经用户明确允许后启动高完整性目标，验证 UIPI 返回“已复制，请手动粘贴”。
- Office 和远程桌面只在可隔离的生成数据或测试会话中执行。
- 定位 10,000 次压力测试 Private Bytes 增长并满足 `< 8 MiB` 预算。
