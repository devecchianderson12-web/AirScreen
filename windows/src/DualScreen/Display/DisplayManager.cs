using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DualScreen.Core;

namespace DualScreen.Display;

public sealed class DriverFailedException(string message, Exception inner) : Exception(message, inner);

public sealed record DisplayTarget(string DeviceName, IntPtr Monitor, int Width, int Height, int RefreshHz, bool IsVirtual);

/// <summary>
/// Creates/removes the virtual monitor and resolves the HMONITOR to capture.
/// Extended mode: plugs a DualScreen IDD monitor, applies the negotiated mode, captures that monitor.
/// Mirror mode: no virtual monitor; captures the primary monitor and scales it to the phone.
/// </summary>
public sealed class DisplayManager : IDisposable
{
    private const uint Connector = 0;
    private DriverClient? _driver;
    private bool _monitorPlugged;

    public bool DriverAvailable
    {
        get
        {
            if (_driver != null) return true;
            _driver = DriverClient.TryOpen();
            return _driver != null;
        }
    }

    public async Task<DisplayTarget> AcquireAsync(DisplayMode mode, int width, int height, int hz, Orientation orientation, CancellationToken ct)
    {
        if (mode == DisplayMode.Mirror) return GetMirrorTarget(SettingsStore.Current.MirrorMonitor);

        if (!DriverAvailable)
            throw new InvalidOperationException("El modo extendido necesita el controlador de pantalla virtual de DualScreen. Instálalo desde Configuración → Controlador.");

        bool portrait = orientation is Orientation.Portrait or Orientation.PortraitFlipped;
        int lw = portrait ? height : width, lh = portrait ? width : height; // driver modes are always landscape
        var modes = BuildModeList(lw, lh, hz);
        bool freshlyPlugged = !_monitorPlugged;
        if (!_monitorPlugged)
        {
            try
            {
                try { _driver!.AddMonitor(Connector, modes, 0); }
                catch (IOException first) when (modes.Length > 1 || hz != 60)
                {
                    Log.Warn("Retrying the virtual monitor with a single 60 Hz mode: " + first.Message);
                    modes = new[] { (lw, lh, 60) };
                    _driver!.AddMonitor(Connector, modes, 0);
                }
            }
            catch (Exception ex)
            {
                // The driver host may have restarted (its handle is now dead): reopen next time.
                _driver?.Dispose();
                _driver = null;
                throw new DriverFailedException("El controlador de pantalla virtual no pudo crear el monitor: " + ex.Message, ex);
            }
            _monitorPlugged = true;
            Log.Info($"Virtual monitor plugged: {lw}x{lh}@{hz}");
        }

        string? device = null;
        for (int i = 0; i < 50 && device == null; i++)
        {
            device = FindVirtualDisplayDevice();
            if (device == null) await Task.Delay(100, ct).ConfigureAwait(false);
        }
        if (device == null) throw new TimeoutException("Windows no activó la pantalla virtual.");

        EnsureExtendTopology(device);
        if (freshlyPlugged) ApplyMode(device, lw, lh, hz, orientation);

        IntPtr mon = IntPtr.Zero;
        for (int i = 0; i < 30 && mon == IntPtr.Zero; i++)
        {
            mon = FindMonitorHandle(device);
            if (mon == IntPtr.Zero) await Task.Delay(100, ct).ConfigureAwait(false);
        }
        if (mon == IntPtr.Zero) throw new TimeoutException("No se encontró el monitor virtual en el escritorio.");
        var cur = GetCurrentMode(device);
        return new DisplayTarget(device, mon, cur.w, cur.h, cur.hz, true);
    }

    /// <summary>Unplugs monitors left behind if a previous DualScreen process crashed.</summary>
    public void CleanupStale()
    {
        try { if (DriverAvailable) _driver!.RemoveAll(); }
        catch (Exception ex) { Log.Warn("Stale monitor cleanup failed: " + ex.Message); }
    }

