using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DualScreen.Core;
using DualScreen.Net;
using DualScreen.Remote;

namespace DualScreen.Receiver;

public sealed record ReceiverLink(Stream Stream, string Fingerprint, string Transport, IPAddress? HostAddress);

/// <summary>
/// Opens an authenticated-host TLS stream to a DualScreen PC, fastest path first:
/// 1. direct TCP when the host accepts incoming connections;
/// 2. reverse connection: this PC listens on a random port and asks the host to dial it, through the host's LAN
///    beacon (no Internet needed) and, in parallel, through the MQTT signaling broker (works across subnets).
/// The host certificate is checked against the pinned fingerprint and the host id (hash of its public key).
/// </summary>
public static class ReceiverConnector
{
    public static async Task<ReceiverLink> ConnectAsync(string hostId, string? pinnedFp, SavedHost? saved, BeaconListener beacons,
                                                        Action<string> progress, CancellationToken ct)
    {
        var errors = new List<string>();

        // Wait briefly for a beacon: it gives the current IP and whether the host listens.
        var seen = beacons.Find(hostId);
        if (seen == null)
        {
            progress("Buscando el equipo en la red…");
            seen = await WaitForBeacon(beacons, hostId, TimeSpan.FromSeconds(2.5), ct).ConfigureAwait(false);
        }

        // 1. Direct.
        var direct = new List<IPEndPoint>();
        if (seen is { Port: > 0 }) direct.Add(new IPEndPoint(seen.Address, seen.Port));
        if (saved is { Port: > 0 })
            foreach (var a in saved.Addresses)
                if (IPAddress.TryParse(a, out var ip)) direct.Add(new IPEndPoint(ip, saved.Port));
        foreach (var ep in direct.Distinct())
        {
            progress($"Conectando a {ep.Address}…");
            var tcp = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(1.5));
                await tcp.ConnectAsync(ep.Address, ep.Port, cts.Token).ConfigureAwait(false);
                var (ssl, fp) = await Tls(tcp, hostId, pinnedFp, ct).ConfigureAwait(false);
                return new ReceiverLink(ssl, fp, "LAN directa (TLS/TCP)", ep.Address);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { tcp.Dispose(); throw; }
            catch (Exception ex) { tcp.Dispose(); errors.Add($"{ep.Address}: {Explain(ex)}"); }
        }

