using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Application.Clipboard;

public sealed class ClipboardCaptureService(
    IClipboardCapturePolicyChain policyChain,
    IClipboardHistoryStore store,
    ClipboardCaptureOptions options,
    IHistorySettingsService historySettings,
    ClipboardHistoryChangeNotifier notifier) : IClipboardCaptureService
{
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
}
