using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DualScreen.Core;
using DualScreen.Display;
using DualScreen.Net;
using static DualScreen.Input.PointerNative;

namespace DualScreen.Input;

/// <summary>
/// Host side: replays touch, pen, mouse and keyboard events from a receiver onto the monitor being streamed.
/// Touch uses the touch-injection API (real multi-touch: Windows sees a touchscreen on that monitor), pen uses a
/// synthetic pen device (pressure, barrel button, eraser), mouse and keyboard use SendInput with scan codes.
/// Runs as a normal user: windows of elevated programs (UIPI) cannot be controlled, as with any non-admin tool.
/// </summary>
public sealed class InputInjector : IDisposable
{
    private const int MaxContacts = 10;
    private readonly object _gate = new();
    private readonly Dictionary<byte, Contact> _contacts = new();
    private readonly HashSet<(ushort scan, bool ext)> _keysDown = new();
    private readonly HashSet<MouseButtonId> _buttonsDown = new();
    private readonly Timer _keepAlive;
    private IntPtr _monitor;
    private int _videoW, _videoH;
    private bool _touchReady, _touchFailed, _touchAsMouse;
    private int _touchFailures;
    private byte? _mouseFinger;
    private IntPtr _penDevice;
    private bool _penFailed;
    private (int x, int y, uint pressure, PenButtons buttons, bool contact)? _pen;
    private long _lastInjectTicks;
    private int _errorsLogged;

    private sealed class Contact
    {
        public uint PointerId;
        public int X, Y;
        public uint Pressure;
        public bool Injected; // DOWN already sent
    }

    public InputInjector()
    {
        _keepAlive = new Timer(_ => KeepAlive(), null, 50, 50);
    }

    /// <summary>Monitor shown to the receiver and the size of the video frame the receiver's coordinates refer to.</summary>
    public void SetTarget(IntPtr monitor, int videoW, int videoH)
    {
        lock (_gate)
        {
            ReleaseAllLocked();
            _monitor = monitor;
            _videoW = videoW;
            _videoH = videoH;
        }
    }

    public void ClearTarget()
    {
        lock (_gate)
        {
            ReleaseAllLocked();
            _monitor = IntPtr.Zero;
        }
    }

    public void Handle(ReadOnlySpan<byte> p)
    {
        if (p.Length < 1) return;
        lock (_gate)
        {
            if (_monitor == IntPtr.Zero) return;
            try
            {
                switch ((InputKind)p[0])
                {
                    case InputKind.Touch when p.Length >= 9:
                        OnTouch(p[1], (ContactState)p[2], U16(p, 3), U16(p, 5), U16(p, 7));
                        break;
                    case InputKind.Pen when p.Length >= 9:
                        OnPen((PenState)p[1], U16(p, 2), U16(p, 4), U16(p, 6), (PenButtons)p[8]);
                        break;
                    case InputKind.MouseMove when p.Length >= 5:
                        MouseAt(U16(p, 1), U16(p, 3), 0, 0);
                        break;
                    case InputKind.MouseButton when p.Length >= 7:
                        OnMouseButton((MouseButtonId)p[1], p[2] != 0, U16(p, 3), U16(p, 5));
                        break;
                    case InputKind.Wheel when p.Length >= 4:
                        SendMouse(0, 0, (uint)BinaryPrimitives.ReadInt16BigEndian(p[2..]), p[1] != 0 ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL);
                        break;
                    case InputKind.Key when p.Length >= 4:
                        OnKey(U16(p, 1), (p[3] & 1) != 0, (p[3] & 2) != 0);
                        break;
                    case InputKind.ReleaseAll:
                        ReleaseAllLocked();
                        break;
                }
            }
            catch (Exception ex) { LogOnce("Input injection failed: " + ex.Message); }
        }
    }

    private static ushort U16(ReadOnlySpan<byte> p, int at) => BinaryPrimitives.ReadUInt16BigEndian(p[at..]);

    // ------------------------------------------------------------------ coordinates

