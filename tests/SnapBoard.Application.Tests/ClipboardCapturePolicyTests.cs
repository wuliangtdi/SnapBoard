using System.Text;
using SnapBoard.Application.Clipboard;
using SnapBoard.Domain.Clipboard;
using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Application.Tests;

public sealed class ClipboardCapturePolicyTests
{
    [Fact]
    public async Task CurrentApplicationAndPasswordManagersAreIgnored()
    {
        ClipboardCaptureOptions options = new();
        ClipboardCapturePolicyChain chain = CreateChain(options);

        ClipboardCapturePolicyDecision current = await chain.EvaluateAsync(
            CreateSnapshot(isFromCurrentApplication: true),
            CancellationToken.None);
        Assert.False(current.ShouldCapture);
        Assert.Equal("current-application", current.ReasonCode);

        ClipboardCapturePolicyDecision passwordManager = await chain.EvaluateAsync(
            CreateSnapshot(processName: "KeePassXC.exe"),
            CancellationToken.None);
        Assert.False(passwordManager.ShouldCapture);
        Assert.Equal("password-manager", passwordManager.ReasonCode);
    }

    [Fact]
    public async Task SensitiveTransientFormatIsIgnoredBeforePayloadNormalization()
    {
        ClipboardCaptureOptions options = new();
        ClipboardCapturePolicyDecision decision = await CreateChain(options).EvaluateAsync(
            CreateSnapshot(
                formats:
                [
                    new ClipboardFormatDescriptor(
                        "org.nspasteboard.TransientType",
                        "Transient"),
                ]),
            CancellationToken.None);

        Assert.False(decision.ShouldCapture);
        Assert.Equal("sensitive-format", decision.ReasonCode);
    }

    [Fact]
    public async Task ApplicationTextOnlyRuleDropsBinaryRepresentations()
    {
        ClipboardCaptureOptions options = new()
        {
            ApplicationRules = new Dictionary<string, ClipboardApplicationRuleMode>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["restricted-app"] = ClipboardApplicationRuleMode.TextOnly,
            },
        };
        ClipboardContentSnapshot snapshot = CreateSnapshot(
            processName: "restricted-app.exe",
            text: null,
            html: Encoding.UTF8.GetBytes(
                "Version:0.9\r\n<!--StartFragment--><b>allowed text</b><!--EndFragment-->"),
            bitmap: new ClipboardBitmapData(
                ClipboardBitmapEncoding.PortableNetworkGraphics,
                new byte[] { 1, 2, 3 },
                1,
                1,
                32));

        ClipboardCapturePolicyDecision decision = await CreateChain(options).EvaluateAsync(
            snapshot,
            CancellationToken.None);
        ClipboardCapturedItem? normalized = ClipboardContentNormalizer.Normalize(
            snapshot,
            decision,
            options);

