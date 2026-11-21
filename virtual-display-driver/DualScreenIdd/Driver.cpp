// DualScreen Indirect Display Driver (UMDF 2 + IddCx).
//
// Exposes up to DS_MAX_MONITORS virtual monitors that Windows treats exactly like physically connected
// displays (they appear in Settings → System → Display, can be extended/duplicated, arranged, rotated,
// and accept any of the modes DualScreen.exe negotiated with the phone, including high refresh rates).
// Monitors are plugged/unplugged on demand through IOCTLs on GUID_DEVINTERFACE_DUALSCREEN_IDD.
//
// Structure follows Microsoft's IddSampleDriver (MIT licensed, Windows-driver-samples).

#include "Driver.h"

using namespace DualScreen;
using namespace Microsoft::WRL;

extern "C" DRIVER_INITIALIZE DriverEntry;

EVT_WDF_DRIVER_DEVICE_ADD DsDeviceAdd;
EVT_WDF_DEVICE_D0_ENTRY DsDeviceD0Entry;
EVT_IDD_CX_DEVICE_IO_CONTROL DsDeviceIoControl;
EVT_IDD_CX_ADAPTER_INIT_FINISHED DsAdapterInitFinished;
EVT_IDD_CX_ADAPTER_COMMIT_MODES DsAdapterCommitModes;
EVT_IDD_CX_PARSE_MONITOR_DESCRIPTION DsParseMonitorDescription;
EVT_IDD_CX_MONITOR_GET_DEFAULT_DESCRIPTION_MODES DsMonitorGetDefaultModes;
EVT_IDD_CX_MONITOR_QUERY_TARGET_MODES DsMonitorQueryModes;
EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN DsMonitorAssignSwapChain;
EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN DsMonitorUnassignSwapChain;
EVT_WDF_OBJECT_CONTEXT_CLEANUP DsMonitorCleanup;
static NTSTATUS DsParseMonitorDescriptionCore(const IDARG_IN_PARSEMONITORDESCRIPTION* pInArgs, IDARG_OUT_PARSEMONITORDESCRIPTION* pOutArgs);

// Most recently added monitor's modes: used for GetDefaultDescriptionModes (no EDID path).
static std::vector<Mode> g_lastModes = { { 1920, 1080, 60 } };

// One adapter per device node (the driver is installed as a single root-enumerated device).
static AdapterContext* g_adapter = nullptr;

// Diagnostics for IOCTL_DS_ADD_MONITOR (see DS_ADD_RESULT).
static std::atomic<UINT32> g_stage{ 0 }, g_parseCalls{ 0 }, g_queryCalls{ 0 };
static std::atomic<INT32> g_parseStatus{ 0 };

// ---------------------------------------------------------------------------------------------- EDID

