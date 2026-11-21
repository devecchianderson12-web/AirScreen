using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DualScreen.Display;
using DualScreen.Input;
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
    private bool _driverHintShown;
    private readonly InputInjector _injector = new();
    private AudioCapture? _audio;
    private bool _perfActive;

    public HostIdentity Identity { get; }
    public PairingService Pairing { get; }
    public LanServer Lan { get; }
    public DiscoveryResponder Discovery { get; }
    public RemoteHost Remote { get; }
    public BeaconService Beacon { get; }
    public Update.UpdateService Updates { get; }

    public LinkState State { get; private set; } = LinkState.Waiting;
    public string StatusText { get; private set; } = "Esperando un teléfono";
    public string? LastError { get; private set; }
    public ClientSession? Session => _session;
    public StreamConfigMsg? CurrentConfig { get; private set; }
    public NegotiatedCaps? Caps { get; private set; }
    public PipelineStats? Stats => _pipeline?.Stats;
    public string DisplayDevice { get; private set; } = "";
    /// <summary>Mode actually in use (Extended falls back to Mirror when the driver is not installed).</summary>
    public DisplayMode EffectiveMode { get; private set; }

    public event Action? StateChanged;
    public event Action<string, string>? Notify; // title, text

    public AppController()
    {
        Instance = this;
        DualScreen.Remote.SipLogBridge.Install();
        Identity = HostIdentity.LoadOrCreate();
        Pairing = new PairingService(Identity);
        Lan = new LanServer(Identity, OnIncomingStream);
        Discovery = new DiscoveryResponder(Identity, () => Lan.Port);
        Remote = new RemoteHost(Identity, OnIncomingStream);
        Beacon = new BeaconService(Identity, () => Lan.IsRunning ? Lan.Port : 0, async (addrs, port) =>
        {
            if (!await Lan.DialAsync(addrs, port).ConfigureAwait(false)) Log.Info("Dial requested by a receiver failed on every address");
        });
        Updates = new Update.UpdateService();
        Remote.StatusChanged += RaiseChanged;
        Remote.DialRequested += async (addrs, port) =>
        {
            if (!await Lan.DialAsync(addrs, port).ConfigureAwait(false)) Log.Info("Reverse LAN dial failed on every address");
        };
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
        if (s.AnnounceOnLan) Beacon.Start(); else Beacon.Stop();
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

        bool lanAllowed = SettingsStore.Current.LanNoCode && transport.StartsWith("LAN", StringComparison.Ordinal);
        var auth = Pairing.Authenticate(hello, out string? newToken, lanAllowed);
        if (auth is not (PairingService.AuthResult.Ok or PairingService.AuthResult.Paired))
        {
            await Reject(auth switch
            {
                PairingService.AuthResult.BadToken => "not-paired",
                PairingService.AuthResult.BadCode => "bad-code",
                PairingService.AuthResult.CodeExpired => "code-expired",
                PairingService.AuthResult.TooManyAttempts => "too-many-attempts",
                PairingService.AuthResult.CodeRequired => "code-required",
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
            case MsgType.Input:
                if (SettingsStore.Current.AllowRemoteInput) _injector.Handle(f.Payload);
                break;
            case MsgType.HostSettings:
                var hs = f.Json<HostSettingsMsg>();
                if (hs != null) ApplyHostSettings(session, hs);
                break;
        }
    }

    // ------------------------------------------------------------------ settings from the receiver

    /// <summary>
    /// Settings changed on the receiver (phone or PC) are applied to this PC's own settings — the same ones shown in
    /// the Windows window — and the stream restarts only if something that affects it changed.
    /// </summary>
    private void ApplyHostSettings(ClientSession session, HostSettingsMsg m)
    {
        bool restart = false, replug = false;
        if (m.Audio is bool a && a != session.AudioEnabled)
        {
            session.AudioEnabled = a;
            UpdateAudio(session);
        }
        SettingsStore.Update(s =>
        {
            if (m.Mode is "extended" or "mirror")
            {
                var mode = m.Mode == "mirror" ? DisplayMode.Mirror : DisplayMode.Extended;
                if (mode != s.Mode) { s.Mode = mode; restart = replug = true; }
            }
            if (m.MirrorMonitor != null && m.MirrorMonitor != s.MirrorMonitor) { s.MirrorMonitor = m.MirrorMonitor; restart |= s.Mode == DisplayMode.Mirror; }
            if (m.Resolution != null && m.Resolution != s.Resolution && (m.Resolution == "auto" || Negotiator.TryParse(m.Resolution, out _)))
            { s.Resolution = m.Resolution; restart = replug = true; }
            if (m.RefreshRate is int hz && hz >= 0 && hz != s.RefreshRate) { s.RefreshRate = hz; restart = replug = true; }
            if (m.Quality != null)
            {
                var q = m.Quality switch
                {
                    "maximum" => QualityPreset.Maximum, "high" => QualityPreset.High, "balanced" => QualityPreset.Balanced,
                    "saver" => QualityPreset.Saver, _ => QualityPreset.Auto,
                };
                if (q != s.Quality) { s.Quality = q; restart = true; }
            }
            if (m.Performance != null)
            {
                var p = m.Performance switch { "balanced" => PerformancePreset.Balanced, "quality" => PerformancePreset.Quality, _ => PerformancePreset.LowLatency };
                if (p != s.Performance) { s.Performance = p; restart = true; }
            }
            if (m.Codec != null)
            {
                var c = m.Codec switch { "h264" => CodecPreference.H264, "hevc" => CodecPreference.Hevc, "av1" => CodecPreference.Av1, _ => CodecPreference.Auto };
                if (c != s.Codec) { s.Codec = c; restart = true; }
            }
            if (m.MaxBitrateMbps is int mb && mb >= 0 && mb <= 300 && mb != s.MaxBitrateMbps) { s.MaxBitrateMbps = mb; restart = true; }
        });
        if (restart) _ = RestartStreamingAsync(replug);
        else SendHostState(session);
    }

    private void SendHostState(ClientSession session)
    {
        if (!session.Hello.Caps.Has("hostSettings") || session.IsClosed) return;
        var s = SettingsStore.Current;
        var caps = Caps;
        string key = Negotiator.TryParse(s.Resolution, out var rr) && caps != null && caps.FpsByResolution.ContainsKey(rr) ? s.Resolution : "";
        var resKey = key != "" ? rr : caps?.Resolutions.FirstOrDefault() ?? default;
        var msg = new HostStateMsg
        {
            Mode = s.Mode == DisplayMode.Mirror ? "mirror" : "extended",
            EffectiveMode = EffectiveMode == DisplayMode.Mirror ? "mirror" : "extended",
            DriverOk = DriverInstaller.GetStatus() == DriverStatus.Ok,
            MirrorMonitor = s.MirrorMonitor,
            Monitors = DisplayManager.ListMonitors(),
            Resolution = s.Resolution,
            Resolutions = caps?.Resolutions.Select(r => $"{r.W}x{r.H}").ToList() ?? new(),
            RefreshRate = s.RefreshRate,
            Rates = caps != null && caps.FpsByResolution.TryGetValue(resKey, out var rates) ? rates.ToList() : new List<int> { 60 },
            Quality = s.Quality switch
            {
                QualityPreset.Maximum => "maximum", QualityPreset.High => "high", QualityPreset.Balanced => "balanced",
                QualityPreset.Saver => "saver", _ => "auto",
            },
            Performance = s.Performance switch { PerformancePreset.Balanced => "balanced", PerformancePreset.Quality => "quality", _ => "lowLatency" },
            Codec = s.Codec switch { CodecPreference.H264 => "h264", CodecPreference.Hevc => "hevc", CodecPreference.Av1 => "av1", _ => "auto" },
            Codecs = caps?.Codecs.Select(c => c switch { "video/hevc" => "hevc", "video/av01" => "av1", _ => "h264" }).ToList() ?? new(),
            MaxBitrateMbps = s.MaxBitrateMbps,
            Audio = session.AudioEnabled,
            HostName = s.HostName,
        };
        session.SendJson(MsgType.HostState, msg);
    }

    /// <summary>Starts/stops sound capture according to the session's choice.</summary>
    private void UpdateAudio(ClientSession session)
    {
        bool want = session.AudioEnabled && !session.IsClosed && session.Hello.Caps.Has("audio");
        if (want)
        {
            _audio ??= new AudioCapture(frame => { var s = _session; if (s != null && s.AudioEnabled) s.SendControl(frame); });
            _audio.Start();
        }
        else
        {
            _audio?.Stop();
        }
    }

    /// <summary>Resolution or rotation of the streamed monitor changed in Windows: rebuild the stream with the new size.</summary>
    private async Task OnSourceResizedAsync(ClientSession session)
    {
        await Task.Delay(300).ConfigureAwait(false); // let Windows finish the mode change
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_session, session) || session.IsClosed) return;
            Log.Info("Streamed monitor changed size; reconfiguring the stream");
            StopStreaming(keepMonitor: true);
            await StartStreamingAsync(session).ConfigureAwait(false);
        }
        finally { _sessionGate.Release(); }
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

            // Extended mode needs the virtual display driver (a Windows requirement). Without it, fall back to
            // mirror mode so the phone always shows something with zero installation.
            var displayMode = s.Mode;
            if (displayMode == DisplayMode.Extended && !_display.DriverAvailable)
            {
                displayMode = DisplayMode.Mirror;
                if (!_driverHintShown)
                {
                    _driverHintShown = true;
                    Notify?.Invoke("Modo espejo", DriverInstaller.GetStatus() switch
                    {
                        DriverStatus.AccessDenied => "Se usa el modo espejo: el controlador necesita una reparación (Configuración → Controlador → Reparar).",
                        DriverStatus.Failed or DriverStatus.Outdated => "Se usa el modo espejo: el controlador está detenido o desactualizado (Configuración → Controlador → Actualizar).",
                        _ => "Se usa el modo espejo. Para usar la pantalla extendida, instala el controlador en Configuración.",
                    });
                }
            }
            EffectiveMode = displayMode;

            DisplayTarget target;
            try
            {
                target = await _display.AcquireAsync(displayMode, mode.Width, mode.Height, mode.Fps, s.Orientation, session.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (displayMode == DisplayMode.Extended && ex is not OperationCanceledException)
            {
                // Never lose the connection because of the virtual display: show the main screen instead.
                Log.Error("Extended mode failed, falling back to mirror", ex);
                displayMode = DisplayMode.Mirror;
                EffectiveMode = displayMode;
                Notify?.Invoke("Modo espejo", "No se pudo crear la pantalla extendida (" + ex.Message + "). Se muestra el modo espejo.");
                target = await _display.AcquireAsync(DisplayMode.Mirror, mode.Width, mode.Height, mode.Fps, s.Orientation, session.Token).ConfigureAwait(false);
            }
            DisplayDevice = target.DeviceName;
            bool extended = displayMode == DisplayMode.Extended;
            int outW = extended ? target.Width : mode.Width;
            int outH = extended ? target.Height : mode.Height;
            outW &= ~1; outH &= ~1;
            int fps = target.RefreshHz > 0 ? Math.Min(mode.Fps, target.RefreshHz) : mode.Fps;

            var abr = new BitrateController(outW, outH, fps, EffectiveQuality(session), s.MaxBitrateMbps);
            _d3d ??= new D3DDevice();
            var pipeline = new StreamPipeline(_d3d, target, session, abr, mime, outW, outH)
            {
                // Receivers that draw the cursor locally get it out of the video (zero-latency pointer).
                CaptureCursor = !session.Hello.Caps.Has("cursor"),
            };
            pipeline.SourceResized += (_, _) => _ = OnSourceResizedAsync(session);
            pipeline.Failed += ex =>
            {
                SetState(LinkState.Error, "Error en la transmisión", ex.Message);
                session.Close("pipeline failure");
            };

            CurrentConfig = new StreamConfigMsg
            {
                Mime = mime, Width = outW, Height = outH, Fps = fps, BitrateKbps = abr.TargetKbps,
                Mode = extended ? "extended" : "mirror",
            };
            session.SendJson(MsgType.StreamConfig, CurrentConfig);
            pipeline.Start();
            _pipeline = pipeline;
            _injector.SetTarget(target.Monitor, outW, outH);
            if (!_perfActive) { Perf.BeginStreaming(); _perfActive = true; }
            UpdateAudio(session);
            SendHostState(session);
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
        _injector.ClearTarget();
        _pipeline?.Dispose();
        _pipeline = null;
        if (!keepMonitor || _session == null || _session.IsClosed)
        {
            _audio?.Stop();
            if (_perfActive) { Perf.EndStreaming(); _perfActive = false; }
        }
        if (!keepMonitor) _display.Release();
    }

    private void FreeGpu()
    {
        _d3d?.Dispose();
        _d3d = null;
        GC.Collect(2, GCCollectionMode.Optimized, false);
    }

    /// <summary>Re-applies display settings to a live session (resolution, Hz, orientation, mode, quality).</summary>
    /// <param name="replug">Unplug/re-plug the virtual monitor (needed for mode, resolution or refresh changes).
    /// Quality, latency and codec changes only rebuild the encoder, so Windows does not rearrange windows.</param>
    public async Task RestartStreamingAsync(bool replug = true)
    {
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var s = _session;
            if (s == null || s.IsClosed) return;
            // Unplug and re-plug the virtual monitor so the driver publishes the new mode list.
            StopStreaming(keepMonitor: !replug);
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
        Beacon.Dispose();
        _injector.Dispose();
        _audio?.Dispose();
        if (_perfActive) { Perf.EndStreaming(); _perfActive = false; }
        Updates.Dispose();
        Identity.Dispose();
    }
}
