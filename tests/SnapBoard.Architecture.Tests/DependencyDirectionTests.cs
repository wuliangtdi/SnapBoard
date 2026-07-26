using SnapBoard.Application;
using SnapBoard.Domain.Clipboard;

namespace SnapBoard.Architecture.Tests;

public sealed class DependencyDirectionTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayersOrFrameworks()
    {
        HashSet<string> references = GetReferencedAssemblyNames(typeof(ClipboardItemId));
        string[] forbiddenPrefixes =
        [
            "Avalonia",
            "Microsoft.Data.Sqlite",
            "Microsoft.Extensions",
            "SnapBoard.Application",
            "SnapBoard.Desktop",
            "SnapBoard.Infrastructure",
            "SnapBoard.Platform",
            "SnapBoard.Sync",
            "SqlSugar",
        ];

        Assert.DoesNotContain(references, reference =>
            forbiddenPrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal)));
    }

    [Fact]
    public void ApplicationDoesNotReferenceImplementationLayers()
    {
        HashSet<string> references = GetReferencedAssemblyNames(typeof(ApplicationAssemblyMarker));
        string[] forbidden =
        [
            "SnapBoard.Desktop",
            "SnapBoard.Infrastructure",
            "SnapBoard.Platform.Linux",
            "SnapBoard.Platform.MacOS",
            "SnapBoard.Platform.Windows",
            "SnapBoard.Sync.WebDav",
        ];

        Assert.DoesNotContain(references, forbidden.Contains);
    }

    private static HashSet<string> GetReferencedAssemblyNames(Type markerType) =>
        markerType.Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
}
