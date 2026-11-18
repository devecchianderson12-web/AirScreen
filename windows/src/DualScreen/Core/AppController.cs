using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DualScreen.Display;
using DualScreen.Media;
using DualScreen.Net;
using DualScreen.Remote;

namespace DualScreen.Core;

public enum LinkState { Waiting, Negotiating, Streaming, Error }

/// <summary>
/// Central orchestrator: owns the listeners, pairing, the single active phone session, the virtual display and
/// the streaming pipeline. All UI reads its state from here.
/// </summary>
public sealed class AppController : IDisposable
{
    public static AppController Instance { get; private set; } = null!;

    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly DisplayManager _display = new();
    private List<string>? _encoderMimes;
    private ClientSession? _session;
    private StreamPipeline? _pipeline;
    private D3DDevice? _d3d;
    private CancellationTokenSource? _releaseDelay;

    public HostIdentity Identity { get; }
    public PairingService Pairing { get; }
    public LanServer Lan { get; }
    public DiscoveryResponder Discovery { get; }
    public RemoteHost Remote { get; }
    public Update.UpdateService Updates { get; }

    public LinkState State { get; private set; } = LinkState.Waiting;
    public string StatusText { get; private set; } = "Esperando un teléfono";
    public string? LastError { get; private set; }
    public ClientSession? Session => _session;
    public StreamConfigMsg? CurrentConfig { get; private set; }
    public NegotiatedCaps? Caps { get; private set; }
    public PipelineStats? Stats => _pipeline?.Stats;
    public string DisplayDevice { get; private set; } = "";

    public event Action? StateChanged;
    public event Action<string, string>? Notify; // title, text

    public AppController()
    {
        Instance = this;
        Identity = HostIdentity.LoadOrCreate();
        Pairing = new PairingService(Identity);
        Lan = new LanServer(Identity, OnIncomingStream);
        Discovery = new DiscoveryResponder(Identity, () => Lan.Port);
        Remote = new RemoteHost(Identity, Pairing, OnIncomingStream);
        Updates = new Update.UpdateService();
        Remote.StatusChanged += RaiseChanged;
        Pairing.Changed += RaiseChanged;
    }

    public void Start()
    {
        _display.CleanupStale();
        ApplyNetworkSettings();
        Updates.Start();
    }

    public void ApplyNetworkSettings()
    {
        var s = SettingsStore.Current;
        if (s.LanEnabled)
        {
            if (!Lan.IsRunning || Lan.Port != s.LanPort) Lan.Start(s.LanPort);
            Discovery.Start();
        }
        else
        {
            Lan.Stop();
            Discovery.Stop();
        }
        if (s.RemoteEnabled) Remote.Start(); else Remote.Stop();
        RaiseChanged();
    }

    private void RaiseChanged() => StateChanged?.Invoke();

    private void SetState(LinkState st, string text, string? error = null)
    {
        State = st;
        StatusText = text;
        LastError = error;
        RaiseChanged();
    }

    // ------------------------------------------------------------------ connection handling

