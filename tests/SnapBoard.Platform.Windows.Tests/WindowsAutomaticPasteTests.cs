using System.Runtime.InteropServices;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Windows.Clipboard;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Tests;

public sealed class WindowsAutomaticPasteTests
{
    [Fact]
    public void NativeInputLayoutMatchesWin32Abi()
    {
        int expectedSize = IntPtr.Size == 8 ? 40 : 28;

        Assert.Equal(expectedSize, Marshal.SizeOf<NativeInput>());
    }

    [Fact]
    public async Task PastesWhenTargetIsSameIntegrityAndCanBeActivated()
    {
        FakeWindowsPasteNative native = new()
        {
            ForegroundWindow = 123,
            TargetProcessId = 456,
            Integrity = IntegrityComparison.SameOrLower,
            SendPasteResult = true,
        };
        WindowsAutomaticPaste paste = new(native);
        IAutomaticPasteTarget target = Assert.IsAssignableFrom<IAutomaticPasteTarget>(
            paste.CaptureForegroundTarget());

        AutomaticPasteResult result = await paste.TryPasteAsync(target, CancellationToken.None);

        Assert.Equal(AutomaticPasteStatus.Pasted, result.Status);
        Assert.True(native.SendPasteCalled);
    }

    [Fact]
    public async Task HigherIntegrityTargetFallsBackToManualPaste()
    {
        FakeWindowsPasteNative native = new()
        {
            ForegroundWindow = 123,
            TargetProcessId = 456,
            Integrity = IntegrityComparison.Higher,
        };
        WindowsAutomaticPaste paste = new(native);
        IAutomaticPasteTarget target = Assert.IsAssignableFrom<IAutomaticPasteTarget>(
            paste.CaptureForegroundTarget());

        AutomaticPasteResult result = await paste.TryPasteAsync(target, CancellationToken.None);

        Assert.Equal(AutomaticPasteStatus.ManualPasteRequired, result.Status);
        Assert.Equal(AutomaticPasteFailureReason.HigherIntegrityTarget, result.FailureReason);
        Assert.False(native.SendPasteCalled);
    }

    [Fact]
    public async Task SendInputFailureFallsBackToManualPaste()
    {
        FakeWindowsPasteNative native = new()
        {
            ForegroundWindow = 123,
            TargetProcessId = 456,
            Integrity = IntegrityComparison.SameOrLower,
            SendPasteResult = false,
        };
        WindowsAutomaticPaste paste = new(native);
        IAutomaticPasteTarget target = Assert.IsAssignableFrom<IAutomaticPasteTarget>(
            paste.CaptureForegroundTarget());

        AutomaticPasteResult result = await paste.TryPasteAsync(target, CancellationToken.None);

        Assert.Equal(AutomaticPasteStatus.ManualPasteRequired, result.Status);
        Assert.Equal(AutomaticPasteFailureReason.InputInjectionBlocked, result.FailureReason);
    }

    [Fact]
    public async Task RestoresForegroundTargetWithoutInjectingInput()
    {
        FakeWindowsPasteNative native = new();
        WindowsAutomaticPaste paste = new(native);
        IAutomaticPasteTarget target = Assert.IsAssignableFrom<IAutomaticPasteTarget>(
            paste.CaptureForegroundTarget());

        ForegroundActivationResult result =
            await paste.TryActivateTargetAsync(target, CancellationToken.None);

        Assert.Equal(ForegroundActivationStatus.Activated, result.Status);
        Assert.False(native.SendPasteCalled);
    }

    [Fact]
    public async Task RevalidatesWindowAndProcessImmediatelyBeforeSendInput()
    {
        FakeWindowsPasteNative native = new()
        {
            ChangeProcessAfterActivation = true,
        };
        WindowsAutomaticPaste paste = new(native);
        IAutomaticPasteTarget target = Assert.IsAssignableFrom<IAutomaticPasteTarget>(
            paste.CaptureForegroundTarget());

        AutomaticPasteResult result = await paste.TryPasteAsync(target, CancellationToken.None);

        Assert.Equal(AutomaticPasteStatus.TargetUnavailable, result.Status);
        Assert.False(native.SendPasteCalled);
    }

    [Fact]
    public async Task DoesNotInjectWhenAnotherWindowTakesForegroundBeforeSendInput()
    {
        FakeWindowsPasteNative native = new()
        {
            StealForegroundBeforeSend = true,
        };
        WindowsAutomaticPaste paste = new(native);
        IAutomaticPasteTarget target = Assert.IsAssignableFrom<IAutomaticPasteTarget>(
            paste.CaptureForegroundTarget());

        AutomaticPasteResult result = await paste.TryPasteAsync(target, CancellationToken.None);

        Assert.Equal(AutomaticPasteStatus.ManualPasteRequired, result.Status);
        Assert.Equal(AutomaticPasteFailureReason.TargetActivationFailed, result.FailureReason);
        Assert.False(native.SendPasteCalled);
    }
}
