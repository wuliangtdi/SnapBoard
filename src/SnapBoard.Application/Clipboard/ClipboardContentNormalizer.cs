using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SnapBoard.Domain.Clipboard;
using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Application.Clipboard;

public static class ClipboardContentNormalizer
{
    public static ClipboardCapturedItem? Normalize(
        ClipboardContentSnapshot snapshot,
        ClipboardCapturePolicyDecision decision,
        ClipboardCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(options);
        if (!decision.ShouldCapture)
        {
            return null;
        }

        List<ClipboardCapturedRepresentation> representations = [];
        string? plainText = snapshot.Text;
        string htmlText = snapshot.Html.IsEmpty ? string.Empty : ExtractHtmlText(snapshot.Html.Span);
        if (plainText is null && htmlText.Length > 0)
        {
            // 仅 HTML 来源也保留派生纯文本，使“复制为纯文本”无需重新加载或解析富格式。
            plainText = htmlText;
        }

        if (options.EnabledContentKinds.Contains(ClipboardContentKind.Text) && plainText is not null)
        {
            representations.Add(new ClipboardCapturedRepresentation(
                ClipboardContentKind.Text,
                "text/plain; charset=utf-8",
                plainText,
                default));
        }

        if (!decision.TextOnly &&
            options.EnabledContentKinds.Contains(ClipboardContentKind.Html) &&
            !snapshot.Html.IsEmpty)
        {
            representations.Add(new ClipboardCapturedRepresentation(
                ClipboardContentKind.Html,
                "text/html",
                null,
                snapshot.Html));
        }

        if (!decision.TextOnly &&
            options.EnabledContentKinds.Contains(ClipboardContentKind.RichText) &&
            !snapshot.RichText.IsEmpty)
        {
            representations.Add(new ClipboardCapturedRepresentation(
                ClipboardContentKind.RichText,
                "text/rtf",
                null,
                snapshot.RichText));
        }

        if (!decision.TextOnly &&
            options.EnabledContentKinds.Contains(ClipboardContentKind.Image) &&
            snapshot.Bitmap is { } bitmap)
        {
            representations.Add(new ClipboardCapturedRepresentation(
                ClipboardContentKind.Image,
                GetBitmapMediaType(bitmap.Encoding),
                null,
                bitmap.Data,
                MapBitmapEncoding(bitmap.Encoding),
                bitmap.Width,
                bitmap.Height,
                bitmap.BitsPerPixel));
        }

        string[] filePaths = !decision.TextOnly &&
            options.EnabledContentKinds.Contains(ClipboardContentKind.FileReference)
            ? snapshot.FilePaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray()
            : Array.Empty<string>();
        if (representations.Count == 0 && filePaths.Length == 0)
        {
            return null;
        }

        ClipboardContentKind primaryKind = GetPrimaryKind(representations, filePaths);
        string rtfText = plainText is null && !snapshot.RichText.IsEmpty
            ? ExtractRtfText(snapshot.RichText.Span)
            : string.Empty;
        string displayText = GetDisplayText(
            primaryKind,
            plainText,
            htmlText,
            rtfText,
            snapshot.Bitmap,
            filePaths);
        string searchableText = BuildSearchableText(
            plainText,
            htmlText,
            rtfText,
            filePaths,
            snapshot.Source.ProcessName,
            options.MaximumSearchableCharacters);
        long totalSizeBytes = representations.Sum(representation => representation.SizeBytes) +
            filePaths.Sum(path => (long)Encoding.UTF8.GetByteCount(path));

        return new ClipboardCapturedItem
        {
            Id = ClipboardItemId.New(),
            SequenceNumber = snapshot.SequenceNumber,
            CapturedAt = snapshot.CapturedAt,
            SourceProcessId = snapshot.Source.ProcessId,
            SourceProcessName = snapshot.Source.ProcessName,
            SourceExecutablePath = snapshot.Source.ExecutablePath,
            SourceAccessStatus = (int)snapshot.Source.AccessStatus,
            ContentHash = CalculateHash(representations, filePaths),
            PrimaryKind = primaryKind,
            DisplayCategory = Classify(primaryKind, displayText),
            PreviewText = Truncate(displayText, 2048),
            SearchableText = searchableText,
            Representations = representations,
            FilePaths = filePaths,
            Formats = CreateFormats(snapshot),
            TotalSizeBytes = totalSizeBytes,
        };
    }

