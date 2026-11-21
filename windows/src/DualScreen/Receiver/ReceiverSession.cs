using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DualScreen.Core;
using DualScreen.Media;
using DualScreen.Net;

namespace DualScreen.Receiver;

/// <summary>
/// One live "use this PC as a monitor" session: a dedicated thread reads frames and decodes + presents each
/// video frame as soon as it arrives (no jitter buffer, no queue), while input, pings and stats go out through
/// a small writer task so the UI thread never blocks on the network.
/// </summary>
public sealed class ReceiverSession
{
    private readonly Stream _stream;
    private readonly D3DDevice _d3d;
    private readonly ViewerWindow _viewer;
    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _reader;
    private VideoRenderer? _renderer;
    private MfDecoder? _decoder;
    private bool _waitKeyframe = true;
    private int _closed;
    private int _framesWindow, _dropped, _decodeErrors;
    private long _bytesWindow;
    private double _decodeMsAvg;
    private double _latencyAvg;
    private long _clockOffsetUs;      // host clock - local clock
    private double _bestRttUs = double.MaxValue;
    private bool _haveOffset;
    private readonly AudioPlayer _audio = new();

    public string HostName { get; }
    public string Transport { get; }
    public StreamConfigMsg? Config { get; private set; }
    public double RttMs { get; private set; } = double.NaN;
    public float Fps { get; private set; }
    public float RecvKbps { get; private set; }
    public float DecodeMs => (float)_decodeMsAvg;
    /// <summary>Measured capture-to-display latency (ms), or 0 until the clock offset is known.</summary>
    public float LatencyMs => _haveOffset ? (float)_latencyAvg : 0;
    public HostStateMsg? HostState { get; private set; }
    public string DecoderName => _decoder == null ? "" : _decoder.Name + (_decoder.IsHardware ? " (DXVA)" : " (software)");
    public bool IsClosed => _closed != 0;

    /// <summary>reason, closedByHost.</summary>
    public event Action<string, bool>? Closed;
    public event Action? Changed;

