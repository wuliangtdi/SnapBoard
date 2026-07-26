namespace SnapBoard.Platform.MacOS.Clipboard;

internal static class MacOSClipboardSequence
{
    public static ulong ToPublicSequence(long changeCount) => unchecked((ulong)changeCount);
}
