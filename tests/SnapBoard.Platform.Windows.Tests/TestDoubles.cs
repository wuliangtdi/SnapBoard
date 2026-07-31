using SnapBoard.Platform.Windows.Clipboard;
using SnapBoard.Platform.Windows.Desktop;

namespace SnapBoard.Platform.Windows.Tests;

internal sealed class FakeClipboardMessageHost : IClipboardMessageHost
{
    public event Action<ClipboardUpdateObservation>? ClipboardUpdated;

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

    public void RaiseClipboardUpdated(
        uint sequenceNumber,
        int? clipboardOwnerProcessId = null,
        int? foregroundProcessId = null,
        nint clipboardOwnerWindowHandle = 0) =>
        ClipboardUpdated?.Invoke(new ClipboardUpdateObservation(
            sequenceNumber,
            clipboardOwnerProcessId,
            foregroundProcessId,
            clipboardOwnerWindowHandle));

    public void FailMessageLoop(Exception error) => MessageLoopStopped?.Invoke(error);
}

internal sealed class CancelableStartupClipboardMessageHost : IClipboardMessageHost
{
    public event Action<ClipboardUpdateObservation>? ClipboardUpdated
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

    public bool ChangeProcessAfterActivation { get; set; }

    public bool StealForegroundBeforeSend { get; set; }

    private int _processIdReadCount;

    public nint GetForegroundWindow() => ForegroundWindow;

    public bool IsWindow(nint windowHandle) => WindowExists;

    public uint GetWindowProcessId(nint windowHandle)
    {
        uint processId = TargetProcessId;
        if (StealForegroundBeforeSend && Interlocked.Increment(ref _processIdReadCount) >= 4)
        {
            ForegroundWindow = 999;
        }

        return processId;
    }

    public bool SetForegroundWindow(nint windowHandle)
    {
        if (ActivateResult)
        {
            ForegroundWindow = windowHandle;
            if (ChangeProcessAfterActivation)
            {
                TargetProcessId++;
            }
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

internal sealed class FakeWindowsRegistryStore : IWindowsRegistryStore
{
    private readonly Dictionary<(string SubKey, string Name), string> _values = [];

    public string? GetString(string subKey, string name) =>
        _values.GetValueOrDefault((subKey, name));

    public void SetString(string subKey, string name, string value) =>
        _values[(subKey, name)] = value;

    public void DeleteValue(string subKey, string name) => _values.Remove((subKey, name));

    public void Seed(string subKey, string name, string value) =>
        _values[(subKey, name)] = value;

    public bool TryGetValue(string subKey, string name, out string? value) =>
        _values.TryGetValue((subKey, name), out value);
}

internal sealed record WindowsHotKeyNativeRegistration(
    nint WindowHandle,
    int Identifier,
    uint Modifiers,
    uint VirtualKey);

internal sealed class FakeWindowsHotKeyNative : IWindowsHotKeyNative
{
    private readonly Queue<(bool Result, int Error)> _registerResults = new();
    private readonly Queue<(bool Result, int Error)> _unregisterResults = new();

    public List<WindowsHotKeyNativeRegistration> Registrations { get; } = [];

    public List<int> UnregisteredIdentifiers { get; } = [];

    public int RegisterCount { get; private set; }

    public int UnregisterCount { get; private set; }

    public int LastError { get; private set; }

    public void EnqueueRegisterResult(bool result, int error = 0) =>
        _registerResults.Enqueue((result, error));

    public void EnqueueUnregisterResult(bool result, int error = 0) =>
        _unregisterResults.Enqueue((result, error));

    public bool Register(nint windowHandle, int identifier, uint modifiers, uint virtualKey)
    {
        RegisterCount++;
        Registrations.Add(new WindowsHotKeyNativeRegistration(
            windowHandle,
            identifier,
            modifiers,
            virtualKey));
        (bool result, int error) = _registerResults.Count == 0
            ? (true, 0)
            : _registerResults.Dequeue();
        LastError = error;
        return result;
    }

    public bool Unregister(nint windowHandle, int identifier)
    {
        UnregisterCount++;
        UnregisteredIdentifiers.Add(identifier);
        (bool result, int error) = _unregisterResults.Count == 0
            ? (true, 0)
            : _unregisterResults.Dequeue();
        LastError = error;
        return result;
    }

    public int GetLastError() => LastError;
}
