using System.Buffers.Binary;
using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Platform.MacOS.Clipboard;

internal static class ImageMetadataReader
{
    private static ReadOnlySpan<byte> PngSignature =>
        [137, 80, 78, 71, 13, 10, 26, 10];

    public static (int Width, int Height, ushort BitsPerPixel) Read(
        ClipboardBitmapEncoding encoding,
        ReadOnlySpan<byte> data) => encoding switch
        {
            ClipboardBitmapEncoding.PortableNetworkGraphics => ReadPng(data),
            ClipboardBitmapEncoding.TaggedImageFileFormat => ReadTiff(data),
            _ => default,
        };

    private static (int Width, int Height, ushort BitsPerPixel) ReadPng(
        ReadOnlySpan<byte> data)
    {
        if (data.Length < 29 || !data[..8].SequenceEqual(PngSignature) ||
            !data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return default;
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        byte bitDepth = data[24];
        int channels = data[25] switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => 0,
        };

        if (width > int.MaxValue || height > int.MaxValue || channels == 0)
        {
            return default;
        }

        int bitsPerPixel = bitDepth * channels;
        return ((int)width, (int)height, (ushort)Math.Min(bitsPerPixel, ushort.MaxValue));
    }

    private static (int Width, int Height, ushort BitsPerPixel) ReadTiff(
        ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            return default;
        }

        bool littleEndian;
        if (data[0] == (byte)'I' && data[1] == (byte)'I')
        {
            littleEndian = true;
        }
        else if (data[0] == (byte)'M' && data[1] == (byte)'M')
        {
            littleEndian = false;
        }
        else
        {
            return default;
        }

        if (ReadUInt16(data.Slice(2, 2), littleEndian) != 42)
        {
            return default;
        }

        uint directoryOffset = ReadUInt32(data.Slice(4, 4), littleEndian);
        if (directoryOffset > int.MaxValue || (ulong)directoryOffset + 2UL > (ulong)data.Length)
        {
            return default;
        }

        int offset = (int)directoryOffset;
        ushort entryCount = ReadUInt16(data.Slice(offset, 2), littleEndian);
        offset += 2;

        uint width = 0;
        uint height = 0;
        uint samplesPerPixel = 1;
        uint bitsPerSample = 0;
        uint bitsPerSampleCount = 0;

        for (int index = 0; index < entryCount; index++)
        {
            int entryOffset = offset + (index * 12);
            if (entryOffset < 0 || entryOffset + 12 > data.Length)
            {
                break;
            }

            ReadOnlySpan<byte> entry = data.Slice(entryOffset, 12);
            ushort tag = ReadUInt16(entry[..2], littleEndian);
            ushort type = ReadUInt16(entry.Slice(2, 2), littleEndian);
            uint count = ReadUInt32(entry.Slice(4, 4), littleEndian);

            switch (tag)
            {
                case 256:
                    width = ReadFirstValue(data, entry, type, count, littleEndian);
                    break;
                case 257:
                    height = ReadFirstValue(data, entry, type, count, littleEndian);
                    break;
                case 258:
                    bitsPerSample = SumValues(data, entry, type, count, littleEndian);
                    bitsPerSampleCount = count;
                    break;
                case 277:
                    samplesPerPixel = ReadFirstValue(data, entry, type, count, littleEndian);
                    break;
            }
        }

        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            return default;
        }

        if (bitsPerSample == 0)
        {
            bitsPerSample = 8 * Math.Max(samplesPerPixel, 1);
        }
        else if (bitsPerSampleCount == 1 && samplesPerPixel > 1 && bitsPerSample <= 64)
        {
            // 单个 BitsPerSample 值通常表示所有通道采用相同位宽。
            bitsPerSample *= samplesPerPixel;
        }

        return (
            (int)width,
            (int)height,
            (ushort)Math.Min(bitsPerSample, ushort.MaxValue));
    }

    private static uint ReadFirstValue(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> entry,
        ushort type,
        uint count,
        bool littleEndian)
    {
        ReadOnlySpan<byte> values = ResolveValues(data, entry, type, count, littleEndian);
        return ReadValue(values, type, littleEndian);
    }

    private static uint SumValues(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> entry,
        ushort type,
        uint count,
        bool littleEndian)
    {
        ReadOnlySpan<byte> values = ResolveValues(data, entry, type, count, littleEndian);
        int valueSize = GetTypeSize(type);
        if (valueSize == 0 || values.IsEmpty)
        {
            return 0;
        }

        uint sum = 0;
        int readableCount = (int)Math.Min(count, (uint)(values.Length / valueSize));
        for (int index = 0; index < readableCount; index++)
        {
            sum += ReadValue(values.Slice(index * valueSize, valueSize), type, littleEndian);
        }

        return sum;
    }

    private static ReadOnlySpan<byte> ResolveValues(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> entry,
        ushort type,
        uint count,
        bool littleEndian)
    {
        int valueSize = GetTypeSize(type);
        if (valueSize == 0 || count == 0 || count > int.MaxValue / valueSize)
        {
            return [];
        }

        int byteCount = (int)count * valueSize;
        if (byteCount <= 4)
        {
            return entry.Slice(8, byteCount);
        }

        uint valueOffset = ReadUInt32(entry.Slice(8, 4), littleEndian);
        if (valueOffset > int.MaxValue ||
            (ulong)valueOffset + (uint)byteCount > (ulong)data.Length)
        {
            return [];
        }

        return data.Slice((int)valueOffset, byteCount);
    }

    private static uint ReadValue(ReadOnlySpan<byte> data, ushort type, bool littleEndian) =>
        type switch
        {
            1 when !data.IsEmpty => data[0],
            3 when data.Length >= 2 => ReadUInt16(data[..2], littleEndian),
            4 when data.Length >= 4 => ReadUInt32(data[..4], littleEndian),
            _ => 0,
        };

    private static int GetTypeSize(ushort type) => type switch
    {
        1 => 1,
        3 => 2,
        4 => 4,
        _ => 0,
    };

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(data)
        : BinaryPrimitives.ReadUInt16BigEndian(data);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(data)
        : BinaryPrimitives.ReadUInt32BigEndian(data);
}
