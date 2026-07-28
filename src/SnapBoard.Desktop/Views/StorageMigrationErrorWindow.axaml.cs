using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SnapBoard.Desktop.Views;

public partial class StorageMigrationErrorWindow : Window
{
    public StorageMigrationErrorWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => DismissButton.Focus();
    }

    public StorageMigrationErrorWindow(string targetDirectory, string message)
        : this()
    {
        TargetDirectoryText.Text = targetDirectory;
        ToolTip.SetTip(TargetDirectoryText, targetDirectory);
        ErrorMessageText.Text = message;
    }

    private void OnDismissClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close();
    }
}
