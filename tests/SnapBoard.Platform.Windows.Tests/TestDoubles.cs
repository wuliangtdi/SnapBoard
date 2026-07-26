using SnapBoard.Platform.Windows.Clipboard;

namespace SnapBoard.Platform.Windows.Tests;

internal sealed class FakeClipboardMessageHost : IClipboardMessageHost
{
    public event Action<uint>? ClipboardUpdated;

    public event Action<Exception?>? MessageLoopStopped;

    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public nint WindowHandle => 123;

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        Started.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync()
    {
        StopCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => StopAsync();

    public void RaiseClipboardUpdated(uint sequenceNumber) =>
        ClipboardUpdated?.Invoke(sequenceNumber);

    public void FailMessageLoop(Exception error) => MessageLoopStopped?.Invoke(error);
}

internal sealed class CancelableStartupClipboardMessageHost : IClipboardMessageHost
{
    public event Action<uint>? ClipboardUpdated
    {
        add { }
        remove { }
    }

    public event Action<Exception?>? MessageLoopStopped
    {
        add { }
        remove { }
    }

    public TaskCompletionSource StartEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int StopCount { get; private set; }

    public nint WindowHandle => 0;

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        StartEntered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    public ValueTask StopAsync()
    {
        StopCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => StopAsync();
}

internal sealed class FakeWindowsPasteNative : IWindowsPasteNative
{
    public nint ForegroundWindow { get; set; } = 123;

    public uint TargetProcessId { get; set; } = 456;

    public bool WindowExists { get; set; } = true;

    public IntegrityComparison Integrity { get; set; } = IntegrityComparison.SameOrLower;

    public bool ActivateResult { get; set; } = true;

    public bool SendPasteResult { get; set; } = true;

    public bool SendPasteCalled { get; private set; }

    public nint GetForegroundWindow() => ForegroundWindow;

    public bool IsWindow(nint windowHandle) => WindowExists;

    public uint GetWindowProcessId(nint windowHandle) => TargetProcessId;

    public bool SetForegroundWindow(nint windowHandle)
    {
        if (ActivateResult)
        {
            ForegroundWindow = windowHandle;
        }

        return ActivateResult;
    }

    public IntegrityComparison CompareIntegrity(uint targetProcessId) => Integrity;

    public bool SendPasteShortcut()
    {
        SendPasteCalled = true;
        return SendPasteResult;
    }
}
