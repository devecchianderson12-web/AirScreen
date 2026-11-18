using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DualScreen.Remote;
using Xunit;

/// <summary>Runs against a broker given in DS_TEST_MQTT_URL (e.g. mqtt+ws://127.0.0.1:18083/); skipped otherwise.</summary>
public class MqttTests
{
    [Fact]
    public async Task PublishSubscribeRoundTrip()
    {
        string? url = Environment.GetEnvironmentVariable("DS_TEST_MQTT_URL");
        if (string.IsNullOrEmpty(url)) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var a = new MqttWsClient();
        await using var b = new MqttWsClient();
        var got = new TaskCompletionSource<(string, string)>();
        b.Message += (t, p) => got.TrySetResult((t, Encoding.UTF8.GetString(p)));
        await a.ConnectAsync(url, "test-a-" + Guid.NewGuid().ToString("N")[..8], cts.Token);
        await b.ConnectAsync(url, "test-b-" + Guid.NewGuid().ToString("N")[..8], cts.Token);
        await b.SubscribeAsync("dualscreen/v1/h/test");
        await Task.Delay(300);
        string big = new string('x', 5000); // SDP-sized payload (multi-byte remaining length)
        await a.PublishAsync("dualscreen/v1/h/test", Encoding.UTF8.GetBytes("{\"t\":\"join\",\"pad\":\"" + big + "\"}"));
        var (topic, payload) = await got.Task.WaitAsync(cts.Token);
        Assert.Equal("dualscreen/v1/h/test", topic);
        Assert.Contains(big, payload);
    }
}
