using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DualScreen.Display;
using DualScreen.Net;

namespace DualScreen.Media;

/// <summary>
/// Sends the PC's mouse cursor separately from the video (to receivers with feature "cursor"): its image when it
/// changes and its position when it moves inside the streamed monitor. The receiver draws it locally, so the
/// pointer moves with zero delay instead of waiting for the next encoded frame.
/// <code>
/// CURSOR shape    [1][hotX:u16][hotY:u16][w:u16][h:u16][BGRA w*h*4, straight alpha]
/// CURSOR position [2][visible:u8][x:u16][y:u16]   (0..65535 over the video frame)
/// </code>
/// </summary>
public sealed class CursorTracker : IDisposable
{
    private readonly ClientSession _session;
    private readonly IntPtr _monitor;
    private readonly int _videoW, _videoH;
    private IntPtr _lastHandle = new(-1);
    private (bool visible, ushort x, ushort y) _lastPos = (false, 0, 0);
    private long _lastPollUs;

    private readonly bool _shapes;

    /// <param name="shapes">Also send the cursor image (receivers that draw the cursor themselves).</param>
    public CursorTracker(ClientSession session, IntPtr monitor, int videoW, int videoH, bool shapes = true)
    {
        _shapes = shapes;
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

        if (_shapes && showing && ci.hCursor != _lastHandle)
        {
            _lastHandle = ci.hCursor;
            var shape = Capture(ci.hCursor);
            if (shape != null) _session.SendControl(shape);
        }

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

    /// <summary>Converts an HCURSOR (color or monochrome) to straight-alpha BGRA.</summary>
    private static unsafe byte[]? Capture(IntPtr hCursor)
    {
        if (!GetIconInfo(hCursor, out ICONINFO ii)) return null;
        try
        {
            bool mono = ii.hbmColor == IntPtr.Zero;
            GetObject(ii.hbmMask, Marshal.SizeOf<BITMAP>(), out BITMAP bm);
            int w = bm.bmWidth, h = mono ? bm.bmHeight / 2 : bm.bmHeight;
            if (!mono) { GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out BITMAP bc); w = bc.bmWidth; h = bc.bmHeight; }
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;

            var mask = ReadBits(ii.hbmMask, w, mono ? h * 2 : h);
            byte[] bgra = new byte[w * h * 4];
            if (!mono)
            {
                var color = ReadBits(ii.hbmColor, w, h);
                bool hasAlpha = false;
                for (int i = 3; i < color.Length; i += 4) if (color[i] != 0) { hasAlpha = true; break; }
                for (int i = 0; i < w * h; i++)
                {
                    bgra[i * 4] = color[i * 4]; bgra[i * 4 + 1] = color[i * 4 + 1]; bgra[i * 4 + 2] = color[i * 4 + 2];
                    bgra[i * 4 + 3] = hasAlpha ? color[i * 4 + 3] : (byte)(mask[i * 4] == 0 ? 255 : 0);
                }
            }
            else
            {
                // AND/XOR: 0/0 black, 0/1 white, 1/0 transparent, 1/1 inverted (drawn black with a white halo).
                var invert = new bool[w * h];
                for (int i = 0; i < w * h; i++)
                {
                    bool and = mask[i * 4] != 0, xor = mask[(w * h + i) * 4] != 0;
                    byte v = (!and && xor) ? (byte)255 : (byte)0;
                    bool opaque = !and || xor;
                    if (and && xor) invert[i] = true;
                    bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = v;
                    bgra[i * 4 + 3] = opaque ? (byte)255 : (byte)0;
                }
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (bgra[i * 4 + 3] != 0) continue;
                        bool near = false;
                        for (int dy = -1; dy <= 1 && !near; dy++)
                            for (int dx = -1; dx <= 1 && !near; dx++)
                            {
                                int xx = x + dx, yy = y + dy;
                                if (xx >= 0 && yy >= 0 && xx < w && yy < h && invert[yy * w + xx]) near = true;
                            }
                        if (near) { bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = 255; bgra[i * 4 + 3] = 255; }
                    }
            }

            var payload = new byte[9 + bgra.Length];
            payload[0] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), (ushort)ii.xHotspot);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(3), (ushort)ii.yHotspot);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(5), (ushort)w);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(7), (ushort)h);
            bgra.CopyTo(payload, 9);
            return FrameCodec.Encode(MsgType.Cursor, payload);
        }
        catch { return null; }
        finally
        {
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
    }

    /// <summary>Reads a bitmap as top-down 32-bpp BGRA.</summary>
    private static byte[] ReadBits(IntPtr hbm, int w, int h)
    {
        var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var buf = new byte[w * h * 4];
        IntPtr dc = GetDC(IntPtr.Zero);
        try { GetDIBits(dc, hbm, 0, (uint)h, buf, ref bmi, 0); }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        return buf;
    }

    public void Dispose() { }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public int[]? bmiColors; // room for the palette of 1-bpp sources
    }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr h, out ICONINFO ii);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
