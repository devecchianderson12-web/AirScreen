using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using DualScreen.Core;
using DualScreen.Net;
using static DualScreen.Input.PointerNative;

namespace DualScreen.Receiver;

/// <summary>
/// Borderless, top-most, full-screen Win32 window that shows the remote monitor. No title bar, menu or overlay.
/// Touch, pen, mouse and keyboard are captured here and forwarded to the host.
/// Exits: Esc or Alt+F4 close it; the Windows key, Alt+Tab, Ctrl+Esc, Ctrl+Shift+Esc… let Windows show the
/// taskbar and the window minimizes itself (the connection stays open; click it in the taskbar to return).
/// "Do not disturb" mode swallows the whole keyboard with a low-level hook; the way out is holding the top-left
/// corner for 3 seconds (finger, pen or mouse) or stopping from the other PC. Convertibles in tent/stand/tablet posture
/// behave the same way (touch only).
/// </summary>
public sealed class ViewerWindow : IDisposable
{
    private const int CornerHoldMs = 3000;
    private const uint CornerTimerId = 1;
    private const uint TopmostTimerId = 2;
    private const uint RefocusTimerId = 3;

    private readonly IntPtr _monitor;
    private readonly bool _noKeyboard;
    /// <summary>Convertible in tent/stand/tablet posture (keyboard folded away): only touch and pen are used.</summary>
    private volatile bool _slate;
    private long _lastSwitchKeyMs = long.MinValue / 2;
    /// <summary>Keyboard and mouse are ignored ("No molestar" or tablet posture); exit with the corner gesture.</summary>
    private bool InputLocked => _noKeyboard || _slate;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _created = new();
    private readonly WndProc _wndProc;
    private readonly LowLevelKeyboardProc _hookProc;
    private IntPtr _hwnd, _hook;
    private int _width, _height;
    private long _shownTicks;
    private bool _closing;
    private readonly Dictionary<uint, byte> _touchIds = new();
    private readonly HashSet<(ushort, bool)> _keysDown = new();
    private int _mouseButtons;
    private CornerHold? _corner;
    private bool _cornerFired;


    private sealed class CornerHold
    {
        public bool IsTouch;
        public uint PointerId;
        public byte ContactId;
        public ushort X, Y;
    }

    /// <summary>Encoded INPUT frames to send to the host.</summary>
    public Action<byte[]>? Send;
    /// <summary>Picture rectangle inside the client area (letterbox), for coordinate mapping.</summary>
    public Func<(int l, int t, int r, int b)>? ContentRect;
    public event Action? ExitRequested;
    public event Action<int, int>? Resized;

    public IntPtr Handle => _hwnd;
    public int Width => _width;
    public int Height => _height;