    /// <summary>Maps 0..65535 video coordinates to desktop pixels, undoing the letterbox of mirror mode.</summary>
    private bool Map(ushort nx, ushort ny, out int x, out int y)
    {
        x = y = 0;
        var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (!Native.GetMonitorInfo(_monitor, ref info)) return false;
        var r = info.rcMonitor;
        int mw = Math.Max(1, r.Right - r.Left), mh = Math.Max(1, r.Bottom - r.Top);
        int vw = _videoW > 0 ? _videoW : mw, vh = _videoH > 0 ? _videoH : mh;
        // Same letterbox math as FrameConverter.
        double scale = Math.Min(vw / (double)mw, vh / (double)mh);
        int dw = (int)Math.Round(mw * scale) & ~1, dh = (int)Math.Round(mh * scale) & ~1;
        int dx = ((vw - dw) / 2) & ~1, dy = ((vh - dh) / 2) & ~1;
        double vx = nx / 65535.0 * vw, vy = ny / 65535.0 * vh;
        double px = (vx - dx) / Math.Max(dw, 1) * mw, py = (vy - dy) / Math.Max(dh, 1) * mh;
        x = r.Left + (int)Math.Clamp(Math.Round(px), 0, mw - 1);
        y = r.Top + (int)Math.Clamp(Math.Round(py), 0, mh - 1);
        return true;
    }

    // ------------------------------------------------------------------ touch

    private void OnTouch(byte id, ContactState state, ushort nx, ushort ny, ushort pressure)
    {
        if (!Map(nx, ny, out int x, out int y)) return;
        if (_touchAsMouse || !EnsureTouch()) { TouchAsMouse(id, state, nx, ny); return; }
        if (!_contacts.TryGetValue(id, out var c))
        {
            if (state is ContactState.Up or ContactState.Cancel) return;
            if (_contacts.Count >= MaxContacts) return;
            uint pid = 0;
            while (_contacts.Values.Any(v => v.PointerId == pid)) pid++;
            c = new Contact { PointerId = pid };
            _contacts[id] = c;
        }
        // UP must repeat the last UPDATE position (InjectTouchInput rule).
        if (state is not (ContactState.Up or ContactState.Cancel)) { c.X = x; c.Y = y; }
        c.Pressure = pressure == 0 ? 512u : Math.Min(pressure, (ushort)1024);
        InjectTouchFrame(id, state);
    }

    /// <summary>Fallback: the first finger drives the mouse (tap = click, drag = drag); other fingers are ignored.</summary>
    private void TouchAsMouse(byte id, ContactState state, ushort nx, ushort ny)
    {
        switch (state)
        {
            case ContactState.Down when _mouseFinger == null:
                _mouseFinger = id;
                OnMouseButton(MouseButtonId.Left, true, nx, ny);
                break;
            case ContactState.Move when _mouseFinger == id:
                MouseAt(nx, ny, 0, 0);
                break;
            case ContactState.Up or ContactState.Cancel when _mouseFinger == id:
                OnMouseButton(MouseButtonId.Left, false, nx, ny);
                _mouseFinger = null;
                break;
        }
    }

    private bool EnsureTouch()
    {
        if (_touchReady) return true;
        if (_touchFailed) return false;
        if (InitializeTouchInjection(MaxContacts, TOUCH_FEEDBACK_DEFAULT)) { _touchReady = true; return true; }
        _touchFailed = true;
        Log.Warn($"Touch injection unavailable (error {Marshal.GetLastWin32Error()})");
        return false;
    }

