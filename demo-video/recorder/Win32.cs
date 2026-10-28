using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FindIt.DemoRecorder;

internal static partial class Win32
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    [LibraryImport("user32.dll")] public static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(nint hwnd, int command);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindowVisible(nint hwnd);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(nint hwnd, out Rect rect);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetCursorPos(int x, int y);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out Point point);
    [LibraryImport("user32.dll")] private static partial uint SendInput(uint count, [In] Input[] inputs, int size);
    [LibraryImport("dwmapi.dll")] private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);

    public static nint WaitForMainWindow(Process process, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            process.Refresh();
            if (process.HasExited) throw new InvalidOperationException($"FindIt exited with code {process.ExitCode}.");
            if (process.MainWindowHandle != 0 && IsWindowVisible(process.MainWindowHandle)) return process.MainWindowHandle;
            Thread.Sleep(100);
        }
        throw new TimeoutException("FindIt did not show its window.");
    }

    public static void Place(nint hwnd, int x, int y, int width, int height)
    {
        ShowWindow(hwnd, 9); // SW_RESTORE
        SetWindowPos(hwnd, 0, x, y, width, height, 0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE
        Thread.Sleep(300);
    }

    /// <summary>Grows the window by a pixel and back, which makes it repaint without visibly changing.</summary>
    public static void Nudge(nint hwnd)
    {
        GetWindowRect(hwnd, out var w);
        SetWindowPos(hwnd, 0, 0, 0, w.Right - w.Left + 1, w.Bottom - w.Top, 0x0002 | 0x0004 | 0x0010); // SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE
        Thread.Sleep(60);
        SetWindowPos(hwnd, 0, 0, 0, w.Right - w.Left, w.Bottom - w.Top, 0x0002 | 0x0004 | 0x0010);
    }

    /// <summary>The window's visible bounds in physical pixels, which is what Windows.Graphics.Capture records.</summary>
    public static Rect Bounds(nint hwnd)
    {
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(hwnd, 9, out var rect, Marshal.SizeOf<Rect>())); // DWMWA_EXTENDED_FRAME_BOUNDS
        return rect;
    }

    public static (int X, int Y) Cursor() { GetCursorPos(out var p); return (p.X, p.Y); }

    /// <summary>Moves the real pointer along an eased path, like a hand would.</summary>
    public static void Glide(int x, int y, int milliseconds)
    {
        var (sx, sy) = Cursor();
        var steps = Math.Max(1, milliseconds / 8);
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var e = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            SetCursorPos((int)Math.Round(sx + (x - sx) * e), (int)Math.Round(sy + (y - sy) * e));
            Thread.Sleep(8);
        }
    }

    public static void Click()
    {
        Send([Mouse(0x0002), Mouse(0x0004)]); // left down, left up
    }

    public static void Wheel(int delta)
    {
        Send([new Input { Type = 0, Union = new() { Mouse = new() { Data = unchecked((uint)delta), Flags = 0x0800 } } }]); // MOUSEEVENTF_WHEEL
    }

    public static void Type(char c)
    {
        Send([Key(0, c, 0x0004), Key(0, c, 0x0004 | 0x0002)]); // KEYEVENTF_UNICODE, then key up
    }

    private static void Send(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length) throw new InvalidOperationException("Windows rejected synthetic input.");
    }

    private static Input Mouse(uint flags) => new() { Type = 0, Union = new() { Mouse = new() { Flags = flags } } };
    private static Input Key(ushort vk, char c, uint flags) => new() { Type = 1, Union = new() { Keyboard = new() { VirtualKey = vk, Scan = c, Flags = flags } } };

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey; public ushort Scan; public uint Flags, Time; public nint Extra; }
}
