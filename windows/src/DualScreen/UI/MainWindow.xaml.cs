using System;
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
using DualScreen.Update;
using QRCoder;

namespace DualScreen.UI;

public partial class MainWindow : Window
{
    private readonly AppController _app = AppController.Instance;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _loading = true;
    private string? _qrPayload;

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
        _timer.Tick += (_, _) => RefreshLive();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _app.StateChanged -= OnAppChanged;
            _app.Updates.Changed -= OnAppChanged;
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
            : sender == NavUpdates ? PageUpdates
            : sender == NavSettings ? PageSettings : PageHome;
        foreach (UIElement p in Pages.Children) p.Visibility = p == show ? Visibility.Visible : Visibility.Collapsed;
        if (show == PageConnection && _app.Pairing.CurrentCode == null) _app.Pairing.NewCode();
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
    }

    private void RefreshLive()
    {
        RefreshHome();
        if (PageConnection.Visibility == Visibility.Visible) RefreshPairingCountdown();
    }

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
            DriverWarning.Visibility = s.Mode == DisplayMode.Extended && !driver ? Visibility.Visible : Visibility.Collapsed;
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
        bool installed = _app.DriverInstalled;
        DriverStatus.Text = installed ? "Controlador instalado y en funcionamiento" : "Controlador no instalado";
        DriverDetail.Text = installed
            ? "Windows puede crear el monitor virtual DualScreen (Configuración → Sistema → Pantalla)."
            : DriverInstaller.HasEmbeddedPackage
                ? "Necesario para el modo extendido. Se pedirá permiso de administrador una sola vez."
                : "Esta compilación no incluye el paquete del controlador (consulta docs/DRIVER.md).";
        DriverInstallButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
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

    public static void Apply(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        int dark = 1, round = 2, mica = 2;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        if (Environment.OSVersion.Version.Build >= 22621 &&
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref mica, sizeof(int)) == 0)
        {
            // Let the Mica material show through the WPF surface.
            w.Background = Brushes.Transparent;
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: not null } src)
                src.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
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
