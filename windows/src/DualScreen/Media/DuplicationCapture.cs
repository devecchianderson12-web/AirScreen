using System;
using System.Threading;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DualScreen.Core;

namespace DualScreen.Media;

/// <summary>What the pipeline needs from a monitor capture (Windows.Graphics.Capture or Desktop Duplication).</summary>
public interface ICaptureSource : IDisposable
{
    ID3D11Texture2D? Latest { get; }
    int Width { get; }
    int Height { get; }
    long Counter { get; }
    long TimeUs { get; }
    AutoResetEvent FrameEvent { get; }
    object TextureLock { get; }
    event Action<int, int>? SizeChanged;
    void Start();
}

/// <summary>
/// Fallback capture with the DXGI Desktop Duplication API, used when Windows.Graphics.Capture is unavailable
/// (for example when the Windows "capture service" was disabled by a tweaking tool: error 0x80070422).
/// Desktop Duplication leaves the mouse pointer out of the image, so it is drawn back in here (shape and position
/// come with each frame); every receiver therefore always sees the PC's cursor.
/// </summary>
public sealed class DuplicationCapture : ICaptureSource
{
    private readonly D3DDevice _d3d;
    private readonly IntPtr _monitor;
    private readonly object _texLock = new();
    private readonly Thread _thread;
    private volatile bool _stop;
    private IDXGIOutputDuplication? _dup;
    private long _counter, _timeUs;
    private ID3D11Texture2D? _clean;      // desktop without pointer
    private ID3D11Texture2D? _staging;    // pointer-sized CPU copy for blending
    private byte[]? _pointer;             // straight-alpha BGRA
    private int _pw, _ph, _hotX, _hotY;
    private int _px, _py;
    private bool _pVisible;

