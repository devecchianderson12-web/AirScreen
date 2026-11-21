using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DualScreen.Core;
using DualScreen.Media;
using DualScreen.Net;

namespace DualScreen.Receiver;

public enum ReceiverState { Idle, Connecting, Streaming, Error }

/// <summary>
/// "Use this PC as a monitor": links to another DualScreen PC with its 6-digit code once, remembers it, and then
/// starts the full-screen viewer with one click. One session at a time.
/// </summary>
public sealed class ReceiverController : IDisposable
{
    public static ReceiverController Instance { get; } = new();

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private ReceiverSession? _session;
    private ViewerWindow? _viewer;
    private D3DDevice? _d3d;
    private IntPtr _lastMonitor;
    private int _reconnecting;
    private volatile bool _reconnectCancelled;

    public BeaconListener Beacons { get; } = new();
    public ReceiverState State { get; private set; } = ReceiverState.Idle;
    public string StatusText { get; private set; } = "Sin conectar";
    public string? LastError { get; private set; }
    public string? ActiveHostId { get; private set; }
    public ReceiverSession? Session => _session;
    public bool Busy => State is ReceiverState.Connecting or ReceiverState.Streaming;

    public event Action? Changed;
    /// <summary>The viewer closed (user exit, host stop or error). Argument: error to show, or null.</summary>
    public event Action<string?>? Ended;

    private ReceiverController() { }

    private void Set(ReceiverState st, string text, string? error = null)
    {
        State = st;
        StatusText = text;
        LastError = error;
        Changed?.Invoke();
    }

    /// <summary>First time: links with the code shown on the other PC, saves it and starts showing it.</summary>
    public Task<bool> PairAndStartAsync(SeenHost host, string code, bool noKeyboard, IntPtr monitor) =>
        RunAsync(host.HostId, host.Name, code, false, noKeyboard, monitor);

    /// <summary>One click: reconnects to a saved PC.</summary>
    public Task<bool> StartAsync(SavedHost host, IntPtr monitor) =>
        RunAsync(host.HostId, host.Name, null, false, host.NoKeyboard, monitor);

    /// <summary>One click on a PC found on the local network: no code (if that PC allows it), saved afterwards.</summary>
    public Task<bool> ConnectAsync(SeenHost host, bool noKeyboard, IntPtr monitor)
    {
        var saved = ReceiverStore.Find(host.HostId);
        return saved != null ? StartAsync(saved, monitor) : RunAsync(host.HostId, host.Name, null, true, noKeyboard, monitor);
    }

    private async Task<bool> RunAsync(string hostId, string name, string? code, bool lan, bool noKeyboard, IntPtr monitor)
    {
        _lastMonitor = monitor;
        CancellationToken ct;
        lock (_gate)
        {
            if (Busy) return false;
            _cts = new CancellationTokenSource();
            ct = _cts.Token;
            ActiveHostId = hostId;
            Set(ReceiverState.Connecting, $"Conectando con {name}…");
        }
        Stream? stream = null;
        try
        {
            Beacons.Start();
            var saved = ReceiverStore.Find(hostId);
            if (code == null && !lan && saved == null) throw new InvalidOperationException("Este equipo no está vinculado.");
            bool useToken = code == null && !lan;
            var link = await ReceiverConnector.ConnectAsync(hostId, useToken ? saved!.Fingerprint : null, saved, Beacons,
                msg => Set(ReceiverState.Connecting, msg), ct).ConfigureAwait(false);
            stream = link.Stream;

            if (monitor == IntPtr.Zero) monitor = MonitorFromPoint(default, 1);
            var hello = new HelloMsg
            {
                Proto = AppInfo.ProtocolVersion,
                DeviceId = ReceiverStore.DeviceId,
                DeviceName = SettingsStore.Current.HostName,
                Model = "PC con Windows",
                AppVersion = AppInfo.VersionString,
                Caps = ReceiverCaps.Build(monitor),
                Quality = "maximum",
                Auth = useToken ? new AuthBlock { Mode = "token", Token = saved!.Token }
                    : code != null ? PairAuth(code, link.Fingerprint)
                    : new AuthBlock { Mode = "lan" },
            };
            Set(ReceiverState.Connecting, "Autenticando…");
            var welcome = await ReceiverConnector.HandshakeAsync(link.Stream, hello, ct).ConfigureAwait(false);
            if (!welcome.Ok) throw new IOException(ReceiverConnector.WelcomeError(welcome.Error));

            var seen = Beacons.Find(hostId);
            var host = saved ?? new SavedHost { HostId = hostId, PairedUtc = DateTime.UtcNow };
            host.Name = string.IsNullOrWhiteSpace(welcome.HostName) ? name : welcome.HostName;
            host.Fingerprint = link.Fingerprint;
            if (!string.IsNullOrEmpty(welcome.DeviceToken)) host.Token = welcome.DeviceToken;
            if (string.IsNullOrEmpty(host.Token)) throw new IOException("El otro equipo no entregó la credencial de vinculación.");
            var addrs = new[] { link.HostAddress?.ToString(), seen?.Address.ToString() }.Concat(host.Addresses)
                .Where(a => !string.IsNullOrEmpty(a) && a != "255.255.255.255").Distinct().Take(4).ToList();
            host.Addresses = addrs!;
            if (seen != null) { host.Port = seen.Port; host.SignalingUrl = seen.SignalingUrl ?? host.SignalingUrl; }
            host.NoKeyboard = noKeyboard;
            host.LastUsedUtc = DateTime.UtcNow;
            ReceiverStore.Upsert(host);

            var viewer = new ViewerWindow(monitor, noKeyboard);
            viewer.Show();
            var d3d = new D3DDevice();
            var session = new ReceiverSession(link.Stream, d3d, viewer, host.Name, link.Transport);
            viewer.ExitRequested += () => Task.Run(() => Stop());
            session.Closed += (reason, byHost) => OnClosed(session, reason, byHost);
            session.Changed += () => Changed?.Invoke();
            lock (_gate)
            {
                _viewer = viewer;
                _d3d = d3d;
                _session = session;
            }
            session.Start();
            Set(ReceiverState.Streaming, $"Mostrando {host.Name}");
            Log.Info($"Receiver: showing {host.Name} via {link.Transport}{(noKeyboard ? " (keyboard disabled)" : "")}");
            return true;
        }
        catch (Exception ex)
        {
            if (stream != null) { try { stream.Dispose(); } catch { } }
            CleanupViewer();
            string msg = ex is OperationCanceledException ? "Cancelado." : ex.Message;
            Log.Warn("Receiver start failed: " + ex.Message);
            Set(ReceiverState.Error, "No se pudo conectar", msg);
            return false;
        }
    }