// Minimal EDID 1.4 block: manufacturer "DSC", product 0x0001, serial = connector + 1, monitor name
// "DualScreen". No detailed timings: the modes come from EvtIddCxParseMonitorDescription.
static std::array<BYTE, 128> BuildEdid(UINT32 connector)
{
    std::array<BYTE, 128> e{};
    const BYTE header[8] = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
    memcpy(e.data(), header, 8);
    const UINT16 mfg = ((('D' - '@') & 0x1F) << 10) | ((('S' - '@') & 0x1F) << 5) | (('C' - '@') & 0x1F);
    e[8] = (BYTE)(mfg >> 8);
    e[9] = (BYTE)(mfg & 0xFF);
    e[10] = 0x01; e[11] = 0x00;                        // product code
    const UINT32 serial = connector + 1;
    memcpy(&e[12], &serial, 4);                        // serial number (little endian)
    e[16] = 1; e[17] = 35;                              // week 1, 2025
    e[18] = 1; e[19] = 4;                               // EDID 1.4
    e[20] = 0xA5;                                       // digital, 8 bpc, DisplayPort
    e[21] = 16; e[22] = 9;                              // aspect-ratio style size
    e[23] = 0x78;                                       // gamma 2.2
    e[24] = 0x06;                                       // RGB 4:4:4, sRGB default
    // chromaticity (sRGB)
    const BYTE chroma[10] = { 0xEE, 0x91, 0xA3, 0x54, 0x4C, 0x99, 0x26, 0x0F, 0x50, 0x54 };
    memcpy(&e[25], chroma, 10);
    for (int i = 38; i < 54; i += 2) { e[i] = 0x01; e[i + 1] = 0x01; } // no standard timings
    // Descriptor 1: monitor name
    BYTE* d = &e[54];
    d[3] = 0xFC;
    const char name[13] = { 'D','u','a','l','S','c','r','e','e','n','\n',' ',' ' };
    memcpy(&d[5], name, 13);
    // Descriptor 2: serial string
    d = &e[72];
    d[3] = 0xFF;
    char sn[13] = { 'D','S','-','0','0','0','0','\n',' ',' ',' ',' ',' ' };
    sn[6] = (char)('0' + (connector % 10));
    memcpy(&d[5], sn, 13);
    // Descriptors 3 and 4: dummy
    e[90 + 3] = 0x10;
    e[108 + 3] = 0x10;
    e[126] = 0; // no extensions
    BYTE sum = 0;
    for (int i = 0; i < 127; i++) sum += e[i];
    e[127] = (BYTE)(0x100 - sum);
    return e;
}

static UINT32 ConnectorFromEdid(const BYTE* edid, UINT32 size)
{
    if (edid == nullptr || size < 16 || edid[8] != 0x12 || edid[9] != 0x63) return UINT32_MAX;
    UINT32 serial = 0;
    memcpy(&serial, &edid[12], 4);
    return serial == 0 ? UINT32_MAX : serial - 1;
}

// ---------------------------------------------------------------------------------------------- modes

static DISPLAYCONFIG_VIDEO_SIGNAL_INFO SignalInfo(const Mode& m)
{
    DISPLAYCONFIG_VIDEO_SIGNAL_INFO s = {};
    s.totalSize.cx = s.activeSize.cx = m.Width;
    s.totalSize.cy = s.activeSize.cy = m.Height;
    s.AdditionalSignalInfo.videoStandard = 255;
    s.AdditionalSignalInfo.vSyncFreqDivider = 1;
    s.vSyncFreq.Numerator = m.RefreshHz;
    s.vSyncFreq.Denominator = 1;
    s.hSyncFreq.Numerator = m.RefreshHz * m.Height;
    s.hSyncFreq.Denominator = 1;
    s.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
    s.pixelRate = (UINT64)m.RefreshHz * m.Width * m.Height;
    return s;
}

static IDDCX_MONITOR_MODE MonitorMode(const Mode& m, IDDCX_MONITOR_MODE_ORIGIN origin)
{
    IDDCX_MONITOR_MODE mode = {};
    mode.Size = sizeof(mode);
    mode.Origin = origin;
    mode.MonitorVideoSignalInfo = SignalInfo(m);
    return mode;
}

static IDDCX_TARGET_MODE TargetMode(const Mode& m)
{
    IDDCX_TARGET_MODE mode = {};
    mode.Size = sizeof(mode);
    mode.TargetVideoSignalInfo.targetVideoSignalInfo = SignalInfo(m);
    return mode;
}

// ---------------------------------------------------------------------------------------------- driver

extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT pDriverObject, PUNICODE_STRING pRegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    WDF_DRIVER_CONFIG_INIT(&config, DsDeviceAdd);
    return WdfDriverCreate(pDriverObject, pRegistryPath, &attributes, &config, WDF_NO_HANDLE);
}

