using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DualScreen.Core;
using DualScreen.Display;
using DualScreen.Net;
using DualScreen.Receiver;
using DualScreen.Update;
using QRCoder;

namespace DualScreen.UI;

public partial class MainWindow : Window
{
    private readonly AppController _app = AppController.Instance;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _loading = true;
    private string? _qrPayload;
    private readonly ReceiverController _rx = ReceiverController.Instance;
    private string _savedSignature = "";
    private string _seenSignature = "";

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Backdrop.Apply(this);
        Loaded += (_, _) =>
        {
            LoadSettingsIntoControls();
            _loading = false;
            RefreshAll();
        };
        StateChanged += (_, _) => MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        _app.StateChanged += OnAppChanged;
        _app.Updates.Changed += OnAppChanged;
        _rx.Changed += OnAppChanged;
        ReceiverStore.Changed += OnAppChanged;
        _timer.Tick += (_, _) => RefreshLive();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _app.StateChanged -= OnAppChanged;
            _app.Updates.Changed -= OnAppChanged;
            _rx.Changed -= OnAppChanged;
            ReceiverStore.Changed -= OnAppChanged;
        };
    }

    private void OnAppChanged() => Dispatcher.BeginInvoke(RefreshAll);

    // ------------------------------------------------------------------ navigation

    public void Navigate(string page)
    {
        var target = page switch
        {
            "display" => NavDisplay,
            "connection" => NavConnection,
            "receive" => NavReceive,
            "updates" => NavUpdates,
            "settings" => NavSettings,
            _ => NavHome,
        };
        target.IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (Pages == null) return;
        StackPanel show = sender == NavDisplay ? PageDisplay
            : sender == NavConnection ? PageConnection
            : sender == NavReceive ? PageReceive
            : sender == NavUpdates ? PageUpdates
            : sender == NavSettings ? PageSettings : PageHome;
        foreach (UIElement p in Pages.Children) p.Visibility = p == show ? Visibility.Visible : Visibility.Collapsed;
        if (show == PageConnection && _app.Pairing.CurrentCode == null) _app.Pairing.NewCode();
        if (show == PageReceive) _rx.Beacons.Start(); // listening starts only when this feature is used
        ContentScroll.ScrollToTop();
        // Windows 11-style entrance: slide up + fade.
        show.Opacity = 0;
        var tt = new TranslateTransform(0, 18);
        show.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        show.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        RefreshAll();
    }

    // ------------------------------------------------------------------ refresh

    private void RefreshAll()
    {
        RefreshHome();
        RefreshDisplayOptions();
        RefreshConnection();
        RefreshUpdates();
        RefreshSettingsStatus();
        RefreshReceive();
    }

    private void RefreshLive()
    {
        RefreshHome();
        if (PageConnection.Visibility == Visibility.Visible) RefreshPairingCountdown();
        if (PageReceive.Visibility == Visibility.Visible) { RefreshSeenHosts(); RefreshReceiveStatus(); }
    }

    // ------------------------------------------------------------------ use this PC as a monitor

    private IntPtr CurrentMonitor() => MonitorFromWindow(new WindowInteropHelper(this).Handle, 2);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    private void RefreshReceive()
    {
        RefreshReceiveStatus();
        RefreshSeenHosts();
        var hosts = ReceiverStore.Hosts;
        string sig = string.Join("|", hosts.Select(h => $"{h.HostId}:{h.Name}:{h.NoKeyboard}:{h.LastUsedUtc.Ticks}")) + "|" + _rx.Busy;
        if (sig == _savedSignature) return;
        _savedSignature = sig;
        SavedHostList.Children.Clear();
        NoSavedHosts.Visibility = hosts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var h in hosts)
        {
            var card = new Border { Style = (Style)FindResource("Card") };
            var dock = new DockPanel();
            var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var nk = new CheckBox { Style = (Style)FindResource("ToggleSwitch"), IsChecked = h.NoKeyboard, VerticalAlignment = VerticalAlignment.Center,
                                    ToolTip = "No molestar: el teclado queda desactivado. Para salir, mantené presionada 3 s la esquina superior izquierda." };
            string id = h.HostId;
            nk.Click += (_, _) => ReceiverStore.Update(id, x => x.NoKeyboard = nk.IsChecked == true);
            actions.Children.Add(new TextBlock { Text = "No molestar", Style = (Style)FindResource("CaptionText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            actions.Children.Add(nk);
            var start = new Button { Style = (Style)FindResource("AccentButton"), Margin = new Thickness(16, 0, 8, 0), IsEnabled = !_rx.Busy };
            var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = "\uE768", Style = (Style)FindResource("Icon"), Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = "Iniciar", Foreground = Brushes.White });
            start.Content = sp;
            start.Click += async (_, _) => await StartReceiving(h);
            actions.Children.Add(start);
            var forget = new Button { Content = "Olvidar" };
            forget.Click += (_, _) =>
            {
                if (MessageBox.Show(this, $"¿Olvidar «{h.Name}»? Para volver a usarlo vas a necesitar un código nuevo.", "DualScreen",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    ReceiverStore.Remove(id);
            };
            actions.Children.Add(forget);
            DockPanel.SetDock(actions, Dock.Right);
            dock.Children.Add(actions);
            var left = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            left.Children.Add(new TextBlock { Text = "\uE7F4", Style = (Style)FindResource("Icon"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = h.Name, FontWeight = FontWeights.SemiBold });
            info.Children.Add(new TextBlock
            {
                Text = (h.LastUsedUtc > DateTime.MinValue ? $"Último uso {h.LastUsedUtc.ToLocalTime():g}" : "Vinculado") + " · un clic en Iniciar",
                Style = (Style)FindResource("CaptionText"),
            });
            left.Children.Add(info);
            dock.Children.Add(left);
            card.Child = dock;
            SavedHostList.Children.Add(card);
        }
    }

    private void RefreshSeenHosts()
    {
        var seen = _rx.Beacons.Hosts;
        string sig = string.Join("|", seen.Select(h => h.HostId + h.Name + h.Address + (ReceiverStore.Find(h.HostId) != null))) + "|" + _rx.Busy;
        if (sig != _seenSignature)
        {
            _seenSignature = sig;
            BuildSeenList(seen);
            string? selected = (SeenHostBox.SelectedItem as ComboBoxItem)?.Tag as string;
            SeenHostBox.Items.Clear();
            foreach (var h in seen)
                SeenHostBox.Items.Add(new ComboBoxItem { Content = $"{h.Name}  ·  {h.Address}", Tag = h.HostId });
            SeenHostBox.SelectedItem = SeenHostBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected)
                                       ?? SeenHostBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
        SeenHint.Text = _rx.Beacons.Error != null
            ? _rx.Beacons.Error
            : seen.Count == 0
                ? "Buscando equipos con DualScreen en esta red… (abrí DualScreen en la otra PC; si Windows pregunta, permití el acceso a redes privadas)."
                : "Hacé clic en un equipo o en Conectar.";
        PairButton.IsEnabled = !_rx.Busy;
    }

    /// <summary>Every DualScreen PC on this network: name, IP below, one click to connect (no code).</summary>
    private void BuildSeenList(IReadOnlyList<SeenHost> seen)
    {
        SeenList.Children.Clear();
        foreach (var h in seen)
        {
            bool saved = ReceiverStore.Find(h.HostId) != null;
            var card = new Border { Style = (Style)FindResource("Card"), Cursor = System.Windows.Input.Cursors.Hand };
            var dock = new DockPanel();
            var connect = new Button { Style = (Style)FindResource("AccentButton"), IsEnabled = !_rx.Busy, VerticalAlignment = VerticalAlignment.Center };
            var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = "\uE71B", Style = (Style)FindResource("Icon"), Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = saved ? "Iniciar" : "Conectar", Foreground = Brushes.White });
            connect.Content = sp;
            connect.Click += async (_, e) => { e.Handled = true; await ConnectSeen(h); };
            DockPanel.SetDock(connect, Dock.Right);
            dock.Children.Add(connect);
            var left = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            left.Children.Add(new TextBlock { Text = "\uE7F4", Style = (Style)FindResource("Icon"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = h.Name, FontWeight = FontWeights.SemiBold });
            info.Children.Add(new TextBlock { Text = h.Address + (saved ? " · vinculado" : ""), Style = (Style)FindResource("CaptionText") });
            left.Children.Add(info);
            dock.Children.Add(left);
            card.Child = dock;
            card.MouseLeftButtonUp += async (_, _) => { if (!_rx.Busy) await ConnectSeen(h); };
            SeenList.Children.Add(card);
        }
    }

    private async Task ConnectSeen(SeenHost h)
    {
        await _rx.ConnectAsync(h, PairNoKeyboard.IsChecked == true, CurrentMonitor());
        RefreshAll();
    }

    private void StartMinimized_Click(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s => s.StartMinimized = StartMinimizedToggle.IsChecked == true);

    private void RefreshReceiveStatus()
    {
        var st = _rx.State;
        bool show = st != ReceiverState.Idle;
        ReceiveStatusCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        ReceiveStatus.Text = _rx.StatusText;
        ReceiveDot.Fill = st switch
        {
            ReceiverState.Streaming => (Brush)FindResource("SuccessBrush"),
            ReceiverState.Error => (Brush)FindResource("ErrorBrush"),
            _ => (Brush)FindResource("WarningBrush"),
        };
        var s = _rx.Session;
        var cfg = s?.Config;
        ReceiveDetail.Text = s != null && cfg != null
            ? $"{cfg.Width} × {cfg.Height} @ {cfg.Fps} Hz · {(cfg.Mime == "video/hevc" ? "HEVC" : "H.264")} · {s.Fps:0} fps · " +
              $"{s.RecvKbps / 1000:0.0} Mbps · decodifica en {s.DecodeMs:0.0} ms · RTT {(double.IsNaN(s.RttMs) ? "—" : s.RttMs.ToString("0") + " ms")} · {s.Transport} · {s.DecoderName}"
            : "";
        ReceiveError.Text = _rx.LastError ?? "";
        ReceiveError.Visibility = string.IsNullOrEmpty(_rx.LastError) ? Visibility.Collapsed : Visibility.Visible;
        ReceiveStopButton.Content = st == ReceiverState.Error ? "Cerrar" : "Detener";
    }

    private async Task StartReceiving(SavedHost h)
    {
        await _rx.StartAsync(h, CurrentMonitor());
        RefreshAll();
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        string code = new string(PairCodeBox.Text.Where(char.IsDigit).ToArray());
        var id = (SeenHostBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var host = id != null ? _rx.Beacons.Find(id) : null;
        if (host == null)
        {
            MessageBox.Show(this, "Elegí el equipo que va a transmitir. Si no aparece, abrí DualScreen en él y verificá que estén en la misma red.",
                "DualScreen", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (code.Length != 6)
        {
            MessageBox.Show(this, "Escribí el código de 6 dígitos que muestra el otro equipo en Conexión.", "DualScreen",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        PairButton.IsEnabled = false;
        bool ok = await _rx.PairAndStartAsync(host, code, PairNoKeyboard.IsChecked == true, CurrentMonitor());
        if (ok) PairCodeBox.Text = "";
        RefreshAll();
    }

    private void ReceiveStop_Click(object sender, RoutedEventArgs e)
    {
        _rx.Stop();
        RefreshAll();
    }

    private void RemoteInput_Click(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s => s.AllowRemoteInput = RemoteInputToggle.IsChecked == true);

    private void RefreshHome()
    {
        var st = _app.State;
        var s = _app.Session;
        StatusText.Text = _app.StatusText;
        StatusDot.Fill = st switch
        {
            LinkState.Streaming => (Brush)FindResource("SuccessBrush"),
            LinkState.Negotiating => (Brush)FindResource("WarningBrush"),
            LinkState.Error => (Brush)FindResource("ErrorBrush"),
            _ => (Brush)FindResource("TextTertiaryBrush"),
        };
        DeviceText.Text = s != null
            ? $"{s.Hello.DeviceName} · {s.Hello.Model}"
            : "Abre DualScreen en tu Android y pulsa Conectar, o escanea el código QR.";
        ErrorText.Text = _app.LastError ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(_app.LastError) ? Visibility.Collapsed : Visibility.Visible;

        bool connected = s != null && !s.IsClosed;
        ConnectLabel.Text = connected ? "Desconectar" : "Conectar";
        ConnectIcon.Text = connected ? "" : "";

        var cfg = _app.CurrentConfig;
        var stats = _app.Stats;
        if (cfg != null && connected)
        {
            StatResolution.Text = $"{cfg.Width} × {cfg.Height}";
            int fps = stats?.TargetFps > 0 ? stats.TargetFps : cfg.Fps;
            StatRefresh.Text = fps < cfg.Fps ? $"{fps} Hz (limitado de {cfg.Fps})" : $"{cfg.Fps} Hz";
            StatQuality.Text = SettingsStore.Current.Quality switch
            {
                QualityPreset.Maximum => "Máxima", QualityPreset.High => "Alta", QualityPreset.Balanced => "Equilibrada",
                QualityPreset.Saver => "Ahorro", _ => stats?.Congested == true ? "Auto (reduciendo)" : "Auto",
            };
            double rtt = s!.RttMs;
            var cst = s.LastStats;
            double latency = (double.IsNaN(rtt) ? 0 : rtt / 2) + (stats?.EncodeMs ?? 0) + (cst?.DecodeMs ?? 0) + s.QueueDelayMs;
            StatLatency.Text = double.IsNaN(rtt) ? "midiendo…" : $"≈ {latency:0} ms";
            StatBitrate.Text = stats != null ? $"{stats.SentKbps / 1000:0.0} Mbps" : "—";
            string codec = cfg.Mime switch { "video/hevc" => "HEVC", "video/av01" => "AV1", _ => "H.264" };
            StatEncoder.Text = $"{codec} · {stats?.Encoder}";
            TransportText.Text = $"Conexión: {s.Transport} · RTT {(double.IsNaN(rtt) ? "—" : rtt.ToString("0") + " ms")} · " +
                                 $"teléfono: {cst?.Fps ?? 0:0} fps, decodifica en {cst?.DecodeMs ?? 0:0.0} ms · objetivo {(stats?.TargetKbps ?? 0) / 1000.0:0.0} Mbps";
        }
        else
        {
            StatResolution.Text = StatRefresh.Text = StatQuality.Text = StatLatency.Text = StatBitrate.Text = StatEncoder.Text = "—";
            TransportText.Text = "";
        }
    }

    private void RefreshDisplayOptions()
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            var s = SettingsStore.Current;
            var caps = _app.Caps;
            ResolutionBox.Items.Clear();
            ResolutionBox.Items.Add(new ComboBoxItem { Content = "Automática (nativa del teléfono)", Tag = "auto" });
            var resolutions = caps?.Resolutions ?? Negotiator.StandardResolutions;
            foreach (var r in resolutions)
                ResolutionBox.Items.Add(new ComboBoxItem { Content = $"{r.W} × {r.H}", Tag = $"{r.W}x{r.H}" });
            ResolutionBox.SelectedItem = ResolutionBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Resolution)
                                         ?? ResolutionBox.Items[0];
            ResolutionHint.Text = caps == null
                ? "Conecta el teléfono para ver solo las resoluciones que realmente soporta."
                : "Solo se muestran las resoluciones que tu teléfono puede decodificar.";

            RefreshBox.Items.Clear();
            RefreshBox.Items.Add(new ComboBoxItem { Content = "Máxima disponible", Tag = 0 });
            int[] rates;
            if (caps != null)
            {
                (int, int) key = Negotiator.TryParse(s.Resolution, out var rr) && caps.FpsByResolution.ContainsKey(rr) ? rr
                    : caps.Resolutions.FirstOrDefault();
                rates = caps.FpsByResolution.TryGetValue(key, out var l) ? l.ToArray() : new[] { 60 };
            }
            else rates = new[] { 30, 60, 90, 120, 144 };
            foreach (int r in rates) RefreshBox.Items.Add(new ComboBoxItem { Content = $"{r} Hz", Tag = r });
            RefreshBox.SelectedItem = RefreshBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == s.RefreshRate) ?? RefreshBox.Items[0];

            CodecHint.Text = caps == null ? "Se elige según el hardware de ambos dispositivos."
                : "Disponibles en ambos: " + string.Join(", ", caps.Codecs.Select(c => c switch { "video/hevc" => "HEVC", "video/av01" => "AV1", _ => "H.264" }));
            bool driver = _app.DriverInstalled;
            var dst = DriverInstaller.GetStatus();
            DriverWarning.Visibility = s.Mode == DisplayMode.Extended && dst != Display.DriverStatus.Ok ? Visibility.Visible : Visibility.Collapsed;
            DriverWarningText.Text = dst switch
            {
                Display.DriverStatus.Outdated or Display.DriverStatus.Failed => "El controlador de pantalla virtual está desactualizado o detenido: actualizalo para usar la pantalla extendida (un aviso de administrador).",
                Display.DriverStatus.AccessDenied => "El controlador necesita una reparación para usar la pantalla extendida (un aviso de administrador).",
                _ => "El modo extendido necesita el controlador de pantalla virtual (requiere permisos de administrador una sola vez).",
            };
            DriverWarningButton.Content = dst switch
            {
                Display.DriverStatus.Outdated or Display.DriverStatus.Failed => "Actualizar controlador",
                Display.DriverStatus.AccessDenied => "Reparar controlador",
                _ => "Instalar controlador",
            };
        }
        finally { _loading = wasLoading; }
    }

    private void RefreshConnection()
    {
        RefreshPairingCountdown();
        var s = SettingsStore.Current;
        LanStatus.Text = _app.Lan.IsRunning ? "Escuchando" : "Desactivada";
        LanAddresses.Text = string.Join("  ", PairingService.LocalAddresses().Select(a => a.ToString())) + $"  ·  TCP {_app.Lan.Port}";
        RemoteStatus.Text = s.RemoteEnabled ? _app.Remote.Status : "Desactivada";
        string fp = _app.Identity.Fingerprint;
        Fingerprint.Text = string.Join(":", Enumerable.Range(0, 8).Select(i => fp.Substring(i * 2, 2))).ToUpperInvariant() + "…";

        DeviceList.Children.Clear();
        var devices = _app.Pairing.Devices;
        NoDevices.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var d in devices.OrderByDescending(d => d.LastSeenUtc))
        {
            var card = new Border { Style = (Style)FindResource("Card") };
            var dock = new DockPanel();
            var revoke = new Button { Content = "Revocar", Tag = d.DeviceId };
            revoke.Click += (_, _) =>
            {
                if (MessageBox.Show(this, $"¿Revocar el acceso de «{d.Name}»? Tendrá que emparejarse de nuevo.", "DualScreen",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                if (_app.Session?.Hello.DeviceId == d.DeviceId) _app.Disconnect();
                _app.Pairing.Revoke(d.DeviceId);
            };
            DockPanel.SetDock(revoke, Dock.Right);
            dock.Children.Add(revoke);
            var info = new StackPanel();
            var icon = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            icon.Children.Add(new TextBlock { Text = "", Style = (Style)FindResource("Icon"), Margin = new Thickness(0, 0, 12, 0) });
            info.Children.Add(new TextBlock { Text = d.Name });
            info.Children.Add(new TextBlock
            {
                Text = $"{d.Model} · emparejado {d.PairedUtc.ToLocalTime():d} · visto {d.LastSeenUtc.ToLocalTime():g}",
                Style = (Style)FindResource("CaptionText"),
            });
            icon.Children.Add(info);
            dock.Children.Add(icon);
            card.Child = dock;
            DeviceList.Children.Add(card);
        }
    }

    private void RefreshPairingCountdown()
    {
        string? code = _app.Pairing.CurrentCode;
        if (code == null)
        {
            PairCode.Text = "— — —";
            PairExpiry.Text = "Código caducado. Genera uno nuevo.";
            QrImage.Source = null;
            _qrPayload = null;
            return;
        }
        PairCode.Text = $"{code[..3]} {code[3..]}";
        var left = _app.Pairing.CodeExpiresUtc - DateTime.UtcNow;
        PairExpiry.Text = $"Caduca en {(int)left.TotalMinutes}:{left.Seconds:00} · un solo uso";
        string url = SettingsStore.Current.RemoteEnabled ? SettingsStore.Current.EffectiveSignalingUrl : "";
        string payload = _app.Pairing.BuildQrPayload(_app.Lan.Port, url);
        if (payload != _qrPayload)
        {
            _qrPayload = payload;
            QrImage.Source = MakeQr(payload);
        }
    }

    private static BitmapImage MakeQr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        byte[] png = new PngByteQRCode(data).GetGraphic(8);
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = new MemoryStream(png);
        img.EndInit();
        img.Freeze();
        return img;
    }

    private void RefreshUpdates()
    {
        var u = _app.Updates;
        var latest = u.Latest;
        bool available = u.State is UpdateState.Available or UpdateState.ReadyToInstall or UpdateState.Downloading
                         || (u.State == UpdateState.Error && latest != null && latest.Version > AppInfo.Version);
        UpdateBadge.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        UpdateTitle.Text = available ? $"Nueva versión disponible: {latest!.Version}" : "DualScreen está actualizado";
        UpdateVersions.Text = $"Versión actual: {AppInfo.VersionString}" + (latest != null ? $" · Última publicada: {latest.Version}" : "");
        UpdateStatus.Text = u.State switch
        {
            UpdateState.Checking => "Buscando actualizaciones…",
            UpdateState.Downloading => $"Descargando… {u.DownloadProgress:P0}",
            UpdateState.ReadyToInstall => "Descarga verificada (SHA-256 y firma). Reiniciando para instalar…",
            UpdateState.Error => u.Error ?? "Error",
            UpdateState.UpToDate => u.LastCheckUtc is { } t ? $"Última comprobación: {t.ToLocalTime():g}" : "",
            _ => UpdateService.HasPublicKey ? "" : "Compilación de desarrollo: sin clave de verificación de actualizaciones.",
        };
        UpdateProgress.Visibility = u.State == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Value = u.DownloadProgress;
        UpdateNowButton.Visibility = PostponeButton.Visibility = available && u.State != UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.IsEnabled = u.State is not (UpdateState.Checking or UpdateState.Downloading);
        ReleaseNotes.Text = string.IsNullOrWhiteSpace(latest?.Notes) ? "—" : latest!.Notes;
        UpdateSourceText.Text = $"Fuente: github.com/{AppInfo.Repo.Owner}/{AppInfo.Repo.Repo} (GitHub Releases)";
    }

    private void RefreshSettingsStatus()
    {
        var st = DriverInstaller.GetStatus();
        bool installed = st != Display.DriverStatus.NotInstalled;
        bool ok = st == Display.DriverStatus.Ok;
        DriverStatus.Text = st switch
        {
            Display.DriverStatus.Ok => "Controlador instalado y en funcionamiento",
            Display.DriverStatus.Outdated => "Hay una versión nueva del controlador",
            Display.DriverStatus.Failed => "El controlador está detenido",
            Display.DriverStatus.AccessDenied => "El controlador necesita una reparación",
            _ => "Controlador no instalado",
        };
        DriverDetail.Text = st switch
        {
            Display.DriverStatus.Outdated => $"Instalado {DriverInstaller.InstalledVersion()}, incluido en esta versión {DriverInstaller.EmbeddedVersion}. «Actualizar» lo reemplaza (un aviso de administrador).",
            Display.DriverStatus.Failed => "Windows lo detuvo (por ejemplo tras un error de una versión anterior). «Actualizar» instala el incluido y lo reinicia.",
            Display.DriverStatus.AccessDenied => "Está instalado pero DualScreen no tiene permiso para usarlo. «Reparar» lo corrige (un aviso de administrador).",
            _ => "",
        };
        if (DriverDetail.Text.Length == 0) DriverDetail.Text = installed
            ? "Windows puede crear el monitor virtual DualScreen (Configuración → Sistema → Pantalla)."
            : DriverInstaller.HasEmbeddedPackage
                ? "Necesario para el modo extendido. Se pedirá permiso de administrador una sola vez y se instalará también el certificado del controlador DualScreen."
                : "Esta compilación no incluye el paquete del controlador (consulta docs/DRIVER.md).";
        DriverInstallButton.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        DriverInstallButton.Content = st switch
        {
            Display.DriverStatus.AccessDenied => "Reparar",
            Display.DriverStatus.Outdated or Display.DriverStatus.Failed => "Actualizar",
            _ => "Instalar",
        };
        DriverUninstallButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        DataPath.Text = AppPaths.DataDir;
        AboutText.Text = $"DualScreen {AppInfo.VersionString} · Protocolo v{AppInfo.ProtocolVersion} · ID del PC {_app.Identity.HostId}";
    }

    private void LoadSettingsIntoControls()
    {
        var s = SettingsStore.Current;
        ModeExtended.IsChecked = s.Mode == DisplayMode.Extended;
        ModeMirror.IsChecked = s.Mode == DisplayMode.Mirror;
        OrientationBox.SelectedIndex = (int)s.Orientation;
        QualityBox.SelectedIndex = (int)s.Quality;
        PerformanceBox.SelectedIndex = (int)s.Performance;
        CodecBox.SelectedIndex = (int)s.Codec;
        BitrateSlider.Value = s.MaxBitrateMbps;
        BitrateValue.Text = s.MaxBitrateMbps == 0 ? "Auto" : $"{s.MaxBitrateMbps} Mbps";
        StartupToggle.IsChecked = StartupRegistration.IsEnabled();
        NotifyToggle.IsChecked = s.ShowNotifications;
        AutoUpdateToggle.IsChecked = s.AutoCheckUpdates;
        HostNameBox.Text = s.HostName;
        LanToggle.IsChecked = s.LanEnabled;
        AnnounceToggle.IsChecked = s.AnnounceOnLan;
        LanNoCodeToggle.IsChecked = s.LanNoCode;
        StartMinimizedToggle.IsChecked = s.StartMinimized;
        RemoteInputToggle.IsChecked = s.AllowRemoteInput;
        PortBox.Text = s.LanPort.ToString();
        RemoteToggle.IsChecked = s.RemoteEnabled;
        SignalingBox.Text = s.SignalingUrl;
        StunBox.Text = s.StunUrls;
        TurnBox.Text = s.TurnUrl;
        TurnUserBox.Text = s.TurnUser;
        TurnPassBox.Password = s.GetTurnPassword() ?? "";
    }

    // ------------------------------------------------------------------ handlers

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_app.Session != null && !_app.Session.IsClosed) _app.Disconnect();
        else Navigate("connection");
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Update(s => s.Mode = ModeMirror.IsChecked == true ? DisplayMode.Mirror : DisplayMode.Extended);
        RefreshDisplayOptions();
        _ = _app.RestartStreamingAsync();
    }

    private void DisplaySetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Update(s =>
        {
            if (ResolutionBox.SelectedItem is ComboBoxItem r) s.Resolution = (string)r.Tag;
            if (RefreshBox.SelectedItem is ComboBoxItem f) s.RefreshRate = (int)f.Tag;
            if (OrientationBox.SelectedIndex >= 0) s.Orientation = (Core.Orientation)OrientationBox.SelectedIndex;
            if (QualityBox.SelectedIndex >= 0) s.Quality = (QualityPreset)QualityBox.SelectedIndex;
            if (PerformanceBox.SelectedIndex >= 0) s.Performance = (PerformancePreset)PerformanceBox.SelectedIndex;
            if (CodecBox.SelectedIndex >= 0) s.Codec = (CodecPreference)CodecBox.SelectedIndex;
        });
        if (sender == ResolutionBox) RefreshDisplayOptions();
        _ = _app.RestartStreamingAsync();
    }

    private void Bitrate_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BitrateValue == null) return;
        int v = (int)BitrateSlider.Value;
        BitrateValue.Text = v == 0 ? "Auto" : $"{v} Mbps";
        if (_loading) return;
        SettingsStore.Update(s => s.MaxBitrateMbps = v);
    }

    private void NewCode_Click(object sender, RoutedEventArgs e)
    {
        _app.Pairing.NewCode();
        RefreshConnection();
    }

    private async void InstallDriver_Click(object sender, RoutedEventArgs e)
    {
        var (ok, msg) = await Task.Run(() => DriverInstaller.RunElevated("install"));
        MessageBox.Show(this, string.IsNullOrWhiteSpace(msg) ? (ok ? "Controlador instalado." : "La instalación falló.") : msg,
            "DualScreen", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        RefreshAll();
    }

    private async void UninstallDriver_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "¿Desinstalar el controlador de pantalla virtual? El modo extendido dejará de estar disponible.",
                "DualScreen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _app.Disconnect();
        var (ok, msg) = await Task.Run(() => DriverInstaller.RunElevated("uninstall"));
        MessageBox.Show(this, msg, "DualScreen", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        RefreshAll();
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await _app.Updates.CheckAsync(userInitiated: true);

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        UpdateNowButton.IsEnabled = false;
        try
        {
            string? file = _app.Updates.VerifiedFile ?? await _app.Updates.DownloadAndVerifyAsync();
            if (file != null && _app.Updates.LaunchUpdater(file))
                ((App)Application.Current).ExitApplication();
        }
        finally { UpdateNowButton.IsEnabled = true; }
    }

    private void Postpone_Click(object sender, RoutedEventArgs e)
    {
        _app.Updates.Postpone(TimeSpan.FromDays(1));
        UpdateStatus.Text = "Te lo recordaremos mañana.";
    }

    private void AutoUpdate_Click(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s => s.AutoCheckUpdates = AutoUpdateToggle.IsChecked == true);

    private void Startup_Click(object sender, RoutedEventArgs e) => StartupRegistration.Set(StartupToggle.IsChecked == true);

    private void Notify_Click(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s => s.ShowNotifications = NotifyToggle.IsChecked == true);

    private void HostName_LostFocus(object sender, RoutedEventArgs e)
    {
        string name = HostNameBox.Text.Trim();
        if (name.Length is > 0 and <= 64) SettingsStore.Update(s => s.HostName = name);
    }

    private void Network_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Update(s =>
        {
            s.LanEnabled = LanToggle.IsChecked == true;
            s.AnnounceOnLan = AnnounceToggle.IsChecked == true;
            s.LanNoCode = LanNoCodeToggle.IsChecked == true;
            if (int.TryParse(PortBox.Text, out int port) && port is > 1024 and < 65536) s.LanPort = port;
            s.RemoteEnabled = RemoteToggle.IsChecked == true;
            s.SignalingUrl = SignalingBox.Text.Trim();
            s.StunUrls = StunBox.Text.Trim();
            s.TurnUrl = TurnBox.Text.Trim();
            s.TurnUser = TurnUserBox.Text.Trim();
            s.SetTurnPassword(TurnPassBox.Password);
        });
        _app.ApplyNetworkSettings();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true });

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Mica backdrop, dark title bar and rounded corners through DWM (Windows 11; graceful on Windows 10).</summary>
internal static class Backdrop
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_BORDER_COLOR = 34;

    public static void Apply(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        int dark = 1, round = 2, none = 1, border = 0x00422E2A; // COLORREF 0x00BBGGRR = #2A2E42
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        // Solid deep-navy surface (the theme's WindowBrush) instead of the translucent Mica material.
        if (Environment.OSVersion.Version.Build >= 22621)
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
    }
}

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(AppInfo.ProductName) is string v && v.Contains(AppPaths.ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(AppInfo.ProductName, $"\"{AppPaths.ExePath}\" --background");
        else k.DeleteValue(AppInfo.ProductName, false);
    }
}
