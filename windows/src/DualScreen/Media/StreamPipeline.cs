using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using DualScreen.Core;
using DualScreen.Display;
using DualScreen.Net;

namespace DualScreen.Media;

public sealed class PipelineStats
{
    public double EncodeFps;
    public double SentKbps;
    public double EncodeMs;
    public int TargetKbps;
    public int TargetFps;
    public string Encoder = "";
    public bool Congested;
}

/// <summary>
/// Capture → GPU colour conversion → hardware encode → session queue, on one high-priority thread.
/// Frames are paced to the negotiated refresh rate, and capture is skipped (not queued) whenever the link is
/// behind, so latency never accumulates.
/// </summary>
public sealed class StreamPipeline : IDisposable
{
    private readonly D3DDevice _d3d;
    private readonly DisplayTarget _target;
    private readonly ClientSession _session;
    private readonly BitrateController _abr;
    private string _mime;
    /// <summary>Codec actually used (H.264 when the requested hardware HEVC/AV1 encoder cannot start).</summary>
    public string Mime => _mime;
    private readonly int _outW, _outH;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private ICaptureSource? _capture;
    private FrameConverter? _converter;
    private MfEncoder? _encoder;
    private volatile bool _keyframeRequested = true;
    private volatile bool _captureResized;

    public PipelineStats Stats { get; } = new();
    public event Action<Exception>? Failed;
    /// <summary>The captured monitor changed size (resolution or rotation changed in Windows Settings).</summary>
    public event Action<int, int>? SourceResized;
    private CursorTracker? _cursor;

    public StreamPipeline(D3DDevice d3d, DisplayTarget target, ClientSession session, BitrateController abr, string mime, int outW, int outH)
    {
        _d3d = d3d;
        _target = target;
        _session = session;
        _abr = abr;
        _mime = mime;
        _outW = outW;
        _outH = outH;
        _thread = new Thread(RunThread) { IsBackground = true, Name = "DualScreen.Pipeline", Priority = ThreadPriority.Highest };
    }

    public void Start()
    {
        _capture = CreateCapture();
        try { _capture.Start(); }
        catch (Exception ex) when (_capture is WgcCapture)
        {
            // e.g. 0x80070422: the Windows capture service is disabled on this PC.
            Log.Warn("Windows.Graphics.Capture could not start (" + ex.Message + "); using Desktop Duplication");
            _capture.Dispose();
            _capture = new DuplicationCapture(_d3d, _target.Monitor);
            _capture.Start();
        }
        _capture.SizeChanged += (w, h) => { _captureResized = true; SourceResized?.Invoke(w, h); };
        if (_session.Hello.Caps.Has("cursorPos")) _cursor = new CursorTracker(_session, _target.Monitor, _outW, _outH); // phone touchpad mode
        _converter = new FrameConverter(_d3d, _capture.Width, _capture.Height, _outW, _outH, _abr.TargetFps);
        _encoder = CreateEncoder();
        Stats.Encoder = _encoder.Name + (_encoder.IsHardware ? " (HW)" : " (SW)");
    }

    /// <summary>Starts encoding and sending (call after STREAM_CONFIG with <see cref="Mime"/> was sent).</summary>
    public void Run() => _thread.Start();

    /// <summary>Windows.Graphics.Capture first; Desktop Duplication when WGC is unavailable on this PC.</summary>
    private ICaptureSource CreateCapture()
    {
        try { return new WgcCapture(_d3d, _target.Monitor); }
        catch (Exception ex)
        {
            Log.Warn("Windows.Graphics.Capture unavailable (" + ex.Message + "); using Desktop Duplication");
            return new DuplicationCapture(_d3d, _target.Monitor);
        }
    }

    private MfEncoder CreateEncoder()
    {
        var perf = SettingsStore.Current.Performance;
        try { return new MfEncoder(_d3d, _mime, _outW, _outH, _abr.TargetFps, _abr.TargetKbps, perf, preferHardware: true); }
        catch (Exception ex) when (_mime != "video/avc")
        {
            Log.Warn($"{_mime} encoder unavailable, falling back to H.264: " + ex.Message);
            _mime = "video/avc";
        }
        try { return new MfEncoder(_d3d, _mime, _outW, _outH, _abr.TargetFps, _abr.TargetKbps, perf, preferHardware: true); }
        catch (Exception ex)
        {
            Log.Warn("Hardware encoder unavailable, using software H.264: " + ex.Message);
            return new MfEncoder(_d3d, _mime, _outW, _outH, _abr.TargetFps, _abr.TargetKbps, perf, preferHardware: false);
        }
    }

    public void RequestKeyframe() => _keyframeRequested = true;

    private void RunThread()
    {
        IntPtr mmcss = Perf.EnterRealtime();
        try { RunCore(); }
        finally { Perf.LeaveRealtime(mmcss); }
    }