    private static AuthBlock PairAuth(string code, string fp)
    {
        string nonce = ReceiverConnector.NewNonce();
        return new AuthBlock
        {
            Mode = "pair",
            Nonce = nonce,
            Proof = PairingService.ComputeProof(code, fp, ReceiverStore.DeviceId, nonce),
        };
    }

    private void OnClosed(ReceiverSession s, string reason, bool byHost)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_session, s)) return;
            _session = null;
        }
        CleanupViewer();
        bool lost = !byHost && reason is not ("user" or "closed by user");
        if (lost && ActiveHostId != null && ReceiverStore.Find(ActiveHostId) is { } saved && Interlocked.Exchange(ref _reconnecting, 1) == 0)
        {
            _ = ReconnectAsync(saved);
            return;
        }
        string? error = !lost ? null : "Se perdió la conexión con el otro equipo.";
        Set(error == null ? ReceiverState.Idle : ReceiverState.Error, byHost ? "El otro equipo terminó la transmisión" : "Sin conectar", error);
        Ended?.Invoke(error);
    }

    private async Task ReconnectAsync(SavedHost host)
    {
        try
        {
            _reconnectCancelled = false;
            int[] delays = { 500, 1500, 3000, 5000, 8000 };
            for (int i = 0; i < delays.Length; i++)
            {
                Set(ReceiverState.Connecting, $"Reconectando con {host.Name}… (intento {i + 1} de {delays.Length})");
                await Task.Delay(delays[i]).ConfigureAwait(false);
                if (_reconnectCancelled) return; // the user pressed Stop
                if (State == ReceiverState.Connecting) State = ReceiverState.Idle; // RunAsync refuses to start while busy
                if (await RunAsync(host.HostId, host.Name, null, false, host.NoKeyboard, _lastMonitor).ConfigureAwait(false)) return;
            }
            Set(ReceiverState.Error, "No se pudo reconectar", "Se perdió la conexión con el otro equipo.");
            Ended?.Invoke("Se perdió la conexión con el otro equipo.");
        }
        finally { Interlocked.Exchange(ref _reconnecting, 0); }
    }

    private void CleanupViewer()
    {
        ViewerWindow? v;
        D3DDevice? d;
        lock (_gate)
        {
            v = _viewer; _viewer = null;
            d = _d3d; _d3d = null;
        }
        try { v?.Close(); } catch { }
        try { d?.Dispose(); } catch { }
    }

    /// <summary>Stops connecting or showing.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        if (_reconnecting != 0 && _session == null) { _reconnectCancelled = true; Set(ReceiverState.Idle, "Sin conectar"); return; }
        var s = _session;
        if (s != null) s.Close("user", false);
        else if (State == ReceiverState.Error) Set(ReceiverState.Idle, "Sin conectar");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Input.PointerNative.POINT pt, uint flags);

    public void Dispose()
    {
        Stop();
        Beacons.Dispose();
    }
}
