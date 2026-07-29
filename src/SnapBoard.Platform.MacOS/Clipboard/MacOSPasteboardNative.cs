using System.Runtime.Versioning;
using System.Text;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Clipboard;

[SupportedOSPlatform("macos")]
internal sealed class MacOSPasteboardNative : IMacOSPasteboardNative
{
    private const string FileUrlType = "public.file-url";
    private const string HtmlType = "public.html";
    private const string LegacyFileNamesType = "NSFilenamesPboardType";
    private const string PngType = "public.png";
    private const string RichTextType = "public.rtf";
    private const string TextType = "public.utf8-plain-text";
    private const string TiffType = "public.tiff";
    private const int MaximumTypeCount = 4096;

    private readonly MacOSClipboardSettings _settings;
    private readonly MacOSClipboardOriginMarker _originMarker;
    private readonly IMacOSClipboardSourceReader _sourceReader;
    private readonly nint _mutableArrayClass;
    private readonly nint _pasteboardClass;
    private readonly nint _pasteboardItemClass;
    private readonly nint _urlClass;
    private readonly nint _addObjectSelector;
    private readonly nint _changeCountSelector;
    private readonly nint _clearContentsSelector;
    private readonly nint _dataForTypeSelector;
    private readonly nint _fileUrlWithPathSelector;
    private readonly nint _generalPasteboardSelector;
    private readonly nint _initSelector;
    private readonly nint _initWithCapacitySelector;
    private readonly nint _objectAtIndexSelector;
    private readonly nint _pasteboardItemsSelector;
    private readonly nint _propertyListForTypeSelector;
    private readonly nint _setDataForTypeSelector;
    private readonly nint _setStringForTypeSelector;
    private readonly nint _stringForTypeSelector;
    private readonly nint _typesSelector;
    private readonly nint _writeObjectsSelector;

    public MacOSPasteboardNative(
        MacOSClipboardSettings settings,
        MacOSClipboardOriginMarker originMarker)
        : this(settings, originMarker, new MacOSClipboardSourceReader())
    {
    }

    internal MacOSPasteboardNative(
        MacOSClipboardSettings settings,
        MacOSClipboardOriginMarker originMarker,
        IMacOSClipboardSourceReader sourceReader)
    {
        MacOSAppKit.EnsureInitialized();

        _settings = settings;
        _originMarker = originMarker;
        _sourceReader = sourceReader ?? throw new ArgumentNullException(nameof(sourceReader));
        _mutableArrayClass = ObjectiveC.GetRequiredClass("NSMutableArray");
        _pasteboardClass = ObjectiveC.GetRequiredClass("NSPasteboard");
        _pasteboardItemClass = ObjectiveC.GetRequiredClass("NSPasteboardItem");
        _urlClass = ObjectiveC.GetRequiredClass("NSURL");
        _addObjectSelector = ObjectiveC.GetSelector("addObject:");
        _changeCountSelector = ObjectiveC.GetSelector("changeCount");
        _clearContentsSelector = ObjectiveC.GetSelector("clearContents");
        _dataForTypeSelector = ObjectiveC.GetSelector("dataForType:");
        _fileUrlWithPathSelector = ObjectiveC.GetSelector("fileURLWithPath:isDirectory:");
        _generalPasteboardSelector = ObjectiveC.GetSelector("generalPasteboard");
        _initSelector = ObjectiveC.GetSelector("init");
        _initWithCapacitySelector = ObjectiveC.GetSelector("initWithCapacity:");
        _objectAtIndexSelector = ObjectiveC.GetSelector("objectAtIndex:");
        _pasteboardItemsSelector = ObjectiveC.GetSelector("pasteboardItems");
        _propertyListForTypeSelector = ObjectiveC.GetSelector("propertyListForType:");
        _setDataForTypeSelector = ObjectiveC.GetSelector("setData:forType:");
        _setStringForTypeSelector = ObjectiveC.GetSelector("setString:forType:");
        _stringForTypeSelector = ObjectiveC.GetSelector("stringForType:");
        _typesSelector = ObjectiveC.GetSelector("types");
        _writeObjectsSelector = ObjectiveC.GetSelector("writeObjects:");
    }

