using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DualScreen.Core;
using DualScreen.Net;
using SIPSorcery.Net;

namespace DualScreen.Remote;

/// <summary>
/// Remote connectivity (phone on mobile data, PC at home, etc.).
/// 1. The PC keeps an authenticated WebSocket to the DualScreen signaling server (proves ownership of its HostId
///    by signing a server challenge with its identity key).
/// 2. A phone joins by HostId (already paired) or by the hash of the current pairing code.
/// 3. The PC creates a WebRTC offer with one ordered data channel and signs the SDP (which contains the DTLS
///    fingerprint) with its identity key, so the signaling server cannot man-in-the-middle the session.
/// 4. ICE tries host/srflx (direct P2P, STUN hole punching) first and falls back to TURN relay automatically.
/// </summary>
public sealed class RemoteHost : IDisposable
{
    private readonly HostIdentity _identity;
    private readonly PairingService _pairing;
    private readonly Func<System.IO.Stream, string, string, Task> _onStream;
    private readonly ConcurrentDictionary<string, RTCPeerConnection> _peers = new();
    private CancellationTokenSource? _cts;
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private List<RTCIceServer> _iceServers = new();

    public string Status { get; private set; } = "Desactivado";
    public bool Connected { get; private set; }
    public event Action? StatusChanged;

    public RemoteHost(HostIdentity identity, PairingService pairing, Func<System.IO.Stream, string, string, Task> onStream)
    {
        _identity = identity;
        _pairing = pairing;
        _onStream = onStream;
        _pairing.Changed += () => _ = PublishPairKey();
    }

    public void Start()
    {
        Stop();
        string url = SettingsStore.Current.EffectiveSignalingUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            SetStatus("Sin servidor de señalización configurado", false);
            return;
        }
        _cts = new CancellationTokenSource();
        _ = RunLoop(url, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try { _ws?.Abort(); } catch { }
        _ws = null;
        foreach (var pc in _peers.Values) { try { pc.close(); } catch { } }
        _peers.Clear();
        SetStatus("Desactivado", false);
    }

    private void SetStatus(string s, bool connected)
    {
        Status = s;
        Connected = connected;
        StatusChanged?.Invoke();
    }

