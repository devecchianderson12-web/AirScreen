using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace DualScreen.Core;

/// <summary>Static product information and build-time configuration (embedded repo.json).</summary>
public static class AppInfo
{
    public const string ProductName = "DualScreen";
    public const int ProtocolVersion = 1;
    public const int DefaultLanPort = 47800;
    public const int DiscoveryPort = 47801;

    public static Version Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);

    public static string VersionString => $"{Version.Major}.{Version.Minor}.{Version.Build}";

    public static RepoConfig Repo { get; } = LoadRepoConfig();

    private static RepoConfig LoadRepoConfig()
    {
        try
        {
            using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("DualScreen.repo.json");
            if (s != null)
            {
                var cfg = JsonSerializer.Deserialize<RepoConfig>(s, Json.Options);
                if (cfg != null) return cfg;
            }
        }
        catch { /* fall back to defaults */ }
        return new RepoConfig();
    }
}

public sealed class RepoConfig
{
    public string Owner { get; set; } = "your-github-user";
    public string Repo { get; set; } = "DualScreen";
    public string ExeAssetName { get; set; } = "DualScreen.exe";
    public string DefaultSignalingUrl { get; set; } = "";
    public int UpdateCheckHours { get; set; } = 6;
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };
}
