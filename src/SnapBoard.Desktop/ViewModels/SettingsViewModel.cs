using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Desktop.ViewModels;

public sealed record HotKeyOption(string DisplayName, GlobalHotKeyGesture Gesture);

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IAutoStartService _autoStartService;
    private readonly IGlobalHotKeyService _hotKeyService;
    private bool _initializing;

    public SettingsViewModel(
        IGlobalHotKeyService hotKeyService,
        IAutoStartService autoStartService)
    {
        _hotKeyService = hotKeyService;
        _autoStartService = autoStartService;
        HotKeyOptions = CreateHotKeyOptions(hotKeyService.ConfiguredGesture);
        SelectedHotKey = HotKeyOptions.First(option =>
            option.Gesture == hotKeyService.ConfiguredGesture);

        _initializing = true;
        IsAutoStartEnabled = autoStartService.IsEnabled();
        _initializing = false;
    }

    public IReadOnlyList<HotKeyOption> HotKeyOptions { get; }

    [ObservableProperty]
    public partial HotKeyOption SelectedHotKey { get; set; }

    [ObservableProperty]
    public partial bool IsAutoStartEnabled { get; set; }

    [ObservableProperty]
    public partial string HotKeyStatus { get; set; } = "快捷键可用";

    [ObservableProperty]
    public partial string AutoStartStatus { get; set; } = string.Empty;

    partial void OnIsAutoStartEnabledChanged(bool value)
    {
        if (_initializing)
        {
            return;
        }

        AutoStartUpdateResult result = _autoStartService.SetEnabled(value);
        AutoStartStatus = result.Status == AutoStartUpdateStatus.Updated
            ? value ? "已启用开机启动" : "已关闭开机启动"
            : "开机启动设置失败";
        if (result.Status != AutoStartUpdateStatus.Updated)
        {
            _initializing = true;
            IsAutoStartEnabled = _autoStartService.IsEnabled();
            _initializing = false;
        }
    }

    [RelayCommand]
    private async Task ApplyHotKeyAsync()
    {
        GlobalHotKeyRegistrationResult result = await _hotKeyService.RegisterAsync(
            SelectedHotKey.Gesture,
            CancellationToken.None);
        HotKeyStatus = result.Status switch
        {
            GlobalHotKeyRegistrationStatus.Registered =>
                $"已启用 {SelectedHotKey.DisplayName}",
            GlobalHotKeyRegistrationStatus.Conflict =>
                "快捷键已被其他应用占用，原快捷键已恢复",
            GlobalHotKeyRegistrationStatus.Unsupported => "当前平台不支持全局快捷键",
            _ => "快捷键注册失败",
        };
    }

    [RelayCommand]
    private async Task RestoreDefaultHotKeyAsync()
    {
        SelectedHotKey = HotKeyOptions.First(option =>
            option.Gesture == GlobalHotKeyGesture.Default);
        await ApplyHotKeyAsync();
    }

    private static List<HotKeyOption> CreateHotKeyOptions(
        GlobalHotKeyGesture configuredGesture)
    {
        List<HotKeyOption> options =
        [
            new("Ctrl+Shift+V", GlobalHotKeyGesture.Default),
            new("Ctrl+Alt+V", new GlobalHotKeyGesture(
                GlobalHotKeyModifiers.Control |
                GlobalHotKeyModifiers.Alt |
                GlobalHotKeyModifiers.NoRepeat,
                0x56,
                "Ctrl+Alt+V")),
            new("Ctrl+Shift+Space", new GlobalHotKeyGesture(
                GlobalHotKeyModifiers.Control |
                GlobalHotKeyModifiers.Shift |
                GlobalHotKeyModifiers.NoRepeat,
                0x20,
                "Ctrl+Shift+Space")),
            new("Ctrl+Alt+Space", new GlobalHotKeyGesture(
                GlobalHotKeyModifiers.Control |
                GlobalHotKeyModifiers.Alt |
                GlobalHotKeyModifiers.NoRepeat,
                0x20,
                "Ctrl+Alt+Space")),
        ];

        if (!options.Any(option => option.Gesture == configuredGesture))
        {
            options.Add(new HotKeyOption(configuredGesture.DisplayName, configuredGesture));
        }

        return options;
    }
}
