using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using DualScreen.Core;
using Forms = System.Windows.Forms;

namespace DualScreen.UI;

/// <summary>
/// Notification-area icon. Left click opens the main window; right click shows a Windows 11-styled WPF menu
/// (not the dated WinForms one). Notifications use the shell balloon API, which Windows 10/11 renders as native toasts.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly Forms.NotifyIcon _icon;
    private readonly ContextMenu _menu;
    private readonly MenuItem _connectItem;
    private readonly MenuItem _disconnectItem;
    private readonly MenuItem _receiveItem;
    private readonly MenuItem _castItem;
    private readonly MenuItem _stopReceiveItem;
    private Window? _menuHost;
    private string? _balloonTarget;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public TrayIcon(App app)
    {
        _app = app;
        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "DualScreen",
            Visible = true,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _app.ShowMainWindow();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
        _icon.BalloonTipClicked += (_, _) => _app.ShowMainWindow(_balloonTarget);

        _menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        _menu.Items.Add(Item("Abrir DualScreen", "", () => _app.ShowMainWindow()));
        _connectItem = Item("Conectar dispositivo", "", () => _app.ShowMainWindow("connection"));
        _disconnectItem = Item("Desconectar dispositivo", "", () => AppController.Instance.Disconnect());
        _menu.Items.Add(_connectItem);
        _menu.Items.Add(_disconnectItem);
        _menu.Items.Add(new Separator());
        // One click: show the last linked PC on this screen.
        _receiveItem = Item("Usar como monitor", "\uE7F8", () =>
        {
            var last = Receiver.ReceiverStore.Hosts.FirstOrDefault();
            if (last == null) _app.ShowMainWindow("receive");
            else _ = Receiver.ReceiverController.Instance.StartAsync(last, IntPtr.Zero);
        });
        _stopReceiveItem = Item("Dejar de usar como monitor", "\uE71A", () => Receiver.ReceiverController.Instance.Stop());
        _castItem = Item("Transmitir", "\uEC15", () =>
        {
            var c = AppController.Instance;
            var target = c.CastTargets.FirstOrDefault();
            if (target == null) _app.ShowMainWindow();
            else c.CastTo(target);
        });
        _menu.Items.Add(_castItem);
        _menu.Items.Add(_receiveItem);
        _menu.Items.Add(_stopReceiveItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("Buscar actualizaciones", "", () =>
        {
            _app.ShowMainWindow("updates");
            _ = AppController.Instance.Updates.CheckAsync(userInitiated: true);
        }));
        _menu.Items.Add(Item("Configuración", "", () => _app.ShowMainWindow("settings")));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("Salir", "", () => _app.ExitApplication()));
        _menu.Closed += (_, _) => _menuHost?.Hide();
        UpdateState();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/DualScreen.ico"));
        return res != null ? new System.Drawing.Icon(res.Stream, Forms.SystemInformation.SmallIconSize) : System.Drawing.SystemIcons.Application;
    }

    private static MenuItem Item(string header, string glyph, Action action)
    {
        var mi = new MenuItem { Header = header, Tag = glyph };
        mi.Click += (_, _) => action();
        return mi;
    }

    public void UpdateState()
    {
        var c = AppController.Instance;
        bool connected = c.Session != null && !c.Session.IsClosed;
        _connectItem.IsEnabled = !connected;
        _disconnectItem.IsEnabled = connected;
        var castTarget = c.CastTargets.FirstOrDefault();
        _castItem.Header = castTarget != null ? $"Transmitir a {castTarget.Name}" : "Transmitir a un equipo…";
        _castItem.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        var rx = Receiver.ReceiverController.Instance;
        var last = Receiver.ReceiverStore.Hosts.FirstOrDefault();
        _receiveItem.Header = last != null ? $"Usar como monitor de {last.Name}" : "Usar como monitor de otra PC…";
        _receiveItem.IsEnabled = !rx.Busy;
        _stopReceiveItem.Visibility = rx.Busy ? Visibility.Visible : Visibility.Collapsed;
        string text = connected ? $"DualScreen — conectado a {c.Session!.Hello.DeviceName}" : "DualScreen — esperando un teléfono";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ShowMenu()
    {
        // The menu needs a foreground owner window so it closes when the user clicks elsewhere.
        if (_menuHost == null)
        {
            _menuHost = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false, Width = 1, Height = 1, Left = -10000, Top = -10000, Topmost = true, ShowActivated = true,
            };
        }
        _menuHost.Show();
        SetForegroundWindow(new WindowInteropHelper(_menuHost).Handle);
        UpdateState();
        _menu.PlacementTarget = null;
        _menu.IsOpen = true;
    }

    public void ShowNotification(string title, string text, string? targetPage = null)
    {
        if (!SettingsStore.Current.ShowNotifications) return;
        _balloonTarget = targetPage;
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menuHost?.Close();
    }
}
