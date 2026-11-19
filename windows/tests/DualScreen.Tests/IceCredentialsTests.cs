using System.Text.Json.Nodes;
using DualScreen.Remote;
using Xunit;

public class IceCredentialsTests
{
    [Fact]
    public void NormalizesCloudflareArrayShape()
    {
        var node = JsonNode.Parse("""[{"urls":["stun:stun.cloudflare.com:3478"]},{"urls":["turn:turn.cloudflare.com:3478?transport=udp","turns:turn.cloudflare.com:443?transport=tcp"],"username":"u","credential":"c"}]""");
        var a = IceCredentials.Normalize(node);
        Assert.Equal(2, a.Count); // turns: is skipped
        Assert.Equal("stun:stun.cloudflare.com:3478", (string?)a[0]!["urls"]);
        Assert.Equal("turn:turn.cloudflare.com:3478?transport=udp", (string?)a[1]!["urls"]);
        Assert.Equal("u", (string?)a[1]!["username"]);
        Assert.Equal("c", (string?)a[1]!["credential"]);
    }

    [Fact]
    public void NormalizesLegacySingleObjectShape()
    {
        var node = JsonNode.Parse("""{"urls":"turn:turn.cloudflare.com:3478","username":"u","credential":"c"}""");
        var a = IceCredentials.Normalize(node);
        Assert.Single(a);
        Assert.Equal("u", (string?)a[0]!["username"]);
    }
}
