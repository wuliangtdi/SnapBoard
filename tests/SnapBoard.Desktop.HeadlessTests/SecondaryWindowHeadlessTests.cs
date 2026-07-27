using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
        Assert.NotNull(first.FindControl<ComboBox>("HotKeySelector"));
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
