using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Material.Icons;

namespace SnapBoard.Desktop.Views;

public enum ProviderMigrationConfirmationAction
{
    FreezeAndContinue,
    Rollback,
}

public partial class ProviderMigrationConfirmationWindow : Window
{
    public ProviderMigrationConfirmationWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => CancelButton.Focus();
    }

    public ProviderMigrationConfirmationWindow(ProviderMigrationConfirmationAction action)
        : this()
    {
        if (action == ProviderMigrationConfirmationAction.Rollback)
        {
            HeaderText.Text = "回滚服务迁移";
            SubtitleText.Text = "恢复旧服务为权威端";
            MessageText.Text =
                "SnapBoard 将停止本次迁移，恢复旧服务凭据，并清理本机暂存的新服务凭据。";
            SafetyText.Text =
                "不会删除旧服务或新服务上的远端对象；旧服务数据仍保持完整。";
            ConfirmText.Text = "确认回滚";
            HeaderIcon.Kind = MaterialIconKind.Restore;
            ConfirmIcon.Kind = MaterialIconKind.Restore;
            return;
        }

        HeaderText.Text = "冻结并继续迁移";
        SubtitleText.Text = "等待全部设备后复制密文";
        MessageText.Text =
            "SnapBoard 会先确认全部已登记设备就绪，然后冻结旧服务上传、复制并逐项校验密文，最后切换凭据。";
        SafetyText.Text =
            "任何离线设备都会阻止切换；失败时旧服务继续保持权威，旧服务数据不会自动删除。";
        ConfirmText.Text = "冻结并继续";
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);

    private void OnConfirmClicked(object? sender, RoutedEventArgs e) => Close(true);

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close(false);
    }
}
