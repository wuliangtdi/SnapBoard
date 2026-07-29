using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SnapBoard.Application.Clipboard;
using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Desktop.HeadlessTests;

public sealed class ClipboardCaptureCoordinatorTests
{
    [Fact]
    public async Task ReadResultsArePassedToApplicationCaptureService()
    {
        FakeClipboardMonitor monitor = new();
        FakeClipboardReader reader = new()
        {
            Result = new ClipboardReadResult(
                ClipboardReadStatus.Success,
                new ClipboardContentSnapshot
                {
                    SequenceNumber = 42,
                    CapturedAt = DateTimeOffset.UtcNow,
                    Source = new ClipboardSourceInfo(
                        1,
                        "test",
                        null,
                        ClipboardSourceAccessStatus.Identified),
                    Text = "captured",
                }),
        };
        RecordingCaptureService captureService = new();
        using ClipboardCaptureCoordinator coordinator = new(monitor, reader, captureService);

        coordinator.Start();
        monitor.Publish(42);
        ClipboardReadResult captured = await captureService.Captured.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(42UL, captured.Snapshot?.SequenceNumber);
        Assert.Equal(1, captureService.ProcessCount);
    }

    [Fact]
    public async Task PausedCaptureKeepsDrainingEventsWithoutReadingPayloads()
    {
        FakeClipboardMonitor monitor = new();
        FakeClipboardReader reader = new();
        using ClipboardCaptureCoordinator coordinator = new(monitor, reader);
        TaskCompletionSource observedPausedBatch =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += state =>
        {
            if (state.ObservedEventCount >= 100)
            {
                observedPausedBatch.TrySetResult();
            }
        };

        coordinator.SetPaused(paused: true);
        coordinator.Start();
        for (ulong sequence = 1; sequence <= 100; sequence++)
        {
            monitor.Publish(sequence);
        }

        await observedPausedBatch.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, reader.ReadCount);

