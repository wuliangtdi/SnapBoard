using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IAutoStartService _autoStartService;
    private readonly IGlobalHotKeyService _hotKeyService;
    private GlobalHotKeyGesture _pendingHotKey;
    private bool _initializing;

    public SettingsViewModel(
        IGlobalHotKeyService hotKeyService,
        IAutoStartService autoStartService)
    {
        _hotKeyService = hotKeyService;
        _autoStartService = autoStartService;
        _pendingHotKey = hotKeyService.ConfiguredGesture;
        HotKeyDisplayName = _pendingHotKey.DisplayName;

        _initializing = true;
        IsAutoStartEnabled = autoStartService.IsEnabled();
        _initializing = false;
    }

    [ObservableProperty]
    public partial string HotKeyDisplayName { get; set; }

    [ObservableProperty]
    public partial bool IsCapturingHotKey { get; set; }

    [ObservableProperty]
    public partial bool HasPendingHotKeyChange { get; set; }

    [ObservableProperty]
    public partial bool IsAutoStartEnabled { get; set; }

    [ObservableProperty]
    public partial string HotKeyStatus { get; set; } =
        "点击快捷键区域，然后按下新的组合键";

    [ObservableProperty]
    public partial string AutoStartStatus { get; set; } =
        "登录 Windows 后自动在后台运行";

    public void BeginHotKeyCapture()
    {
        IsCapturingHotKey = true;
        HotKeyStatus = "请按下包含 Ctrl、Alt、Shift 或 Win 的组合键，Esc 取消";
    }

    public void CancelHotKeyCapture()
    {
        if (!IsCapturingHotKey)
        {
            return;
        }

        IsCapturingHotKey = false;
        HotKeyStatus = "已取消快捷键录入";
    }

    public bool CaptureHotKey(GlobalHotKeyModifiers modifiers, string keyName)
    {
        if (!IsCapturingHotKey)
        {
            return false;
        }

        GlobalHotKeyGestureCreationResult result =
            _hotKeyService.CreateGesture(modifiers, keyName);
        if (result is
            {
                Status: GlobalHotKeyGestureCreationStatus.Created,
                Gesture: GlobalHotKeyGesture gesture,
            })
        {
            _pendingHotKey = gesture;
            HotKeyDisplayName = gesture.DisplayName;
            HasPendingHotKeyChange = gesture != _hotKeyService.ConfiguredGesture;
            IsCapturingHotKey = false;
            HotKeyStatus = HasPendingHotKeyChange
                ? $"已录入 {gesture.DisplayName}，点击应用后生效"
                : $"{gesture.DisplayName} 已是当前快捷键";
            return true;
        }

        HotKeyStatus = result.Status == GlobalHotKeyGestureCreationStatus.MissingModifier
            ? "快捷键必须包含 Ctrl、Alt、Shift 或 Win，请重新按下"
            : "该按键暂不支持注册为全局快捷键，请重新按下";
        return false;
    }

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
            _pendingHotKey,
            CancellationToken.None);
        if (result.Status == GlobalHotKeyRegistrationStatus.Registered)
        {
            HasPendingHotKeyChange = false;
            HotKeyStatus = $"已启用 {_pendingHotKey.DisplayName}";
            return;
        }

        // 平台层在注册冲突时会恢复上一组有效快捷键；设置页也同步回实际配置，
        // 避免界面显示一个并未生效的组合键。
        ResetPendingHotKey(_hotKeyService.ConfiguredGesture);
        HotKeyStatus = result.Status switch
        {
            GlobalHotKeyRegistrationStatus.Conflict =>
                "快捷键已被其他应用占用，原快捷键已恢复",
            GlobalHotKeyRegistrationStatus.Unsupported => "当前平台不支持全局快捷键",
            _ => "快捷键注册失败，原快捷键已恢复",
        };
    }

    [RelayCommand]
    private async Task RestoreDefaultHotKeyAsync()
    {
        ResetPendingHotKey(GlobalHotKeyGesture.Default);
        HasPendingHotKeyChange = true;
        await ApplyHotKeyAsync();
    }

    private void ResetPendingHotKey(GlobalHotKeyGesture gesture)
    {
        _pendingHotKey = gesture;
        HotKeyDisplayName = gesture.DisplayName;
        HasPendingHotKeyChange = false;
        IsCapturingHotKey = false;
    }
}
