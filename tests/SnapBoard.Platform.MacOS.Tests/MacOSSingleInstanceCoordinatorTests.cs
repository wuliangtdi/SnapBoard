using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Desktop;

namespace SnapBoard.Platform.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacOSSingleInstanceCoordinatorTests
{
    [MacOSFact]
    public void SecondaryCannotReplacePrimaryBeforeListenerStarts()
    {
        const string applicationId =
            "com.wuliangtdi.snapboard.tests.secondary-before-listener";
        Assert.True(MacOSSingleInstanceCoordinator.TryAcquire(
            applicationId,
            SingleInstanceCommand.ActivateMainWindow,
            out MacOSSingleInstanceCoordinator? primary,
            out bool initialNotification));
        Assert.False(initialNotification);
        Assert.NotNull(primary);
        using (primary)
        {
            Assert.False(MacOSSingleInstanceCoordinator.TryAcquire(
                applicationId,
                SingleInstanceCommand.ShowQuickWindow,
                out MacOSSingleInstanceCoordinator? secondary,
                out bool primaryNotified));
            Assert.Null(secondary);
            Assert.False(primaryNotified);
        }
    }

    [MacOSFact]
    public async Task SecondaryInstanceNotifiesPrimaryWithBoundedCommand()
    {
        const string applicationId =
            "com.wuliangtdi.snapboard.tests.secondary-notification";
        Assert.True(MacOSSingleInstanceCoordinator.TryAcquire(
            applicationId,
            SingleInstanceCommand.ActivateMainWindow,
            out MacOSSingleInstanceCoordinator? primary,
            out bool initialNotification));
        Assert.False(initialNotification);
        Assert.NotNull(primary);
        using (primary)
        {
            TaskCompletionSource<SingleInstanceCommand> received =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            primary.CommandReceived += command => received.TrySetResult(command);
            primary.StartListening();

            Assert.False(MacOSSingleInstanceCoordinator.TryAcquire(
                applicationId,
                SingleInstanceCommand.ShowQuickWindow,
                out MacOSSingleInstanceCoordinator? secondary,
                out bool primaryNotified));
            Assert.Null(secondary);
            Assert.True(primaryNotified);
            Assert.Equal(
                SingleInstanceCommand.ShowQuickWindow,
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }
}
