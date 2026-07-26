namespace SnapBoard.Platform.Windows;

public sealed class WindowsClipboardOptions
{
    public int EventQueueCapacity { get; init; } = 256;

    public int MaximumPayloadBytes { get; init; } = 64 * 1024 * 1024;

    public int MaximumFileCount { get; init; } = 4096;

    public IReadOnlyList<TimeSpan> OpenRetryDelays { get; init; } =
    [
        TimeSpan.FromMilliseconds(5),
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(80),
    ];

    internal WindowsClipboardSettings ToSettings()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(EventQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumPayloadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumFileCount, 1);
        ArgumentNullException.ThrowIfNull(OpenRetryDelays);

        TimeSpan[] delays = OpenRetryDelays.ToArray();
        if (delays.Any(delay => delay < TimeSpan.Zero || delay > TimeSpan.FromSeconds(1)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(OpenRetryDelays),
                "Clipboard retry delays must be between zero and one second.");
        }

        return new WindowsClipboardSettings(
            EventQueueCapacity,
            MaximumPayloadBytes,
            MaximumFileCount,
            delays);
    }
}

internal sealed record WindowsClipboardSettings(
    int EventQueueCapacity,
    int MaximumPayloadBytes,
    int MaximumFileCount,
    IReadOnlyList<TimeSpan> OpenRetryDelays);
