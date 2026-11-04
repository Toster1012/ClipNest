using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClipNest.Services;

public sealed class ClipboardShortcut : IDisposable
{
    public const int ShortcutMessage = 0x8056;
    public const int PointerPressedMessage = 0x8057;
    private const int LowLevelKeyboardHook = 13;
    private const int LowLevelMouseHook = 14;
    private const int LeftButtonDownMessage = 0x0201;
    private const int RightButtonDownMessage = 0x0204;
    private const int MiddleButtonDownMessage = 0x0207;
    private const int ExtendedButtonDownMessage = 0x020B;
    private const int KeyDownMessage = 0x0100;
    private const int SystemKeyDownMessage = 0x0104;
    private const int QuitMessage = 0x0012;
    private const int VirtualKeyV = 0x56;
    private const int VirtualKeyLeftWindows = 0x5B;
    private const int VirtualKeyRightWindows = 0x5C;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyAlt = 0x12;
    private const byte VirtualKeyNoname = 0xFC;
    private const uint KeyUpFlag = 0x0002;
    private const uint InjectedFlag = 0x0010;
    private const int PressedMask = 0x8000;
    private const int ShutdownWaitMilliseconds = 1000;
    private readonly IntPtr _targetHandle;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
    private readonly HookCallback _callback;
    private readonly HookCallback _pointerCallback;
    private IntPtr _hook;
    private IntPtr _pointerHook;
    private uint _threadIdentifier;
    private bool _isChordActive;

    public ClipboardShortcut(IntPtr targetHandle)
    {
        _targetHandle = targetHandle;
        _callback = OnKeyboardMessage;
        _pointerCallback = OnPointerMessage;
        _thread = new Thread(RunMessageLoop) { IsBackground = true, Name = "ClipNest.WinV" };
        _thread.Start();
        _ready.Wait();
    }

    public bool IsAvailable { get; private set; }

    public void Dispose()
    {
        if (_threadIdentifier != 0)
        {
            PostThreadMessage(_threadIdentifier, QuitMessage, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(ShutdownWaitMilliseconds);
        }
    }

    private void RunMessageLoop()
    {
        _threadIdentifier = GetCurrentThreadId();
        PeekMessage(out NativeMessage initialMessage, IntPtr.Zero, 0, 0, 0);
        _hook = SetWindowsHookEx(LowLevelKeyboardHook, _callback, GetModuleHandle(null), 0);
        _pointerHook = SetWindowsHookEx(LowLevelMouseHook, _pointerCallback, GetModuleHandle(null), 0);
        IsAvailable = _hook != IntPtr.Zero;
        _ready.Set();

        if (IsAvailable == false && _pointerHook == IntPtr.Zero)
        {
            return;
        }

        try
        {
            while (GetMessage(out NativeMessage message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            UnhookWindowsHookEx(_hook);
            UnhookWindowsHookEx(_pointerHook);
        }
    }

    private IntPtr OnKeyboardMessage(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            KeyboardInput input = Marshal.PtrToStructure<KeyboardInput>(data);

            if (input.VirtualKey == VirtualKeyV && (input.Flags & InjectedFlag) == 0)
            {
                bool isDown = message.ToInt32() == KeyDownMessage || message.ToInt32() == SystemKeyDownMessage;
                bool isWindowsPressed = IsPressed(VirtualKeyLeftWindows) || IsPressed(VirtualKeyRightWindows);
                bool isOnlyWindows = isWindowsPressed && IsPressed(VirtualKeyControl) == false && IsPressed(VirtualKeyShift) == false && IsPressed(VirtualKeyAlt) == false;

                if (isDown && isOnlyWindows)
                {
                    if (_isChordActive == false)
                    {
                        _isChordActive = true;
                        keybd_event(VirtualKeyNoname, 0, 0, UIntPtr.Zero);
                        keybd_event(VirtualKeyNoname, 0, KeyUpFlag, UIntPtr.Zero);
                        PostMessage(_targetHandle, ShortcutMessage, IntPtr.Zero, IntPtr.Zero);
                    }

                    return new IntPtr(1);
                }

                if (isDown == false && _isChordActive)
                {
                    _isChordActive = false;
                    return new IntPtr(1);
                }
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private static bool IsPressed(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & PressedMask) != 0;
    }

    private IntPtr OnPointerMessage(int code, IntPtr message, IntPtr data)
    {
        int pointerMessage = message.ToInt32();

        if (code >= 0 && pointerMessage is LeftButtonDownMessage or RightButtonDownMessage or MiddleButtonDownMessage or ExtendedButtonDownMessage)
        {
            PointerInput input = Marshal.PtrToStructure<PointerInput>(data);
            PostMessage(_targetHandle, PointerPressedMessage, new IntPtr(input.X), new IntPtr(input.Y));
        }

        return CallNextHookEx(_pointerHook, code, message, data);
    }

    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr FirstParameter;
        public IntPtr SecondParameter;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hook, HookCallback callback, IntPtr module, uint threadIdentifier);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInformation);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, IntPtr handle, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr handle, uint minimum, uint maximum, uint removal);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr handle, int message, IntPtr firstParameter, IntPtr secondParameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadIdentifier, int message, IntPtr firstParameter, IntPtr secondParameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