        coordinator.SetPaused(paused: false);
        monitor.Publish(101);
        await reader.FirstRead.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public async Task ReaderFailureDoesNotExposeExceptionMessageToUiState()
    {
        FakeClipboardMonitor monitor = new();
        FakeClipboardReader reader = new()
        {
            Exception = new InvalidOperationException("sensitive clipboard content"),
        };
        using ClipboardCaptureCoordinator coordinator = new(monitor, reader);
        TaskCompletionSource<string> failure = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += state =>
        {
            if (state.ErrorMessage is not null)
            {
                failure.TrySetResult(state.ErrorMessage);
            }
        };

        coordinator.Start();
        monitor.Publish(1);
        string error = await failure.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal("clipboard-capture-failed", error);
        Assert.DoesNotContain("sensitive", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForegroundProtectionDrainsWithoutReadingProcessingOrWriting()
    {
        FakeClipboardMonitor monitor = new();
        FakeClipboardReader reader = new();
        RecordingCaptureService captureService = new();
        FakeForegroundWindowStateService foreground = new()
        {
            Result = Protected(ForegroundWindowState.FullScreen),
        };
        FakeDesktopLocalSettingsService settings = new();
        using ClipboardCaptureCoordinator coordinator = new(
            monitor,
            reader,
            captureService,
            foreground,
            settings);

        coordinator.Start();
        monitor.Publish(1);
        monitor.Publish(2);
        monitor.Publish(3);
        await WaitUntilAsync(() => coordinator.PauseReasons.HasFlag(
            ClipboardCapturePauseReason.ForegroundProtection));
        await WaitUntilAsync(() => reader.ReadCount == 0 &&
            captureService.ProcessCount == 0 &&
            coordinator.IsPaused);
        await WaitForObservedAsync(coordinator, 3);

        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(0, captureService.ProcessCount);
        Assert.Equal(0, captureService.SqliteWriteCount);
        Assert.Equal(0, captureService.BlobWriteCount);
        Assert.Equal(0, captureService.OutboxWriteCount);
    }

    [Fact]
    public async Task MaximizedForegroundAllowsCaptureByDefault()
    {
        FakeClipboardMonitor monitor = new();
        FakeClipboardReader reader = new();
        FakeForegroundWindowStateService foreground = new()
        {
            Result = Protected(ForegroundWindowState.Maximized),
        };
        using ClipboardCaptureCoordinator coordinator = new(
            monitor,
            reader,
            captureService: null,
            foreground,
            new FakeDesktopLocalSettingsService());

        coordinator.Start();
        monitor.Publish(9);
        await reader.FirstRead.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal([9UL], reader.ReadSequences);
        Assert.False(coordinator.PauseReasons.HasFlag(
            ClipboardCapturePauseReason.ForegroundProtection));
    }

    [Fact]
    public async Task StrictScopeProtectsMaximizedForegroundUntilTheNextEvent()
    {
        FakeClipboardMonitor monitor = new();
        FakeClipboardReader reader = new();
        RecordingCaptureService captureService = new();
        FakeForegroundWindowStateService foreground = new()
        {
            Result = Protected(ForegroundWindowState.Maximized),
        };
        FakeDesktopLocalSettingsService settings = new();
        settings.Update(settings.Current with
        {
            ProtectionScope = ForegroundProtectionScope.FullScreenAndMaximized,
        });
        using ClipboardCaptureCoordinator coordinator = new(
            monitor,
            reader,
            captureService,
            foreground,
            settings);

        coordinator.Start();
        monitor.Publish(10);
        await WaitForObservedAsync(coordinator, 1);
        foreground.Result = Normal();
        monitor.Publish(11);
        await reader.FirstRead.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => captureService.ProcessCount == 1);

        Assert.Equal(1, reader.ReadCount);
        Assert.Equal([11UL], reader.ReadSequences);
        Assert.Equal(1, captureService.ProcessCount);
    }

    [Fact]
    public async Task ProtectionScopeChangeImmediatelyReevaluatesMaximizedForeground()
    {
        FakeClipboardMonitor monitor = new();
        FakeForegroundWindowStateService foreground = new()
        {
            Result = Protected(ForegroundWindowState.Maximized),
        };
        FakeDesktopLocalSettingsService settings = new();
        using ClipboardCaptureCoordinator coordinator = new(
            monitor,
            new FakeClipboardReader(),
            captureService: null,
            foreground,
            settings);
        coordinator.Start();

        settings.Update(settings.Current with
        {
            ProtectionScope = ForegroundProtectionScope.FullScreenAndMaximized,
        });
        Assert.True(coordinator.PauseReasons.HasFlag(
            ClipboardCapturePauseReason.ForegroundProtection));

        settings.Update(settings.Current with
        {
            ProtectionScope = ForegroundProtectionScope.FullScreenOnly,
        });
        Assert.False(coordinator.PauseReasons.HasFlag(
            ClipboardCapturePauseReason.ForegroundProtection));

        monitor.Publish(12);
        await WaitForObservedAsync(coordinator, 1);
    }

    [Fact]
    public async Task ManualPauseSurvivesEnteringAndLeavingProtection()
    {
        FakeClipboardMonitor monitor = new();
        FakeClipboardReader reader = new();
        FakeForegroundWindowStateService foreground = new()
        {
            Result = Protected(ForegroundWindowState.FullScreen),
        };
        using ClipboardCaptureCoordinator coordinator = new(
            monitor,
            reader,
            captureService: null,
            foreground,
            new FakeDesktopLocalSettingsService());
        coordinator.SetPauseReason(ClipboardCapturePauseReason.Manual, active: true);
        coordinator.Start();

        monitor.Publish(1);
        await WaitForObservedAsync(coordinator, 1);
        Assert.True(coordinator.PauseReasons.HasFlag(
            ClipboardCapturePauseReason.ForegroundProtection));
        foreground.Result = Normal();
        monitor.Publish(2);
        await WaitForObservedAsync(coordinator, 2);

        Assert.True(coordinator.IsManuallyPaused);
        Assert.False(coordinator.PauseReasons.HasFlag(
            ClipboardCapturePauseReason.ForegroundProtection));
        Assert.Equal(0, reader.ReadCount);

        coordinator.SetPauseReason(ClipboardCapturePauseReason.Manual, active: false);
        monitor.Publish(3);
        await reader.FirstRead.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Equal([3UL], reader.ReadSequences);
    }

    [Fact]
    public void ClearingOnePauseReasonNeverClearsAnother()
    {
        using ClipboardCaptureCoordinator coordinator = new(
            new FakeClipboardMonitor(),
            new FakeClipboardReader());
        coordinator.SetPauseReason(ClipboardCapturePauseReason.Manual, active: true);
        coordinator.SetPauseReason(ClipboardCapturePauseReason.StorageMigration, active: true);
        coordinator.SetPauseReason(ClipboardCapturePauseReason.UpdateInstallation, active: true);
        coordinator.SetPauseReason(ClipboardCapturePauseReason.ForegroundProtection, active: true);

        coordinator.SetPauseReason(ClipboardCapturePauseReason.ForegroundProtection, active: false);
        Assert.True(coordinator.IsManuallyPaused);
        Assert.True(coordinator.PauseReasons.HasFlag(
            ClipboardCapturePauseReason.StorageMigration));
        Assert.True(coordinator.PauseReasons.HasFlag(
            ClipboardCapturePauseReason.UpdateInstallation));

        coordinator.SetPauseReason(ClipboardCapturePauseReason.StorageMigration, active: false);
        Assert.True(coordinator.IsManuallyPaused);
        Assert.True(coordinator.PauseReasons.HasFlag(
            ClipboardCapturePauseReason.UpdateInstallation));

        coordinator.SetPauseReason(ClipboardCapturePauseReason.Manual, active: false);
        Assert.True(coordinator.IsPaused);
        coordinator.SetPauseReason(ClipboardCapturePauseReason.UpdateInstallation, active: false);
        Assert.False(coordinator.IsPaused);
    }

    [Fact]
    public async Task DisablingAutomaticProtectionDoesNotReleaseMigrationPause()
    {
        FakeClipboardMonitor monitor = new();
        FakeForegroundWindowStateService foreground = new()
        {
            Result = Protected(ForegroundWindowState.FullScreen),
        };
        FakeDesktopLocalSettingsService settings = new();
        using ClipboardCaptureCoordinator coordinator = new(
            monitor,
            new FakeClipboardReader(),
            captureService: null,
            foreground,
            settings);
        coordinator.SetPauseReason(ClipboardCapturePauseReason.StorageMigration, active: true);
        coordinator.Start();
        monitor.Publish(1);
        await WaitForObservedAsync(coordinator, 1);

        settings.Update(settings.Current with
        {
            PauseClipboardCaptureWhenProtected = false,
        });

        Assert.False(coordinator.PauseReasons.HasFlag(
            ClipboardCapturePauseReason.ForegroundProtection));
        Assert.True(coordinator.PauseReasons.HasFlag(
            ClipboardCapturePauseReason.StorageMigration));
        Assert.True(coordinator.IsPaused);
    }

    [Theory]
    [InlineData(ForegroundWindowState.Unknown)]
    [InlineData(ForegroundWindowState.Unavailable)]
    public async Task IndeterminateForegroundStateAllowsCapture(ForegroundWindowState state)
    {
        FakeClipboardMonitor monitor = new();
        FakeClipboardReader reader = new();
        FakeForegroundWindowStateService foreground = new()
        {
            Result = new ForegroundWindowStateResult(
                state,
                IsSnapBoard: false,
                Identity: null,
                ForegroundWindowDiagnosticCode.NativeFailure),
        };
        using ClipboardCaptureCoordinator coordinator = new(
            monitor,
            reader,
            captureService: null,
            foreground,
            new FakeDesktopLocalSettingsService());

        coordinator.Start();
        monitor.Publish(1);
        await reader.FirstRead.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public async Task ForegroundDetectionFailureDoesNotStopWatcher()
    {
        FakeClipboardMonitor monitor = new();
        FakeClipboardReader reader = new();
        FakeForegroundWindowStateService foreground = new() { Throw = true };
        using ClipboardCaptureCoordinator coordinator = new(
            monitor,
            reader,
            captureService: null,
            foreground,
            new FakeDesktopLocalSettingsService());

        coordinator.Start();
        monitor.Publish(1);
        monitor.Publish(2);
        await WaitUntilAsync(() => reader.ReadCount == 2);

        Assert.Equal([1UL, 2UL], reader.ReadSequences);
    }

    private static ForegroundWindowStateResult Protected(ForegroundWindowState state) => new(
        state,
        IsSnapBoard: false,
        new ForegroundWindowIdentity(1, 2),
        ForegroundWindowDiagnosticCode.None);

    private static ForegroundWindowStateResult Normal() => new(
        ForegroundWindowState.Normal,
        IsSnapBoard: false,
        new ForegroundWindowIdentity(1, 2),
        ForegroundWindowDiagnosticCode.None);

    private static async Task WaitForObservedAsync(
        ClipboardCaptureCoordinator coordinator,
        long count) => await WaitUntilAsync(() => coordinator.ObservedEventCount >= count);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeClipboardMonitor : IClipboardMonitor
    {
        private readonly Channel<ClipboardChangedEvent> _events = Channel.CreateUnbounded<ClipboardChangedEvent>();

        public async IAsyncEnumerable<ClipboardChangedEvent> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (ClipboardChangedEvent change in
                _events.Reader.ReadAllAsync(cancellationToken))
            {
                yield return change;
            }
        }

        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void Publish(ulong sequenceNumber) => _events.Writer.TryWrite(
            new ClipboardChangedEvent(sequenceNumber, DateTimeOffset.UtcNow));
    }

    private sealed class FakeClipboardReader : IClipboardContentReader
    {
        private int _readCount;

        public TaskCompletionSource FirstRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReadCount => Volatile.Read(ref _readCount);

        public List<ulong> ReadSequences { get; } = [];

        public ClipboardReadResult Result { get; init; } = new(
            ClipboardReadStatus.Failed,
            null,
            ClipboardReadFailureReason.NativeFailure);

        public Exception? Exception { get; init; }

        public ValueTask<ClipboardReadResult> ReadAsync(
            ClipboardChangedEvent change,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _readCount);
            lock (ReadSequences)
            {
                ReadSequences.Add(change.SequenceNumber);
            }
            FirstRead.TrySetResult();
            if (Exception is not null)
            {
                throw Exception;
            }

            return ValueTask.FromResult(Result);
        }
    }

