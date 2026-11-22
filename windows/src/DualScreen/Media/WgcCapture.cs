using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using DualScreen.Core;

namespace DualScreen.Media;

/// <summary>
/// Monitor capture with Windows.Graphics.Capture (Windows 10 1903+). Works on IDD virtual monitors, includes the
/// mouse cursor, follows rotation and only produces frames when the screen content changes (zero cost when idle).
/// Every new frame is copied on the GPU into <see cref="Latest"/>; the encoder thread consumes the latest one.
/// </summary>
public sealed class WgcCapture : ICaptureSource
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid ID3D11Texture2DIid = typeof(ID3D11Texture2D).GUID;

    private readonly D3DDevice _d3d;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly object _texLock = new();
    private Windows.Graphics.SizeInt32 _poolSize;

    public ID3D11Texture2D? Latest { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long FrameCounter;
    /// <summary>QPC capture time of <see cref="Latest"/> in µs (same timebase as <see cref="Net.HostClock"/>).</summary>
    public long LatestTimeUs;
    public readonly AutoResetEvent FrameEvent = new(false);
    public event Action<int, int>? SizeChanged;

    public object TextureLock => _texLock;
    public long Counter => Interlocked.Read(ref FrameCounter);
    public long TimeUs => Interlocked.Read(ref LatestTimeUs);
    AutoResetEvent ICaptureSource.FrameEvent => FrameEvent;

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    public WgcCapture(D3DDevice d3d, IntPtr hmonitor, bool captureCursor = true)
    {
        _d3d = d3d;
        using (var dxgi = d3d.Device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr raw));
            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(raw);
            Marshal.Release(raw);
        }

        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemIid;
        IntPtr itemPtr = interop.CreateForMonitor(hmonitor, ref iid);
        _item = GraphicsCaptureItem.FromAbi(itemPtr);
        Marshal.Release(itemPtr);

        _poolSize = _item.Size;
        Width = _poolSize.Width;
        Height = _poolSize.Height;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        // Receivers that draw the cursor themselves (zero-latency pointer) get the picture without it.
        try { _session.IsCursorCaptureEnabled = captureCursor; } catch { }
        try { _session.IsBorderRequired = false; } catch { /* Windows 10: yellow border cannot be removed */ }
        AllocateLatest(Width, Height);
    }

    public void Start() => _session.StartCapture();

    private void AllocateLatest(int w, int h)
    {
        lock (_texLock)
        {
            Latest?.Dispose();
            Latest = _d3d.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            });
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;
            var size = frame.ContentSize;
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                _poolSize = size;
                sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                Width = size.Width;
                Height = size.Height;
                AllocateLatest(Width, Height);
                SizeChanged?.Invoke(Width, Height);
                return;
            }

            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            Guid texIid = ID3D11Texture2DIid;
            IntPtr texPtr = access.GetInterface(ref texIid);
            using var src = new ID3D11Texture2D(texPtr);
            lock (_texLock)
            {
                if (Latest == null) return;
                var d = src.Description;
                if (d.Width == Latest.Description.Width && d.Height == Latest.Description.Height)
                    _d3d.Context.CopyResource(Latest, src);
                else
                    _d3d.Context.CopySubresourceRegion(Latest, 0, 0, 0, 0, src, 0,
                        new Vortice.Mathematics.Box(0, 0, 0, Math.Min((int)d.Width, Width), Math.Min((int)d.Height, Height), 1));
            }
            Interlocked.Exchange(ref LatestTimeUs, (long)(frame.SystemRelativeTime.Ticks / 10));
            Interlocked.Increment(ref FrameCounter);
            FrameEvent.Set();
        }
        catch (Exception ex)
        {
            Log.Warn("Capture frame failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        try { _pool.FrameArrived -= OnFrameArrived; } catch { }
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
        lock (_texLock)
        {
            Latest?.Dispose();
            Latest = null;
        }
        FrameEvent.Dispose();
        (_winrtDevice as IDisposable)?.Dispose();
    }
}
