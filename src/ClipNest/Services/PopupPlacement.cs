using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace ClipNest.Services;

public static class PopupPlacement
{
    private const int ScreenMargin = 16;
    private const int CursorOffset = 16;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;

    public static void Position(Window window, bool openInCorner, double preferredWidth, double preferredHeight)
    {
        System.Drawing.Point cursor = Forms.Cursor.Position;
        System.Drawing.Rectangle workArea = Forms.Screen.FromPoint(cursor).WorkingArea;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        SetWindowPos(handle, IntPtr.Zero, workArea.Left, workArea.Top, 0, 0, NoSize | NoZOrder | NoActivate);
        uint dpi = GetDpiForWindow(handle);
        double scale = dpi == 0 ? 1 : dpi / 96d;
        int marginX = Math.Min(ScreenMargin, Math.Max(0, (workArea.Width - 1) / 2));
        int marginY = Math.Min(ScreenMargin, Math.Max(0, (workArea.Height - 1) / 2));
        int width = (int)Math.Min(Math.Ceiling(preferredWidth * scale), workArea.Width - marginX * 2);
        int height = (int)Math.Min(Math.Ceiling(preferredHeight * scale), workArea.Height - marginY * 2);
        window.Width = width / scale;
        window.Height = height / scale;
        int minimumX = workArea.Left + marginX;
        int minimumY = workArea.Top + marginY;
        int maximumX = workArea.Right - marginX - width;
        int maximumY = workArea.Bottom - marginY - height;
        int left = maximumX;
        int top = maximumY;

        if (openInCorner == false)
        {
            left = cursor.X + CursorOffset;
            top = cursor.Y + CursorOffset;

            if (left > maximumX)
            {
                left = cursor.X - CursorOffset - width;
            }

            if (top > maximumY)
            {
                top = cursor.Y - CursorOffset - height;
            }
        }

        left = Math.Clamp(left, minimumX, maximumX);
        top = Math.Clamp(top, minimumY, maximumY);
        SetWindowPos(handle, IntPtr.Zero, left, top, width, height, NoZOrder | NoActivate);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
