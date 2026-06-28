// TFFAFilter: HID class upper filter for Logitech Trueforce-enabled wheels.
//
// We install as a HIDClass upper filter, which means PnP attaches us to every
// HID device on the system. In EvtDriverDeviceAdd we read the device's
// hardware ID and only attach to Logitech wheels with one of our target PIDs.
// For every other HID device (mice, keyboards, headsets, generic gamepads,
// etc.) we return STATUS_NOT_SUPPORTED, and the framework leaves us out of
// that device's stack so unrelated devices see zero overhead.
//
// Ownership + inverted-call IOCTL channel. A control device is exposed at
// \\?\TFFAControl. The plugin (or any test app) opens it; that handle's
// owning process becomes the active wheel owner (first-claim-wins). While
// an owner is set, writes from the owner pass through to the wheel
// normally, and writes from any other process are intercepted: their bytes
// get delivered to the plugin via the inverted-call IOCTL_TFFA_RECV
// (caller posts an output buffer; we complete it when intercepted bytes
// arrive), and the original write is completed with success without
// reaching the wheel. The plugin combines the intercepted game FFB with
// its own effects and writes the merged output back via its normal HID
// WriteFile (which passes through us because the plugin IS the owner).
// When the plugin closes its control handle (clean exit OR crash; Windows
// closes all handles on process death), ownership is released and the
// driver reverts to pure passthrough. Safe default: no owner means every
// write reaches the wheel as before.
//
// The legacy registry-based ProtectedPid is retained as a fallback for
// scripted spike testing; it only takes effect when no control-device
// owner is currently set.
//
// Log lines go to the kernel debug stream via DbgPrintEx at ERROR level
// (which bypasses the default DbgPrint informational-message filter).
// View them with Sysinternals DebugView (run as admin; enable "Capture
// Kernel" and "Enable Verbose Kernel Output" under the Capture menu).

#include <ntddk.h>
#include <wdf.h>
#include <hidport.h>

#define TFFA_LOG(...) DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_ERROR_LEVEL, __VA_ARGS__)
#define TFFA_TAG      ((ULONG)'AFFT')

DRIVER_INITIALIZE                       DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD               TFFAEvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL TFFAEvtIoInternalDeviceControl;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL      TFFAEvtIoDeviceControl;
EVT_WDF_IO_QUEUE_IO_WRITE               TFFAEvtIoWrite;
EVT_WDF_IO_QUEUE_IO_DEFAULT             TFFAEvtIoDefault;
EVT_WDF_DEVICE_FILE_CREATE              TFFAEvtControlFileCreate;
EVT_WDF_FILE_CLEANUP                    TFFAEvtControlFileCleanup;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL      TFFAEvtControlIoDeviceControl;

// Forward declaration so TFFADispatchIoctl can call into the registry-fallback
// helper that's physically defined further down in the file.
static ULONG TFFAGetProtectedPid(VOID);

// Per-file-handle context. Records which PID claimed ownership through THIS
// specific handle so the cleanup callback can decide whether to release the
// global g_OwnerPid. Required to handle rapid restart: if a new SimHub
// process claims ownership before the old one's handle cleanup has fired,
// the old close MUST NOT clear ownership (which would belong to the new
// SimHub by then). We compare this per-file PID to g_OwnerPid in cleanup.
typedef struct _TFFA_FILE_CONTEXT {
    ULONG ClaimingPid;
} TFFA_FILE_CONTEXT, *PTFFA_FILE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(TFFA_FILE_CONTEXT, TFFAGetFileContext)

// Control device ownership state. Single-owner; no per-device tracking yet
// because the plugin only needs one logical ownership over all the wheel's
// HID interfaces simultaneously. Accessed lock-free for reads (snapshot the
// ULONG); writes serialized through TFFAOwnerLock. Atomic ULONG would also
// work; spinlock is clearer and the contention is essentially zero.
static WDFDEVICE   g_ControlDevice    = NULL;
static WDFQUEUE    g_InterceptedQueue = NULL;  // manual queue for pending RECV
static WDFSPINLOCK g_OwnerLock        = NULL;
static ULONG       g_OwnerPid         = 0;     // 0 = no owner

// IOCTL codes (control device only). Use FILE_DEVICE_UNKNOWN per WDF
// guidance for custom devices. METHOD_BUFFERED is fine for our small
// payloads.
#define TFFA_IOCTL(code)        CTL_CODE(FILE_DEVICE_UNKNOWN, (code), METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_TFFA_PING         TFFA_IOCTL(0x800)
#define IOCTL_TFFA_RECV         TFFA_IOCTL(0x801)
#define TFFA_PING_MAGIC         0x54464641UL    // 'TFFA' in ASCII

// ---------- Target wheels --------------------------------------------------
//
// Hardware-ID substrings (case-insensitive) we will attach to. Matches the
// user-mode plugin's WheelDiscovery.SupportedPids list. To add a new wheel
// PID later, just append to this table; no INF change required (the INF
// installs us as a class filter on all HID devices, so adding a PID here is
// the only change needed for runtime gating).

typedef struct _TFFA_TARGET_WHEEL {
    PCWSTR HardwareIdSubstring;     // e.g. L"VID_046D&PID_C272"
    PCWSTR Model;                    // human-readable, for log lines
} TFFA_TARGET_WHEEL;

static const TFFA_TARGET_WHEEL g_TargetWheels[] = {
    { L"VID_046D&PID_C272", L"Logitech G PRO (Xbox/PC)"      },
    { L"VID_046D&PID_C268", L"Logitech G PRO (PS/PC)"        },
    { L"VID_046D&PID_C266", L"Logitech G923 (PS/PC)"         },
    { L"VID_046D&PID_C26D", L"Logitech G923 (Xbox/PC)"       },
    { L"VID_046D&PID_C26E", L"Logitech G923 (Xbox/PC, B)"    },
    { L"VID_046D&PID_C276", L"Logitech RS50"                 },
};