    public void Release()
    {
        if (!_monitorPlugged || _driver == null) return;
        try { _driver.RemoveMonitor(Connector); Log.Info("Virtual monitor unplugged"); }
        catch (Exception ex) { Log.Warn("Unplug failed: " + ex.Message); }
        _monitorPlugged = false;
    }

    /// <summary>
    /// Modes the virtual monitor advertises, like a real one: the receiver's native mode first, other refresh
    /// rates, then the usual smaller resolutions, so Windows Settings can change resolution freely (rotation and
    /// scaling are handled by Windows itself).
    /// </summary>
    private static (int W, int H, int Hz)[] BuildModeList(int w, int h, int hz)
    {
        var list = new List<(int, int, int)> { (w, h, hz) };
        foreach (int r in new[] { 60, 75, 90, 120, 144, 165 }.Where(r => r != hz && r <= Math.Max(hz, 60)))
            list.Add((w, h, r));
        var smaller = Net.Negotiator.StandardResolutions
            .Concat(new[] { (1024, 768), (800, 600) })
            .Where(x => x.Item1 <= w && x.Item2 <= h && (x.Item1 != w || x.Item2 != h))
            .Distinct().OrderByDescending(x => x.Item1 * x.Item2);
        foreach (var (sw, sh) in smaller)
        {
            list.Add((sw, sh, 60));
            if (hz > 60 && list.Count < DriverClient.MaxModes - 4) list.Add((sw, sh, hz));
        }
        return list.Distinct().Take(DriverClient.MaxModes).ToArray();
    }

