using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace SnapBoard.Desktop.Bootstrap;

internal interface IDesktopApplicationLifetime : IDisposable
{
    event EventHandler? ReopenRequested;

    Window? MainWindow { get; set; }

    void UseExplicitShutdown();

    bool TryShutdown();
}

internal sealed class AvaloniaDesktopApplicationLifetime : IDesktopApplicationLifetime
{
    private readonly IActivatableLifetime? _activatableLifetime;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private int _disposed;

    public AvaloniaDesktopApplicationLifetime(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        _activatableLifetime = desktop as IActivatableLifetime;
        if (_activatableLifetime is not null)
        {
            _activatableLifetime.Activated += OnActivated;
        }
    }

    public event EventHandler? ReopenRequested;

    public Window? MainWindow
    {
        get => _desktop.MainWindow;
        set => _desktop.MainWindow = value;
    }

    public void UseExplicitShutdown() =>
        _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

    public bool TryShutdown() => _desktop.TryShutdown();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _activatableLifetime is not null)
        {
            _activatableLifetime.Activated -= OnActivated;
        }
    }

    private void OnActivated(object? sender, ActivatedEventArgs e)
    {
        if (e.Kind == ActivationKind.Reopen)
        {
            ReopenRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