_Use_decl_annotations_
NTSTATUS DsDeviceAdd(WDFDRIVER Driver, PWDFDEVICE_INIT pDeviceInit)
{
    UNREFERENCED_PARAMETER(Driver);

    WDF_PNPPOWER_EVENT_CALLBACKS pnp;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnp);
    pnp.EvtDeviceD0Entry = DsDeviceD0Entry;
    WdfDeviceInitSetPnpPowerEventCallbacks(pDeviceInit, &pnp);

    IDD_CX_CLIENT_CONFIG cfg;
    IDD_CX_CLIENT_CONFIG_INIT(&cfg);
    cfg.EvtIddCxDeviceIoControl = DsDeviceIoControl;
    cfg.EvtIddCxAdapterInitFinished = DsAdapterInitFinished;
    cfg.EvtIddCxParseMonitorDescription = DsParseMonitorDescription;
    cfg.EvtIddCxMonitorGetDefaultDescriptionModes = DsMonitorGetDefaultModes;
    cfg.EvtIddCxMonitorQueryTargetModes = DsMonitorQueryModes;
    cfg.EvtIddCxAdapterCommitModes = DsAdapterCommitModes;
    cfg.EvtIddCxMonitorAssignSwapChain = DsMonitorAssignSwapChain;
    cfg.EvtIddCxMonitorUnassignSwapChain = DsMonitorUnassignSwapChain;

    NTSTATUS status = IddCxDeviceInitConfig(pDeviceInit, &cfg);
    if (!NT_SUCCESS(status)) return status;

    WDF_OBJECT_ATTRIBUTES attr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attr, DeviceContextWrapper);
    attr.EvtCleanupCallback = [](WDFOBJECT obj)
    {
        auto* w = WdfObjectGet_DeviceContextWrapper(obj);
        if (w && w->pContext) { if (g_adapter == w->pContext) g_adapter = nullptr; delete w->pContext; w->pContext = nullptr; }
    };

    WDFDEVICE device = nullptr;
    status = WdfDeviceCreate(&pDeviceInit, &attr, &device);
    if (!NT_SUCCESS(status)) return status;

    status = IddCxDeviceInitialize(device);
    if (!NT_SUCCESS(status)) return status;

    status = WdfDeviceCreateDeviceInterface(device, &GUID_DEVINTERFACE_DUALSCREEN_IDD, nullptr);
    if (!NT_SUCCESS(status)) return status;

    g_adapter = new AdapterContext(device);
    WdfObjectGet_DeviceContextWrapper(device)->pContext = g_adapter;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS DsDeviceD0Entry(WDFDEVICE Device, WDF_POWER_DEVICE_STATE PreviousState)
{
    UNREFERENCED_PARAMETER(PreviousState);
    return WdfObjectGet_DeviceContextWrapper(Device)->pContext->InitAdapter();
}

NTSTATUS AdapterContext::InitAdapter()
{
    if (Adapter != nullptr) return STATUS_SUCCESS;

    IDDCX_ADAPTER_CAPS caps = {};
    caps.Size = sizeof(caps);
    caps.MaxMonitorsSupported = DS_MAX_MONITORS;
    caps.EndPointDiagnostics.Size = sizeof(caps.EndPointDiagnostics);
    caps.EndPointDiagnostics.GammaSupport = IDDCX_FEATURE_IMPLEMENTATION_NONE;
    caps.EndPointDiagnostics.TransmissionType = IDDCX_TRANSMISSION_TYPE_WIRELESS_OTHER;
    caps.EndPointDiagnostics.pEndPointFriendlyName = L"DualScreen";
    caps.EndPointDiagnostics.pEndPointManufacturerName = L"DualScreen";
    caps.EndPointDiagnostics.pEndPointModelName = L"Android wireless display";
    IDDCX_ENDPOINT_VERSION version = {};
    version.Size = sizeof(version);
    version.MajorVer = 1;
    caps.EndPointDiagnostics.pFirmwareVersion = &version;
    caps.EndPointDiagnostics.pHardwareVersion = &version;

    WDF_OBJECT_ATTRIBUTES attr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attr, DeviceContextWrapper);

    IDARG_IN_ADAPTER_INIT in = {};
    in.WdfDevice = m_device;
    in.pCaps = &caps;
    in.ObjectAttributes = &attr;

    IDARG_OUT_ADAPTER_INIT out = {};
    NTSTATUS status = IddCxAdapterInitAsync(&in, &out);
    if (NT_SUCCESS(status))
    {
        Adapter = out.AdapterObject;
        WdfObjectGet_DeviceContextWrapper(out.AdapterObject)->pContext = this;
    }
    return status;
}

