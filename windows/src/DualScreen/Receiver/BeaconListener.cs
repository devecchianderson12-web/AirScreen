using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DualScreen.Core;
using DualScreen.Net;

namespace DualScreen.Receiver;

/// <summary>A DualScreen PC seen on the local network (beacon or discovery answer).</summary>
public sealed class SeenHost
{
    public string HostId { get; init; } = "";
    public string Name { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int Port { get; set; }
    public string Version { get; set; } = "";
    public string? SignalingUrl { get; set; }
    /// <summary>Where the last beacon came from: dial requests are answered to this endpoint.</summary>
    public IPEndPoint? BeaconSource { get; set; }
    public IPAddress Address { get; set; } = IPAddress.None;
    public DateTime LastSeenUtc { get; set; }
    public override string ToString() => $"{Name} ({Address})";
}

/// <summary>
/// Receiver side of LAN linking: listens for host beacons on UDP 47802 and also probes hosts that accept
/// incoming connections (DSDISCOVER1). Started only when the user opens "Usar como monitor" or starts a session,
/// so a PC used only as a host never opens a listening socket (no firewall prompt).
/// </summary>
public sealed class BeaconListener : IDisposable
{
    private static readonly byte[] Probe = Encoding.ASCII.GetBytes("DSDISCOVER1");
    private readonly ConcurrentDictionary<string, SeenHost> _hosts = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public event Action<SeenHost>? HostSeen;
    public string? Error { get; private set; }
    public bool IsRunning => _udp != null;

    public IReadOnlyList<SeenHost> Hosts =>
        _hosts.Values.Where(h => DateTime.UtcNow - h.LastSeenUtc < TimeSpan.FromSeconds(10)).OrderBy(h => h.Name).ToList();

    public SeenHost? Find(string hostId) =>
        _hosts.TryGetValue(hostId, out var h) && DateTime.UtcNow - h.LastSeenUtc < TimeSpan.FromSeconds(10) ? h : null;

    public void Start()
    {
        if (_udp != null) return;
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconService.BeaconPort));
            _udp = udp;
            _cts = new CancellationTokenSource();
            Error = null;
            _ = ReceiveLoop(udp, _cts.Token);
            _ = ProbeLoop(udp, _cts.Token);
            Log.Info("Beacon listener on UDP " + BeaconService.BeaconPort);
        }
        catch (Exception ex)
        {
            Error = "No se pudo abrir el puerto UDP " + BeaconService.BeaconPort + ": " + ex.Message;
            Log.Warn(Error);
            _udp?.Dispose();
            _udp = null;
        }
    }

    private async Task ProbeLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await udp.SendAsync(Probe, new IPEndPoint(IPAddress.Broadcast, AppInfo.DiscoveryPort), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
            try { await Task.Delay(3000, ct).ConfigureAwait(false); } catch { break; }
        }
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        string ownId = Core.AppController.Instance?.Identity.HostId ?? "";
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            if (r.Buffer.Length > 4096 || r.Buffer.Length == 0 || r.Buffer[0] != (byte)'{') continue;
            try
            {
                var o = JsonNode.Parse(r.Buffer) as JsonObject;
                string? magic = (string?)o?["magic"];
                if (magic is not (BeaconService.BeaconMagic or "DSHOST1")) continue;
                string id = (string?)o!["hostId"] ?? "";
                if (id.Length != 32 || id == ownId) continue;
                var h = _hosts.GetOrAdd(id, _ => new SeenHost { HostId = id });
                h.Name = Truncate((string?)o["name"] ?? "PC", 64);
                h.Fingerprint = (string?)o["fp"] ?? "";
                h.Version = (string?)o["version"] ?? "";
                h.Address = r.RemoteEndPoint.Address;
                h.LastSeenUtc = DateTime.UtcNow;
                if (magic == BeaconService.BeaconMagic)
                {
                    h.Port = (int?)o["port"] ?? 0;
                    h.SignalingUrl = (string?)o["s"];
                    h.BeaconSource = r.RemoteEndPoint;
                }
                else h.Port = (int?)o["port"] ?? h.Port; // a discovery answer means the host is listening
                HostSeen?.Invoke(h);
            }
            catch { /* not ours */ }
        }
    }

    /// <summary>Asks a host (through its beacon socket) to open a connection to us.</summary>
    public void SendDialRequest(SeenHost host, IEnumerable<IPAddress> addresses, int port)
    {
        var udp = _udp;
        if (udp == null || host.BeaconSource == null) return;
        byte[] msg = JsonSerializer.SerializeToUtf8Bytes(new
        {
            magic = BeaconService.DialMagic,
            hostId = host.HostId,
            addrs = addresses.Select(a => a.ToString()).ToArray(),
            port,
        }, Json.Options);
        try { udp.Send(msg, msg.Length, host.BeaconSource); }
        catch (Exception ex) { Log.Info("Dial request failed: " + ex.Message); }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    public void Dispose()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
    }
}
