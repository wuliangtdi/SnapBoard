namespace SnapBoard.Platform.Windows.Clipboard;

internal interface IClipboardMessageHost : IAsyncDisposable
{
    event Action<uint>? ClipboardUpdated;

    event Action<Exception?>? MessageLoopStopped;

    nint WindowHandle { get; }

    ValueTask StartAsync(CancellationToken cancellationToken);

    ValueTask StopAsync();
}
