using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DualScreen.Core;

namespace DualScreen.Remote;

/// <summary>
/// Fetches short-lived TURN relay credentials from the DualScreen ICE endpoint (a small Cloudflare Worker that
/// holds the Cloudflare Realtime TURN API key; see signaling-server/cloudflare-turn-worker). The relay is what makes
/// remote connections work when a direct peer-to-peer path is impossible (e.g. phones on mobile carrier CGNAT).
/// Result: a flat array of {urls, username?, credential?} objects with one URL each.
/// </summary>
public static class IceCredentials
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static JsonArray? _cached;
    private static DateTime _fetchedUtc;

    public static string Url => !string.IsNullOrWhiteSpace(SettingsStore.Current.IceServersUrl)
        ? SettingsStore.Current.IceServersUrl.Trim() : AppInfo.Repo.IceServersUrl;

    public static async Task<JsonArray> GetAsync()
    {
        string url = Url;
        if (string.IsNullOrWhiteSpace(url)) return new JsonArray();
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Credentials live 24 h; refresh well before they expire.
            if (_cached != null && DateTime.UtcNow - _fetchedUtc < TimeSpan.FromHours(6)) return (JsonArray)_cached.DeepClone();
            var root = JsonNode.Parse(await Http.GetStringAsync(url).ConfigureAwait(false));
            _cached = Normalize(root?["iceServers"]);
            _fetchedUtc = DateTime.UtcNow;
            Log.Info($"TURN credentials fetched ({_cached.Count} entries)");
            return (JsonArray)_cached.DeepClone();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not fetch TURN credentials: " + ex.Message);
            return _cached != null ? (JsonArray)_cached.DeepClone() : new JsonArray();
        }
        finally { Gate.Release(); }
    }

    /// <summary>Accepts both Cloudflare response shapes (object or array; "urls" string or array).</summary>
    public static JsonArray Normalize(JsonNode? node)
    {
        var result = new JsonArray();
        var servers = node switch { JsonArray a => a.ToList(), JsonObject o => new() { o }, _ => new() };
        foreach (var s in servers.OfType<JsonObject>())
        {
            var urls = s["urls"] switch
            {
                JsonArray a => a.Select(u => (string?)u).ToList(),
                JsonValue v => new() { (string?)v },
                _ => new(),
            };
            foreach (var u in urls.Where(u => !string.IsNullOrEmpty(u)))
            {
                // SIPSorcery handles stun:/turn: (UDP/TCP); "turns:" (TLS) is skipped.
                if (u!.StartsWith("turns:", StringComparison.OrdinalIgnoreCase)) continue;
                var e = new JsonObject { ["urls"] = u };
                if (s["username"] is JsonNode user) e["username"] = (string?)user;
                if (s["credential"] is JsonNode cred) e["credential"] = (string?)cred;
                result.Add(e);
            }
        }
        return result;
    }
}