// Case-insensitive wcsstr-style search. Some kernel headers lack _wcsnicmp /
// wcsstr, so we roll a small one for the hardware-id matching path.
static BOOLEAN
TFFAFindSubstringNoCase(_In_ PCWSTR haystack, _In_ PCWSTR needle)
{
    if (haystack == NULL || needle == NULL || *needle == L'\0') {
        return FALSE;
    }
    for (PCWSTR h = haystack; *h != L'\0'; ++h) {
        PCWSTR a = h;
        PCWSTR b = needle;
        while (*a != L'\0' && *b != L'\0') {
            WCHAR ca = (*a >= L'a' && *a <= L'z') ? (WCHAR)(*a - 32) : *a;
            WCHAR cb = (*b >= L'a' && *b <= L'z') ? (WCHAR)(*b - 32) : *b;
            if (ca != cb) break;
            ++a; ++b;
        }
        if (*b == L'\0') return TRUE;
    }
    return FALSE;
}

// Walk the REG_MULTI_SZ hardware-id list and return the matching wheel entry,
// or NULL if none of our target PIDs appear in the list.
static const TFFA_TARGET_WHEEL *
TFFAMatchHardwareId(_In_ PCWSTR hwIdMultiSz, _In_ size_t byteSize)
{
    if (hwIdMultiSz == NULL || byteSize < sizeof(WCHAR)) {
        return NULL;
    }
    // Walk null-terminated strings until we hit a double-null or end of buffer.
    size_t maxChars = byteSize / sizeof(WCHAR);
    size_t i = 0;
    while (i < maxChars && hwIdMultiSz[i] != L'\0') {
        PCWSTR thisId = &hwIdMultiSz[i];
        for (ULONG w = 0; w < ARRAYSIZE(g_TargetWheels); ++w) {
            if (TFFAFindSubstringNoCase(thisId, g_TargetWheels[w].HardwareIdSubstring)) {
                return &g_TargetWheels[w];
            }
        }
        // Advance past this string + its terminator.
        while (i < maxChars && hwIdMultiSz[i] != L'\0') ++i;
        ++i;
    }
    return NULL;
}

// ---------- Driver entry / device add --------------------------------------

// Create the singleton control device + manual RECV queue. Called once
// from DriverEntry after the WDFDRIVER object exists. The control device is
// exposed at the legacy symbolic link \\?\TFFAControl so PowerShell or any
// CreateFile-capable app can open it.
static NTSTATUS
TFFACreateControlDevice(_In_ WDFDRIVER Driver)
{
    DECLARE_CONST_UNICODE_STRING(deviceName, L"\\Device\\TFFAControl");
    DECLARE_CONST_UNICODE_STRING(symLink,    L"\\DosDevices\\TFFAControl");
    // SDDL: SYSTEM all access (GA) + Administrators all access (GA) +
    // Builtin Users all access (GA). The plugin runs in the user's session
    // (SimHub usually runs non-elevated) so we have to allow Users to open
    // the device; otherwise the plugin gets ACCESS_DENIED on CreateFile.
    // For shipping we'd narrow Users to GR|GW (read+write only) and consider
    // restricting CLAIM-equivalent ownership to authenticated logons; for the
    // dev spike full Users access is fine.
    DECLARE_CONST_UNICODE_STRING(sddlOpen, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;BU)");

    PWDFDEVICE_INIT pInit = WdfControlDeviceInitAllocate(Driver, &sddlOpen);
    if (pInit == NULL) {
        TFFA_LOG("TFFAFilter: control device init alloc failed\n");
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    NTSTATUS status = WdfDeviceInitAssignName(pInit, &deviceName);
    if (!NT_SUCCESS(status)) {
        WdfDeviceInitFree(pInit);
        return status;
    }
    WdfDeviceInitSetExclusive(pInit, FALSE);

    // Enable file create/cleanup events so we can track which process owns
    // the open handle (= active owner). Attach a per-file context to record
    // which PID claimed ownership through each handle - cleanup needs this
    // to avoid an old handle's close wiping out a newer takeover handle's
    // ownership.
    WDF_FILEOBJECT_CONFIG fileConfig;
    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig,
        TFFAEvtControlFileCreate,
        TFFAEvtControlFileCleanup,
        WDF_NO_EVENT_CALLBACK);
    WDF_OBJECT_ATTRIBUTES fileAttrs;
    WDF_OBJECT_ATTRIBUTES_INIT(&fileAttrs);
    WDF_OBJECT_ATTRIBUTES_SET_CONTEXT_TYPE(&fileAttrs, TFFA_FILE_CONTEXT);
    WdfDeviceInitSetFileObjectConfig(pInit, &fileConfig, &fileAttrs);

    WDFDEVICE controlDev;
    status = WdfDeviceCreate(&pInit, WDF_NO_OBJECT_ATTRIBUTES, &controlDev);
    if (!NT_SUCCESS(status)) {
        // WdfDeviceCreate consumes pInit on both success AND failure paths;
        // MS docs explicitly say "do not call WdfDeviceInitFree" after
        // WdfDeviceCreate returns.
        TFFA_LOG("TFFAFilter: control device create failed 0x%X\n", status);
        return status;
    }

    status = WdfDeviceCreateSymbolicLink(controlDev, &symLink);
    if (!NT_SUCCESS(status)) {
        TFFA_LOG("TFFAFilter: control symlink failed 0x%X\n", status);
        return status;
    }

    // Default queue: handles incoming IOCTLs (PING, RECV).
    WDF_IO_QUEUE_CONFIG defaultQueueConfig;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&defaultQueueConfig, WdfIoQueueDispatchParallel);
    defaultQueueConfig.EvtIoDeviceControl = TFFAEvtControlIoDeviceControl;
    status = WdfIoQueueCreate(controlDev, &defaultQueueConfig, WDF_NO_OBJECT_ATTRIBUTES, NULL);
    if (!NT_SUCCESS(status)) {
        TFFA_LOG("TFFAFilter: control default queue failed 0x%X\n", status);
        return status;
    }

    // Manual queue: RECV requests park here until an intercepted write
    // arrives to fill them.
    WDF_IO_QUEUE_CONFIG manualConfig;
    WDF_IO_QUEUE_CONFIG_INIT(&manualConfig, WdfIoQueueDispatchManual);
    status = WdfIoQueueCreate(controlDev, &manualConfig, WDF_NO_OBJECT_ATTRIBUTES, &g_InterceptedQueue);
    if (!NT_SUCCESS(status)) {
        TFFA_LOG("TFFAFilter: control manual queue failed 0x%X\n", status);
        return status;
    }

    // Ownership lock. Allocated on the control device so it's torn down
    // when the device is.
    WDF_OBJECT_ATTRIBUTES lockAttrs;
    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttrs);
    lockAttrs.ParentObject = controlDev;
    status = WdfSpinLockCreate(&lockAttrs, &g_OwnerLock);
    if (!NT_SUCCESS(status)) {
        TFFA_LOG("TFFAFilter: ownership spinlock failed 0x%X\n", status);
        return status;
    }

    WdfControlFinishInitializing(controlDev);
    g_ControlDevice = controlDev;
    TFFA_LOG("TFFAFilter: control device ready at \\\\?\\TFFAControl\n");
    return STATUS_SUCCESS;
}

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT  DriverObject,
    _In_ PUNICODE_STRING RegistryPath
)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, TFFAEvtDeviceAdd);

    TFFA_LOG("TFFAFilter: DriverEntry\n");

    WDFDRIVER driver;
    NTSTATUS status = WdfDriverCreate(
        DriverObject,
        RegistryPath,
        WDF_NO_OBJECT_ATTRIBUTES,
        &config,
        &driver);
    if (!NT_SUCCESS(status)) {
        TFFA_LOG("TFFAFilter: WdfDriverCreate failed 0x%X\n", status);
        return status;
    }

    // Create the control device immediately. Failure here is non-fatal for
    // the filter itself (the filter still functions in passthrough mode); we
    // just log and return success so the filter loads. Drop logic falls back
    // to registry ProtectedPid.
    NTSTATUS ctrlStatus = TFFACreateControlDevice(driver);
    if (!NT_SUCCESS(ctrlStatus)) {
        TFFA_LOG("TFFAFilter: control device unavailable (status=0x%X); filter continues without IOCTL channel\n", ctrlStatus);
    }
    return STATUS_SUCCESS;
}

