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
/// The cursor is not part of the image (receivers that draw it locally are unaffected).
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
                    if (info.LastPresentTime != 0 && res != null)
                    {
                        using var tex = res.QueryInterface<ID3D11Texture2D>();
                        lock (_texLock)
                        {
                            if (Latest != null && tex.Description.Width == Latest.Description.Width && tex.Description.Height == Latest.Description.Height)
                                _d3d.Context.CopyResource(Latest, tex);
                        }
                        long qpcUs = (long)(info.LastPresentTime * (1_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
                        Interlocked.Exchange(ref _timeUs, qpcUs);
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

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(1000);
        _dup?.Dispose();
        lock (_texLock) { Latest?.Dispose(); Latest = null; }
        FrameEvent.Dispose();
    }
}