    public ID3D11Texture2D? Latest { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long Counter => Interlocked.Read(ref _counter);
    public long TimeUs => Interlocked.Read(ref _timeUs);
    public AutoResetEvent FrameEvent { get; } = new(false);
    public object TextureLock => _texLock;
    public event Action<int, int>? SizeChanged;

    public DuplicationCapture(D3DDevice d3d, IntPtr monitor)
    {
        _d3d = d3d;
        _monitor = monitor;
        Open();
        _thread = new Thread(Run) { IsBackground = true, Name = "DualScreen.Duplication", Priority = ThreadPriority.AboveNormal };
    }

    private void Open()
    {
        using var dxgiDevice = _d3d.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        for (uint i = 0; adapter.EnumOutputs(i, out IDXGIOutput? output).Success && output != null; i++)
        {
            using (output)
            {
                if (output.Description.Monitor != _monitor) continue;
                using var o1 = output.QueryInterface<IDXGIOutput1>();
                _dup?.Dispose();
                _dup = o1.DuplicateOutput(_d3d.Device);
                var d = _dup.Description.ModeDescription;
                bool rotated = _dup.Description.Rotation is ModeRotation.Rotate90 or ModeRotation.Rotate270;
                int w = (int)(rotated ? d.Height : d.Width), h = (int)(rotated ? d.Width : d.Height);
                if (w != Width || h != Height)
                {
                    bool resized = Width != 0;
                    Width = w; Height = h;
                    Allocate();
                    if (resized) SizeChanged?.Invoke(w, h);
                }
                Log.Info($"Desktop Duplication capture {Width}x{Height}");
                return;
            }
        }
        throw new InvalidOperationException("Desktop Duplication: el monitor no pertenece a esta GPU");
    }

    private void Allocate()
    {
        lock (_texLock)
        {
            Latest?.Dispose();
            Latest = _d3d.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            });
            _clean?.Dispose();
            _clean = _d3d.Device.CreateTexture2D(Latest.Description);
        }
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        while (!_stop)
        {
            try
            {
                var dup = _dup;
                if (dup == null) { Thread.Sleep(200); Open(); continue; }
                Result r = dup.AcquireNextFrame(100, out OutduplFrameInfo info, out IDXGIResource? res);
                if (r.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) continue;
                if (r.Code == Vortice.DXGI.ResultCode.AccessLost.Code) { _dup?.Dispose(); _dup = null; continue; }
                r.CheckError();
                try
                {
                    bool image = info.LastPresentTime != 0 && res != null;
                    bool pointer = info.LastMouseUpdateTime != 0;
                    if (pointer)
                    {
                        _pVisible = info.PointerPosition.Visible;
                        _px = info.PointerPosition.Position.X; _py = info.PointerPosition.Position.Y;
                        if (info.PointerShapeBufferSize > 0) ReadPointerShape(dup, info.PointerShapeBufferSize);
                    }
                    if (image || pointer)
                    {
                        lock (_texLock)
                        {
                            if (Latest != null && _clean != null)
                            {
                                if (image)
                                {
                                    using var tex = res!.QueryInterface<ID3D11Texture2D>();
                                    if (tex.Description.Width == _clean.Description.Width && tex.Description.Height == _clean.Description.Height)
                                        _d3d.Context.CopyResource(_clean, tex);
                                }
                                _d3d.Context.CopyResource(Latest, _clean);
                                if (_pVisible) DrawPointer();
                            }
                        }
                        long qpc = image ? info.LastPresentTime : info.LastMouseUpdateTime;
                        Interlocked.Exchange(ref _timeUs, (long)(qpc * (1_000_000.0 / System.Diagnostics.Stopwatch.Frequency)));
                        Interlocked.Increment(ref _counter);
                        FrameEvent.Set();
                    }
                }
                finally
                {
                    res?.Dispose();
                    dup.ReleaseFrame();
                }
            }
            catch (Exception ex) when (!_stop)
            {
                Log.Info("Duplication frame failed: " + ex.Message);
                _dup?.Dispose(); _dup = null;
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>Converts the duplication pointer shape (color, masked color or monochrome) to straight-alpha BGRA.</summary>
    private unsafe void ReadPointerShape(IDXGIOutputDuplication dup, uint size)
    {
        var buf = new byte[size];
        OutduplPointerShapeInfo si;
        fixed (byte* p = buf)
        {
            if (dup.GetFramePointerShape(size, (IntPtr)p, out _, out si).Failure) return;
        }
        int w = (int)si.Width, pitch = (int)si.Pitch;
        bool mono = si.Type == 1;
        int h = mono ? (int)si.Height / 2 : (int)si.Height;
        if (w <= 0 || h <= 0 || w > 256 || h > 256) return;
        var bgra = new byte[w * h * 4];
        if (si.Type == 2) // color, straight alpha
        {
            for (int y = 0; y < h; y++) Buffer.BlockCopy(buf, y * pitch, bgra, y * w * 4, w * 4);
        }
        else if (si.Type == 4) // masked color: alpha 0 = replace, 0xFF = XOR with the screen (drawn opaque unless black)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = y * pitch + x * 4, d = (y * w + x) * 4;
                    bgra[d] = buf[s]; bgra[d + 1] = buf[s + 1]; bgra[d + 2] = buf[s + 2];
                    bool xor = buf[s + 3] == 0xFF;
                    bgra[d + 3] = (byte)(!xor || (buf[s] | buf[s + 1] | buf[s + 2]) != 0 ? 255 : 0);
                }
        }
        else // monochrome AND/XOR: 0/0 black, 0/1 white, 1/0 transparent, 1/1 inverted (black with a white halo)
        {
            var invert = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool and = (buf[y * pitch + x / 8] & (0x80 >> (x % 8))) != 0;
                    bool xor = (buf[(y + h) * pitch + x / 8] & (0x80 >> (x % 8))) != 0;
                    int d = (y * w + x) * 4;
                    byte v = (!and && xor) ? (byte)255 : (byte)0;
                    bgra[d] = bgra[d + 1] = bgra[d + 2] = v;
                    bgra[d + 3] = (!and || xor) ? (byte)255 : (byte)0;
                    if (and && xor) invert[y * w + x] = true;
                }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int d = (y * w + x) * 4;
                    if (bgra[d + 3] != 0) continue;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx >= 0 && yy >= 0 && xx < w && yy < h && invert[yy * w + xx])
                            { bgra[d] = bgra[d + 1] = bgra[d + 2] = 255; bgra[d + 3] = 255; }
                        }
                }
        }
        _pointer = bgra; _pw = w; _ph = h; _hotX = si.HotSpot.X; _hotY = si.HotSpot.Y;
    }

    /// <summary>Alpha-blends the pointer onto <see cref="Latest"/> (a cursor-sized region round-trips through the CPU).</summary>
    private unsafe void DrawPointer()
    {
        var ptr = _pointer;
        if (ptr == null || Latest == null) return;
        // DXGI reports the pointer's top-left corner (the hotspot is already applied).
        int x0 = Math.Max(0, _px), y0 = Math.Max(0, _py);
        int x1 = Math.Min(Width, _px + _pw), y1 = Math.Min(Height, _py + _ph);
        if (x1 <= x0 || y1 <= y0) return;
        int rw = x1 - x0, rh = y1 - y0;
        if (_staging == null || _staging.Description.Width < _pw || _staging.Description.Height < _ph)
        {
            _staging?.Dispose();
            _staging = _d3d.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Math.Max(_pw, 64), Height = (uint)Math.Max(_ph, 64), MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read | CpuAccessFlags.Write,
            });
        }
        var box = new Vortice.Mathematics.Box(x0, y0, 0, x1, y1, 1);
        _d3d.Context.CopySubresourceRegion(_staging, 0, 0, 0, 0, Latest, 0, box);
        var map = _d3d.Context.Map(_staging, 0, MapMode.ReadWrite, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* b = (byte*)map.DataPointer;
            int ox = x0 - _px, oy = y0 - _py;
            for (int y = 0; y < rh; y++)
            {
                byte* row = b + y * map.RowPitch;
                for (int x = 0; x < rw; x++)
                {
                    int s = ((y + oy) * _pw + (x + ox)) * 4;
                    int a = ptr[s + 3];
                    if (a == 0) continue;
                    byte* d = row + x * 4;
                    d[0] = (byte)((ptr[s] * a + d[0] * (255 - a)) / 255);
                    d[1] = (byte)((ptr[s + 1] * a + d[1] * (255 - a)) / 255);
                    d[2] = (byte)((ptr[s + 2] * a + d[2] * (255 - a)) / 255);
                }
            }
        }
        finally { _d3d.Context.Unmap(_staging, 0); }
        _d3d.Context.CopySubresourceRegion(Latest, 0, (uint)x0, (uint)y0, 0, _staging, 0, new Vortice.Mathematics.Box(0, 0, 0, rw, rh, 1));
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(1000);
        _dup?.Dispose();
        lock (_texLock) { Latest?.Dispose(); Latest = null; _clean?.Dispose(); _clean = null; _staging?.Dispose(); _staging = null; }
        FrameEvent.Dispose();
    }
}
