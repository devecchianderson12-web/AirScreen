using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DualScreen.Core;

namespace DualScreen.Net;

/// <summary>
/// Answers LAN discovery probes on UDP 47801. A phone broadcasts "DSDISCOVER1"; we unicast back a JSON
/// description including the certificate fingerprint. The fingerprint learned this way is NOT trusted by itself:
/// pairing still requires the 6-digit code proof, and QR pairing pins it out-of-band.
/// </summary>
public sealed class DiscoveryResponder : IDisposable
{
    private static readonly byte[] Probe = Encoding.ASCII.GetBytes("DSDISCOVER1");
    private readonly HostIdentity _identity;
    private readonly Func<int> _port;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public DiscoveryResponder(HostIdentity identity, Func<int> port)
    {
        _identity = identity;
        _port = port;
    }

    public void Start()
    {
        Stop();
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, AppInfo.DiscoveryPort));
            _cts = new CancellationTokenSource();
            _ = Loop(_udp, _cts.Token);
            Log.Info($"Discovery responder on UDP {AppInfo.DiscoveryPort}");
        }
        catch (Exception ex)
        {
            Log.Warn("Discovery responder unavailable: " + ex.Message);
            _udp?.Dispose();
            _udp = null;
        }
    }

    private async Task Loop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            if (!r.Buffer.AsSpan().StartsWith(Probe)) continue;
            var reply = JsonSerializer.SerializeToUtf8Bytes(new
            {
                magic = "DSHOST1",
                hostId = _identity.HostId,
                name = SettingsStore.Current.HostName,
                port = _port(),
                fp = _identity.Fingerprint,
                version = AppInfo.VersionString,
                proto = AppInfo.ProtocolVersion,
            }, Json.Options);
            try { await udp.SendAsync(reply, r.RemoteEndPoint, ct).ConfigureAwait(false); } catch { }
        }
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
