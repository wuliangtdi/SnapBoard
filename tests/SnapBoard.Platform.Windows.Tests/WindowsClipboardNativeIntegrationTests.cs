using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Windows.Clipboard;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Tests;

[Collection(CollectionName)]
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardNativeIntegrationTests
{
    public const string CollectionName = "Windows clipboard native integration";

    [WindowsFact]
    public async Task ListenerReceivesAnotherAdapterWriteAndReaderIdentifiesFormats()
    {
        await using WindowsClipboardAdapter listener = new();
        await using WindowsClipboardAdapter writer = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<ClipboardChangedEvent> enumerator =
            listener.WatchAsync(cancellation.Token).GetAsyncEnumerator();

        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();
        await WaitForMessageWindowAsync(listener, cancellation.Token);
        string text = $"SnapBoard integration {Guid.NewGuid():N}";

        ClipboardWriteResult write = await writer.WritePlainTextAsync(text, cancellation.Token);

        Assert.Equal(ClipboardWriteStatus.Success, write.Status);
        Assert.True(await moveNext.WaitAsync(cancellation.Token));
        Assert.Equal(Environment.ProcessId, enumerator.Current.SourceHint.ClipboardOwnerProcessId);

        ClipboardReadResult read = await listener.ReadAsync(enumerator.Current, cancellation.Token);
        ClipboardContentSnapshot snapshot = Assert.IsType<ClipboardContentSnapshot>(read.Snapshot);
        Assert.Equal(text, snapshot.Text);
        Assert.False(snapshot.IsFromCurrentApplication);
        Assert.Contains(snapshot.Formats, format => format.Name == "CF_UNICODETEXT");
    }

    [WindowsFact]
    public async Task RoundTripsSupportedFormatsAndSourceProcess()
    {
        await using WindowsClipboardAdapter adapter = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        string temporaryFile = Path.GetTempFileName();

        try
        {
            byte[] html = Encoding.UTF8.GetBytes("<b>SnapBoard HTML</b>");
            byte[] richText = Encoding.ASCII.GetBytes(@"{\rtf1\ansi SnapBoard RTF}");
            ClipboardWriteRequest request = new()
            {
                Text = "SnapBoard Unicode 文本",
                Html = html,
                RichText = richText,
                Bitmap = CreateOnePixelBitmap(),
                FilePaths = [temporaryFile],
            };

            ClipboardWriteResult write = await adapter.WriteAsync(request, cancellation.Token);
            ClipboardReadResult read = await adapter.ReadAsync(
                new ClipboardChangedEvent(write.SequenceNumber, DateTimeOffset.UtcNow),
                cancellation.Token);

            ClipboardContentSnapshot snapshot = Assert.IsType<ClipboardContentSnapshot>(read.Snapshot);
            Assert.Equal(ClipboardWriteStatus.Success, write.Status);
            Assert.Equal(ClipboardReadStatus.Success, read.Status);
            Assert.Equal(request.Text, snapshot.Text);
            Assert.Equal(html, snapshot.Html.ToArray());
            Assert.Equal(richText, snapshot.RichText.ToArray());
            Assert.NotNull(snapshot.Bitmap);
            Assert.Equal(1, snapshot.Bitmap.Width);
            Assert.Equal(1, snapshot.Bitmap.Height);
            Assert.Equal(32, snapshot.Bitmap.BitsPerPixel);
            Assert.Equal([temporaryFile], snapshot.FilePaths);
            Assert.True(snapshot.IsFromCurrentApplication);
            Assert.Equal(Environment.ProcessId, snapshot.Source.ProcessId);
            Assert.Equal(ClipboardSourceAccessStatus.Identified, snapshot.Source.AccessStatus);
            Assert.Equal(
                ClipboardSourceAttributionKind.ClipboardOwnerAtRead,
                snapshot.Source.AttributionKind);
            Assert.Contains(snapshot.Formats, format => format.Name == "HTML Format");
            Assert.Contains(snapshot.Formats, format => format.Name == "Rich Text Format");
            Assert.Contains(snapshot.Formats, format => format.Name == "CF_HDROP");
        }
        finally
        {
            File.Delete(temporaryFile);
        }
    }

    [WindowsFact]
    public async Task SelfWriteDoesNotProduceMonitorEvent()
    {
        await using WindowsClipboardAdapter adapter = new();
        using CancellationTokenSource watchCancellation = new();
        await using IAsyncEnumerator<ClipboardChangedEvent> enumerator =
            adapter.WatchAsync(watchCancellation.Token).GetAsyncEnumerator();

        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();
        await WaitForMessageWindowAsync(adapter, CancellationToken.None);
        ClipboardWriteResult write = await adapter.WritePlainTextAsync(
            $"SnapBoard feedback {Guid.NewGuid():N}",
            CancellationToken.None);

        Assert.Equal(ClipboardWriteStatus.Success, write.Status);
        Task completed = await Task.WhenAny(moveNext, Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.NotSame(moveNext, completed);

        watchCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await moveNext);
    }

    [WindowsFact]
    public async Task ReadsAnsiTextFromCfTextClipboardFormat()
    {
        await using WindowsClipboardAdapter adapter = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        ClipboardWriteResult initialWrite = await adapter.WritePlainTextAsync(
            "prime clipboard owner",
            cancellation.Token);
        Assert.Equal(ClipboardWriteStatus.Success, initialWrite.Status);

        string expected = $"SnapBoard ANSI {Guid.NewGuid():N}";
        WriteAnsiText(adapter.MessageWindowHandle, expected, cancellation.Token);

        uint sequence = WindowsNativeMethods.GetClipboardSequenceNumber();
        ClipboardReadResult read = await adapter.ReadAsync(
            new ClipboardChangedEvent(sequence, DateTimeOffset.UtcNow),
            cancellation.Token);

        ClipboardContentSnapshot snapshot = Assert.IsType<ClipboardContentSnapshot>(read.Snapshot);
        Assert.Equal(expected, snapshot.Text);
        Assert.Contains(snapshot.Formats, format => format.Name == "CF_TEXT");
    }

    [WindowsFact]
    public async Task AggregatePayloadBudgetRejectsMultiFormatWrite()
    {
        await using WindowsClipboardAdapter adapter = new(new WindowsClipboardOptions
        {
            MaximumPayloadBytes = 64,
        });
        ClipboardWriteRequest request = new()
        {
            Html = new byte[32],
            RichText = new byte[32],
        };

        ClipboardWriteResult result = await adapter.WriteAsync(request, CancellationToken.None);

        Assert.Equal(ClipboardWriteStatus.InvalidContent, result.Status);
    }

    [WindowsFact]
    public async Task RoundTripsRegisteredPngWhenDibIsUnavailable()
    {
        await using WindowsClipboardAdapter adapter = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        ClipboardWriteRequest request = new()
        {
            Bitmap = new ClipboardBitmapData(
                ClipboardBitmapEncoding.PortableNetworkGraphics,
                png,
                1,
                1,
                16),
        };

        ClipboardWriteResult write = await adapter.WriteAsync(request, cancellation.Token);
        ClipboardReadResult read = await adapter.ReadAsync(
            new ClipboardChangedEvent(write.SequenceNumber, DateTimeOffset.UtcNow),
            cancellation.Token);

        ClipboardContentSnapshot snapshot = Assert.IsType<ClipboardContentSnapshot>(read.Snapshot);
        Assert.Equal(ClipboardWriteStatus.Success, write.Status);
        Assert.Equal(ClipboardReadStatus.Success, read.Status);
        Assert.NotNull(snapshot.Bitmap);
        Assert.Equal(ClipboardBitmapEncoding.PortableNetworkGraphics, snapshot.Bitmap.Encoding);
        Assert.Equal(png, snapshot.Bitmap.Data.ToArray());
        Assert.Equal(1, snapshot.Bitmap.Width);
        Assert.Equal(1, snapshot.Bitmap.Height);
        Assert.Equal(16, snapshot.Bitmap.BitsPerPixel);
        Assert.Contains(snapshot.Formats, format => format.Name == "PNG");
    }

    private static async Task WaitForMessageWindowAsync(
        WindowsClipboardAdapter adapter,
        CancellationToken cancellationToken)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (adapter.MessageWindowHandle == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timeout.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("Windows clipboard message window did not start.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    private static ClipboardBitmapData CreateOnePixelBitmap()
    {
        byte[] dib = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(20), 4);
        dib[40] = 0x33;
        dib[41] = 0x66;
        dib[42] = 0x99;
        dib[43] = 0xFF;
        return new ClipboardBitmapData(
            ClipboardBitmapEncoding.DeviceIndependentBitmap,
            dib,
            1,
            1,
            32);
    }

    private static void WriteAnsiText(
        nint ownerWindow,
        string text,
        CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.ASCII.GetBytes(text + '\0');
        nint memoryHandle = WindowsNativeMethods.GlobalAlloc(
            WindowsNativeConstants.GlobalMemoryMoveable |
            WindowsNativeConstants.GlobalMemoryZeroInitialize,
            (nuint)payload.Length);
        if (memoryHandle == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        bool clipboardOpen = false;
        try
        {
            nint memory = WindowsNativeMethods.GlobalLock(memoryHandle);
            if (memory == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                Marshal.Copy(payload, 0, memory, payload.Length);
            }
            finally
            {
                WindowsNativeMethods.GlobalUnlock(memoryHandle);
            }

            WindowsClipboardSettings settings = new WindowsClipboardOptions().ToSettings();
            if (!ClipboardRetryPolicy.Try(
                    () => WindowsNativeMethods.OpenClipboard(ownerWindow),
                    settings.OpenRetryDelays,
                    cancellationToken))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            clipboardOpen = true;
            if (!WindowsNativeMethods.EmptyClipboard())
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            nint result = WindowsNativeMethods.SetClipboardData(
                WindowsNativeConstants.ClipboardFormatText,
                memoryHandle);
            if (result == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            memoryHandle = 0;
        }
        finally
        {
            if (clipboardOpen)
            {
                WindowsNativeMethods.CloseClipboard();
            }

            if (memoryHandle != 0)
            {
                WindowsNativeMethods.GlobalFree(memoryHandle);
            }
        }
    }
}

[CollectionDefinition(
    WindowsClipboardNativeIntegrationTests.CollectionName,
    DisableParallelization = true)]
public sealed class WindowsClipboardNativeIntegrationFixture;
