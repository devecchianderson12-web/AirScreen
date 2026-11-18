using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DualScreen.Net;
using Xunit;

public class ProtocolTests
{
    [Fact]
    public async Task VideoFrameRoundTrip()
    {
        byte[] data = { 0, 0, 0, 1, 0x65, 1, 2, 3 };
        var ms = new MemoryStream(FrameCodec.EncodeVideo(VideoFlags.Keyframe, 123456789, data));
        var f = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        Assert.Equal(MsgType.Video, f.Type);
        Assert.Equal((byte)VideoFlags.Keyframe, f.Payload[0]);
        Assert.Equal(123456789, System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(f.Payload.Slice(1)));
        Assert.Equal(data, f.Payload.Slice(9).ToArray());
        f.Return();
    }

    [Fact]
    public async Task JsonFrameRoundTrip()
    {
        var ms = new MemoryStream(FrameCodec.EncodeJson(MsgType.StreamConfig, new StreamConfigMsg { Mime = "video/hevc", Width = 2400, Height = 1080, Fps = 120 }));
        var f = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        var cfg = f.Json<StreamConfigMsg>()!;
        Assert.Equal(2400, cfg.Width);
        Assert.Equal(120, cfg.Fps);
        Assert.Contains("\"mime\":\"video/hevc\"", System.Text.Encoding.UTF8.GetString(f.Payload));
    }

    [Fact]
    public async Task RejectsOversizedFrame()
    {
        var ms = new MemoryStream(new byte[] { 4, 0x7F, 0xFF, 0xFF, 0xFF });
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.ReadAsync(ms, CancellationToken.None));
    }

    [Fact]
    public void PairingProofMatchesReferenceVector()
    {
        // Same vector is used by the Android implementation (Protocol.pairingProof).
        string proof = PairingService.ComputeProof("123456", new string('a', 64), "device-1", "00112233445566778899aabbccddeeff");
        using var h = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes("123456"));
        string expected = System.Convert.ToHexString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
            "DSPAIR1|" + new string('a', 64) + "|device-1|00112233445566778899aabbccddeeff"))).ToLowerInvariant();
        Assert.Equal(expected, proof);
        Assert.Equal(64, proof.Length);
    }
}
