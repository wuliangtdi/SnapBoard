using System.Buffers.Binary;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.MacOS.Clipboard;

namespace SnapBoard.Platform.MacOS.Tests;

public sealed class ImageMetadataReaderTests
{
    [Fact]
    public void ReadsPngHeaderWithoutDecodingImage()
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        (int width, int height, ushort bitsPerPixel) = ImageMetadataReader.Read(
            ClipboardBitmapEncoding.PortableNetworkGraphics,
            png);

        Assert.Equal(1, width);
        Assert.Equal(1, height);
        Assert.Equal(16, bitsPerPixel);
    }

    [Fact]
    public void ReadsLittleEndianTiffDirectory()
    {
        byte[] tiff = CreateMinimalTiff();

        (int width, int height, ushort bitsPerPixel) = ImageMetadataReader.Read(
            ClipboardBitmapEncoding.TaggedImageFileFormat,
            tiff);

        Assert.Equal(1, width);
        Assert.Equal(1, height);
        Assert.Equal(8, bitsPerPixel);
    }

    internal static byte[] CreateMinimalTiff()
    {
        byte[] data = new byte[8 + 2 + (3 * 12) + 4];
        data[0] = (byte)'I';
        data[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 3);
        WriteEntry(data.AsSpan(10, 12), 256, 4, 1, 1);
        WriteEntry(data.AsSpan(22, 12), 257, 4, 1, 1);
        WriteEntry(data.AsSpan(34, 12), 258, 3, 1, 8);
        return data;
    }

    private static void WriteEntry(
        Span<byte> entry,
        ushort tag,
        ushort type,
        uint count,
        uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(entry, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], type);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], count);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], value);
    }
}
