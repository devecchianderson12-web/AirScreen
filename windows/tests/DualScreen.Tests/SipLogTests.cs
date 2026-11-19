using System.Threading.Tasks;
using DualScreen.Remote;
using SIPSorcery.Net;
using Xunit;

public class SipLogTests
{
    [Fact]
    public async Task BridgeDoesNotBreakPeerConnection()
    {
        SipLogBridge.Install();
        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = new() { new RTCIceServer { urls = "stun:stun.l.google.com:19302" } } });
        var dc = await pc.createDataChannel("ds", new RTCDataChannelInit { ordered = true });
        var offer = pc.createOffer(null);
        await pc.setLocalDescription(offer);
        Assert.Contains("m=application", offer.sdp);
        pc.close();
    }
}
