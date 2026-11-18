using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DualScreen.Core;

namespace DualScreen.Update;

public sealed class ReleaseInfo
{
    public Version Version { get; init; } = new();
    public string Tag { get; init; } = "";
    public string Notes { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
    public string? ExeUrl { get; init; }
    public string? SigUrl { get; init; }
    public string? SumsUrl { get; init; }
}

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, ReadyToInstall, Error }

/// <summary>
/// Checks GitHub Releases periodically, downloads the new DualScreen.exe, verifies SHA-256 (SHA256SUMS) and an
/// ECDSA P-256 signature made with the project's offline release key, then hands over to the external updater.
/// An update that fails any check is discarded; the running executable is never touched by this class.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly HttpClient Http = CreateHttp();
    private Timer? _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public ReleaseInfo? Latest { get; private set; }
    public string? Error { get; private set; }
    public double DownloadProgress { get; private set; }
    public DateTime? LastCheckUtc { get; private set; }
    public string? VerifiedFile { get; private set; }

    public event Action? Changed;
    public event Action<ReleaseInfo>? UpdateAvailable;

    public static bool HasPublicKey => LoadPublicKeyPem() != null;

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DualScreen-Updater", AppInfo.VersionString));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    public void Start()
    {
        int hours = Math.Max(1, AppInfo.Repo.UpdateCheckHours);
        _timer = new Timer(_ =>
        {
            if (SettingsStore.Current.AutoCheckUpdates) _ = CheckAsync(userInitiated: false);
        }, null, TimeSpan.FromSeconds(45), TimeSpan.FromHours(hours));
    }

    private void Set(UpdateState s, string? error = null)
    {
        State = s;
        Error = error;
        Changed?.Invoke();
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            Set(UpdateState.Checking);
            string url = $"https://api.github.com/repos/{AppInfo.Repo.Owner}/{AppInfo.Repo.Repo}/releases/latest";
            using var resp = await Http.GetAsync(url).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) { Set(UpdateState.UpToDate); return; }
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) { Set(UpdateState.UpToDate); return; }
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) throw new InvalidDataException($"Etiqueta de versión inválida: {tag}");

            string? Asset(string name) => root.GetProperty("assets").EnumerateArray()
                .Where(a => string.Equals(a.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.GetProperty("browser_download_url").GetString()).FirstOrDefault();

            string exe = AppInfo.Repo.ExeAssetName;
            Latest = new ReleaseInfo
            {
                Version = v,
                Tag = tag,
                Notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                HtmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
                ExeUrl = Asset(exe),
                SigUrl = Asset(exe + ".sig"),
                SumsUrl = Asset("SHA256SUMS"),
            };
            LastCheckUtc = DateTime.UtcNow;
            var current = new Version(AppInfo.Version.Major, AppInfo.Version.Minor, Math.Max(0, AppInfo.Version.Build));
            if (v > current)
            {
                Set(UpdateState.Available);
                var s = SettingsStore.Current;
                bool postponed = s.PostponedVersion == tag && s.PostponedUntilUtc > DateTime.UtcNow;
                if (userInitiated || !postponed) UpdateAvailable?.Invoke(Latest);
            }
            else Set(UpdateState.UpToDate);
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed: " + ex.Message);
            Set(UpdateState.Error, "No se pudo comprobar: " + ex.Message);
        }
        finally { _gate.Release(); }
    }

    public void Postpone(TimeSpan duration)
    {
        if (Latest == null) return;
        SettingsStore.Update(s =>
        {
            s.PostponedVersion = Latest.Tag;
            s.PostponedUntilUtc = DateTime.UtcNow + duration;
        });
    }

    /// <summary>Downloads and verifies the update. Returns the verified file path or null (Error is set).</summary>
    public async Task<string?> DownloadAndVerifyAsync()
    {
        var rel = Latest;
        if (rel?.ExeUrl == null || rel.SigUrl == null || rel.SumsUrl == null)
        {
            Set(UpdateState.Error, "La versión publicada no incluye el ejecutable, su firma o SHA256SUMS.");
            return null;
        }
        string? pem = LoadPublicKeyPem();
        if (pem == null)
        {
            Set(UpdateState.Error, "Esta compilación no tiene clave pública de actualizaciones; no se puede verificar la descarga.");
            return null;
        }
        try
        {
            Set(UpdateState.Downloading);
            string target = Path.Combine(AppPaths.UpdateDir, $"DualScreen-{rel.Version}.exe.download");
            await DownloadAsync(rel.ExeUrl, target).ConfigureAwait(false);
            string sums = await Http.GetStringAsync(rel.SumsUrl).ConfigureAwait(false);
            string sigB64 = (await Http.GetStringAsync(rel.SigUrl).ConfigureAwait(false)).Trim();

            byte[] fileHash;
            await using (var fs = File.OpenRead(target)) fileHash = await SHA256.HashDataAsync(fs).ConfigureAwait(false);
            string hex = Convert.ToHexString(fileHash).ToLowerInvariant();
            string? expected = sums.Split('\n')
                .Select(l => l.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length == 2 && p[1].TrimStart('*').Trim() == AppInfo.Repo.ExeAssetName)
                .Select(p => p[0].ToLowerInvariant()).FirstOrDefault();
            if (expected == null || !SecretStore.FixedTimeEqualsHex(expected, hex))
                throw new CryptographicException("El hash SHA-256 no coincide con SHA256SUMS.");

            if (!VerifySignature(fileHash, sigB64, pem))
                throw new CryptographicException("La firma digital de la actualización no es válida.");

            string verified = Path.Combine(AppPaths.UpdateDir, $"DualScreen-{rel.Version}.exe");
            File.Move(target, verified, overwrite: true);
            VerifiedFile = verified;
            Log.Info($"Update {rel.Version} downloaded and verified ({hex})");
            Set(UpdateState.ReadyToInstall);
            return verified;
        }
        catch (Exception ex)
        {
            Log.Error("Update download/verification failed", ex);
            foreach (var f in Directory.GetFiles(AppPaths.UpdateDir, "*.download")) { try { File.Delete(f); } catch { } }
            Set(UpdateState.Error, ex.Message);
            return null;
        }
    }

    /// <summary>Verifies a base64 DER ECDSA P-256 signature (as produced by `openssl dgst -sha256 -sign`) over a SHA-256 hash.</summary>
    public static bool VerifySignature(byte[] sha256, string signatureBase64, string publicKeyPem)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            return ecdsa.VerifyHash(sha256, Convert.FromBase64String(signatureBase64.Trim()), DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    private async Task DownloadAsync(string url, string path)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var dst = File.Create(path);
        var buf = new byte[81920];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
            done += n;
            if (total > 0)
            {
                DownloadProgress = done / (double)total;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Starts the external updater (a temporary copy of this exe) and returns true if the app must exit now.</summary>
    public bool LaunchUpdater(string verifiedFile)
    {
        try
        {
            string updater = Path.Combine(AppPaths.UpdateDir, "DualScreen.Updater.exe");
            File.Copy(AppPaths.ExePath, updater, overwrite: true);
            string token = Guid.NewGuid().ToString("N");
            var psi = new ProcessStartInfo(updater)
            {
                UseShellExecute = true,
                WorkingDirectory = AppPaths.UpdateDir,
                Arguments = $"--apply-update --pid {Environment.ProcessId} --target \"{AppPaths.ExePath}\" --new \"{verifiedFile}\" --token {token}",
            };
            if (!Updater.IsDirectoryWritable(AppPaths.ExeDir)) psi.Verb = "runas";
            Process.Start(psi);
            Log.Info("Updater launched");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not launch updater", ex);
            Set(UpdateState.Error, "No se pudo iniciar el actualizador: " + ex.Message);
            return false;
        }
    }

    private static string? LoadPublicKeyPem()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("DualScreen.update-public-key.pem");
        if (s == null) return null;
        string pem = new StreamReader(s).ReadToEnd();
        return pem.Contains("BEGIN PUBLIC KEY") ? pem : null;
    }

    public void Dispose() => _timer?.Dispose();
}