    private sealed class RecordingCaptureService : IClipboardCaptureService
    {
        private int _processCount;

        public TaskCompletionSource<ClipboardReadResult> Captured { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessCount => Volatile.Read(ref _processCount);

        public int SqliteWriteCount { get; private set; }

        public int BlobWriteCount { get; private set; }

        public int OutboxWriteCount { get; private set; }

        public ValueTask<ClipboardCaptureResult> ProcessAsync(
            ClipboardReadResult readResult,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _processCount);
            SqliteWriteCount++;
            BlobWriteCount++;
            OutboxWriteCount++;
            Captured.TrySetResult(readResult);
            return ValueTask.FromResult(new ClipboardCaptureResult(
                ClipboardCaptureStatus.Stored,
                "stored"));
        }
    }

    private sealed class FakeForegroundWindowStateService :
        IPlatformForegroundWindowStateService
    {
        public ForegroundWindowStateResult Result { get; set; } = Normal();

        public bool Throw { get; set; }

        public ForegroundWindowStateResult GetForegroundWindowState() => Throw
            ? throw new InvalidOperationException("native detail")
            : Result;
    }

    private sealed class FakeDesktopLocalSettingsService : IDesktopLocalSettingsService
    {
        public event EventHandler<DesktopLocalSettingsChangedEventArgs>? Changed;

        public DesktopLocalSettings Current { get; private set; } =
            DesktopLocalSettings.CreateDefaults(GlobalHotKeyGesture.WindowsDefault);

        public DesktopLocalSettingsUpdateResult Update(DesktopLocalSettings settings)
        {
            Current = settings;
            Changed?.Invoke(this, new DesktopLocalSettingsChangedEventArgs(settings));
            return new DesktopLocalSettingsUpdateResult(Persisted: true);
        }

        public DesktopLocalSettingsUpdateResult Update(
            Func<DesktopLocalSettings, DesktopLocalSettings> update) => Update(update(Current));
    }
}
