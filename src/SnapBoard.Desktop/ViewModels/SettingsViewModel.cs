using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SnapBoard.Application.Clipboard;
using SnapBoard.Application.Storage;
using SnapBoard.Application.Sync;
using SnapBoard.Application.Updates;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.Abstractions.Storage;
using SnapBoard.Sync.Contracts;

namespace SnapBoard.Desktop.ViewModels;

public sealed record RetentionPeriodOption(string DisplayName, int Days, bool IsCustom = false);

public sealed record SyncFrequencyOption(string DisplayName, int IntervalSeconds);

public sealed record ApplicationUpdateChannelOption(
    string DisplayName,
    ApplicationUpdateChannel Channel);

public sealed record ApplicationUpdateSourceOption(
    string DisplayName,
    ApplicationUpdateSource Source);

public sealed record ProviderMigrationDeviceViewItem(
    string DeviceId,
    string Status,
    string Watermark);

public enum SettingsSection
{
    General = 0,
    HistoryAndPrivacy = 1,
    Sync = 2,
    Storage = 3,
    Updates = 4,
    Permissions = 5,
}

public enum SyncSettingsPane
{
    Overview = 0,
    Configuration = 1,
    Migration = 2,
}

public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private static readonly RetentionPeriodOption ThirtyDays = new("30 天", 30);
    private static readonly IReadOnlyList<RetentionPeriodOption> AvailableRetentionPeriods =
    [
        new("7 天", 7),
        ThirtyDays,
        new("3 个月", 90),
        new("6 个月", 180),
        new("1 年", 365),
        new("自定义", 30, IsCustom: true),
    ];
    private static readonly SyncFrequencyOption FiveMinutes = new(
        "5 分钟（默认）",
        SyncPollingSettings.DefaultPollIntervalSeconds);
    private static readonly IReadOnlyList<SyncFrequencyOption> AvailableSyncFrequencies =
    [
        new("30 秒", 30),
        new("1 分钟", 60),
        FiveMinutes,
        new("15 分钟", 15 * 60),
        new("30 分钟", 30 * 60),
        new("1 小时", 60 * 60),
    ];
    private static readonly ApplicationUpdateChannelOption StableUpdateChannel = new(
        "稳定版",
        ApplicationUpdateChannel.Stable);
    private static readonly IReadOnlyList<ApplicationUpdateChannelOption> AvailableUpdateChannels =
    [
        StableUpdateChannel,
        new("测试版", ApplicationUpdateChannel.Beta),
    ];
    private static readonly ApplicationUpdateSourceOption AutomaticUpdateSource = new(
        "自动选择",
        ApplicationUpdateSource.Automatic);
    private static readonly ApplicationUpdateSourceOption GitHubUpdateSource = new(
        "GitHub",
        ApplicationUpdateSource.GitHub);
    private readonly IAutoStartService _autoStartService;
    private readonly IAccessibilityPermissionService? _accessibilityPermissionService;
    private readonly IPlatformForegroundWindowStateService? _foregroundWindowStateService;
    private readonly IGlobalHotKeyService _hotKeyService;
    private readonly ITwoSlotGlobalHotKeyService? _twoSlotHotKeyService;
    private readonly IDesktopLocalSettingsService? _localSettings;
    private readonly IHistorySettingsService? _historySettingsService;
    private readonly Func<string, CancellationToken, ValueTask>? _requestStorageMigration;
    private readonly IStorageManagementService? _storageManagementService;
    private readonly IStoragePlatformService? _storagePlatformService;
    private readonly ISyncService? _syncService;
    private readonly ISyncProviderMigrationService? _providerMigrationService;
    private readonly IApplicationUpdateService? _applicationUpdateService;
    private readonly Func<CancellationToken, ValueTask>? _requestUpdateInstall;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SynchronizationContext? _uiContext;
    private GlobalHotKeyGesture _pendingHotKey;
    private GlobalHotKeyGesture? _pendingDoubleHotKey;
    private bool _initializing;
    private bool _historySettingsInitialized;
    private bool _syncSettingsInitialized;
    private bool _storageInitialized;
    private bool _updateInitialized;
    private int _disposed;

    public SettingsViewModel(
        IGlobalHotKeyService hotKeyService,
        IAutoStartService autoStartService,
        IAccessibilityPermissionService? accessibilityPermissionService = null,
        IStorageManagementService? storageManagementService = null,
        IStoragePlatformService? storagePlatformService = null,
        Func<string, CancellationToken, ValueTask>? requestStorageMigration = null,
        ISyncService? syncService = null,
        IHistorySettingsService? historySettingsService = null,
        IApplicationUpdateService? applicationUpdateService = null,
        Func<CancellationToken, ValueTask>? requestUpdateInstall = null,
        IDesktopLocalSettingsService? localSettings = null,
        IPlatformForegroundWindowStateService? foregroundWindowStateService = null)
    {
        _initializing = true;
        _hotKeyService = hotKeyService;
        _twoSlotHotKeyService = hotKeyService as ITwoSlotGlobalHotKeyService;
        _autoStartService = autoStartService;
        _accessibilityPermissionService = accessibilityPermissionService;
        _localSettings = localSettings;
        _foregroundWindowStateService = foregroundWindowStateService;
        _storageManagementService = storageManagementService;
        _storagePlatformService = storagePlatformService;
        _requestStorageMigration = requestStorageMigration;
        _syncService = syncService;
        _providerMigrationService = syncService as ISyncProviderMigrationService;
        _historySettingsService = historySettingsService;
        _applicationUpdateService = applicationUpdateService;
        _requestUpdateInstall = requestUpdateInstall;
        _uiContext = SynchronizationContext.Current;
        _pendingHotKey = hotKeyService.ConfiguredGesture;
        _pendingDoubleHotKey = _twoSlotHotKeyService?.GetConfiguredGesture(
            GlobalHotKeySlot.Double);
        HotKeyDisplayName = _pendingHotKey.DisplayName;
        DoubleHotKeyDisplayName = _pendingDoubleHotKey?.DisplayName ?? "未设置";
        IsDoubleHotKeyAvailable = _twoSlotHotKeyService is not null;
        HasConfiguredDoubleHotKey = _pendingDoubleHotKey is not null;
        DoubleHotKeyStatus = IsDoubleHotKeyAvailable
            ? _pendingDoubleHotKey is null ? "未设置" : "当前已启用"
            : "当前平台尚未实现连按两次快捷键";
        DefaultHotKeyToolTip = $"恢复默认快捷键 {hotKeyService.DefaultGesture.DisplayName}";
        SettingsScopeDescription =
            "本机设置保存在当前用户下；历史、隐私与同步频率随加密空间同步";
        IsPermissionSectionVisible = accessibilityPermissionService is not null;
        IsStorageSectionVisible = storageManagementService is not null &&
            storagePlatformService is not null &&
            requestStorageMigration is not null;
        IsSyncSectionVisible = syncService is not null;
        IsProviderMigrationSectionVisible = _providerMigrationService is not null;
        IsHistorySettingsSectionVisible = historySettingsService is not null;
        IsUpdateSectionVisible = applicationUpdateService is not null;
        IsFullScreenProtectionAvailable = localSettings is not null &&
            foregroundWindowStateService is not null;
        DesktopLocalSettings? desktopSettings = localSettings?.Current;
        SelectedForegroundProtectionScopeIndex = (int)(
            desktopSettings?.ProtectionScope ?? ForegroundProtectionScope.FullScreenOnly);
        IsDisableGlobalHotKeysWhenProtected =
            desktopSettings?.DisableGlobalHotKeysWhenProtected ?? true;
        IsPauseClipboardCaptureWhenProtected =
            desktopSettings?.PauseClipboardCaptureWhenProtected ?? true;
        UpdateSourceOptions = applicationUpdateService?.IsOfficialSourceConfigured == true
            ?
            [
                AutomaticUpdateSource,
                new ApplicationUpdateSourceOption("官方源", ApplicationUpdateSource.Official),
                GitHubUpdateSource,
            ]
            : [AutomaticUpdateSource, GitHubUpdateSource];
        if (_historySettingsService is not null)
        {
            _historySettingsService.Changed += OnHistorySettingsChanged;
        }

        if (_syncService is not null)
        {
            _syncService.StatusChanged += OnSyncStatusChanged;
            _syncService.PollingSettingsChanged += OnSyncPollingSettingsChanged;
            ApplySyncStatus(_syncService.Status);
            ApplySyncPollingSettings(_syncService.PollingSettings);
        }

        if (_providerMigrationService is not null)
        {
            _providerMigrationService.ProviderMigrationChanged += OnProviderMigrationChanged;
            ApplyProviderMigration(_providerMigrationService.ProviderMigration);
        }

        if (_applicationUpdateService is not null)
        {
            _applicationUpdateService.StatusChanged += OnApplicationUpdateStatusChanged;
            ApplyApplicationUpdateSettings(_applicationUpdateService.Settings);
            ApplyApplicationUpdateStatus(_applicationUpdateService.Status);
        }

        RefreshAutoStartState();
        _initializing = false;
        RefreshAccessibilityPermission();
        RefreshForegroundWindowStatus();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralSettingsSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsHistorySettingsSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsSyncSettingsSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsStorageSettingsSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsUpdateSettingsSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsPermissionSettingsSectionSelected))]
    public partial int SelectedSettingsSectionIndex { get; set; } =
        (int)SettingsSection.General;

    public bool IsGeneralSettingsSectionSelected =>
        SelectedSettingsSectionIndex == (int)SettingsSection.General;

    public bool IsHistorySettingsSectionSelected =>
        SelectedSettingsSectionIndex == (int)SettingsSection.HistoryAndPrivacy &&
        IsHistorySettingsSectionVisible;

    public bool IsSyncSettingsSectionSelected =>
        SelectedSettingsSectionIndex == (int)SettingsSection.Sync && IsSyncSectionVisible;

    public bool IsStorageSettingsSectionSelected =>
        SelectedSettingsSectionIndex == (int)SettingsSection.Storage && IsStorageSectionVisible;

    public bool IsUpdateSettingsSectionSelected =>
        SelectedSettingsSectionIndex == (int)SettingsSection.Updates && IsUpdateSectionVisible;

    public bool IsPermissionSettingsSectionSelected =>
        SelectedSettingsSectionIndex == (int)SettingsSection.Permissions &&
        IsPermissionSectionVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSyncOverviewPaneSelected))]
    [NotifyPropertyChangedFor(nameof(IsSyncConfigurationPaneSelected))]
    [NotifyPropertyChangedFor(nameof(IsSyncMigrationPaneSelected))]
    public partial int SelectedSyncSettingsPaneIndex { get; set; } =
        (int)SyncSettingsPane.Overview;

    public bool IsSyncOverviewPaneSelected =>
        SelectedSyncSettingsPaneIndex == (int)SyncSettingsPane.Overview;

    public bool IsSyncConfigurationPaneSelected =>
        SelectedSyncSettingsPaneIndex == (int)SyncSettingsPane.Configuration;

    public bool IsSyncMigrationPaneSelected =>
        SelectedSyncSettingsPaneIndex == (int)SyncSettingsPane.Migration &&
        IsProviderMigrationSectionVisible;

    [ObservableProperty]
    public partial string HotKeyDisplayName { get; set; }

    [ObservableProperty]
    public partial bool IsCapturingHotKey { get; set; }

    public bool IsCapturingPrimaryHotKey => IsCapturingHotKey && !IsCapturingDoubleHotKey;

    [ObservableProperty]
    public partial bool IsCapturingDoubleHotKey { get; set; }

    [ObservableProperty]
    public partial bool HasPendingHotKeyChange { get; set; }

    [ObservableProperty]
    public partial string DoubleHotKeyDisplayName { get; set; } = "未设置";

    [ObservableProperty]
    public partial bool HasPendingDoubleHotKeyChange { get; set; }

    [ObservableProperty]
    public partial bool HasConfiguredDoubleHotKey { get; set; }

    [ObservableProperty]
    public partial bool IsDoubleHotKeyAvailable { get; set; }

    [ObservableProperty]
    public partial string DoubleHotKeyStatus { get; set; } = "未设置";

    [ObservableProperty]
    public partial bool IsFullScreenProtectionAvailable { get; set; }

    [ObservableProperty]
    public partial int SelectedForegroundProtectionScopeIndex { get; set; } =
        (int)ForegroundProtectionScope.FullScreenOnly;

    [ObservableProperty]
    public partial bool IsDisableGlobalHotKeysWhenProtected { get; set; } = true;

    [ObservableProperty]
    public partial bool IsPauseClipboardCaptureWhenProtected { get; set; } = true;

    [ObservableProperty]
    public partial string FullScreenProtectionStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ForegroundWindowStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsAutoStartEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsAutoStartAvailable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPermissionSettingsSectionSelected))]
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStorageSettingsSectionSelected))]
    public partial bool IsStorageSectionVisible { get; set; }

    [ObservableProperty]
    public partial bool IsStorageBusy { get; set; }

    [ObservableProperty]
    public partial bool IsStorageMigrationConfirmationVisible { get; set; }

    [ObservableProperty]
    public partial bool CanConfirmStorageMigration { get; set; }

    [ObservableProperty]
    public partial string CurrentStorageDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DefaultStorageDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedStorageDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StorageUsageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StorageStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StorageTargetDetails { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StorageTargetTitle { get; set; } = "确认迁移本地数据";

    [ObservableProperty]
    public partial string StorageMigrationErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHistorySettingsSectionSelected))]
    public partial bool IsHistorySettingsSectionVisible { get; set; }

    [ObservableProperty]
    public partial bool IsHistorySettingsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsCaptureTextEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsCaptureRichTextEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsCaptureImagesEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsCaptureFilesEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsRetentionEnabled { get; set; }

    [ObservableProperty]
    public partial bool PreserveFavoritesDuringRetention { get; set; } = true;

    [ObservableProperty]
    public partial RetentionPeriodOption SelectedRetentionPeriod { get; set; } = ThirtyDays;

    [ObservableProperty]
    public partial int CustomRetentionDays { get; set; } = 30;

    [ObservableProperty]
    public partial string HistorySettingsStatus { get; set; } =
        "默认记录全部类型，历史不会自动清理";

    public IReadOnlyList<RetentionPeriodOption> RetentionPeriodOptions { get; } =
        AvailableRetentionPeriods;

    public bool IsCustomRetentionPeriod => SelectedRetentionPeriod.IsCustom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSyncSettingsSectionSelected))]
    public partial bool IsSyncSectionVisible { get; set; }

    [ObservableProperty]
    public partial bool IsSyncBusy { get; set; }

    [ObservableProperty]
    public partial bool IsSyncConfigured { get; set; }

    [ObservableProperty]
    public partial bool IsJoinSyncSpace { get; set; }

    [ObservableProperty]
    public partial bool IsSyncAdvancedVisible { get; set; }

    [ObservableProperty]
    public partial bool IsSyncFrequencyBusy { get; set; }

    [ObservableProperty]
    public partial SyncFrequencyOption SelectedSyncFrequency { get; set; } = FiveMinutes;

    [ObservableProperty]
    public partial string SyncFrequencyStatus { get; set; } =
        "未设置时每 5 分钟检查一次远端";

    public IReadOnlyList<SyncFrequencyOption> SyncFrequencyOptions { get; } =
        AvailableSyncFrequencies;

    public bool IsCreateSyncSpace => !IsJoinSyncSpace;

    [ObservableProperty]
    public partial string SyncEndpoint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SyncRemoteRoot { get; set; } =
        $"{SyncProtocol.ProductDirectoryName}/{SyncProtocol.VersionDirectoryName}";

    [ObservableProperty]
    public partial string SyncUsername { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SyncPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SyncCertificatePin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SyncSpaceId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SyncKeyVersion { get; set; } = 1;

    [ObservableProperty]
    public partial string SyncRecoveryCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SyncRecoveryMaterialPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SyncActiveSpaceId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SyncStatus { get; set; } = "尚未配置";

    [ObservableProperty]
    public partial string SyncLastSuccessText { get; set; } = "尚无成功同步记录";

    public bool HasSyncRecoveryMaterial => !string.IsNullOrWhiteSpace(SyncRecoveryMaterialPath);

    public bool HasSyncActiveSpaceId => !string.IsNullOrWhiteSpace(SyncActiveSpaceId);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSyncMigrationPaneSelected))]
    public partial bool IsProviderMigrationSectionVisible { get; set; }

    [ObservableProperty]
    public partial bool IsProviderMigrationBusy { get; set; }

    [ObservableProperty]
    public partial bool IsProviderMigrationInputVisible { get; set; }

    [ObservableProperty]
    public partial bool IsProviderMigrationProgressVisible { get; set; }

    [ObservableProperty]
    public partial bool IsProviderMigrationContinueVisible { get; set; }

    [ObservableProperty]
    public partial bool IsProviderMigrationCancelVisible { get; set; }

    [ObservableProperty]
    public partial string ProviderMigrationCurrentEndpoint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProviderMigrationCurrentRoot { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProviderMigrationTargetEndpoint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProviderMigrationTargetRoot { get; set; } =
        $"{SyncProtocol.ProductDirectoryName}/{SyncProtocol.VersionDirectoryName}";

    [ObservableProperty]
    public partial string ProviderMigrationTargetUsername { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProviderMigrationTargetPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProviderMigrationTargetCertificatePin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ProviderMigrationAllowInsecureLoopback { get; set; }

    [ObservableProperty]
    public partial string ProviderMigrationStatus { get; set; } = "尚未开始迁移";

    [ObservableProperty]
    public partial string ProviderMigrationProgress { get; set; } = "对象 0 / 0，0 B / 0 B";

    [ObservableProperty]
    public partial double ProviderMigrationProgressValue { get; set; }

    [ObservableProperty]
    public partial string ProviderMigrationCredentialActionText { get; set; } = "验证并开始";

    [ObservableProperty]
    public partial IReadOnlyList<ProviderMigrationDeviceViewItem> ProviderMigrationDevices
    {
        get;
        set;
    } = [];

    private Guid? ProviderMigrationPlanId { get; set; }

    public string DefaultHotKeyToolTip { get; }

    public string SettingsScopeDescription { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateSettingsSectionSelected))]
    public partial bool IsUpdateSectionVisible { get; set; }

    [ObservableProperty]
    public partial bool IsUpdateBusy { get; set; }

    [ObservableProperty]
    public partial bool AreAutomaticUpdateChecksEnabled { get; set; } = true;

    [ObservableProperty]
    public partial ApplicationUpdateChannelOption SelectedUpdateChannel { get; set; } =
        StableUpdateChannel;

    [ObservableProperty]
    public partial ApplicationUpdateSourceOption SelectedUpdateSource { get; set; } =
        AutomaticUpdateSource;

    [ObservableProperty]
    public partial string ApplicationUpdateStatusText { get; set; } = "尚未检查更新";

    [ObservableProperty]
    public partial string ApplicationUpdateVersionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ApplicationUpdateProgress { get; set; }

    [ObservableProperty]
    public partial bool IsApplicationUpdateProgressVisible { get; set; }

    [ObservableProperty]
    public partial bool IsApplicationUpdateDownloadVisible { get; set; }

    [ObservableProperty]
    public partial bool IsApplicationUpdateInstallVisible { get; set; }

    public IReadOnlyList<ApplicationUpdateChannelOption> UpdateChannelOptions { get; } =
        AvailableUpdateChannels;

    public IReadOnlyList<ApplicationUpdateSourceOption> UpdateSourceOptions { get; }

    public async Task InitializeApplicationUpdateAsync()
    {
        if (_applicationUpdateService is null || _updateInitialized)
        {
            return;
        }

        _updateInitialized = true;
        IsUpdateBusy = true;
        try
        {
            await _applicationUpdateService.InitializeAsync(_lifetime.Token);
            ApplyApplicationUpdateSettings(_applicationUpdateService.Settings);
            ApplyApplicationUpdateStatus(_applicationUpdateService.Status);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is
            InvalidDataException or InvalidOperationException or IOException)
        {
            _updateInitialized = false;
            ApplicationUpdateStatusText = "无法读取更新设置";
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveApplicationUpdateSettingsAsync()
    {
        if (_applicationUpdateService is null || IsUpdateBusy)
        {
            return;
        }

        IsUpdateBusy = true;
        try
        {
            ApplicationUpdateSettings settings = new(
                AreAutomaticUpdateChecksEnabled,
                SelectedUpdateChannel.Channel,
                SelectedUpdateSource.Source);
            await _applicationUpdateService.UpdateSettingsAsync(settings, _lifetime.Token);
            ApplicationUpdateStatusText = "更新设置已保存到本机";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidDataException or InvalidOperationException or IOException)
        {
            ApplicationUpdateStatusText = "更新设置保存失败，原设置保持不变";
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    [RelayCommand]
    private async Task CheckForApplicationUpdatesAsync()
    {
        if (_applicationUpdateService is null || IsUpdateBusy)
        {
            return;
        }

        IsUpdateBusy = true;
        try
        {
            await _applicationUpdateService.CheckForUpdatesAsync(_lifetime.Token);
            ApplyApplicationUpdateStatus(_applicationUpdateService.Status);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownloadApplicationUpdateAsync()
    {
        if (_applicationUpdateService is null || IsUpdateBusy)
        {
            return;
        }

        IsUpdateBusy = true;
        try
        {
            await _applicationUpdateService.DownloadUpdateAsync(_lifetime.Token);
            ApplyApplicationUpdateStatus(_applicationUpdateService.Status);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallApplicationUpdateAsync()
    {
        if (_applicationUpdateService is null || IsUpdateBusy)
        {
            return;
        }

        IsUpdateBusy = true;
        try
        {
            if (_requestUpdateInstall is not null)
            {
                await _requestUpdateInstall(_lifetime.Token);
            }
            else
            {
                _applicationUpdateService.ScheduleInstallAndRestart();
            }

            ApplicationUpdateStatusText = "正在退出并安装更新";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException)
        {
            ApplyApplicationUpdateStatus(_applicationUpdateService.Status);
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    public async Task InitializeHistorySettingsAsync()
    {
        if (_historySettingsService is null || _historySettingsInitialized)
        {
            return;
        }

        _historySettingsInitialized = true;
        IsHistorySettingsBusy = true;
        try
        {
            await _historySettingsService.InitializeAsync(_lifetime.Token);
            ApplyHistorySettings(_historySettingsService.Current);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is
            InvalidDataException or InvalidOperationException or IOException)
        {
            _historySettingsInitialized = false;
            HistorySettingsStatus = "无法读取历史设置，本次继续使用安全默认值";
        }
        finally
        {
            IsHistorySettingsBusy = false;
        }
    }

    public async Task InitializeSyncAsync()
    {
        if (_syncService is null)
        {
            return;
        }

        ApplySyncStatus(_syncService.Status);
        if (_syncSettingsInitialized)
        {
            return;
        }

        _syncSettingsInitialized = true;
        IsSyncFrequencyBusy = true;
        try
        {
            await _syncService.InitializePollingSettingsAsync(_lifetime.Token);
            ApplySyncPollingSettings(_syncService.PollingSettings);
            if (_providerMigrationService is not null)
            {
                ApplyProviderMigration(await _providerMigrationService
                    .RefreshProviderMigrationAsync(_lifetime.Token));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is
            InvalidDataException or InvalidOperationException or IOException)
        {
            _syncSettingsInitialized = false;
            SyncFrequencyStatus = "无法读取同步频率，本次继续使用默认的 5 分钟";
        }
        finally
        {
            IsSyncFrequencyBusy = false;
        }
    }

    public void SelectSyncRecoveryMaterial(string path)
    {
        if (IsJoinSyncSpace && !string.IsNullOrWhiteSpace(path))
        {
            SyncRecoveryMaterialPath = Path.GetFullPath(path);
        }
    }

    [RelayCommand]
    private void SelectCreateSyncMode() => IsJoinSyncSpace = false;

    [RelayCommand]
    private void SelectJoinSyncMode() => IsJoinSyncSpace = true;

    partial void OnIsJoinSyncSpaceChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCreateSyncSpace));
        SyncStatus = value
            ? "输入现有空间信息和恢复材料以加入"
            : "创建新空间会生成本机主密钥和恢复材料";
    }

    partial void OnSelectedRetentionPeriodChanged(RetentionPeriodOption value)
    {
        OnPropertyChanged(nameof(IsCustomRetentionPeriod));
        SaveHistorySettingsCommand.NotifyCanExecuteChanged();
    }

    partial void OnCustomRetentionDaysChanged(int value) =>
        SaveHistorySettingsCommand.NotifyCanExecuteChanged();

    partial void OnIsHistorySettingsBusyChanged(bool value)
    {
        SaveHistorySettingsCommand.NotifyCanExecuteChanged();
        ApplyRetentionNowCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRetentionEnabledChanged(bool value)
    {
        SaveHistorySettingsCommand.NotifyCanExecuteChanged();
        ApplyRetentionNowCommand.NotifyCanExecuteChanged();
    }

    partial void OnSyncRecoveryMaterialPathChanged(string value) =>
        OnPropertyChanged(nameof(HasSyncRecoveryMaterial));

    partial void OnSyncActiveSpaceIdChanged(string value) =>
        OnPropertyChanged(nameof(HasSyncActiveSpaceId));

    [RelayCommand(CanExecute = nameof(CanSaveHistorySettings))]
    private async Task SaveHistorySettingsAsync()
    {
        if (_historySettingsService is null || IsHistorySettingsBusy)
        {
            return;
        }

        int retentionDays = IsCustomRetentionPeriod
            ? CustomRetentionDays
            : SelectedRetentionPeriod.Days;
        HistoryCaptureSettings capture = new(
            IsCaptureTextEnabled,
            IsCaptureRichTextEnabled,
            IsCaptureImagesEnabled,
            IsCaptureFilesEnabled);
        HistoryRetentionSettings retention = new(
            IsRetentionEnabled,
            retentionDays,
            PreserveFavoritesDuringRetention);
        IsHistorySettingsBusy = true;
        try
        {
            await _historySettingsService.UpdateAsync(capture, retention, _lifetime.Token);
            HistorySettingsStatus = !IsRetentionEnabled
                ? "已保存并同步；历史不会自动清理"
                : PreserveFavoritesDuringRetention
                    ? $"已保存并同步；收藏内容保留，其他记录保留 {retentionDays} 天"
                    : $"已保存并同步；全部记录保留 {retentionDays} 天";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidDataException or InvalidOperationException or IOException)
        {
            HistorySettingsStatus = "历史设置保存失败，原设置保持不变";
        }
        finally
        {
            IsHistorySettingsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyRetentionNow))]
    private async Task ApplyRetentionNowAsync()
    {
        if (_historySettingsService is null || IsHistorySettingsBusy || !IsRetentionEnabled)
        {
            return;
        }

        IsHistorySettingsBusy = true;
        try
        {
            int deleted = await _historySettingsService.ApplyRetentionNowAsync(_lifetime.Token);
            HistorySettingsStatus = deleted == 0
                ? "没有需要清理的过期记录"
                : $"已清理 {deleted} 条过期记录，删除状态将同步到其他设备";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is
            InvalidDataException or InvalidOperationException or IOException)
        {
            HistorySettingsStatus = "清理未完成，将在下次周期维护时重试";
        }
        finally
        {
            IsHistorySettingsBusy = false;
        }
    }

    private bool CanSaveHistorySettings() =>
        IsHistorySettingsSectionVisible &&
        !IsHistorySettingsBusy &&
        (!IsRetentionEnabled ||
         (!IsCustomRetentionPeriod || CustomRetentionDays is >=
             HistoryRetentionSettings.MinimumRetentionDays and <=
             HistoryRetentionSettings.MaximumRetentionDays));

    private bool CanApplyRetentionNow() =>
        IsHistorySettingsSectionVisible && IsRetentionEnabled && !IsHistorySettingsBusy;

    public async Task InitializeStorageAsync()
    {
        if (!IsStorageSectionVisible || _storageInitialized ||
            _storageManagementService is null)
        {
            return;
        }

        _storageInitialized = true;
        IsStorageBusy = true;
        try
        {
            StorageLocationSnapshot snapshot = await _storageManagementService.GetSnapshotAsync(
                CancellationToken.None);
            CurrentStorageDirectory = snapshot.RootDirectory;
            DefaultStorageDirectory = snapshot.DefaultRootDirectory;
            StorageUsageText =
                $"数据库 {FormatBytes(snapshot.Usage.DatabaseBytes)} · " +
                $"Blob 与缩略图 {FormatBytes(snapshot.Usage.BlobBytes)} · " +
                $"恢复材料 {FormatBytes(snapshot.Usage.RecoveryBytes)} · " +
                $"合计 {FormatBytes(snapshot.Usage.TotalBytes)}";
            StorageStatus = snapshot.MigrationPhase switch
            {
                StorageMigrationPhase.None or StorageMigrationPhase.Completed =>
                    "本地数据可用",
                StorageMigrationPhase.RolledBack => "上次迁移已回滚，当前数据保持不变",
                StorageMigrationPhase.Failed => "上次迁移需要处理后才能重试",
                _ => $"迁移状态：{snapshot.MigrationPhase}",
            };
        }
        catch (Exception exception) when (exception is
            IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            StorageStatus = $"无法读取本地存储状态（{exception.GetType().Name}）";
        }
        finally
        {
            IsStorageBusy = false;
        }
    }

    public async Task SelectStorageTargetAsync(string targetDirectory)
    {
        if (_storageManagementService is null || IsStorageBusy)
        {
            return;
        }

        IsStorageBusy = true;
        IsStorageMigrationConfirmationVisible = false;
        CanConfirmStorageMigration = false;
        StorageMigrationErrorMessage = string.Empty;
        try
        {
            StorageLocationValidationResult validation =
                await _storageManagementService.ValidateTargetAsync(
                    targetDirectory,
                    CancellationToken.None);
            SelectedStorageDirectory = validation.CanonicalTargetDirectory;
            IsStorageMigrationConfirmationVisible = true;
            if (!validation.IsValid)
            {
                StorageTargetDetails = GetStorageValidationMessage(validation);
                StorageTargetTitle = "无法使用所选目录";
                StorageStatus = $"未更改位置：{StorageTargetDetails}";
                return;
            }

            StorageTargetTitle = "确认迁移本地数据";
            StorageTargetDetails =
                $"需迁移约 {FormatBytes(validation.RequiredBytes)}；" +
                $"目标可用 {FormatBytes(validation.AvailableBytes)}。";
            StorageStatus = "目标目录验证通过，请确认退出并迁移";
            CanConfirmStorageMigration = true;
        }
        catch (Exception exception) when (exception is
            IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            SelectedStorageDirectory = targetDirectory;
            StorageTargetTitle = "无法使用所选目录";
            StorageTargetDetails = $"目标目录验证失败（{exception.GetType().Name}）";
            StorageStatus = $"未更改位置：{StorageTargetDetails}";
            IsStorageMigrationConfirmationVisible = true;
        }
        finally
        {
            IsStorageBusy = false;
        }
    }

    public void BeginHotKeyCapture()
    {
        IsCapturingDoubleHotKey = false;
        IsCapturingHotKey = true;
        HotKeyStatus = "请按下一个按键或组合键，Esc 取消";
    }

    public void BeginDoubleHotKeyCapture()
    {
        if (_twoSlotHotKeyService is null)
        {
            DoubleHotKeyStatus = "当前平台尚未实现连按两次快捷键";
            return;
        }

        IsCapturingDoubleHotKey = true;
        IsCapturingHotKey = true;
        DoubleHotKeyStatus = "请按下一个按键或组合键，Esc 取消";
    }

    public void CancelHotKeyCapture()
    {
        if (!IsCapturingHotKey)
        {
            return;
        }

        bool wasDouble = IsCapturingDoubleHotKey;
        IsCapturingHotKey = false;
        IsCapturingDoubleHotKey = false;
        if (wasDouble)
        {
            DoubleHotKeyStatus = "已取消快捷键录入";
        }
        else
        {
            HotKeyStatus = "已取消快捷键录入";
        }
    }

    public bool CaptureHotKey(GlobalHotKeyModifiers modifiers, string keyName)
    {
        if (!IsCapturingHotKey)
        {
            return false;
        }

        bool isDouble = IsCapturingDoubleHotKey;
        GlobalHotKeyGestureCreationResult result = isDouble &&
            _twoSlotHotKeyService is not null
            ? _twoSlotHotKeyService.CreateGesture(
                GlobalHotKeySlot.Double,
                modifiers,
                keyName)
            : _hotKeyService.CreateGesture(modifiers, keyName);
        if (result is
            {
                Status: GlobalHotKeyGestureCreationStatus.Created,
                Gesture: GlobalHotKeyGesture gesture,
            })
        {
            IsCapturingHotKey = false;
            IsCapturingDoubleHotKey = false;
            if (isDouble)
            {
                _pendingDoubleHotKey = gesture;
                DoubleHotKeyDisplayName = gesture.DisplayName;
                HasPendingDoubleHotKeyChange = gesture !=
                    _twoSlotHotKeyService?.GetConfiguredGesture(GlobalHotKeySlot.Double);
                DoubleHotKeyStatus = gesture.HasSameBinding(_pendingHotKey)
                    ? "两组快捷键不能相同"
                    : HasPendingDoubleHotKeyChange
                        ? $"已录入 {gesture.DisplayName}，点击应用后生效"
                        : $"{gesture.DisplayName} 已是当前快捷键";
            }
            else
            {
                _pendingHotKey = gesture;
                HotKeyDisplayName = gesture.DisplayName;
                HasPendingHotKeyChange = gesture != _hotKeyService.ConfiguredGesture;
                HotKeyStatus = _pendingDoubleHotKey is GlobalHotKeyGesture pendingDouble &&
                    gesture.HasSameBinding(pendingDouble)
                    ? "两组快捷键不能相同"
                    : HasPendingHotKeyChange
                        ? $"已录入 {gesture.DisplayName}，点击应用后生效"
                        : $"{gesture.DisplayName} 已是当前快捷键";
            }

            return true;
        }

        string status = result.Status switch
        {
            GlobalHotKeyGestureCreationStatus.MissingModifier =>
                $"快捷键必须包含 {_hotKeyService.ModifierDisplayNames}，请重新按下",
            _ => "该按键暂不支持注册为全局快捷键，请重新按下",
        };
        if (isDouble)
        {
            DoubleHotKeyStatus = status;
        }
        else
        {
            HotKeyStatus = status;
        }

        return false;
    }

    partial void OnIsCapturingHotKeyChanged(bool value) =>
        OnPropertyChanged(nameof(IsCapturingPrimaryHotKey));

    partial void OnIsCapturingDoubleHotKeyChanged(bool value) =>
        OnPropertyChanged(nameof(IsCapturingPrimaryHotKey));

    partial void OnSelectedSettingsSectionIndexChanged(int value)
    {
        if (value != (int)SettingsSection.General)
        {
            CancelHotKeyCapture();
        }
    }

    partial void OnIsProviderMigrationSectionVisibleChanged(bool value)
    {
        if (!value && SelectedSyncSettingsPaneIndex == (int)SyncSettingsPane.Migration)
        {
            SelectedSyncSettingsPaneIndex = (int)SyncSettingsPane.Overview;
        }
    }

    partial void OnSelectedForegroundProtectionScopeIndexChanged(int value)
    {
        ForegroundProtectionScope scope = (ForegroundProtectionScope)value;
        if (!_initializing && _localSettings is not null && Enum.IsDefined(scope))
        {
            SaveProtectionSettings(settings => settings with
            {
                ProtectionScope = scope,
            });
        }
    }

    partial void OnIsDisableGlobalHotKeysWhenProtectedChanged(bool value)
    {
        if (!_initializing && _localSettings is not null)
        {
            SaveProtectionSettings(settings => settings with
            {
                DisableGlobalHotKeysWhenProtected = value,
            });
        }
    }

    partial void OnIsPauseClipboardCaptureWhenProtectedChanged(bool value)
    {
        if (!_initializing && _localSettings is not null)
        {
            SaveProtectionSettings(settings => settings with
            {
                PauseClipboardCaptureWhenProtected = value,
            });
        }
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
        if (_pendingDoubleHotKey is GlobalHotKeyGesture pendingDouble &&
            _pendingHotKey.HasSameBinding(pendingDouble))
        {
            HotKeyStatus = "两组快捷键不能相同";
            return;
        }

        GlobalHotKeyRegistrationResult result = await _hotKeyService.RegisterAsync(
            _pendingHotKey,
            CancellationToken.None);
        if (result.Status == GlobalHotKeyRegistrationStatus.Registered)
        {
            HasPendingHotKeyChange = false;
            HotKeyStatus = result.SettingsPersisted
                ? $"已启用 {_pendingHotKey.DisplayName}"
                : "快捷键已生效，但本机设置保存失败";
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
            GlobalHotKeyRegistrationStatus.Duplicate => "两组快捷键不能相同",
            _ => "快捷键注册失败，原快捷键已恢复",
        };
    }

    [RelayCommand]
    private async Task ApplyDoubleHotKeyAsync()
    {
        if (_twoSlotHotKeyService is null || _pendingDoubleHotKey is not GlobalHotKeyGesture gesture)
        {
            DoubleHotKeyStatus = "请先录入一组完整快捷键";
            return;
        }

        if (gesture.HasSameBinding(_pendingHotKey))
        {
            DoubleHotKeyStatus = "两组快捷键不能相同";
            return;
        }

        GlobalHotKeyRegistrationResult result = await _twoSlotHotKeyService.RegisterAsync(
            GlobalHotKeySlot.Double,
            gesture,
            CancellationToken.None);
        if (result.Status == GlobalHotKeyRegistrationStatus.Registered)
        {
            HasPendingDoubleHotKeyChange = false;
            HasConfiguredDoubleHotKey = true;
            DoubleHotKeyStatus = result.SettingsPersisted
                ? $"已启用 {gesture.DisplayName}"
                : "快捷键已生效，但本机设置保存失败";
            return;
        }

        ResetPendingDoubleHotKey(
            _twoSlotHotKeyService.GetConfiguredGesture(GlobalHotKeySlot.Double));
        DoubleHotKeyStatus = result.Status switch
        {
            GlobalHotKeyRegistrationStatus.Conflict =>
                "快捷键已被其他应用占用，原配置已保留",
            GlobalHotKeyRegistrationStatus.Duplicate => "两组快捷键不能相同",
            GlobalHotKeyRegistrationStatus.Unsupported =>
                "当前平台尚未实现连按两次快捷键",
            _ => "快捷键注册失败，原配置已保留",
        };
    }

    [RelayCommand]
    private async Task ClearDoubleHotKeyAsync()
    {
        if (_twoSlotHotKeyService is null)
        {
            DoubleHotKeyStatus = "当前平台尚未实现连按两次快捷键";
            return;
        }

        GlobalHotKeyRegistrationResult result = await _twoSlotHotKeyService.ClearAsync(
            GlobalHotKeySlot.Double,
            CancellationToken.None);
        if (result.Status == GlobalHotKeyRegistrationStatus.Registered)
        {
            ResetPendingDoubleHotKey(null);
            DoubleHotKeyStatus = result.SettingsPersisted
                ? "未设置"
                : "快捷键已清除，但本机设置保存失败";
            return;
        }

        DoubleHotKeyStatus = "清除失败，原配置已保留";
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
            AccessibilityPermissionStatus = "授权尚未生效；启用闪剪后返回此页刷新";
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

    public void RefreshForegroundWindowStatus()
    {
        if (_foregroundWindowStateService is null)
        {
            ForegroundWindowStatus = "当前平台尚未实现前台窗口状态检测";
            return;
        }

        try
        {
            ForegroundWindowStateResult result =
                _foregroundWindowStateService.GetForegroundWindowState();
            ForegroundProtectionScope scope = _localSettings?.Current.ProtectionScope ??
                ForegroundProtectionScope.FullScreenOnly;
            ForegroundWindowStatus = result.State switch
            {
                ForegroundWindowState.Maximized when result.IsProtected(scope) =>
                    "当前检测到窗口最大化，已进入保护范围",
                ForegroundWindowState.Maximized =>
                    "当前检测到窗口最大化，按当前范围不启用保护",
                ForegroundWindowState.FullScreen => "当前检测到全屏窗口，已进入保护范围",
                ForegroundWindowState.Unknown or ForegroundWindowState.Unavailable =>
                    "当前平台暂时无法判断前台窗口状态",
                _ when result.IsSnapBoard => "闪剪自身窗口不触发全屏保护",
                _ => "当前前台窗口未进入保护范围",
            };
        }
        catch
        {
            ForegroundWindowStatus = "当前平台暂时无法判断前台窗口状态";
        }
    }

    [RelayCommand]
    private void OpenStorageDirectory()
    {
        if (_storagePlatformService is not null &&
            !_storagePlatformService.OpenDirectory(CurrentStorageDirectory))
        {
            StorageStatus = "无法打开当前数据目录";
        }
    }

    [RelayCommand]
    private async Task RestoreDefaultStorageAsync()
    {
        if (string.IsNullOrWhiteSpace(DefaultStorageDirectory))
        {
            return;
        }

        await SelectStorageTargetAsync(DefaultStorageDirectory);
    }

    [RelayCommand]
    private void CancelStorageMigration()
    {
        IsStorageMigrationConfirmationVisible = false;
        CanConfirmStorageMigration = false;
        SelectedStorageDirectory = string.Empty;
        StorageTargetDetails = string.Empty;
        StorageTargetTitle = "确认迁移本地数据";
        StorageMigrationErrorMessage = string.Empty;
        StorageStatus = "本地数据可用";
    }

    [RelayCommand]
    private async Task ConfirmStorageMigrationAsync()
    {
        if (!CanConfirmStorageMigration || IsStorageBusy ||
            _requestStorageMigration is null ||
            string.IsNullOrWhiteSpace(SelectedStorageDirectory))
        {
            return;
        }

        IsStorageBusy = true;
        CanConfirmStorageMigration = false;
        IsStorageMigrationConfirmationVisible = false;
        StorageMigrationErrorMessage = string.Empty;
        StorageStatus = "正在停止写入并启动迁移程序";
        try
        {
            await _requestStorageMigration(
                SelectedStorageDirectory,
                CancellationToken.None);
            StorageStatus = "主程序即将退出，迁移完成后会自动重新启动";
        }
        catch (StorageLocationValidationException exception)
        {
            StorageTargetDetails = GetStorageValidationMessage(exception.Validation);
            StorageMigrationErrorMessage = GetFinalStorageValidationMessage(
                exception.Validation);
            StorageStatus = $"迁移未启动：{StorageTargetDetails}";
            IsStorageBusy = false;
        }
        catch (Exception exception) when (exception is
            IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            StorageStatus = $"迁移未启动，数据未切换（{exception.GetType().Name}）";
            StorageMigrationErrorMessage =
                "启动迁移时发生错误。闪剪没有关闭，仍在使用原数据目录；" +
                "请重新选择目标目录后再试。";
            IsStorageBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfigureSync))]
    private async Task ConfigureSyncAsync()
    {
        if (_syncService is null || IsSyncBusy)
        {
            return;
        }

        IsSyncBusy = true;
        byte[] password = [];
        byte[] recoveryCode = [];
        byte[] recoveryEnvelope = [];
        try
        {
            if (!Uri.TryCreate(SyncEndpoint.Trim(), UriKind.Absolute, out Uri? endpoint))
            {
                SyncStatus = "WebDAV 地址格式无效";
                return;
            }

            password = Encoding.UTF8.GetBytes(SyncPassword);
            recoveryCode = Encoding.UTF8.GetBytes(SyncRecoveryCode);
            if (password.Length > 2048)
            {
                SyncStatus = "WebDAV 密码过长";
                return;
            }

            if (recoveryCode.Length is < 16 or > 256)
            {
                SyncStatus = "恢复码需包含 16 到 256 个 UTF-8 字节";
                return;
            }

            string? certificatePin = string.IsNullOrWhiteSpace(SyncCertificatePin)
                ? null
                : SyncCertificatePin.Trim().ToLowerInvariant();
            SyncRemoteConfiguration remote = new(
                endpoint,
                SyncRemoteRoot.Trim(),
                SyncUsername,
                certificatePin);
            SyncSetupRequest request = new(remote);
            SyncSetupResult result;
            if (IsJoinSyncSpace)
            {
                if (!Guid.TryParse(SyncSpaceId, out Guid spaceId) || spaceId == Guid.Empty)
                {
                    SyncStatus = "同步空间 ID 格式无效";
                    return;
                }

                if (SyncKeyVersion is < 1 or > 1_000_000)
                {
                    SyncStatus = "密钥版本无效";
                    return;
                }

                recoveryEnvelope = await ReadRecoveryMaterialAsync(
                    SyncRecoveryMaterialPath,
                    _lifetime.Token);
                result = await _syncService.JoinSpaceAsync(
                    spaceId,
                    SyncKeyVersion,
                    request,
                    password,
                    recoveryEnvelope,
                    recoveryCode,
                    _lifetime.Token);
            }
            else
            {
                result = await _syncService.CreateSpaceAsync(
                    request,
                    password,
                    recoveryCode,
                    _lifetime.Token);
            }

            ApplySyncSetupResult(result);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ArgumentException)
        {
            SyncStatus = "同步配置包含无效字段";
        }
        catch (InvalidDataException)
        {
            SyncStatus = "恢复材料无效或超过大小限制";
        }
        catch (IOException)
        {
            SyncStatus = "无法读取恢复材料或保存本地同步状态";
        }
        catch (UnauthorizedAccessException)
        {
            SyncStatus = "当前用户无权读取恢复材料";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(recoveryCode);
            CryptographicOperations.ZeroMemory(recoveryEnvelope);
            SyncPassword = string.Empty;
            SyncRecoveryCode = string.Empty;
            IsSyncBusy = _syncService.Status.State == SyncServiceState.Synchronizing;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunSync))]
    private async Task SynchronizeNowAsync()
    {
        if (_syncService is null || IsSyncBusy)
        {
            return;
        }

        IsSyncBusy = true;
        try
        {
            SyncStatusSnapshot status = await _syncService.SynchronizeNowAsync(_lifetime.Token);
            ApplySyncStatus(status);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            IsSyncBusy = _syncService.Status.State == SyncServiceState.Synchronizing;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveSyncFrequency))]
    private async Task SaveSyncFrequencyAsync()
    {
        if (_syncService is null || IsSyncFrequencyBusy)
        {
            return;
        }

        IsSyncFrequencyBusy = true;
        try
        {
            SyncPollingSettings settings = new(SelectedSyncFrequency.IntervalSeconds);
            await _syncService.UpdatePollingSettingsAsync(settings, _lifetime.Token);
            ApplySyncPollingSettings(_syncService.PollingSettings);
            SyncFrequencyStatus = IsSyncConfigured
                ? $"已应用 {SelectedSyncFrequency.DisplayName}，并将同步到其他设备"
                : $"已应用 {SelectedSyncFrequency.DisplayName}，创建或加入空间后同步";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is
            InvalidDataException or InvalidOperationException or IOException)
        {
            SyncFrequencyStatus = "同步频率保存失败，继续使用原设置";
        }
        finally
        {
            IsSyncFrequencyBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartOrProvideProviderMigration))]
    private async Task StartOrProvideProviderMigrationAsync()
    {
        if (_providerMigrationService is null || IsProviderMigrationBusy)
        {
            return;
        }

        IsProviderMigrationBusy = true;
        byte[] password = [];
        try
        {
            if (!TryCreateProviderMigrationRequest(
                    out SyncProviderMigrationRequest? request,
                    out string? validationError))
            {
                ProviderMigrationStatus = validationError!;
                return;
            }

            password = Encoding.UTF8.GetBytes(ProviderMigrationTargetPassword);
            if (password.Length > 2048)
            {
                ProviderMigrationStatus = "目标 WebDAV 密码过长";
                return;
            }

            SyncProviderMigrationResult result;
            if (_providerMigrationService.ProviderMigration.State ==
                    SyncProviderMigrationState.TargetCredentialsRequired &&
                ProviderMigrationPlanId is Guid planId)
            {
                result = await _providerMigrationService
                    .ProvideProviderMigrationCredentialsAsync(
                        planId,
                        request!,
                        password,
                        _lifetime.Token);
            }
            else
            {
                result = await _providerMigrationService.StartProviderMigrationAsync(
                    request!,
                    password,
                    _lifetime.Token);
            }

            ApplyProviderMigrationResult(result);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ArgumentException)
        {
            ProviderMigrationStatus = "目标 WebDAV 配置包含无效字段";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            ProviderMigrationTargetPassword = string.Empty;
            IsProviderMigrationBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanContinueProviderMigration))]
    private async Task ContinueProviderMigrationAsync()
    {
        if (_providerMigrationService is null ||
            ProviderMigrationPlanId is not Guid planId || IsProviderMigrationBusy)
        {
            return;
        }

        IsProviderMigrationBusy = true;
        try
        {
            ApplyProviderMigrationResult(await _providerMigrationService
                .ContinueProviderMigrationAsync(planId, _lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            IsProviderMigrationBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelProviderMigration))]
    private async Task CancelProviderMigrationAsync()
    {
        if (_providerMigrationService is null ||
            ProviderMigrationPlanId is not Guid planId || IsProviderMigrationBusy)
        {
            return;
        }

        IsProviderMigrationBusy = true;
        try
        {
            ApplyProviderMigrationResult(await _providerMigrationService
                .CancelOrRollbackProviderMigrationAsync(planId, _lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            IsProviderMigrationBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshProviderMigrationAsync()
    {
        if (_providerMigrationService is null || IsProviderMigrationBusy)
        {
            return;
        }

        IsProviderMigrationBusy = true;
        try
        {
            ApplyProviderMigration(await _providerMigrationService
                .RefreshProviderMigrationAsync(_lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            IsProviderMigrationBusy = false;
        }
    }

    partial void OnIsSyncBusyChanged(bool value)
    {
        ConfigureSyncCommand.NotifyCanExecuteChanged();
        SynchronizeNowCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsProviderMigrationBusyChanged(bool value)
    {
        StartOrProvideProviderMigrationCommand.NotifyCanExecuteChanged();
        ContinueProviderMigrationCommand.NotifyCanExecuteChanged();
        CancelProviderMigrationCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSyncConfiguredChanged(bool value)
    {
        SynchronizeNowCommand.NotifyCanExecuteChanged();
        IsProviderMigrationSectionVisible = value && _providerMigrationService is not null;
        StartOrProvideProviderMigrationCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSyncFrequencyBusyChanged(bool value) =>
        SaveSyncFrequencyCommand.NotifyCanExecuteChanged();

    private bool CanConfigureSync() => IsSyncSectionVisible && !IsSyncBusy;

    private bool CanRunSync() => IsSyncSectionVisible && IsSyncConfigured && !IsSyncBusy;

    private bool CanSaveSyncFrequency() => IsSyncSectionVisible && !IsSyncFrequencyBusy;

    private bool CanStartOrProvideProviderMigration() =>
        IsProviderMigrationSectionVisible && IsProviderMigrationInputVisible &&
        !IsProviderMigrationBusy;

    private bool CanContinueProviderMigration() =>
        IsProviderMigrationSectionVisible && IsProviderMigrationContinueVisible &&
        !IsProviderMigrationBusy;

    private bool CanCancelProviderMigration() =>
        IsProviderMigrationSectionVisible && IsProviderMigrationCancelVisible &&
        !IsProviderMigrationBusy;

    private void OnHistorySettingsChanged(object? sender, HistorySettingsChangedEvent change) =>
        PostToUi(() =>
        {
            ApplyHistorySettings(change.Settings);
            HistorySettingsStatus = change.ChangedKey == HistorySettingKeys.Retention
                ? "保留策略已更新；过期删除会同步到所有设备"
                : "记录类型已更新；仅影响之后复制的内容";
        });

    private void ApplyHistorySettings(HistorySettingsSnapshot settings)
    {
        IsCaptureTextEnabled = settings.Capture.Text;
        IsCaptureRichTextEnabled = settings.Capture.RichText;
        IsCaptureImagesEnabled = settings.Capture.Images;
        IsCaptureFilesEnabled = settings.Capture.Files;
        IsRetentionEnabled = settings.Retention.Enabled;
        PreserveFavoritesDuringRetention = settings.Retention.PreserveFavorites;
        RetentionPeriodOption? preset = AvailableRetentionPeriods.FirstOrDefault(option =>
            !option.IsCustom && option.Days == settings.Retention.RetentionDays);
        SelectedRetentionPeriod = preset ?? AvailableRetentionPeriods[^1];
        CustomRetentionDays = settings.Retention.RetentionDays;
        HistorySettingsStatus = !settings.Retention.Enabled
            ? "历史不会自动清理"
            : settings.Retention.PreserveFavorites
                ? $"收藏内容不清理；其他记录保留 {settings.Retention.RetentionDays} 天"
                : $"全部记录保留 {settings.Retention.RetentionDays} 天";
    }

    private void OnSyncStatusChanged(object? sender, SyncStatusSnapshot status) =>
        PostToUi(() => ApplySyncStatus(status));

    private void OnSyncPollingSettingsChanged(
        object? sender,
        SyncPollingSettingsChangedEvent change) => PostToUi(() =>
        {
            ApplySyncPollingSettings(change.Settings);
            SyncFrequencyStatus = "同步频率已从加密空间更新";
        });

    private void OnProviderMigrationChanged(
        object? sender,
        SyncProviderMigrationSnapshot snapshot) =>
        PostToUi(() => ApplyProviderMigration(snapshot));

    private void OnApplicationUpdateStatusChanged(
        object? sender,
        ApplicationUpdateStatus status) =>
        PostToUi(() => ApplyApplicationUpdateStatus(status));

    private void ApplyApplicationUpdateSettings(ApplicationUpdateSettings settings)
    {
        AreAutomaticUpdateChecksEnabled = settings.AutomaticChecks;
        SelectedUpdateChannel = UpdateChannelOptions.FirstOrDefault(
                option => option.Channel == settings.Channel) ??
            StableUpdateChannel;
        SelectedUpdateSource = UpdateSourceOptions.FirstOrDefault(
                option => option.Source == settings.Source) ??
            AutomaticUpdateSource;
    }

    private void ApplyApplicationUpdateStatus(ApplicationUpdateStatus status)
    {
        ApplicationUpdateVersionText = status.AvailableVersion is null
            ? $"当前版本 {status.CurrentVersion}"
            : $"{status.CurrentVersion} → {status.AvailableVersion}";
        ApplicationUpdateProgress = status.DownloadProgress;
        IsApplicationUpdateProgressVisible =
            status.State == ApplicationUpdateState.Downloading;
        IsApplicationUpdateDownloadVisible =
            status.State == ApplicationUpdateState.UpdateAvailable;
        IsApplicationUpdateInstallVisible =
            status.State == ApplicationUpdateState.ReadyToInstall;
        string source = string.IsNullOrWhiteSpace(status.ActiveSource)
            ? string.Empty
            : $"（{status.ActiveSource}）";
        ApplicationUpdateStatusText = status.State switch
        {
            ApplicationUpdateState.Unavailable => status.Failure switch
            {
                ApplicationUpdateFailure.NotInstalled =>
                    "当前安装方式不支持自动更新；请先使用新版安装包安装一次",
                ApplicationUpdateFailure.UnsupportedPlatform => "当前系统不支持自动更新",
                _ => "自动更新当前不可用",
            },
            ApplicationUpdateState.Idle => "尚未检查更新",
            ApplicationUpdateState.Checking => "正在检查更新",
            ApplicationUpdateState.UpToDate => "已是最新版本",
            ApplicationUpdateState.UpdateAvailable => $"发现可用更新{source}",
            ApplicationUpdateState.Downloading => $"正在下载更新 {status.DownloadProgress}%{source}",
            ApplicationUpdateState.ReadyToInstall => $"更新已下载完成{source}",
            ApplicationUpdateState.Installing => "正在准备安装更新",
            ApplicationUpdateState.Failed => FormatApplicationUpdateFailure(status.Failure),
            _ => "自动更新当前不可用",
        };
    }

    private static string FormatApplicationUpdateFailure(ApplicationUpdateFailure failure) =>
        failure switch
        {
            ApplicationUpdateFailure.OfficialSourceUnavailable => "官方更新源尚未配置",
            ApplicationUpdateFailure.Network => "暂时无法连接更新源",
            ApplicationUpdateFailure.InvalidSignature => "更新签名验证失败，已拒绝下载",
            ApplicationUpdateFailure.SourceConflict => "可信更新源内容不一致，已停止更新",
            ApplicationUpdateFailure.InvalidPackage => "安装包校验失败，已拒绝安装",
            ApplicationUpdateFailure.Busy => "另一个更新操作正在进行",
            ApplicationUpdateFailure.NotInstalled =>
                "当前安装方式不支持自动更新；请先使用新版安装包安装一次",
            ApplicationUpdateFailure.UnsupportedPlatform => "当前系统不支持自动更新",
            _ => "检查更新失败",
        };

    private void ApplyProviderMigrationResult(SyncProviderMigrationResult result)
    {
        ApplyProviderMigration(result.Snapshot);
        if (result.Status is SyncProviderMigrationStatus.Success or
            SyncProviderMigrationStatus.WaitingForDevices)
        {
            return;
        }

        ProviderMigrationStatus = result.Status switch
        {
            SyncProviderMigrationStatus.NotConfigured => "请先配置同步空间",
            SyncProviderMigrationStatus.NotSupported => "当前版本不支持服务迁移",
            SyncProviderMigrationStatus.InvalidState => "当前迁移状态不允许此操作",
            SyncProviderMigrationStatus.CredentialStoreFailed =>
                "目标凭据无法验证或写入系统安全存储",
            SyncProviderMigrationStatus.PermissionDenied => "WebDAV 或安全存储权限不足",
            SyncProviderMigrationStatus.RemoteUnavailable => "WebDAV 暂时不可用，可稍后继续",
            SyncProviderMigrationStatus.RemoteProtocolError =>
                "目标内容或 WebDAV 响应冲突，尚未切换服务",
            SyncProviderMigrationStatus.CryptographicFailure =>
                "迁移标记无法通过空间密钥验证",
            _ => "本机迁移状态无法保存",
        };
    }

    private void ApplyProviderMigration(SyncProviderMigrationSnapshot snapshot)
    {
        ProviderMigrationPlanId = snapshot.PlanId;
        bool terminal = snapshot.State is SyncProviderMigrationState.None or
            SyncProviderMigrationState.Completed or SyncProviderMigrationState.RolledBack or
            SyncProviderMigrationState.Failed;
        bool credentialsRequired = snapshot.State ==
            SyncProviderMigrationState.TargetCredentialsRequired;
        IsProviderMigrationInputVisible = terminal || credentialsRequired;
        IsProviderMigrationProgressVisible = snapshot.State != SyncProviderMigrationState.None;
        IsProviderMigrationContinueVisible = snapshot.State is
            SyncProviderMigrationState.PreflightTarget or
            SyncProviderMigrationState.PreparingDevices or
            SyncProviderMigrationState.WaitingForDeviceAcks or
            SyncProviderMigrationState.Frozen or
            SyncProviderMigrationState.MirroringCiphertext or
            SyncProviderMigrationState.VerifyingTarget or
            SyncProviderMigrationState.Committing or
            SyncProviderMigrationState.WaitingForDeviceCommits;
        IsProviderMigrationCancelVisible = snapshot.State is not
            SyncProviderMigrationState.None and not SyncProviderMigrationState.Completed and not
            SyncProviderMigrationState.RolledBack;
        ProviderMigrationCredentialActionText = credentialsRequired
            ? "验证并参与"
            : "验证并开始";
        bool targetIsCurrent = snapshot.State == SyncProviderMigrationState.Completed;
        string? currentEndpoint = targetIsCurrent
            ? snapshot.TargetEndpoint
            : snapshot.SourceEndpoint;
        string? currentRoot = targetIsCurrent
            ? snapshot.TargetRemoteRoot
            : snapshot.SourceRemoteRoot;
        if (currentEndpoint is not null)
        {
            ProviderMigrationCurrentEndpoint = currentEndpoint;
        }

        if (currentRoot is not null)
        {
            ProviderMigrationCurrentRoot = currentRoot;
        }
        if (credentialsRequired)
        {
            ProviderMigrationTargetEndpoint = snapshot.TargetEndpoint ??
                ProviderMigrationTargetEndpoint;
            ProviderMigrationTargetRoot = snapshot.TargetRemoteRoot ??
                ProviderMigrationTargetRoot;
            ProviderMigrationTargetCertificatePin = snapshot.TargetCertificateSha256Pin ??
                string.Empty;
            ProviderMigrationAllowInsecureLoopback = snapshot.TargetAllowInsecureLoopback;
        }

        ProviderMigrationDevices = snapshot.DeviceStates.Select(static device =>
            new ProviderMigrationDeviceViewItem(
                device.DeviceId.ToString("D"),
                FormatProviderMigrationDeviceState(device.State),
                $"本地 {device.HighestLocalSequence} / 已上传 {device.HighestUploadedSequence}"))
            .ToArray();
        ProviderMigrationProgress =
            $"对象 {snapshot.CompletedObjects} / {snapshot.TotalObjects}，" +
            $"{FormatByteCount(snapshot.CompletedBytes)} / {FormatByteCount(snapshot.TotalBytes)}";
        ProviderMigrationProgressValue = snapshot.TotalObjects == 0
            ? snapshot.State == SyncProviderMigrationState.Completed ? 100d : 0d
            : Math.Clamp(snapshot.CompletedObjects * 100d / snapshot.TotalObjects, 0d, 100d);
        ProviderMigrationStatus = FormatProviderMigrationState(snapshot);
        StartOrProvideProviderMigrationCommand.NotifyCanExecuteChanged();
        ContinueProviderMigrationCommand.NotifyCanExecuteChanged();
        CancelProviderMigrationCommand.NotifyCanExecuteChanged();
    }

    private bool TryCreateProviderMigrationRequest(
        out SyncProviderMigrationRequest? request,
        out string? validationError)
    {
        request = null;
        validationError = null;
        if (!Uri.TryCreate(
                ProviderMigrationTargetEndpoint.Trim(),
                UriKind.Absolute,
                out Uri? endpoint))
        {
            validationError = "目标 WebDAV 地址格式无效";
            return false;
        }

        string? certificatePin = string.IsNullOrWhiteSpace(
            ProviderMigrationTargetCertificatePin)
            ? null
            : ProviderMigrationTargetCertificatePin.Trim().ToLowerInvariant();
        request = new SyncProviderMigrationRequest(new SyncRemoteConfiguration(
            endpoint,
            ProviderMigrationTargetRoot.Trim(),
            ProviderMigrationTargetUsername,
            certificatePin,
            ProviderMigrationAllowInsecureLoopback));
        return true;
    }

    private static string FormatProviderMigrationState(
        SyncProviderMigrationSnapshot snapshot) => snapshot.State switch
        {
            SyncProviderMigrationState.None => "尚未开始迁移",
            SyncProviderMigrationState.Draft or SyncProviderMigrationState.PreflightTarget =>
                "正在验证目标服务",
            SyncProviderMigrationState.PreparingDevices => "正在准备参与设备",
            SyncProviderMigrationState.TargetCredentialsRequired =>
                "旧服务已发布迁移计划，请输入此设备的新服务凭据",
            SyncProviderMigrationState.WaitingForDeviceAcks =>
                "等待所有设备就绪；离线设备会阻止安全迁移",
            SyncProviderMigrationState.Frozen => "所有设备已冻结旧端写入",
            SyncProviderMigrationState.MirroringCiphertext => "正在复制加密对象",
            SyncProviderMigrationState.VerifyingTarget => "正在逐项验证目标密文",
            SyncProviderMigrationState.Committing => "正在切换本机安全凭据",
            SyncProviderMigrationState.WaitingForDeviceCommits => "等待其他设备提交新服务",
            SyncProviderMigrationState.Completed =>
                "新服务已生效；旧服务数据仍保留，未自动删除",
            SyncProviderMigrationState.RollingBack => "正在恢复旧服务",
            SyncProviderMigrationState.RolledBack => "已恢复旧服务，目标凭据已清理",
            _ => "迁移失败；旧服务保持权威，请检查后回滚",
        };

    private static string FormatProviderMigrationDeviceState(
        SyncProviderMigrationDeviceState state) => state switch
        {
            SyncProviderMigrationDeviceState.Pending => "离线或等待响应",
            SyncProviderMigrationDeviceState.TargetCredentialsRequired => "待配置目标凭据",
            SyncProviderMigrationDeviceState.Ready => "已就绪",
            SyncProviderMigrationDeviceState.Committed => "已提交",
            SyncProviderMigrationDeviceState.RolledBack => "已回滚",
            _ => "需要处理",
        };

    private static string FormatByteCount(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double kib = bytes / 1024d;
        return kib < 1024
            ? $"{kib:F1} KiB"
            : $"{kib / 1024d:F1} MiB";
    }

    private void ApplySyncPollingSettings(SyncPollingSettings settings)
    {
        SelectedSyncFrequency = AvailableSyncFrequencies.FirstOrDefault(
                option => option.IntervalSeconds == settings.PollIntervalSeconds) ??
            FiveMinutes;
        SyncFrequencyStatus =
            $"每 {FormatSyncFrequency(settings.PollIntervalSeconds)}检查远端；本地变化仍会立即同步";
    }

    private void ApplySyncStatus(SyncStatusSnapshot status)
    {
        IsSyncBusy = status.State == SyncServiceState.Synchronizing;
        IsSyncConfigured = status.SpaceId is not null &&
            status.State is not SyncServiceState.NotConfigured and not SyncServiceState.Disabled;
        SyncActiveSpaceId = status.SpaceId?.ToString("D") ?? string.Empty;
        SyncStatus = status.State switch
        {
            SyncServiceState.NotConfigured => "尚未配置",
            SyncServiceState.Disabled => "同步已关闭",
            SyncServiceState.Idle when status.LastSuccessfulSync is null => "已连接，等待首次同步",
            SyncServiceState.Idle =>
                $"同步完成：上传 {status.UploadedEvents}，下载 {status.DownloadedEvents}",
            SyncServiceState.Synchronizing when status.DiagnosticCode == "configuring" =>
                "正在验证远端并保存加密配置",
            SyncServiceState.Synchronizing => "正在同步",
            SyncServiceState.Paused => "本地数据迁移期间已暂停同步",
            SyncServiceState.AuthenticationRequired => "WebDAV 认证失败，请重新配置凭据",
            SyncServiceState.PermissionDenied => "WebDAV 或本机密钥存储权限不足",
            SyncServiceState.KeyUnavailable => "本机同步主密钥不可用",
            _ => "同步失败，本地历史未被远端错误覆盖",
        };
        SyncLastSuccessText = status.LastSuccessfulSync is null
            ? "尚无成功同步记录"
            : $"上次成功：{status.LastSuccessfulSync.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
    }

    private void ApplySyncSetupResult(SyncSetupResult result)
    {
        if (result.Status == SyncSetupStatus.Success)
        {
            IsSyncConfigured = true;
            SyncActiveSpaceId = result.SpaceId?.ToString("D") ?? string.Empty;
            SyncSpaceId = SyncActiveSpaceId;
            SyncRecoveryMaterialPath = result.RecoveryMaterialPath ??
                SyncRecoveryMaterialPath;
            SyncStatus = "同步空间已配置，正在执行首次同步";
            return;
        }

        SyncStatus = result.Status switch
        {
            SyncSetupStatus.InvalidConfiguration => "同步配置无效",
            SyncSetupStatus.CredentialStoreFailed => "WebDAV 凭据无法安全保存",
            SyncSetupStatus.KeyStoreFailed => "同步主密钥无法安全保存",
            SyncSetupStatus.RecoveryMaterialFailed => "恢复材料无法保存",
            SyncSetupStatus.AuthenticationFailed => "WebDAV 用户名或密码错误",
            SyncSetupStatus.PermissionDenied => "WebDAV 或本机密钥存储权限不足",
            SyncSetupStatus.RemoteUnavailable => "WebDAV 当前不可用",
            SyncSetupStatus.RemoteProtocolError => "WebDAV 返回了不兼容的协议数据",
            SyncSetupStatus.CryptographicFailure => "恢复码错误或恢复材料已损坏",
            _ => "本地同步状态无法保存",
        };
    }

    private static async Task<byte[]> ReadRecoveryMaterialAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException("Recovery material path is required.");
        }

        await using FileStream stream = new(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > SyncProtocol.MaximumEncryptedEnvelopeBytes)
        {
            throw new InvalidDataException("Recovery material size is invalid.");
        }

        byte[] content = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        try
        {
            await stream.ReadExactlyAsync(content, cancellationToken);
            if (stream.ReadByte() >= 0)
            {
                throw new InvalidDataException("Recovery material changed while being read.");
            }

            return content;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(content);
            throw;
        }
    }

    private void PostToUi(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (ReferenceEquals(SynchronizationContext.Current, _uiContext) ||
            (_uiContext is null && Dispatcher.UIThread.CheckAccess()))
        {
            action();
            return;
        }

        if (_uiContext is not null)
        {
            _uiContext.Post(
                static state =>
                {
                    var (owner, callback) = ((SettingsViewModel, Action))state!;
                    if (Volatile.Read(ref owner._disposed) == 0)
                    {
                        callback();
                    }
                },
                (this, action));
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                action();
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_syncService is not null)
        {
            _syncService.StatusChanged -= OnSyncStatusChanged;
            _syncService.PollingSettingsChanged -= OnSyncPollingSettingsChanged;
        }

        if (_providerMigrationService is not null)
        {
            _providerMigrationService.ProviderMigrationChanged -= OnProviderMigrationChanged;
        }

        if (_historySettingsService is not null)
        {
            _historySettingsService.Changed -= OnHistorySettingsChanged;
        }

        if (_applicationUpdateService is not null)
        {
            _applicationUpdateService.StatusChanged -= OnApplicationUpdateStatusChanged;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
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
        IsCapturingDoubleHotKey = false;
    }

    private void ResetPendingDoubleHotKey(GlobalHotKeyGesture? gesture)
    {
        _pendingDoubleHotKey = gesture;
        DoubleHotKeyDisplayName = gesture?.DisplayName ?? "未设置";
        HasPendingDoubleHotKeyChange = false;
        HasConfiguredDoubleHotKey = gesture is not null;
        IsCapturingHotKey = false;
        IsCapturingDoubleHotKey = false;
    }

    private void SaveProtectionSettings(
        Func<DesktopLocalSettings, DesktopLocalSettings> update)
    {
        DesktopLocalSettingsUpdateResult result = _localSettings!.Update(update);
        FullScreenProtectionStatus = result.Persisted
            ? "本机设置已保存并立即生效"
            : "当前会话已生效，但本机设置保存失败";
        RefreshForegroundWindowStatus();
    }

    private static string GetStorageValidationMessage(
        StorageLocationValidationResult validation) => validation.Error switch
        {
            StorageLocationValidationError.PathTooBroad => "不能把磁盘根目录或用户主目录作为数据目录",
            StorageLocationValidationError.SameAsCurrent => "所选目录已经是当前数据目录",
            StorageLocationValidationError.NestedWithCurrent => "新旧数据目录不能互为父目录或子目录",
            StorageLocationValidationError.ReservedLocation => "不能使用安装目录、临时目录或云盘目录",
            StorageLocationValidationError.UnsupportedVolume => "首个版本只支持本地固定磁盘",
            StorageLocationValidationError.ReparsePoint => "目录路径包含符号链接、联接或重解析点",
            StorageLocationValidationError.InsufficientSpace => "目标磁盘空间不足，无法保留校验和回滚余量",
            StorageLocationValidationError.InsecurePermissions when
                validation.ErrorCode == "acl-hardening-failed" =>
                "无法收紧所选目录权限；请选择当前用户拥有的空目录",
            StorageLocationValidationError.InsecurePermissions =>
                "目录权限仍可能向其他本机用户暴露数据",
            StorageLocationValidationError.ExistingStorage when
                validation.ErrorCode == "target-not-empty" =>
                "所选目录中已有文件或子目录；为避免覆盖现有内容，请选择一个空目录",
            StorageLocationValidationError.ExistingStorage =>
                "所选目录属于另一份闪剪数据，现有内容不会被覆盖",
            StorageLocationValidationError.ProbeFailed => "目标目录无法可靠写入、刷盘或重命名",
            StorageLocationValidationError.Unavailable => "目标目录当前不可用",
            _ => "目标目录无效",
        };

    private static string GetFinalStorageValidationMessage(
        StorageLocationValidationResult validation)
    {
        if (validation.ErrorCode == "target-not-empty")
        {
            return "目标目录在确认期间出现了文件或子目录。为保护已有内容，迁移没有开始。" +
                "请清空该目录，或重新选择一个空目录。";
        }

        return $"目标目录在最终检查时不再满足要求：{GetStorageValidationMessage(validation)}。" +
            "迁移没有开始，请重新选择目标目录。";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private static string FormatSyncFrequency(int intervalSeconds) => intervalSeconds switch
    {
        < 60 => $"{intervalSeconds} 秒",
        3600 => "1 小时",
        _ => $"{intervalSeconds / 60} 分钟",
    };
}
