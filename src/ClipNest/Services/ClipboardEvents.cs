using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClipNest.Services;

public sealed class ClipboardEvents : IDisposable
{
    public event Action? ClipboardChanged;
    public event Action? HotkeyPressed;
    public event Action<int, int>? PointerPressed;

    private const int WindowMessageClipboardUpdate = 0x031D;
    private readonly HwndSource _source;
    private readonly ClipboardShortcut _shortcut;

    public ClipboardEvents(IntPtr windowHandle)
    {
        _source = HwndSource.FromHwnd(windowHandle) ?? throw new InvalidOperationException("Не найдено окно приложения.");
        _source.AddHook(OnMessage);
        _shortcut = new ClipboardShortcut(windowHandle);

        if (AddClipboardFormatListener(windowHandle) == false)
        {
            Dispose();
            throw new InvalidOperationException("Windows не разрешила наблюдение за буфером обмена.");
        }
    }

    public bool IsHotkeyRegistered => _shortcut.IsAvailable;

    public void Dispose()
    {
        RemoveClipboardFormatListener(_source.Handle);
        _shortcut.Dispose();
        _source.RemoveHook(OnMessage);
    }

    private IntPtr OnMessage(IntPtr handle, int message, IntPtr firstParameter, IntPtr secondParameter, ref bool isHandled)
    {
        if (message == WindowMessageClipboardUpdate)
        {
            ClipboardChanged?.Invoke();
        }
        else if (message == ClipboardShortcut.ShortcutMessage)
        {
            HotkeyPressed?.Invoke();
            isHandled = true;
        }
        else if (message == ClipboardShortcut.PointerPressedMessage)
        {
            PointerPressed?.Invoke(firstParameter.ToInt32(), secondParameter.ToInt32());
            isHandled = true;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr handle);
}
