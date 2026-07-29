using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Desktop.Views;

public partial class SettingsWindow : Window
{
    private readonly HashSet<Key> _pressedModifierKeys = [];
    private GlobalHotKeyModifiers _capturedModifierFlags;
    private string? _modifierMainKeyName;

    public SettingsWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
        Activated += OnWindowActivated;
        Deactivated += OnWindowDeactivated;
    }

    private void OnHotKeyCaptureClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            ResetModifierCapture();
            viewModel.BeginHotKeyCapture();
            HotKeyCaptureButton.Focus();
        }
    }

    private void OnDoubleHotKeyCaptureClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            ResetModifierCapture();
            viewModel.BeginDoubleHotKeyCapture();
            DoubleHotKeyCaptureButton.Focus();
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel { IsCapturingHotKey: true } viewModel)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            ResetModifierCapture();
            viewModel.CancelHotKeyCapture();
            e.Handled = true;
            return;
        }

        if (TryMapModifierKey(
                e.Key,
                out GlobalHotKeyModifiers modifier,
                out string modifierKeyName))
        {
            if (_pressedModifierKeys.Add(e.Key))
            {
                _capturedModifierFlags |= modifier;
                _modifierMainKeyName = modifierKeyName;
            }

            e.Handled = true;
            return;
        }

        ResetModifierCapture();
        viewModel.CaptureHotKey(MapModifiers(e.KeyModifiers), e.Key.ToString());
        e.Handled = true;
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel { IsCapturingHotKey: true } viewModel ||
            !TryMapModifierKey(
                e.Key,
                out GlobalHotKeyModifiers releasedModifier,
                out _))
        {
            return;
        }

        e.Handled = true;
        _pressedModifierKeys.Remove(e.Key);
        GlobalHotKeyModifiers remainingModifiers =
            MapModifiers(e.KeyModifiers) & ~releasedModifier;
        if (_pressedModifierKeys.Count != 0 ||
            remainingModifiers != GlobalHotKeyModifiers.None)
        {
            return;
        }

        GlobalHotKeyModifiers modifiers = _capturedModifierFlags;
        string? modifierKeyName = _modifierMainKeyName;
        ResetModifierCapture();
        if (modifierKeyName is not null)
        {
            viewModel.CaptureHotKey(modifiers, modifierKeyName);
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            ResetModifierCapture();
            viewModel.CancelHotKeyCapture();
        }
    }

    private async void OnWindowActivated(object? sender, EventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.RefreshAccessibilityPermission();
            viewModel.RefreshForegroundWindowStatus();
            await viewModel.InitializeHistorySettingsAsync();
            await viewModel.InitializeStorageAsync();
            await viewModel.InitializeSyncAsync();
            await viewModel.InitializeApplicationUpdateAsync();
        }
    }

    private void OnSettingsNavigationSelectionChanged(
        object? sender,
        SelectionChangedEventArgs e) => QueueContentScrollReset();

    private void OnSyncSettingsPaneSelectionChanged(
        object? sender,
        SelectionChangedEventArgs e) => QueueContentScrollReset();

    private void QueueContentScrollReset()
    {
        Dispatcher.UIThread.Post(
            () => SettingsContentScrollViewer.Offset = default,
            DispatcherPriority.Background);
    }

    private async void OnCopySyncSpaceIdClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel ||
            string.IsNullOrWhiteSpace(viewModel.SyncActiveSpaceId) ||
            TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(viewModel.SyncActiveSpaceId);
        viewModel.SyncStatus = "空间 ID 已复制";
    }

    private void OnOpenSyncRecoveryFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel ||
            string.IsNullOrWhiteSpace(viewModel.SyncRecoveryMaterialPath))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(viewModel.SyncRecoveryMaterialPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            viewModel.SyncStatus = "恢复材料所在目录不存在";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            viewModel.SyncStatus = "无法打开恢复材料所在目录";
        }
    }

    private async void OnChooseSyncRecoveryMaterialClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "选择闪剪同步恢复材料",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("闪剪恢复材料")
                    {
                        Patterns = ["*.recovery"],
                    },
                ],
            });
        if (files.Count == 1)
        {
            viewModel.SelectSyncRecoveryMaterial(files[0].Path.LocalPath);
        }
    }

    private async void OnContinueProviderMigrationClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel ||
            !viewModel.ContinueProviderMigrationCommand.CanExecute(null))
        {
            return;
        }

        ProviderMigrationConfirmationWindow confirmation = new(
            ProviderMigrationConfirmationAction.FreezeAndContinue);
        if (await confirmation.ShowDialog<bool>(this))
        {
            await viewModel.ContinueProviderMigrationCommand.ExecuteAsync(null);
        }
    }

    private async void OnRollbackProviderMigrationClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel ||
            !viewModel.CancelProviderMigrationCommand.CanExecute(null))
        {
            return;
        }

        ProviderMigrationConfirmationWindow confirmation = new(
            ProviderMigrationConfirmationAction.Rollback);
        if (await confirmation.ShowDialog<bool>(this))
        {
            await viewModel.CancelProviderMigrationCommand.ExecuteAsync(null);
        }
    }

    private async void OnChooseStorageFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "选择闪剪本地数据目录",
                AllowMultiple = false,
            });
        if (folders.Count == 1)
        {
            await SelectAndConfirmStorageTargetAsync(
                viewModel,
                folders[0].Path.LocalPath);
        }
    }

    private async void OnRestoreDefaultStorageClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel ||
            string.IsNullOrWhiteSpace(viewModel.DefaultStorageDirectory))
        {
            return;
        }

        await SelectAndConfirmStorageTargetAsync(
            viewModel,
            viewModel.DefaultStorageDirectory);
    }

    private async Task SelectAndConfirmStorageTargetAsync(
        SettingsViewModel viewModel,
        string targetDirectory)
    {
        await viewModel.SelectStorageTargetAsync(targetDirectory);
        if (!viewModel.CanConfirmStorageMigration)
        {
            return;
        }

        StorageMigrationConfirmationWindow confirmation = new(
            viewModel.SelectedStorageDirectory,
            viewModel.StorageTargetDetails);
        bool confirmed = await confirmation.ShowDialog<bool>(this);
        if (!confirmed)
        {
            viewModel.CancelStorageMigrationCommand.Execute(null);
            return;
        }

        await viewModel.ConfirmStorageMigrationCommand.ExecuteAsync(null);
        if (!string.IsNullOrWhiteSpace(viewModel.StorageMigrationErrorMessage))
        {
            StorageMigrationErrorWindow error = new(
                viewModel.SelectedStorageDirectory,
                viewModel.StorageMigrationErrorMessage);
            await error.ShowDialog(this);
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.CancelHotKeyCapture();
        }

        Close();
    }

    private static GlobalHotKeyModifiers MapModifiers(KeyModifiers modifiers)
    {
        GlobalHotKeyModifiers result = GlobalHotKeyModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            result |= GlobalHotKeyModifiers.Control;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            result |= GlobalHotKeyModifiers.Alt;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            result |= GlobalHotKeyModifiers.Shift;
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            result |= GlobalHotKeyModifiers.Meta;
        }

        return result;
    }

    private void ResetModifierCapture()
    {
        _pressedModifierKeys.Clear();
        _capturedModifierFlags = GlobalHotKeyModifiers.None;
        _modifierMainKeyName = null;
    }

    private static bool TryMapModifierKey(
        Key key,
        out GlobalHotKeyModifiers modifier,
        out string keyName)
    {
        (modifier, keyName) = key switch
        {
            Key.LeftCtrl => (GlobalHotKeyModifiers.Control, "LeftCtrl"),
            Key.RightCtrl => (GlobalHotKeyModifiers.Control, "RightCtrl"),
            Key.LeftAlt => (GlobalHotKeyModifiers.Alt, "LeftAlt"),
            Key.RightAlt => (GlobalHotKeyModifiers.Alt, "RightAlt"),
            Key.System => (GlobalHotKeyModifiers.Alt, "System"),
            Key.LeftShift => (GlobalHotKeyModifiers.Shift, "LeftShift"),
            Key.RightShift => (GlobalHotKeyModifiers.Shift, "RightShift"),
            Key.LWin => (GlobalHotKeyModifiers.Windows, "LWin"),
            Key.RWin => (GlobalHotKeyModifiers.Windows, "RWin"),
            _ => (GlobalHotKeyModifiers.None, string.Empty),
        };
        return modifier != GlobalHotKeyModifiers.None;
    }
}
