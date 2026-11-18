using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using DualScreen.Core;

namespace DualScreen.Media;

public readonly record struct EncodedFrame(byte[] Data, long PtsUs, bool Keyframe);

/// <summary>
/// Media Foundation video encoder. Prefers the GPU vendor's asynchronous hardware MFT (NVENC, Intel Quick Sync,
/// AMD AMF are all exposed this way) fed directly with D3D11 NV12 textures (zero copy), configured for low-latency
/// CBR without B-frames. Falls back to the Microsoft software H.264 MFT if no hardware encoder is usable.
/// </summary>
public sealed unsafe class MfEncoder : IDisposable
{
    private static bool _mfStarted;
    private readonly D3DDevice _d3d;
    private readonly IMFTransform _mft;
    private readonly IMFDXGIDeviceManager? _manager;
    private readonly IMFMediaEventGenerator? _events;
    private readonly CodecApi? _codecApi;
    private readonly bool _async;
    private readonly int _width, _height;
    private readonly bool _providesSamples;
    private readonly int _outputBufferSize;
    private byte[]? _sequenceHeader;
    private int _pendingNeedInput;
    private ID3D11Texture2D? _staging; // software path only
    private readonly string _mime;

    public string Name { get; }
    public bool IsHardware { get; }
    public int BitrateKbps { get; private set; }

    public static void Startup()
    {
        if (_mfStarted) return;
        MediaFactory.MFStartup(false).CheckError();
        _mfStarted = true;
    }

    public static Guid SubtypeFor(string mime) => mime switch
    {
        "video/hevc" => MfGuids.MFVideoFormat_HEVC,
        "video/av01" => MfGuids.MFVideoFormat_AV1,
        _ => MfGuids.MFVideoFormat_H264,
    };

    /// <summary>Which codecs have a hardware encoder on this PC.</summary>
    public static List<string> HardwareMimes()
    {
        Startup();
        var result = new List<string>();
        foreach (var mime in new[] { "video/avc", "video/hevc", "video/av01" })
        {
            try
            {
                using var col = MediaFactory.MFTEnumEx(MfGuids.MFT_CATEGORY_VIDEO_ENCODER,
                    MfGuids.MFT_ENUM_FLAG_HARDWARE | MfGuids.MFT_ENUM_FLAG_SORTANDFILTER,
                    new RegisterTypeInfo { GuidMajorType = MfGuids.MFMediaType_Video, GuidSubtype = MfGuids.MFVideoFormat_NV12 },
                    new RegisterTypeInfo { GuidMajorType = MfGuids.MFMediaType_Video, GuidSubtype = SubtypeFor(mime) });
                if (col.Any()) result.Add(mime);
            }
            catch (Exception ex) { Log.Warn($"Encoder enumeration for {mime} failed: {ex.Message}"); }
        }
        if (!result.Contains("video/avc")) result.Add("video/avc"); // software MFT always present
        return result;
    }

