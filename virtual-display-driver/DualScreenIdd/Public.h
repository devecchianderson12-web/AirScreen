// Contract between DualScreen.exe (user mode) and the DualScreen Indirect Display Driver.
// Mirrored in windows/src/DualScreen/Display/DriverClient.cs — keep both in sync.
#pragma once

// {7C0A3A5E-2F4B-4D8E-9A61-3D5C2B1F9E47}
DEFINE_GUID(GUID_DEVINTERFACE_DUALSCREEN_IDD,
    0x7c0a3a5e, 0x2f4b, 0x4d8e, 0x9a, 0x61, 0x3d, 0x5c, 0x2b, 0x1f, 0x9e, 0x47);

#define DS_INTERFACE_VERSION 1
#define DS_MAX_MODES 32
#define DS_MAX_MONITORS 4

#define IOCTL_DS_GET_VERSION    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_DS_ADD_MONITOR    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_DS_REMOVE_MONITOR CTL_CODE(FILE_DEVICE_UNKNOWN, 0x802, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_DS_REMOVE_ALL     CTL_CODE(FILE_DEVICE_UNKNOWN, 0x803, METHOD_BUFFERED, FILE_ANY_ACCESS)

#pragma pack(push, 4)
typedef struct _DS_MODE {
    UINT32 Width;
    UINT32 Height;
    UINT32 RefreshHz;
} DS_MODE;

typedef struct _DS_ADD_MONITOR {
    UINT32 ConnectorIndex;   // 0..DS_MAX_MONITORS-1
    UINT32 ModeCount;        // 1..DS_MAX_MODES
    UINT32 PreferredMode;    // index into Modes
    DS_MODE Modes[DS_MAX_MODES];
} DS_ADD_MONITOR;
#pragma pack(pop)
