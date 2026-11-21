using System;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DualScreen.Core;

namespace DualScreen.Media;

/// <summary>One hardware D3D11 device shared by capture, colour conversion and the hardware encoder (zero-copy).</summary>
public sealed class D3DDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public ID3D11VideoDevice VideoDevice { get; }
    public ID3D11VideoContext VideoContext { get; }
    public long AdapterLuid { get; }
    public string AdapterName { get; }
    public uint VendorId { get; }

    public D3DDevice()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        // Adapter 0 owns the primary desktop; IDD monitors are rendered by it as well.
        factory.EnumAdapters1(0, out IDXGIAdapter1 adapter).CheckError();
        using (adapter)
        {
            var desc = adapter.Description1;
            AdapterLuid = desc.Luid;
            AdapterName = desc.Description;
            VendorId = desc.VendorId;
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, flags,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device? dev, out ID3D11DeviceContext? ctx).CheckError();
            Device = dev!;
            Context = ctx!;
        }
        using (var mt = Device.QueryInterface<ID3D11Multithread>())
            mt.SetMultithreadProtected(true);
        try
        {
            using var dxgiDev = Device.QueryInterface<IDXGIDevice>();
            dxgiDev.SetGPUThreadPriority(7); // highest normal priority: our frames are not starved by games/apps
        }
        catch { }
        VideoDevice = Device.QueryInterface<ID3D11VideoDevice>();
        VideoContext = Context.QueryInterface<ID3D11VideoContext>();
        Log.Info($"GPU: {AdapterName} (vendor 0x{VendorId:X4})");
    }

    public void Dispose()
    {
        VideoContext.Dispose();
        VideoDevice.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
