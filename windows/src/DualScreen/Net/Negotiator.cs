using System;
using System.Collections.Generic;
using System.Linq;
using DualScreen.Core;

namespace DualScreen.Net;

public sealed record VideoMode(int Width, int Height, int Fps)
{
    public override string ToString() => $"{Width} × {Height} @ {Fps} Hz";
}

public sealed record NegotiatedCaps(
    IReadOnlyList<(int W, int H)> Resolutions,
    IReadOnlyDictionary<(int W, int H), IReadOnlyList<int>> FpsByResolution,
    IReadOnlyList<string> Codecs);

/// <summary>
/// Capability negotiation. The phone reports what its screen and hardware decoders can do; the PC reports which
/// hardware encoders exist. Only modes both sides can really sustain are offered — nothing is promised blindly.
/// </summary>
public static class Negotiator
{
    /// <summary>Candidate resolutions (landscape). The phone tests the same list (see shared/PROTOCOL.md).</summary>
    public static readonly (int W, int H)[] StandardResolutions =
    {
        (3840, 2160), (3200, 1800), (2960, 1848), (2560, 1600), (2560, 1440), (2400, 1080), (2340, 1080),
        (2048, 1536), (1920, 1200), (1920, 1080), (1680, 1050), (1600, 900), (1440, 900), (1366, 768),
        (1280, 800), (1280, 720),
    };

    public static readonly int[] StandardRates = { 30, 60, 72, 75, 90, 120, 144, 165, 240 };

    public static NegotiatedCaps Compute(DeviceCaps caps, IReadOnlyCollection<string> encoderMimes)
    {
        var codecs = caps.Codecs.Where(c => encoderMimes.Contains(c.Mime)).ToList();
        if (codecs.Count == 0)
            codecs = caps.Codecs.Where(c => c.Mime == "video/avc").ToList(); // software H.264 encoder fallback exists on every Windows

        var phoneRates = caps.RefreshRates.Select(r => (int)Math.Round(r)).Where(r => r >= 30).Distinct().ToList();
        if (phoneRates.Count == 0) phoneRates.Add(60);
        int maxPhoneRate = phoneRates.Max();

        var fpsByRes = new Dictionary<(int, int), IReadOnlyList<int>>();
        var native = Landscape(caps.ScreenWidth, caps.ScreenHeight);

        IEnumerable<(int W, int H)> candidates = StandardResolutions.Append(native).Distinct();
        foreach (var res in candidates)
        {
            int best = 0;
            foreach (var c in codecs)
            {
                var m = c.Modes.FirstOrDefault(x => x.Width == res.W && x.Height == res.H);
                if (m == null)
                {
                    // Not explicitly tested: accept only if within decoder max size, and assume 60.
                    bool fits = (res.W <= c.MaxWidth && res.H <= c.MaxHeight) || (res.H <= c.MaxWidth && res.W <= c.MaxHeight);
                    if (fits) best = Math.Max(best, 60);
                }
                else best = Math.Max(best, m.MaxFps);
            }
            if (best < 30) continue;
            var rates = StandardRates
                .Concat(phoneRates)
                .Distinct()
                .Where(r => r <= best && r <= Math.Max(maxPhoneRate, 60))
                .Where(r => r == 30 || r == 60 || phoneRates.Contains(r)) // offer only rates the panel really runs at
                .OrderBy(r => r)
                .ToList();
            if (rates.Count > 0) fpsByRes[res] = rates;
        }

        var resolutions = fpsByRes.Keys.OrderByDescending(r => r.Item1 * r.Item2).ToList();
        return new NegotiatedCaps(resolutions, fpsByRes, codecs.Select(c => c.Mime).Distinct().ToList());
    }

    public static (int W, int H) Landscape(int w, int h) => w >= h ? (w & ~1, h & ~1) : (h & ~1, w & ~1);

    public static VideoMode Choose(NegotiatedCaps neg, DeviceCaps caps, AppSettings s)
    {
        (int W, int H) res;
        var native = Landscape(caps.ScreenWidth, caps.ScreenHeight);
        if (s.Resolution != "auto" && TryParse(s.Resolution, out var wanted) && neg.FpsByResolution.ContainsKey(wanted))
            res = wanted;
        else if (neg.FpsByResolution.ContainsKey(native))
            res = native;
        else
            res = neg.Resolutions.FirstOrDefault(r => r.W <= 1920) is var r && r != default ? r : (1280, 720);

        var rates = neg.FpsByResolution.TryGetValue(res, out var list) ? list : new[] { 60 };
        int fps = s.RefreshRate > 0 && rates.Contains(s.RefreshRate) ? s.RefreshRate : rates.Max();

        if (s.Orientation is Orientation.Portrait or Orientation.PortraitFlipped)
            return new VideoMode(res.H, res.W, fps);
        return new VideoMode(res.W, res.H, fps);
    }

    public static string ChooseCodec(NegotiatedCaps neg, AppSettings s, VideoMode mode)
    {
        string? pref = s.Codec switch
        {
            CodecPreference.H264 => "video/avc",
            CodecPreference.Hevc => "video/hevc",
            CodecPreference.Av1 => "video/av01",
            _ => null,
        };
        if (pref != null && neg.Codecs.Contains(pref)) return pref;
        // Auto: H.264 has the lowest encode/decode latency on most hardware. Switch to HEVC only when the
        // pixel rate is high enough that H.264 would need an impractical bitrate over Wi-Fi.
        long pixelRate = (long)mode.Width * mode.Height * mode.Fps;
        if (pixelRate > 2560L * 1440 * 60 && neg.Codecs.Contains("video/hevc")) return "video/hevc";
        return neg.Codecs.Contains("video/avc") ? "video/avc" : neg.Codecs.FirstOrDefault() ?? "video/avc";
    }

    public static bool TryParse(string s, out (int W, int H) res)
    {
        res = default;
        var parts = s.ToLowerInvariant().Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && w > 0 && h > 0)
        {
            res = (w, h);
            return true;
        }
        return false;
    }
}
