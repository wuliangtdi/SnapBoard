# SnapBoard 第 2 版命令中心 Design QA

## 比较目标

- source visual truth path: `docs/design/snapboard-command-center-reference.png`
- implementation screenshot path: `docs/design/snapboard-command-center-implementation.png`
- viewport: 1487 x 1058
- source pixels: 1487 x 1058 PNG
- implementation pixels: 1487 x 1058 PNG
- implementation logical size: 1487 x 1058 Avalonia DIP
- density normalization: source 与实现均按 1:1 像素比较，无缩放或二次采样
- state: Light 主题、全部类型、最新优先、第一条代码记录选中、同步完成

参考稿包含约 31 px 的 Windows 原生标题栏；实现证据由 Avalonia Headless/Skia 输出，不包含操作系统窗口装饰。比较时对齐标题栏以下的应用内容区域，这属于平台外壳差异，不计为应用视觉偏差。

## Full-view comparison evidence

参考稿与最终实现已在同一次视觉输入中以原始分辨率打开比较。主要区域比例、搜索框宽度、49/51 双栏、列表列线、79 DIP 行密度、选中状态、代码预览尺寸、元数据顺序和底部状态栏均保持同一信息层级。

## Focused region comparison evidence

未额外生成裁剪图。两张证据均为 1487 x 1058 原始分辨率，列表标题、来源应用、时间、代码行号、语法颜色和元数据在同一比较输入中清晰可读，已足以检查密集区域，无需通过放大或裁剪改变像素关系。

## Required fidelity surfaces

- Fonts and typography: 应用正文使用 Inter，代码使用 Cascadia Mono/JetBrains Mono/Menlo/Consolas 回退；字重、14 px 主文本、12-13 px 辅助文本、24 px 代码行高和 0 字距符合参考层级。长标题和 URL 使用单行截断，没有重叠。
- Spacing and layout rhythm: 搜索框 660 DIP，列表与预览为 49/51，列表图标、标题、来源和时间列与参考稿对齐；连续分隔线替代卡片，圆角均不超过 6 px。
- Colors and visual tokens: 白色表面、石墨正文、灰色分隔线、`#1677FF` 强调色、淡蓝选中底、绿色同步、琥珀置顶和红色删除与参考语义一致，对比度清晰。
- Image quality and asset fidelity: 品牌图标为 ImageGen 生成的 1254 x 1254 PNG，在 54 DIP 槽位中清晰显示，无拉伸、压缩块或边缘光晕；其余操作图标来自同一 Material Icons 库，没有手绘 SVG、字符图标或占位图形。
- Copy and content: 品牌、搜索、筛选、排序、来源、时间、代码摘要、类型、语言、字符数、来源窗口、位置和备注均为独立可理解的中文界面文案；模拟剪贴板数据经过脱敏。
- Icons: 工具栏、类型、来源和状态图标使用统一笔画与尺寸，按钮均提供 Tooltip。
- States and interactions: Headless 测试验证搜索输入、代码筛选、紧凑模式和默认选择；ViewModel 测试覆盖搜索、筛选、删除和状态切换。
- Accessibility and resilience: 启动焦点进入搜索框，焦点边框可见，核心按钮可键盘触发；最小尺寸和 200% 缩放仍需在 Windows 11 实机阶段补充截图与辅助技术测试。

## Findings

没有剩余可执行的 P0、P1 或 P2 视觉问题。

可接受的 P3/约束差异：

- Headless 证据不渲染原生标题栏，正式应用由目标系统提供窗口装饰。
- 设置和紧凑模式使用图标加 Tooltip，而不是参考稿的图标加文字，符合桌面工具栏控件规范。
- 参考稿底部的快捷键教程条改为右侧状态文本；快捷键帮助后续进入设置或命令菜单，避免主界面常驻使用说明。

## Comparison history

### Iteration 1

- Earlier evidence: 本轮早期本机窗口截图（仅用于迭代诊断，未纳入仓库）
- P2 findings: 默认错误选中“代码”筛选；列表只有两条记录导致信息密度失真；选中背景过饱和；窗口比例和右侧预览高度偏离参考稿。
- Fixes: 启动时显式选择“全部”；扩充 10 条脱敏数据；自定义 ListBoxItem 主题；重设双栏比例、行高和预览网格。
- Post-fix evidence: 本轮中间态本机窗口截图（仅用于迭代诊断，未纳入仓库）

### Iteration 2

- P2 findings: 搜索框因 `MaxWidth + Left` 收缩，列表标题与图标间距不足，代码预览仍为单色且缺少备注字段。
- Fixes: 固定搜索框为 660 DIP；按参考稿调整品牌列、列表左边距和 79 DIP 行高；增加轻量代码着色控件与备注行；使用 1487 x 1058 Headless/Skia 画布复核。
- Post-fix evidence: `docs/design/snapboard-command-center-implementation.png`

### Final comparison

- Source 和最终实现已在同一比较输入中按原始分辨率检查。
- 早期 P2 均已修复，没有新增 P0/P1/P2。

## Implementation checklist

- [x] 品牌、搜索、筛选和排序区域
- [x] 49/51 历史与预览双栏
- [x] 稳定列表密度、选中和空状态
- [x] 代码行号与轻量语法着色
- [x] 元数据、底部状态和工具栏命令
- [x] 搜索、筛选、紧凑模式 Headless 交互
- [x] 同尺寸最终视觉比较

final result: passed
