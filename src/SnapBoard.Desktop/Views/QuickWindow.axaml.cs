using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SnapBoard.Desktop.ViewModels;

namespace SnapBoard.Desktop.Views;

public sealed class QuickPasteRequestedEventArgs(bool plainText) : EventArgs
{
    public bool PlainText { get; } = plainText;
}

public partial class QuickWindow : Window
{
    public QuickWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        KeyDown += OnWindowKeyDown;
    }

    private void OnHistoryItemLoaded(object? sender, RoutedEventArgs e)
        => LoadHistoryItem(sender);

    private void OnHistoryItemDataContextChanged(object? sender, EventArgs e)
        => LoadHistoryItem(sender);

    private void LoadHistoryItem(object? sender)
    {
        if (sender is Control { DataContext: ClipboardHistoryItemViewModel item } &&
            DataContext is MainViewModel viewModel)
        {
            _ = viewModel.LoadThumbnailAsync(item);
            _ = viewModel.LoadSourceApplicationMetadataAsync(item);
        }
    }

    public event EventHandler? DismissRequested;

    public event EventHandler<QuickPasteRequestedEventArgs>? PasteRequested;

    private void OnOpened(object? sender, EventArgs e)
    {
        QuickSearchBox.Focus();
        QuickSearchBox.SelectAll();
    }

    private void OnHistoryDoubleTapped(object? sender, TappedEventArgs e)
    {
        RequestPaste(plainText: false);
        e.Handled = true;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DismissRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter &&
            DataContext is MainViewModel { SelectedItem: not null })
        {
            RequestPaste(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
    }

    private void OnPasteClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        RequestPaste(plainText: false);

    private void OnPlainTextPasteClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        RequestPaste(plainText: true);

    private void RequestPaste(bool plainText)
    {
        if (DataContext is MainViewModel { SelectedItem: not null })
        {
            PasteRequested?.Invoke(this, new QuickPasteRequestedEventArgs(plainText));
        }
    }

}