    public long GetChangeCount()
    {
        using NativeAutoreleasePool pool = new();
        nint pasteboard = GetGeneralPasteboard();
        return (long)MacOSNativeMethods.SendIntPtr(pasteboard, _changeCountSelector);
    }

    public ClipboardReadResult Read(ClipboardChangedEvent change)
    {
        try
        {
            using NativeAutoreleasePool pool = new();
            nint pasteboard = GetGeneralPasteboard();
            return ReadCore(pasteboard, change);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new ClipboardReadResult(
                ClipboardReadStatus.Failed,
                null,
                ClipboardReadFailureReason.NativeFailure);
        }
    }

    public ClipboardWriteResult Write(ClipboardWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using NativeAutoreleasePool pool = new();
            return WriteCore(GetGeneralPasteboard(), request);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new ClipboardWriteResult(ClipboardWriteStatus.Failed);
        }
    }

    private ClipboardReadResult ReadCore(nint pasteboard, ClipboardChangedEvent change)
    {
        List<ClipboardFormatDescriptor> formats = ReadFormats(pasteboard);
        HashSet<string> availableTypes = formats
            .Select(format => format.Name)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> unavailable = new(StringComparer.Ordinal);
        int remainingBytes = _settings.MaximumPayloadBytes;
        bool contentTooLarge = false;

        string? text = ReadString(
            pasteboard,
            TextType,
            availableTypes,
            unavailable,
            ref remainingBytes,
            ref contentTooLarge);
        byte[]? html = ReadData(
            pasteboard,
            HtmlType,
            availableTypes,
            unavailable,
            ref remainingBytes,
            ref contentTooLarge);
        byte[]? richText = ReadData(
            pasteboard,
            RichTextType,
            availableTypes,
            unavailable,
            ref remainingBytes,
            ref contentTooLarge);

        ClipboardBitmapData? bitmap = ReadBitmap(
            pasteboard,
            availableTypes,
            unavailable,
            ref remainingBytes,
            ref contentTooLarge);
        List<string> files = ReadFilePaths(
            pasteboard,
            availableTypes,
            unavailable,
            ref remainingBytes,
            ref contentTooLarge);
        bool isCurrentApplication = ReadOriginMarker(
            pasteboard,
            availableTypes,
            unavailable,
            ref remainingBytes,
            ref contentTooLarge);

        long currentChangeCount =
            (long)MacOSNativeMethods.SendIntPtr(pasteboard, _changeCountSelector);
        ulong sequence = currentChangeCount == 0 && change.SequenceNumber != 0
            ? change.SequenceNumber
            : MacOSClipboardSequence.ToPublicSequence(currentChangeCount);
        bool sourceHintMatchesCurrentClipboard =
            MacOSClipboardSequence.ToPublicSequence(currentChangeCount) == change.SequenceNumber;
        ClipboardSourceInfo source = sourceHintMatchesCurrentClipboard
            ? _sourceReader.Read(
                change.SourceHint.ForegroundProcessId,
                ClipboardSourceAttributionKind.ForegroundWindowAtChange)
            : CreateUnknownSource();

        ClipboardContentSnapshot snapshot = new()
        {
            SequenceNumber = sequence,
            CapturedAt = DateTimeOffset.UtcNow,
            Source = source,
            Formats = formats,
            UnavailableFormats = unavailable.ToArray(),
            Text = text,
            Html = html ?? ReadOnlyMemory<byte>.Empty,
            RichText = richText ?? ReadOnlyMemory<byte>.Empty,
            Bitmap = bitmap,
            FilePaths = files,
            IsFromCurrentApplication = isCurrentApplication,
        };

        return new ClipboardReadResult(
            unavailable.Count == 0 ? ClipboardReadStatus.Success : ClipboardReadStatus.Partial,
            snapshot,
            contentTooLarge
                ? ClipboardReadFailureReason.ContentTooLarge
                : ClipboardReadFailureReason.None);
    }

    private ClipboardWriteResult WriteCore(nint pasteboard, ClipboardWriteRequest request)
    {
        if (!request.HasContent || !ValidateRequest(request))
        {
            return new ClipboardWriteResult(ClipboardWriteStatus.InvalidContent);
        }

        nint objects = CreateMutableArray((nuint)(request.FilePaths.Count + 1));
        nint item = CreatePasteboardItem();
        if (objects == 0 || item == 0)
        {
            ObjectiveC.Release(item);
            ObjectiveC.Release(objects);
            return new ClipboardWriteResult(ClipboardWriteStatus.Failed);
        }

        bool anyFailure = false;
        int successfulContentFormats = 0;
        bool markerWritten = false;

        try
        {
            if (request.Text is not null)
            {
                RecordSetResult(
                    SetString(item, TextType, request.Text),
                    ref successfulContentFormats,
                    ref anyFailure);
            }

            if (!request.Html.IsEmpty)
            {
                RecordSetResult(
                    SetData(item, HtmlType, request.Html.Span),
                    ref successfulContentFormats,
                    ref anyFailure);
            }

            if (!request.RichText.IsEmpty)
            {
                RecordSetResult(
                    SetData(item, RichTextType, request.RichText.Span),
                    ref successfulContentFormats,
                    ref anyFailure);
            }

            if (request.Bitmap is not null)
            {
                string bitmapType = request.Bitmap.Encoding switch
                {
                    ClipboardBitmapEncoding.PortableNetworkGraphics => PngType,
                    ClipboardBitmapEncoding.TaggedImageFileFormat => TiffType,
                    _ => string.Empty,
                };
                if (bitmapType.Length == 0)
                {
                    return new ClipboardWriteResult(ClipboardWriteStatus.InvalidContent);
                }

                RecordSetResult(
                    SetData(item, bitmapType, request.Bitmap.Data.Span),
                    ref successfulContentFormats,
                    ref anyFailure);
            }

            markerWritten = SetData(
                item,
                MacOSClipboardOriginMarker.TypeName,
                _originMarker.Payload.Span);
            anyFailure |= !markerWritten;

            MacOSNativeMethods.SendVoidWithIntPtr(objects, _addObjectSelector, item);

            foreach (string path in request.FilePaths)
            {
                nint nativePath = ObjectiveC.CreateString(path);
                if (nativePath == 0)
                {
                    anyFailure = true;
                    continue;
                }

                try
                {
                    byte isDirectory = Directory.Exists(path) ? (byte)1 : (byte)0;
                    nint fileUrl = MacOSNativeMethods.SendIntPtrWithIntPtrByte(
                        _urlClass,
                        _fileUrlWithPathSelector,
                        nativePath,
                        isDirectory);
                    if (fileUrl == 0)
                    {
                        anyFailure = true;
                        continue;
                    }

                    MacOSNativeMethods.SendVoidWithIntPtr(objects, _addObjectSelector, fileUrl);
                    successfulContentFormats++;
                }
                finally
                {
                    ObjectiveC.Release(nativePath);
                }
            }

            MacOSNativeMethods.SendIntPtr(pasteboard, _clearContentsSelector);
            bool writeSucceeded = MacOSNativeMethods.SendBoolWithIntPtr(
                pasteboard,
                _writeObjectsSelector,
                objects) != 0;
            long finalChangeCount =
                (long)MacOSNativeMethods.SendIntPtr(pasteboard, _changeCountSelector);

            ClipboardWriteStatus status = !writeSucceeded || successfulContentFormats == 0
                ? ClipboardWriteStatus.Failed
                : anyFailure
                    ? ClipboardWriteStatus.Partial
                    : ClipboardWriteStatus.Success;
            return new ClipboardWriteResult(
                status,
                MacOSClipboardSequence.ToPublicSequence(finalChangeCount),
                markerWritten);
        }
        finally
        {
            // writeObjects: 会复制或保留所需对象；本进程持有的 +1 引用必须在本轮释放。
            ObjectiveC.Release(item);
            ObjectiveC.Release(objects);
        }
    }

    private bool ValidateRequest(ClipboardWriteRequest request)
    {
        if (request.FilePaths.Count > _settings.MaximumFileCount ||
            request.FilePaths.Any(path =>
                string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
        {
            return false;
        }

        long totalBytes = _originMarker.Payload.Length;
        totalBytes += request.Text is null ? 0 : Encoding.UTF8.GetByteCount(request.Text);
        totalBytes += request.Html.Length;
        totalBytes += request.RichText.Length;
        totalBytes += request.Bitmap?.Data.Length ?? 0;

        foreach (string path in request.FilePaths)
        {
            totalBytes += Encoding.UTF8.GetByteCount(path);
            if (totalBytes > _settings.MaximumPayloadBytes)
            {
                return false;
            }
        }

        if (request.Bitmap is not null &&
            request.Bitmap.Encoding is not ClipboardBitmapEncoding.PortableNetworkGraphics and
                not ClipboardBitmapEncoding.TaggedImageFileFormat)
        {
            return false;
        }

        return totalBytes <= _settings.MaximumPayloadBytes;
    }

    private List<ClipboardFormatDescriptor> ReadFormats(nint pasteboard)
    {
        nint types = MacOSNativeMethods.SendIntPtr(pasteboard, _typesSelector);
        if (types == 0)
        {
            return [];
        }

        nuint nativeCount = MacOSNativeMethods.SendNUInt(types, ObjectiveC.GetSelector("count"));
        int count = (int)Math.Min(nativeCount, MaximumTypeCount);
        List<ClipboardFormatDescriptor> formats = new(count);
        for (int index = 0; index < count; index++)
        {
            nint type = MacOSNativeMethods.SendIntPtrWithNUInt(
                types,
                _objectAtIndexSelector,
                (nuint)index);
            string? name = ObjectiveC.ToManagedString(type);
            if (!string.IsNullOrEmpty(name))
            {
                formats.Add(new ClipboardFormatDescriptor($"macos:{name}", name));
            }
        }

        return formats;
    }

    private string? ReadString(
        nint pasteboard,
        string typeName,
        HashSet<string> availableTypes,
        HashSet<string> unavailable,
        ref int remainingBytes,
        ref bool contentTooLarge)
    {
        if (!availableTypes.Contains(typeName))
        {
            return null;
        }

        nint type = ObjectiveC.CreateString(typeName);
        if (type == 0)
        {
            unavailable.Add(typeName);
            return null;
        }

        try
        {
            nint value = MacOSNativeMethods.SendIntPtrWithIntPtr(
                pasteboard,
                _stringForTypeSelector,
                type);
            string? managed = ObjectiveC.ToManagedString(value);
            if (managed is null)
            {
                unavailable.Add(typeName);
                return null;
            }

            int byteCount = Encoding.UTF8.GetByteCount(managed);
            if (byteCount > remainingBytes)
            {
                contentTooLarge = true;
                unavailable.Add(typeName);
                return null;
            }

            remainingBytes -= byteCount;
            return managed;
        }
        finally
        {
            ObjectiveC.Release(type);
        }
    }

    private byte[]? ReadData(
        nint pasteboard,
        string typeName,
        HashSet<string> availableTypes,
        HashSet<string> unavailable,
        ref int remainingBytes,
        ref bool contentTooLarge)
    {
        if (!availableTypes.Contains(typeName))
        {
            return null;
        }

        nint type = ObjectiveC.CreateString(typeName);
        if (type == 0)
        {
            unavailable.Add(typeName);
            return null;
        }

        try
        {
            nint data = MacOSNativeMethods.SendIntPtrWithIntPtr(
                pasteboard,
                _dataForTypeSelector,
                type);
            long length = ObjectiveC.GetDataLength(data);
            if (length < 0)
            {
                unavailable.Add(typeName);
                return null;
            }

            if (length > remainingBytes)
            {
                contentTooLarge = true;
                unavailable.Add(typeName);
                return null;
            }

            byte[]? managed = ObjectiveC.ToManagedData(data, remainingBytes);
            if (managed is null)
            {
                unavailable.Add(typeName);
                return null;
            }

            remainingBytes -= managed.Length;
            return managed;
        }
        finally
        {
            ObjectiveC.Release(type);
        }
    }

    private ClipboardBitmapData? ReadBitmap(
        nint pasteboard,
        HashSet<string> availableTypes,
        HashSet<string> unavailable,
        ref int remainingBytes,
        ref bool contentTooLarge)
    {
        string? type = availableTypes.Contains(PngType)
            ? PngType
            : availableTypes.Contains(TiffType)
                ? TiffType
                : null;
        if (type is null)
        {
            return null;
        }

        byte[]? data = ReadData(
            pasteboard,
            type,
            availableTypes,
            unavailable,
            ref remainingBytes,
            ref contentTooLarge);
        if (data is null)
        {
            return null;
        }

        ClipboardBitmapEncoding encoding = type == PngType
            ? ClipboardBitmapEncoding.PortableNetworkGraphics
            : ClipboardBitmapEncoding.TaggedImageFileFormat;
        (int width, int height, ushort bitsPerPixel) = ImageMetadataReader.Read(encoding, data);
        return new ClipboardBitmapData(encoding, data, width, height, bitsPerPixel);
    }

    private List<string> ReadFilePaths(
        nint pasteboard,
        HashSet<string> availableTypes,
        HashSet<string> unavailable,
        ref int remainingBytes,
        ref bool contentTooLarge)
    {
        if (!availableTypes.Contains(FileUrlType) &&
            !availableTypes.Contains(LegacyFileNamesType))
        {
            return [];
        }

        List<string> paths = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        if (availableTypes.Contains(LegacyFileNamesType))
        {
            // Finder 的 public.file-url 可能采用 file:///.file/id=... 引用形式，
            // 该字符串不能直接当作 POSIX 路径。它同时提供的文件名属性列表才是稳定真实路径。
            ReadLegacyFilePaths(
                pasteboard,
                paths,
                seen,
                ref remainingBytes,
                ref contentTooLarge);
        }

        nint items = MacOSNativeMethods.SendIntPtr(pasteboard, _pasteboardItemsSelector);
        if (paths.Count == 0 && items != 0)
        {
            nint fileType = ObjectiveC.CreateString(FileUrlType);
            try
            {
                nuint count = MacOSNativeMethods.SendNUInt(items, ObjectiveC.GetSelector("count"));
                if (count > (nuint)_settings.MaximumFileCount)
                {
                    unavailable.Add(FileUrlType);
                }

                for (nuint index = 0;
                     index < count && paths.Count < _settings.MaximumFileCount;
                     index++)
                {
                    nint item = MacOSNativeMethods.SendIntPtrWithNUInt(
                        items,
                        _objectAtIndexSelector,
                        index);
                    nint nativeUrl = MacOSNativeMethods.SendIntPtrWithIntPtr(
                        item,
                        _stringForTypeSelector,
                        fileType);
                    AddFileUrl(
                        ObjectiveC.ToManagedString(nativeUrl),
                        paths,
                        seen,
                        ref remainingBytes,
                        ref contentTooLarge);
                }
            }
            finally
            {
                ObjectiveC.Release(fileType);
            }
        }

        if (contentTooLarge)
        {
            unavailable.Add(FileUrlType);
        }

        return paths;
    }

    private void ReadLegacyFilePaths(
        nint pasteboard,
        List<string> paths,
        HashSet<string> seen,
        ref int remainingBytes,
        ref bool contentTooLarge)
    {
        nint legacyType = ObjectiveC.CreateString(LegacyFileNamesType);
        if (legacyType == 0)
        {
            return;
        }

        try
        {
            nint values = MacOSNativeMethods.SendIntPtrWithIntPtr(
                pasteboard,
                _propertyListForTypeSelector,
                legacyType);
            if (values == 0)
            {
                return;
            }

            nuint count = MacOSNativeMethods.SendNUInt(values, ObjectiveC.GetSelector("count"));
            if (count > (nuint)_settings.MaximumFileCount)
            {
                contentTooLarge = true;
            }

            for (nuint index = 0;
                 index < count && paths.Count < _settings.MaximumFileCount;
                 index++)
            {
                nint value = MacOSNativeMethods.SendIntPtrWithNUInt(
                    values,
                    _objectAtIndexSelector,
                    index);
                AddPath(
                    ObjectiveC.ToManagedString(value),
                    paths,
                    seen,
                    ref remainingBytes,
                    ref contentTooLarge);
            }
        }
        finally
        {
            ObjectiveC.Release(legacyType);
        }
    }

    private static void AddFileUrl(
        string? url,
        List<string> paths,
        HashSet<string> seen,
        ref int remainingBytes,
        ref bool contentTooLarge)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ||
            !parsed.IsFile)
        {
            return;
        }

        AddPath(
            parsed.LocalPath,
            paths,
            seen,
            ref remainingBytes,
            ref contentTooLarge);
    }

    private static void AddPath(
        string? path,
        List<string> paths,
        HashSet<string> seen,
        ref int remainingBytes,
        ref bool contentTooLarge)
    {
        if (string.IsNullOrEmpty(path) || !seen.Add(path))
        {
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(path);
        if (byteCount > remainingBytes)
        {
            contentTooLarge = true;
            return;
        }

        remainingBytes -= byteCount;
        paths.Add(path);
    }

    private bool ReadOriginMarker(
        nint pasteboard,
        HashSet<string> availableTypes,
        HashSet<string> unavailable,
        ref int remainingBytes,
        ref bool contentTooLarge)
    {
        byte[]? marker = ReadData(
            pasteboard,
            MacOSClipboardOriginMarker.TypeName,
            availableTypes,
            unavailable,
            ref remainingBytes,
            ref contentTooLarge);
        return marker is not null && _originMarker.Matches(marker);
    }

    private bool SetString(nint item, string typeName, string value)
    {
        nint nativeType = ObjectiveC.CreateString(typeName);
        nint nativeValue = ObjectiveC.CreateString(value);
        try
        {
            return nativeType != 0 && nativeValue != 0 &&
                MacOSNativeMethods.SendBoolWithIntPtrIntPtr(
                    item,
                    _setStringForTypeSelector,
                    nativeValue,
                    nativeType) != 0;
        }
        finally
        {
            ObjectiveC.Release(nativeValue);
            ObjectiveC.Release(nativeType);
        }
    }

    private bool SetData(nint item, string typeName, ReadOnlySpan<byte> value)
    {
        nint nativeType = ObjectiveC.CreateString(typeName);
        try
        {
            nint nativeData = ObjectiveC.CreateData(value);
            return nativeType != 0 && nativeData != 0 &&
                MacOSNativeMethods.SendBoolWithIntPtrIntPtr(
                    item,
                    _setDataForTypeSelector,
                    nativeData,
                    nativeType) != 0;
        }
        finally
        {
            ObjectiveC.Release(nativeType);
        }
    }

    private nint CreateMutableArray(nuint capacity)
    {
        nint allocated = MacOSNativeMethods.SendIntPtr(
            _mutableArrayClass,
            ObjectiveC.GetSelector("alloc"));
        return allocated == 0
            ? 0
            : MacOSNativeMethods.SendIntPtrWithNUInt(
                allocated,
                _initWithCapacitySelector,
                capacity);
    }

    private nint CreatePasteboardItem()
    {
        nint allocated = MacOSNativeMethods.SendIntPtr(
            _pasteboardItemClass,
            ObjectiveC.GetSelector("alloc"));
        return allocated == 0
            ? 0
            : MacOSNativeMethods.SendIntPtr(allocated, _initSelector);
    }

    private nint GetGeneralPasteboard()
    {
        nint pasteboard = MacOSNativeMethods.SendIntPtr(
            _pasteboardClass,
            _generalPasteboardSelector);
        return pasteboard != 0
            ? pasteboard
            : throw new InvalidOperationException("NSPasteboard.generalPasteboard is unavailable.");
    }

    private static ClipboardSourceInfo CreateUnknownSource() => new(
        null,
        null,
        null,
        ClipboardSourceAccessStatus.Unknown);

    private static void RecordSetResult(
        bool success,
        ref int successfulContentFormats,
        ref bool anyFailure)
    {
        if (success)
        {
            successfulContentFormats++;
        }
        else
        {
            anyFailure = true;
        }
    }
}
