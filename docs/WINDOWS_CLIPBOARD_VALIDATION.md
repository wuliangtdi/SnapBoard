# Windows 剪贴板验收记录

> 日期：2026-07-26
> 环境：Windows 11 Pro 10.0.28000 x64，.NET SDK 10.0.302
> 分支：`phase1/windows-clipboard`

## 1. 自动测试

`SnapBoard.Platform.Windows.Tests` 共 21 项，区分纯逻辑测试和真实系统剪贴板集成测试。真实集成测试在 Windows 上串行执行，覆盖监听器生命周期、跨适配器事件、Unicode/ANSI Text、HTML、RTF、DIB、File List、格式清单、来源进程、自定义来源标记和反馈循环抑制。

自动粘贴的确定性测试覆盖同级权限成功、高完整性目标降级、`SendInput` 失败降级，以及 x64 `INPUT` 结构必须为 40 字节。自动测试不会向用户当前前台应用注入真实粘贴快捷键，因此不替代交互式桌面验收。

## 2. 交互式桌面结果

所有操作只使用生成的测试文本。探针只输出序列号、来源、格式名称和布尔状态，不输出剪贴板正文。

| 场景 | 目标 | 结果 | 实际证据/限制 |
| --- | --- | --- | --- |
| 文本复制监听 | Windows 打包版 Notepad 11.2605.34.0 | 通过 | 生成文本复制后捕获 `Sequence=6388`、`Source=Notepad`、`SourceAccess=Identified`、`CF_UNICODETEXT`，`DroppedEvents=0` |
| 浏览器复制 | Google Chrome | 未完成 | 创建独立窗口后，桌面控制因无法可靠确认当前 URL 被安全机制终止；未把浏览器插件的虚拟剪贴板结果计为系统剪贴板通过 |
| UWP/WinUI | 未选择目标应用 | 未执行 | 不能由包已安装或 Headless 测试推断通过 |
| Explorer 文件复制 | 文件资源管理器 | 未执行 | 自动集成已验证 `CF_HDROP`，但未执行 Explorer 中真实选中文件并复制 |
| 普通窗口自动粘贴 | 外部应用 | 未执行 | `SendInput` ABI 和结果映射已自动测试，真实前台恢复/粘贴仍待验收 |
| 管理员窗口/UIPI | 高完整性目标 | 未执行 | 当前进程为非提升令牌；本轮未请求或接受 UAC，不能声称降级提示已实机通过 |
| Office | Word/Excel/PowerPoint | 未执行 | 未操作现有用户文档，不伪造 Office 结果 |
| 远程桌面 | RDP 会话 | 未执行 | 未操作现有远程会话，不伪造远程桌面结果 |

## 3. 待验收

- 构造真实 delayed-rendering clipboard owner，并验证有限重试和取消。
- Chrome/Edge/Firefox 的正文、地址栏、HTML、图片复制与写回。
- Explorer 的单文件、多文件和目录 `CF_HDROP`。
- 一个明确的 UWP/WinUI 文本目标与普通权限自动粘贴。
- 经用户确认后启动高完整性目标，验证 UIPI 返回“已复制，请手动粘贴”。
- Office 和远程桌面只在可隔离的生成数据/测试会话中执行。
- 8 小时、10,000 次事件、句柄与内存增长长稳测试。
