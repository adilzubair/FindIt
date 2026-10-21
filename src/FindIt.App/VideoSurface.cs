using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FindIt.App;

/// <summary>Dedicated native airspace. Controls deliberately live outside this rectangle.</summary>
public sealed class VideoSurface : HwndHost
{
    public nint WindowHandle => Handle;
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var handle = CreateWindowEx(0, "STATIC", "FindIt video", 0x40000000 | 0x10000000 | 0x02000000, 0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight), hwndParent.Handle, 0, 0, 0);
        if (handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new(this, handle);
    }
    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int extendedStyle, string className, string title, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint hwnd);
}
