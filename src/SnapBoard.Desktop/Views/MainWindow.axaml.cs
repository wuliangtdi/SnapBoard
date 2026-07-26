using Avalonia.Controls;
using Avalonia.Input;
using SnapBoard.Desktop.ViewModels;

namespace SnapBoard.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        KeyDown += OnWindowKeyDown;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SelectFilterCommand.Execute("All");
        }

        SearchBox.Focus();
    }

    private void OnHistoryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.PasteCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.K)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (DataContext is not MainViewModel viewModel || !HistoryList.IsKeyboardFocusWithin)
        {
            return;
        }

        // 高频操作只在历史列表持有焦点时拦截，避免覆盖搜索框的常规编辑行为。
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.C)
        {
            viewModel.CopyCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.V)
        {
            viewModel.PasteCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            viewModel.DeleteCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            viewModel.OpenSelectedItemCommand.Execute(null);
            e.Handled = true;
        }
    }
}
