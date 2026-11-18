using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DualScreen.Core;

namespace DualScreen.Net;

public sealed class TrustedDevice
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>SHA-256 of the device token. The token itself is never stored on the PC.</summary>
    public string TokenHash { get; set; } = "";
    public DateTime PairedUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// Manages short-lived pairing codes (6 digits, 5 minutes, single use, 5 attempts) and the list of trusted
/// devices. Device tokens are 256-bit random values; only their SHA-256 is kept (DPAPI-protected file).
/// </summary>
public sealed class PairingService
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    private const int MaxAttempts = 5;

    private readonly object _gate = new();
    private readonly HostIdentity _identity;
    private List<TrustedDevice> _devices;
    private string? _code;
    private DateTime _codeExpiresUtc;
    private int _failedAttempts;

    public event Action? Changed;

    public PairingService(HostIdentity identity)
    {
        _identity = identity;
        _devices = LoadDevices();
    }

    public string? CurrentCode { get { lock (_gate) return IsCodeValid ? _code : null; } }
    public DateTime CodeExpiresUtc { get { lock (_gate) return _codeExpiresUtc; } }
    private bool IsCodeValid => _code != null && DateTime.UtcNow < _codeExpiresUtc;

    public IReadOnlyList<TrustedDevice> Devices { get { lock (_gate) return _devices.ToList(); } }

    public string NewCode()
    {
        lock (_gate)
        {
            _code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _codeExpiresUtc = DateTime.UtcNow + CodeLifetime;
            _failedAttempts = 0;
        }
        Changed?.Invoke();
        return _code!;
    }

    public void InvalidateCode()
    {
        lock (_gate) { _code = null; }
        Changed?.Invoke();
    }

    /// <summary>Builds the QR payload. It pins the host certificate fingerprint, so QR pairing is MITM-safe.</summary>
    public string BuildQrPayload(int port, string? signalingUrl)
    {
        string code = CurrentCode ?? NewCode();
        var addrs = string.Join(",", LocalAddresses().Select(a => a.ToString()));
        var sb = new StringBuilder("dualscreen://pair?v=1");
        sb.Append("&h=").Append(_identity.HostId);
        sb.Append("&n=").Append(Uri.EscapeDataString(SettingsStore.Current.HostName));
        sb.Append("&a=").Append(Uri.EscapeDataString(addrs));
        sb.Append("&p=").Append(port);
        sb.Append("&f=").Append(_identity.Fingerprint);
        sb.Append("&c=").Append(code);
        if (!string.IsNullOrWhiteSpace(signalingUrl)) sb.Append("&s=").Append(Uri.EscapeDataString(signalingUrl));
        return sb.ToString();
    }

    public static string ComputeProof(string code, string fingerprint, string deviceId, string nonce)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(code));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes($"DSPAIR1|{fingerprint}|{deviceId}|{nonce}"))).ToLowerInvariant();
    }

    public enum AuthResult { Ok, Paired, BadToken, BadCode, CodeExpired, TooManyAttempts, Malformed }

    /// <summary>Authenticates a HELLO. On successful pairing returns the new device token (only time it exists in clear).</summary>
    public AuthResult Authenticate(HelloMsg hello, out string? newToken)
    {
        newToken = null;
        if (string.IsNullOrWhiteSpace(hello.DeviceId) || hello.DeviceId.Length > 128) return AuthResult.Malformed;

        if (hello.Auth.Mode == "token")
        {
            if (string.IsNullOrEmpty(hello.Auth.Token)) return AuthResult.Malformed;
            string hash = SecretStore.Sha256Hex(Encoding.UTF8.GetBytes(hello.Auth.Token));
            lock (_gate)
            {
                var dev = _devices.FirstOrDefault(d => d.DeviceId == hello.DeviceId);
                if (dev == null || !SecretStore.FixedTimeEqualsHex(dev.TokenHash, hash)) return AuthResult.BadToken;
                dev.LastSeenUtc = DateTime.UtcNow;
                dev.Name = Truncate(hello.DeviceName, 64);
                SaveDevices();
            }
            Changed?.Invoke();
            return AuthResult.Ok;
        }

        if (hello.Auth.Mode == "pair")
        {
            if (string.IsNullOrEmpty(hello.Auth.Proof) || string.IsNullOrEmpty(hello.Auth.Nonce) || hello.Auth.Nonce.Length < 16)
                return AuthResult.Malformed;
            lock (_gate)
            {
                if (_code == null) return AuthResult.CodeExpired;
                if (DateTime.UtcNow >= _codeExpiresUtc) { _code = null; return AuthResult.CodeExpired; }
                string expected = ComputeProof(_code, _identity.Fingerprint, hello.DeviceId, hello.Auth.Nonce);
                if (!SecretStore.FixedTimeEqualsHex(expected, hello.Auth.Proof))
                {
                    if (++_failedAttempts >= MaxAttempts) { _code = null; return AuthResult.TooManyAttempts; }
                    return AuthResult.BadCode;
                }
                _code = null; // single use
                byte[] tokenBytes = RandomNumberGenerator.GetBytes(32);
                newToken = Convert.ToBase64String(tokenBytes);
                _devices.RemoveAll(d => d.DeviceId == hello.DeviceId);
                _devices.Add(new TrustedDevice
                {
                    DeviceId = hello.DeviceId,
                    Name = Truncate(hello.DeviceName, 64),
                    Model = Truncate(hello.Model, 64),
                    TokenHash = SecretStore.Sha256Hex(Encoding.UTF8.GetBytes(newToken)),
                    PairedUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow,
                });
                SaveDevices();
            }
            Changed?.Invoke();
            return AuthResult.Paired;
        }
        return AuthResult.Malformed;
    }

    public void Revoke(string deviceId)
    {
        lock (_gate)
        {
            _devices.RemoveAll(d => d.DeviceId == deviceId);
            SaveDevices();
        }
        Log.Info($"Device revoked: {deviceId}");
        Changed?.Invoke();
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private static List<TrustedDevice> LoadDevices()
    {
        byte[]? data = SecretStore.ReadProtected(AppPaths.DevicesFile);
        if (data == null) return new();
        try { return JsonSerializer.Deserialize<List<TrustedDevice>>(data, Json.Options) ?? new(); }
        catch { return new(); }
    }

    private void SaveDevices() =>
        SecretStore.WriteProtected(AppPaths.DevicesFile, JsonSerializer.SerializeToUtf8Bytes(_devices, Json.Options));

    public static IEnumerable<IPAddress> LocalAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address)
                    && !ua.Address.ToString().StartsWith("169.254."))
                    yield return ua.Address;
            }
        }
    }
}