NTSTATUS
TFFAEvtDeviceAdd(
    _In_    WDFDRIVER       Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
)
{
    UNREFERENCED_PARAMETER(Driver);

    // Read the underlying device's hardware-id list BEFORE we commit to
    // building a WDF device. This lets us opt out cleanly for non-target
    // HID devices by returning STATUS_NOT_SUPPORTED (the framework then
    // skips loading us into that device's stack).
    WDFMEMORY hwIdMem = NULL;
    NTSTATUS  status  = WdfFdoInitAllocAndQueryProperty(
        DeviceInit,
        DevicePropertyHardwareID,
        NonPagedPoolNx,
        WDF_NO_OBJECT_ATTRIBUTES,
        &hwIdMem);

    // Copy the most specific (first) hwid string for logging AFTER we
    // delete hwIdMem. Knowing the MI_xx / collection of every PDO we attach
    // to is critical for figuring out which interface games actually write
    // FFB to vs which ones we are blind to.
    const TFFA_TARGET_WHEEL *matched = NULL;
    WCHAR firstHwid[256] = { 0 };
    if (NT_SUCCESS(status) && hwIdMem != NULL) {
        size_t  size   = 0;
        PCWSTR  hwIds  = (PCWSTR)WdfMemoryGetBuffer(hwIdMem, &size);
        matched        = TFFAMatchHardwareId(hwIds, size);
        if (matched != NULL && hwIds != NULL) {
            size_t maxChars = size / sizeof(WCHAR);
            size_t i = 0;
            while (i < maxChars && i < 255 && hwIds[i] != L'\0') {
                firstHwid[i] = hwIds[i];
                ++i;
            }
            firstHwid[i] = L'\0';
        }
        WdfObjectDelete(hwIdMem);
    }

    if (matched == NULL) {
        // Not a wheel we care about. Tell the framework we don't want this
        // device; we won't appear in its driver stack.
        return STATUS_NOT_SUPPORTED;
    }

    TFFA_LOG("TFFAFilter: DeviceAdd MATCH (%ws) hwid='%ws'\n", matched->Model, firstHwid);

    // Mark this as a filter (FDO that sits in the device stack without owning
    // it). Without this we would behave as a function driver and the stack
    // would never be built correctly.
    WdfFdoInitSetFilter(DeviceInit);

    WDFDEVICE device;
    status = WdfDeviceCreate(&DeviceInit, WDF_NO_OBJECT_ATTRIBUTES, &device);
    if (!NT_SUCCESS(status)) {
        TFFA_LOG("TFFAFilter: WdfDeviceCreate failed 0x%X\n", status);
        return status;
    }

    // Single default queue. We are an UPPER filter on HIDClass, so user-mode
    // IRPs reach us as IRP_MJ_DEVICE_CONTROL (HID IOCTLs) or IRP_MJ_WRITE
    // (raw report writes). We also hook IRP_MJ_INTERNAL_DEVICE_CONTROL in
    // case anything kernel-side sends us those. Everything else falls
    // through EvtIoDefault.
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl         = TFFAEvtIoDeviceControl;
    queueConfig.EvtIoInternalDeviceControl = TFFAEvtIoInternalDeviceControl;
    queueConfig.EvtIoWrite                 = TFFAEvtIoWrite;
    queueConfig.EvtIoDefault               = TFFAEvtIoDefault;

    WDFQUEUE queue;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &queue);
    if (!NT_SUCCESS(status)) {
        TFFA_LOG("TFFAFilter: WdfIoQueueCreate failed 0x%X\n", status);
        return status;
    }

    TFFA_LOG("TFFAFilter: DeviceAdd OK (device=%p)\n", device);
    return STATUS_SUCCESS;
}