        // 2. Reverse.
        progress("Pidiendo al equipo que se conecte…");
        var listener = new TcpListener(IPAddress.Any, 0);
        try
        {
            listener.Start(4);
        }
        catch (Exception ex)
        {
            throw new IOException("No se pudo abrir un puerto local para recibir la conexión: " + ex.Message);
        }
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var local = PairingService.LocalAddresses().ToList();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            string? signaling = seen?.SignalingUrl ?? saved?.SignalingUrl;
            if (signaling != null && MqttWsClient.IsMqttUrl(signaling))
                _ = MqttDial(signaling, hostId, local, port, stop.Token);
            _ = BeaconDialLoop(beacons, hostId, local, port, stop.Token);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    errors.Add(beacons.Find(hostId) == null
                        ? "el equipo no aparece en esta red (¿DualScreen está abierto en él y en la misma Wi‑Fi?)"
                        : "el equipo no se conectó (¿el firewall de este equipo bloquea DualScreen?)");
                    throw new IOException("No se pudo conectar." + string.Concat(errors.Select(e => "\n• " + e)));
                }
                try
                {
                    var (ssl, fp) = await Tls(client, hostId, pinnedFp, ct).ConfigureAwait(false);
                    stop.Cancel();
                    var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                    return new ReceiverLink(ssl, fp, "LAN (conexión inversa TLS/TCP)", remote);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Info("Rejected incoming connection: " + ex.Message);
                    client.Dispose(); // not the host we want: keep waiting
                }
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task<SeenHost?> WaitForBeacon(BeaconListener beacons, string hostId, TimeSpan max, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<SeenHost>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(SeenHost h) { if (h.HostId == hostId) tcs.TrySetResult(h); }
        beacons.HostSeen += On;
        try
        {
            var existing = beacons.Find(hostId);
            if (existing != null) return existing;
            var done = await Task.WhenAny(tcs.Task, Task.Delay(max, ct)).ConfigureAwait(false);
            return done == tcs.Task ? tcs.Task.Result : null;
        }
        finally { beacons.HostSeen -= On; }
    }

    private static async Task BeaconDialLoop(BeaconListener beacons, string hostId, List<IPAddress> local, int port, CancellationToken ct)
    {
        // The host only accepts a reply within a few seconds of its own broadcast, so answer each fresh beacon.
        DateTime lastSent = DateTime.MinValue;
        void On(SeenHost h)
        {
            if (h.HostId != hostId || DateTime.UtcNow - lastSent < TimeSpan.FromMilliseconds(900)) return;
            lastSent = DateTime.UtcNow;
            beacons.SendDialRequest(h, local, port);
        }
        beacons.HostSeen += On;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var h = beacons.Find(hostId);
                if (h != null && DateTime.UtcNow - h.LastSeenUtc < TimeSpan.FromSeconds(2)) On(h);
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally { beacons.HostSeen -= On; }
    }

    private static async Task MqttDial(string url, string hostId, List<IPAddress> local, int port, CancellationToken ct)
    {
        try
        {
            string peer = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            await using var mqtt = new MqttWsClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            await mqtt.ConnectAsync(url, "ds-d-" + peer, cts.Token).ConfigureAwait(false);
            byte[] msg = JsonSerializer.SerializeToUtf8Bytes(new
            {
                t = "signal",
                from = peer,
                data = new { kind = "dial", addrs = local.Select(a => a.ToString()).ToArray(), port },
            }, Json.Options);
            await mqtt.PublishAsync(RemoteHost.MqttPrefix + "h/" + hostId, msg).ConfigureAwait(false);
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Info("MQTT dial request failed: " + ex.Message); }
        catch (OperationCanceledException) { }
    }

    private static async Task<(SslStream, string)> Tls(TcpClient tcp, string hostId, string? pinnedFp, CancellationToken ct)
    {
        tcp.NoDelay = true;
        tcp.ReceiveBufferSize = 4 * 1024 * 1024;
        tcp.SendBufferSize = 256 * 1024;
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        string fp = "";
        string? reason = null;
        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "dualscreen-host",
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert == null) { reason = "sin certificado"; return false; }
                    byte[] der = cert.GetRawCertData();
                    fp = SecretStore.Sha256Hex(der);
                    if (pinnedFp != null && !string.Equals(fp, pinnedFp, StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "el certificado del equipo cambió (volvé a vincularlo)";
                        return false;
                    }
                    using var x = new X509Certificate2(der);
                    string id = Convert.ToHexString(SHA256.HashData(x.PublicKey.ExportSubjectPublicKeyInfo()), 0, 16).ToLowerInvariant();
                    if (id != hostId) { reason = "es otro equipo"; return false; }
                    return true;
                },
            }, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new IOException(reason ?? ex.Message, ex);
        }
        return (ssl, fp);
    }

    private static string Explain(Exception e) => e switch
    {
        OperationCanceledException => "sin respuesta",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "conexión rechazada",
        _ => e.Message,
    };

    // ------------------------------------------------------------------ handshake

    public static async Task<WelcomeMsg> HandshakeAsync(Stream s, HelloMsg hello, CancellationToken ct)
    {
        await s.WriteAsync(FrameCodec.EncodeJson(MsgType.Hello, hello), ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var f = await FrameCodec.ReadAsync(s, cts.Token).ConfigureAwait(false);
        try
        {
            if (f.Type != MsgType.Welcome) throw new IOException("Respuesta inesperada del equipo");
            return f.Json<WelcomeMsg>() ?? throw new IOException("Respuesta vacía del equipo");
        }
        finally { f.Return(); }
    }

    public static string WelcomeError(string? code) => code switch
    {
        "bad-code" => "Código incorrecto.",
        "code-expired" => "El código caducó. Generá uno nuevo en el otro equipo (Conexión).",
        "too-many-attempts" => "Demasiados intentos. Generá un código nuevo en el otro equipo.",
        "not-paired" => "El otro equipo revocó el acceso. Volvé a vincularlo con un código nuevo.",
        "busy" => "El otro equipo ya está transmitiendo a otro dispositivo.",
        "protocol-mismatch" => "Versiones incompatibles: actualizá DualScreen en ambos equipos.",
        "code-required" => "Ese equipo pide el código de vinculación: miralo en su sección Conexión y escribilo en «Vincular con código».",
        _ => "Conexión rechazada.",
    };

    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