    private static ClipboardCapturedFormat[] CreateFormats(
        ClipboardContentSnapshot snapshot)
    {
        HashSet<string> unavailable = snapshot.UnavailableFormats.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        return snapshot.Formats
            .Select(format => new ClipboardCapturedFormat(
                format.Identifier,
                format.Name,
                !unavailable.Contains(format.Name) && !unavailable.Contains(format.Identifier)))
            .ToArray();
    }

    private static ClipboardContentKind GetPrimaryKind(
        IReadOnlyCollection<ClipboardCapturedRepresentation> representations,
        string[] filePaths)
    {
        if (representations.Any(representation => representation.Kind == ClipboardContentKind.Image))
        {
            return ClipboardContentKind.Image;
        }

        if (filePaths.Length > 0)
        {
            return ClipboardContentKind.FileReference;
        }

        if (representations.Any(representation => representation.Kind == ClipboardContentKind.Html))
        {
            return ClipboardContentKind.Html;
        }

        return representations.Any(representation => representation.Kind == ClipboardContentKind.RichText)
            ? ClipboardContentKind.RichText
            : ClipboardContentKind.Text;
    }

    private static string GetDisplayText(
        ClipboardContentKind primaryKind,
        string? plainText,
        string htmlText,
        string rtfText,
        ClipboardBitmapData? bitmap,
        IReadOnlyList<string> filePaths) => primaryKind switch
        {
            ClipboardContentKind.Image when bitmap is not null =>
                $"图片 {bitmap.Width} x {bitmap.Height}",
            ClipboardContentKind.FileReference => string.Join(Environment.NewLine, filePaths),
            ClipboardContentKind.Html when htmlText.Length > 0 => htmlText,
            ClipboardContentKind.RichText when rtfText.Length > 0 => rtfText,
            _ when !string.IsNullOrWhiteSpace(plainText) => plainText,
            _ when htmlText.Length > 0 => htmlText,
            _ when rtfText.Length > 0 => rtfText,
            _ => "剪贴板内容",
        };

    private static string BuildSearchableText(
        string? plainText,
        string htmlText,
        string rtfText,
        IReadOnlyList<string> filePaths,
        string? sourceProcessName,
        int maximumCharacters)
    {
        StringBuilder builder = new();
        AppendSearchPart(builder, plainText);
        AppendSearchPart(builder, htmlText);
        AppendSearchPart(builder, rtfText);
        foreach (string path in filePaths)
        {
            AppendSearchPart(builder, path);
        }

        AppendSearchPart(builder, sourceProcessName);
        return Truncate(CollapseWhitespace(builder.ToString()), maximumCharacters);
    }

    private static void AppendSearchPart(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(value);
    }