    private async Task OnIncomingStream(Stream stream, string transport, string remote)
    {
        HelloMsg? hello;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var f = await FrameCodec.ReadAsync(stream, cts.Token).ConfigureAwait(false);
            try
            {
                if (f.Type != MsgType.Hello) throw new InvalidDataException("Expected HELLO");
                hello = f.Json<HelloMsg>() ?? throw new InvalidDataException("Empty HELLO");
            }
            finally { f.Return(); }
        }
        catch (Exception ex)
        {
            Log.Warn($"Handshake with {remote} failed: {ex.Message}");
            await stream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        async Task Reject(string error)
        {
            Log.Warn($"Rejected {hello.DeviceName} ({remote}): {error}");
            try
            {
                await stream.WriteAsync(FrameCodec.EncodeJson(MsgType.Welcome, new WelcomeMsg { Ok = false, Error = error, HostId = Identity.HostId })).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch { }
            await Task.Delay(200).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        if (hello.Proto != AppInfo.ProtocolVersion) { await Reject("protocol-mismatch").ConfigureAwait(false); return; }

        var auth = Pairing.Authenticate(hello, out string? newToken);
        if (auth is not (PairingService.AuthResult.Ok or PairingService.AuthResult.Paired))
        {
            await Reject(auth switch
            {
                PairingService.AuthResult.BadToken => "not-paired",
                PairingService.AuthResult.BadCode => "bad-code",
                PairingService.AuthResult.CodeExpired => "code-expired",
                PairingService.AuthResult.TooManyAttempts => "too-many-attempts",
                _ => "malformed",
            }).ConfigureAwait(false);
            return;
        }

        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session != null && !_session.IsClosed)
            {
                if (_session.Hello.DeviceId == hello.DeviceId)
                {
                    StopStreaming(keepMonitor: true);
                    _session.Close("replaced by a new connection from the same phone");
                }
                else
                {
                    await Reject("busy").ConfigureAwait(false);
                    return;
                }
            }

            var session = new ClientSession(stream, hello, transport, remote);
            session.Device = Pairing.Devices.FirstOrDefault(d => d.DeviceId == hello.DeviceId);
            session.Closed += OnSessionClosed;
            session.FrameReceived += f => OnSessionFrame(session, f);
            session.SendJson(MsgType.Welcome, new WelcomeMsg
            {
                Ok = true,
                HostId = Identity.HostId,
                HostName = SettingsStore.Current.HostName,
                HostVersion = AppInfo.VersionString,
                DeviceToken = newToken,
            });
            _session = session;
            session.Start();
            Log.Info($"Phone connected: {hello.DeviceName} ({hello.Model}) via {transport} from {remote}");
            if (auth == PairingService.AuthResult.Paired)
                Notify?.Invoke("Dispositivo emparejado", $"{hello.DeviceName} ya puede usarse como monitor.");

            await StartStreamingAsync(session).ConfigureAwait(false);
        }
        finally { _sessionGate.Release(); }
    }

    private void OnSessionFrame(ClientSession session, Frame f)
    {
        switch (f.Type)
        {
            case MsgType.KeyframeRequest:
                _pipeline?.RequestKeyframe();
                break;
            case MsgType.ClientSettings:
                var cs = f.Json<ClientSettingsMsg>();
                if (cs != null && cs.Quality != session.Hello.Quality)
                {
                    session.Hello.Quality = cs.Quality;
                    _ = RestartStreamingAsync();
                }
                break;
            case MsgType.Stats:
                RaiseChanged();
                break;
        }
    }

