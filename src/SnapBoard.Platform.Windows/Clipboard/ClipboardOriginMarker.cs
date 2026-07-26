using System.Text;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Clipboard;

internal sealed class ClipboardOriginMarker
{
    public const string FormatName = "SnapBoard.Source.v1";

    private readonly object _gate = new();
    private readonly byte[] _payload = Encoding.ASCII.GetBytes($"SnapBoard/1/{Guid.NewGuid():N}");
    private uint _formatId;

    public ReadOnlyMemory<byte> Payload => _payload;

    public uint GetFormatId()
    {
        uint existing = Volatile.Read(ref _formatId);
        if (existing != 0)
        {
            return existing;
        }

        lock (_gate)
        {
            if (_formatId == 0)
            {
                _formatId = WindowsNativeMethods.RegisterClipboardFormat(FormatName);
            }

            return _formatId;
        }
    }

    public bool Matches(ReadOnlySpan<byte> payload)
    {
        while (!payload.IsEmpty && payload[^1] == 0)
        {
            payload = payload[..^1];
        }

        return payload.SequenceEqual(_payload);
    }
}
