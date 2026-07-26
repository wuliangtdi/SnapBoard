namespace SnapBoard.Platform.MacOS;

public sealed class MacOSClipboardOptions
{
    public int EventQueueCapacity { get; init; } = 256;

    public int MaximumPayloadBytes { get; init; } = 64 * 1024 * 1024;

    public int MaximumFileCount { get; init; } = 4096;

    public TimeSpan ActivePollingInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan IdlePollingInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public int UnchangedPollsBeforeIdle { get; init; } = 10;

    public TimeSpan TargetActivationPollInterval { get; init; } = TimeSpan.FromMilliseconds(40);

    public int TargetActivationAttempts { get; init; } = 8;

    internal MacOSClipboardSettings ToSettings()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(EventQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumPayloadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumFileCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(UnchangedPollsBeforeIdle, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(TargetActivationAttempts, 1);

        ValidateInterval(ActivePollingInterval, nameof(ActivePollingInterval));
        ValidateInterval(IdlePollingInterval, nameof(IdlePollingInterval));
        ValidateInterval(TargetActivationPollInterval, nameof(TargetActivationPollInterval));

        if (IdlePollingInterval < ActivePollingInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(IdlePollingInterval),
                "Idle polling cannot be faster than active polling.");
        }

        return new MacOSClipboardSettings(
            EventQueueCapacity,
            MaximumPayloadBytes,
            MaximumFileCount,
            ActivePollingInterval,
            IdlePollingInterval,
            UnchangedPollsBeforeIdle,
            TargetActivationPollInterval,
            TargetActivationAttempts);
    }

    private static void ValidateInterval(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.FromMilliseconds(20) || value > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Polling intervals must be between 20 milliseconds and 5 seconds.");
        }
    }
}

internal sealed record MacOSClipboardSettings(
    int EventQueueCapacity,
    int MaximumPayloadBytes,
    int MaximumFileCount,
    TimeSpan ActivePollingInterval,
    TimeSpan IdlePollingInterval,
    int UnchangedPollsBeforeIdle,
    TimeSpan TargetActivationPollInterval,
    int TargetActivationAttempts);
