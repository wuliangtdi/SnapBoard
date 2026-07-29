using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Clipboard;

/// <summary>
/// 读取剪贴板变化发生时的最佳努力进程身份。NSPasteboard 不可靠暴露 owner PID，
/// 因此调用方只能传入变化时的前台 PID，并在使用前校验 changeCount 序列。
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOSClipboardSourceReader : IMacOSClipboardSourceReader
{
    private readonly nint _executableUrlSelector;
    private readonly nint _localizedNameSelector;
    private readonly nint _pathSelector;
    private readonly nint _runningApplicationClass;
    private readonly nint _runningApplicationWithProcessIdentifierSelector;

    public MacOSClipboardSourceReader()
    {
        MacOSAppKit.EnsureInitialized();
        _runningApplicationClass = ObjectiveC.GetRequiredClass("NSRunningApplication");
        _executableUrlSelector = ObjectiveC.GetSelector("executableURL");
        _localizedNameSelector = ObjectiveC.GetSelector("localizedName");
        _pathSelector = ObjectiveC.GetSelector("path");
        _runningApplicationWithProcessIdentifierSelector =
            ObjectiveC.GetSelector("runningApplicationWithProcessIdentifier:");
    }

    public ClipboardSourceInfo Read(
        int? processId,
        ClipboardSourceAttributionKind attributionKind)
    {
        if (processId is not > 0)
        {
            return CreateUnknownSource();
        }

        try
        {
            nint application = MacOSNativeMethods.SendIntPtrWithInt32(
                _runningApplicationClass,
                _runningApplicationWithProcessIdentifierSelector,
                processId.Value);
            if (application == 0)
            {
                return CreateUnknownSource();
            }

            string? localizedName = ObjectiveC.ToManagedString(
                MacOSNativeMethods.SendIntPtr(application, _localizedNameSelector));
            nint executableUrl = MacOSNativeMethods.SendIntPtr(
                application,
                _executableUrlSelector);
            string? executablePath = executableUrl == 0
                ? null
                : ObjectiveC.ToManagedString(
                    MacOSNativeMethods.SendIntPtr(executableUrl, _pathSelector));
            localizedName = Normalize(localizedName);
            executablePath = Normalize(executablePath);
            if (localizedName is null && executablePath is not null)
            {
                localizedName = Normalize(Path.GetFileNameWithoutExtension(executablePath));
            }

            if (localizedName is null)
            {
                return CreateUnknownSource();
            }

            return new ClipboardSourceInfo(
                processId,
                localizedName,
                executablePath,
                executablePath is null
                    ? ClipboardSourceAccessStatus.PathUnavailable
                    : ClipboardSourceAccessStatus.Identified,
                AttributionKind: attributionKind);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            // PID 过期或 AppKit 查询失败属于正常降级，不得中断剪贴板监控。
            return CreateUnknownSource();
        }
    }

    private static ClipboardSourceInfo CreateUnknownSource() => new(
        null,
        null,
        null,
        ClipboardSourceAccessStatus.Unknown);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal interface IMacOSClipboardSourceReader
{
    ClipboardSourceInfo Read(
        int? processId,
        ClipboardSourceAttributionKind attributionKind);
}