    public ViewerWindow(IntPtr monitor, bool noKeyboard)
    {
        _monitor = monitor;
        _noKeyboard = noKeyboard;
        _wndProc = WindowProc;
        _hookProc = HookProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "DualScreen.Viewer" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public void Show()
    {
        _thread.Start();
        if (!_created.Wait(TimeSpan.FromSeconds(5)) || _hwnd == IntPtr.Zero)
            throw new InvalidOperationException("No se pudo crear la ventana de visualización");
    }

    public void Close()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _thread && _thread.IsAlive) _thread.Join(3000);
    }

    public void Dispose() => Close();

    // ------------------------------------------------------------------ window thread

    private void Run()
    {
        try
        {
            var hInst = GetModuleHandle(null);
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInst,
                lpszClassName = "DualScreenViewer",
                hbrBackground = GetStockObject(BLACK_BRUSH),
                hIcon = LoadIcon(hInst, (IntPtr)32512),
            };
            RegisterClassEx(ref wc); // fails harmlessly if already registered
            var r = MonitorRect(_monitor);
            _width = r.Right - r.Left;
            _height = r.Bottom - r.Top;
            _hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_APPWINDOW, "DualScreenViewer", "DualScreen", WS_POPUP,
                r.Left, r.Top, _width, _height, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { _created.Set(); return; }
            PrepareTouch(_hwnd);
            _slate = IsTabletPosture();
            ShowWindow(_hwnd, SW_SHOW);
            ForceForeground(_hwnd);
            _shownTicks = Environment.TickCount64;
            InstallHook();
            // Being the monitor has priority: stay above every other window (re-asserted every 2 s).
            SetTimer(_hwnd, TopmostTimerId, 2000, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("Viewer window failed", ex);
            _hwnd = IntPtr.Zero;
        }
        finally { _created.Set(); }

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        RemoveHook();
    }

    /// <summary>No edge swipes (Start, Task View, notifications), no touch/pen visual feedback, no press-and-hold right click.</summary>
    private static void PrepareTouch(IntPtr hwnd)
    {
        int off = 0;
        for (int f = 1; f <= 11; f++) SetWindowFeedbackSetting(hwnd, f, 0, 4, ref off);
        SetProp(hwnd, "MicrosoftTabletPenServiceProperty", (IntPtr)(0x1 | 0x8 | 0x10 | 0x10000));
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out IPropertyStore store) == 0)
            {
                var key = new PROPERTYKEY { fmtid = new Guid("32CE38B2-2C9A-41B1-9BC5-B3784394AA44"), pid = 2 };
                var v = new PROPVARIANT { vt = 11, boolVal = -1 }; // VT_BOOL true
                store.SetValue(ref key, ref v);
                store.Commit();
                Marshal.ReleaseComObject(store);
            }
        }
        catch (Exception ex) { Log.Info("Edge gestures could not be disabled: " + ex.Message); }
    }

    private static RECT MonitorRect(IntPtr monitor)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref mi))
        {
            var primary = MonitorFromPoint(new POINT(), 1);
            GetMonitorInfo(primary, ref mi);
        }
        return mi.rcMonitor;
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_POINTERDOWN:
                case WM_POINTERUPDATE:
                case WM_POINTERUP:
                case WM_POINTERCAPTURECHANGED:
                    if (OnPointer(msg, wParam)) return IntPtr.Zero;
                    break;
                case WM_POINTERLEAVE:
                    if (OnPointerLeave(wParam)) return IntPtr.Zero;
                    break;

                case WM_MOUSEMOVE:
                    if (_slate) return IntPtr.Zero;
                    if (!FromTouch()) { var (x, y) = Norm(lParam); Send?.Invoke(InputCodec.MouseMove(x, y)); }
                    return IntPtr.Zero;
                case WM_LBUTTONDOWN: return MouseButton(MouseButtonId.Left, true, lParam);
                case WM_LBUTTONUP: return MouseButton(MouseButtonId.Left, false, lParam);
                case WM_RBUTTONDOWN: return MouseButton(MouseButtonId.Right, true, lParam);
                case WM_RBUTTONUP: return MouseButton(MouseButtonId.Right, false, lParam);
                case WM_MBUTTONDOWN: return MouseButton(MouseButtonId.Middle, true, lParam);
                case WM_MBUTTONUP: return MouseButton(MouseButtonId.Middle, false, lParam);
                case WM_XBUTTONDOWN: MouseButton(HiWord(wParam) == 1 ? MouseButtonId.X1 : MouseButtonId.X2, true, lParam); return (IntPtr)1;
                case WM_XBUTTONUP: MouseButton(HiWord(wParam) == 1 ? MouseButtonId.X1 : MouseButtonId.X2, false, lParam); return (IntPtr)1;
                case WM_LBUTTONDBLCLK: case WM_RBUTTONDBLCLK: case WM_MBUTTONDBLCLK:
                    return IntPtr.Zero;
                case WM_MOUSEWHEEL:
                    if (_slate) return IntPtr.Zero;
                    Send?.Invoke(InputCodec.Wheel(false, (short)HiWord(wParam)));
                    return IntPtr.Zero;
                case WM_MOUSEHWHEEL:
                    if (_slate) return IntPtr.Zero;
                    Send?.Invoke(InputCodec.Wheel(true, (short)HiWord(wParam)));
                    return IntPtr.Zero;
                case WM_SETCURSOR:
                    // The PC's pointer is drawn inside the video: no local cursor on top of it.
                    if (LoWord(lParam) == HTCLIENT) { SetCursor(IntPtr.Zero); return (IntPtr)1; }
                    break;

                case WM_KEYDOWN:
                case WM_SYSKEYDOWN:
                    OnKey((int)wParam, lParam, false, msg == WM_SYSKEYDOWN);
                    return IntPtr.Zero;
                case WM_KEYUP:
                case WM_SYSKEYUP:
                    OnKey((int)wParam, lParam, true, msg == WM_SYSKEYUP);
                    return IntPtr.Zero;
                case WM_SYSCHAR:
                case WM_CHAR:
                    return IntPtr.Zero;
                case WM_SYSCOMMAND:
                    // No window menu (Alt+Space / F10) and no screen saver while in use.
                    int sc = (int)wParam & 0xFFF0;
                    if (sc is SC_KEYMENU or SC_SCREENSAVE or SC_MONITORPOWER) return IntPtr.Zero;
                    break;

                case WM_ACTIVATE:
                    OnActivate(LoWord(wParam) != 0, HiWord(wParam) != 0);
                    return IntPtr.Zero;
                case WM_TIMER when (uint)wParam == TopmostTimerId:
                    if (!IsIconic(hwnd)) SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    return IntPtr.Zero;
                case WM_TIMER when (uint)wParam == RefocusTimerId:
                    KillTimer(hwnd, RefocusTimerId);
                    if (!IsIconic(hwnd) && GetForegroundWindow() != hwnd) ForceForeground(hwnd);
                    return IntPtr.Zero;
                case WM_SETTINGCHANGE:
                    // Laptop <-> tent/stand/tablet posture (ConvertibleSlateMode) on convertibles like the Lenovo Flex.
                    bool slate = IsTabletPosture();
                    if (slate != _slate)
                    {
                        _slate = slate;
                        ReleaseAll();
                        Log.Info(slate ? "Tablet posture: keyboard and mouse disabled, touch only" : "Laptop posture: keyboard and mouse enabled");
                    }
                    break;
                case WM_TIMER when (uint)wParam == CornerTimerId:
                    KillTimer(hwnd, CornerTimerId);
                    if (_corner != null) { _corner = null; _cornerFired = true; RequestExit(); }
                    return IntPtr.Zero;
                case WM_DISPLAYCHANGE:
                    FitToMonitor();
                    return IntPtr.Zero;
                case WM_SIZE:
                    int w = LoWord(lParam), h = HiWord(lParam);
                    if ((int)wParam != SIZE_MINIMIZED && w > 0 && h > 0 && (w != _width || h != _height))
                    {
                        _width = w; _height = h;
                        Resized?.Invoke(w, h);
                    }
                    return IntPtr.Zero;
                case WM_NCHITTEST:
                    return (IntPtr)HTCLIENT;
                case WM_ERASEBKGND:
                    return (IntPtr)1;
                case WM_CLOSE:
                    if (!_closing) { _closing = true; ReleaseAll(); }
                    RemoveHook();
                    DestroyWindow(hwnd);
                    return IntPtr.Zero;
                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex) { Log.Warn("Viewer input error: " + ex.Message); }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void RequestExit()
    {
        if (_closing) return;
        ReleaseAll();
        ExitRequested?.Invoke();
    }

    private void FitToMonitor()
    {
        var mon = MonitorFromWindow(_hwnd, 2);
        var r = MonitorRect(mon);
        SetWindowPos(_hwnd, HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOACTIVATE);
    }

    private void OnActivate(bool active, bool minimized)
    {
        if (active && !minimized)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            InstallHook();
            SetTimer(_hwnd, TopmostTimerId, 2000, IntPtr.Zero);
            return;
        }
        if (active) return;
        ReleaseAll();
        bool userLeaving = !InputLocked && Environment.TickCount64 - _lastSwitchKeyMs < 2500
                           && Environment.TickCount64 - _shownTicks > 600;
        if (!userLeaving || _closing)
        {
            // Something else took the focus (a notification, another app, rotating the screen…): the monitor keeps
            // priority — stay on top and take the focus back.
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (!_closing) SetTimer(_hwnd, RefocusTimerId, 400, IntPtr.Zero);
            return;
        }
        // Windows key, Alt+Tab, Ctrl+Esc, Task Manager, Ctrl+Alt+Del: let the user reach the taskbar and the desktop.
        KillTimer(_hwnd, TopmostTimerId);
        SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        ShowWindow(_hwnd, SW_MINIMIZE);
    }

    /// <summary>Tent/stand/tablet posture of a convertible (needs an integrated touch screen to count).</summary>
    private static bool IsTabletPosture()
    {
        const int SM_CONVERTIBLESLATEMODE = 0x2003, SM_DIGITIZER = 94, NID_INTEGRATED_TOUCH = 0x01, NID_READY = 0x80;
        int digitizer = GetSystemMetrics(SM_DIGITIZER);
        if ((digitizer & NID_INTEGRATED_TOUCH) == 0 || (digitizer & NID_READY) == 0) return false;
        return GetSystemMetrics(SM_CONVERTIBLESLATEMODE) == 0;
    }

    /// <summary>Brings the window to the front even when DualScreen was started in the background (cast).</summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, IntPtr.Zero) : 0;
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
        finally { if (attached) AttachThreadInput(me, fgThread, false); }
    }

    // ------------------------------------------------------------------ coordinates

    private (ushort, ushort) Norm(IntPtr lParam) => NormClient((short)LoWord(lParam), (short)HiWord(lParam));

    private (ushort, ushort) NormScreen(POINT p)
    {
        ScreenToClient(_hwnd, ref p);
        return NormClient(p.X, p.Y);
    }

    private (ushort, ushort) NormClient(int x, int y)
    {
        var (l, t, r, b) = ContentRect?.Invoke() ?? (0, 0, _width, _height);
        if (r <= l || b <= t) (l, t, r, b) = (0, 0, Math.Max(_width, 1), Math.Max(_height, 1));
        double nx = Math.Clamp((x - l) / (double)(r - l), 0, 1), ny = Math.Clamp((y - t) / (double)(b - t), 0, 1);
        return ((ushort)Math.Round(nx * 65535), (ushort)Math.Round(ny * 65535));
    }

    private bool InCorner(POINT screen)
    {
        ScreenToClient(_hwnd, ref screen);
        int zone = Math.Max(48, Math.Min(_width, _height) / 25);
        return screen.X < zone && screen.Y < zone;
    }

    // ------------------------------------------------------------------ pointer (touch / pen)

    private bool OnPointer(uint msg, IntPtr wParam)
    {
        uint id = (uint)LoWord(wParam);
        if (!GetPointerType(id, out uint type)) return false;
        if (type == PT_TOUCH)
        {
            if (!GetPointerTouchInfo(id, out var ti)) return false;
            var pi = ti.pointerInfo;
            uint f = pi.pointerFlags;
            var (x, y) = NormScreen(pi.ptPixelLocation);
            ushort pressure = (ti.touchMask & TOUCH_MASK_PRESSURE) != 0 ? (ushort)Math.Min(ti.pressure, 1024u) : (ushort)512;
            ContactState state;
            if ((f & POINTER_FLAG_CANCELED) != 0 || msg == WM_POINTERCAPTURECHANGED) state = ContactState.Cancel;
            else if ((f & POINTER_FLAG_DOWN) != 0) state = ContactState.Down;
            else if ((f & POINTER_FLAG_UP) != 0) state = ContactState.Up;
            else if ((f & POINTER_FLAG_INCONTACT) != 0) state = ContactState.Move;
            else return true;

            if (state == ContactState.Down)
            {
                byte cid = 0;
                while (_touchIds.ContainsValue(cid) && cid < 255) cid++;
                _touchIds[id] = cid;
                if (InputLocked && _corner == null && !_cornerFired && InCorner(pi.ptPixelLocation))
                {
                    _corner = new CornerHold { IsTouch = true, PointerId = id, ContactId = cid, X = x, Y = y };
                    SetTimer(_hwnd, CornerTimerId, CornerHoldMs, IntPtr.Zero);
                    return true;
                }
            }
            if (!_touchIds.TryGetValue(id, out byte contact)) return true;

            if (_corner is { IsTouch: true } c && c.PointerId == id)
            {
                if (state == ContactState.Move && InCorner(pi.ptPixelLocation)) return true;
                FlushCorner(); // left the corner or lifted early: deliver what was held back
            }
            if (state is ContactState.Up or ContactState.Cancel)
            {
                _touchIds.Remove(id);
                if (_cornerFired && _touchIds.Count == 0) _cornerFired = false;
            }
            Send?.Invoke(InputCodec.Touch(contact, state, x, y, pressure));
            return true;
        }
        if (type == PT_PEN)
        {
            if (!GetPointerPenInfo(id, out var pen)) return false;
            uint f = pen.pointerInfo.pointerFlags;
            var (x, y) = NormScreen(pen.pointerInfo.ptPixelLocation);
            PenState state = (f & POINTER_FLAG_DOWN) != 0 ? PenState.Down
                : (f & POINTER_FLAG_UP) != 0 ? PenState.Up
                : (f & POINTER_FLAG_INCONTACT) != 0 ? PenState.Move
                : (f & POINTER_FLAG_INRANGE) != 0 ? PenState.Hover : PenState.Leave;
            var buttons = PenButtons.None;
            if ((pen.penFlags & PEN_FLAG_BARREL) != 0) buttons |= PenButtons.Barrel;
            if ((pen.penFlags & PEN_FLAG_INVERTED) != 0) buttons |= PenButtons.Inverted;
            if ((pen.penFlags & PEN_FLAG_ERASER) != 0) buttons |= PenButtons.Eraser;
            ushort pressure = (pen.penMask & PEN_MASK_PRESSURE) != 0 ? (ushort)Math.Min(pen.pressure, 1024u) : (ushort)512;
            if (InputLocked && state == PenState.Down && _corner == null && InCorner(pen.pointerInfo.ptPixelLocation))
            {
                _corner = new CornerHold { IsTouch = false, PointerId = id, X = x, Y = y };
                SetTimer(_hwnd, CornerTimerId, CornerHoldMs, IntPtr.Zero);
                return true;
            }
            if (_corner is { IsTouch: false } c && c.PointerId == id)
            {
                if (state == PenState.Move && InCorner(pen.pointerInfo.ptPixelLocation)) return true;
                if (_cornerFired) return true;
                FlushCorner();
            }
            if (_cornerFired) { if (state is PenState.Up or PenState.Leave) _cornerFired = false; return true; }
            Send?.Invoke(InputCodec.Pen(state, x, y, pressure, buttons));
            return true;
        }
        return false; // mouse / touchpad: handled by the WM_MOUSE* path
    }

    private bool OnPointerLeave(IntPtr wParam)
    {
        uint id = (uint)LoWord(wParam);
        if (!GetPointerType(id, out uint type) || type != PT_PEN) return type == PT_TOUCH;
        Send?.Invoke(InputCodec.Pen(PenState.Leave, 0, 0, 0, PenButtons.None));
        return true;
    }

    /// <summary>A press that started in the exit corner and did not last 3 s is delivered late, as it happened.</summary>
    private void FlushCorner()
    {
        var c = _corner;
        if (c == null) return;
        _corner = null;
        KillTimer(_hwnd, CornerTimerId);
        if (c.IsTouch) Send?.Invoke(InputCodec.Touch(c.ContactId, ContactState.Down, c.X, c.Y, 512));
        else if (c.PointerId != 0) Send?.Invoke(InputCodec.Pen(PenState.Down, c.X, c.Y, 512, PenButtons.None));
        else Send?.Invoke(InputCodec.MouseButton(MouseButtonId.Left, true, c.X, c.Y));
    }

    // ------------------------------------------------------------------ mouse

    /// <summary>Mouse messages synthesized from touch/pen carry this signature: ignore them (already sent as pointer input).</summary>
    private static bool FromTouch() => ((long)GetMessageExtraInfo() & 0xFFFFFF00) == 0xFF515700;

    private IntPtr MouseButton(MouseButtonId b, bool down, IntPtr lParam)
    {
        if (FromTouch() || _slate) return IntPtr.Zero;
        var (x, y) = Norm(lParam);
        int bit = 1 << (int)b;
        if (down)
        {
            if (_mouseButtons == 0) SetCapture(_hwnd);
            _mouseButtons |= bit;
            if (b == MouseButtonId.Left && _noKeyboard && !_slate && _corner == null && !_cornerFired)
            {
                var p = new POINT { X = (short)LoWord(lParam), Y = (short)HiWord(lParam) };
                ClientToScreen(_hwnd, ref p);
                if (InCorner(p))
                {
                    _corner = new CornerHold { IsTouch = false, PointerId = 0, X = x, Y = y };
                    SetTimer(_hwnd, CornerTimerId, CornerHoldMs, IntPtr.Zero);
                    return IntPtr.Zero;
                }
            }
        }
        else
        {
            _mouseButtons &= ~bit;
            if (_mouseButtons == 0) ReleaseCapture();
            if (b == MouseButtonId.Left && _cornerFired) { _cornerFired = false; return IntPtr.Zero; }
            if (b == MouseButtonId.Left && _corner is { IsTouch: false, PointerId: 0 }) FlushCorner();
        }
        Send?.Invoke(InputCodec.MouseButton(b, down, x, y));
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------ keyboard

    private void OnKey(int vk, IntPtr lParam, bool up, bool sys)
    {
        if (InputLocked) return; // the hook already swallows everything; belt and braces
        long lp = (long)lParam;
        ushort scan = (ushort)((lp >> 16) & 0xFF);
        bool ext = ((lp >> 24) & 1) != 0;
        if (!up)
        {
            if (vk == VK_ESCAPE) { RequestExit(); return; }
            if (vk == VK_F4 && sys) { RequestExit(); return; } // Alt+F4
        }
        if (vk is VK_LWIN or VK_RWIN or VK_ESCAPE) return; // Windows handles these locally
        if (scan == 0) return;
        if (up)
        {
            if (!_keysDown.Remove((scan, ext))) return;
        }
        else _keysDown.Add((scan, ext));
        Send?.Invoke(InputCodec.Key(scan, up, ext));
    }

    private void ReleaseAll()
    {
        _keysDown.Clear();
        _touchIds.Clear();
        _mouseButtons = 0;
        _corner = null;
        if (_hwnd != IntPtr.Zero) KillTimer(_hwnd, CornerTimerId);
        Send?.Invoke(InputCodec.ReleaseAll());
    }

    private void InstallHook()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Warn($"Keyboard hook failed ({Marshal.GetLastWin32Error()})");
    }

    private void RemoveHook()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>"Do not disturb": every key (including Windows, Alt+Tab, Ctrl+Esc, Ctrl+Shift+Esc) is discarded. Ctrl+Alt+Del cannot be blocked by design.</summary>
    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(_hook, code, wParam, lParam);
        if (InputLocked && !IsIconic(_hwnd)) return (IntPtr)1; // "No molestar" / tablet posture: every key discarded
        int vk = Marshal.ReadInt32(lParam);
        bool down = (int)wParam is 0x0100 or 0x0104; // WM_KEYDOWN / WM_SYSKEYDOWN
        // Keys that mean "let me reach the taskbar": Windows, Alt(+Tab), Ctrl(+Esc / +Shift+Esc / +Alt+Del).
        if (down && vk is VK_LWIN or VK_RWIN or 0x12 or 0xA4 or 0xA5 or 0x11 or 0xA2 or 0xA3 or VK_ESCAPE or 0x09)
            _lastSwitchKeyMs = Environment.TickCount64;
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    // ------------------------------------------------------------------ Win32

    private static int LoWord(IntPtr v) => (int)((long)v & 0xFFFF);
    private static int HiWord(IntPtr v) => (int)(((long)v >> 16) & 0xFFFF);

    private const uint WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_ACTIVATE = 0x0006, WM_CLOSE = 0x0010, WM_ERASEBKGND = 0x0014,
        WM_SETCURSOR = 0x0020, WM_NCHITTEST = 0x0084, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102,
        WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_SYSCHAR = 0x0106, WM_SYSCOMMAND = 0x0112, WM_TIMER = 0x0113,
        WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203,
        WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_RBUTTONDBLCLK = 0x0206, WM_MBUTTONDOWN = 0x0207,
        WM_MBUTTONUP = 0x0208, WM_MBUTTONDBLCLK = 0x0209, WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDOWN = 0x020B,
        WM_XBUTTONUP = 0x020C, WM_MOUSEHWHEEL = 0x020E, WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246,
        WM_POINTERUP = 0x0247, WM_POINTERLEAVE = 0x024A, WM_POINTERCAPTURECHANGED = 0x024C, WM_DISPLAYCHANGE = 0x007E,
        WM_SETTINGCHANGE = 0x001A;
    private const int SC_KEYMENU = 0xF100, SC_SCREENSAVE = 0xF140, SC_MONITORPOWER = 0xF170;
    private const int HTCLIENT = 1, SIZE_MINIMIZED = 1, SW_SHOW = 5, SW_MINIMIZE = 6, BLACK_BRUSH = 4;
    private const int VK_ESCAPE = 0x1B, VK_F4 = 0x73, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int WH_KEYBOARD_LL = 13;
    private const uint WS_POPUP = 0x80000000, WS_EX_TOPMOST = 0x8, WS_EX_APPWINDOW = 0x40000;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }


    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public short boolVal; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PROPERTYKEY key);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        void Commit();
    }

    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IPropertyStore store);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr GetMessageExtraInfo();
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, uint id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, uint id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(IntPtr hwnd, string name, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr inst, IntPtr name);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
}
