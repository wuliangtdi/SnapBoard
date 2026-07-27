using System.Buffers.Binary;
using SkiaSharp;
using SnapBoard.Application.Clipboard;
using SnapBoard.Domain.Clipboard;

namespace SnapBoard.Infrastructure.Persistence;

internal static class SkiaThumbnailGenerator
{
    private const int MaximumWidth = 320;
    private const int MaximumHeight = 180;

    public static ValueTask<ReadOnlyMemory<byte>> GenerateAsync(
        ClipboardCapturedRepresentation representation,
        CancellationToken cancellationToken)
    {
        if (representation.Kind != ClipboardContentKind.Image ||
            representation.Data.IsEmpty)
        {
            return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
        }

        return new ValueTask<ReadOnlyMemory<byte>>(Task.Run(
            () => GenerateCore(representation, cancellationToken),
            cancellationToken));
    }

    private static ReadOnlyMemory<byte> GenerateCore(
        ClipboardCapturedRepresentation representation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] encoded = representation.BitmapEncoding is
            ClipboardStoredBitmapEncoding.DeviceIndependentBitmap or
            ClipboardStoredBitmapEncoding.DeviceIndependentBitmapV5
            ? AddBitmapFileHeader(representation.Data.Span)
            : representation.Data.ToArray();
        try
        {
            using SKBitmap? source = SKBitmap.Decode(encoded);
            if (source is null || source.Width <= 0 || source.Height <= 0)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            double scale = Math.Min(
                1d,
                Math.Min((double)MaximumWidth / source.Width, (double)MaximumHeight / source.Height));
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            using SKSurface? surface = SKSurface.Create(new SKImageInfo(
                width,
                height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul));
            if (surface is null)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.DrawBitmap(source, new SKRect(0, 0, width, height));
            surface.Canvas.Flush();
            using SKImage image = surface.Snapshot();
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);
            return data.ToArray();
        }
        catch (ArgumentException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
        finally
        {
            Array.Clear(encoded);
        }
    }

    private static byte[] AddBitmapFileHeader(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40)
        {
            return dib.ToArray();
        }

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        if (headerSize < 40 || headerSize > dib.Length)
        {
            return dib.ToArray();
        }

        ushort bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        uint colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        long paletteEntries = colorsUsed != 0
            ? colorsUsed
            : bitsPerPixel <= 8 ? 1L << bitsPerPixel : 0;
        long maskBytes = headerSize == 40 && compression is 3 or 6
            ? compression == 6 ? 16 : 12
            : 0;
        long pixelOffset = 14L + headerSize + (paletteEntries * 4) + maskBytes;
        if (pixelOffset > int.MaxValue || pixelOffset > dib.Length + 14L)
        {
            return dib.ToArray();
        }

        byte[] bitmap = new byte[dib.Length + 14];
        bitmap[0] = (byte)'B';
        bitmap[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bitmap.AsSpan(2), checked((uint)bitmap.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bitmap.AsSpan(10), checked((uint)pixelOffset));
        dib.CopyTo(bitmap.AsSpan(14));
        return bitmap;
    }
}