_Use_decl_annotations_
NTSTATUS DsAdapterInitFinished(IDDCX_ADAPTER AdapterObject, const IDARG_IN_ADAPTER_INIT_FINISHED* pInArgs)
{
    UNREFERENCED_PARAMETER(AdapterObject);
    // Monitors are created on demand by DualScreen.exe (IOCTL_DS_ADD_MONITOR), not at start-up.
    return pInArgs->AdapterInitStatus;
}

_Use_decl_annotations_
NTSTATUS DsAdapterCommitModes(IDDCX_ADAPTER AdapterObject, const IDARG_IN_COMMITMODES* pInArgs)
{
    UNREFERENCED_PARAMETER(AdapterObject);
    UNREFERENCED_PARAMETER(pInArgs);
    return STATUS_SUCCESS;
}

// ---------------------------------------------------------------------------------------------- monitors

NTSTATUS AdapterContext::AddMonitor(const DS_ADD_MONITOR& req)
{
    g_stage = 5;
    if (Adapter == nullptr) return STATUS_DEVICE_NOT_READY;
    g_stage = 1;
    if (req.ConnectorIndex >= DS_MAX_MONITORS || req.ModeCount == 0 || req.ModeCount > DS_MAX_MODES) return STATUS_INVALID_PARAMETER;

    std::vector<Mode> modes;
    for (UINT32 i = 0; i < req.ModeCount; i++)
    {
        const DS_MODE& m = req.Modes[i];
        if (m.Width < 640 || m.Height < 360 || m.Width > 7680 || m.Height > 4320 || m.RefreshHz < 24 || m.RefreshHz > 500)
            return STATUS_INVALID_PARAMETER;
        modes.push_back({ m.Width, m.Height, m.RefreshHz });
    }

    MonitorSlot& slot = m_slots[req.ConnectorIndex];
    {
        // Reserve the slot under the lock, but release it before calling IddCx: monitor creation and
        // arrival call back into ParseMonitorDescription/QueryTargetModes on this same thread, and those
        // take m_lock too (std::mutex is not recursive: re-locking would terminate the driver host).
        std::lock_guard<std::mutex> lock(m_lock);
        g_stage = 2;
        if (slot.InUse) return STATUS_DEVICE_ALREADY_ATTACHED;
        slot.InUse = true;
        slot.Monitor = nullptr;
        slot.Modes = modes;
        slot.Preferred = req.PreferredMode < modes.size() ? req.PreferredMode : 0;
        slot.Edid = BuildEdid(req.ConnectorIndex);
        g_lastModes = modes;
    }
    auto release = [&]()
    {
        std::lock_guard<std::mutex> lock(m_lock);
        slot.InUse = false;
        slot.Monitor = nullptr;
    };

    WDF_OBJECT_ATTRIBUTES attr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attr, MonitorContextWrapper);
    attr.EvtCleanupCallback = DsMonitorCleanup;

    IDDCX_MONITOR_INFO info = {};
    info.Size = sizeof(info);
    info.MonitorType = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL;
    info.ConnectorIndex = req.ConnectorIndex;
    info.MonitorDescription.Size = sizeof(info.MonitorDescription);
    info.MonitorDescription.Type = IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
    info.MonitorDescription.DataSize = (UINT)slot.Edid.size();
    info.MonitorDescription.pData = slot.Edid.data();
    // Stable container id per connector so Windows remembers position/scale of the phone display.
    info.MonitorContainerId = { 0x44530000u | req.ConnectorIndex, 0x5343, 0x4E52, { 0x8A, 0x1E, 0x44, 0x75, 0x61, 0x6C, 0x53, 0x63 } };

    IDARG_IN_MONITORCREATE in = {};
    in.ObjectAttributes = &attr;
    in.pMonitorInfo = &info;
    IDARG_OUT_MONITORCREATE out = {};
    g_stage = 3;
    NTSTATUS status = IddCxMonitorCreate(Adapter, &in, &out);
    if (!NT_SUCCESS(status)) { release(); return status; }

    auto* ctx = new MonitorContext{ out.MonitorObject, nullptr };
    WdfObjectGet_MonitorContextWrapper(out.MonitorObject)->pContext = ctx;
    {
        std::lock_guard<std::mutex> lock(m_lock);
        slot.Monitor = out.MonitorObject; // QueryTargetModes during arrival can now find this monitor's modes
    }

    IDARG_OUT_MONITORARRIVAL arrival = {};
    g_stage = 4;
    status = IddCxMonitorArrival(out.MonitorObject, &arrival);
    if (!NT_SUCCESS(status))
    {
        release();
        WdfObjectDelete(out.MonitorObject);
        return status;
    }
    g_stage = 0;
    return STATUS_SUCCESS;
}

