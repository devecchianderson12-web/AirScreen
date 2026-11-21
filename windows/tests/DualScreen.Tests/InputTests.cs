using System.IO;
using DualScreen.Net;
using Xunit;

public class InputTests
{
    private static Frame Read(byte[] bytes) => FrameCodec.Read(new MemoryStream(bytes));

    [Fact]
    public void TouchEventLayout()
    {
        var f = Read(InputCodec.Touch(3, ContactState.Move, 0x1234, 0xFFFF, 512));
        Assert.Equal(MsgType.Input, f.Type);
        Assert.Equal(new byte[] { 1, 3, 1, 0x12, 0x34, 0xFF, 0xFF, 0x02, 0x00 }, f.Payload.ToArray());
        f.Return();
    }

    [Fact]
    public void KeyAndWheelLayout()
    {
        var k = Read(InputCodec.Key(0x1D, up: true, extended: true));
        Assert.Equal(new byte[] { 6, 0x00, 0x1D, 3 }, k.Payload.ToArray());
        var w = Read(InputCodec.Wheel(false, -120));
        Assert.Equal(new byte[] { 5, 0, 0xFF, 0x88 }, w.Payload.ToArray());
        k.Return(); w.Return();
    }

    [Fact]
    public void ReleaseAllIsOneByte()
    {
        var f = Read(InputCodec.ReleaseAll());
        Assert.Equal(new byte[] { 7 }, f.Payload.ToArray());
        f.Return();
    }
}
