namespace SnapBoard.Sync.WebDav;

internal static class WebDavPathPolicy
{
    private const int MaximumRelativePathLength = 1024;
    private const int MaximumSegmentLength = 128;

    public static bool IsValidRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.Length > MaximumRelativePathLength ||
            relativePath.StartsWith('/') || relativePath.EndsWith('/') ||
            relativePath.Contains('\\') || relativePath.Contains('%') ||
            relativePath.Contains('?') || relativePath.Contains('#'))
        {
            return false;
        }

        ReadOnlySpan<char> path = relativePath.AsSpan();
        foreach (Range range in path.Split('/'))
        {
            if (!IsValidSegment(path[range]))
            {
                return false;
            }
        }

        return true;
    }

    public static Uri BuildRootUri(WebDavOptions options) =>
        new(options.Endpoint, options.RemoteRoot + "/");

    public static Uri BuildRequestUri(Uri rootUri, string relativePath, bool collection)
    {
        if (!IsValidRelativePath(relativePath))
        {
            throw new ArgumentException("The WebDAV relative path is invalid.", nameof(relativePath));
        }

        string suffix = collection ? relativePath + "/" : relativePath;
        Uri result = new(rootUri, suffix);
        if (!IsSameOrigin(rootUri, result) || !IsUnderRoot(rootUri, result))
        {
            throw new ArgumentException("The WebDAV path escaped the sync root.", nameof(relativePath));
        }

        return result;
    }

    public static bool TryResolveHref(
        Uri rootUri,
        Uri collectionUri,
        string href,
        out WebDavResourceLocation location)
    {
        location = default;
        if (string.IsNullOrWhiteSpace(href) || href.Length > 2048 ||
            !string.Equals(href, href.Trim(), StringComparison.Ordinal) ||
            href.Any(char.IsControl) ||
            href.Contains('\\') ||
            href.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            href.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
            href.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(collectionUri, href, out Uri? resolved) ||
            !IsSameOrigin(rootUri, resolved) ||
            !IsUnderRoot(rootUri, resolved) ||
            !string.IsNullOrEmpty(resolved.Query) ||
            !string.IsNullOrEmpty(resolved.Fragment))
        {
            return false;
        }

        string rootPath = rootUri.GetComponents(UriComponents.Path, UriFormat.UriEscaped)
            .TrimEnd('/');
        string resolvedPath = resolved.GetComponents(UriComponents.Path, UriFormat.UriEscaped)
            .TrimEnd('/');
        if (resolvedPath.Length < rootPath.Length)
        {
            return false;
        }

        string relativeEscaped = resolvedPath.Length == rootPath.Length
            ? string.Empty
            : resolvedPath[(rootPath.Length + 1)..];
        if (relativeEscaped.Contains('%') || relativeEscaped.Contains('\\'))
        {
            return false;
        }

        string[] segments = relativeEscaped.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(segment => !IsValidSegment(segment.AsSpan())))
        {
            return false;
        }

        string relativePath = string.Join('/', segments);
        string objectName = segments.Length == 0 ? string.Empty : segments[^1];
        location = new WebDavResourceLocation(relativePath, objectName);
        return true;
    }

    public static bool IsSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    public static bool IsUnderRoot(Uri rootUri, Uri candidate)
    {
        string rootPath = rootUri.AbsolutePath.TrimEnd('/') + "/";
        string candidatePath = candidate.AbsolutePath.TrimEnd('/') + "/";
        return candidatePath.StartsWith(rootPath, StringComparison.Ordinal);
    }

    private static bool IsValidSegment(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty || segment.Length > MaximumSegmentLength ||
            segment.SequenceEqual(".") || segment.SequenceEqual(".."))
        {
            return false;
        }

        foreach (char character in segment)
        {
            if (character is not (>= 'a' and <= 'z') and
                not (>= 'A' and <= 'Z') and
                not (>= '0' and <= '9') and
                not '-' and not '_' and not '.')
            {
                return false;
            }
        }

        return true;
    }
}

internal readonly record struct WebDavResourceLocation(
    string RelativePath,
    string ObjectName);