NTSTATUS AdapterContext::RemoveMonitor(UINT32 connector)
{
    if (connector >= DS_MAX_MONITORS) return STATUS_INVALID_PARAMETER;
    IDDCX_MONITOR monitor = nullptr;
    {
        std::lock_guard<std::mutex> lock(m_lock);
        MonitorSlot& slot = m_slots[connector];
        if (!slot.InUse) return STATUS_SUCCESS;
        monitor = slot.Monitor;
        slot.InUse = false;
        slot.Monitor = nullptr;
    }
    return monitor ? IddCxMonitorDeparture(monitor) : STATUS_SUCCESS;
}

void AdapterContext::RemoveAll()
{
    for (UINT32 i = 0; i < DS_MAX_MONITORS; i++) RemoveMonitor(i);
}

bool AdapterContext::ModesForEdid(const BYTE* edid, UINT32 size, std::vector<Mode>& modes, UINT32& preferred)
{
    UINT32 c = ConnectorFromEdid(edid, size);
    std::lock_guard<std::mutex> lock(m_lock);
    if (c >= DS_MAX_MONITORS || m_slots[c].Modes.empty()) return false;
    modes = m_slots[c].Modes;
    preferred = m_slots[c].Preferred;
    return true;
}

bool AdapterContext::ModesForMonitor(IDDCX_MONITOR monitor, std::vector<Mode>& modes)
{
    std::lock_guard<std::mutex> lock(m_lock);
    for (auto& s : m_slots)
    {
        if (s.Monitor == monitor && !s.Modes.empty()) { modes = s.Modes; return true; }
    }
    return false;
}

_Use_decl_annotations_
void DsMonitorCleanup(WDFOBJECT Object)
{
    auto* w = WdfObjectGet_MonitorContextWrapper(Object);
    if (w && w->pContext) { delete w->pContext; w->pContext = nullptr; }
}

// The OS asks which modes the monitor supports (from its EDID).
_Use_decl_annotations_
NTSTATUS DsParseMonitorDescription(const IDARG_IN_PARSEMONITORDESCRIPTION* pInArgs, IDARG_OUT_PARSEMONITORDESCRIPTION* pOutArgs)
{
    g_parseCalls++;
    NTSTATUS st = DsParseMonitorDescriptionCore(pInArgs, pOutArgs);
    g_parseStatus = st;
    return st;
}

