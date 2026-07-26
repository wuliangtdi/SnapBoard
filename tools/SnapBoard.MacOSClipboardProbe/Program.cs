using System.Text;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.MacOS;

namespace SnapBoard.MacOSClipboardProbe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("This probe requires macOS.");
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

        await using MacOSClipboardAdapter adapter = new();
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "watch" => await WatchAsync(adapter, args, cancellation.Token),
                "read" => await ReadCurrentAsync(adapter, cancellation.Token),
                "write-text" => await WriteTextAsync(adapter, args, cancellation.Token),
                "write-formats" => await WriteFormatsAsync(adapter, args, cancellation.Token),
                "paste-text" => await PasteTextAsync(adapter, args, cancellation.Token),
                "permission" => PrintPermission(adapter, args),
                _ => UnknownCommand(),
            };
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
    }

    private static async Task<int> WatchAsync(
        MacOSClipboardAdapter adapter,
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
        MacOSClipboardAdapter adapter,
        CancellationToken cancellationToken)
    {
        ClipboardReadResult read = await adapter.ReadAsync(
            new ClipboardChangedEvent(0, DateTimeOffset.UtcNow),
            cancellationToken);
        PrintSnapshot(read);
        return read.Snapshot is null ? 3 : 0;
    }

    private static async Task<int> WriteTextAsync(
        MacOSClipboardAdapter adapter,
        string[] args,
        CancellationToken cancellationToken)
    {
        string text = ReadStringOption(args, "--text") ?? "SnapBoard macOS clipboard probe";
        ClipboardWriteResult result = await adapter.WritePlainTextAsync(text, cancellationToken);
        PrintWriteResult(result);
        return IsSuccessful(result.Status) ? 0 : 4;
    }

    private static async Task<int> WriteFormatsAsync(
        MacOSClipboardAdapter adapter,
        string[] args,
        CancellationToken cancellationToken)
    {
        string? imagePath = ReadStringOption(args, "--image");
        string? filePath = ReadStringOption(args, "--file");
        ClipboardBitmapData? bitmap = imagePath is null ? null : ReadBitmap(imagePath);
        ClipboardWriteRequest request = new()
        {
            Text = ReadStringOption(args, "--text") ?? "SnapBoard format validation",
            Html = Encoding.UTF8.GetBytes("<b>SnapBoard format validation</b>"),
            RichText = Encoding.ASCII.GetBytes(@"{\rtf1\ansi SnapBoard format validation}"),
            Bitmap = bitmap,
            FilePaths = filePath is null ? [] : [Path.GetFullPath(filePath)],
        };

        ClipboardWriteResult result = await adapter.WriteAsync(request, cancellationToken);
        PrintWriteResult(result);
        return IsSuccessful(result.Status) ? 0 : 4;
    }

    private static async Task<int> PasteTextAsync(
        MacOSClipboardAdapter adapter,
        string[] args,
        CancellationToken cancellationToken)
    {
        string text = ReadStringOption(args, "--text") ?? "SnapBoard automatic paste probe";
        int delaySeconds = ReadIntOption(args, "--delay", 3);

        Console.WriteLine($"Focus the target application within {delaySeconds} seconds.");
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
        IAutomaticPasteTarget? target = adapter.CaptureForegroundTarget();
        Console.WriteLine($"CapturedTarget={target?.ToString() ?? "none"}");
        int afterCaptureDelaySeconds = ReadNonNegativeIntOption(
            args,
            "--after-capture-delay",
            0);
        if (target is not null && afterCaptureDelaySeconds > 0)
        {
            Console.WriteLine(
                $"Target captured. Switch away for {afterCaptureDelaySeconds} seconds " +
                "to validate restoration.");
            await Task.Delay(
                TimeSpan.FromSeconds(afterCaptureDelaySeconds),
                cancellationToken);
        }

        ClipboardWriteResult write = await adapter.WritePlainTextAsync(text, cancellationToken);
        PrintWriteResult(write);
        if (!IsSuccessful(write.Status) || target is null)
        {
            string failure = AutomaticPasteResult.ManualPasteRequiredMessage;
            Console.WriteLine(failure);
            WriteOutputIfRequested(args, [FormatWriteResult(write), failure]);
            return 5;
        }

        AutomaticPasteResult paste = await adapter.TryPasteAsync(target, cancellationToken);
        string pasteLine = $"PasteStatus={paste.Status}; Reason={paste.FailureReason}";
        Console.WriteLine(pasteLine);
        List<string> output = [FormatWriteResult(write), pasteLine];
        if (paste.Status != AutomaticPasteStatus.Pasted)
        {
            Console.WriteLine(AutomaticPasteResult.ManualPasteRequiredMessage);
            output.Add(AutomaticPasteResult.ManualPasteRequiredMessage);
        }

        WriteOutputIfRequested(args, output);

        return paste.Status == AutomaticPasteStatus.Pasted ? 0 : 5;
    }

    private static int PrintPermission(MacOSClipboardAdapter adapter, string[] args)
    {
        bool granted = adapter.IsAccessibilityPermissionGranted;
        string line = $"AccessibilityPermissionGranted={granted}";
        Console.WriteLine(line);
        WriteOutputIfRequested(args, [line]);
        return granted ? 0 : 5;
    }

    private static ClipboardBitmapData ReadBitmap(string imagePath)
    {
        string fullPath = Path.GetFullPath(imagePath);
        byte[] bytes = File.ReadAllBytes(fullPath);
        ClipboardBitmapEncoding encoding = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".png" => ClipboardBitmapEncoding.PortableNetworkGraphics,
            ".tif" or ".tiff" => ClipboardBitmapEncoding.TaggedImageFileFormat,
            _ => throw new ArgumentException("Only PNG and TIFF images are supported.", nameof(imagePath)),
        };

        return new ClipboardBitmapData(encoding, bytes, 0, 0, 0);
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
        string files = string.Join('|', snapshot.FilePaths);
        Console.WriteLine(
            $"Sequence={snapshot.SequenceNumber}; ReadStatus={read.Status}; " +
            $"Source={snapshot.Source.ProcessName ?? "unknown"}; " +
            $"SourceAccess={snapshot.Source.AccessStatus}; Text={snapshot.Text is not null}; " +
            $"Html={!snapshot.Html.IsEmpty}; Rtf={!snapshot.RichText.IsEmpty}; " +
            $"Bitmap={snapshot.Bitmap?.Encoding.ToString() ?? "none"}; " +
            $"Files={snapshot.FilePaths.Count}; Self={snapshot.IsFromCurrentApplication}; " +
            $"FilePaths=[{files}]; Formats=[{formats}]");
    }

    private static void PrintWriteResult(ClipboardWriteResult result) =>
        Console.WriteLine(FormatWriteResult(result));

    private static string FormatWriteResult(ClipboardWriteResult result) =>
        $"WriteStatus={result.Status}; Sequence={result.SequenceNumber}; " +
        $"Marker={result.FeedbackMarkerWritten}; NativeError={result.NativeErrorCode}";

    private static void WriteOutputIfRequested(string[] args, IReadOnlyList<string> lines)
    {
        string? outputPath = ReadStringOption(args, "--output");
        if (outputPath is not null)
        {
            File.WriteAllLines(Path.GetFullPath(outputPath), lines);
        }
    }

    private static int ReadIntOption(string[] args, string option, int fallback)
    {
        string? value = ReadStringOption(args, option);
        return value is not null && int.TryParse(value, out int parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private static int ReadNonNegativeIntOption(string[] args, string option, int fallback)
    {
        string? value = ReadStringOption(args, option);
        return value is not null && int.TryParse(value, out int parsed) && parsed >= 0
            ? parsed
            : fallback;
    }

    private static string? ReadStringOption(string[] args, string option)
    {
        int index = Array.FindIndex(args, argument =>
            string.Equals(argument, option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool IsSuccessful(ClipboardWriteStatus status) =>
        status is ClipboardWriteStatus.Success or ClipboardWriteStatus.Partial;

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
        Console.WriteLine("  write-text --text <text>");
        Console.WriteLine("  write-formats [--text <text>] [--image <png-or-tiff>] [--file <path>]");
        Console.WriteLine(
            "  paste-text --text <text> --delay 3 " +
            "[--after-capture-delay 5] [--output <path>]");
        Console.WriteLine("  permission [--output <path>]");
    }
}
