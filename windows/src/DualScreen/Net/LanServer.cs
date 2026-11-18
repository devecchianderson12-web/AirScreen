using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using DualScreen.Core;

namespace DualScreen.Net;

/// <summary>
/// LAN transport: TLS (1.2/1.3, ECDSA host certificate) over TCP with Nagle disabled.
/// Each accepted stream is handed to <see cref="HandshakeHandler"/> which authenticates it.
/// </summary>
public sealed class LanServer : IDisposable
{
    private readonly HostIdentity _identity;
    private readonly Func<System.IO.Stream, string, string, Task> _onStream;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }
    public bool IsRunning => _listener != null;

    public LanServer(HostIdentity identity, Func<System.IO.Stream, string, string, Task> onStream)
    {
        _identity = identity;
        _onStream = onStream;
    }

    public void Start(int port)
    {
        Stop();
        _cts = new CancellationTokenSource();
        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
            _listener.Start(8);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            Log.Warn($"Port {port} busy, using a random port");
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start(8);
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Log.Info($"LAN server listening on TCP {Port}");
        _ = AcceptLoop(_listener, _cts.Token);
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Log.Warn("Accept failed: " + ex.Message); await Task.Delay(200, ct).ConfigureAwait(false); continue; }
            _ = HandleClient(client, ct);
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        try
        {
            client.NoDelay = true;
            client.SendBufferSize = 4 * 1024 * 1024;
            client.ReceiveBufferSize = 256 * 1024;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            using var hsTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hsTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _identity.Certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.None, // OS default: TLS 1.3 on Windows 11, 1.2 on Windows 10
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
            }, hsTimeout.Token).ConfigureAwait(false);
            await _onStream(ssl, "LAN (TLS/TCP)", remote).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"LAN client {remote} rejected: {ex.Message}");
            client.Dispose();
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();
}
