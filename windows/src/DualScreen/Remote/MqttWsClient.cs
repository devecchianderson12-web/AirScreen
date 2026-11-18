using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DualScreen.Remote;

/// <summary>
/// Minimal MQTT 3.1.1 client over WebSocket (QoS 0 only), used as a zero-setup signaling channel through a
/// public broker. Only signaling travels through it: WebRTC offers are signed by the PC and the video goes
/// peer-to-peer over DTLS, so the broker cannot read or alter the session.
/// </summary>
public sealed class MqttWsClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private ushort _packetId;
    private Task? _reader, _pinger;

    public event Action<string, byte[]>? Message;

    /// <summary>"mqtt+wss://host:port/path" → "wss://host:port/path".</summary>
    public static bool IsMqttUrl(string url) => url.StartsWith("mqtt+", StringComparison.OrdinalIgnoreCase);
    public static Uri ToWebSocketUri(string url) => new(IsMqttUrl(url) ? url[5..] : url);

    public async Task ConnectAsync(string url, string clientId, CancellationToken ct)
    {
        _ws.Options.AddSubProtocol("mqtt");
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _ws.ConnectAsync(ToWebSocketUri(url), ct).ConfigureAwait(false);

        var body = new List<byte>();
        AddString(body, "MQTT");
        body.Add(4);              // protocol level 3.1.1
        body.Add(0x02);           // clean session
        body.Add(0); body.Add(60); // keep alive 60 s
        AddString(body, clientId);
        await SendPacketAsync(0x10, body).ConfigureAwait(false);

        var (type, payload) = await ReadPacketAsync(ct).ConfigureAwait(false);
        if (type >> 4 != 2 || payload.Length < 2 || payload[1] != 0)
            throw new IOException($"MQTT broker refused the connection (code {(payload.Length > 1 ? payload[1] : -1)})");

        _reader = Task.Run(() => ReadLoop(_cts.Token));
        _pinger = Task.Run(() => PingLoop(_cts.Token));
    }

    public Task Completion => _reader ?? Task.CompletedTask;

    public Task SubscribeAsync(string topic)
    {
        var body = new List<byte>();
        AddId(body);
        AddString(body, topic);
        body.Add(0); // QoS 0
        return SendPacketAsync(0x82, body);
    }

    public Task UnsubscribeAsync(string topic)
    {
        var body = new List<byte>();
        AddId(body);
        AddString(body, topic);
        return SendPacketAsync(0xA2, body);
    }

    public Task PublishAsync(string topic, byte[] payload)
    {
        var body = new List<byte>();
        AddString(body, topic);
        body.AddRange(payload);
        return SendPacketAsync(0x30, body);
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (type, p) = await ReadPacketAsync(ct).ConfigureAwait(false);
            if (type >> 4 != 3) continue; // only PUBLISH matters (SUBACK, PINGRESP… are ignored)
            int topicLen = (p[0] << 8) | p[1];
            string topic = Encoding.UTF8.GetString(p, 2, topicLen);
            int offset = 2 + topicLen + (((type >> 1) & 3) > 0 ? 2 : 0); // skip packet id if QoS > 0
            var data = new byte[p.Length - offset];
            Buffer.BlockCopy(p, offset, data, 0, data.Length);
            try { Message?.Invoke(topic, data); } catch { }
        }
    }

    private async Task PingLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                await SendPacketAsync(0xC0, new List<byte>()).ConfigureAwait(false);
            }
        }
        catch { }
    }

    // ---------------------------------------------------------------- framing

    private readonly MemoryStream _rx = new();

    private async Task<(byte type, byte[] payload)> ReadPacketAsync(CancellationToken ct)
    {
        var buf = new byte[8192];
        while (true)
        {
            if (TryParse(out var pkt)) return pkt;
            var r = await _ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) throw new EndOfStreamException("MQTT broker closed the connection");
            _rx.Seek(0, SeekOrigin.End);
            _rx.Write(buf, 0, r.Count);
        }
    }

    private bool TryParse(out (byte, byte[]) packet)
    {
        packet = default;
        byte[] data = _rx.ToArray();
        if (data.Length < 2) return false;
        int len = 0, mul = 1, i = 1;
        while (true)
        {
            if (i >= data.Length) return false;
            byte b = data[i++];
            len += (b & 0x7F) * mul;
            if ((b & 0x80) == 0) break;
            mul *= 128;
            if (mul > 128 * 128 * 128) throw new InvalidDataException("Bad MQTT length");
        }
        if (data.Length < i + len) return false;
        var payload = new byte[len];
        Buffer.BlockCopy(data, i, payload, 0, len);
        packet = (data[0], payload);
        _rx.SetLength(0);
        _rx.Write(data, i + len, data.Length - i - len);
        return true;
    }

    private async Task SendPacketAsync(byte header, List<byte> body)
    {
        var pkt = new List<byte>(body.Count + 5) { header };
        int len = body.Count;
        do
        {
            byte b = (byte)(len % 128);
            len /= 128;
            if (len > 0) b |= 0x80;
            pkt.Add(b);
        } while (len > 0);
        pkt.AddRange(body);
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try { await _ws.SendAsync(pkt.ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    private void AddId(List<byte> body)
    {
        _packetId = (ushort)(_packetId % 65535 + 1);
        body.Add((byte)(_packetId >> 8));
        body.Add((byte)_packetId);
    }

    private static void AddString(List<byte> body, string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        body.Add((byte)(b.Length >> 8));
        body.Add((byte)b.Length);
        body.AddRange(b);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                await SendPacketAsync(0xE0, new List<byte>()).ConfigureAwait(false); // DISCONNECT
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch { }
        _ws.Dispose();
    }
}
