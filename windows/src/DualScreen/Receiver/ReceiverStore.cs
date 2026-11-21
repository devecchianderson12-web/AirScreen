using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DualScreen.Core;

namespace DualScreen.Receiver;

/// <summary>A PC this computer is linked to as a monitor. Saved once at pairing; afterwards one click starts it.</summary>
public sealed class SavedHost
{
    public string HostId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Pinned SHA-256 of the host certificate.</summary>
    public string Fingerprint { get; set; } = "";
    public List<string> Addresses { get; set; } = new();
    /// <summary>Host's TCP port when it accepts incoming connections; 0 = only reverse connections.</summary>
    public int Port { get; set; }
    public string? SignalingUrl { get; set; }
    /// <summary>Device token (the whole file is DPAPI-protected).</summary>
    public string Token { get; set; } = "";
    public DateTime PairedUtc { get; set; }
    public DateTime LastUsedUtc { get; set; }
    /// <summary>"Do not disturb": the keyboard is disabled while this host is shown.</summary>
    public bool NoKeyboard { get; set; }
}

internal sealed class ReceiverData
{
    public string DeviceId { get; set; } = "";
    public List<SavedHost> Hosts { get; set; } = new();
}

/// <summary>receiver.json (DPAPI, current user): this PC's receiver identity and the linked hosts.</summary>
public static class ReceiverStore
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(AppPaths.DataDir, "receiver.json");
    private static ReceiverData? _data;

    public static event Action? Changed;

    private static ReceiverData Data
    {
        get
        {
            if (_data != null) return _data;
            try
            {
                byte[]? raw = SecretStore.ReadProtected(FilePath);
                if (raw != null) _data = JsonSerializer.Deserialize<ReceiverData>(raw, Json.Options);
            }
            catch (Exception ex) { Log.Warn("receiver.json unreadable: " + ex.Message); }
            _data ??= new ReceiverData();
            if (string.IsNullOrEmpty(_data.DeviceId))
            {
                _data.DeviceId = Guid.NewGuid().ToString();
                SaveLocked();
            }
            return _data;
        }
    }

    public static string DeviceId { get { lock (Gate) return Data.DeviceId; } }

    public static IReadOnlyList<SavedHost> Hosts
    {
        get { lock (Gate) return Data.Hosts.OrderByDescending(h => h.LastUsedUtc).ToList(); }
    }

    public static SavedHost? Find(string hostId)
    {
        lock (Gate) return Data.Hosts.FirstOrDefault(h => h.HostId == hostId);
    }

    public static void Upsert(SavedHost host)
    {
        lock (Gate)
        {
            Data.Hosts.RemoveAll(h => h.HostId == host.HostId);
            Data.Hosts.Add(host);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public static void Update(string hostId, Action<SavedHost> change)
    {
        lock (Gate)
        {
            var h = Data.Hosts.FirstOrDefault(x => x.HostId == hostId);
            if (h == null) return;
            change(h);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public static void Remove(string hostId)
    {
        lock (Gate)
        {
            Data.Hosts.RemoveAll(h => h.HostId == hostId);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    private static void SaveLocked()
    {
        try { SecretStore.WriteProtected(FilePath, JsonSerializer.SerializeToUtf8Bytes(_data, Json.Options)); }
        catch (Exception ex) { Log.Error("Saving receiver.json failed", ex); }
    }
}