// ---------- IO handlers ----------------------------------------------------

static const char *
TFFAIoctlName(ULONG code)
{
    switch (code) {
        case IOCTL_HID_WRITE_REPORT:       return "WRITE_REPORT";
        case IOCTL_HID_READ_REPORT:        return "READ_REPORT";
        case IOCTL_HID_SET_FEATURE:        return "SET_FEATURE";
        case IOCTL_HID_GET_FEATURE:        return "GET_FEATURE";
        case IOCTL_HID_GET_INPUT_REPORT:   return "GET_INPUT_REPORT";
        case IOCTL_HID_SET_OUTPUT_REPORT:  return "SET_OUTPUT_REPORT";
        case IOCTL_HID_GET_STRING:         return "GET_STRING";
        case IOCTL_HID_ACTIVATE_DEVICE:    return "ACTIVATE";
        case IOCTL_HID_DEACTIVATE_DEVICE:  return "DEACTIVATE";
        case IOCTL_GET_PHYSICAL_DESCRIPTOR:return "GET_PHYS_DESC";
        case IOCTL_HID_FLUSH_QUEUE:        return "FLUSH_QUEUE";
        case IOCTL_HID_GET_COLLECTION_INFORMATION: return "GET_COLL_INFO";
        case IOCTL_HID_GET_COLLECTION_DESCRIPTOR:  return "GET_COLL_DESC";
        case IOCTL_HID_GET_HARDWARE_ID:    return "GET_HW_ID";
        case IOCTL_HID_GET_INDEXED_STRING: return "GET_INDEXED_STR";
        case IOCTL_HID_GET_MS_GENRE_DESCRIPTOR: return "GET_MS_GENRE";
        case IOCTL_HID_GET_DRIVER_CONFIG:  return "GET_DRV_CONFIG";
        case IOCTL_HID_SET_DRIVER_CONFIG:  return "SET_DRV_CONFIG";
        case IOCTL_HID_GET_POLL_FREQUENCY_MSEC: return "GET_POLL_FREQ";
        case IOCTL_HID_SET_POLL_FREQUENCY_MSEC: return "SET_POLL_FREQ";
        case IOCTL_HID_DEVICERESET_NOTIFICATION: return "RESET_NOTIFY";
        default:                            return "OTHER_IOCTL";
    }
}

