using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DualScreen.Core;

namespace DualScreen.Display;

/// <summary>
/// User-mode client for the DualScreen Indirect Display Driver. Talks to the driver through its device
/// interface with DeviceIoControl. The IOCTL contract is defined in virtual-display-driver/DualScreenIdd/Public.h.
/// </summary>
public sealed class DriverClient : IDisposable
{
    public static readonly Guid InterfaceGuid = new("7c0a3a5e-2f4b-4d8e-9a61-3d5c2b1f9e47");
    public const string HardwareId = @"Root\DualScreenIdd";
    public const int MaxModes = 32;
    public const uint ExpectedInterfaceVersion = 1;

    private const uint FILE_DEVICE_UNKNOWN = 0x22;
    private static uint Ctl(uint fn) => (FILE_DEVICE_UNKNOWN << 16) | (fn << 2);
    private static readonly uint IOCTL_GET_VERSION = Ctl(0x800);
    private static readonly uint IOCTL_ADD_MONITOR = Ctl(0x801);
    private static readonly uint IOCTL_REMOVE_MONITOR = Ctl(0x802);
    private static readonly uint IOCTL_REMOVE_ALL = Ctl(0x803);

    [StructLayout(LayoutKind.Sequential)]
    private struct DsMode { public uint Width, Height, RefreshHz; }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DsAddMonitor
    {
        public uint ConnectorIndex;
        public uint ModeCount;
        public uint PreferredMode;
        public fixed uint Modes[MaxModes * 3];
    }

    private readonly SafeFileHandle _handle;

    private DriverClient(SafeFileHandle handle) => _handle = handle;

    public static DriverClient? TryOpen()
    {
        string? path = FindInterfacePath();
        if (path == null) return null;
        var h = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var c = new DriverClient(h);
        try
        {
            uint v = c.GetVersion();
            if (v != ExpectedInterfaceVersion) Log.Warn($"Driver interface version {v}, expected {ExpectedInterfaceVersion}");
        }
        catch (Exception ex)
        {
            Log.Warn("Driver did not answer: " + ex.Message);
            c.Dispose();
            return null;
        }
        return c;
    }

    public static bool IsInstalled() => FindInterfacePath() != null;

    public unsafe uint GetVersion()
    {
        uint v = 0;
        Ioctl(IOCTL_GET_VERSION, null, 0, &v, sizeof(uint));
        return v;
    }

    /// <summary>Plugs a virtual monitor with the given mode list (preferred first). Windows sees it like a new HDMI display.</summary>
    public unsafe void AddMonitor(uint connector, (int W, int H, int Hz)[] modes, int preferredIndex)
    {
        if (modes.Length == 0) throw new ArgumentException("No modes");
        var req = new DsAddMonitor { ConnectorIndex = connector, ModeCount = (uint)Math.Min(modes.Length, MaxModes), PreferredMode = (uint)preferredIndex };
        for (int i = 0; i < req.ModeCount; i++)
        {
            req.Modes[i * 3] = (uint)modes[i].W;
            req.Modes[i * 3 + 1] = (uint)modes[i].H;
            req.Modes[i * 3 + 2] = (uint)modes[i].Hz;
        }
        Ioctl(IOCTL_ADD_MONITOR, &req, sizeof(DsAddMonitor), null, 0);
    }

    public unsafe void RemoveMonitor(uint connector) => Ioctl(IOCTL_REMOVE_MONITOR, &connector, sizeof(uint), null, 0);

    public unsafe void RemoveAll() => Ioctl(IOCTL_REMOVE_ALL, null, 0, null, 0);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, int inSize, void* outBuf, int outSize, out int returned, IntPtr overlapped);

    private unsafe void Ioctl(uint code, void* inBuf, int inSize, void* outBuf, int outSize)
    {
        if (!DeviceIoControl(_handle, code, inBuf, inSize, outBuf, outSize, out _, IntPtr.Zero))
            throw new IOException($"Driver IOCTL 0x{code:X} failed (Win32 error {Marshal.GetLastWin32Error()})");
    }

    private static unsafe string? FindInterfacePath()
    {
        Guid g = InterfaceGuid;
        IntPtr set = Native.SetupDiGetClassDevs(ref g, null, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == Native.INVALID_HANDLE_VALUE) return null;
        try
        {
            var data = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
            if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, 0, ref data)) return null;
            Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int required, IntPtr.Zero);
            IntPtr buf = Marshal.AllocHGlobal(required);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 on x64 (DWORD + WCHAR[1] padded).
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, buf, required, out _, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(buf + 4);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
    }

    public void Dispose() => _handle.Dispose();
}