NTSTATUS DsParseMonitorDescriptionCore(const IDARG_IN_PARSEMONITORDESCRIPTION* pInArgs, IDARG_OUT_PARSEMONITORDESCRIPTION* pOutArgs)
{
    std::vector<Mode> modes;
    UINT32 preferred = 0;
    // The adapter context is a singleton per device; find it through any monitor-independent path.
    if (g_adapter == nullptr || !g_adapter->ModesForEdid((const BYTE*)pInArgs->MonitorDescription.pData,
                                                        pInArgs->MonitorDescription.DataSize, modes, preferred))
        modes = g_lastModes;

    pOutArgs->MonitorModeBufferOutputCount = (UINT)modes.size();
    if (pInArgs->MonitorModeBufferInputCount < modes.size())
        return pInArgs->MonitorModeBufferInputCount > 0 ? STATUS_BUFFER_TOO_SMALL : STATUS_SUCCESS;

    for (size_t i = 0; i < modes.size(); i++)
        pInArgs->pMonitorModes[i] = MonitorMode(modes[i], IDDCX_MONITOR_MODE_ORIGIN_MONITORDESCRIPTOR);
    pOutArgs->PreferredMonitorModeIdx = preferred;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS DsMonitorGetDefaultModes(IDDCX_MONITOR MonitorObject, const IDARG_IN_GETDEFAULTDESCRIPTIONMODES* pInArgs, IDARG_OUT_GETDEFAULTDESCRIPTIONMODES* pOutArgs)
{
    UNREFERENCED_PARAMETER(MonitorObject);
    const std::vector<Mode>& modes = g_lastModes;
    pOutArgs->DefaultMonitorModeBufferOutputCount = (UINT)modes.size();
    if (pInArgs->DefaultMonitorModeBufferInputCount < modes.size())
        return pInArgs->DefaultMonitorModeBufferInputCount > 0 ? STATUS_BUFFER_TOO_SMALL : STATUS_SUCCESS;
    for (size_t i = 0; i < modes.size(); i++)
        pInArgs->pDefaultMonitorModes[i] = MonitorMode(modes[i], IDDCX_MONITOR_MODE_ORIGIN_DRIVER);
    pOutArgs->PreferredMonitorModeIdx = 0;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS DsMonitorQueryModes(IDDCX_MONITOR MonitorObject, const IDARG_IN_QUERYTARGETMODES* pInArgs, IDARG_OUT_QUERYTARGETMODES* pOutArgs)
{
    g_queryCalls++;
    std::vector<Mode> modes;
    if (g_adapter == nullptr || !g_adapter->ModesForMonitor(MonitorObject, modes)) modes = g_lastModes;

    pOutArgs->TargetModeBufferOutputCount = (UINT)modes.size();
    if (pInArgs->TargetModeBufferInputCount >= modes.size())
    {
        for (size_t i = 0; i < modes.size(); i++) pInArgs->pTargetModes[i] = TargetMode(modes[i]);
    }
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS DsMonitorAssignSwapChain(IDDCX_MONITOR MonitorObject, const IDARG_IN_SETSWAPCHAIN* pInArgs)
{
    auto* ctx = WdfObjectGet_MonitorContextWrapper(MonitorObject)->pContext;
    ctx->Processor.reset();
    ctx->Processor = std::make_unique<SwapChainProcessor>(pInArgs->hSwapChain, pInArgs->RenderAdapterLuid, pInArgs->hNextSurfaceAvailable);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS DsMonitorUnassignSwapChain(IDDCX_MONITOR MonitorObject)
{
    WdfObjectGet_MonitorContextWrapper(MonitorObject)->pContext->Processor.reset();
    return STATUS_SUCCESS;
}

// ---------------------------------------------------------------------------------------------- IOCTL

_Use_decl_annotations_
VOID DsDeviceIoControl(WDFDEVICE Device, WDFREQUEST Request, size_t OutputBufferLength, size_t InputBufferLength, ULONG IoControlCode)
{
    UNREFERENCED_PARAMETER(InputBufferLength);
    AdapterContext* ctx = WdfObjectGet_DeviceContextWrapper(Device)->pContext;
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR info = 0;

    switch (IoControlCode)
    {
    case IOCTL_DS_GET_VERSION:
    {
        UINT32* out = nullptr;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(UINT32), (PVOID*)&out, nullptr);
        if (NT_SUCCESS(status)) { *out = DS_INTERFACE_VERSION; info = sizeof(UINT32); }
        break;
    }
    case IOCTL_DS_ADD_MONITOR:
    {
        DS_ADD_MONITOR* in = nullptr;
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(DS_ADD_MONITOR), (PVOID*)&in, nullptr);
        if (NT_SUCCESS(status))
        {
            g_parseCalls = 0; g_queryCalls = 0; g_parseStatus = 0;
            status = ctx->AddMonitor(*in);
            DS_ADD_RESULT* res = nullptr;
            if (OutputBufferLength >= sizeof(DS_ADD_RESULT) &&
                NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, sizeof(DS_ADD_RESULT), (PVOID*)&res, nullptr)))
            {
                res->Stage = NT_SUCCESS(status) ? 0 : g_stage.load();
                res->Status = status;
                res->ParseCalls = g_parseCalls.load();
                res->ParseStatus = g_parseStatus.load();
                res->QueryCalls = g_queryCalls.load();
                info = sizeof(DS_ADD_RESULT);
                status = STATUS_SUCCESS;
            }
        }
        break;
    }
    case IOCTL_DS_REMOVE_MONITOR:
    {
        UINT32* in = nullptr;
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(UINT32), (PVOID*)&in, nullptr);
        if (NT_SUCCESS(status)) status = ctx->RemoveMonitor(*in);
        break;
    }
    case IOCTL_DS_REMOVE_ALL:
        ctx->RemoveAll();
        status = STATUS_SUCCESS;
        break;
    }
    WdfRequestCompleteWithInformation(Request, status, info);
}