// Shared dispatch for both DEVICE_CONTROL and INTERNAL_DEVICE_CONTROL paths.
// As an upper filter on HIDClass, user-mode app IOCTLs arrive via
// IRP_MJ_DEVICE_CONTROL; kernel-side ones (rare in upper-filter position)
// arrive via IRP_MJ_INTERNAL_DEVICE_CONTROL. Both feed into here so we
// don't have to duplicate logic.
static VOID
TFFADispatchIoctl(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request,
    _In_ ULONG      IoControlCode,
    _In_ PCSTR      pathTag         // "DEV" or "INT" so log lines tell which path
)
{
    // DIAGNOSTIC: log every non-owner IOCTL (any code) so we can see init/
    // handshake/keepalive exchanges the game does during startup, and catch
    // any IOCTL code we don't currently recognize. Owner snapshot done here
    // so the diagnostic log can include it.
    ULONG diagOwner = 0;
    if (g_OwnerLock != NULL) {
        WdfSpinLockAcquire(g_OwnerLock);
        diagOwner = g_OwnerPid;
        WdfSpinLockRelease(g_OwnerLock);
    }
    if (diagOwner == 0) diagOwner = TFFAGetProtectedPid();
    ULONG diagRequestor = HandleToULong(PsGetCurrentProcessId());
    // Log every non-plugin IOCTL (any code). requestor==0 is a kernel
    // housekeeping path, skip to keep boot noise low. Fires even when no
    // plugin has claimed ownership so we can run no-plugin baseline
    // captures and see what IOCTLs games use.
    if (diagRequestor != 0 && diagRequestor != diagOwner) {
        static volatile LONG s_nonOwnerIoctlSeen = 0;
        LONG n = InterlockedIncrement(&s_nonOwnerIoctlSeen);
        if (n <= 40 || (n % 50) == 1) {
            WDFDEVICE diagDev = WdfIoQueueGetDevice(Queue);
            TFFA_LOG("TFFAFilter: %s_IOCTL #%ld dev=%p code=0x%08X (%s) pid=%lu owner=%lu\n",
                pathTag, n, diagDev, IoControlCode, TFFAIoctlName(IoControlCode),
                diagRequestor, diagOwner);
        }
    }

    // SET-direction HID IOCTLs carry an output report from the caller to
    // the device. These are what HidD_SetOutputReport / DirectInput PID FFB
    // writes (and the like) use; they reach us in HID_XFER_PACKET form via
    // Parameters.Others.Arg1. We need to apply ownership intercept here for
    // the same reason we do on IRP_MJ_WRITE: apps that talk to the wheel via
    // the HID API (DirectInput, HidSharp's SetOutputReport path, etc.) end
    // up here, not in EvtIoWrite.
    BOOLEAN isSetIoctl = (IoControlCode == IOCTL_HID_WRITE_REPORT ||
                          IoControlCode == IOCTL_HID_SET_OUTPUT_REPORT ||
                          IoControlCode == IOCTL_HID_SET_FEATURE);

    if (isSetIoctl)
    {
        WDF_REQUEST_PARAMETERS params;
        WDF_REQUEST_PARAMETERS_INIT(&params);
        WdfRequestGetParameters(Request, &params);

        HID_XFER_PACKET * packet = (HID_XFER_PACKET *)params.Parameters.Others.Arg1;
        if (packet != NULL && packet->reportBuffer != NULL && packet->reportBufferLen > 0) {
            // Same owner snapshot as EvtIoWrite.
            ULONG owner = 0;
            if (g_OwnerLock != NULL) {
                WdfSpinLockAcquire(g_OwnerLock);
                owner = g_OwnerPid;
                WdfSpinLockRelease(g_OwnerLock);
            }
            if (owner == 0) owner = TFFAGetProtectedPid();
            ULONG requestor = HandleToULong(PsGetCurrentProcessId());

            // Same proxy classification as EvtIoWrite: HID++ FFB writes
            // (feat 0x0E, func 2_/3_) and LED writes (feat 0x09) get
            // intercepted and dropped; the plugin echoes them back via
            // its own HID handle so the wheel sees one writer only.
            BOOLEAN isFfbWrite = FALSE;
            BOOLEAN isLedWrite = FALSE;
            UCHAR *pb = (UCHAR *)packet->reportBuffer;
            if (packet->reportBufferLen >= 4) {
                BOOLEAN isHidpp = (pb[0] == 0x10 || pb[0] == 0x11 || pb[0] == 0x12) && pb[1] == 0xFF;
                if (isHidpp && pb[2] == 0x0E && ((pb[3] & 0xF0) == 0x20 || (pb[3] & 0xF0) == 0x30)) {
                    isFfbWrite = TRUE;
                }
                if (isHidpp && pb[2] == 0x09) {
                    isLedWrite = TRUE;
                }
            }

            if (owner != 0 && requestor != owner && (isFfbWrite || isLedWrite)) {
                // INTERCEPT. Deliver bytes to RECV, complete the original
                // with success, do NOT forward. Plugin will echo.
                if (g_InterceptedQueue != NULL) {
                    WDFREQUEST recv;
                    NTSTATUS rs = WdfIoQueueRetrieveNextRequest(g_InterceptedQueue, &recv);
                    if (NT_SUCCESS(rs)) {
                        PVOID  outBuf = NULL;
                        size_t outLen = 0;
                        NTSTATUS os = WdfRequestRetrieveOutputBuffer(recv, 1, &outBuf, &outLen);
                        if (NT_SUCCESS(os)) {
                            size_t copy = (packet->reportBufferLen < outLen) ? packet->reportBufferLen : outLen;
                            RtlCopyMemory(outBuf, packet->reportBuffer, copy);
                            WdfRequestCompleteWithInformation(recv, STATUS_SUCCESS, copy);
                        } else {
                            WdfRequestComplete(recv, os);
                        }
                    }
                }
                WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, packet->reportBufferLen);
                return;
            }

            // Owner write OR no-owner: log + pass through. Throttle the log
            // because games can drive these at 250-500 Hz; one in 500 keeps
            // DebugView usable.
            static volatile LONG s_passCount = 0;
            LONG c = InterlockedIncrement(&s_passCount);
            if ((c % 500) == 1) {
                ULONG  n = packet->reportBufferLen;
                UCHAR *b = packet->reportBuffer;
                ULONG dump = n < 12 ? n : 12;
                UCHAR pad[12] = { 0 };
                RtlCopyMemory(pad, b, dump);
                TFFA_LOG(
                    "TFFAFilter: %s_PASS #%ld id=0x%02X len=%lu pid=%lu  %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X\n",
                    pathTag, c, packet->reportId, n, requestor,
                    pad[0], pad[1], pad[2], pad[3],
                    pad[4], pad[5], pad[6], pad[7],
                    pad[8], pad[9], pad[10], pad[11]);
            }
        }
    }

    // Forward unchanged. SEND_AND_FORGET means we are out of the picture for
    // this request once it leaves; the lower driver completes it directly.
    WDF_REQUEST_SEND_OPTIONS opts;
    WDF_REQUEST_SEND_OPTIONS_INIT(&opts, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);

    WDFIOTARGET target = WdfDeviceGetIoTarget(WdfIoQueueGetDevice(Queue));
    if (!WdfRequestSend(Request, target, &opts)) {
        NTSTATUS s = WdfRequestGetStatus(Request);
        TFFA_LOG("TFFAFilter: WdfRequestSend failed 0x%X\n", s);
        WdfRequestComplete(Request, s);
    }
}

// Thin wrappers that route the two IOCTL paths into the shared dispatcher.

VOID
TFFAEvtIoDeviceControl(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t     OutputBufferLength,
    _In_ size_t     InputBufferLength,
    _In_ ULONG      IoControlCode
)
{
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);
    TFFADispatchIoctl(Queue, Request, IoControlCode, "DEV");
}

