using System.Collections.Generic;
using System.Linq;
using DualScreen.Core;
using DualScreen.Net;
using Xunit;

public class NegotiatorTests
{
    private static DeviceCaps Phone120Hz() => new()
    {
        ScreenWidth = 1080, ScreenHeight = 2400, RefreshRates = new() { 60f, 90f, 120f },
        Codecs = new()
        {
            new CodecCaps { Mime = "video/avc", Hardware = true, MaxWidth = 4096, MaxHeight = 2304,
                Modes = new() { new ModeCaps { Width = 2400, Height = 1080, MaxFps = 120 }, new ModeCaps { Width = 1920, Height = 1080, MaxFps = 240 },
                                new ModeCaps { Width = 3840, Height = 2160, MaxFps = 30 } } },
            new CodecCaps { Mime = "video/hevc", Hardware = true, MaxWidth = 4096, MaxHeight = 2304,
                Modes = new() { new ModeCaps { Width = 2400, Height = 1080, MaxFps = 120 } } },
        },
    };

    [Fact]
    public void OffersOnlyRatesThePanelRuns()
    {
        var n = Negotiator.Compute(Phone120Hz(), new List<string> { "video/avc" });
        var rates = n.FpsByResolution[(2400, 1080)];
        Assert.Contains(120, rates);
        Assert.Contains(90, rates);
        Assert.DoesNotContain(144, rates); // panel cannot do 144 Hz: never promised
        Assert.DoesNotContain(240, n.FpsByResolution[(1920, 1080)]);
    }

    [Fact]
    public void DropsModesTheDecoderCannotSustain()
    {
        var n = Negotiator.Compute(Phone120Hz(), new List<string> { "video/avc" });
        Assert.Equal(new[] { 30 }, n.FpsByResolution[(3840, 2160)].ToArray());
    }

    [Fact]
    public void AutoPicksNativeResolutionAndHighestRate()
    {
        var caps = Phone120Hz();
        var n = Negotiator.Compute(caps, new List<string> { "video/avc", "video/hevc" });
        var mode = Negotiator.Choose(n, caps, new AppSettings());
        Assert.Equal(new VideoMode(2400, 1080, 120), mode);
        Assert.Equal("video/hevc", Negotiator.ChooseCodec(n, new AppSettings(), mode)); // high pixel rate -> HEVC
    }

    [Fact]
    public void PortraitSwapsDimensions()
    {
        var caps = Phone120Hz();
        var n = Negotiator.Compute(caps, new List<string> { "video/avc" });
        var mode = Negotiator.Choose(n, caps, new AppSettings { Orientation = Orientation.Portrait, RefreshRate = 60 });
        Assert.Equal(new VideoMode(1080, 2400, 60), mode);
    }

    [Fact]
    public void FallsBackToSoftwareH264WhenNoHardwareEncoderMatches()
    {
        var n = Negotiator.Compute(Phone120Hz(), new List<string>());
        Assert.Equal(new[] { "video/avc" }, n.Codecs.ToArray());
    }
}