        Assert.True(decision.ShouldCapture);
        Assert.True(decision.TextOnly);
        Assert.NotNull(normalized);
        ClipboardCapturedRepresentation representation = Assert.Single(normalized.Representations);
        Assert.Equal(ClipboardContentKind.Text, representation.Kind);
        Assert.Equal("allowed text", representation.Text);
    }

    [Fact]
    public async Task NormalizationPreservesPackagedSourceIdentityAndAttribution()
    {
        ClipboardCaptureOptions options = new();
        ClipboardContentSnapshot snapshot = CreateSnapshot(
            applicationUserModelId: "OpenAI.Codex_2p2nqsd0c76g0!App",
            packageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0",
            sourceAttributionKind: ClipboardSourceAttributionKind.ClipboardOwnerAtChange);
        ClipboardCapturePolicyDecision decision = await CreateChain(options).EvaluateAsync(
            snapshot,
            CancellationToken.None);

        ClipboardCapturedItem? normalized = ClipboardContentNormalizer.Normalize(
            snapshot,
            decision,
            options);

        Assert.NotNull(normalized);
        Assert.Equal(
            "OpenAI.Codex_2p2nqsd0c76g0!App",
            normalized.SourceApplicationUserModelId);
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0", normalized.SourcePackageFamilyName);
        Assert.Equal(
            (int)ClipboardSourceAttributionKind.ClipboardOwnerAtChange,
            normalized.SourceAttributionKind);
    }

    [Fact]
    public async Task NormalizationKeepsUnknownMacOSSourceFreeOfWindowsIdentity()
    {
        ClipboardCaptureOptions options = new();
        ClipboardContentSnapshot snapshot = CreateSnapshot(
            processName: null,
            sourceProcessId: null,
            sourceAccessStatus: ClipboardSourceAccessStatus.Unknown);
        ClipboardCapturePolicyDecision decision = await CreateChain(options).EvaluateAsync(
            snapshot,
            CancellationToken.None);

        ClipboardCapturedItem? normalized = ClipboardContentNormalizer.Normalize(
            snapshot,
            decision,
            options);

        Assert.NotNull(normalized);
        Assert.Null(normalized.SourceProcessId);
        Assert.Null(normalized.SourceProcessName);
        Assert.Null(normalized.SourceExecutablePath);
        Assert.Null(normalized.SourceApplicationUserModelId);
        Assert.Null(normalized.SourcePackageFamilyName);
        Assert.Equal((int)ClipboardSourceAccessStatus.Unknown, normalized.SourceAccessStatus);
        Assert.Equal(
            (int)ClipboardSourceAttributionKind.Unknown,
            normalized.SourceAttributionKind);
    }

    [Fact]
    public async Task PayloadLimitAndUnsupportedContentAreIgnored()
    {
        ClipboardCaptureOptions small = new() { MaximumPayloadBytes = 4 };
        ClipboardCapturePolicyDecision tooLarge = await CreateChain(small).EvaluateAsync(
            CreateSnapshot(text: "12345"),
            CancellationToken.None);
        Assert.False(tooLarge.ShouldCapture);
        Assert.Equal("payload-too-large", tooLarge.ReasonCode);

        ClipboardCaptureOptions textOnly = new()
        {
            EnabledContentKinds = new HashSet<ClipboardContentKind>
            {
                ClipboardContentKind.Text,
            },
        };
        ClipboardCapturePolicyDecision unsupported = await CreateChain(textOnly).EvaluateAsync(
            CreateSnapshot(
                text: null,
                bitmap: new ClipboardBitmapData(
                    ClipboardBitmapEncoding.PortableNetworkGraphics,
                    new byte[] { 1 },
                    1,
                    1,
                    32)),
            CancellationToken.None);
        Assert.False(unsupported.ShouldCapture);
        Assert.Equal("no-supported-content", unsupported.ReasonCode);
    }

    [Fact]
    public async Task ApplicationBlacklistTakesPrecedenceOverTextOnlyResult()
    {
        ClipboardCaptureOptions options = new()
        {
            ApplicationRules = new Dictionary<string, ClipboardApplicationRuleMode>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["ignored-app"] = ClipboardApplicationRuleMode.Ignore,
            },
        };
        ClipboardCapturePolicyDecision decision = await CreateChain(options).EvaluateAsync(
            CreateSnapshot(processName: "ignored-app"),
            CancellationToken.None);

        Assert.False(decision.ShouldCapture);
        Assert.Equal("application-blacklist", decision.ReasonCode);
    }

    [Fact]
    public async Task CommittedCaptureRemainsSuccessfulWhenRetentionCleanupFails()
    {
        ClipboardCaptureOptions options = new();
        RetentionFailureStore store = new();
        ClipboardHistoryChangeNotifier notifier = new();
        ClipboardHistoryChangedEvent? published = null;
        notifier.Changed += (_, change) => published = change;
        ClipboardCaptureService service = new(
            CreateChain(options),
            store,
            options,
            ClipboardRetentionPolicy.Default,
            notifier);

        ClipboardCaptureResult result = await service.ProcessAsync(
            new ClipboardReadResult(
                ClipboardReadStatus.Success,
                CreateSnapshot(text: "committed before retention")),
            CancellationToken.None);

        Assert.Equal(ClipboardCaptureStatus.Stored, result.Status);
        Assert.Equal("stored-retention-pending", result.ReasonCode);
        Assert.NotNull(result.SaveResult);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, store.RetentionCount);
        Assert.Equal(ClipboardHistoryChangeKind.Added, published?.Kind);
        Assert.Equal(result.SaveResult.ItemId, published?.ItemId);
    }

    private static ClipboardCapturePolicyChain CreateChain(ClipboardCaptureOptions options) => new(
        [
            new CurrentApplicationClipboardPolicy(),
            new SensitiveClipboardPolicy(options),
            new ApplicationRuleClipboardPolicy(options),
            new PayloadSizeClipboardPolicy(options),
            new SupportedContentClipboardPolicy(options),
        ]);

    private static ClipboardContentSnapshot CreateSnapshot(
        string? processName = "source-app",
        string? text = "ordinary clipboard text",
        byte[]? html = null,
        ClipboardBitmapData? bitmap = null,
        IReadOnlyList<ClipboardFormatDescriptor>? formats = null,
        bool isFromCurrentApplication = false,
        string? applicationUserModelId = null,
        string? packageFamilyName = null,
        int? sourceProcessId = 42,
        ClipboardSourceAccessStatus sourceAccessStatus = ClipboardSourceAccessStatus.Identified,
        ClipboardSourceAttributionKind sourceAttributionKind =
            ClipboardSourceAttributionKind.Unknown) => new()
            {
                SequenceNumber = 1,
                CapturedAt = DateTimeOffset.UtcNow,
                Source = new ClipboardSourceInfo(
                    sourceProcessId,
                    processName,
                    processName is null ? null : $"C:\\Apps\\{processName}",
                    sourceAccessStatus,
                    applicationUserModelId,
                    packageFamilyName,
                    sourceAttributionKind),
                Text = text,
                Html = html ?? Array.Empty<byte>(),
                Bitmap = bitmap,
                Formats = formats ?? Array.Empty<ClipboardFormatDescriptor>(),
                IsFromCurrentApplication = isFromCurrentApplication,
            };

    private sealed class RetentionFailureStore : IClipboardHistoryStore
    {
        public int SaveCount { get; private set; }

        public int RetentionCount { get; private set; }

        public ValueTask<ClipboardHistorySaveResult> SaveAsync(
            ClipboardCapturedItem item,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCount++;
            return ValueTask.FromResult(new ClipboardHistorySaveResult(item.Id, false));
        }

        public ValueTask<int> ApplyRetentionAsync(
            ClipboardRetentionPolicy policy,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RetentionCount++;
            return ValueTask.FromException<int>(new IOException("synthetic retention failure"));
        }

        public ValueTask<ClipboardHistoryInitializationResult> InitializeAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ClipboardHistoryPage> SearchAsync(
            ClipboardHistoryQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ClipboardHistoryContent?> GetContentAsync(
            ClipboardItemId itemId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ReadOnlyMemory<byte>> GetThumbnailAsync(
            ClipboardItemId itemId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<bool> SetPinnedAsync(
            ClipboardItemId itemId,
            bool isPinned,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<bool> SetTagsAsync(
            ClipboardItemId itemId,
            IReadOnlyCollection<string> tags,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<bool> SoftDeleteAsync(
            ClipboardItemId itemId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<int> ClearAsync(
            bool includePinned,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<bool> RecordUseAsync(
            ClipboardItemId itemId,
            DateTimeOffset usedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<string?> GetSettingAsync(
            string key,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetSettingAsync(
            string key,
            string value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<int> CleanupOrphanedBlobsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
