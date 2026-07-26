using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using SnapBoard.Domain.Clipboard;

namespace SnapBoard.PerformanceTests;

internal static class Program
{
    public static void Main() => BenchmarkRunner.Run<ClipboardItemIdBenchmarks>();
}

[MemoryDiagnoser]
public class ClipboardItemIdBenchmarks
{
    private ClipboardItemId _lastIdentifier;

    [Benchmark]
    public ClipboardItemId CreateIdentifier()
    {
        _lastIdentifier = ClipboardItemId.New();
        return _lastIdentifier;
    }
}
