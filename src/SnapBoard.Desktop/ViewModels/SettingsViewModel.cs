using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IAutoStartService _autoStartService;
    private readonly IAccessibilityPermissionService? _accessibilityPermissionService;
    private readonly IGlobalHotKeyService _hotKeyService;
    private GlobalHotKeyGesture _pendingHotKey;
    private bool _initializing;

    public SettingsViewModel(
        IGlobalHotKeyService hotKeyService,
        IAutoStartService autoStartService,
        IAccessibilityPermissionService? accessibilityPermissionService = null)
    {
        _hotKeyService = hotKeyService;
        _autoStartService = autoStartService;
        _accessibilityPermissionService = accessibilityPermissionService;
        _pendingHotKey = hotKeyService.ConfiguredGesture;
        HotKeyDisplayName = _pendingHotKey.DisplayName;
        DefaultHotKeyToolTip = $"恢复默认快捷键 {hotKeyService.DefaultGesture.DisplayName}";
        SettingsScopeDescription = "设置会保存在当前用户下";
        IsPermissionSectionVisible = accessibilityPermissionService is not null;

        _initializing = true;
        RefreshAutoStartState();
        _initializing = false;
        RefreshAccessibilityPermission();
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
    public partial bool IsAutoStartAvailable { get; set; }

    [ObservableProperty]
    public partial bool IsPermissionSectionVisible { get; set; }

    [ObservableProperty]
    public partial bool IsRestrictedMode { get; set; }

    [ObservableProperty]
    public partial string HotKeyStatus { get; set; } =
        "点击快捷键区域，然后按下新的组合键";

    [ObservableProperty]
    public partial string AutoStartStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccessibilityPermissionStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApplicationIdentityStatus { get; set; } = string.Empty;

    public string DefaultHotKeyToolTip { get; }

    public string SettingsScopeDescription { get; }

    public void BeginHotKeyCapture()
    {
        IsCapturingHotKey = true;
        HotKeyStatus = $"请按下包含 {_hotKeyService.ModifierDisplayNames} 的组合键，Esc 取消";
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
            ? $"快捷键必须包含 {_hotKeyService.ModifierDisplayNames}，请重新按下"
            : "该按键暂不支持注册为全局快捷键，请重新按下";
        return false;
    }

    partial void OnIsAutoStartEnabledChanged(bool value)
    {
        if (_initializing)
        {
            return;
        }

        if (!IsAutoStartAvailable)
        {
            _initializing = true;
            IsAutoStartEnabled = false;
            _initializing = false;
            return;
        }

        AutoStartUpdateResult result = _autoStartService.SetEnabled(value);
        AutoStartStatus = result.Status switch
        {
            AutoStartUpdateStatus.Updated => value ? "已启用登录启动" : "已关闭登录启动",
            AutoStartUpdateStatus.UserApprovalRequired =>
                "已注册，需在系统设置的登录项中允许",
            AutoStartUpdateStatus.Unsupported => "正式 App Bundle 才支持登录启动",
            _ => "登录启动设置失败",
        };
        if (result.Status != AutoStartUpdateStatus.Updated)
        {
            _initializing = true;
            RefreshAutoStartState();
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
        ResetPendingHotKey(_hotKeyService.DefaultGesture);
        HasPendingHotKeyChange = true;
        await ApplyHotKeyAsync();
    }

    [RelayCommand]
    private void RequestAccessibilityPermission()
    {
        if (_accessibilityPermissionService is null)
        {
            return;
        }

        AccessibilityPermissionActionResult result =
            _accessibilityPermissionService.RequestAccess();
        ApplyAccessibilityState(result.State);
        if (result.State.IsRestrictedMode && result.ActionSucceeded)
        {
            AccessibilityPermissionStatus = "授权尚未生效；启用 SnapBoard 后返回此页刷新";
        }
    }

    [RelayCommand]
    private void OpenAccessibilitySettings()
    {
        if (_accessibilityPermissionService is not null &&
            !_accessibilityPermissionService.OpenSystemSettings())
        {
            AccessibilityPermissionStatus = "无法打开系统设置，请手动进入隐私与安全性";
        }
    }

    [RelayCommand]
    public void RefreshAccessibilityPermission()
    {
        if (_accessibilityPermissionService is not null)
        {
            ApplyAccessibilityState(_accessibilityPermissionService.GetState());
        }
    }

    private void RefreshAutoStartState()
    {
        AutoStartAvailability availability = _autoStartService.Availability;
        IsAutoStartAvailable = availability is
            AutoStartAvailability.Available or AutoStartAvailability.RequiresUserApproval;
        IsAutoStartEnabled = IsAutoStartAvailable && _autoStartService.IsEnabled();
        AutoStartStatus = availability switch
        {
            AutoStartAvailability.RequiresAppBundle =>
                "开发裸程序不支持；正式 App Bundle 可启用",
            AutoStartAvailability.RequiresUserApproval =>
                "需在系统设置的登录项中允许",
            AutoStartAvailability.Unsupported => "当前平台不支持登录启动",
            _ => IsAutoStartEnabled ? "登录后自动在后台运行" : "登录启动未开启",
        };
    }

    private void ApplyAccessibilityState(AccessibilityPermissionState state)
    {
        IsRestrictedMode = state.IsRestrictedMode;
        AccessibilityPermissionStatus = state.IsRestrictedMode
            ? "受限模式：仍会写入剪贴板，请手动粘贴"
            : "已授权：可恢复目标应用并自动粘贴";
        ApplicationIdentityStatus = state.IdentityKind switch
        {
            ApplicationIdentityKind.AppBundle =>
                $"App Bundle 身份：{state.BundleIdentifier}",
            ApplicationIdentityKind.DevelopmentExecutable =>
                "当前为开发裸程序；正式授权以稳定 App Bundle 身份为准",
            _ => "无法识别当前应用身份",
        };
    }

    private void ResetPendingHotKey(GlobalHotKeyGesture gesture)
    {
        _pendingHotKey = gesture;
        HotKeyDisplayName = gesture.DisplayName;
        HasPendingHotKeyChange = false;
        IsCapturingHotKey = false;
    }
}
