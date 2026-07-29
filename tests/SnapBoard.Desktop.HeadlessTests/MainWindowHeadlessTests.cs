using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SnapBoard.Desktop.Controls;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Desktop.Views;

namespace SnapBoard.Desktop.HeadlessTests;

public sealed class MainWindowHeadlessTests
{
    [AvaloniaFact]
    public void MainWindowRendersTheSelectedCommandCenterState()
    {
        MainViewModel viewModel = new();
        MainWindow window = CreateWindow(viewModel, 1487, 1058);

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // 视觉基线与选定参考稿使用完全相同的画布，避免缩放掩盖间距问题。
            Assert.Equal(new Avalonia.Size(1487, 1058), window.ClientSize);
            Assert.Equal("闪剪", window.Title);
            Assert.NotNull(window.Icon);
            Assert.NotNull(window.FindControl<TextBox>("SearchBox"));
            Assert.NotNull(window.FindControl<Button>("SettingsButton"));
            SyntaxHighlightedCodeView codePreview = window.FindControl<SyntaxHighlightedCodeView>("CodePreview")!;
            Assert.Contains("MainViewModel", codePreview.Code, StringComparison.Ordinal);
            ListBox historyList = window.FindControl<ListBox>("HistoryList")!;
            historyList.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(viewModel.SelectedItem);

            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal(1487, frame.PixelSize.Width);
            Assert.Equal(1058, frame.PixelSize.Height);

            string? capturePath = Environment.GetEnvironmentVariable("SNAPBOARD_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
                frame.Save(capturePath, PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SearchFilterAndCompactModeAreWiredThroughTheRenderedWindow()
    {
        MainViewModel viewModel = new();
        MainWindow window = CreateWindow(viewModel);

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            TextBox searchBox = window.FindControl<TextBox>("SearchBox")!;
            searchBox.Focus();
            window.KeyTextInput("Avalonia");

            Assert.Equal("Avalonia", viewModel.SearchText);
            Assert.NotEmpty(viewModel.VisibleItems);
            Assert.All(viewModel.VisibleItems, item =>
                Assert.Contains("Avalonia", $"{item.Title} {item.Subtitle} {item.Content}", StringComparison.OrdinalIgnoreCase));

            searchBox.Text = string.Empty;
            ToggleButton codeFilterButton = window.FindControl<ToggleButton>("CodeFilterButton")!;
            ActivateButton(window, codeFilterButton);

            Assert.Equal(ClipboardItemType.Code, viewModel.SelectedFilter);
            Assert.All(viewModel.VisibleItems, item => Assert.Equal(ClipboardItemType.Code, item.Type));

            Button compactModeButton = window.FindControl<Button>("CompactModeButton")!;
            ActivateButton(window, compactModeButton);

            Assert.True(viewModel.IsCompactMode);
            Assert.Equal(new GridLength(0), viewModel.PreviewColumnWidth);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void HeaderSearchAdaptsToWindowWidthWithoutOverlappingCommands()
    {
        HeaderLayout normal = MeasureHeaderLayout(
            982,
            730,
            "SNAPBOARD_HEADER_NORMAL_CAPTURE_PATH");
        HeaderLayout maximized = MeasureHeaderLayout(
            1914,
            1017,
            "SNAPBOARD_HEADER_MAXIMIZED_CAPTURE_PATH");

        Assert.True(normal.SearchRight + 16 <= normal.SyncLeft);
        Assert.True(maximized.SearchRight + 16 <= maximized.SyncLeft);
        Assert.InRange(Math.Abs(normal.SearchLeft - maximized.SearchLeft), 0, 1);
        Assert.True(maximized.SearchWidth >= normal.SearchWidth + 800);
        Assert.InRange(normal.CompactRight, 940, 958);
        Assert.InRange(maximized.CompactRight, 1872, 1890);
    }

    [AvaloniaFact]
    public void QuickWindowRendersAndSelectsClipboardHistory()
    {
        MainViewModel viewModel = new();
        QuickWindow window = new()
        {
            DataContext = viewModel,
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new Avalonia.Size(680, 480), window.ClientSize);
            Assert.Equal("闪剪", window.Title);
            Assert.NotNull(window.Icon);
            Assert.NotNull(window.FindControl<Image>("QuickBrandLogo"));
            Assert.NotNull(window.FindControl<TextBlock>("QuickProductName"));
            Assert.NotNull(window.FindControl<TextBlock>("QuickSearchDescription"));
            Assert.NotNull(window.FindControl<TextBox>("QuickSearchBox"));
            Assert.NotNull(window.FindControl<Button>("QuickClearSearchButton"));
            ListBox historyList = window.FindControl<ListBox>("QuickHistoryList")!;
            Assert.Equal(viewModel.VisibleItems.Count, historyList.ItemCount);
            Assert.NotNull(viewModel.SelectedItem);
            Assert.IsType<Button>(window.FindControl<Button>("QuickPlainTextPasteButton"));
            Assert.IsType<Button>(window.FindControl<Button>("QuickPasteButton"));

            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal(680, frame.PixelSize.Width);
            Assert.Equal(480, frame.PixelSize.Height);
            string? capturePath = Environment.GetEnvironmentVariable("SNAPBOARD_QUICK_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
                frame.Save(capturePath, PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void QuickWindowMinimumSizeKeepsSearchAndPasteActionsInViewport()
    {
        QuickWindow window = new()
        {
            DataContext = new MainViewModel(),
            Width = 560,
            Height = 380,
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new Avalonia.Size(560, 380), window.ClientSize);
            TextBox searchBox = window.FindControl<TextBox>("QuickSearchBox")!;
            Button plainTextButton = window.FindControl<Button>("QuickPlainTextPasteButton")!;
            Button pasteButton = window.FindControl<Button>("QuickPasteButton")!;
            Assert.True(IsInsideWindow(searchBox, window));
            Assert.True(IsInsideWindow(plainTextButton, window));
            Assert.True(IsInsideWindow(pasteButton, window));

            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            string? capturePath =
                Environment.GetEnvironmentVariable("SNAPBOARD_QUICK_MIN_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
                frame.Save(capturePath, PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static MainWindow CreateWindow(MainViewModel viewModel, double width = 1305, double height = 900)
        => new()
        {
            DataContext = viewModel,
            Width = width,
            Height = height,
        };

    private static HeaderLayout MeasureHeaderLayout(
        double width,
        double height,
        string capturePathVariable)
    {
        MainWindow window = CreateWindow(new MainViewModel(), width, height);

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            TextBox searchBox = window.FindControl<TextBox>("SearchBox")!;
            Button syncButton = window.FindControl<Button>("SyncButton")!;
            Button compactModeButton = window.FindControl<Button>("CompactModeButton")!;
            Avalonia.Point searchOrigin = searchBox.TranslatePoint(default, window)!.Value;
            Avalonia.Point syncOrigin = syncButton.TranslatePoint(default, window)!.Value;
            Avalonia.Point compactOrigin = compactModeButton.TranslatePoint(default, window)!.Value;

            string? capturePath = Environment.GetEnvironmentVariable(capturePathVariable);
            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
                frame.Save(capturePath, PngBitmapEncoderOptions.Default);
            }

            return new HeaderLayout(
                searchOrigin.X,
                searchBox.Bounds.Width,
                syncOrigin.X,
                compactOrigin.X + compactModeButton.Bounds.Width);
        }
        finally
        {
            window.Close();
        }
    }

    private static void ActivateButton(Window window, Button button)
    {
        button.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static bool IsInsideWindow(Control control, Window window)
    {
        Avalonia.Point origin = control.TranslatePoint(default, window)!.Value;
        return origin.X >= 0 && origin.Y >= 0 &&
            origin.X + control.Bounds.Width <= window.ClientSize.Width &&
            origin.Y + control.Bounds.Height <= window.ClientSize.Height;
    }

    private readonly record struct HeaderLayout(
        double SearchLeft,
        double SearchWidth,
        double SyncLeft,
        double CompactRight)
    {
        public double SearchRight => SearchLeft + SearchWidth;
    }
}
