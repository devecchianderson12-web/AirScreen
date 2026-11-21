using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace DualScreen.Core;

/// <summary>
/// Latency tuning that only applies while something is streaming (host or receiver), and is undone afterwards:
/// 1 ms timer resolution, Wi-Fi "media streaming mode" (no power-save naps or background scans, the main cause
/// of 50–200 ms Wi-Fi spikes) and MMCSS scheduling for the real-time threads.
/// </summary>
public static class Perf
{
    private static readonly object Gate = new();
    private static int _users;
    private static readonly List<Guid> _tunedWifi = new();

    /// <summary>Call when a stream starts; pair every call with <see cref="EndStreaming"/>.</summary>
    public static void BeginStreaming()
    {
        lock (Gate)
        {
            if (_users++ > 0) return;
            try { timeBeginPeriod(1); } catch { }
            SetWifiStreaming(true);
        }
    }

    public static void EndStreaming()
    {
        lock (Gate)
        {
            if (_users == 0 || --_users > 0) return;
            try { timeEndPeriod(1); } catch { }
            SetWifiStreaming(false);
        }
    }

    /// <summary>Registers the calling thread with MMCSS ("Games": high priority, no starvation). Returns a handle for <see cref="LeaveRealtime"/>.</summary>
    public static IntPtr EnterRealtime()
    {
        try
        {
            uint task = 0;
            IntPtr h = AvSetMmThreadCharacteristics("Games", ref task);
            if (h != IntPtr.Zero) AvSetMmThreadPriority(h, 1 /* AVRT_PRIORITY_HIGH */);
            return h;
        }
        catch { return IntPtr.Zero; }
    }

    public static void LeaveRealtime(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        try { AvRevertMmThreadCharacteristics(h); } catch { }
    }

    // ------------------------------------------------------------------ Wi-Fi

    private const int WLAN_INTF_OPCODE_BACKGROUND_SCAN_ENABLED = 2;
    private const int WLAN_INTF_OPCODE_MEDIA_STREAMING_MODE = 3;

    private static void SetWifiStreaming(bool on)
    {
        IntPtr client = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out client) != 0) return;
            if (on)
            {
                if (WlanEnumInterfaces(client, IntPtr.Zero, out list) != 0) return;
                int count = Marshal.ReadInt32(list);
                // WLAN_INTERFACE_INFO_LIST: dwNumberOfItems, dwIndex, then WLAN_INTERFACE_INFO[] (GUID + 256 WCHAR + state) = 532 bytes each.
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = list + 8 + i * 532;
                    var guid = Marshal.PtrToStructure<Guid>(item);
                    int state = Marshal.ReadInt32(item + 16 + 512);
                    if (state != 1 /* wlan_interface_state_connected */) continue;
                    if (SetBool(client, guid, WLAN_INTF_OPCODE_MEDIA_STREAMING_MODE, true)) _tunedWifi.Add(guid);
                    SetBool(client, guid, WLAN_INTF_OPCODE_BACKGROUND_SCAN_ENABLED, false);
                }
                if (_tunedWifi.Count > 0) Log.Info($"Wi-Fi media streaming mode on ({_tunedWifi.Count} adapter(s))");
            }
            else
            {
                foreach (var g in _tunedWifi)
                {
                    SetBool(client, g, WLAN_INTF_OPCODE_MEDIA_STREAMING_MODE, false);
                    SetBool(client, g, WLAN_INTF_OPCODE_BACKGROUND_SCAN_ENABLED, true);
                }
                _tunedWifi.Clear();
            }
        }
        catch (Exception ex) { Log.Info("Wi-Fi tuning unavailable: " + ex.Message); }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero);
        }
    }

    private static bool SetBool(IntPtr client, Guid iface, int opcode, bool value)
    {
        IntPtr p = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(p, value ? 1 : 0);
            return WlanSetInterface(client, ref iface, opcode, 4, p, IntPtr.Zero) == 0;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] private static extern IntPtr AvSetMmThreadCharacteristics(string task, ref uint index);
    [DllImport("avrt.dll")] private static extern bool AvSetMmThreadPriority(IntPtr h, int priority);
    [DllImport("avrt.dll")] private static extern bool AvRevertMmThreadCharacteristics(IntPtr h);
    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr client);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr client, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanSetInterface(IntPtr client, ref Guid iface, int opcode, int size, IntPtr data, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr p);
}