VOID
TFFAEvtIoInternalDeviceControl(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t     OutputBufferLength,
    _In_ size_t     InputBufferLength,
    _In_ ULONG      IoControlCode
)
{
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);
    TFFADispatchIoctl(Queue, Request, IoControlCode, "INT");
}

// Read the ProtectedPid registry value. Returns 0 if absent/malformed,
// otherwise the process ID whose writes are allowed through. Called per
// WRITE so a flip from user-mode takes effect immediately. Registry reads
// from kernel are cheap; we can cache if perf demands.
static ULONG
TFFAGetProtectedPid(VOID)
{
    UNICODE_STRING keyPath = RTL_CONSTANT_STRING(
        L"\\Registry\\Machine\\SYSTEM\\CurrentControlSet\\Services\\TFFAFilter\\Parameters");
    UNICODE_STRING valueName = RTL_CONSTANT_STRING(L"ProtectedPid");
    OBJECT_ATTRIBUTES attrs;
    HANDLE handle = NULL;
    NTSTATUS status;
    ULONG pid = 0;

    InitializeObjectAttributes(&attrs, &keyPath,
        OBJ_KERNEL_HANDLE | OBJ_CASE_INSENSITIVE, NULL, NULL);

    status = ZwOpenKey(&handle, KEY_QUERY_VALUE, &attrs);
    if (NT_SUCCESS(status)) {
        UCHAR buf[sizeof(KEY_VALUE_PARTIAL_INFORMATION) + sizeof(ULONG)];
        ULONG resultLen = 0;
        status = ZwQueryValueKey(handle, &valueName,
            KeyValuePartialInformation, buf, sizeof(buf), &resultLen);
        if (NT_SUCCESS(status)) {
            PKEY_VALUE_PARTIAL_INFORMATION info = (PKEY_VALUE_PARTIAL_INFORMATION)buf;
            if (info->Type == REG_DWORD && info->DataLength == sizeof(ULONG)) {
                pid = *(PULONG)info->Data;
            }
        }
        ZwClose(handle);
    }
    return pid;
}

