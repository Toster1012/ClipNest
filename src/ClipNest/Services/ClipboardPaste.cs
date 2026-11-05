using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ClipNest.Services;

public static class ClipboardPaste
{
    public static async Task PasteAsync(PasteTarget target, IReadOnlyList<DataObject> items, Action onTargetRestored, CancellationToken cancellationToken)
    {
        await WaitForModifiersAsync(cancellationToken);

        if (await WindowFocus.RestoreTargetAsync(target, cancellationToken) == false)
        {
            throw new InvalidOperationException("Не удалось вернуть фокус в исходное приложение. Выберите поле и откройте Win+V снова.");
        }

        onTargetRestored();
        await Task.Delay(50, cancellationToken);

        foreach (DataObject item in items)
        {
            EnsureTarget(target);
            await SetClipboardAsync(item, cancellationToken);
            await WaitForModifiersAsync(cancellationToken);
            EnsureTarget(target);
            SendPaste();
            await Task.Delay(item.ContainsText() ? 100 : 300, cancellationToken);
            EnsureTarget(target);
        }
    }

    private static void EnsureTarget(PasteTarget target)
    {
        if (WindowFocus.IsTargetFocused(target) == false)
        {
            throw new InvalidOperationException("Вставка остановлена: активное окно или поле ввода изменилось. Уже вставленные записи повторять не нужно.");
        }
    }

    private static async Task SetClipboardAsync(DataObject data, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                Clipboard.SetDataObject(data, true);
                return;
            }
            catch (COMException) when (attempt < 7)
            {
                await Task.Delay(90, cancellationToken);
            }
        }
    }

    private static async Task WaitForModifiersAsync(CancellationToken cancellationToken)
    {
        int[] keys = { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x0D, 0x01 };

        for (int attempt = 0; attempt < 150; attempt++)
        {
            bool isPressed = false;

            foreach (int key in keys)
            {
                isPressed |= (GetAsyncKeyState(key) & 0x8000) != 0;
            }

            if (isPressed == false)
            {
                return;
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new InvalidOperationException("Для вставки отпустите кнопки мыши, Win, Ctrl, Shift и Alt и повторите выбор.");
    }

    private static void SendPaste()
    {
        NativeInput[] inputs = { CreateKey(0x11, false), CreateKey(0x56, false), CreateKey(0x56, true), CreateKey(0x11, true) };

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
        {
            NativeInput[] releases = { CreateKey(0x56, true), CreateKey(0x11, true) };
            SendInput((uint)releases.Length, releases, Marshal.SizeOf<NativeInput>());
            throw new InvalidOperationException("Windows заблокировала вставку. Если приложение запущено от администратора, запустите ClipNest с такими же правами.");
        }
    }

    private static NativeInput CreateKey(ushort key, bool isUp) => new NativeInput { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = isUp ? 2u : 0u } } };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInformation;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
