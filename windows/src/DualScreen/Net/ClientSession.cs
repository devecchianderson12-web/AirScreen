using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DualScreen.Core;

namespace DualScreen.Net;

/// <summary>
/// One authenticated connection to a phone, over any byte stream (TLS/TCP or WebRTC data channel).
/// Owns a dedicated writer loop with a small bounded video queue, so a slow link never blocks capture/encode:
/// the pipeline checks <see cref="QueuedVideoFrames"/> and skips capturing instead of piling up latency.
/// </summary>
public sealed class ClientSession : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly Channel<byte[]> _control = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<(byte[] data, long enqueuedTicks)> _video =
        Channel.CreateBounded<(byte[], long)>(new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Task? _writer, _reader, _pinger;
    private int _queuedVideo;
    private int _closed;
    private long _lastRxUs = HostClock.NowUs();

    public string Transport { get; }
    public string RemoteDescription { get; }
    public HelloMsg Hello { get; }
    public TrustedDevice? Device { get; set; }

    public event Action<Frame>? FrameReceived;
    public event Action<ClientSession, string>? Closed;

    public double RttMs { get; private set; } = double.NaN;
    public double QueueDelayMs { get; private set; }
    public ClientStatsMsg? LastStats { get; private set; }
    public int QueuedVideoFrames => Volatile.Read(ref _queuedVideo);
    public bool IsClosed => _closed != 0;
    public CancellationToken Token => _cts.Token;
    /// <summary>The receiver asked for the PC's sound (HOST_SETTINGS audio).</summary>
    public bool AudioEnabled { get; set; }

    public ClientSession(Stream stream, HelloMsg hello, string transport, string remoteDescription)
    {
        _stream = stream;
        Hello = hello;
        Transport = transport;
        RemoteDescription = remoteDescription;
    }

    public void Start()
    {
        _writer = Task.Run(WriteLoop);
        _reader = Task.Run(ReadLoop);
        _pinger = Task.Run(PingLoop);
    }

    public void SendControl(byte[] frame)
    {
        if (!IsClosed) _control.Writer.TryWrite(frame);
    }

    public void SendJson<T>(MsgType type, T msg) => SendControl(FrameCodec.EncodeJson(type, msg));

    /// <summary>Queues an encoded video frame. Returns false if the queue is full (caller should drop and request IDR).</summary>
    public bool TrySendVideo(byte[] frame)
    {
        if (IsClosed) return false;
        if (_video.Writer.TryWrite((frame, _clock.ElapsedTicks)))
        {
            Interlocked.Increment(ref _queuedVideo);
            return true;
        }
        return false;
    }

    private async Task WriteLoop()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Control frames always go first (pings must not wait behind video).
                while (_control.Reader.TryRead(out var c))
                {
                    await _stream.WriteAsync(c, ct).ConfigureAwait(false);
                }
                if (_video.Reader.TryRead(out var v))
                {
                    Interlocked.Decrement(ref _queuedVideo);
                    await _stream.WriteAsync(v.data, ct).ConfigureAwait(false);
                    double delay = (_clock.ElapsedTicks - v.enqueuedTicks) * 1000.0 / Stopwatch.Frequency;
                    QueueDelayMs = QueueDelayMs * 0.8 + delay * 0.2;
                    continue;
                }
                await _stream.FlushAsync(ct).ConfigureAwait(false);
                var waitControl = _control.Reader.WaitToReadAsync(ct).AsTask();
                var waitVideo = _video.Reader.WaitToReadAsync(ct).AsTask();
                await Task.WhenAny(waitControl, waitVideo).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Close("write failed: " + ex.Message); }
    }

    private async Task ReadLoop()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var f = await FrameCodec.ReadAsync(_stream, ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastRxUs, HostClock.NowUs());
                try
                {
                    switch (f.Type)
                    {
                        case MsgType.Ping:
                            // Echo + the PC clock: lets the receiver estimate the clock offset and measure real
                            // capture-to-display latency (older receivers only read the first 8 bytes).
                            var pong = new byte[16];
                            f.Payload[..Math.Min(8, f.Length)].CopyTo(pong);
                            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(pong.AsSpan(8), HostClock.NowUs());
                            SendControl(FrameCodec.Encode(MsgType.Pong, pong));
                            break;
                        case MsgType.Pong when f.Length >= 8:
                            long sent = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(f.Payload);
                            double rtt = (NowUs() - sent) / 1000.0;
                            RttMs = double.IsNaN(RttMs) ? rtt : RttMs * 0.7 + rtt * 0.3;
                            break;
                        case MsgType.Stats:
                            LastStats = f.Json<ClientStatsMsg>();
                            FrameReceived?.Invoke(f);
                            break;
                        case MsgType.Bye:
                            Close("phone disconnected");
                            return;
                        default:
                            FrameReceived?.Invoke(f);
                            break;
                    }
                }
                finally { f.Return(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { Close("connection closed"); }
        catch (Exception ex) { Close("read failed: " + ex.Message); }
    }

    private async Task PingLoop()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SendControl(FrameCodec.EncodeTimestamp(MsgType.Ping, NowUs()));
                await Task.Delay(1000, ct).ConfigureAwait(false);
                // Watchdog: receivers ping every second; 10 s of silence means a dead link (Wi-Fi gone, sleep…).
                if (HostClock.NowUs() - Interlocked.Read(ref _lastRxUs) > 10_000_000)
                {
                    Close("timeout: the device stopped answering");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static long NowUs() => HostClock.NowUs();

    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Log.Info($"Session {Hello.DeviceName} closed: {reason}");
        try
        {
            // Best-effort goodbye; do not wait on a dead link.
            var bye = FrameCodec.EncodeJson(MsgType.Bye, new ByeMsg { Reason = reason });
            _stream.WriteAsync(bye).AsTask().Wait(300);
        }
        catch { }
        _cts.Cancel();
        _control.Writer.TryComplete();
        _video.Writer.TryComplete();
        try { _stream.Dispose(); } catch { }
        Closed?.Invoke(this, reason);
    }

    public async ValueTask DisposeAsync()
    {
        Close("disposed");
        try
        {
            if (_writer != null) await _writer.ConfigureAwait(false);
            if (_reader != null) await _reader.ConfigureAwait(false);
            if (_pinger != null) await _pinger.ConfigureAwait(false);
        }
        catch { }
        _cts.Dispose();
    }
}
