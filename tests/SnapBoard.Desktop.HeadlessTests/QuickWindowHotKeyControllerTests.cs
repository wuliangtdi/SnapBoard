using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Desktop.HeadlessTests;

public sealed class QuickWindowHotKeyControllerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(400);

    [Fact]
    public void PrimaryTriggerOpensQuickWindowOnce()
    {
        ControllerContext context = new();

        context.Controller.HandleTrigger(Primary());

        Assert.Equal(1, context.ShowCount);
        Assert.False(context.Controller.IsWaitingForSecondTrigger);
    }

    [Fact]
    public void TwoDoubleTriggersWithinIntervalOpenExactlyOnceAndThirdStartsNextPair()
    {
        ControllerContext context = new();

        context.Controller.HandleTrigger(Double());
        Assert.Equal(0, context.ShowCount);
        Assert.True(context.Controller.IsWaitingForSecondTrigger);

        context.Time.Advance(TimeSpan.FromMilliseconds(399));
        context.Controller.HandleTrigger(Double());
        Assert.Equal(1, context.ShowCount);
        Assert.False(context.Controller.IsWaitingForSecondTrigger);

        context.Time.Advance(TimeSpan.FromMilliseconds(1));
        context.Controller.HandleTrigger(Double());
        Assert.Equal(1, context.ShowCount);
        Assert.True(context.Controller.IsWaitingForSecondTrigger);
    }

    [Fact]
    public void TriggerAfterTimeoutExpiresFirstTriggerWithoutOpening()
    {
        ControllerContext context = new();
        context.Controller.HandleTrigger(Double());

        context.Time.Advance(TimeSpan.FromMilliseconds(401));
        context.Controller.HandleTrigger(Double());

        Assert.Equal(0, context.ShowCount);
        Assert.True(context.Controller.IsWaitingForSecondTrigger);
    }

    [Fact]
    public void AutoRepeatCannotCompleteDoubleTrigger()
    {
        ControllerContext context = new();
        context.Controller.HandleTrigger(Double());

        context.Controller.HandleTrigger(Double(isRepeat: true));

        Assert.Equal(0, context.ShowCount);
        Assert.True(context.Controller.IsWaitingForSecondTrigger);
        context.Controller.HandleTrigger(Double());
        Assert.Equal(1, context.ShowCount);
    }

    [Fact]
    public void SettingsCaptureClearsPendingSequence()
    {
        ControllerContext context = new();
        context.Controller.HandleTrigger(Double());
        context.CaptureActive = true;

        context.Controller.HandleTrigger(Double());
        context.CaptureActive = false;
        context.Controller.HandleTrigger(Double());

        Assert.Equal(0, context.ShowCount);
        Assert.True(context.Controller.IsWaitingForSecondTrigger);
    }

    [Fact]
    public void ProtectionNeverStartsOrCompletesPendingSequence()
    {
        ControllerContext context = new() { ProtectionActive = true };

        context.Controller.HandleTrigger(Double());
        Assert.False(context.Controller.IsWaitingForSecondTrigger);

        context.ProtectionActive = false;
        context.Controller.HandleTrigger(Double());
        context.ProtectionActive = true;
        context.Controller.HandleTrigger(Double());
        context.ProtectionActive = false;
        context.Controller.HandleTrigger(Double());

        Assert.Equal(0, context.ShowCount);
        Assert.True(context.Controller.IsWaitingForSecondTrigger);
    }

    [Fact]
    public void PrimaryMixedIntoDoubleSequenceResetsPair()
    {
        ControllerContext context = new();
        context.Controller.HandleTrigger(Double());

        context.Controller.HandleTrigger(Primary());
        context.Controller.HandleTrigger(Double());

        Assert.Equal(1, context.ShowCount);
        Assert.True(context.Controller.IsWaitingForSecondTrigger);
    }

    [Fact]
    public void ExplicitRequestBypassesGlobalHotKeyProtectionAndResetsPendingPair()
    {
        ControllerContext context = new() { ProtectionActive = true };
        context.ProtectionActive = false;
        context.Controller.HandleTrigger(Double());
        context.ProtectionActive = true;

        context.Controller.ShowExplicitly();

        Assert.Equal(1, context.ShowCount);
        Assert.False(context.Controller.IsWaitingForSecondTrigger);
    }

    [Theory]
    [InlineData("configuration-change")]
    [InlineData("double-slot-clear")]
    [InlineData("window-opened")]
    [InlineData("application-exit")]
    public void ExplicitLifecycleResetClearsPendingSequence(string scenario)
    {
        ControllerContext context = new();
        context.Controller.HandleTrigger(Double());

        context.Controller.Reset();
        context.Controller.HandleTrigger(Double());

        Assert.False(string.IsNullOrWhiteSpace(scenario));
        Assert.Equal(0, context.ShowCount);
        Assert.True(context.Controller.IsWaitingForSecondTrigger);
    }

    private static GlobalHotKeyTriggeredEventArgs Primary() =>
        new(GlobalHotKeySlot.Primary);

    private static GlobalHotKeyTriggeredEventArgs Double(bool isRepeat = false) =>
        new(GlobalHotKeySlot.Double, isRepeat);

    private sealed class ControllerContext
    {
        public ControllerContext()
        {
            Controller = new QuickWindowHotKeyController(
                Interval,
                () => CaptureActive,
                () => ProtectionActive,
                () => ShowCount++,
                Time);
        }

        public FakeTimeProvider Time { get; } = new();

        public QuickWindowHotKeyController Controller { get; }

        public bool CaptureActive { get; set; }

        public bool ProtectionActive { get; set; }

        public int ShowCount { get; private set; }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed) =>
            Interlocked.Add(ref _timestamp, (long)elapsed.TotalMilliseconds);
    }
}
