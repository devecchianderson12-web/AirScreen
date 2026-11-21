using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DualScreen.Core;

namespace DualScreen.Net;

/// <summary>
/// Host side of PC-to-PC linking on the local network, without any inbound firewall rule on the host:
/// the host broadcasts a small UDP beacon every 1.5 s (outbound only) and a receiver answers it with a unicast
/// "dial me" request. Windows Firewall allows unicast replies to a broadcast by default, so the request reaches
/// the beacon socket; the host then dials the receiver (TLS server role and authentication are unchanged).
/// </summary>
public sealed class BeaconService : IDisposable
{
    public const int BeaconPort = 47802;
    public const string BeaconMagic = "DSBEACON1";
    public const string DialMagic = "DSDIAL1";

    private readonly HostIdentity _identity;
    private readonly Func<int> _lanPort;
    private readonly Func<IReadOnlyList<IPAddress>, int, Task> _dial;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private DateTime _lastDial = DateTime.MinValue;

    public BeaconService(HostIdentity identity, Func<int> lanPort, Func<IReadOnlyList<IPAddress>, int, Task> dial)
    {
        _identity = identity;
        _lanPort = lanPort;
        _dial = dial;
    }

    public bool IsRunning => _udp != null;

    public void Start()
    {
        if (_udp != null) return;
        try
        {
            // Never bound explicitly: the first send binds an ephemeral port like any client socket.
            _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            _cts = new CancellationTokenSource();
            _ = Loop(_udp, _cts.Token);
            Log.Info("LAN beacon started");
        }
        catch (Exception ex)
        {
            Log.Warn("LAN beacon unavailable: " + ex.Message);
            Stop();
        }
    }

    private async Task Loop(UdpClient udp, CancellationToken ct)
    {
        bool receiving = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var s = SettingsStore.Current;
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    magic = BeaconMagic,
                    hostId = _identity.HostId,
                    name = s.HostName,
                    fp = _identity.Fingerprint,
                    port = _lanPort(),
                    version = AppInfo.VersionString,
                    proto = AppInfo.ProtocolVersion,
                    s = s.RemoteEnabled ? s.EffectiveSignalingUrl : null,
                }, Json.Options);
                foreach (var target in BroadcastTargets())
                {
                    try { await udp.SendAsync(payload, new IPEndPoint(target, BeaconPort), ct).ConfigureAwait(false); }
                    catch (SocketException) { }
                }
                if (!receiving)
                {
                    receiving = true;
                    _ = ReceiveLoop(udp, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Log.Info("Beacon send failed: " + ex.Message); }
            try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch { break; }
        }
    }

    /// <summary>Limited broadcast plus the directed broadcast of every IPv4 interface (255.255.255.255 leaves through one NIC only).</summary>
    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        var set = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                byte[] a = ua.Address.GetAddressBytes(), m = ua.IPv4Mask.GetAddressBytes();
                if (a[0] == 169 && a[1] == 254) continue;
                var b = new byte[4];
                for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
                set.Add(new IPAddress(b));
            }
        }
        return set;
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            try { HandleDial(r); }
            catch (Exception ex) { Log.Info("Ignoring malformed dial request: " + ex.Message); }
        }
    }

    private void HandleDial(UdpReceiveResult r)
    {
        if (r.Buffer.Length > 2048) return;
        var msg = JsonNode.Parse(r.Buffer) as JsonObject;
        if ((string?)msg?["magic"] != DialMagic || (string?)msg["hostId"] != _identity.HostId) return;
        // At most one dial per second, private addresses and unprivileged ports only: a forged request can at
        // worst make the PC open a TLS-server connection to a LAN address, which reveals nothing.
        if (DateTime.UtcNow - _lastDial < TimeSpan.FromSeconds(1)) return;
        int port = (int?)msg["port"] ?? 0;
        if (port is <= 1024 or >= 65536) return;
        var addrs = new List<IPAddress> { r.RemoteEndPoint.Address };
        foreach (var a in msg["addrs"] as JsonArray ?? new JsonArray())
            if (IPAddress.TryParse((string?)a, out var ip) && !addrs.Contains(ip)) addrs.Add(ip);
        addrs = addrs.Where(IsPrivate).Take(4).ToList();
        if (addrs.Count == 0) return;
        _lastDial = DateTime.UtcNow;
        Log.Info($"Dial request from {r.RemoteEndPoint} → port {port}");
        _ = _dial(addrs, port);
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || (b[0] == 169 && b[1] == 254);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();
}
