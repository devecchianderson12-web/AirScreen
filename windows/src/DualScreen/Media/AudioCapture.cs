using System;
using System.Buffers.Binary;
using DualScreen.Core;
using DualScreen.Net;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualScreen.Media;

/// <summary>
/// Captures what the PC is playing (WASAPI loopback of the default output) and hands it out as 16-bit PCM in
/// ~10 ms AUDIO frames. Uncompressed on purpose: ~1.5 Mbps is nothing on a LAN and avoids codec delay.
/// <code>AUDIO [rate:u32][channels:u8][ptsUs:i64][PCM s16le interleaved]</code>
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private readonly Action<byte[]> _send;
    private WasapiLoopbackCapture? _capture;
    private int _rate, _channels;
    private bool _float;
    private int _bytesPerSample;

    public AudioCapture(Action<byte[]> send) => _send = send;

    public void Start()
    {
        if (_capture != null) return;
        try
        {
            var cap = new WasapiLoopbackCapture();
            var f = cap.WaveFormat;
            _rate = f.SampleRate;
            _channels = Math.Min(f.Channels, 2);
            _float = f.Encoding == WaveFormatEncoding.IeeeFloat || (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32);
            _bytesPerSample = f.BitsPerSample / 8;
            _srcChannels = f.Channels;
            cap.DataAvailable += OnData;
            cap.StartRecording();
            _capture = cap;
            Log.Info($"Audio capture: {f.SampleRate} Hz, {f.Channels} ch, {f.BitsPerSample} bit {(_float ? "float" : "pcm")}");
        }
        catch (Exception ex) { Log.Warn("Audio capture unavailable: " + ex.Message); }
    }

    private int _srcChannels;

    private void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            int frameBytes = _bytesPerSample * _srcChannels;
            if (frameBytes == 0) return;
            int frames = e.BytesRecorded / frameBytes;
            if (frames == 0) return;
            var payload = new byte[13 + frames * _channels * 2];
            BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)_rate);
            payload[4] = (byte)_channels;
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(5), HostClock.NowUs());
            int o = 13;
            for (int i = 0; i < frames; i++)
            {
                for (int c = 0; c < _channels; c++)
                {
                    int at = i * frameBytes + c * _bytesPerSample;
                    short v;
                    if (_float && _bytesPerSample == 4)
                        v = (short)Math.Clamp(BitConverter.ToSingle(e.Buffer, at) * 32767f, -32768f, 32767f);
                    else if (_bytesPerSample == 2) v = BitConverter.ToInt16(e.Buffer, at);
                    else if (_bytesPerSample == 4) v = (short)(BitConverter.ToInt32(e.Buffer, at) >> 16);
                    else if (_bytesPerSample == 3) v = (short)(e.Buffer[at + 1] | (e.Buffer[at + 2] << 8));
                    else v = 0;
                    BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(o), v);
                    o += 2;
                }
            }
            _send(FrameCodec.Encode(MsgType.Audio, payload));
        }
        catch (Exception ex) { Log.Info("Audio frame dropped: " + ex.Message); }
    }

    public void Stop()
    {
        var c = _capture;
        _capture = null;
        if (c == null) return;
        try { c.DataAvailable -= OnData; c.StopRecording(); } catch { }
        c.Dispose();
    }

    public void Dispose() => Stop();
}