    /// <summary>Injects one frame with every active contact (the API wants the whole set each time).</summary>
    private void InjectTouchFrame(byte? changedId, ContactState changedState)
    {
        if (_contacts.Count == 0) return;
        var frame = new POINTER_TOUCH_INFO[_contacts.Count];
        var ended = new List<byte>();
        int i = 0;
        foreach (var (id, c) in _contacts)
        {
            uint flags;
            if (id == changedId && changedState == ContactState.Up) { flags = POINTER_FLAG_UP; ended.Add(id); }
            else if (id == changedId && changedState == ContactState.Cancel) { flags = POINTER_FLAG_UP | POINTER_FLAG_CANCELED; ended.Add(id); }
            else if (!c.Injected) { flags = POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT; c.Injected = true; }
            else flags = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
            frame[i++] = new POINTER_TOUCH_INFO
            {
                pointerInfo = new POINTER_INFO
                {
                    pointerType = PT_TOUCH,
                    pointerId = c.PointerId,
                    pointerFlags = flags,
                    ptPixelLocation = new POINT { X = c.X, Y = c.Y },
                },
                touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_ORIENTATION | TOUCH_MASK_PRESSURE,
                rcContact = new RECT { Left = c.X - 2, Top = c.Y - 2, Right = c.X + 2, Bottom = c.Y + 2 },
                orientation = 90,
                pressure = c.Pressure,
            };
        }
        // A contact that never got its DOWN cannot be lifted: just forget it.
        if (!InjectTouchInput((uint)frame.Length, frame))
        {
            int err = Marshal.GetLastWin32Error();
            LogOnce($"InjectTouchInput failed ({err})");
            if (err == 87) // ERROR_INVALID_PARAMETER: Windows cancelled every contact
            {
                _contacts.Clear();
                if (++_touchFailures >= 3)
                {
                    // Some systems refuse injected touch: keep the screen usable with touch → mouse.
                    _touchAsMouse = true;
                    Log.Warn("Touch injection keeps failing; emulating touch with the mouse");
                }
                return;
            }
        }
        else _touchFailures = 0;
        foreach (var id in ended) _contacts.Remove(id);
        _lastInjectTicks = Environment.TickCount64;
    }

    // ------------------------------------------------------------------ pen

    private void OnPen(PenState state, ushort nx, ushort ny, ushort pressure, PenButtons buttons)
    {
        if (_penDevice == IntPtr.Zero && !_penFailed)
        {
            _penDevice = CreateSyntheticPointerDevice(PT_PEN, 1, POINTER_FEEDBACK_DEFAULT);
            if (_penDevice == IntPtr.Zero) { _penFailed = true; Log.Warn($"Synthetic pen unavailable ({Marshal.GetLastWin32Error()})"); }
        }
        if (_penDevice == IntPtr.Zero) return;
        if (!Map(nx, ny, out int x, out int y)) return;
        uint flags = state switch
        {
            PenState.Down => POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_FIRSTBUTTON,
            PenState.Move => POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_FIRSTBUTTON,
            PenState.Up => POINTER_FLAG_UP | POINTER_FLAG_INRANGE,
            PenState.Leave => POINTER_FLAG_UPDATE,
            _ => POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE,
        };
        if (state == PenState.Up && _pen is { } last) { x = last.x; y = last.y; }
        InjectPen(flags, x, y, pressure, buttons);
        _pen = state switch
        {
            PenState.Leave => null,
            _ => (x, y, (uint)pressure, buttons, state is PenState.Down or PenState.Move),
        };
    }

    private void InjectPen(uint flags, int x, int y, uint pressure, PenButtons buttons)
    {
        uint penFlags = 0;
        if (buttons.HasFlag(PenButtons.Barrel)) { penFlags |= PEN_FLAG_BARREL; flags |= POINTER_FLAG_SECONDBUTTON; }
        if (buttons.HasFlag(PenButtons.Inverted)) penFlags |= PEN_FLAG_INVERTED;
        if (buttons.HasFlag(PenButtons.Eraser)) penFlags |= PEN_FLAG_ERASER;
        var info = new[]
        {
            new POINTER_TYPE_INFO
            {
                type = PT_PEN,
                penInfo = new POINTER_PEN_INFO
                {
                    pointerInfo = new POINTER_INFO
                    {
                        pointerType = PT_PEN,
                        pointerFlags = flags | POINTER_FLAG_PRIMARY,
                        ptPixelLocation = new POINT { X = x, Y = y },
                    },
                    penFlags = penFlags,
                    penMask = PEN_MASK_PRESSURE,
                    pressure = Math.Min(pressure, 1024u),
                },
            },
        };
        if (!InjectSyntheticPointerInput(_penDevice, info, 1)) LogOnce($"Pen injection failed ({Marshal.GetLastWin32Error()})");
        _lastInjectTicks = Environment.TickCount64;
    }

