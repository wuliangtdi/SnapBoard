using System.Runtime.Versioning;
using System.Text;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.MacOS.Clipboard;

namespace SnapBoard.Platform.MacOS.Tests;

[Collection(CollectionName)]
[SupportedOSPlatform("macos")]
public sealed class MacOSClipboardNativeIntegrationTests
{
    public const string CollectionName = "macOS clipboard native integration";

    [MacOSFact]
    public async Task RoundTripsTextHtmlRtfPngFileUrlAndUtiList()
    {
        await using MacOSClipboardAdapter adapter = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        string firstTemporaryFile = Path.GetTempFileName();
        string secondTemporaryFile = Path.GetTempFileName();

        try
        {
            byte[] html = Encoding.UTF8.GetBytes("<b>SnapBoard HTML</b>");
            byte[] richText = Encoding.ASCII.GetBytes(@"{\rtf1\ansi SnapBoard RTF}");
            byte[] png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            ClipboardWriteRequest request = new()
            {
                Text = "SnapBoard macOS 文本",
                Html = html,
                RichText = richText,
                Bitmap = new ClipboardBitmapData(
                    ClipboardBitmapEncoding.PortableNetworkGraphics,
                    png,
                    1,
                    1,
                    16),
                FilePaths = [firstTemporaryFile, secondTemporaryFile],
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
            Assert.Equal(png, Assert.IsType<ClipboardBitmapData>(snapshot.Bitmap).Data.ToArray());
            Assert.Equal(ClipboardBitmapEncoding.PortableNetworkGraphics, snapshot.Bitmap.Encoding);
            Assert.Equal([firstTemporaryFile, secondTemporaryFile], snapshot.FilePaths);
            Assert.True(snapshot.IsFromCurrentApplication);
            Assert.Equal(ClipboardSourceAccessStatus.Unknown, snapshot.Source.AccessStatus);
            Assert.Contains(snapshot.Formats, format => format.Name == "public.utf8-plain-text");
            Assert.Contains(snapshot.Formats, format => format.Name == "public.html");
            Assert.Contains(snapshot.Formats, format => format.Name == "public.rtf");
            Assert.Contains(snapshot.Formats, format => format.Name == "public.png");
            Assert.Contains(snapshot.Formats, format => format.Name == "public.file-url");
            Assert.Contains(
                snapshot.Formats,
                format => format.Name == "com.wuliangtdi.snapboard.source.v1");
        }
        finally
        {
            File.Delete(firstTemporaryFile);
            File.Delete(secondTemporaryFile);
        }
    }

    [MacOSFact]
    public async Task RejectsWindowsDibWithoutClearingClipboard()
    {
        await using MacOSClipboardAdapter adapter = new();
        ClipboardWriteResult initial = await adapter.WritePlainTextAsync(
            "content must survive invalid write",
            CancellationToken.None);
        ClipboardWriteRequest invalid = new()
        {
            Bitmap = new ClipboardBitmapData(
                ClipboardBitmapEncoding.DeviceIndependentBitmap,
                new byte[40],
                1,
                1,
                32),
        };

        ClipboardWriteResult rejected = await adapter.WriteAsync(invalid, CancellationToken.None);
        ClipboardReadResult read = await adapter.ReadAsync(
            new ClipboardChangedEvent(initial.SequenceNumber, DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(ClipboardWriteStatus.InvalidContent, rejected.Status);
        Assert.Equal("content must survive invalid write", read.Snapshot?.Text);
    }

    [MacOSFact]
    public async Task RoundTripsTiffWithoutTreatingItAsDib()
    {
        await using MacOSClipboardAdapter adapter = new();
        byte[] tiff = ImageMetadataReaderTests.CreateMinimalTiff();
        ClipboardWriteRequest request = new()
        {
            Bitmap = new ClipboardBitmapData(
                ClipboardBitmapEncoding.TaggedImageFileFormat,
                tiff,
                1,
                1,
                8),
        };

        ClipboardWriteResult write = await adapter.WriteAsync(request, CancellationToken.None);
        ClipboardReadResult read = await adapter.ReadAsync(
            new ClipboardChangedEvent(write.SequenceNumber, DateTimeOffset.UtcNow),
            CancellationToken.None);

        ClipboardBitmapData bitmap = Assert.IsType<ClipboardBitmapData>(read.Snapshot?.Bitmap);
        Assert.Equal(ClipboardWriteStatus.Success, write.Status);
        Assert.Equal(ClipboardBitmapEncoding.TaggedImageFileFormat, bitmap.Encoding);
        Assert.Equal(tiff, bitmap.Data.ToArray());
    }

    [MacOSFact]
    public void SourceHintIsUsedOnlyWhileItsSequenceMatchesThePasteboard()
    {
        FakeMacOSClipboardSourceReader sourceReader = new();
        MacOSClipboardOptions options = new();
        MacOSPasteboardNative native = new(
            options.ToSettings(),
            new MacOSClipboardOriginMarker(),
            sourceReader);
        ClipboardWriteResult write = native.Write(new ClipboardWriteRequest
        {
            Text = $"SnapBoard source hint {Guid.NewGuid():N}",
        });

        ClipboardReadResult matching = native.Read(new ClipboardChangedEvent(
            write.SequenceNumber,
            DateTimeOffset.UtcNow,
            new ClipboardSourceProcessHint(ForegroundProcessId: 101)));
        ClipboardContentSnapshot matchingSnapshot =
            Assert.IsType<ClipboardContentSnapshot>(matching.Snapshot);

        Assert.Equal(sourceReader.Result, matchingSnapshot.Source);
        Assert.Equal(1, sourceReader.CallCount);
        Assert.Equal(101, sourceReader.ProcessId);
        Assert.Equal(
            ClipboardSourceAttributionKind.ForegroundWindowAtChange,
            sourceReader.AttributionKind);

        ClipboardReadResult stale = native.Read(new ClipboardChangedEvent(
            write.SequenceNumber + 1,
            DateTimeOffset.UtcNow,
            new ClipboardSourceProcessHint(ForegroundProcessId: 202)));
        ClipboardContentSnapshot staleSnapshot =
            Assert.IsType<ClipboardContentSnapshot>(stale.Snapshot);

        Assert.Equal(ClipboardSourceAccessStatus.Unknown, staleSnapshot.Source.AccessStatus);
        Assert.Equal(ClipboardSourceAttributionKind.Unknown, staleSnapshot.Source.AttributionKind);
        Assert.Equal(1, sourceReader.CallCount);
    }

    [MacOSFact]
    public async Task ListenerPreservesBestEffortForegroundIdentityOrUnknownFallback()
    {
        await using MacOSClipboardAdapter listener = new();
        await using MacOSClipboardAdapter writer = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<ClipboardChangedEvent> enumerator =
            listener.WatchAsync(cancellation.Token).GetAsyncEnumerator();

        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(200, cancellation.Token);
        string text = $"SnapBoard listener {Guid.NewGuid():N}";
        ClipboardWriteResult write = await writer.WritePlainTextAsync(text, cancellation.Token);

        Assert.Equal(ClipboardWriteStatus.Success, write.Status);
        Assert.True(await moveNext.WaitAsync(cancellation.Token));
        ClipboardReadResult read = await listener.ReadAsync(enumerator.Current, cancellation.Token);
        ClipboardContentSnapshot snapshot = Assert.IsType<ClipboardContentSnapshot>(read.Snapshot);
        Assert.Equal(text, snapshot.Text);
        Assert.False(snapshot.IsFromCurrentApplication);
        if (snapshot.Source.AccessStatus == ClipboardSourceAccessStatus.Unknown)
        {
            Assert.Null(snapshot.Source.ProcessId);
            Assert.Null(snapshot.Source.ProcessName);
            Assert.Null(snapshot.Source.ExecutablePath);
            Assert.Equal(
                ClipboardSourceAttributionKind.Unknown,
                snapshot.Source.AttributionKind);
        }
        else
        {
            Assert.True(snapshot.Source.AccessStatus is
                ClipboardSourceAccessStatus.Identified or
                ClipboardSourceAccessStatus.PathUnavailable);
            Assert.True(snapshot.Source.ProcessId is > 0);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Source.ProcessName));
            Assert.Equal(
                ClipboardSourceAttributionKind.ForegroundWindowAtChange,
                snapshot.Source.AttributionKind);
        }
    }

    [MacOSFact]
    public async Task SelfWriteDoesNotProduceMonitorEvent()
    {
        await using MacOSClipboardAdapter adapter = new();
        using CancellationTokenSource cancellation = new();
        await using IAsyncEnumerator<ClipboardChangedEvent> enumerator =
            adapter.WatchAsync(cancellation.Token).GetAsyncEnumerator();

        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(200);
        ClipboardWriteResult write = await adapter.WritePlainTextAsync(
            $"SnapBoard feedback {Guid.NewGuid():N}",
            CancellationToken.None);

        Assert.Equal(ClipboardWriteStatus.Success, write.Status);
        Task completed = await Task.WhenAny(moveNext, Task.Delay(TimeSpan.FromMilliseconds(800)));
        Assert.NotSame(moveNext, completed);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await moveNext);
    }
}

[CollectionDefinition(MacOSClipboardNativeIntegrationTests.CollectionName, DisableParallelization = true)]
public sealed class MacOSClipboardNativeIntegrationGroup;
