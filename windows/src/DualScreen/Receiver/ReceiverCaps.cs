using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using DualScreen.Core;
using DualScreen.Display;
using DualScreen.Media;
using DualScreen.Net;

namespace DualScreen.Receiver;

/// <summary>Describes this PC's screen and hardware decoders in the same terms an Android receiver uses.</summary>
public static class ReceiverCaps
{
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    public static DeviceCaps Build(IntPtr monitor)
    {
        var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        Native.GetMonitorInfo(monitor, ref info);
        int w = info.rcMonitor.Right - info.rcMonitor.Left, h = info.rcMonitor.Bottom - info.rcMonitor.Top;

        var rates = new SortedSet<float>();
        var dm = Native.DEVMODE.Create();
        if (Native.EnumDisplaySettings(info.szDevice, Native.ENUM_CURRENT_SETTINGS, ref dm) && dm.dmDisplayFrequency > 1)
            rates.Add(dm.dmDisplayFrequency);
        // Only the rate the panel is running at: frames beyond it would never reach the screen.
        if (rates.Count == 0) rates.Add(60);

        uint dpi = 96;
        try { if (GetDpiForMonitor(monitor, 0, out uint dx, out _) == 0) dpi = dx; } catch { }

        var codecs = new List<CodecCaps>();
        foreach (var mime in MfDecoder.AvailableMimes())
            codecs.Add(CodecFor(mime, w, h));

        return new DeviceCaps
        {
            ScreenWidth = w,
            ScreenHeight = h,
            DensityDpi = (int)dpi,
            RefreshRates = rates.ToList(),
            Codecs = codecs,
            Abi = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            SdkInt = Environment.OSVersion.Version.Build,
            Platform = "windows",
            Input = new List<string> { "touch", "pen", "mouse", "keyboard" },
            // No "cursor": the PC draws its pointer into the video, so it is always visible (a locally drawn
            // cursor disappears when Windows hides the pointer after touch input on convertibles).
            Features = new List<string> { "audio", "hostSettings", "latency" },
        };
    }

    /// <summary>Level 5.2 limits (what every DXVA decoder of the last decade handles), capped at 240 fps.</summary>
    private static CodecCaps CodecFor(string mime, int nativeW, int nativeH)
    {
        var c = new CodecCaps { Mime = mime, Hardware = true, MaxWidth = 4096, MaxHeight = 2304 };
        var native = Negotiator.Landscape(nativeW, nativeH);
        foreach (var (rw, rh) in Negotiator.StandardResolutions.Append(native).Distinct())
        {
            double fps;
            if (mime == "video/hevc") fps = 1_069_547_520.0 / (rw * (double)rh);
            else
            {
                double mbs = Math.Ceiling(rw / 16.0) * Math.Ceiling(rh / 16.0);
                if (mbs > 36864) continue;
                fps = 2_073_600.0 / mbs;
            }
            if (rw > c.MaxWidth || rh > c.MaxHeight) continue;
            c.Modes.Add(new ModeCaps { Width = rw, Height = rh, MaxFps = (int)Math.Min(240, Math.Floor(fps)) });
        }
        return c;
    }
}
