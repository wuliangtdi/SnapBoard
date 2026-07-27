using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Data.Sqlite;
using SnapBoard.Application.Clipboard;
using SnapBoard.Domain.Clipboard;
using SnapBoard.Infrastructure.Persistence;

namespace SnapBoard.PerformanceTests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 &&
            string.Equals(args[0], "history-search", StringComparison.OrdinalIgnoreCase))
        {
            return await HistorySearchScenario.RunAsync(CancellationToken.None);
        }

        BenchmarkRunner.Run<ClipboardItemIdBenchmarks>();
        return 0;
    }
}

internal static class HistorySearchScenario
{
    private const int BatchSize = 2_000;
    private const int ItemCount = 100_000;
    private const int MeasurementsPerQuery = 50;
    private const int WarmupCount = 10;
    private static readonly string SharedPayload =
        " https://snapboard.example/history/search?source=benchmark " +
        "C:\\SnapBoard\\fixtures\\clipboard-record.json " +
        "{\"kind\":\"clipboard\",\"persistent\":true,\"version\":3} " +
        new string('x', 350);
    private static readonly SearchCase[] SearchCases =
    [
        new("ChineseSelective", "样本编号 099999", true),
        new("EnglishSelective", "sample number 099997", true),
        new("CodeSelective", "token-099998", true),
        new("ChineseBroad", "中文剪贴板", false),
        new("EnglishBroad", "persistent clipboard", false),
        new("CodeBroad", "Console.WriteLine", false),
    ];

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"SnapBoard.PerformanceTests-{Guid.NewGuid():N}");
        SnapBoardStoragePaths paths = SnapBoardStoragePaths.Create(root);
        try
        {
            SnapBoardDatabaseConnectionFactory factory = new(paths.DatabasePath);
            await using (SqliteClipboardHistoryStore store = new(
                paths,
                factory,
                new SnapBoardDatabaseMigrator()))
            {
                await store.InitializeAsync(cancellationToken);
                Stopwatch import = Stopwatch.StartNew();
                DateTimeOffset start = DateTimeOffset.UtcNow.AddDays(-1);
                for (int offset = 0; offset < ItemCount; offset += BatchSize)
                {
                    int count = Math.Min(BatchSize, ItemCount - offset);
                    ClipboardCapturedItem[] batch = new ClipboardCapturedItem[count];
                    for (int index = 0; index < count; index++)
                    {
                        int sequence = offset + index;
                        batch[index] = CreateItem(sequence, start.AddMilliseconds(sequence));
                    }

                    await store.BulkImportAsync(batch, cancellationToken);
                }

                import.Stop();

                foreach (SearchCase searchCase in SearchCases)
                {
                    for (int index = 0; index < WarmupCount; index++)
                    {
                        await SearchAsync(store, searchCase.Query, cancellationToken);
                    }
                }

                List<double> allMeasurements = [];
                List<double> targetMeasurements = [];
                bool passed = true;
                Console.WriteLine(
                    $"HistorySearchData Items={ItemCount}; ImportMs={import.Elapsed.TotalMilliseconds:F2}; " +
                    $"DatabaseBytes={new FileInfo(paths.DatabasePath).Length}; " +
                    $"AverageTextCharacters={GetAverageTextLength():F1};");
                foreach (SearchCase searchCase in SearchCases)
                {
                    double[] measurements = new double[MeasurementsPerQuery];
                    for (int index = 0; index < measurements.Length; index++)
                    {
                        Stopwatch stopwatch = Stopwatch.StartNew();
                        ClipboardHistoryPage page = await SearchAsync(
                            store,
                            searchCase.Query,
                            cancellationToken);
                        stopwatch.Stop();
                        if (page.Items.Count == 0)
                        {
                            throw new InvalidDataException(
                                $"Search case {searchCase.Name} returned no data.");
                        }

                        measurements[index] = stopwatch.Elapsed.TotalMilliseconds;
                    }

                    Array.Sort(measurements);
                    allMeasurements.AddRange(measurements);
                    if (searchCase.EnforceP95Target)
                    {
                        targetMeasurements.AddRange(measurements);
                    }

                    double p50 = Percentile(measurements, 0.50);
                    double p95 = Percentile(measurements, 0.95);
                    double maximum = measurements[^1];
                    bool targetPassed = !searchCase.EnforceP95Target || p95 < 80;
                    passed &= maximum <= 200 && targetPassed;
                    Console.WriteLine(
                        $"HistorySearch Name={searchCase.Name}; Samples={measurements.Length}; " +
                        $"P50Ms={p50:F2}; P95Ms={p95:F2}; MaxMs={maximum:F2}; " +
                        $"EnforceP95Target={searchCase.EnforceP95Target}; " +
                        $"TargetP95Under80={p95 < 80}; HardLimitUnderOrEqual200={maximum <= 200};");
                }

                double[] target = targetMeasurements.ToArray();
                Array.Sort(target);
                double[] overall = allMeasurements.ToArray();
                Array.Sort(overall);
                Console.WriteLine(
                    $"HistorySearchTargetOverall Samples={target.Length}; " +
                    $"P50Ms={Percentile(target, 0.50):F2}; " +
                    $"P95Ms={Percentile(target, 0.95):F2}; MaxMs={target[^1]:F2};");
                Console.WriteLine(
                    $"HistorySearchOverall Samples={overall.Length}; " +
                    $"P50Ms={Percentile(overall, 0.50):F2}; " +
                    $"P95Ms={Percentile(overall, 0.95):F2}; MaxMs={overall[^1]:F2}; " +
                    $"Result={(passed ? "PASS" : "FAIL")};");
                return passed ? 0 : 1;
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            // 只删除本场景创建且带固定前缀的临时目录，避免性能工具扩大清理范围。
            string expectedParent = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar);
            DirectoryInfo directory = new(root);
            if (directory.Parent is not null &&
                string.Equals(
                    directory.Parent.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    expectedParent,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal) &&
                directory.Name.StartsWith(
                    "SnapBoard.PerformanceTests-",
                    StringComparison.Ordinal))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ValueTask<ClipboardHistoryPage> SearchAsync(
        SqliteClipboardHistoryStore store,
        string query,
        CancellationToken cancellationToken) => store.SearchAsync(
        new ClipboardHistoryQuery
        {
            SearchText = query,
            PageSize = 50,
        },
        cancellationToken);

    private static ClipboardCapturedItem CreateItem(
        int sequence,
        DateTimeOffset capturedAt)
    {
        (string text, ClipboardHistoryDisplayCategory category) = (sequence % 3) switch
        {
            0 => ($"中文剪贴板检索 样本编号 {sequence:D6} 标签 项目{SharedPayload}", ClipboardHistoryDisplayCategory.Text),
            1 => ($"persistent clipboard history search sample number {sequence:D6}{SharedPayload}", ClipboardHistoryDisplayCategory.Text),
            _ => ($"public static void Entry{sequence:D6}() {{ Console.WriteLine(\"token-{sequence:D6}\"); }}{SharedPayload}", ClipboardHistoryDisplayCategory.Code),
        };
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        string hash = Convert.ToHexStringLower(SHA256.HashData(utf8));
        ClipboardItemId id = ClipboardItemId.New();
        return new ClipboardCapturedItem
        {
            Id = id,
            SequenceNumber = checked((ulong)sequence + 1),
            CapturedAt = capturedAt,
            SourceProcessName = sequence % 2 == 0 ? "generator-a" : "generator-b",
            SourceAccessStatus = 1,
            ContentHash = new ClipboardContentHash(hash),
            PrimaryKind = ClipboardContentKind.Text,
            DisplayCategory = category,
            PreviewText = text,
            SearchableText = text,
            Representations =
            [
                new ClipboardCapturedRepresentation(
                    ClipboardContentKind.Text,
                    "text/plain; charset=utf-8",
                    text,
                    default),
            ],
            TotalSizeBytes = utf8.Length,
        };
    }

    private static double Percentile(double[] sortedMeasurements, double percentile)
    {
        int index = Math.Clamp(
            (int)Math.Ceiling(sortedMeasurements.Length * percentile) - 1,
            0,
            sortedMeasurements.Length - 1);
        return sortedMeasurements[index];
    }

    private static double GetAverageTextLength() => Enumerable.Range(0, 3)
        .Select(sequence => (sequence % 3) switch
        {
            0 => $"中文剪贴板检索 样本编号 {sequence:D6} 标签 项目{SharedPayload}",
            1 => $"persistent clipboard history search sample number {sequence:D6}{SharedPayload}",
            _ => $"public static void Entry{sequence:D6}() {{ Console.WriteLine(\"token-{sequence:D6}\"); }}{SharedPayload}",
        })
        .Average(text => text.Length);

    private sealed record SearchCase(string Name, string Query, bool EnforceP95Target);
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
