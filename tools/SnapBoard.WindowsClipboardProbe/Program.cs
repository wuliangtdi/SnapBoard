using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Windows;

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
        int delaySeconds = ReadIntOption(args, "--delay", 3);

        Console.WriteLine($"Focus the target window within {delaySeconds} seconds.");
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
        IAutomaticPasteTarget? target = adapter.CaptureForegroundTarget();

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
        Console.WriteLine("  paste-text --text <generated-text> --delay 3");
    }
}
