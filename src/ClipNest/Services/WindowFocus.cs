using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace ClipNest.Services;

public static class WindowFocus
{
    public static PasteTarget CaptureTarget()
    {
        IntPtr window = GetForegroundWindow();
        uint thread = GetWindowThreadProcessId(window, out uint process);
        GuiThreadInformation information = new GuiThreadInformation { Size = Marshal.SizeOf<GuiThreadInformation>() };
        IntPtr focusedControl = GetGUIThreadInfo(thread, ref information) ? information.Focus : IntPtr.Zero;
        return new PasteTarget(window, focusedControl, process);
    }

    public static async Task<bool> RestoreTargetAsync(PasteTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsTargetValid(target) == false)
        {
            return false;
        }

        if (IsForegroundWindow(target.Window) == false)
        {
            SetForegroundWindow(target.Window);
        }

        for (int attempt = 0; IsForegroundWindow(target.Window) == false; attempt++)
        {
            if (attempt >= 49 || IsTargetValid(target) == false)
            {
                return false;
            }

            await Task.Delay(10, cancellationToken);
        }

        if (IsTargetFocused(target))
        {
            return true;
        }

        if (target.Control == IntPtr.Zero || IsWindow(target.Control) == false || (target.Control != target.Window && IsChild(target.Window, target.Control) == false))
        {
            return false;
        }

        uint targetThread = GetWindowThreadProcessId(target.Window, IntPtr.Zero);
        uint currentThread = GetCurrentThreadId();
        bool attached = targetThread != currentThread && AttachThreadInput(currentThread, targetThread, true);

        try
        {
            if (IsForegroundWindow(target.Window))
            {
                SetFocus(target.Control);
            }
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }
        }

        return IsTargetFocused(target);
    }

    public static bool IsTargetValid(PasteTarget target)
    {
        GetWindowThreadProcessId(target.Window, out uint process);
        return target.Window != IntPtr.Zero && IsWindow(target.Window) && process == target.ProcessIdentifier && process != (uint)Environment.ProcessId;
    }

    public static bool IsTargetFocused(PasteTarget target)
    {
        if (IsTargetValid(target) == false || IsForegroundWindow(target.Window) == false)
        {
            return false;
        }

        GuiThreadInformation information = new GuiThreadInformation { Size = Marshal.SizeOf<GuiThreadInformation>() };
        uint thread = GetWindowThreadProcessId(target.Window, IntPtr.Zero);
        return target.Control == IntPtr.Zero || (GetGUIThreadInfo(thread, ref information) && information.Focus == target.Control);
    }

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
    private struct GuiThreadInformation
    {
        public int Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public WindowBounds CaretBounds;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInformation information);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

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

public readonly record struct PasteTarget(IntPtr Window, IntPtr Control, uint ProcessIdentifier);