    private static ClipboardHistoryDisplayCategory Classify(
        ClipboardContentKind primaryKind,
        string displayText)
    {
        if (primaryKind == ClipboardContentKind.Image)
        {
            return ClipboardHistoryDisplayCategory.Image;
        }

        string trimmed = displayText.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) &&
            uri.Scheme is "http" or "https")
        {
            return ClipboardHistoryDisplayCategory.Link;
        }

        bool looksLikeCode =
            trimmed.Contains("using ", StringComparison.Ordinal) ||
            trimmed.Contains("public ", StringComparison.Ordinal) ||
            trimmed.Contains("private ", StringComparison.Ordinal) ||
            trimmed.Contains("=>", StringComparison.Ordinal) ||
            trimmed.Contains("{\"", StringComparison.Ordinal) ||
            (trimmed.Contains('{') && trimmed.Contains('}') && trimmed.Contains(';')) ||
            trimmed.StartsWith("git ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase);
        return looksLikeCode
            ? ClipboardHistoryDisplayCategory.Code
            : ClipboardHistoryDisplayCategory.Text;
    }

    private static ClipboardContentHash CalculateHash(
        IReadOnlyCollection<ClipboardCapturedRepresentation> representations,
        IReadOnlyList<string> filePaths)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (ClipboardCapturedRepresentation representation in
            representations.OrderBy(representation => representation.Kind))
        {
            AppendInt32(hash, (int)representation.Kind);
            if (representation.Text is not null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(representation.Text);
                try
                {
                    AppendData(hash, bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            else
            {
                AppendData(hash, representation.Data.Span);
            }
        }

        foreach (string filePath in filePaths)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(filePath);
            try
            {
                AppendData(hash, bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        return new ClipboardContentHash(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void AppendData(IncrementalHash hash, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(length, data.Length);
        hash.AppendData(length);
        hash.AppendData(data);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static string ExtractHtmlText(ReadOnlySpan<byte> data)
    {
        string html = Encoding.UTF8.GetString(data).TrimEnd('\0');
        const string startFragment = "<!--StartFragment-->";
        const string endFragment = "<!--EndFragment-->";
        int start = html.IndexOf(startFragment, StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            start += startFragment.Length;
            int end = html.IndexOf(endFragment, start, StringComparison.OrdinalIgnoreCase);
            html = end > start ? html[start..end] : html[start..];
        }
        else
        {
            int firstTag = html.IndexOf('<');
            if (firstTag > 0 && html[..firstTag].Contains("StartHTML", StringComparison.OrdinalIgnoreCase))
            {
                html = html[firstTag..];
            }
        }

        StringBuilder text = new(html.Length);
        bool insideTag = false;
        foreach (char character in html)
        {
            if (character == '<')
            {
                insideTag = true;
                text.Append(' ');
            }
            else if (character == '>')
            {
                insideTag = false;
                text.Append(' ');
            }
            else if (!insideTag)
            {
                text.Append(character);
            }
        }

        return CollapseWhitespace(WebUtility.HtmlDecode(text.ToString()));
    }

    private static string ExtractRtfText(ReadOnlySpan<byte> data)
    {
        string rtf = Encoding.UTF8.GetString(data).TrimEnd('\0');
        StringBuilder text = new(rtf.Length);
        for (int index = 0; index < rtf.Length; index++)
        {
            char character = rtf[index];
            if (character is '{' or '}')
            {
                continue;
            }

            if (character != '\\')
            {
                text.Append(character);
                continue;
            }

            if (index + 3 < rtf.Length && rtf[index + 1] == '\'' &&
                byte.TryParse(rtf.AsSpan(index + 2, 2),
                    System.Globalization.NumberStyles.HexNumber,
                    provider: null,
                    out byte encoded))
            {
                text.Append((char)encoded);
                index += 3;
                continue;
            }

            index++;
            while (index < rtf.Length && char.IsLetter(rtf[index]))
            {
                index++;
            }

            while (index < rtf.Length && (rtf[index] == '-' || char.IsDigit(rtf[index])))
            {
                index++;
            }

            if (index < rtf.Length && rtf[index] != ' ')
            {
                index--;
            }
        }

        return CollapseWhitespace(text.ToString());
    }

    private static string CollapseWhitespace(string value)
    {
        StringBuilder builder = new(value.Length);
        bool previousWasWhitespace = true;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }
            }
            else
            {
                builder.Append(character);
                previousWasWhitespace = false;
            }
        }

        return builder.ToString().Trim();
    }

    private static string Truncate(string value, int maximumCharacters)
    {
        if (maximumCharacters <= 0 || value.Length <= maximumCharacters)
        {
            return value;
        }

        int length = maximumCharacters;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }

    private static ClipboardStoredBitmapEncoding MapBitmapEncoding(
        ClipboardBitmapEncoding encoding) => encoding switch
        {
            ClipboardBitmapEncoding.DeviceIndependentBitmap =>
                ClipboardStoredBitmapEncoding.DeviceIndependentBitmap,
            ClipboardBitmapEncoding.DeviceIndependentBitmapV5 =>
                ClipboardStoredBitmapEncoding.DeviceIndependentBitmapV5,
            ClipboardBitmapEncoding.PortableNetworkGraphics =>
                ClipboardStoredBitmapEncoding.PortableNetworkGraphics,
            ClipboardBitmapEncoding.TaggedImageFileFormat =>
                ClipboardStoredBitmapEncoding.TaggedImageFileFormat,
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };

    private static string GetBitmapMediaType(ClipboardBitmapEncoding encoding) => encoding switch
    {
        ClipboardBitmapEncoding.DeviceIndependentBitmap => "image/x-dib",
        ClipboardBitmapEncoding.DeviceIndependentBitmapV5 => "image/x-dibv5",
        ClipboardBitmapEncoding.PortableNetworkGraphics => "image/png",
        ClipboardBitmapEncoding.TaggedImageFileFormat => "image/tiff",
        _ => "application/octet-stream",
    };
}
