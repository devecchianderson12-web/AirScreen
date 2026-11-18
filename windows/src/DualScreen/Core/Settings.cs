using System;
using System.IO;
using System.Text.Json;

namespace DualScreen.Core;

public enum DisplayMode { Extended, Mirror }
public enum Orientation { Landscape, Portrait, LandscapeFlipped, PortraitFlipped }
public enum QualityPreset { Auto, Maximum, High, Balanced, Saver }
public enum PerformancePreset { LowLatency, Balanced, Quality }
public enum CodecPreference { Auto, H264, Hevc, Av1 }

public sealed class AppSettings
{
    /// <summary>Schema version for migrations of settings.json.</summary>
    public int SettingsVersion { get; set; }
    public const int CurrentVersion = 2;

    public DisplayMode Mode { get; set; } = DisplayMode.Extended;
    /// <summary>"auto" = phone's native resolution, otherwise "WIDTHxHEIGHT".</summary>
    public string Resolution { get; set; } = "auto";
    /// <summary>0 = highest the phone + link can sustain.</summary>
    public int RefreshRate { get; set; } = 0;
    public Orientation Orientation { get; set; } = Orientation.Landscape;
    public QualityPreset Quality { get; set; } = QualityPreset.Auto;
    public PerformancePreset Performance { get; set; } = PerformancePreset.LowLatency;
    public CodecPreference Codec { get; set; } = CodecPreference.Auto;
    /// <summary>Upper bound in Mbps for the adaptive bitrate. 0 = derived from resolution/fps.</summary>
    public int MaxBitrateMbps { get; set; } = 0;

    public int LanPort { get; set; } = AppInfo.DefaultLanPort;
    public bool LanEnabled { get; set; } = true;
    public bool RemoteEnabled { get; set; } = true;
    public string SignalingUrl { get; set; } = "";
    public string StunUrls { get; set; } = "stun:stun.l.google.com:19302,stun:stun1.l.google.com:19302";
    public string TurnUrl { get; set; } = "";
    public string TurnUser { get; set; } = "";
    /// <summary>DPAPI-protected, base64. Never stored in clear text.</summary>
    public string TurnPasswordProtected { get; set; } = "";

    public bool AutoCheckUpdates { get; set; } = true;
    public string? PostponedVersion { get; set; }
    public DateTime? PostponedUntilUtc { get; set; }
    public bool ShowNotifications { get; set; } = true;
    public string HostName { get; set; } = Environment.MachineName;

    public string? GetTurnPassword()
    {
        if (string.IsNullOrEmpty(TurnPasswordProtected)) return null;
        try { return System.Text.Encoding.UTF8.GetString(SecretStore.Unprotect(Convert.FromBase64String(TurnPasswordProtected))); }
        catch { return null; }
    }

    public void SetTurnPassword(string? value)
    {
        TurnPasswordProtected = string.IsNullOrEmpty(value)
            ? ""
            : Convert.ToBase64String(SecretStore.Protect(System.Text.Encoding.UTF8.GetBytes(value)));
    }

    public string EffectiveSignalingUrl =>
        !string.IsNullOrWhiteSpace(SignalingUrl) ? SignalingUrl.Trim() : AppInfo.Repo.DefaultSignalingUrl;
}

public static class SettingsStore
{
    private static readonly object Gate = new();
    public static AppSettings Current { get; private set; } = Load();
    public static event Action? Changed;

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json.Options) ?? new AppSettings();
                if (s.SettingsVersion < AppSettings.CurrentVersion)
                {
                    // v0.1.0 shipped with remote connections off; they are now on by default (zero-setup broker).
                    s.RemoteEnabled = true;
                    s.SettingsVersion = AppSettings.CurrentVersion;
                }
                return s;
            }
        }
        catch (Exception ex) { Log.Warn($"Settings file unreadable, using defaults: {ex.Message}"); }
        return new AppSettings { SettingsVersion = AppSettings.CurrentVersion };
    }

    public static void Save()
    {
        lock (Gate)
        {
            try
            {
                string tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json.Pretty));
                File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
            }
            catch (Exception ex) { Log.Error("Saving settings failed", ex); }
        }
        Changed?.Invoke();
    }

    public static void Update(Action<AppSettings> change)
    {
        change(Current);
        Save();
    }
}
