using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
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
            result |= GlobalHotKeyModifiers.Windows;
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