    /// <summary>Press-and-hold needs repeated frames, otherwise Windows cancels the contact.</summary>
    private void KeepAlive()
    {
        lock (_gate)
        {
            if (Environment.TickCount64 - _lastInjectTicks < 45) return;
            try
            {
                if (_contacts.Count > 0 && _touchReady) InjectTouchFrame(null, ContactState.Move);
                if (_pen is { contact: true } p && _penDevice != IntPtr.Zero)
                    InjectPen(POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_FIRSTBUTTON, p.x, p.y, p.pressure, p.buttons);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ mouse / keyboard

    private void MouseAt(ushort nx, ushort ny, uint data, uint extraFlags)
    {
        if (!Map(nx, ny, out int x, out int y)) return;
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), vh = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        int ax = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        int ay = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        SendMouse(ax, ay, data, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | extraFlags);
    }

    private void OnMouseButton(MouseButtonId b, bool down, ushort nx, ushort ny)
    {
        (uint flags, uint data) = (b, down) switch
        {
            (MouseButtonId.Left, true) => (MOUSEEVENTF_LEFTDOWN, 0u),
            (MouseButtonId.Left, false) => (MOUSEEVENTF_LEFTUP, 0u),
            (MouseButtonId.Right, true) => (MOUSEEVENTF_RIGHTDOWN, 0u),
            (MouseButtonId.Right, false) => (MOUSEEVENTF_RIGHTUP, 0u),
            (MouseButtonId.Middle, true) => (MOUSEEVENTF_MIDDLEDOWN, 0u),
            (MouseButtonId.Middle, false) => (MOUSEEVENTF_MIDDLEUP, 0u),
            (MouseButtonId.X1, _) => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 1u),
            _ => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 2u),
        };
        if (down) _buttonsDown.Add(b); else _buttonsDown.Remove(b);
        MouseAt(nx, ny, data, flags);
    }

    private static void SendMouse(int dx, int dy, uint data, uint flags)
    {
        var input = new[] { new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } } } };
        SendInput(1, input, Marshal.SizeOf<INPUT>());
    }

    private void OnKey(ushort scan, bool up, bool extended)
    {
        if (scan == 0) return;
        if (up) _keysDown.Remove((scan, extended)); else _keysDown.Add((scan, extended));
        SendKey(scan, up, extended);
    }

    private static void SendKey(ushort scan, bool up, bool extended)
    {
        uint flags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
        var input = new[] { new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = scan, dwFlags = flags } } } };
        SendInput(1, input, Marshal.SizeOf<INPUT>());
    }

    // ------------------------------------------------------------------ cleanup

    /// <summary>Lifts everything the receiver was holding (keys, buttons, fingers, pen) so nothing stays stuck.</summary>
    public void ReleaseAll()
    {
        lock (_gate) ReleaseAllLocked();
    }

    private void ReleaseAllLocked()
    {
        try
        {
            foreach (var (scan, ext) in _keysDown) SendKey(scan, true, ext);
            _keysDown.Clear();
            foreach (var b in _buttonsDown.ToList())
            {
                uint flags = b switch
                {
                    MouseButtonId.Left => MOUSEEVENTF_LEFTUP, MouseButtonId.Right => MOUSEEVENTF_RIGHTUP,
                    MouseButtonId.Middle => MOUSEEVENTF_MIDDLEUP, _ => MOUSEEVENTF_XUP,
                };
                SendMouse(0, 0, b == MouseButtonId.X1 ? 1u : b == MouseButtonId.X2 ? 2u : 0u, flags);
            }
            _buttonsDown.Clear();
            if (_contacts.Count > 0 && _touchReady)
            {
                foreach (var id in _contacts.Keys.ToList())
                {
                    if (!_contacts.TryGetValue(id, out var c)) continue;
                    if (c.Injected) InjectTouchFrame(id, ContactState.Cancel);
                    else _contacts.Remove(id);
                }
            }
            _contacts.Clear();
            _mouseFinger = null;
            if (_pen is { } p && _penDevice != IntPtr.Zero)
            {
                if (p.contact) InjectPen(POINTER_FLAG_UP | POINTER_FLAG_INRANGE, p.x, p.y, 0, PenButtons.None);
                InjectPen(POINTER_FLAG_UPDATE, p.x, p.y, 0, PenButtons.None);
            }
            _pen = null;
        }
        catch (Exception ex) { LogOnce("Releasing input failed: " + ex.Message); }
    }

    private void LogOnce(string msg)
    {
        if (_errorsLogged++ < 20) Log.Warn(msg);
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        lock (_gate)
        {
            ReleaseAllLocked();
            if (_penDevice != IntPtr.Zero) DestroySyntheticPointerDevice(_penDevice);
            _penDevice = IntPtr.Zero;
        }
    }
}
