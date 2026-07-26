using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.MacOS.Clipboard;

namespace SnapBoard.Platform.MacOS.Tests;

public sealed class MacOSAutomaticPasteTests
{
    [Fact]
    public async Task AccessibilityDenialReturnsManualPasteWithoutInjection()
    {
        FakeMacOSPasteNative native = new() { AccessibilityPermission = false };
        MacOSAutomaticPaste paste = CreatePaste(native);

        AutomaticPasteResult result = await paste.TryPasteAsync(
            Assert.IsType<MacOSAutomaticPasteTarget>(native.Target),
            CancellationToken.None);

        Assert.Equal(AutomaticPasteStatus.ManualPasteRequired, result.Status);
        Assert.Equal(
            AutomaticPasteFailureReason.AccessibilityPermissionDenied,
            result.FailureReason);
        Assert.False(native.SendPasteCalled);
    }

    [Fact]
    public async Task TargetActivationFailureReturnsManualPaste()
    {
        FakeMacOSPasteNative native = new() { ActivateResult = false };
        MacOSAutomaticPaste paste = CreatePaste(native);

        AutomaticPasteResult result = await paste.TryPasteAsync(
            Assert.IsType<MacOSAutomaticPasteTarget>(native.Target),
            CancellationToken.None);

        Assert.Equal(AutomaticPasteStatus.ManualPasteRequired, result.Status);
        Assert.Equal(
            AutomaticPasteFailureReason.TargetActivationFailed,
            result.FailureReason);
        Assert.False(native.SendPasteCalled);
    }

    [Fact]
    public async Task RestoredTargetReceivesCommandV()
    {
        FakeMacOSPasteNative native = new();
        MacOSAutomaticPaste paste = CreatePaste(native);

        AutomaticPasteResult result = await paste.TryPasteAsync(
            Assert.IsType<MacOSAutomaticPasteTarget>(native.Target),
            CancellationToken.None);

        Assert.Equal(AutomaticPasteStatus.Pasted, result.Status);
        Assert.True(native.SendPasteCalled);
    }

    [Fact]
    public async Task EventCreationFailureReturnsManualPaste()
    {
        FakeMacOSPasteNative native = new() { SendPasteResult = false };
        MacOSAutomaticPaste paste = CreatePaste(native);

        AutomaticPasteResult result = await paste.TryPasteAsync(
            Assert.IsType<MacOSAutomaticPasteTarget>(native.Target),
            CancellationToken.None);

        Assert.Equal(AutomaticPasteStatus.ManualPasteRequired, result.Status);
        Assert.Equal(AutomaticPasteFailureReason.InputInjectionBlocked, result.FailureReason);
    }

    private static MacOSAutomaticPaste CreatePaste(FakeMacOSPasteNative native) => new(
        native,
        MacOSPollingBackoffTests.CreateSettings(),
        new ImmediateDelay());

    private sealed class ImmediateDelay : IAsyncDelay
    {
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
