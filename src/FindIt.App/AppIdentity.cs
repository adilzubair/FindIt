using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FindIt.App;

internal static class AppIdentity
{
    // Keep the portable launcher and application in the same Windows taskbar group.
    internal const string AppUserModelId = "FindIt.Desktop";

    internal static void Initialize()
    {
        // Windows requires this before creating any top-level application windows.
        // Window.Icon and the executable resource supply the small/large shell icons.
        var result = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        if (result < 0)
            Trace.WriteLine($"Windows could not apply the FindIt taskbar identity (0x{result:X8}).");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
