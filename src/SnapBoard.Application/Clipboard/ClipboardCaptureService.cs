using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Application.Clipboard;

public sealed class ClipboardCaptureService(
    IClipboardCapturePolicyChain policyChain,
    IClipboardHistoryStore store,
    ClipboardCaptureOptions options,
    IHistorySettingsService historySettings,
    ClipboardHistoryChangeNotifier notifier,
    IClipboardSourceApplicationIconProvider? sourceIconProvider = null) : IClipboardCaptureService
{
    private static readonly TimeSpan SourceIconRetryDelay = TimeSpan.FromMilliseconds(50);

    public async ValueTask<ClipboardCaptureResult> ProcessAsync(
        ClipboardReadResult readResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readResult);
        cancellationToken.ThrowIfCancellationRequested();
        await historySettings.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (readResult.Snapshot is null ||
            readResult.Status is ClipboardReadStatus.ClipboardBusy or ClipboardReadStatus.Failed)
        {
            return new ClipboardCaptureResult(
                ClipboardCaptureStatus.ReadUnavailable,
                readResult.FailureReason.ToString());
        }

        ClipboardCapturePolicyDecision decision = await policyChain
            .EvaluateAsync(readResult.Snapshot, cancellationToken)
            .ConfigureAwait(false);
        if (!decision.ShouldCapture)
        {
            return new ClipboardCaptureResult(
                ClipboardCaptureStatus.Ignored,
                decision.ReasonCode);
        }

        ClipboardCapturedItem? item = ClipboardContentNormalizer.Normalize(
            readResult.Snapshot,
            decision,
            options);
        if (item is null)
        {
            return new ClipboardCaptureResult(
                ClipboardCaptureStatus.Ignored,
                "normalization-empty");
        }

        item.SourceApplicationIcon = await CaptureSourceIconAsync(
                item,
                sourceIconProvider,
                cancellationToken)
            .ConfigureAwait(false);

        ClipboardHistorySaveResult saveResult;
        try
        {
            saveResult = await store
                .SaveAsync(item, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 采集路径不能把可能包含正文的 Provider/文件异常向日志或 UI 透传。
            return new ClipboardCaptureResult(
                ClipboardCaptureStatus.Failed,
                "history-write-failed");
        }

        notifier.Publish(new ClipboardHistoryChangedEvent(
            saveResult.WasMerged
                ? ClipboardHistoryChangeKind.Updated
                : ClipboardHistoryChangeKind.Added,
            saveResult.ItemId));
        return new ClipboardCaptureResult(
            saveResult.WasMerged
                ? ClipboardCaptureStatus.Merged
                : ClipboardCaptureStatus.Stored,
            saveResult.WasMerged ? "adjacent-duplicate" : "stored",
            saveResult);
    }

    private static async ValueTask<ClipboardSourceApplicationIcon?> CaptureSourceIconAsync(
        ClipboardCapturedItem item,
        IClipboardSourceApplicationIconProvider? sourceIconProvider,
        CancellationToken cancellationToken)
    {
        if (sourceIconProvider is null)
        {
            return null;
        }

        ClipboardSourceApplicationIdentity identity = new(
            item.SourceProcessName ?? string.Empty,
            item.SourceExecutablePath,
            item.SourceApplicationUserModelId,
            item.SourcePackageFamilyName);
        if (string.IsNullOrWhiteSpace(identity.ExecutablePath) &&
            string.IsNullOrWhiteSpace(identity.ApplicationUserModelId))
        {
            return null;
        }

        try
        {
            ClipboardSourceApplicationIcon? icon = await sourceIconProvider
                .CaptureAsync(identity, cancellationToken)
                .ConfigureAwait(false);
            if (icon is null)
            {
                await Task.Delay(SourceIconRetryDelay, cancellationToken).ConfigureAwait(false);
                icon = await sourceIconProvider
                    .CaptureAsync(identity, cancellationToken)
                    .ConfigureAwait(false);
            }

            return icon is not null && ClipboardSourceApplicationIconRules.IsCanonical(icon)
                ? icon
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
