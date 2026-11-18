using System;
using DualScreen.Core;

namespace DualScreen.Net;

/// <summary>
/// Delay-based adaptive bitrate (AIMD), in the spirit of WebRTC's GCC: it reacts to growth of the sender
/// queue delay and of the round-trip time instead of waiting for packet loss (TCP/SCTP never "lose" packets,
/// they just add latency). When even the floor bitrate cannot be sustained, it lowers the frame rate.
/// </summary>
public sealed class BitrateController
{
    private readonly int _minKbps;
    private readonly int _maxKbps;
    private readonly int _maxFps;
    private double _targetKbps;
    private double _baseRtt = double.MaxValue;
    private DateTime _baseRttReset = DateTime.UtcNow;
    private DateTime _lastIncrease = DateTime.UtcNow;
    private DateTime _congestedSince = DateTime.MaxValue;
    private DateTime _healthySince = DateTime.UtcNow;

    public int TargetKbps => (int)_targetKbps;
    public int TargetFps { get; private set; }
    public bool Congested { get; private set; }

    public BitrateController(int width, int height, int fps, QualityPreset quality, int maxMbpsSetting)
    {
        double bpp = quality switch
        {
            QualityPreset.Maximum => 0.16,
            QualityPreset.High => 0.11,
            QualityPreset.Balanced => 0.075,
            QualityPreset.Saver => 0.045,
            _ => 0.11,
        };
        // Desktop content compresses well; the effective bits-per-pixel shrinks as frame rate grows.
        double fpsFactor = Math.Sqrt(60.0 / Math.Max(fps, 30)) * (fps / 60.0);
        double nominal = width * (double)height * 60 * bpp * fpsFactor / 1000.0;
        int cap = maxMbpsSetting > 0 ? maxMbpsSetting * 1000 : 150_000;
        _maxKbps = (int)Math.Clamp(nominal * (quality == QualityPreset.Auto ? 1.6 : 1.2), 2_000, cap);
        _minKbps = (int)Math.Clamp(nominal * 0.12, 1_000, _maxKbps);
        _targetKbps = Math.Min(nominal, _maxKbps);
        _maxFps = fps;
        TargetFps = fps;
    }

    /// <summary>Call ~every 250 ms. Returns true when the target changed noticeably.</summary>
    public bool Update(double queueDelayMs, int queuedFrames, double rttMs, int droppedRecently)
    {
        var now = DateTime.UtcNow;
        if (!double.IsNaN(rttMs))
        {
            if (now - _baseRttReset > TimeSpan.FromSeconds(20)) { _baseRtt = rttMs; _baseRttReset = now; }
            _baseRtt = Math.Min(_baseRtt, rttMs);
        }
        double frameMs = 1000.0 / TargetFps;
        bool rttGrowing = !double.IsNaN(rttMs) && _baseRtt < double.MaxValue && rttMs > _baseRtt + Math.Max(25, frameMs * 2);
        bool queueGrowing = queueDelayMs > frameMs * 1.5 || queuedFrames >= 3;
        Congested = rttGrowing || queueGrowing || droppedRecently > 0;

        double before = _targetKbps;
        int fpsBefore = TargetFps;
        if (Congested)
        {
            _healthySince = now;
            if (_congestedSince == DateTime.MaxValue) _congestedSince = now;
            _targetKbps = Math.Max(_minKbps, _targetKbps * 0.80);
            _lastIncrease = now;
            if (_targetKbps <= _minKbps * 1.01 && now - _congestedSince > TimeSpan.FromSeconds(3) && TargetFps > 30)
            {
                TargetFps = TargetFps > 60 ? 60 : 30;
                _congestedSince = now;
                Log.Info($"Link cannot sustain the stream; lowering frame rate to {TargetFps}");
            }
        }
        else
        {
            _congestedSince = DateTime.MaxValue;
            if (now - _lastIncrease > TimeSpan.FromMilliseconds(1000))
            {
                _targetKbps = Math.Min(_maxKbps, _targetKbps * 1.08 + 100);
                _lastIncrease = now;
            }
            if (TargetFps < _maxFps && now - _healthySince > TimeSpan.FromSeconds(10) && _targetKbps >= _maxKbps * 0.5)
            {
                TargetFps = _maxFps;
                _healthySince = now;
            }
        }
        return Math.Abs(before - _targetKbps) > before * 0.04 || fpsBefore != TargetFps;
    }
}
