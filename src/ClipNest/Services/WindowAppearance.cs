using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace ClipNest.Services;

public static class WindowAppearance
{
    private const int BorderColorAttribute = 34;
    private const int CornerPreferenceAttribute = 33;
    private const int RoundCorners = 2;
    private const int NoBorderColor = -2;

    public static void Apply(Window window, bool isResizable)
    {
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(12),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = isResizable ? new Thickness(5) : new Thickness(0),
            UseAeroCaptionButtons = false
        });
        IntPtr handle = new WindowInteropHelper(window).Handle;
        int cornerPreference = RoundCorners;
        int borderColor = NoBorderColor;
        DwmSetWindowAttribute(handle, CornerPreferenceAttribute, ref cornerPreference, sizeof(int));
        DwmSetWindowAttribute(handle, BorderColorAttribute, ref borderColor, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int valueSize);
}
