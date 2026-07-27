using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Desktop.Views;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Desktop.HeadlessTests;

public sealed class SecondaryWindowHeadlessTests
{
    [AvaloniaFact]
    public void SettingsWindowCanBeClosedAndRecreated()
    {
        FakeGlobalHotKeyService hotKeyService = new();
        FakeAutoStartService autoStartService = new();

        SettingsWindow first = CreateSettingsWindow(hotKeyService, autoStartService);
        first.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(first.FindControl<Button>("HotKeyCaptureButton"));
        first.Close();
        Dispatcher.UIThread.RunJobs();

        SettingsWindow second = CreateSettingsWindow(hotKeyService, autoStartService);
        try
        {
            second.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.NotSame(first, second);
            Assert.True(second.IsVisible);
        }
        finally
        {
            second.Close();
        }
    }

    [AvaloniaFact]
    public void SettingsWindowRendersTheBrandedLayout()
    {
        SettingsWindow window = CreateSettingsWindow(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService());

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new Avalonia.Size(640, 520), window.ClientSize);
            Assert.NotNull(window.Icon);
            Assert.NotNull(window.FindControl<Button>("HotKeyCaptureButton"));
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal(640, frame.PixelSize.Width);
            Assert.Equal(520, frame.PixelSize.Height);

            string? capturePath =
                Environment.GetEnvironmentVariable("SNAPBOARD_SETTINGS_CAPTURE_PATH");
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

    [Fact]
    public async Task SettingsViewModelCapturesAndAppliesCustomHotKey()
    {
        FakeGlobalHotKeyService hotKeyService = new();
        SettingsViewModel viewModel = new(hotKeyService, new FakeAutoStartService());

        viewModel.BeginHotKeyCapture();
        bool captured = viewModel.CaptureHotKey(
            GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Alt,
            "K");

        Assert.True(captured);
        Assert.Equal("Ctrl+Alt+K", viewModel.HotKeyDisplayName);
        Assert.True(viewModel.HasPendingHotKeyChange);

        await viewModel.ApplyHotKeyCommand.ExecuteAsync(null);

        Assert.Equal("Ctrl+Alt+K", hotKeyService.ConfiguredGesture.DisplayName);
        Assert.Equal(0x4Bu, hotKeyService.ConfiguredGesture.VirtualKey);
        Assert.False(viewModel.HasPendingHotKeyChange);
    }

    private static SettingsWindow CreateSettingsWindow(
        IGlobalHotKeyService hotKeyService,
        IAutoStartService autoStartService) => new()
        {
            DataContext = new SettingsViewModel(hotKeyService, autoStartService),
        };

    private sealed class FakeGlobalHotKeyService : IGlobalHotKeyService
    {
        public event EventHandler? Pressed
        {
            add { }
            remove { }
        }

        public GlobalHotKeyGesture? CurrentGesture { get; private set; }

        public GlobalHotKeyGesture ConfiguredGesture { get; private set; } =
            GlobalHotKeyGesture.Default;

        public GlobalHotKeyGestureCreationResult CreateGesture(
            GlobalHotKeyModifiers modifiers,
            string keyName)
        {
            GlobalHotKeyModifiers userModifiers = modifiers &
                (GlobalHotKeyModifiers.Control |
                 GlobalHotKeyModifiers.Alt |
                 GlobalHotKeyModifiers.Shift |
                 GlobalHotKeyModifiers.Windows);
            if (userModifiers == GlobalHotKeyModifiers.None)
            {
                return new GlobalHotKeyGestureCreationResult(
                    GlobalHotKeyGestureCreationStatus.MissingModifier);
            }

            if (keyName.Length != 1 || keyName[0] is < 'A' or > 'Z')
            {
                return new GlobalHotKeyGestureCreationResult(
                    GlobalHotKeyGestureCreationStatus.UnsupportedKey);
            }

            List<string> displayParts = [];
            if (userModifiers.HasFlag(GlobalHotKeyModifiers.Control))
            {
                displayParts.Add("Ctrl");
            }

            if (userModifiers.HasFlag(GlobalHotKeyModifiers.Alt))
            {
                displayParts.Add("Alt");
            }

            if (userModifiers.HasFlag(GlobalHotKeyModifiers.Shift))
            {
                displayParts.Add("Shift");
            }

            if (userModifiers.HasFlag(GlobalHotKeyModifiers.Windows))
            {
                displayParts.Add("Win");
            }

            displayParts.Add(keyName);
            GlobalHotKeyGesture gesture = new(
                userModifiers | GlobalHotKeyModifiers.NoRepeat,
                keyName[0],
                string.Join('+', displayParts));
            return new GlobalHotKeyGestureCreationResult(
                GlobalHotKeyGestureCreationStatus.Created,
                gesture);
        }

        public ValueTask<GlobalHotKeyRegistrationResult> RegisterAsync(
            GlobalHotKeyGesture gesture,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentGesture = gesture;
            ConfiguredGesture = gesture;
            return ValueTask.FromResult(new GlobalHotKeyRegistrationResult(
                GlobalHotKeyRegistrationStatus.Registered));
        }

        public ValueTask UnregisterAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentGesture = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAutoStartService : IAutoStartService
    {
        private bool _enabled;

        public bool IsEnabled() => _enabled;

        public AutoStartUpdateResult SetEnabled(bool enabled)
        {
            _enabled = enabled;
            return new AutoStartUpdateResult(AutoStartUpdateStatus.Updated);
        }
    }
}