    private async Task RunLoop(string url, CancellationToken ct)
    {
        int backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetStatus("Conectando al servidor de señalización…", false);
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
                _ws = ws;
                backoff = 1;
                await ReceiveLoop(ws, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warn("Signaling connection failed: " + ex.Message);
            }
            if (ct.IsCancellationRequested) break;
            SetStatus($"Sin conexión con el servidor; reintentando en {backoff} s", false);
            try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct).ConfigureAwait(false); } catch { break; }
            backoff = Math.Min(backoff * 2, 60);
        }
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
            if (!r.EndOfMessage) continue;
            string text = sb.ToString();
            sb.Clear();
            try { await Handle(JsonNode.Parse(text)!.AsObject(), ct).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("Bad signaling message: " + ex.Message); }
        }
    }

    private async Task Handle(JsonObject msg, CancellationToken ct)
    {
        switch ((string?)msg["t"])
        {
            case "challenge":
            {
                string nonce = (string)msg["nonce"]!;
                await Send(new JsonObject
                {
                    ["t"] = "hello-host",
                    ["hostId"] = _identity.HostId,
                    ["cert"] = _identity.CertificateBase64,
                    ["sig"] = _identity.Sign($"DSHOST1|{_identity.HostId}|{nonce}"),
                }).ConfigureAwait(false);
                break;
            }
            case "registered":
                _iceServers = ParseIceServers(msg["iceServers"] as JsonArray);
                SetStatus("Disponible para conexiones remotas", true);
                await PublishPairKey().ConfigureAwait(false);
                break;
            case "peer":
                await StartPeer((string)msg["peerId"]!, ct).ConfigureAwait(false);
                break;
            case "signal":
                HandleSignal((string)msg["from"]!, msg["data"]!.AsObject());
                break;
            case "peer-left":
                if (_peers.TryRemove((string)msg["peerId"]!, out var gone) && !gone.IsDtlsNegotiationComplete) gone.close();
                break;
            case "error":
                Log.Warn("Signaling server error: " + (string?)msg["error"]);
                SetStatus("Servidor: " + (string?)msg["error"], false);
                break;
        }
    }

    private List<RTCIceServer> ParseIceServers(JsonArray? arr)
    {
        var list = new List<RTCIceServer>();
        var s = SettingsStore.Current;
        foreach (var stun in s.StunUrls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            list.Add(new RTCIceServer { urls = stun });
        if (!string.IsNullOrWhiteSpace(s.TurnUrl))
            list.Add(new RTCIceServer { urls = s.TurnUrl, username = s.TurnUser, credential = s.GetTurnPassword() ?? "", credentialType = RTCIceCredentialType.password });
        if (arr != null)
        {
            foreach (var n in arr.OfType<JsonObject>())
            {
                list.Add(new RTCIceServer
                {
                    urls = (string?)n["urls"] ?? "",
                    username = (string?)n["username"],
                    credential = (string?)n["credential"],
                    credentialType = RTCIceCredentialType.password,
                });
            }
        }
        return list;
    }

    private async Task PublishPairKey()
    {
        if (_ws?.State != WebSocketState.Open || !Connected) return;
        try { await Send(new JsonObject { ["t"] = "set-pair-key", ["key"] = _pairing.CurrentCodeRendezvousKey }).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn("Publishing pair key failed: " + ex.Message); }
    }

    private async Task StartPeer(string peerId, CancellationToken ct)
    {
        if (_peers.Count > 4) return; // basic flood protection; a real session is authenticated inside the channel
        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = _iceServers, X_UseRsaForDtlsCertificate = false });
        _peers[peerId] = pc;

        pc.onicecandidate += cand =>
        {
            if (cand == null) return;
            _ = Send(new JsonObject
            {
                ["t"] = "signal", ["to"] = peerId,
                ["data"] = new JsonObject { ["kind"] = "ice", ["candidate"] = cand.candidate, ["sdpMid"] = cand.sdpMid, ["sdpMLineIndex"] = cand.sdpMLineIndex },
            });
        };
        pc.onconnectionstatechange += st =>
        {
            Log.Info($"Remote peer {peerId}: {st}");
            if (st is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed) _peers.TryRemove(peerId, out _);
        };

        var dc = await pc.createDataChannel("ds", new RTCDataChannelInit { ordered = true }).ConfigureAwait(false);
        dc.onopen += () =>
        {
            Log.Info($"Remote data channel open with {peerId}");
            var stream = new DataChannelStream(pc, dc);
            _ = _onStream(stream, "Remota (WebRTC)", "peer " + peerId);
        };

        var offer = pc.createOffer(null);
        await pc.setLocalDescription(offer).ConfigureAwait(false);
        await Send(new JsonObject
        {
            ["t"] = "signal", ["to"] = peerId,
            ["data"] = new JsonObject
            {
                ["kind"] = "offer",
                ["sdp"] = offer.sdp,
                ["cert"] = _identity.CertificateBase64,
                ["sig"] = _identity.Sign("DSOFFER1|" + offer.sdp),
            },
        }).ConfigureAwait(false);

        // Abandon half-open negotiations.
        _ = Task.Delay(TimeSpan.FromSeconds(30), ct).ContinueWith(_ =>
        {
            if (pc.connectionState != RTCPeerConnectionState.connected && _peers.TryRemove(peerId, out var p)) p.close();
        }, TaskScheduler.Default);
    }

    private void HandleSignal(string from, JsonObject data)
    {
        if (!_peers.TryGetValue(from, out var pc)) return;
        switch ((string?)data["kind"])
        {
            case "answer":
                var res = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = (string)data["sdp"]! });
                if (res != SetDescriptionResultEnum.OK) Log.Warn($"Remote answer rejected: {res}");
                break;
            case "ice":
                pc.addIceCandidate(new RTCIceCandidateInit
                {
                    candidate = (string)data["candidate"]!,
                    sdpMid = (string?)data["sdpMid"] ?? "0",
                    sdpMLineIndex = (ushort)((int?)data["sdpMLineIndex"] ?? 0),
                });
                break;
        }
    }

    private async Task Send(JsonObject msg)
    {
        var ws = _ws;
        if (ws?.State != WebSocketState.Open) return;
        byte[] bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    public void Dispose() => Stop();
}