    public MfEncoder(D3DDevice d3d, string mime, int width, int height, int fps, int bitrateKbps,
                     PerformancePreset performance, bool preferHardware = true)
    {
        Startup();
        _d3d = d3d;
        _mime = mime;
        _width = width;
        _height = height;
        BitrateKbps = bitrateKbps;
        Guid subtype = SubtypeFor(mime);

        IMFTransform? mft = null;
        string name = "";
        bool hw = false;
        if (preferHardware)
            (mft, name) = ActivateBest(subtype, MfGuids.MFT_ENUM_FLAG_HARDWARE | MfGuids.MFT_ENUM_FLAG_SORTANDFILTER, d3d.AdapterLuid);
        if (mft != null) hw = true;
        else
        {
            if (mime != "video/avc") throw new NotSupportedException($"No hay codificador por hardware para {mime}");
            (mft, name) = ActivateBest(subtype, MfGuids.MFT_ENUM_FLAG_SYNCMFT | MfGuids.MFT_ENUM_FLAG_SORTANDFILTER, 0);
            if (mft == null) throw new NotSupportedException("No se encontró ningún codificador H.264");
        }
        _mft = mft;
        Name = name;
        IsHardware = hw;

        var attrs = _mft.Attributes;
        _async = attrs.GetUInt32(MfGuids.MF_TRANSFORM_ASYNC, out uint isAsync).Success && isAsync != 0;
        if (_async)
        {
            attrs.Set(MfGuids.MF_TRANSFORM_ASYNC_UNLOCK, 1u).CheckError();
            _events = _mft.QueryInterface<IMFMediaEventGenerator>();
        }
        attrs.Set(MfGuids.MF_LOW_LATENCY, 1u);

        if (hw)
        {
            _manager = MediaFactory.MFCreateDXGIDeviceManager();
            _manager.ResetDevice(d3d.Device).CheckError();
            _mft.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)_manager.NativePointer);
        }

        _codecApi = CodecApi.From(_mft.NativePointer);
        if (_codecApi != null)
        {
            _codecApi.SetBool(MfGuids.CODECAPI_AVLowLatencyMode, true);
            _codecApi.SetUInt32(MfGuids.CODECAPI_AVEncCommonRateControlMode, MfGuids.eAVEncCommonRateControlMode_CBR);
            _codecApi.SetUInt32(MfGuids.CODECAPI_AVEncCommonMeanBitRate, (uint)bitrateKbps * 1000);
            // VBV size in frames: 1 frame keeps every frame small enough to arrive within one frame interval
            // (lowest latency); larger buffers let complex frames (e.g. scrolling text) keep more detail.
            int vbvFrames = performance switch { PerformancePreset.Quality => 4, PerformancePreset.Balanced => 2, _ => 1 };
            _codecApi.SetUInt32(MfGuids.CODECAPI_AVEncCommonBufferSize, (uint)(bitrateKbps * 1000L * vbvFrames / Math.Max(fps, 1)));
            _codecApi.SetUInt32(MfGuids.CODECAPI_AVEncMPVDefaultBPictureCount, 0);
            _codecApi.SetUInt32(MfGuids.CODECAPI_AVEncMPVGOPSize, (uint)Math.Max(fps * 10, 60)); // IDR also sent on demand
            // 0 = fastest encode, 100 = best quality.
            _codecApi.SetUInt32(MfGuids.CODECAPI_AVEncCommonQualityVsSpeed, performance switch
            {
                PerformancePreset.Quality => 90u, PerformancePreset.Balanced => 60u, _ => 30u,
            });
        }

        // Output type must be set before input type on encoders.
        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MfGuids.MF_MT_MAJOR_TYPE, MfGuids.MFMediaType_Video);
            outType.Set(MfGuids.MF_MT_SUBTYPE, subtype);
            outType.Set(MfGuids.MF_MT_AVG_BITRATE, (uint)bitrateKbps * 1000);
            outType.Set(MfGuids.MF_MT_FRAME_SIZE, MediaFactory.PackSize((uint)width, (uint)height));
            outType.Set(MfGuids.MF_MT_FRAME_RATE, MediaFactory.PackRatio(fps, 1));
            outType.Set(MfGuids.MF_MT_PIXEL_ASPECT_RATIO, MediaFactory.PackRatio(1, 1));
            outType.Set(MfGuids.MF_MT_INTERLACE_MODE, (uint)MfGuids.MFVideoInterlace_Progressive);
            outType.Set(MfGuids.MF_MT_VIDEO_NOMINAL_RANGE, (uint)MfGuids.MFNominalRange_16_235);
            outType.Set(MfGuids.MF_MT_YUV_MATRIX, (uint)MfGuids.MFVideoTransferMatrix_BT709);
            outType.Set(MfGuids.MF_MT_VIDEO_PRIMARIES, (uint)MfGuids.MFVideoPrimaries_BT709);
            outType.Set(MfGuids.MF_MT_TRANSFER_FUNCTION, (uint)MfGuids.MFVideoTransFunc_709);
            if (mime == "video/avc") outType.Set(MfGuids.MF_MT_MPEG2_PROFILE, (uint)MfGuids.eAVEncH264VProfile_High);
            if (mime == "video/hevc") outType.Set(MfGuids.MF_MT_MPEG2_PROFILE, (uint)MfGuids.eAVEncH265VProfile_Main_420_8);
            _mft.SetOutputType(0, outType, 0);
        }

        IMFMediaType? inType = null;
        for (int i = 0; ; i++)
        {
            IMFMediaType t;
            try { t = _mft.GetInputAvailableType(0, i); }
            catch { break; }
            if (t.GetGUID(MfGuids.MF_MT_SUBTYPE, out Guid st).Success && st == MfGuids.MFVideoFormat_NV12) { inType = t; break; }
            t.Dispose();
        }
        inType ??= MediaFactory.MFCreateMediaType();
        using (inType)
        {
            inType.Set(MfGuids.MF_MT_MAJOR_TYPE, MfGuids.MFMediaType_Video);
            inType.Set(MfGuids.MF_MT_SUBTYPE, MfGuids.MFVideoFormat_NV12);
            inType.Set(MfGuids.MF_MT_FRAME_SIZE, MediaFactory.PackSize((uint)width, (uint)height));
            inType.Set(MfGuids.MF_MT_FRAME_RATE, MediaFactory.PackRatio(fps, 1));
            inType.Set(MfGuids.MF_MT_PIXEL_ASPECT_RATIO, MediaFactory.PackRatio(1, 1));
            inType.Set(MfGuids.MF_MT_INTERLACE_MODE, (uint)MfGuids.MFVideoInterlace_Progressive);
            inType.Set(MfGuids.MF_MT_VIDEO_NOMINAL_RANGE, (uint)MfGuids.MFNominalRange_16_235);
            inType.Set(MfGuids.MF_MT_YUV_MATRIX, (uint)MfGuids.MFVideoTransferMatrix_BT709);
            _mft.SetInputType(0, inType, 0);
        }

        var osi = _mft.GetOutputStreamInfo(0);
        _providesSamples = (osi.Flags & (MfGuids.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MfGuids.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        _outputBufferSize = Math.Max(osi.Size, width * height * 3 / 2);
        _sequenceHeader = ReadSequenceHeader();

        _mft.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        Log.Info($"Encoder: {Name} ({(hw ? "hardware" : "software")}, {(_async ? "async" : "sync")}) {mime} {width}x{height}@{fps} {bitrateKbps} kbps");
    }

    private static (IMFTransform?, string) ActivateBest(Guid subtype, uint flags, long luid)
    {
        using var col = MediaFactory.MFTEnumEx(MfGuids.MFT_CATEGORY_VIDEO_ENCODER, flags,
            new RegisterTypeInfo { GuidMajorType = MfGuids.MFMediaType_Video, GuidSubtype = MfGuids.MFVideoFormat_NV12 },
            new RegisterTypeInfo { GuidMajorType = MfGuids.MFMediaType_Video, GuidSubtype = subtype });
        var list = col.ToList();
        // Prefer the encoder that lives on the same GPU as the captured desktop (no cross-adapter copies).
        var ordered = list.OrderBy(a => luid != 0 && a.GetUInt64(MfGuids.MFT_ENUM_ADAPTER_LUID, out ulong l).Success && (long)l == luid ? 0 : 1).ToList();
        foreach (var act in ordered)
        {
            string name = "Encoder";
            try { name = act.GetString(MfGuids.MFT_FRIENDLY_NAME_Attribute); } catch { }
            try
            {
                var t = act.ActivateObject<IMFTransform>();
                return (t, name);
            }
            catch (Exception ex) { Log.Warn($"Cannot activate {name}: {ex.Message}"); }
        }
        return (null, "");
    }

    private byte[]? ReadSequenceHeader()
    {
        try
        {
            using var t = _mft.GetOutputCurrentType(0);
            return t.GetBlob(MfGuids.MF_MT_MPEG_SEQUENCE_HEADER);
        }
        catch { return null; }
    }

    public void SetBitrate(int kbps)
    {
        if (_codecApi == null || kbps == BitrateKbps) return;
        int hr = _codecApi.SetUInt32(MfGuids.CODECAPI_AVEncCommonMeanBitRate, (uint)kbps * 1000);
        if (hr == 0) BitrateKbps = kbps;
    }

    public void ForceKeyframe() => _codecApi?.SetUInt32(MfGuids.CODECAPI_AVEncVideoForceKeyFrame, 1);

    /// <summary>True when the encoder can take another frame now.</summary>
    public bool CanAcceptInput => !_async || _pendingNeedInput > 0;

    /// <summary>Pumps encoder events without blocking; returns encoded frames ready to send.</summary>
    public void Poll(List<EncodedFrame> output)
    {
        if (!_async) return;
        while (true)
        {
            IntPtr evPtr;
            var getEvent = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)(*(*(IntPtr**)_events!.NativePointer + 3));
            int hr = getEvent(_events.NativePointer, MfGuids.MF_EVENT_FLAG_NO_WAIT, &evPtr);
            if (hr == MfGuids.MF_E_NO_EVENTS_AVAILABLE || evPtr == IntPtr.Zero) return;
            if (hr < 0) throw new COMException("Encoder event failure", hr);
            using var ev = new IMFMediaEvent(evPtr);
            switch ((int)ev.EventType)
            {
                case MfGuids.METransformNeedInput: _pendingNeedInput++; break;
                case MfGuids.METransformHaveOutput: DrainOutput(output); break;
            }
        }
    }

    public void Encode(ID3D11Texture2D nv12, long ptsUs, List<EncodedFrame> output)
    {
        using var sample = MediaFactory.MFCreateSample();
        if (IsHardware)
        {
            using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, nv12, 0, false);
            sample.AddBuffer(buffer);
        }
        else
        {
            using var buffer = CopyToSystemMemory(nv12);
            sample.AddBuffer(buffer);
        }
        sample.SampleTime = ptsUs * 10;
        sample.SampleDuration = 0;
        _mft.ProcessInput(0, sample, 0);
        if (_async) _pendingNeedInput--;
        else DrainOutput(output);
    }

    private IMFMediaBuffer CopyToSystemMemory(ID3D11Texture2D nv12)
    {
        _staging ??= _d3d.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width, Height = (uint)_height, MipLevels = 1, ArraySize = 1,
            Format = Vortice.DXGI.Format.NV12, SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        _d3d.Context.CopyResource(_staging, nv12);
        int size = _width * _height * 3 / 2;
        var buf = MediaFactory.MFCreateMemoryBuffer(size);
        var map = _d3d.Context.Map(_staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            buf.Lock(out IntPtr dst, out _, out _);
            byte* d = (byte*)dst, s = (byte*)map.DataPointer;
            int pitch = (int)map.RowPitch;
            for (int y = 0; y < _height; y++)
                Buffer.MemoryCopy(s + y * pitch, d + y * _width, _width, _width);
            byte* sUv = s + pitch * _height, dUv = d + _width * _height;
            for (int y = 0; y < _height / 2; y++)
                Buffer.MemoryCopy(sUv + y * pitch, dUv + y * _width, _width, _width);
            buf.Unlock();
            buf.CurrentLength = size;
        }
        finally { _d3d.Context.Unmap(_staging, 0); }
        return buf;
    }

    private void DrainOutput(List<EncodedFrame> output)
    {
        while (true)
        {
            IMFSample? own = null;
            if (!_providesSamples)
            {
                own = MediaFactory.MFCreateSample();
                using var b = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
                own.AddBuffer(b);
            }
            var odb = new OutputDataBuffer { StreamID = 0, Sample = own! };
            Result r = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref odb, out _);
            if (r.Code == MfGuids.MF_E_TRANSFORM_NEED_MORE_INPUT) { own?.Dispose(); return; }
            if (r.Code == MfGuids.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                own?.Dispose();
                using var t = _mft.GetOutputAvailableType(0, 0);
                _mft.SetOutputType(0, t, 0);
                _sequenceHeader = ReadSequenceHeader();
                continue;
            }
            r.CheckError();
            odb.Events?.Dispose();
            using var sample = odb.Sample;
            if (sample == null) return;
            bool key = sample.GetUInt32(MfGuids.MFSampleExtension_CleanPoint, out uint cp).Success && cp != 0;
            long pts = sample.SampleTime / 10;
            using var buf = sample.ConvertToContiguousBuffer();
            buf.Lock(out IntPtr p, out _, out int len);
            byte[] data;
            try
            {
                var span = new ReadOnlySpan<byte>((void*)p, len);
                if (key && _sequenceHeader != null && !HasParameterSets(span))
                {
                    data = new byte[_sequenceHeader.Length + len];
                    _sequenceHeader.CopyTo(data, 0);
                    span.CopyTo(data.AsSpan(_sequenceHeader.Length));
                }
                else data = span.ToArray();
            }
            finally { buf.Unlock(); }
            output.Add(new EncodedFrame(data, pts, key));
            if (_async) return; // one output per METransformHaveOutput event
        }
    }

    /// <summary>Checks Annex-B NAL units for SPS (H.264 type 7) / VPS (HEVC type 32) / AV1 sequence header OBU.</summary>
    private bool HasParameterSets(ReadOnlySpan<byte> s)
    {
        if (_mime == "video/av01") return true;
        for (int i = 0; i + 4 < s.Length && i < 512; i++)
        {
            if (s[i] == 0 && s[i + 1] == 0 && s[i + 2] == 1)
            {
                byte h = s[i + 3];
                if (_mime == "video/avc" && (h & 0x1F) == 7) return true;
                if (_mime == "video/hevc" && ((h >> 1) & 0x3F) == 32) return true;
            }
        }
        return false;
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
        _events?.Dispose();
        try { _mft.QueryInterface<IMFShutdown>()?.Shutdown(); } catch { }
        _mft.Dispose();
        _manager?.Dispose();
        _staging?.Dispose();
    }
}
