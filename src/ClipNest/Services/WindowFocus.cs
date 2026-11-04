using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipNest.Services;

public static class WindowFocus
{
    public static bool IsForegroundWindow(IntPtr handle)
    {
        return GetForegroundWindow() == handle;
    }

    public static bool IsInsideWindow(IntPtr handle, int x, int y)
    {
        return GetWindowRect(handle, out WindowBounds bounds) && x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom;
    }

    public static bool IsApplicationWindowAtPoint(int x, int y)
    {
        IntPtr handle = WindowFromPoint(new ScreenPoint { X = x, Y = y });
        GetWindowThreadProcessId(handle, out uint processIdentifier);
        return processIdentifier == (uint)Environment.ProcessId;
    }

    public static void Activate(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        uint currentThread = GetCurrentThreadId();
        bool isAttached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            SetFocus(handle);
            window.Activate();
            window.Focus();
        }
        finally
        {
            if (isAttached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowBounds
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out WindowBounds bounds);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(ScreenPoint point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processIdentifier);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, IntPtr processIdentifier);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint firstThread, uint secondThread, [MarshalAs(UnmanagedType.Bool)] bool isAttached);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr handle);
}
