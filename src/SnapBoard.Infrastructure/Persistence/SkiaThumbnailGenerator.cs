using System.Buffers.Binary;
using System.Runtime.InteropServices;
using BitMiracle.LibTiff.Classic;
using SkiaSharp;
using SnapBoard.Application.Clipboard;
using SnapBoard.Domain.Clipboard;

namespace SnapBoard.Infrastructure.Persistence;

internal static class SkiaThumbnailGenerator
{
    private const int MaximumWidth = 320;
    private const int MaximumHeight = 180;
    private const int MaximumDecodedPixels = 40_000_000;

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
            using SKBitmap? source = representation.BitmapEncoding ==
                ClipboardStoredBitmapEncoding.TaggedImageFileFormat
                ? DecodeTiff(encoded)
                : SKBitmap.Decode(encoded);
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
        catch (InvalidDataException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
        catch (InvalidOperationException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
        catch (OverflowException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
        finally
        {
            Array.Clear(encoded);
        }
    }

    private static SKBitmap? DecodeTiff(byte[] encoded)
    {
        using MemoryStream stream = new(encoded, writable: false);
        using Tiff? tiff = Tiff.ClientOpen(
            "clipboard-thumbnail.tiff",
            "r",
            stream,
            new TiffStream());
        if (tiff is null)
        {
            return null;
        }

        FieldValue[]? widthField = tiff.GetField(TiffTag.IMAGEWIDTH);
        FieldValue[]? heightField = tiff.GetField(TiffTag.IMAGELENGTH);
        if (widthField is not { Length: > 0 } || heightField is not { Length: > 0 })
        {
            return null;
        }

        int width = widthField[0].ToInt();
        int height = heightField[0].ToInt();
        int pixelCount = checked(width * height);
        if (width <= 0 || height <= 0 || pixelCount > MaximumDecodedPixels)
        {
            return null;
        }

        int[] raster = new int[pixelCount];
        try
        {
            if (!tiff.ReadRGBAImageOriented(width, height, raster, Orientation.TOPLEFT))
            {
                return null;
            }

            SKBitmap bitmap = new(new SKImageInfo(
                width,
                height,
                SKColorType.Rgba8888,
                SKAlphaType.Unpremul));
            nint pixels = bitmap.GetPixels();
            if (pixels == 0)
            {
                bitmap.Dispose();
                return null;
            }

            // LibTiff 在小端平台返回内存顺序 RGBA 的 ABGR 整数，可直接复制给 Skia。
            Marshal.Copy(raster, 0, pixels, raster.Length);
            return bitmap;
        }
        finally
        {
            Array.Clear(raster);
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
