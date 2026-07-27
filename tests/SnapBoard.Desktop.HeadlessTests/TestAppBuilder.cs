using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(SnapBoard.Desktop.HeadlessTests.TestAppBuilder))]

namespace SnapBoard.Desktop.HeadlessTests;

/// <summary>
/// 使用真实 Skia 渲染器启动 Avalonia Headless，使测试既能验证交互，
/// 也能产出不受宿主桌面、窗口阴影和显示器缩放影响的稳定截图。
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        App.EnableNativeWindowsLifecycle = false;
        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            });
    }
}
