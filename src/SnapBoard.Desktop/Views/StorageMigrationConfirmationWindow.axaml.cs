using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SnapBoard.Desktop.Views;

public partial class StorageMigrationConfirmationWindow : Window
{
    public StorageMigrationConfirmationWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => CancelButton.Focus();
    }

    public StorageMigrationConfirmationWindow(string targetDirectory, string details)
        : this()
    {
        TargetDirectoryText.Text = targetDirectory;
        ToolTip.SetTip(TargetDirectoryText, targetDirectory);
        MigrationDetailsText.Text = details;
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
