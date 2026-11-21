using System;
using System.Buffers.Binary;
using DualScreen.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualScreen.Receiver;

/// <summary>
/// Plays the host's sound (AUDIO frames) with a small buffer: WASAPI shared mode at ~30 ms, and anything beyond
/// ~80 ms buffered is dropped, so sound never drifts behind the picture.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private WasapiOut? _out;
    private BufferedWaveProvider? _buffer;
    private int _rate, _channels;

    public void Feed(ReadOnlySpan<byte> p)
    {
        if (p.Length < 13) return;
        int rate = (int)BinaryPrimitives.ReadUInt32BigEndian(p);
        int channels = p[4];
        if (rate <= 0 || channels is < 1 or > 2) return;
        if (_out == null || rate != _rate || channels != _channels) Open(rate, channels);
        var buf = _buffer;
        if (buf == null) return;
        if (buf.BufferedDuration.TotalMilliseconds > 80) buf.ClearBuffer(); // catch up instead of lagging
        var pcm = p[13..].ToArray();
        buf.AddSamples(pcm, 0, pcm.Length);
    }

    private void Open(int rate, int channels)
    {
        Close();
        try
        {
            _rate = rate; _channels = channels;
            _buffer = new BufferedWaveProvider(new WaveFormat(rate, 16, channels))
            {
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            _out = new WasapiOut(AudioClientShareMode.Shared, true, 30);
            _out.Init(_buffer);
            _out.Play();
            Log.Info($"Audio playback {rate} Hz {channels} ch");
        }
        catch (Exception ex)
        {
            Log.Warn("Audio playback unavailable: " + ex.Message);
            Close();
        }
    }

    private void Close()
    {
        try { _out?.Stop(); } catch { }
        _out?.Dispose();
        _out = null;
        _buffer = null;
    }

    public void Dispose() => Close();
}