    /// <summary>Physical monitors of this PC (the DualScreen virtual one excluded), for mirror-mode selection.</summary>
    public static List<Net.MonitorInfoMsg> ListMonitors()
    {
        var result = new List<Net.MonitorInfoMsg>();
        string? virt = FindVirtualDisplayDevice();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref info) && !string.Equals(info.szDevice, virt, StringComparison.OrdinalIgnoreCase))
            {
                var r = info.rcMonitor;
                string name = FriendlyName(info.szDevice);
                result.Add(new Net.MonitorInfoMsg
                {
                    Id = info.szDevice,
                    Name = (info.dwFlags & 1) != 0 ? $"{name} (principal)" : name,
                    Width = r.Right - r.Left,
                    Height = r.Bottom - r.Top,
                    Primary = (info.dwFlags & 1) != 0,
                });
            }
            return true;
        }, IntPtr.Zero);
        return result.OrderByDescending(m => m.Primary).ToList();
    }

    private static string FriendlyName(string device)
    {
        var mon = Native.DISPLAY_DEVICE.Create();
        if (Native.EnumDisplayDevices(device, 0, ref mon, 0) && !string.IsNullOrWhiteSpace(mon.DeviceString))
            return mon.DeviceString.Trim();
        return device.Replace(@"\\.\", "");
    }

    /// <summary>Mirror mode source: the chosen monitor, or the primary one.</summary>
    public static DisplayTarget GetMirrorTarget(string? device)
    {
        if (!string.IsNullOrEmpty(device))
        {
            IntPtr mon = FindMonitorHandle(device);
            if (mon != IntPtr.Zero)
            {
                var cur = GetCurrentMode(device);
                return new DisplayTarget(device, mon, cur.w, cur.h, cur.hz, false);
            }
        }
        return GetPrimary();
    }

    public static DisplayTarget GetPrimary()
    {
        IntPtr mon = Native.MonitorFromPoint(new Native.POINT(), Native.MONITOR_DEFAULTTOPRIMARY);
        var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
        Native.GetMonitorInfo(mon, ref info);
        var cur = GetCurrentMode(info.szDevice);
        return new DisplayTarget(info.szDevice, mon, cur.w, cur.h, cur.hz, false);
    }

    /// <summary>Finds \\.\DISPLAYn whose attached monitor has our EDID manufacturer id "DSC".</summary>
    public static string? FindVirtualDisplayDevice()
    {
        var adapter = Native.DISPLAY_DEVICE.Create();
        for (uint i = 0; Native.EnumDisplayDevices(null, i, ref adapter, 0); i++, adapter = Native.DISPLAY_DEVICE.Create())
        {
            var monitor = Native.DISPLAY_DEVICE.Create();
            for (uint j = 0; Native.EnumDisplayDevices(adapter.DeviceName, j, ref monitor, 0); j++, monitor = Native.DISPLAY_DEVICE.Create())
            {
                if (monitor.DeviceID != null && monitor.DeviceID.Contains(@"\DSC", StringComparison.OrdinalIgnoreCase))
                    return adapter.DeviceName;
                // EDID-less fallback monitor: recognised through its adapter (the DualScreen virtual display).
                if (adapter.DeviceString != null && adapter.DeviceString.Contains("DualScreen", StringComparison.OrdinalIgnoreCase))
                    return adapter.DeviceName;
            }
        }
        return null;
    }

    private static IntPtr FindMonitorHandle(string device)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref info) && string.Equals(info.szDevice, device, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static (int w, int h, int hz) GetCurrentMode(string device)
    {
        var dm = Native.DEVMODE.Create();
        if (Native.EnumDisplaySettings(device, Native.ENUM_CURRENT_SETTINGS, ref dm))
        {
            bool rotated = dm.dmDisplayOrientation is 1 or 3;
            return rotated ? (dm.dmPelsHeight, dm.dmPelsWidth, dm.dmDisplayFrequency) : (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency);
        }
        return (0, 0, 0);
    }

    private static void ApplyMode(string device, int w, int h, int hz, Orientation o)
    {
        var dm = Native.DEVMODE.Create();
        Native.EnumDisplaySettings(device, Native.ENUM_CURRENT_SETTINGS, ref dm);
        // DMDO_DEFAULT=0, DMDO_90=1, DMDO_180=2, DMDO_270=3. "Landscape" (default) keeps the rotation the user
        // chose in Windows Settings for this monitor, like a real display; other values force it.
        int orient = o switch { Orientation.Landscape => dm.dmDisplayOrientation, Orientation.Portrait => 1, Orientation.LandscapeFlipped => 2, _ => 3 };
        bool rotated = orient is 1 or 3;
        dm.dmPelsWidth = rotated ? h : w;
        dm.dmPelsHeight = rotated ? w : h;
        dm.dmDisplayFrequency = hz;
        dm.dmDisplayOrientation = orient;
        dm.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT | Native.DM_DISPLAYFREQUENCY | Native.DM_DISPLAYORIENTATION;
        int r = Native.ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, Native.CDS_UPDATEREGISTRY | Native.CDS_NORESET, IntPtr.Zero);
        if (r != Native.DISP_CHANGE_SUCCESSFUL) Log.Warn($"ChangeDisplaySettingsEx({device}) = {r}");
        Native.ChangeDisplaySettingsExApply(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
    }

    /// <summary>If Windows is in "duplicate" topology the new monitor would just mirror; switch to extend.</summary>
    private static void EnsureExtendTopology(string device)
    {
        var dd = Native.DISPLAY_DEVICE.Create();
        for (uint i = 0; Native.EnumDisplayDevices(null, i, ref dd, 0); i++, dd = Native.DISPLAY_DEVICE.Create())
        {
            if (string.Equals(dd.DeviceName, device, StringComparison.OrdinalIgnoreCase))
            {
                if ((dd.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                {
                    int r = Native.SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, Native.SDC_TOPOLOGY_EXTEND | Native.SDC_APPLY);
                    Log.Info($"Switched display topology to Extend ({r})");
                }
                return;
            }
        }
    }

    public void Dispose()
    {
        Release();
        _driver?.Dispose();
        _driver = null;
    }
}