// Raw IRP_MJ_WRITE path. Apps that WriteFile() the wheel's HID handle land
// here. HIDClass translates these into HID output reports for the underlying
// HID minidriver. Sitting above HIDClass we get them first.
//
// PID-discrimination spike: if ProtectedPid is 0 (or missing), every write
// passes through (default safe behaviour). If ProtectedPid is set, writes
// from THAT process pass through and writes from any other process get
// silently completed (success returned to caller, wheel never sees them).
// Proves we can sort our plugin's writes from everything else; the full
// IOCTL/inverted-call architecture builds on this.
VOID
TFFAEvtIoWrite(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t     Length
)
{
    // Snapshot the active owner. Control-device file-open ownership takes
    // precedence over the registry fallback. If neither is set, no
    // discrimination; everything passes through.
    ULONG owner = 0;
    if (g_OwnerLock != NULL) {
        WdfSpinLockAcquire(g_OwnerLock);
        owner = g_OwnerPid;
        WdfSpinLockRelease(g_OwnerLock);
    }
    if (owner == 0) {
        owner = TFFAGetProtectedPid();   // legacy registry fallback
    }

    // PsGetCurrentProcessId returns the PID of the thread currently running
    // our handler. WDF's parallel default queue dispatches in the requesting
    // user-mode thread's context at PASSIVE_LEVEL, so this resolves to the
    // calling app's PID.
    ULONG requestor = HandleToULong(PsGetCurrentProcessId());

    // Helper: extract the input buffer + first 12 bytes for throttled logging.
    PVOID  writeBuf = NULL;
    size_t writeLen = 0;
    NTSTATUS bs = WdfRequestRetrieveInputBuffer(Request, 1, &writeBuf, &writeLen);
    UCHAR pad[12] = { 0 };
    if (NT_SUCCESS(bs) && writeBuf != NULL && writeLen > 0) {
        size_t dump = writeLen < 12 ? writeLen : 12;
        RtlCopyMemory(pad, writeBuf, dump);
    }

    // Proxy architecture: intercept all non-owner writes that target the
    // wheel's HID++ command processor (FFB feature 0x0E + LED feature
    // 0x09), redirect to the plugin via RECV, and DO NOT forward to the
    // wheel. The plugin then echoes each intercepted byte stream back via
    // its own HID handle (which passes through this filter because plugin
    // is owner). Wheel only ever sees writes from one process (plugin),
    // eliminating LED/FFB contention on the wheel's single command
    // processor. Game's perception is unchanged: writes return success,
    // wheel responds via input reports (because plugin echo reaches it).
    //
    // FFB writes additionally have bytes 10-11 (int16 target) extracted by
    // the plugin's RecvLoop and injected into the Trueforce stream (ep3
    // bytes 6-9) for the audio-haptic path. The HID++ FFB echo and the
    // TF-stream injection coexist exactly as they do in USBPcap mode.
    //
    // Everything else (queries, root features, GET_INFO, notifications,
    // setup exchanges, anything on other feature pages) passes through
    // untouched so the game's HID++ session remains fully healthy.
    BOOLEAN isFfbWrite = FALSE;
    BOOLEAN isLedWrite = FALSE;
    if (NT_SUCCESS(bs) && writeBuf != NULL && writeLen >= 4) {
        UCHAR *b = (UCHAR *)writeBuf;
        BOOLEAN isHidpp = (b[0] == 0x10 || b[0] == 0x11 || b[0] == 0x12) && b[1] == 0xFF;
        if (isHidpp && b[2] == 0x0E && writeLen >= 4 &&
            ((b[3] & 0xF0) == 0x20 || (b[3] & 0xF0) == 0x30)) {
            isFfbWrite = TRUE;
        }
        if (isHidpp && b[2] == 0x09) {
            isLedWrite = TRUE;
        }
    }

    BOOLEAN isNonOwner = (owner != 0 && requestor != owner);

    if (isNonOwner && (isFfbWrite || isLedWrite)) {
        // Deliver bytes to RECV so the plugin can echo + (for FFB) decode.
        BOOLEAN delivered = FALSE;
        if (NT_SUCCESS(bs) && g_InterceptedQueue != NULL) {
            WDFREQUEST recv;
            NTSTATUS rs = WdfIoQueueRetrieveNextRequest(g_InterceptedQueue, &recv);
            if (NT_SUCCESS(rs)) {
                PVOID  outBuf = NULL;
                size_t outLen = 0;
                NTSTATUS os = WdfRequestRetrieveOutputBuffer(recv, 1, &outBuf, &outLen);
                if (NT_SUCCESS(os)) {
                    size_t copy = (writeLen < outLen) ? writeLen : outLen;
                    RtlCopyMemory(outBuf, writeBuf, copy);
                    WdfRequestCompleteWithInformation(recv, STATUS_SUCCESS, copy);
                    delivered = TRUE;
                } else {
                    WdfRequestComplete(recv, os);
                }
            }
        }

        static volatile LONG s_interceptCount = 0;
        LONG c = InterlockedIncrement(&s_interceptCount);
        if (c <= 20 || (c % 50) == 1) {
            WDFDEVICE dev = WdfIoQueueGetDevice(Queue);
            TFFA_LOG(
                "TFFAFilter: WRITE #%ld INTERCEPT_%s dev=%p len=%llu pid=%lu owner=%lu delivered=%d  %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X\n",
                c, isLedWrite ? "LED" : "FFB", dev, (unsigned long long)Length,
                requestor, owner, delivered ? 1 : 0,
                pad[0], pad[1], pad[2], pad[3],
                pad[4], pad[5], pad[6], pad[7],
                pad[8], pad[9], pad[10], pad[11]);
        }

        // Drop the original IRP. Plugin echoes via its own HID handle.
        WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, Length);
        return;
    }

    // Passthrough path. Split counters by who's writing:
    //   - "System" / requestor==0 / owner==0 boot setup -> covered by s_passCount throttle
    //   - non-owner game writes (owner==0 [no plugin loaded] OR owner!=0
    //     but requestor!=owner) -> the diagnostic NONOWNER_PASS path; this
    //     is what we want to see to verify AC's writes are reaching us.
    // requestor==0 (System) is a rare housekeeping path; skip the
    // NONOWNER path to keep boot-time noise out. We could include it but
    // would have to throttle harder.
    BOOLEAN isNonOwnerPass = (requestor != 0 && requestor != owner);
    if (isNonOwnerPass) {
        static volatile LONG s_nonOwnerPassCount = 0;
        LONG np = InterlockedIncrement(&s_nonOwnerPassCount);
        if (np <= 20 || (np % 50) == 1) {
            WDFDEVICE dev = WdfIoQueueGetDevice(Queue);
            TFFA_LOG(
                "TFFAFilter: WRITE #%ld NONOWNER_PASS dev=%p len=%llu pid=%lu owner=%lu  %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X\n",
                np, dev, (unsigned long long)Length, requestor, owner,
                pad[0], pad[1], pad[2], pad[3],
                pad[4], pad[5], pad[6], pad[7],
                pad[8], pad[9], pad[10], pad[11]);
        }
    }
    static volatile LONG s_passCount = 0;
    LONG p = InterlockedIncrement(&s_passCount);
    if ((p % 500) == 1) {
        TFFA_LOG(
            "TFFAFilter: WRITE #%ld PASS len=%llu pid=%lu  %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X\n",
            p, (unsigned long long)Length, requestor,
            pad[0], pad[1], pad[2], pad[3],
            pad[4], pad[5], pad[6], pad[7],
            pad[8], pad[9], pad[10], pad[11]);
    }

    WDF_REQUEST_SEND_OPTIONS opts;
    WDF_REQUEST_SEND_OPTIONS_INIT(&opts, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
    WDFIOTARGET target = WdfDeviceGetIoTarget(WdfIoQueueGetDevice(Queue));
    if (!WdfRequestSend(Request, target, &opts)) {
        NTSTATUS s = WdfRequestGetStatus(Request);
        TFFA_LOG("TFFAFilter: WRITE forward failed 0x%X\n", s);
        WdfRequestComplete(Request, s);
    }
}