    private void OnSessionClosed(ClientSession s, string reason)
    {
        _ = Task.Run(async () =>
        {
            await _sessionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_session, s)) return;
                StopStreaming(keepMonitor: true);
                _session = null;
                CurrentConfig = null;
                SetState(LinkState.Waiting, "Esperando un teléfono");
                ScheduleMonitorRelease();
            }
            finally { _sessionGate.Release(); }
        });
    }

    /// <summary>
    /// Keeps the virtual monitor for a few seconds after a drop so a quick Wi-Fi hiccup does not make Windows
    /// rearrange every window. If the phone reconnects within the grace period, the same monitor is reused.
    /// </summary>
    private void ScheduleMonitorRelease()
    {
        _releaseDelay?.Cancel();
        var cts = _releaseDelay = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(15), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _sessionGate.Wait();
            try
            {
                if (_session == null) { _display.Release(); FreeGpu(); }
            }
            finally { _sessionGate.Release(); }
        }, TaskScheduler.Default);
    }

    private QualityPreset EffectiveQuality(ClientSession s)
    {
        var q = SettingsStore.Current.Quality;
        if (q != QualityPreset.Auto) return q;
        return s.Hello.Quality switch
        {
            "maximum" => QualityPreset.Maximum,
            "high" => QualityPreset.High,
            "balanced" => QualityPreset.Balanced,
            "saver" => QualityPreset.Saver,
            _ => QualityPreset.Auto,
        };
    }

    private async Task StartStreamingAsync(ClientSession session)
    {
        _releaseDelay?.Cancel();
        var s = SettingsStore.Current;
        SetState(LinkState.Negotiating, $"Negociando con {session.Hello.DeviceName}…");
        try
        {
            _encoderMimes ??= MfEncoder.HardwareMimes();
            Caps = Negotiator.Compute(session.Hello.Caps, _encoderMimes);
            var mode = Negotiator.Choose(Caps, session.Hello.Caps, s);
            string mime = Negotiator.ChooseCodec(Caps, s, mode);

            var target = await _display.AcquireAsync(s.Mode, mode.Width, mode.Height, mode.Fps, s.Orientation, session.Token).ConfigureAwait(false);
            DisplayDevice = target.DeviceName;
            int outW = s.Mode == DisplayMode.Extended ? target.Width : mode.Width;
            int outH = s.Mode == DisplayMode.Extended ? target.Height : mode.Height;
            outW &= ~1; outH &= ~1;
            int fps = s.Mode == DisplayMode.Extended && target.RefreshHz > 0 ? Math.Min(mode.Fps, target.RefreshHz) : mode.Fps;
            if (s.Mode == DisplayMode.Mirror && target.RefreshHz > 0) fps = Math.Min(fps, target.RefreshHz);

            var abr = new BitrateController(outW, outH, fps, EffectiveQuality(session), s.MaxBitrateMbps);
            _d3d ??= new D3DDevice();
            var pipeline = new StreamPipeline(_d3d, target, session, abr, mime, outW, outH);
            pipeline.Failed += ex =>
            {
                SetState(LinkState.Error, "Error en la transmisión", ex.Message);
                session.Close("pipeline failure");
            };

            CurrentConfig = new StreamConfigMsg
            {
                Mime = mime, Width = outW, Height = outH, Fps = fps, BitrateKbps = abr.TargetKbps,
                Mode = s.Mode == DisplayMode.Extended ? "extended" : "mirror",
            };
            session.SendJson(MsgType.StreamConfig, CurrentConfig);
            pipeline.Start();
            _pipeline = pipeline;
            SetState(LinkState.Streaming, $"Transmitiendo a {session.Hello.DeviceName}");
        }
        catch (Exception ex)
        {
            Log.Error("Starting stream failed", ex);
            SetState(LinkState.Error, "No se pudo iniciar la transmisión", ex.Message);
            Notify?.Invoke("DualScreen", ex.Message);
            session.Close("start failed: " + ex.Message);
        }
    }

    private void StopStreaming(bool keepMonitor)
    {
        _pipeline?.Dispose();
        _pipeline = null;
        if (!keepMonitor) _display.Release();
    }

    private void FreeGpu()
    {
        _d3d?.Dispose();
        _d3d = null;
        GC.Collect(2, GCCollectionMode.Optimized, false);
    }

    /// <summary>Re-applies display settings to a live session (resolution, Hz, orientation, mode, quality).</summary>
    public async Task RestartStreamingAsync()
    {
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var s = _session;
            if (s == null || s.IsClosed) return;
            // Unplug and re-plug the virtual monitor so the driver publishes the new mode list.
            StopStreaming(keepMonitor: false);
            await StartStreamingAsync(s).ConfigureAwait(false);
        }
        finally { _sessionGate.Release(); }
    }

    public void Disconnect()
    {
        var s = _session;
        if (s == null) return;
        s.Close("disconnected from the PC");
    }

    public bool DriverInstalled => DriverClient.IsInstalled();

    public void Dispose()
    {
        try { _session?.Close("DualScreen closed"); } catch { }
        StopStreaming(keepMonitor: false);
        _display.Dispose();
        FreeGpu();
        Lan.Dispose();
        Discovery.Dispose();
        Remote.Dispose();
        Updates.Dispose();
        Identity.Dispose();
    }
}
