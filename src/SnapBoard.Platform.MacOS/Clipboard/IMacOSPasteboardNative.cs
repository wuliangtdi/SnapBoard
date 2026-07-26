using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Platform.MacOS.Clipboard;

internal interface IMacOSPasteboardNative
{
    long GetChangeCount();

    ClipboardReadResult Read(ClipboardChangedEvent change);

    ClipboardWriteResult Write(ClipboardWriteRequest request);
}
