using System.Runtime.Versioning;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Desktop;

[SupportedOSPlatform("macos")]
public static class MacOSApplicationIdentity
{
    public const string ProductName = "闪剪";

    public static void SetProcessName()
    {
        try
        {
            using NativeAutoreleasePool pool = new();
            nint processInfo = MacOSNativeMethods.SendIntPtr(
                MacOSNativeMethods.GetClass("NSProcessInfo"),
                ObjectiveC.GetSelector("processInfo"));
            if (processInfo == 0)
            {
                return;
            }

            nint name = ObjectiveC.CreateString(ProductName);
            try
            {
                MacOSNativeMethods.SendVoidWithIntPtr(
                    processInfo,
                    ObjectiveC.GetSelector("setProcessName:"),
                    name);
            }
            finally
            {
                ObjectiveC.Release(name);
            }
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            // 原生显示名称属于外观能力；失败时不能阻断应用启动。
        }
    }

    public static void SetApplicationMenuTitle()
    {
        try
        {
            using NativeAutoreleasePool pool = new();
            nint application = MacOSNativeMethods.SendIntPtr(
                ObjectiveC.GetRequiredClass("NSApplication"),
                ObjectiveC.GetSelector("sharedApplication"));
            nint mainMenu = application == 0
                ? 0
                : MacOSNativeMethods.SendIntPtr(
                    application,
                    ObjectiveC.GetSelector("mainMenu"));
            nint firstItem = mainMenu == 0 ||
                MacOSNativeMethods.SendNUInt(
                    mainMenu,
                    ObjectiveC.GetSelector("numberOfItems")) == 0
                    ? 0
                    : MacOSNativeMethods.SendIntPtrWithNUInt(
                        mainMenu,
                        ObjectiveC.GetSelector("itemAtIndex:"),
                        0);
            if (firstItem == 0)
            {
                return;
            }

            nint title = ObjectiveC.CreateString(ProductName);
            try
            {
                MacOSNativeMethods.SendVoidWithIntPtr(
                    firstItem,
                    ObjectiveC.GetSelector("setTitle:"),
                    title);
                nint submenu = MacOSNativeMethods.SendIntPtr(
                    firstItem,
                    ObjectiveC.GetSelector("submenu"));
                if (submenu != 0)
                {
                    MacOSNativeMethods.SendVoidWithIntPtr(
                        submenu,
                        ObjectiveC.GetSelector("setTitle:"),
                        title);
                }
            }
            finally
            {
                ObjectiveC.Release(title);
            }
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            // 菜单标题属于外观能力；失败时保留系统默认菜单，不阻断主窗口。
        }
    }
}
