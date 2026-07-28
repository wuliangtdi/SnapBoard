using System.Runtime.Versioning;
using SnapBoard.Platform.MacOS.Desktop;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Tests;

public sealed class MacOSDesktopSystemEventServiceTests
{
    [MacOSFact]
    [SupportedOSPlatform("macos")]
    public void NativeSourcesDeliverEventsAndStopAfterIdempotentDispose()
    {
        using ManualResetEventSlim wakeReceived = new();
        using ManualResetEventSlim networkReceived = new();
        int wakeCount = 0;
        int networkCount = 0;
        MacOSDesktopSystemEventService service = new(
            DirectPlatformMainThreadDispatcher.Instance);
        service.SystemResumed += (_, _) =>
        {
            Interlocked.Increment(ref wakeCount);
            wakeReceived.Set();
        };
        service.NetworkChanged += (_, _) =>
        {
            Interlocked.Increment(ref networkCount);
            networkReceived.Set();
        };

        service.Start();
        service.Start();

        Assert.True(service.IsWakeObservationActive);
        Assert.True(service.IsNetworkObservationActive);
        PostWorkspaceWakeNotification();
        Assert.True(wakeReceived.Wait(TimeSpan.FromSeconds(2)));
        service.InvokeNetworkCallbackProbe();
        Assert.True(networkReceived.Wait(TimeSpan.FromSeconds(2)));

        service.Dispose();
        service.Dispose();

        Assert.False(service.IsWakeObservationActive);
        Assert.False(service.IsNetworkObservationActive);
        int wakeCountAfterDispose = Volatile.Read(ref wakeCount);
        int networkCountAfterDispose = Volatile.Read(ref networkCount);
        PostWorkspaceWakeNotification();
        service.InvokeNetworkCallbackProbe();
        Assert.Equal(wakeCountAfterDispose, Volatile.Read(ref wakeCount));
        Assert.Equal(networkCountAfterDispose, Volatile.Read(ref networkCount));
    }

    private static void PostWorkspaceWakeNotification()
    {
        using NativeAutoreleasePool pool = new();
        nint workspace = MacOSNativeMethods.SendIntPtr(
            ObjectiveC.GetRequiredClass("NSWorkspace"),
            ObjectiveC.GetSelector("sharedWorkspace"));
        nint notificationCenter = MacOSNativeMethods.SendIntPtr(
            workspace,
            ObjectiveC.GetSelector("notificationCenter"));
        nint notificationName = ObjectiveC.CreateString("NSWorkspaceDidWakeNotification");
        try
        {
            MacOSNativeMethods.SendVoidWithIntPtrIntPtr(
                notificationCenter,
                ObjectiveC.GetSelector("postNotificationName:object:"),
                notificationName,
                0);
        }
        finally
        {
            ObjectiveC.Release(notificationName);
        }
    }
}