VOID
TFFAEvtIoDefault(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request
)
{
    // Forward silently. Previously we logged the IRP major code here for
    // diagnosis, but that produced enough output to crash DebugView under
    // load (every PnP query, power IRP, etc. on every HID device we attach
    // to). The IOCTL and Write handlers above cover what we actually care
    // about; everything else just passes through.
    WDF_REQUEST_SEND_OPTIONS opts;
    WDF_REQUEST_SEND_OPTIONS_INIT(&opts, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
    WDFIOTARGET target = WdfDeviceGetIoTarget(WdfIoQueueGetDevice(Queue));
    if (!WdfRequestSend(Request, target, &opts)) {
        WdfRequestComplete(Request, WdfRequestGetStatus(Request));
    }
}

// ---------- Control device handlers ---------------------------------------
//
// These run on the singleton control device at \\?\TFFAControl.

VOID
TFFAEvtControlFileCreate(
    _In_ WDFDEVICE     Device,
    _In_ WDFREQUEST    Request,
    _In_ WDFFILEOBJECT FileObject
)
{
    UNREFERENCED_PARAMETER(Device);
    UNREFERENCED_PARAMETER(FileObject);

    ULONG caller = HandleToULong(PsGetCurrentProcessId());
    NTSTATUS status = STATUS_SUCCESS;

    // Remember THIS handle's claiming PID in its file context so cleanup
    // (later) knows whether this handle's close is the "real" release vs a
    // stale close after a takeover happened.
    PTFFA_FILE_CONTEXT fileCtx = TFFAGetFileContext(FileObject);
    if (fileCtx != NULL) fileCtx->ClaimingPid = caller;

    // Last-claim-wins: a new opener always takes ownership, even if a stale
    // previous-owner PID is still recorded. Avoids a race where a rapid
    // SimHub restart hits "owner=old-pid" before the old process's handle
    // cleanup has fired, which would otherwise deny the new claim and force
    // the user to restart SimHub a second time for the toggle to take
    // effect. The corresponding file-cleanup callback only clears g_OwnerPid
    // if the closing handle's claiming PID matches current owner, so the
    // old close (when it eventually fires) becomes a no-op once we've handed
    // ownership off.
    WdfSpinLockAcquire(g_OwnerLock);
    ULONG previousOwner = g_OwnerPid;
    g_OwnerPid = caller;
    WdfSpinLockRelease(g_OwnerLock);

    if (previousOwner == 0) {
        TFFA_LOG("TFFAFilter: control device CLAIMED by pid=%lu\n", caller);
    } else if (previousOwner == caller) {
        TFFA_LOG("TFFAFilter: control device re-opened by owner pid=%lu\n", caller);
    } else {
        TFFA_LOG("TFFAFilter: control device CLAIM TAKEOVER by pid=%lu (was pid=%lu - stale or another instance)\n",
                 caller, previousOwner);
    }

    WdfRequestComplete(Request, status);
}

VOID
TFFAEvtControlFileCleanup(
    _In_ WDFFILEOBJECT FileObject
)
{
    UNREFERENCED_PARAMETER(FileObject);

    // Cleanup fires when the LAST handle on this file object closes
    // (including involuntary close from process termination). Only release
    // global ownership if THIS handle was the active owner; otherwise some
    // newer handle has already taken over (rapid SimHub restart case) and
    // we must NOT wipe out the new owner.
    PTFFA_FILE_CONTEXT fileCtx = TFFAGetFileContext(FileObject);
    ULONG thisHandleClaimer = fileCtx != NULL ? fileCtx->ClaimingPid : 0;

    WdfSpinLockAcquire(g_OwnerLock);
    BOOLEAN actuallyReleased = FALSE;
    if (g_OwnerPid != 0 && g_OwnerPid == thisHandleClaimer) {
        g_OwnerPid = 0;
        actuallyReleased = TRUE;
    }
    ULONG ownerSnapshot = g_OwnerPid;
    WdfSpinLockRelease(g_OwnerLock);

    if (actuallyReleased) {
        TFFA_LOG("TFFAFilter: control device RELEASED (was owner=%lu)\n", thisHandleClaimer);
        // Only purge RECVs if we actually released, otherwise we'd cancel
        // the new owner's pending RECVs (which would force their worker to
        // exit and break interception until they restart).
        if (g_InterceptedQueue != NULL) {
            WdfIoQueuePurgeSynchronously(g_InterceptedQueue);
            WdfIoQueueStart(g_InterceptedQueue);
        }
    } else if (thisHandleClaimer != 0) {
        TFFA_LOG("TFFAFilter: control device stale-close ignored (this handle pid=%lu, current owner=%lu)\n",
                 thisHandleClaimer, ownerSnapshot);
    }
}

VOID
TFFAEvtControlIoDeviceControl(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t     OutputBufferLength,
    _In_ size_t     InputBufferLength,
    _In_ ULONG      IoControlCode
)
{
    UNREFERENCED_PARAMETER(Queue);
    UNREFERENCED_PARAMETER(InputBufferLength);

    switch (IoControlCode) {
        case IOCTL_TFFA_PING: {
            // Echo back a magic number. Sanity check for user-mode wiring.
            if (OutputBufferLength < sizeof(ULONG)) {
                WdfRequestComplete(Request, STATUS_BUFFER_TOO_SMALL);
                return;
            }
            PVOID  outBuf = NULL;
            size_t outLen = 0;
            NTSTATUS s = WdfRequestRetrieveOutputBuffer(Request, sizeof(ULONG), &outBuf, &outLen);
            if (!NT_SUCCESS(s)) {
                WdfRequestComplete(Request, s);
                return;
            }
            *(PULONG)outBuf = TFFA_PING_MAGIC;
            WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, sizeof(ULONG));
            return;
        }

        case IOCTL_TFFA_RECV: {
            // Park the request on the manual queue. EvtIoWrite picks one off
            // when it intercepts a write, copies the write payload into the
            // request's output buffer, and completes it. Caller posts a fresh
            // RECV immediately after each completion (standard inverted-call
            // pattern).
            if (OutputBufferLength == 0) {
                WdfRequestComplete(Request, STATUS_INVALID_PARAMETER);
                return;
            }
            NTSTATUS s = WdfRequestForwardToIoQueue(Request, g_InterceptedQueue);
            if (!NT_SUCCESS(s)) {
                TFFA_LOG("TFFAFilter: RECV forward-to-queue failed 0x%X\n", s);
                WdfRequestComplete(Request, s);
            }
            return;
        }

        default:
            WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
            return;
    }
}
