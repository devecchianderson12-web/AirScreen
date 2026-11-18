using DualScreen.Core;
using DualScreen.Net;
using Xunit;

public class BitrateTests
{
    [Fact]
    public void BacksOffOnQueueDelayAndRecovers()
    {
        var c = new BitrateController(1920, 1080, 60, QualityPreset.Auto, 0);
        int start = c.TargetKbps;
        c.Update(queueDelayMs: 80, queuedFrames: 4, rttMs: 10, droppedRecently: 0);
        Assert.True(c.Congested);
        Assert.True(c.TargetKbps < start);
        int low = c.TargetKbps;
        System.Threading.Thread.Sleep(1100);
        c.Update(0, 0, 10, 0);
        Assert.False(c.Congested);
        Assert.True(c.TargetKbps > low);
    }

    [Fact]
    public void LowersFrameRateWhenFloorIsNotSustainable()
    {
        var c = new BitrateController(2400, 1080, 120, QualityPreset.High, 0);
        for (int i = 0; i < 40; i++) c.Update(200, 6, 300, 1);
        System.Threading.Thread.Sleep(3100);
        c.Update(200, 6, 300, 1);
        Assert.True(c.TargetFps <= 60);
    }

    [Fact]
    public void RespectsUserMaximum()
    {
        var c = new BitrateController(3840, 2160, 120, QualityPreset.Maximum, 20);
        for (int i = 0; i < 5; i++) c.Update(0, 0, 5, 0);
        Assert.True(c.TargetKbps <= 20_000);
    }
}
