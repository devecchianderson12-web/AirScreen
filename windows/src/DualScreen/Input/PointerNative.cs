using System;
using System.Runtime.InteropServices;

namespace DualScreen.Input;

/// <summary>Pointer (touch/pen), SendInput and window-feedback interop shared by the injector (host) and the viewer (receiver).</summary>
internal static class PointerNative
{
    public const uint PT_POINTER = 1, PT_TOUCH = 2, PT_PEN = 3, PT_MOUSE = 4, PT_TOUCHPAD = 5;

    public const uint POINTER_FLAG_NEW = 0x1, POINTER_FLAG_INRANGE = 0x2, POINTER_FLAG_INCONTACT = 0x4,
        POINTER_FLAG_FIRSTBUTTON = 0x10, POINTER_FLAG_SECONDBUTTON = 0x20, POINTER_FLAG_PRIMARY = 0x2000,
        POINTER_FLAG_CONFIDENCE = 0x4000, POINTER_FLAG_CANCELED = 0x8000, POINTER_FLAG_DOWN = 0x10000,
        POINTER_FLAG_UPDATE = 0x20000, POINTER_FLAG_UP = 0x40000;

    public const uint TOUCH_MASK_CONTACTAREA = 0x1, TOUCH_MASK_ORIENTATION = 0x2, TOUCH_MASK_PRESSURE = 0x4;
    public const uint PEN_FLAG_BARREL = 0x1, PEN_FLAG_INVERTED = 0x2, PEN_FLAG_ERASER = 0x4;
    public const uint PEN_MASK_PRESSURE = 0x1;
    public const uint TOUCH_FEEDBACK_DEFAULT = 0x1;
    public const uint POINTER_FEEDBACK_DEFAULT = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint penFlags;
        public uint penMask;
        public uint pressure;
        public uint rotation;
        public int tiltX;
        public int tiltY;
    }

    [StructLayout(LayoutKind.Explicit, Size = 152)]
    public struct POINTER_TYPE_INFO
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public POINTER_TOUCH_INFO touchInfo;
        [FieldOffset(8)] public POINTER_PEN_INFO penInfo;
    }

    // ---- injection (host)
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool InjectSyntheticPointerInput(IntPtr device, [In] POINTER_TYPE_INFO[] pointerInfo, uint count);

    [DllImport("user32.dll")]
    public static extern void DestroySyntheticPointerDevice(IntPtr device);

    // ---- pointer messages (receiver)
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerType(uint pointerId, out uint pointerType);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerTouchInfo(uint pointerId, out POINTER_TOUCH_INFO info);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerPenInfo(uint pointerId, out POINTER_PEN_INFO info);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerInfo(uint pointerId, out POINTER_INFO info);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowFeedbackSetting(IntPtr hwnd, int feedback, uint flags, uint size, ref int configuration);

    // ---- SendInput
    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4,
        MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40,
        MOUSEEVENTF_XDOWN = 0x80, MOUSEEVENTF_XUP = 0x100, MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_HWHEEL = 0x1000,
        MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, [In] INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);
    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
}