    public ReceiverSession(Stream stream, D3DDevice d3d, ViewerWindow viewer, string hostName, string transport)
    {
        _stream = stream;
        _d3d = d3d;
        _viewer = viewer;
        HostName = hostName;
        Transport = transport;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "DualScreen.Receiver", Priority = ThreadPriority.Highest };
    }

    public void Start()
    {
        _renderer = new VideoRenderer(_d3d, _viewer.Handle, _viewer.Width, _viewer.Height);
        var r = _renderer;
        _viewer.ContentRect = () => { var c = r.ContentRect; return (c.Left, c.Top, c.Right, c.Bottom); };
        _viewer.Resized += (w, h) => { try { r.Resize(w, h); } catch (Exception ex) { Log.Warn("Resize failed: " + ex.Message); } };
        _viewer.Send = Send;
        // Tell the PC whether we want its sound (setting of this PC as a receiver).
        Send(FrameCodec.EncodeJson(MsgType.HostSettings, new HostSettingsMsg { Audio = SettingsStore.Current.ReceiverAudio }));
        _ = Task.Run(WriteLoop);
        _ = Task.Run(PingLoop);
        _reader.Start();
    }

    public void Send(byte[] frame)
    {
        if (!IsClosed) _out.Writer.TryWrite(frame);
    }

    private async Task WriteLoop()
    {
        try
        {
            while (await _out.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (_out.Reader.TryRead(out var f)) await _stream.WriteAsync(f, _cts.Token).ConfigureAwait(false);
                await _stream.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Close("write failed: " + ex.Message, false); }
    }

    private async Task PingLoop()
    {
        var ct = _cts.Token;
        long lastBytes = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Send(FrameCodec.EncodeTimestamp(MsgType.Ping, NowUs()));
                await Task.Delay(1000, ct).ConfigureAwait(false);
                int frames = Interlocked.Exchange(ref _framesWindow, 0);
                long bytes = Interlocked.Read(ref _bytesWindow);
                Fps = frames;
                RecvKbps = (bytes - lastBytes) * 8f / 1000f;
                lastBytes = bytes;
                Send(FrameCodec.EncodeJson(MsgType.Stats, new ClientStatsMsg
                {
                    Fps = Fps, DecodeMs = DecodeMs, RecvKbps = RecvKbps, Dropped = _dropped, LatencyMs = LatencyMs,
                }));
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ReadLoop()
    {
        IntPtr mmcss = Perf.EnterRealtime();
        Perf.BeginStreaming();
        try { ReadLoopCore(); }
        finally { Perf.LeaveRealtime(mmcss); Perf.EndStreaming(); }
    }

    private void ReadLoopCore()
    {
        string reason = "connection closed";
        bool byHost = false;
        try
        {
            while (!IsClosed)
            {
                var f = FrameCodec.Read(_stream);
                try
                {
                    switch (f.Type)
                    {
                        case MsgType.Video: OnVideo(f.Payload); break;
                        case MsgType.StreamConfig: OnConfig(f.Json<StreamConfigMsg>()); break;
                        case MsgType.Ping: Send(FrameCodec.Encode(MsgType.Pong, f.Payload)); break;
                        case MsgType.Pong when f.Length >= 8:
                            long t0 = BinaryPrimitives.ReadInt64BigEndian(f.Payload);
                            long t1 = NowUs();
                            double rtt = (t1 - t0) / 1000.0;
                            RttMs = double.IsNaN(RttMs) ? rtt : RttMs * 0.7 + rtt * 0.3;
                            if (f.Length >= 16)
                            {
                                // NTP-style offset from the lowest-RTT sample (least queuing error).
                                long hostNow = BinaryPrimitives.ReadInt64BigEndian(f.Payload[8..]);
                                double rttUs = t1 - t0;
                                if (rttUs <= _bestRttUs * 1.2 || !_haveOffset)
                                {
                                    if (rttUs < _bestRttUs) _bestRttUs = rttUs;
                                    _clockOffsetUs = hostNow - (t0 + (long)(rttUs / 2));
                                    _haveOffset = true;
                                }
                            }
                            break;
                        case MsgType.Cursor: _viewer.OnHostCursor(f.Payload); break;
                        case MsgType.Audio: _audio.Feed(f.Payload); break;
                        case MsgType.HostState:
                            HostState = f.Json<HostStateMsg>();
                            Changed?.Invoke();
                            break;
                        case MsgType.Bye:
                            reason = f.Json<ByeMsg>()?.Reason ?? "closed by host";
                            byHost = true;
                            return;
                    }
                }
                finally { f.Return(); }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or InvalidOperationException)
        {
            if (!IsClosed) reason = "conexión perdida";
        }
        catch (Exception ex)
        {
            Log.Error("Receiver failure", ex);
            reason = ex.Message;
        }
        finally
        {
            try { _decoder?.Dispose(); } catch { }
            _decoder = null;
            try { _audio.Dispose(); } catch { }
            Close(reason, byHost);
        }
    }

    private void OnConfig(StreamConfigMsg? cfg)
    {
        if (cfg == null || cfg.Width <= 0 || cfg.Height <= 0) return;
        Config = cfg;
        _decoder?.Dispose();
        _decoder = null;
        _decoder = new MfDecoder(_d3d, cfg.Mime, cfg.Width, cfg.Height, cfg.Fps);
        _renderer!.SetVideoSize(cfg.Width, cfg.Height);
        _viewer.VideoWidth = cfg.Width;
        _waitKeyframe = true;
        RequestKeyframe();
        Log.Info($"Receiving {cfg.Mime} {cfg.Width}x{cfg.Height}@{cfg.Fps} ({cfg.Mode}) from {HostName}");
        Changed?.Invoke();
    }

    private void OnVideo(ReadOnlySpan<byte> p)
    {
        if (p.Length < 9 || _decoder == null) return;
        Interlocked.Add(ref _bytesWindow, p.Length);
        bool key = (p[0] & (byte)VideoFlags.Keyframe) != 0;
        if (_waitKeyframe && !key) { _dropped++; return; }
        _waitKeyframe = false;
        long pts = BinaryPrimitives.ReadInt64BigEndian(p[1..]);
        long t0 = _clock.ElapsedTicks;
        try
        {
            _decoder.Decode(p[9..], pts, (tex, slice) =>
            {
                _renderer!.Present(tex, slice);
                Interlocked.Increment(ref _framesWindow);
                if (_haveOffset)
                {
                    double lat = (NowUs() + _clockOffsetUs - pts) / 1000.0;
                    if (lat is > 0 and < 2000) _latencyAvg = _latencyAvg == 0 ? lat : _latencyAvg * 0.9 + lat * 0.1;
                }
            });
            double ms = (_clock.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
            _decodeMsAvg = _decodeMsAvg * 0.9 + ms * 0.1;
        }
        catch (Exception ex)
        {
            if (_decodeErrors++ < 10) Log.Warn("Decode/present failed: " + ex.Message);
            _dropped++;
            _waitKeyframe = true;
            RequestKeyframe();
        }
    }

    private void RequestKeyframe() => Send(FrameCodec.Encode(MsgType.KeyframeRequest, ReadOnlySpan<byte>.Empty));

    private static long NowUs() => HostClock.NowUs();

    /// <summary>Changes a setting on the PC being shown (applied to its own DualScreen settings).</summary>
    public void SendHostSettings(HostSettingsMsg m) => Send(FrameCodec.EncodeJson(MsgType.HostSettings, m));

    public void Close(string reason, bool byHost)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Log.Info($"Receiver session with {HostName} closed: {reason}");
        if (!byHost)
        {
            try { _stream.WriteAsync(FrameCodec.EncodeJson(MsgType.Bye, new ByeMsg { Reason = reason })).AsTask().Wait(300); } catch { }
        }
        _cts.Cancel();
        _out.Writer.TryComplete();
        try { _stream.Dispose(); } catch { }
        // The read thread disposes the decoder; wait for it before tearing down the renderer it uses.
        if (Thread.CurrentThread != _reader && _reader.IsAlive) _reader.Join(2000);
        try { _renderer?.Dispose(); } catch { }
        _renderer = null;
        Closed?.Invoke(reason, byHost);
    }
}
