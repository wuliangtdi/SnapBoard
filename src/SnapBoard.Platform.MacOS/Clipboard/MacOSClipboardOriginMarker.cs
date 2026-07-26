using System.Text;

namespace SnapBoard.Platform.MacOS.Clipboard;

internal sealed class MacOSClipboardOriginMarker
{
    public const string TypeName = "com.wuliangtdi.snapboard.source.v1";

    private readonly byte[] _payload = Encoding.ASCII.GetBytes($"SnapBoard/1/{Guid.NewGuid():N}");

    public ReadOnlyMemory<byte> Payload => _payload;

    public bool Matches(ReadOnlySpan<byte> payload) => payload.SequenceEqual(_payload);
}
