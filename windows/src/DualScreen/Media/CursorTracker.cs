using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DualScreen.Display;
using DualScreen.Net;

namespace DualScreen.Media;

/// <summary>
/// Sends the position of the PC's mouse pointer (feature "cursorPos") when it moves inside the streamed monitor,
/// so the phone's touchpad mode moves the pointer from where it really is. The pointer itself is in the video.
/// <code>CURSOR position [2][visible:u8][x:u16][y:u16]   (0..65535 over the video frame)</code>
/// </summary>
public sealed class CursorTracker : IDisposable
{
    private readonly ClientSession _session;
    private readonly IntPtr _monitor;
    private readonly int _videoW, _videoH;
    private (bool visible, ushort x, ushort y) _lastPos = (false, 0, 0);
    private long _lastPollUs;

    public CursorTracker(ClientSession session, IntPtr monitor, int videoW, int videoH)
    {
        _session = session;
        _monitor = monitor;
        _videoW = videoW;
        _videoH = videoH;
    }

    /// <summary>Called from the pipeline thread; cheap (one GetCursorInfo) and rate-limited to ~250 Hz.</summary>
    public void Poll()
    {
        long now = HostClock.NowUs();
        if (now - _lastPollUs < 4000) return;
        _lastPollUs = now;
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci)) return;
        bool showing = (ci.flags & 1) != 0 && ci.hCursor != IntPtr.Zero;

        var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (!Native.GetMonitorInfo(_monitor, ref info)) return;
        var r = info.rcMonitor;
        int mw = Math.Max(1, r.Right - r.Left), mh = Math.Max(1, r.Bottom - r.Top);
        bool inside = showing && ci.ptScreenPos.X >= r.Left && ci.ptScreenPos.X < r.Right && ci.ptScreenPos.Y >= r.Top && ci.ptScreenPos.Y < r.Bottom;
        ushort nx = 0, ny = 0;
        if (inside)
        {
            // Inverse of InputInjector.Map: desktop pixel -> video coordinate (letterbox of mirror mode).
            double scale = Math.Min(_videoW / (double)mw, _videoH / (double)mh);
            int dw = (int)Math.Round(mw * scale) & ~1, dh = (int)Math.Round(mh * scale) & ~1;
            int dx = ((_videoW - dw) / 2) & ~1, dy = ((_videoH - dh) / 2) & ~1;
            double vx = dx + (ci.ptScreenPos.X - r.Left) / (double)mw * dw;
            double vy = dy + (ci.ptScreenPos.Y - r.Top) / (double)mh * dh;
            nx = (ushort)Math.Clamp(Math.Round(vx / _videoW * 65535), 0, 65535);
            ny = (ushort)Math.Clamp(Math.Round(vy / _videoH * 65535), 0, 65535);
        }
        var pos = (inside, nx, ny);
        if (pos == _lastPos) return;
        _lastPos = pos;
        Span<byte> p = stackalloc byte[6];
        p[0] = 2; p[1] = (byte)(inside ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(p[2..], nx);
        BinaryPrimitives.WriteUInt16BigEndian(p[4..], ny);
        _session.SendControl(FrameCodec.Encode(MsgType.Cursor, p));
    }

    public void Dispose() { }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
}