// ---------------------------------------------------------------------------------------------- swap chain

SwapChainProcessor::SwapChainProcessor(IDDCX_SWAPCHAIN hSwapChain, LUID renderAdapter, HANDLE newFrameEvent)
    : m_hSwapChain(hSwapChain), m_adapterLuid(renderAdapter), m_hAvailableBufferEvent(newFrameEvent)
{
    m_hTerminateEvent.Attach(CreateEvent(nullptr, FALSE, FALSE, nullptr));
    m_thread = std::thread([this] { Run(); });
}

SwapChainProcessor::~SwapChainProcessor()
{
    SetEvent(m_hTerminateEvent.Get());
    if (m_thread.joinable()) m_thread.join();
}

void SwapChainProcessor::Run()
{
    DWORD taskIndex = 0;
    HANDLE avTask = AvSetMmThreadCharacteristicsW(L"Distribution", &taskIndex);
    RunCore();
    // The swap chain must be deleted when processing ends so the OS can create a new one.
    WdfObjectDelete((WDFOBJECT)m_hSwapChain);
    m_hSwapChain = nullptr;
    if (avTask) AvRevertMmThreadCharacteristics(avTask);
}

void SwapChainProcessor::RunCore()
{
    ComPtr<IDXGIFactory5> factory;
    if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)))) return;
    ComPtr<IDXGIAdapter1> adapter;
    if (FAILED(factory->EnumAdapterByLuid(m_adapterLuid, IID_PPV_ARGS(&adapter)))) return;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    if (FAILED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                 nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context)))
        return;
    ComPtr<IDXGIDevice> dxgiDevice;
    if (FAILED(device.As(&dxgiDevice))) return;

    IDARG_IN_SWAPCHAINSETDEVICE setDevice = {};
    setDevice.pDevice = dxgiDevice.Get();
    if (FAILED(IddCxSwapChainSetDevice(m_hSwapChain, &setDevice))) return;

    for (;;)
    {
        ComPtr<IDXGIResource> buffer;
        IDARG_OUT_RELEASEANDACQUIREBUFFER out = {};
        HRESULT hr = IddCxSwapChainReleaseAndAcquireBuffer(m_hSwapChain, &out);
        if (hr == E_PENDING)
        {
            HANDLE waits[] = { m_hAvailableBufferEvent, m_hTerminateEvent.Get() };
            DWORD w = WaitForMultipleObjects(ARRAYSIZE(waits), waits, FALSE, 16);
            if (w == WAIT_OBJECT_0 || w == WAIT_TIMEOUT) continue;
            break; // terminate requested
        }
        if (FAILED(hr)) break; // swap chain lost; the OS will assign a new one
        buffer.Attach(out.MetaData.pSurface);
        buffer.Reset();
        hr = IddCxSwapChainFinishedProcessingFrame(m_hSwapChain);
        if (FAILED(hr)) break;
    }
}