    private void RunCore()
    {
        var clock = Stopwatch.StartNew();
        // Static-content refinement: after the screen stops changing, the last image is re-encoded a few times.
        // Each pass spends the (now idle) bandwidth on detail, so text becomes sharp without adding latency.
        const int RefinePasses = 4;
        int refineLeft = 0;
        double lastNewFrameMs = 0;
        var outFrames = new List<EncodedFrame>(4);
        var submitTimes = new Dictionary<long, double>();
        long lastCaptured = -1;
        double nextDueMs = 0, lastAbrMs = 0, statsWindowStart = 0;
        long statsBytes = 0; int statsFrames = 0;
        int droppedSinceAbr = 0;

        try
        {
            while (!_cts.IsCancellationRequested && !_session.IsClosed)
            {
                double now = clock.Elapsed.TotalMilliseconds;

                if (_captureResized)
                {
                    _captureResized = false;
                    _converter?.Dispose();
                    _converter = new FrameConverter(_d3d, _capture!.Width, _capture.Height, _outW, _outH, _abr.TargetFps);
                    _keyframeRequested = true;
                    lastCaptured = -1;
                }

                _encoder!.Poll(outFrames);
                foreach (var f in outFrames)
                {
                    if (submitTimes.Remove(f.PtsUs, out double t0))
                        Stats.EncodeMs = Stats.EncodeMs * 0.9 + (now - t0) * 0.1;
                    var flags = f.Keyframe ? VideoFlags.Keyframe : VideoFlags.None;
                    if (!_session.TrySendVideo(FrameCodec.EncodeVideo(flags, f.PtsUs, f.Data)))
                    {
                        droppedSinceAbr++;
                        _keyframeRequested = true; // a reference frame was lost: resync with an IDR
                    }
                    statsBytes += f.Data.Length;
                    statsFrames++;
                }
                outFrames.Clear();

                long counter = _capture!.Counter;
                bool haveNew = counter != lastCaptured;
                bool linkReady = _session.QueuedVideoFrames < 2;
                bool due = now >= nextDueMs;
                if (haveNew) { lastNewFrameMs = now; refineLeft = RefinePasses; }
                bool refine = !haveNew && refineLeft > 0 && lastCaptured >= 0 && now - lastNewFrameMs >= 120
                              && _session.QueuedVideoFrames == 0;
                // A keyframe request re-encodes the last image even if the screen is static.
                bool wantFrame = (haveNew || refine || (_keyframeRequested && lastCaptured >= 0)) && due && linkReady;

                if (wantFrame && _encoder.CanAcceptInput)
                {
                    if (_keyframeRequested)
                    {
                        _encoder.ForceKeyframe();
                        _keyframeRequested = false;
                    }
                    if (!haveNew && refine) refineLeft--;
                    // pts = capture time (QPC µs): receivers measure true capture-to-display latency with it.
                    long pts = _capture.TimeUs;
                    if (pts <= 0 || haveNew == false) pts = Net.HostClock.NowUs();
                    while (submitTimes.ContainsKey(pts)) pts++;
                    lock (_capture.TextureLock)
                    {
                        if (_capture.Latest != null)
                        {
                            var nv12 = _converter!.Convert(_capture.Latest);
                            submitTimes[pts] = now;
                            _encoder.Encode(nv12, pts, outFrames);
                        }
                    }
                    lastCaptured = counter;
                    double interval = 1000.0 / _abr.TargetFps;
                    nextDueMs = Math.Max(nextDueMs + interval, now + interval * 0.5);
                    if (submitTimes.Count > 32) submitTimes.Clear();
                    continue; // sync encoders produced output already; loop to send it
                }

                if (now - lastAbrMs >= 250)
                {
                    lastAbrMs = now;
                    if (_abr.Update(_session.QueueDelayMs, _session.QueuedVideoFrames, _session.RttMs, droppedSinceAbr))
                        _encoder.SetBitrate(_abr.TargetKbps);
                    droppedSinceAbr = 0;
                    Stats.TargetKbps = _abr.TargetKbps;
                    Stats.TargetFps = _abr.TargetFps;
                    Stats.Congested = _abr.Congested;
                }
                if (now - statsWindowStart >= 1000)
                {
                    double secs = (now - statsWindowStart) / 1000.0;
                    Stats.EncodeFps = statsFrames / secs;
                    Stats.SentKbps = statsBytes * 8 / 1000.0 / secs;
                    statsFrames = 0; statsBytes = 0; statsWindowStart = now;
                }

                // Sleep until something can happen: a new captured frame, the pacing deadline, or encoder output.
                _cursor?.Poll();
                int wait = submitTimes.Count > 0 ? 1 : (int)Math.Clamp(nextDueMs - now, 1, _cursor != null ? 8 : refineLeft > 0 ? 20 : 50);
                if (!haveNew || !due) _capture.FrameEvent.WaitOne(wait);
                else Thread.Sleep(1); // frame pending but link/encoder busy
            }
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
            {
                Log.Error("Pipeline failure", ex);
                Failed?.Invoke(ex);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _capture?.FrameEvent.Set(); } catch { }
        if (_thread.IsAlive && !_thread.Join(2000)) Log.Warn("Pipeline thread did not stop in time");
        _encoder?.Dispose();
        _converter?.Dispose();
        _capture?.Dispose();
        _cursor?.Dispose();
        _cts.Dispose();
    }
}
