using System;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice;
using Vortice.Mathematics;

namespace DualScreen.Media;

/// <summary>
/// GPU colour conversion + scaling (BGRA desktop → NV12 BT.709 limited range) with the D3D11 video processor,
/// which runs on the GPU's fixed-function video engine. Output textures are a small ring so the asynchronous
/// encoder can still be reading frame N while frame N+1 is produced.
/// </summary>
public sealed class FrameConverter : IDisposable
{
    public const int RingSize = 6;
    private readonly D3DDevice _d3d;
    private readonly ID3D11VideoProcessorEnumerator _enum;
    private readonly ID3D11VideoProcessor _proc;
    private readonly ID3D11Texture2D[] _ring = new ID3D11Texture2D[RingSize];
    private readonly ID3D11VideoProcessorOutputView[] _outViews = new ID3D11VideoProcessorOutputView[RingSize];
    private ID3D11Texture2D? _inputTex;
    private ID3D11VideoProcessorInputView? _inView;
    private int _next;

    public int OutWidth { get; }
    public int OutHeight { get; }

    public FrameConverter(D3DDevice d3d, int inW, int inH, int outW, int outH, int fps)
    {
        _d3d = d3d;
        OutWidth = outW;
        OutHeight = outH;
        var desc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)fps, 1),
            InputWidth = (uint)inW,
            InputHeight = (uint)inH,
            OutputFrameRate = new Rational((uint)fps, 1),
            OutputWidth = (uint)outW,
            OutputHeight = (uint)outH,
            Usage = VideoUsage.PlaybackNormal,
        };
        _enum = d3d.VideoDevice.CreateVideoProcessorEnumerator(desc);
        _proc = d3d.VideoDevice.CreateVideoProcessor(_enum, 0);

        for (int i = 0; i < RingSize; i++)
        {
            _ring[i] = d3d.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)outW,
                Height = (uint)outH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
            });
            _outViews[i] = d3d.VideoDevice.CreateVideoProcessorOutputView(_ring[i], _enum, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            });
        }

        // Source: full-range RGB. Destination: BT.709, 16-235 (what every Android decoder expects by default).
        var inCs = new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, Nominal_Range = 2 };
        var outCs = new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, Nominal_Range = 1 };
        d3d.VideoContext.VideoProcessorSetStreamColorSpace(_proc, 0, inCs);
        d3d.VideoContext.VideoProcessorSetOutputColorSpace(_proc, outCs);
        d3d.VideoContext.VideoProcessorSetStreamFrameFormat(_proc, 0, VideoFrameFormat.Progressive);
        d3d.VideoContext.VideoProcessorSetOutputBackgroundColor(_proc, true, new VideoColor { YCbCr = new VideoColorYCbCrA { Y = 16f / 255f, Cb = 0.5f, Cr = 0.5f, A = 1f } });

        // Letterbox when aspect ratios differ (mirror mode of a 16:9 desktop on a 20:9 phone, etc.).
        double scale = Math.Min(outW / (double)inW, outH / (double)inH);
        int dw = (int)Math.Round(inW * scale) & ~1, dh = (int)Math.Round(inH * scale) & ~1;
        int dx = ((outW - dw) / 2) & ~1, dy = ((outH - dh) / 2) & ~1;
        d3d.VideoContext.VideoProcessorSetStreamSourceRect(_proc, 0, true, new RawRect(0, 0, inW, inH));
        d3d.VideoContext.VideoProcessorSetStreamDestRect(_proc, 0, true, new RawRect(dx, dy, dx + dw, dy + dh));
        d3d.VideoContext.VideoProcessorSetOutputTargetRect(_proc, true, new RawRect(0, 0, outW, outH));
    }

    /// <summary>Converts <paramref name="source"/> into the next ring texture and returns it.</summary>
    public ID3D11Texture2D Convert(ID3D11Texture2D source)
    {
        if (!ReferenceEquals(source, _inputTex))
        {
            _inView?.Dispose();
            _inView = _d3d.VideoDevice.CreateVideoProcessorInputView(source, _enum, new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });
            _inputTex = source;
        }
        int idx = _next;
        _next = (_next + 1) % RingSize;
        var stream = new VideoProcessorStream { Enable = true, InputSurface = _inView };
        _d3d.VideoContext.VideoProcessorBlt(_proc, _outViews[idx], 0, 1, new[] { stream }).CheckError();
        return _ring[idx];
    }

    public void Dispose()
    {
        _inView?.Dispose();
        foreach (var v in _outViews) v?.Dispose();
        foreach (var t in _ring) t?.Dispose();
        _proc.Dispose();
        _enum.Dispose();
    }
}
