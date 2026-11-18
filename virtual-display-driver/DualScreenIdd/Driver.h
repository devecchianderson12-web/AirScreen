#pragma once

#define NOMINMAX
#include <windows.h>
#include <bugcodes.h>
#include <wudfwdm.h>
#include <wdf.h>
#include <iddcx.h>

#include <dxgi1_5.h>
#include <d3d11_2.h>
#include <avrt.h>
#include <wrl.h>
#include <initguid.h>

#include <array>
#include <atomic>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

#include "Public.h"

namespace DualScreen
{
    struct Mode
    {
        UINT32 Width;
        UINT32 Height;
        UINT32 RefreshHz;
    };

    /// Keeps the IddCx swap chain flowing. Frames are not read here: DualScreen.exe captures the
    /// virtual monitor with Windows.Graphics.Capture, so the driver only acquires and releases buffers.
    class SwapChainProcessor
    {
    public:
        SwapChainProcessor(IDDCX_SWAPCHAIN hSwapChain, LUID renderAdapter, HANDLE newFrameEvent);
        ~SwapChainProcessor();

    private:
        void Run();
        void RunCore();

        IDDCX_SWAPCHAIN m_hSwapChain;
        LUID m_adapterLuid;
        HANDLE m_hAvailableBufferEvent;
        Microsoft::WRL::Wrappers::Event m_hTerminateEvent;
        std::thread m_thread;
    };

    struct MonitorSlot
    {
        bool InUse = false;
        IDDCX_MONITOR Monitor = nullptr;
        std::vector<Mode> Modes;
        UINT32 Preferred = 0;
        std::array<BYTE, 128> Edid{};
    };

    class AdapterContext
    {
    public:
        explicit AdapterContext(WDFDEVICE device) : m_device(device) {}

        NTSTATUS InitAdapter();
        NTSTATUS AddMonitor(const DS_ADD_MONITOR& request);
        NTSTATUS RemoveMonitor(UINT32 connector);
        void RemoveAll();

        // Lookup used by the IddCx mode callbacks.
        bool ModesForEdid(const BYTE* edid, UINT32 size, std::vector<Mode>& modes, UINT32& preferred);
        bool ModesForMonitor(IDDCX_MONITOR monitor, std::vector<Mode>& modes);
        void OnMonitorGone(IDDCX_MONITOR monitor);

        IDDCX_ADAPTER Adapter = nullptr;

    private:
        WDFDEVICE m_device;
        std::mutex m_lock;
        std::array<MonitorSlot, DS_MAX_MONITORS> m_slots;
    };

    struct MonitorContext
    {
        IDDCX_MONITOR Monitor;
        std::unique_ptr<SwapChainProcessor> Processor;
    };
}

struct DeviceContextWrapper { DualScreen::AdapterContext* pContext; };
struct MonitorContextWrapper { DualScreen::MonitorContext* pContext; };
WDF_DECLARE_CONTEXT_TYPE(DeviceContextWrapper);
WDF_DECLARE_CONTEXT_TYPE(MonitorContextWrapper);
