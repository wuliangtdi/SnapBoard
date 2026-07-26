namespace SnapBoard.Platform.Windows.Clipboard;

internal static class ClipboardRetryPolicy
{
    public static bool Try(
        Func<bool> operation,
        IReadOnlyList<TimeSpan> retryDelays,
        CancellationToken cancellationToken,
        Action<TimeSpan, CancellationToken>? wait = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(retryDelays);

        wait ??= Wait;

        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation())
            {
                return true;
            }

            if (attempt >= retryDelays.Count)
            {
                return false;
            }

            wait(retryDelays[attempt], cancellationToken);
        }
    }

    public static void Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        if (cancellationToken.WaitHandle.WaitOne(delay))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
