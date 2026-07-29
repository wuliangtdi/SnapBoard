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
    public SettingsWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Activated += OnWindowActivated;
        Deactivated += OnWindowDeactivated;
    }

    private void OnHotKeyCaptureClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.BeginHotKeyCapture();
            HotKeyCaptureButton.Focus();
        }
    }

    private void OnDoubleHotKeyCaptureClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
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
            viewModel.CancelHotKeyCapture();
            e.Handled = true;
            return;
        }

        if (IsModifierKey(e.Key))
        {
            // 单独按下修饰键时保持录入状态，等待用户继续按主键。
            e.Handled = true;
            return;
        }

        viewModel.CaptureHotKey(MapModifiers(e.KeyModifiers), e.Key.ToString());
        e.Handled = true;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
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

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or
        Key.RightCtrl or
        Key.LeftAlt or
        Key.RightAlt or
        Key.LeftShift or
        Key.RightShift or
        Key.LWin or
        Key.RWin or
        Key.System or
        Key.None;
}
