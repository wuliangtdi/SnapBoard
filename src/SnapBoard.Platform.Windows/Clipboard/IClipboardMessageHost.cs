namespace SnapBoard.Platform.Windows.Clipboard;

internal readonly record struct ClipboardUpdateObservation(
    uint SequenceNumber,
    int? ClipboardOwnerProcessId,
    int? ForegroundProcessId);

internal interface IClipboardMessageHost : IAsyncDisposable
{
    event Action<ClipboardUpdateObservation>? ClipboardUpdated;

    event Action<Exception?>? MessageLoopStopped;

    nint WindowHandle { get; }

    ValueTask StartAsync(CancellationToken cancellationToken);

    ValueTask StopAsync();
}
