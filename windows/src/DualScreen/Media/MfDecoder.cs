using System;
using System.Collections.Generic;
using System.Linq;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using DualScreen.Core;

namespace DualScreen.Media;

/// <summary>
/// Media Foundation video decoder for the receiver. The Microsoft H.264/HEVC decoder MFTs use DXVA (GPU) when
/// given a D3D11 device manager and output NV12 textures on that device, so decoding and display never touch the
/// CPU. Low-latency mode makes the MFT return each frame as soon as its access unit is fed (no reorder delay;
/// the host never sends B-frames).
/// </summary>
public sealed unsafe class MfDecoder : IDisposable
{
    private static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    private const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);

    private readonly D3DDevice _d3d;
    private readonly IMFTransform _mft;
    private readonly IMFDXGIDeviceManager? _manager;
    private readonly CodecApi? _codecApi;
    private bool _providesSamples;
    private int _outputSize;
    private int _width, _height, _alignedHeight;
    private ID3D11Texture2D? _upload; // software path only

    public string Name { get; }
    public bool IsHardware { get; }

    /// <summary>Codecs with a decoder on this PC (H.264 always ships with Windows; HEVC needs the HEVC extension).</summary>
    public static List<string> AvailableMimes()
    {
        MfEncoder.Startup();
        var list = new List<string>();
        foreach (var mime in new[] { "video/avc", "video/hevc" })
        {
            try
            {
                using var col = Enumerate(MfEncoder.SubtypeFor(mime));
                if (col.Any()) list.Add(mime);
            }
            catch (Exception ex) { Log.Warn($"Decoder enumeration for {mime} failed: {ex.Message}"); }
        }
        if (list.Count == 0) list.Add("video/avc");
        return list;
    }

    private static IMFActivateCollection Enumerate(Guid subtype) =>
        MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER, MfGuids.MFT_ENUM_FLAG_SYNCMFT | MfGuids.MFT_ENUM_FLAG_SORTANDFILTER,
            new RegisterTypeInfo { GuidMajorType = MfGuids.MFMediaType_Video, GuidSubtype = subtype },
            new RegisterTypeInfo { GuidMajorType = MfGuids.MFMediaType_Video, GuidSubtype = MfGuids.MFVideoFormat_NV12 });

    public MfDecoder(D3DDevice d3d, string mime, int width, int height, int fps)
    {
        MfEncoder.Startup();
        _d3d = d3d;
        _width = width;
        _height = height;
        _alignedHeight = height;
        Guid subtype = MfEncoder.SubtypeFor(mime);

        IMFTransform? mft = null;
        string name = "Decoder";
        using (var col = Enumerate(subtype))
        {
            foreach (var act in col)
            {
                try { name = act.GetString(MfGuids.MFT_FRIENDLY_NAME_Attribute); } catch { }
                try { mft = act.ActivateObject<IMFTransform>(); break; }
                catch (Exception ex) { Log.Warn($"Cannot activate {name}: {ex.Message}"); }
            }
        }
        _mft = mft ?? throw new NotSupportedException($"No hay decodificador para {mime} en este equipo");
        Name = name;

        var attrs = _mft.Attributes;
        attrs.Set(MfGuids.MF_LOW_LATENCY, 1u);
        bool d3dAware = attrs.GetUInt32(MfGuids.MF_SA_D3D11_AWARE, out uint aware).Success && aware != 0;
        if (d3dAware)
        {
            try
            {
                _manager = MediaFactory.MFCreateDXGIDeviceManager();
                _manager.ResetDevice(d3d.Device).CheckError();
                _mft.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)_manager.NativePointer);
                IsHardware = true;
            }
            catch (Exception ex)
            {
                Log.Warn("DXVA decoding unavailable, using software: " + ex.Message);
                _manager?.Dispose();
                _manager = null;
            }
        }
        _codecApi = CodecApi.From(_mft.NativePointer);
        _codecApi?.SetBool(MfGuids.CODECAPI_AVLowLatencyMode, true);

        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MfGuids.MF_MT_MAJOR_TYPE, MfGuids.MFMediaType_Video);
            inType.Set(MfGuids.MF_MT_SUBTYPE, subtype);
            inType.Set(MfGuids.MF_MT_FRAME_SIZE, MediaFactory.PackSize((uint)width, (uint)height));
            inType.Set(MfGuids.MF_MT_FRAME_RATE, MediaFactory.PackRatio(Math.Max(fps, 1), 1));
            inType.Set(MfGuids.MF_MT_INTERLACE_MODE, (uint)MfGuids.MFVideoInterlace_Progressive);
            _mft.SetInputType(0, inType, 0);
        }
        SetNv12Output();

        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        Log.Info($"Decoder: {Name} ({(IsHardware ? "DXVA" : "software")}) {mime} {width}x{height}@{fps}");
    }

    private void SetNv12Output()
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType t;
            try { t = _mft.GetOutputAvailableType(0, i); }
            catch { throw new NotSupportedException("El decodificador no ofrece salida NV12"); }
            using (t)
            {
                if (t.GetGUID(MfGuids.MF_MT_SUBTYPE, out Guid st).Success && st == MfGuids.MFVideoFormat_NV12)
                {
                    _mft.SetOutputType(0, t, 0);
                    if (t.GetUInt64(MfGuids.MF_MT_FRAME_SIZE, out ulong packed).Success)
                    {
                        int fw = (int)(packed >> 32), fh = (int)(packed & 0xFFFFFFFF);
                        if (fw > 0 && fh > 0) { _alignedHeight = fh; }
                    }
                    break;
                }
            }
        }
        var osi = _mft.GetOutputStreamInfo(0);
        _providesSamples = (osi.Flags & (MfGuids.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MfGuids.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        _outputSize = Math.Max(osi.Size, _width * _alignedHeight * 3 / 2);
    }

    /// <summary>
    /// Feeds one access unit (Annex-B / OBU) and calls <paramref name="onFrame"/> for every decoded picture with the
    /// texture and its array slice. The texture is only valid during the callback.
    /// </summary>
    public void Decode(ReadOnlySpan<byte> au, long ptsUs, Action<ID3D11Texture2D, int> onFrame)
    {
        using var sample = MediaFactory.MFCreateSample();
        using (var buf = MediaFactory.MFCreateMemoryBuffer(au.Length))
        {
            buf.Lock(out IntPtr p, out _, out _);
            au.CopyTo(new Span<byte>((void*)p, au.Length));
            buf.Unlock();
            buf.CurrentLength = au.Length;
            sample.AddBuffer(buf);
        }
        sample.SampleTime = ptsUs * 10;
        sample.SampleDuration = 0;
        try { _mft.ProcessInput(0, sample, 0); }
        catch (SharpGenException ex) when (ex.ResultCode.Code == MF_E_NOTACCEPTING)
        {
            Drain(onFrame);
            _mft.ProcessInput(0, sample, 0);
        }
        Drain(onFrame);
    }

    private void Drain(Action<ID3D11Texture2D, int> onFrame)
    {
        while (true)
        {
            IMFSample? own = null;
            if (!_providesSamples)
            {
                own = MediaFactory.MFCreateSample();
                using var b = MediaFactory.MFCreateMemoryBuffer(_outputSize);
                own.AddBuffer(b);
            }
            var odb = new OutputDataBuffer { StreamID = 0, Sample = own! };
            Result r = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref odb, out _);
            if (r.Code == MfGuids.MF_E_TRANSFORM_NEED_MORE_INPUT) { own?.Dispose(); return; }
            if (r.Code == MfGuids.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                own?.Dispose();
                SetNv12Output();
                continue;
            }
            r.CheckError();
            odb.Events?.Dispose();
            using var sample = odb.Sample;
            if (sample == null) return;
            using var buffer = sample.GetBufferByIndex(0);
            if (IsHardware && _providesSamples)
            {
                using var dxgi = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
                if (dxgi != null)
                {
                    IntPtr res = dxgi.GetResource(typeof(ID3D11Texture2D).GUID);
                    using var tex = new ID3D11Texture2D(res);
                    onFrame(tex, (int)dxgi.SubresourceIndex);
                    continue;
                }
            }
            onFrame(Upload(buffer), 0);
        }
    }

    /// <summary>Software path: copies the NV12 picture into a GPU texture.</summary>
    private ID3D11Texture2D Upload(IMFMediaBuffer buffer)
    {
        _upload ??= _d3d.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width, Height = (uint)_height, MipLevels = 1, ArraySize = 1,
            Format = Vortice.DXGI.Format.NV12, SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic, BindFlags = BindFlags.ShaderResource, CPUAccessFlags = CpuAccessFlags.Write,
        });
        buffer.Lock(out IntPtr src, out _, out int len);
        try
        {
            int pitch = _width;
            int planeRows = _alignedHeight;
            if (len >= 0 && _alignedHeight > 0) pitch = Math.Max(_width, len * 2 / 3 / _alignedHeight);
            var map = _d3d.Context.Map(_upload, 0, MapMode.WriteDiscard, MapFlags.None);
            try
            {
                byte* s = (byte*)src, d = (byte*)map.DataPointer;
                int dp = (int)map.RowPitch;
                for (int y = 0; y < _height; y++) Buffer.MemoryCopy(s + y * pitch, d + y * dp, dp, _width);
                byte* sUv = s + pitch * planeRows, dUv = d + dp * _height;
                for (int y = 0; y < _height / 2; y++) Buffer.MemoryCopy(sUv + y * pitch, dUv + y * dp, dp, _width);
            }
            finally { _d3d.Context.Unmap(_upload, 0); }
        }
        finally { buffer.Unlock(); }
        return _upload;
    }

    public void Dispose()
    {
        try
        {
            _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
            if (_manager != null) _mft.ProcessMessage(TMessageType.MessageSetD3DManager, UIntPtr.Zero);
        }
        catch { }
        _codecApi?.Dispose();
        try { _mft.QueryInterface<IMFShutdown>()?.Shutdown(); } catch { }
        _mft.Dispose();
        _manager?.Dispose();
        _upload?.Dispose();
    }
}
