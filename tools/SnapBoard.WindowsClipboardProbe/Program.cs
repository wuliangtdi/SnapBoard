using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Channels;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Windows;
using SnapBoard.Platform.Windows.Clipboard;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.WindowsClipboardProbe;

internal static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This probe requires Windows.");
            return 2;
        }

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        using CancellationTokenSource cancellation = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        await using WindowsClipboardAdapter adapter = new();

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "watch" => await WatchAsync(adapter, args, cancellation.Token),
                "read" => await ReadCurrentAsync(adapter, cancellation.Token),
                "paste-text" => await PasteTextAsync(adapter, args, cancellation.Token),
                "delayed-read" => await DelayedReadAsync(adapter, cancellation.Token),
                "stress" => await StressAsync(adapter, args, cancellation.Token),
                _ => UnknownCommand(),
            };
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
    }

    private static async Task<int> WatchAsync(
        WindowsClipboardAdapter adapter,
        string[] args,
        CancellationToken cancellationToken)
    {
        int seconds = ReadIntOption(args, "--seconds", 20);
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));

        Console.WriteLine($"Watching clipboard metadata for {seconds} seconds...");
        try
        {
            await foreach (ClipboardChangedEvent change in adapter.WatchAsync(timeout.Token))
            {
                ClipboardReadResult read = await adapter.ReadAsync(change, timeout.Token);
                PrintSnapshot(read);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        Console.WriteLine($"DroppedEvents={adapter.DroppedEventCount}");
        return 0;
    }

    private static async Task<int> ReadCurrentAsync(
        WindowsClipboardAdapter adapter,
        CancellationToken cancellationToken)
    {
        ClipboardReadResult read = await adapter.ReadAsync(
            new ClipboardChangedEvent(0, DateTimeOffset.UtcNow),
            cancellationToken);
        PrintSnapshot(read);
        return read.Snapshot is null ? 3 : 0;
    }

    private static async Task<int> PasteTextAsync(
        WindowsClipboardAdapter adapter,
        string[] args,
        CancellationToken cancellationToken)
    {
        string text = ReadStringOption(args, "--text") ?? "SnapBoard Windows clipboard probe";
        IAutomaticPasteTarget? target;
        string? targetHandleText = ReadStringOption(args, "--target-hwnd");
        if (targetHandleText is not null &&
            long.TryParse(targetHandleText, out long targetHandleValue) &&
            targetHandleValue != 0)
        {
            nint targetHandle = checked((nint)targetHandleValue);
            WindowsNativeMethods.GetWindowThreadProcessId(targetHandle, out uint processId);
            target = WindowsNativeMethods.IsWindow(targetHandle) && processId != 0
                ? new WindowsAutomaticPasteTarget(targetHandle, processId)
                : null;
            Console.WriteLine($"TargetMode=ExplicitHwnd; TargetValid={target is not null}");
        }
        else
        {
            int delaySeconds = ReadIntOption(args, "--delay", 3);
            Console.WriteLine($"Focus the target window within {delaySeconds} seconds.");
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            target = adapter.CaptureForegroundTarget();
        }

        ClipboardWriteResult write = await adapter.WritePlainTextAsync(text, cancellationToken);
        if (write.Status is not ClipboardWriteStatus.Success and not ClipboardWriteStatus.Partial)
        {
            Console.WriteLine($"WriteStatus={write.Status}; NativeError={write.NativeErrorCode}");
            return 4;
        }

        if (target is null)
        {
            Console.WriteLine(
                $"PasteStatus={AutomaticPasteStatus.ManualPasteRequired}; " +
                AutomaticPasteResult.ManualPasteRequiredMessage);
            return 5;
        }

        AutomaticPasteResult paste = await adapter.TryPasteAsync(target, cancellationToken);
        Console.WriteLine($"PasteStatus={paste.Status}; Reason={paste.FailureReason}");
        if (paste.Status != AutomaticPasteStatus.Pasted)
        {
            Console.WriteLine(AutomaticPasteResult.ManualPasteRequiredMessage);
        }

        return paste.Status == AutomaticPasteStatus.Pasted ? 0 : 5;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> DelayedReadAsync(
        WindowsClipboardAdapter adapter,
        CancellationToken cancellationToken)
    {
        string expectedText = $"SnapBoard delayed rendering {Guid.NewGuid():N}";
        await using DelayedRenderingClipboardOwner owner = new(expectedText);
        await owner.StartAsync(cancellationToken);

        ClipboardReadResult read = await adapter.ReadAsync(
            new ClipboardChangedEvent(0, DateTimeOffset.UtcNow),
            cancellationToken);
        bool passed = read.Snapshot?.Text == expectedText &&
            read.FailureReason != ClipboardReadFailureReason.DelayedRenderingUnavailable;
        Console.WriteLine(
            $"DelayedRendering={(passed ? "Passed" : "Failed")}; " +
            $"ReadStatus={read.Status}; Reason={read.FailureReason}; " +
            $"TextMatched={read.Snapshot?.Text == expectedText}");
        return passed ? 0 : 6;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> StressAsync(
        WindowsClipboardAdapter listener,
        string[] args,
        CancellationToken cancellationToken)
    {
        int eventCount = ReadIntOption(args, "--events", 10_000);
        int warmupCount = ReadIntOption(args, "--warmup", 1_000);
        int timeoutSeconds = ReadIntOption(args, "--timeout-seconds", 600);
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        await using WindowsClipboardAdapter writer = new();
        using CancellationTokenSource listenerWatchCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using CancellationTokenSource selfWatchCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Channel<ulong> listenerEvents = Channel.CreateUnbounded<ulong>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
        ConcurrentBag<ulong> writerObservedSequences = [];
        HashSet<ulong> writtenSequences = new(eventCount);
        Task listenerWatch = Task.Run(async () =>
        {
            try
            {
                await foreach (ClipboardChangedEvent change in
                    listener.WatchAsync(listenerWatchCancellation.Token))
                {
                    listenerEvents.Writer.TryWrite(change.SequenceNumber);
                }
            }
            catch (OperationCanceledException) when (listenerWatchCancellation.IsCancellationRequested)
            {
            }
            finally
            {
                listenerEvents.Writer.TryComplete();
            }
        }, CancellationToken.None);
        Task writerWatch = Task.Run(async () =>
        {
            try
            {
                await foreach (ClipboardChangedEvent change in
                    writer.WatchAsync(selfWatchCancellation.Token))
                {
                    writerObservedSequences.Add(change.SequenceNumber);
                }
            }
            catch (OperationCanceledException) when (selfWatchCancellation.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        for (int index = 0; index < warmupCount; index++)
        {
            (ClipboardWriteResult warmupWrite, _) = await WriteWithProbeRetryAsync(
                writer,
                $"SnapBoard stress warmup {index:D3}",
                timeout.Token);
            if (warmupWrite.Status is not (ClipboardWriteStatus.Success or ClipboardWriteStatus.Partial))
            {
                Console.WriteLine($"WarmupWriteFailedAt={index}; Status={warmupWrite.Status}");
                return 7;
            }

            await WaitForMatchingSequenceAsync(
                listenerEvents.Reader,
                warmupWrite.SequenceNumber,
                timeout.Token);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Process process = Process.GetCurrentProcess();
        process.Refresh();
        long privateBytesBefore = process.PrivateMemorySize64;
        long workingSetBefore = process.WorkingSet64;
        int handlesBefore = process.HandleCount;
        TimeSpan cpuBefore = process.TotalProcessorTime;
        Stopwatch stopwatch = Stopwatch.StartNew();

        int matchedEvents = 0;
        int unrelatedEvents = 0;
        int busyRetries = 0;
        bool writeFailed = false;
        bool listenerTimedOut = false;

        for (int index = 0; index < eventCount; index++)
        {
            (ClipboardWriteResult write, int retryCount) = await WriteWithProbeRetryAsync(
                writer,
                $"SnapBoard stress event {index:D5}",
                timeout.Token);
            busyRetries += retryCount;
            if (write.Status is not (ClipboardWriteStatus.Success or ClipboardWriteStatus.Partial))
            {
                Console.WriteLine($"WriteFailedAt={index}; Status={write.Status}");
                writeFailed = true;
                break;
            }

            writtenSequences.Add(write.SequenceNumber);
            try
            {
                unrelatedEvents += await WaitForMatchingSequenceAsync(
                    listenerEvents.Reader,
                    write.SequenceNumber,
                    timeout.Token);
                matchedEvents++;
            }
            catch (TimeoutException)
            {
                Console.WriteLine($"ListenerTimedOutAt={index}");
                listenerTimedOut = true;
            }

            if (listenerTimedOut)
            {
                break;
            }
        }

        stopwatch.Stop();
        await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        long privateBytesAfter = process.PrivateMemorySize64;
        long workingSetAfter = process.WorkingSet64;
        int handlesAfter = process.HandleCount;
        TimeSpan cpuAfter = process.TotalProcessorTime;
        int feedbackEvents = writerObservedSequences.Count(writtenSequences.Contains);
        double cpuPercent = (cpuAfter - cpuBefore).TotalMilliseconds /
            stopwatch.Elapsed.TotalMilliseconds /
            Environment.ProcessorCount * 100;
        long privateGrowth = privateBytesAfter - privateBytesBefore;
        bool resourceBudgetMet = privateGrowth <= 8L * 1024 * 1024;

        Console.WriteLine(
            $"Warmup={warmupCount}; Events={eventCount}; Matched={matchedEvents}; " +
            $"Unrelated={unrelatedEvents}; " +
            $"BusyRetries={busyRetries}; Feedback={feedbackEvents}; " +
            $"ListenerDropped={listener.DroppedEventCount}; " +
            $"WriterDropped={writer.DroppedEventCount}");
        Console.WriteLine(
            $"ElapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0}; CPU={cpuPercent:F2}%; " +
            $"PrivateBefore={privateBytesBefore}; PrivateAfter={privateBytesAfter}; " +
            $"PrivateGrowth={privateGrowth}; WorkingSetBefore={workingSetBefore}; " +
            $"WorkingSetAfter={workingSetAfter}; HandlesBefore={handlesBefore}; " +
            $"HandlesAfter={handlesAfter}; GrowthBudget8MiB={resourceBudgetMet}");

        listenerWatchCancellation.Cancel();
        selfWatchCancellation.Cancel();
        await Task.WhenAll(listenerWatch, writerWatch);

        bool passed = !writeFailed &&
            !listenerTimedOut &&
            matchedEvents == eventCount &&
            feedbackEvents == 0 &&
            listener.DroppedEventCount == 0 &&
            writer.DroppedEventCount == 0 &&
            resourceBudgetMet;
        return passed ? 0 : 9;
    }

    private static async Task<(ClipboardWriteResult Result, int RetryCount)> WriteWithProbeRetryAsync(
        WindowsClipboardAdapter writer,
        string text,
        CancellationToken cancellationToken)
    {
        TimeSpan[] retryDelays =
        [
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(80),
            TimeSpan.FromMilliseconds(160),
        ];

        for (int attempt = 0; ; attempt++)
        {
            ClipboardWriteResult result =
                await writer.WritePlainTextAsync(text, cancellationToken);
            if (result.Status != ClipboardWriteStatus.ClipboardBusy ||
                attempt == retryDelays.Length)
            {
                return (result, attempt);
            }

            await Task.Delay(retryDelays[attempt], cancellationToken);
        }
    }

    private static async Task<int> WaitForMatchingSequenceAsync(
        ChannelReader<ulong> reader,
        ulong expectedSequence,
        CancellationToken cancellationToken)
    {
        int unrelatedEvents = 0;
        while (true)
        {
            ulong sequenceNumber = await reader.ReadAsync(cancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (sequenceNumber == expectedSequence)
            {
                return unrelatedEvents;
            }

            unrelatedEvents++;
        }
    }

    private static void PrintSnapshot(ClipboardReadResult read)
    {
        if (read.Snapshot is not ClipboardContentSnapshot snapshot)
        {
            Console.WriteLine(
                $"ReadStatus={read.Status}; Reason={read.FailureReason}; " +
                $"NativeError={read.NativeErrorCode}");
            return;
        }

        string formats = string.Join(',', snapshot.Formats.Select(format => format.Name));
        Console.WriteLine(
            $"Sequence={snapshot.SequenceNumber}; ReadStatus={read.Status}; " +
            $"Source={snapshot.Source.ProcessName ?? "unknown"}; " +
            $"SourceAccess={snapshot.Source.AccessStatus}; " +
            $"Text={snapshot.Text is not null}; Html={!snapshot.Html.IsEmpty}; " +
            $"Rtf={!snapshot.RichText.IsEmpty}; Bitmap={snapshot.Bitmap is not null}; " +
            $"Files={snapshot.FilePaths.Count}; Self={snapshot.IsFromCurrentApplication}; " +
            $"Formats=[{formats}]");
    }

    private static int ReadIntOption(string[] args, string option, int fallback)
    {
        string? value = ReadStringOption(args, option);
        return value is not null && int.TryParse(value, out int parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private static string? ReadStringOption(string[] args, string option)
    {
        int index = Array.FindIndex(args, argument =>
            string.Equals(argument, option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int UnknownCommand()
    {
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  watch --seconds 20");
        Console.WriteLine("  read");
        Console.WriteLine("  paste-text --text <generated-text> [--delay 3 | --target-hwnd <decimal>]");
        Console.WriteLine("  delayed-read");
        Console.WriteLine("  stress --warmup 1000 --events 10000 --timeout-seconds 600");
    }
}
