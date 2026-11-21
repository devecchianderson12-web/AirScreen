using System;
using System.Collections.Generic;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using DualScreen.Core;
using DualScreen.Media;

namespace DualScreen.Receiver;

/// <summary>
/// Presents decoded NV12 frames on the viewer window with the lowest possible delay: flip-model swap chain,
/// maximum frame latency 1, Present(0) with tearing allowed (a borderless full-screen window gets independent
/// flip, so a frame reaches the panel without waiting for the compositor). NV12 → RGB and scaling run on the
/// GPU's video engine (ID3D11VideoProcessor), like the encoder side.
/// </summary>
public sealed class VideoRenderer : IDisposable
{
    private readonly D3DDevice _d3d;
    private readonly IDXGISwapChain1 _swap;
    private readonly bool _tearing;
    private readonly Dictionary<(IntPtr, int), ID3D11VideoProcessorInputView> _inViews = new();
    private ID3D11VideoProcessorEnumerator? _enum;
    private ID3D11VideoProcessor? _proc;
    private ID3D11VideoProcessorOutputView? _outView;
    private IntPtr _outViewFor;
    private int _w, _h, _vw, _vh;
    private readonly object _gate = new();

    /// <summary>Where the picture lands inside the window (client pixels), for input mapping.</summary>
    public RawRect ContentRect { get; private set; }

    public VideoRenderer(D3DDevice d3d, IntPtr hwnd, int width, int height)
    {
        _d3d = d3d;
        _w = Math.Max(width, 1);
        _h = Math.Max(height, 1);
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        using (var f5 = factory.QueryInterfaceOrNull<IDXGIFactory5>())
            _tearing = f5?.PresentAllowTearing == true;
        var desc = new SwapChainDescription1
        {
            Width = (uint)_w,
            Height = (uint)_h,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard,
            Scaling = Scaling.Stretch,
            AlphaMode = AlphaMode.Ignore,
            Flags = _tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None,
        };
        _swap = factory.CreateSwapChainForHwnd(d3d.Device, hwnd, desc);
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll); // no Alt+Enter mode switch
        using (var dev = d3d.Device.QueryInterfaceOrNull<IDXGIDevice1>())
            dev?.SetMaximumFrameLatency(1);
        Clear();
        Log.Info($"Renderer {_w}x{_h}, tearing {(_tearing ? "on" : "off")}");
    }

    /// <summary>Called on STREAM_CONFIG: (re)builds the video processor for the new stream size.</summary>
    public void SetVideoSize(int vw, int vh)
    {
        lock (_gate)
        {
            _vw = vw; _vh = vh;
            ReleaseProcessor();
            var desc = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational(60, 1),
                InputWidth = (uint)vw,
                InputHeight = (uint)vh,
                OutputFrameRate = new Rational(60, 1),
                OutputWidth = (uint)_w,
                OutputHeight = (uint)_h,
                Usage = VideoUsage.OptimalSpeed,
            };
            _enum = _d3d.VideoDevice.CreateVideoProcessorEnumerator(desc);
            _proc = _d3d.VideoDevice.CreateVideoProcessor(_enum, 0);
            var vc = _d3d.VideoContext;
            // Stream: BT.709 limited range (what the host encodes). Output: full-range RGB.
            vc.VideoProcessorSetStreamColorSpace(_proc, 0, new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, Nominal_Range = 1 });
            vc.VideoProcessorSetOutputColorSpace(_proc, new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, Nominal_Range = 2 });
            vc.VideoProcessorSetStreamFrameFormat(_proc, 0, VideoFrameFormat.Progressive);
            vc.VideoProcessorSetStreamAutoProcessingMode(_proc, 0, false);
            vc.VideoProcessorSetOutputBackgroundColor(_proc, false, new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });

            double scale = Math.Min(_w / (double)vw, _h / (double)vh);
            int dw = (int)Math.Round(vw * scale), dh = (int)Math.Round(vh * scale);
            int dx = (_w - dw) / 2, dy = (_h - dh) / 2;
            ContentRect = new RawRect(dx, dy, dx + dw, dy + dh);
            vc.VideoProcessorSetStreamSourceRect(_proc, 0, true, new RawRect(0, 0, vw, vh));
            vc.VideoProcessorSetStreamDestRect(_proc, 0, true, ContentRect);
            vc.VideoProcessorSetOutputTargetRect(_proc, true, new RawRect(0, 0, _w, _h));
        }
    }

    public void Resize(int width, int height)
    {
        lock (_gate)
        {
            if (width <= 0 || height <= 0 || (width == _w && height == _h)) return;
            _w = width; _h = height;
            _outView?.Dispose(); _outView = null; _outViewFor = IntPtr.Zero;
            _swap.ResizeBuffers(2, (uint)_w, (uint)_h, Format.B8G8R8A8_UNorm, _tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None).CheckError();
            if (_vw > 0) SetVideoSize(_vw, _vh);
            Clear();
        }
    }

    public void Present(ID3D11Texture2D frame, int slice)
    {
        lock (_gate)
        {
            if (_proc == null || _enum == null) return;
            var key = (frame.NativePointer, slice);
            if (!_inViews.TryGetValue(key, out var inView))
            {
                if (_inViews.Count > 48) ClearInputViews(); // decoder reallocated its surfaces
                inView = _d3d.VideoDevice.CreateVideoProcessorInputView(frame, _enum, new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = (uint)slice },
                });
                _inViews[key] = inView;
            }
            using var back = _swap.GetBuffer<ID3D11Texture2D>(0);
            if (_outView == null || _outViewFor != back.NativePointer)
            {
                _outView?.Dispose();
                _outView = _d3d.VideoDevice.CreateVideoProcessorOutputView(back, _enum, new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                });
                _outViewFor = back.NativePointer;
            }
            var stream = new VideoProcessorStream { Enable = true, InputSurface = inView };
            _d3d.VideoContext.VideoProcessorBlt(_proc, _outView, 0, 1, new[] { stream }).CheckError();
            _swap.Present(0, _tearing ? PresentFlags.AllowTearing : PresentFlags.None);
        }
    }

    /// <summary>Paints the window black (before the first frame and after resizes).</summary>
    public void Clear()
    {
        using var back = _swap.GetBuffer<ID3D11Texture2D>(0);
        using var rtv = _d3d.Device.CreateRenderTargetView(back);
        _d3d.Context.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1));
        _swap.Present(0, _tearing ? PresentFlags.AllowTearing : PresentFlags.None);
    }

    private void ClearInputViews()
    {
        foreach (var v in _inViews.Values) v.Dispose();
        _inViews.Clear();
    }

    private void ReleaseProcessor()
    {
        ClearInputViews();
        _outView?.Dispose(); _outView = null; _outViewFor = IntPtr.Zero;
        _proc?.Dispose(); _proc = null;
        _enum?.Dispose(); _enum = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            ReleaseProcessor();
            _swap.Dispose();
        }
    }
}
