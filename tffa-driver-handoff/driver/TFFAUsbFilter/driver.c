// TFFAUsbFilter: USB-layer upper filter for Logitech Trueforce-enabled wheels.
//
// Sits ABOVE usbccgp on each wheel child PDO. We see every URB the host
// submits to the wheel via IOCTL_INTERNAL_USB_SUBMIT_URB, regardless of
// which Windows API a game used to issue the write (DirectInput PID,
// HidD_SetOutputReport, raw WriteFile on a HID handle, WinUSB, etc). This
// is the layer USBPcap operates at; the previous TFFAFilter (HIDClass
// upper) sat too high and missed game writes that take a HID-minidriver-
// internal shortcut on the way down.
//
// Architecture: the plugin opens \\?\TFFAControl (claims wheel ownership).
// While owner is set:
//   - Plugin's own writes pass through to the wheel
//   - Every other (non-owner) host-to-device URB is INTERCEPTED: bytes
//     copied to the plugin via inverted-call IOCTL_TFFA_RECV, URB
//     completed with STATUS_SUCCESS locally without forwarding. Wheel
//     never receives a byte that didn't originate from the plugin process.
// The plugin then decides per-message what to write back to the wheel:
//   - FFB writes (LONG/VERY_LONG feat 0x0E func 0x2_): dropped. The int16
//     target at bytes 10-11 is extracted into the FFB pipeline and injected
//     into the Trueforce stream ep3 bytes 6-9. Wheel produces force from
//     our TF stream alone.
//   - LED writes (feat 0x09 on G PRO): dropped. Plugin owns LED output.
//   - SHORT 0x3B SET_EFFECT_STATE for feat 0x0E: dropped (wheel never got
//     the DOWNLOAD_EFFECT either, so playing a phantom slot is a no-op).
//   - Everything else (root queries, GET_INFO, notifications): plugin
//     ECHOES via HidStream.Write so the wheel responds and the game's
//     HID++ session stays alive.
// Net effect: plugin is the literal sole writer to the wheel. No two-host
// contention on the wheel's HID++ command processor. FFB still works via
// TF stream. LEDs work without contention. Game's perception is unchanged
// (writes succeed, queries get responses).
//
// Log lines: DbgPrintEx at ERROR level. View with DebugView (admin +
// Capture Kernel + Verbose Kernel Output checked).

#include <ntddk.h>
#include <wdf.h>
#include <usbdi.h>
#include <usbdlib.h>
#include <usbioctl.h>

#define TFFA_LOG(...) DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_ERROR_LEVEL, __VA_ARGS__)
#define TFFA_TAG      ((ULONG)'fUFT')

DRIVER_INITIALIZE                            DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD                    TFFAUsbEvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL  TFFAUsbEvtIoInternalDeviceControl;
EVT_WDF_IO_QUEUE_IO_DEFAULT                  TFFAUsbEvtIoDefault;

EVT_WDF_DEVICE_FILE_CREATE                   TFFAUsbEvtControlFileCreate;
EVT_WDF_FILE_CLEANUP                         TFFAUsbEvtControlFileCleanup;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL           TFFAUsbEvtControlIoDeviceControl;

// Forward declaration; full definition further down.
static ULONG TFFAUsbGetProtectedPid(VOID);

// ---------- Target wheels (runtime hwid gate) ---------------------------
//
// We install as a USB class filter, so PnP loads us into every USB device's
// stack. EvtDeviceAdd returns STATUS_NOT_SUPPORTED for anything whose
// hardware-id list doesn't contain one of our wheel VID/PIDs. The framework
// then skips loading us into that device's stack entirely - zero runtime
// cost on unrelated USB devices.

typedef struct _TFFAUSB_TARGET_WHEEL {
    PCWSTR HardwareIdSubstring;
    PCWSTR Model;
} TFFAUSB_TARGET_WHEEL;

static const TFFAUSB_TARGET_WHEEL g_TargetWheels[] = {
    { L"VID_046D&PID_C272", L"Logitech G PRO (Xbox/PC)"      },
    { L"VID_046D&PID_C268", L"Logitech G PRO (PS/PC)"        },
    { L"VID_046D&PID_C266", L"Logitech G923 (PS/PC)"         },
    { L"VID_046D&PID_C26D", L"Logitech G923 (Xbox/PC)"       },
    { L"VID_046D&PID_C26E", L"Logitech G923 (Xbox/PC, B)"    },
    { L"VID_046D&PID_C276", L"Logitech RS50"                 },
};

// Case-insensitive substring search for the hwid match. Some kernel headers
// don't expose wcsstr / _wcsnicmp on amd64, so we roll our own.
static BOOLEAN
TFFAUsbFindSubstringNoCase(_In_ PCWSTR haystack, _In_ PCWSTR needle)
{
    if (haystack == NULL || needle == NULL || *needle == L'\0') return FALSE;
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

// Walk the REG_MULTI_SZ hwid list, return the matched wheel entry or NULL.
static const TFFAUSB_TARGET_WHEEL *
TFFAUsbMatchHardwareId(_In_ PCWSTR hwIdMultiSz, _In_ size_t byteSize)
{
    if (hwIdMultiSz == NULL || byteSize < sizeof(WCHAR)) return NULL;
    size_t maxChars = byteSize / sizeof(WCHAR);
    size_t i = 0;
    while (i < maxChars && hwIdMultiSz[i] != L'\0') {
        PCWSTR thisId = &hwIdMultiSz[i];
        for (ULONG w = 0; w < ARRAYSIZE(g_TargetWheels); ++w) {
            if (TFFAUsbFindSubstringNoCase(thisId, g_TargetWheels[w].HardwareIdSubstring)) {
                return &g_TargetWheels[w];
            }
        }
        while (i < maxChars && hwIdMultiSz[i] != L'\0') ++i;
        ++i;
    }
    return NULL;
}

// Per-file-handle context. Records which PID claimed ownership through THIS
// specific handle, so the cleanup callback can decide whether to release the
// global g_OwnerPid. Required so a rapid SimHub restart's old close doesn't
// wipe out the new instance's ownership.
typedef struct _TFFAUSB_FILE_CONTEXT {
    ULONG ClaimingPid;
} TFFAUSB_FILE_CONTEXT, *PTFFAUSB_FILE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(TFFAUSB_FILE_CONTEXT, TFFAUsbGetFileContext)

// Control device + ownership state. Single-owner; the plugin opens
// \\?\TFFAControl and that handle's owning PID becomes g_OwnerPid.
static WDFDEVICE   g_ControlDevice    = NULL;
static WDFQUEUE    g_InterceptedQueue = NULL;
static WDFSPINLOCK g_OwnerLock        = NULL;
static ULONG       g_OwnerPid         = 0;

// IOCTLs on the control device. Same codes as the legacy TFFAFilter so the
// plugin's TFFADriverChannel reaches us without code changes.
#define TFFA_IOCTL(code)        CTL_CODE(FILE_DEVICE_UNKNOWN, (code), METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_TFFA_PING         TFFA_IOCTL(0x800)
#define IOCTL_TFFA_RECV         TFFA_IOCTL(0x801)
#define TFFA_PING_MAGIC         0x54464641UL    // 'TFFA'

// ---------- URB-function name helper ------------------------------------

static const char *
TFFAUrbFunctionName(USHORT fn)
{
    switch (fn) {
        case URB_FUNCTION_SELECT_CONFIGURATION:           return "SELECT_CONFIG";
        case URB_FUNCTION_SELECT_INTERFACE:               return "SELECT_INTERFACE";
        case URB_FUNCTION_ABORT_PIPE:                     return "ABORT_PIPE";
        case URB_FUNCTION_CONTROL_TRANSFER:               return "CONTROL_XFER";
        case URB_FUNCTION_CONTROL_TRANSFER_EX:            return "CONTROL_XFER_EX";
        case URB_FUNCTION_BULK_OR_INTERRUPT_TRANSFER:     return "BULK_OR_INT";
        case URB_FUNCTION_ISOCH_TRANSFER:                 return "ISOCH";
        case URB_FUNCTION_GET_DESCRIPTOR_FROM_DEVICE:     return "GET_DEV_DESC";
        case URB_FUNCTION_GET_DESCRIPTOR_FROM_INTERFACE:  return "GET_IFC_DESC";
        case URB_FUNCTION_GET_DESCRIPTOR_FROM_ENDPOINT:   return "GET_EP_DESC";
        case URB_FUNCTION_VENDOR_DEVICE:                  return "VENDOR_DEV";
        case URB_FUNCTION_VENDOR_INTERFACE:               return "VENDOR_IFC";
        case URB_FUNCTION_VENDOR_ENDPOINT:                return "VENDOR_EP";
        case URB_FUNCTION_CLASS_DEVICE:                   return "CLASS_DEV";
        case URB_FUNCTION_CLASS_INTERFACE:                return "CLASS_IFC";
        case URB_FUNCTION_CLASS_ENDPOINT:                 return "CLASS_EP";
        case URB_FUNCTION_SYNC_RESET_PIPE_AND_CLEAR_STALL:return "RESET_PIPE";
        case URB_FUNCTION_SYNC_CLEAR_STALL:               return "SYNC_CLEAR_STALL";
        default:                                          return "OTHER";
    }
}

// ---------- URB transfer-buffer accessor --------------------------------
//
// Pull the host->device transfer buffer + length for URB function codes we
// care about. Some URBs use MDLs; for the first pass we only look at the
// virtual-address TransferBuffer field. Returns FALSE if the URB isn't a
// host->device write or the buffer isn't directly accessible.

static BOOLEAN
TFFAUsbGetWriteBuffer(
    _In_  PURB   urb,
    _Out_ PVOID *outBuf,
    _Out_ ULONG *outLen,
    _Out_ ULONG *outFlags
)
{
    *outBuf   = NULL;
    *outLen   = 0;
    *outFlags = 0;

    USHORT fn = urb->UrbHeader.Function;
    switch (fn) {
        case URB_FUNCTION_CLASS_INTERFACE:
        case URB_FUNCTION_CLASS_ENDPOINT:
        case URB_FUNCTION_CLASS_DEVICE:
        case URB_FUNCTION_VENDOR_INTERFACE:
        case URB_FUNCTION_VENDOR_ENDPOINT:
        case URB_FUNCTION_VENDOR_DEVICE: {
            struct _URB_CONTROL_VENDOR_OR_CLASS_REQUEST *r =
                &urb->UrbControlVendorClassRequest;
            // TransferFlags bit USBD_TRANSFER_DIRECTION_IN means in;
            // we want OUT (write).
            if (r->TransferFlags & USBD_TRANSFER_DIRECTION_IN) return FALSE;
            if (r->TransferBuffer == NULL || r->TransferBufferLength == 0) return FALSE;
            *outBuf   = r->TransferBuffer;
            *outLen   = r->TransferBufferLength;
            *outFlags = r->TransferFlags;
            return TRUE;
        }
        case URB_FUNCTION_BULK_OR_INTERRUPT_TRANSFER: {
            struct _URB_BULK_OR_INTERRUPT_TRANSFER *r =
                &urb->UrbBulkOrInterruptTransfer;
            if (r->TransferFlags & USBD_TRANSFER_DIRECTION_IN) return FALSE;
            if (r->TransferBuffer == NULL || r->TransferBufferLength == 0) return FALSE;
            *outBuf   = r->TransferBuffer;
            *outLen   = r->TransferBufferLength;
            *outFlags = r->TransferFlags;
            return TRUE;
        }
        default:
            return FALSE;
    }
}

// ---------- Control device ----------------------------------------------

static NTSTATUS
TFFAUsbCreateControlDevice(_In_ WDFDRIVER Driver)
{
    DECLARE_CONST_UNICODE_STRING(deviceName, L"\\Device\\TFFAControl");
    DECLARE_CONST_UNICODE_STRING(symLink,    L"\\DosDevices\\TFFAControl");
    DECLARE_CONST_UNICODE_STRING(sddlOpen, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;BU)");

    PWDFDEVICE_INIT pInit = WdfControlDeviceInitAllocate(Driver, &sddlOpen);
    if (pInit == NULL) {
        TFFA_LOG("TFFAUsbFilter: control init alloc failed\n");
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    NTSTATUS status = WdfDeviceInitAssignName(pInit, &deviceName);
    if (!NT_SUCCESS(status)) { WdfDeviceInitFree(pInit); return status; }
    WdfDeviceInitSetExclusive(pInit, FALSE);

    WDF_FILEOBJECT_CONFIG fileConfig;
    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig,
        TFFAUsbEvtControlFileCreate,
        TFFAUsbEvtControlFileCleanup,
        WDF_NO_EVENT_CALLBACK);
    WDF_OBJECT_ATTRIBUTES fileAttrs;
    WDF_OBJECT_ATTRIBUTES_INIT(&fileAttrs);
    WDF_OBJECT_ATTRIBUTES_SET_CONTEXT_TYPE(&fileAttrs, TFFAUSB_FILE_CONTEXT);
    WdfDeviceInitSetFileObjectConfig(pInit, &fileConfig, &fileAttrs);

    WDFDEVICE controlDev;
    status = WdfDeviceCreate(&pInit, WDF_NO_OBJECT_ATTRIBUTES, &controlDev);
    if (!NT_SUCCESS(status)) {
        TFFA_LOG("TFFAUsbFilter: control device create failed 0x%X\n", status);
        return status;
    }

    status = WdfDeviceCreateSymbolicLink(controlDev, &symLink);
    if (!NT_SUCCESS(status)) {
        TFFA_LOG("TFFAUsbFilter: control symlink failed 0x%X\n", status);
        return status;
    }

    // Default queue: handles IOCTL_TFFA_PING + IOCTL_TFFA_RECV from plugin.
    WDF_IO_QUEUE_CONFIG defaultQueueConfig;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&defaultQueueConfig, WdfIoQueueDispatchParallel);
    defaultQueueConfig.EvtIoDeviceControl = TFFAUsbEvtControlIoDeviceControl;
    status = WdfIoQueueCreate(controlDev, &defaultQueueConfig, WDF_NO_OBJECT_ATTRIBUTES, NULL);
    if (!NT_SUCCESS(status)) {
        TFFA_LOG("TFFAUsbFilter: control default queue failed 0x%X\n", status);
        return status;
    }

    // Manual queue: pending RECV requests park here until an intercepted
    // URB arrives to fill them.
    WDF_IO_QUEUE_CONFIG manualConfig;
    WDF_IO_QUEUE_CONFIG_INIT(&manualConfig, WdfIoQueueDispatchManual);
    status = WdfIoQueueCreate(controlDev, &manualConfig, WDF_NO_OBJECT_ATTRIBUTES, &g_InterceptedQueue);
    if (!NT_SUCCESS(status)) {
        TFFA_LOG("TFFAUsbFilter: control manual queue failed 0x%X\n", status);
        return status;
    }

    WDF_OBJECT_ATTRIBUTES lockAttrs;
    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttrs);
    lockAttrs.ParentObject = controlDev;
    status = WdfSpinLockCreate(&lockAttrs, &g_OwnerLock);
    if (!NT_SUCCESS(status)) {
        TFFA_LOG("TFFAUsbFilter: ownership spinlock failed 0x%X\n", status);
        return status;
    }

    WdfControlFinishInitializing(controlDev);
    g_ControlDevice = controlDev;
    TFFA_LOG("TFFAUsbFilter: control device ready at \\\\?\\TFFAControl\n");
    return STATUS_SUCCESS;
}

// ---------- Driver entry ------------------------------------------------

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT  DriverObject,
    _In_ PUNICODE_STRING RegistryPath
)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, TFFAUsbEvtDeviceAdd);

    TFFA_LOG("TFFAUsbFilter: DriverEntry\n");

    WDFDRIVER driver;
    NTSTATUS status = WdfDriverCreate(
        DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES,
        &config, &driver);
    if (!NT_SUCCESS(status)) {
        TFFA_LOG("TFFAUsbFilter: WdfDriverCreate failed 0x%X\n", status);
        return status;
    }

    NTSTATUS ctrlStatus = TFFAUsbCreateControlDevice(driver);
    if (!NT_SUCCESS(ctrlStatus)) {
        TFFA_LOG("TFFAUsbFilter: control device unavailable (0x%X); filter continues passthrough-only\n", ctrlStatus);
    }
    return STATUS_SUCCESS;
}

NTSTATUS
TFFAUsbEvtDeviceAdd(
    _In_    WDFDRIVER       Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
)
{
    UNREFERENCED_PARAMETER(Driver);

    // Read the underlying device's hardware-id list BEFORE we commit to
    // building a WDF device. This lets us opt out cleanly for non-target
    // USB devices by returning STATUS_NOT_SUPPORTED, after which the
    // framework skips loading us into that device's stack. Same pattern
    // the working TFFAFilter uses on HIDClass.
    WDFMEMORY hwIdMem = NULL;
    NTSTATUS  status  = WdfFdoInitAllocAndQueryProperty(
        DeviceInit,
        DevicePropertyHardwareID,
        NonPagedPoolNx,
        WDF_NO_OBJECT_ATTRIBUTES,
        &hwIdMem);

    const TFFAUSB_TARGET_WHEEL *matched = NULL;
    WCHAR firstHwid[256] = { 0 };
    if (NT_SUCCESS(status) && hwIdMem != NULL) {
        size_t size = 0;
        PCWSTR hwIds = (PCWSTR)WdfMemoryGetBuffer(hwIdMem, &size);
        matched = TFFAUsbMatchHardwareId(hwIds, size);
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
        // device; we won't appear in its driver stack. This is the runtime
        // gate that keeps a USB-class filter from imposing any cost on the
        // hundreds of unrelated USB devices the system enumerates.
        return STATUS_NOT_SUPPORTED;
    }

    TFFA_LOG("TFFAUsbFilter: DeviceAdd MATCH (%ws) hwid='%ws'\n", matched->Model, firstHwid);

    WdfFdoInitSetFilter(DeviceInit);

    WDFDEVICE device;
    status = WdfDeviceCreate(&DeviceInit, WDF_NO_OBJECT_ATTRIBUTES, &device);
    if (!NT_SUCCESS(status)) {
        TFFA_LOG("TFFAUsbFilter: WdfDeviceCreate failed 0x%X\n", status);
        return status;
    }

    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoInternalDeviceControl = TFFAUsbEvtIoInternalDeviceControl;
    queueConfig.EvtIoDefault               = TFFAUsbEvtIoDefault;

    WDFQUEUE queue;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &queue);
    if (!NT_SUCCESS(status)) {
        TFFA_LOG("TFFAUsbFilter: WdfIoQueueCreate failed 0x%X\n", status);
        return status;
    }

    TFFA_LOG("TFFAUsbFilter: DeviceAdd OK (device=%p)\n", device);
    return STATUS_SUCCESS;
}

// ---------- URB intercept ------------------------------------------------

VOID
TFFAUsbEvtIoInternalDeviceControl(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t     OutputBufferLength,
    _In_ size_t     InputBufferLength,
    _In_ ULONG      IoControlCode
)
{
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    BOOLEAN intercept   = FALSE;
    PVOID   writeBuf    = NULL;
    ULONG   writeLen    = 0;
    USHORT  urbFunction = 0;

    if (IoControlCode == IOCTL_INTERNAL_USB_SUBMIT_URB) {
        WDF_REQUEST_PARAMETERS params;
        WDF_REQUEST_PARAMETERS_INIT(&params);
        WdfRequestGetParameters(Request, &params);

        PURB urb = (PURB)params.Parameters.Others.Arg1;
        if (urb != NULL) {
            urbFunction = urb->UrbHeader.Function;
            ULONG flags = 0;
            TFFAUsbGetWriteBuffer(urb, &writeBuf, &writeLen, &flags);
        }
    }

    // Snapshot ownership.
    ULONG owner = 0;
    if (g_OwnerLock != NULL) {
        WdfSpinLockAcquire(g_OwnerLock);
        owner = g_OwnerPid;
        WdfSpinLockRelease(g_OwnerLock);
    }
    if (owner == 0) owner = TFFAUsbGetProtectedPid();
    ULONG requestor = HandleToULong(PsGetCurrentProcessId());

    // Intercept criteria: a host->device URB carrying a transfer buffer
    // came from a non-owner PID (and there IS an owner set). Wheel never
    // gets these; the plugin will reissue whatever it wants to via its own
    // HID handle (which passes through this filter since plugin == owner).
    if (writeBuf != NULL && writeLen > 0 && owner != 0 && requestor != owner) {
        intercept = TRUE;
    }

    UCHAR pad[12] = { 0 };
    if (writeBuf != NULL && writeLen > 0) {
        ULONG dump = writeLen < 12 ? writeLen : 12;
        __try {
            RtlCopyMemory(pad, writeBuf, dump);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            // Buffer is MDL-only or kernel-only; leave pad zeroed.
        }
    }

    if (intercept) {
        BOOLEAN delivered = FALSE;
        if (g_InterceptedQueue != NULL) {
            WDFREQUEST recv;
            NTSTATUS rs = WdfIoQueueRetrieveNextRequest(g_InterceptedQueue, &recv);
            if (NT_SUCCESS(rs)) {
                PVOID  outBuf = NULL;
                size_t outLen = 0;
                NTSTATUS os = WdfRequestRetrieveOutputBuffer(recv, 1, &outBuf, &outLen);
                if (NT_SUCCESS(os)) {
                    size_t copy = writeLen < outLen ? writeLen : outLen;
                    __try {
                        RtlCopyMemory(outBuf, writeBuf, copy);
                        WdfRequestCompleteWithInformation(recv, STATUS_SUCCESS, copy);
                        delivered = TRUE;
                    } __except (EXCEPTION_EXECUTE_HANDLER) {
                        WdfRequestComplete(recv, STATUS_INVALID_USER_BUFFER);
                    }
                } else {
                    WdfRequestComplete(recv, os);
                }
            }
        }

        static volatile LONG s_interceptCount = 0;
        LONG c = InterlockedIncrement(&s_interceptCount);
        if (c <= 40 || (c % 50) == 1) {
            TFFA_LOG(
                "TFFAUsbFilter: URB #%ld INTERCEPTED fn=0x%04X (%s) len=%lu pid=%lu owner=%lu delivered=%d  %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X\n",
                c, urbFunction, TFFAUrbFunctionName(urbFunction), writeLen,
                requestor, owner, delivered ? 1 : 0,
                pad[0], pad[1], pad[2], pad[3],
                pad[4], pad[5], pad[6], pad[7],
                pad[8], pad[9], pad[10], pad[11]);
        }

        // Complete original URB with success without forwarding. Wheel
        // never sees these bytes; the plugin owns what reaches the wheel.
        WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, 0);
        return;
    }

    // Pass-through path. Log throttled. Distinguish non-owner (informational)
    // vs owner (high-volume; the plugin's own writes flowing through).
    if (writeBuf != NULL && writeLen > 0) {
        BOOLEAN isOwner = (owner != 0 && requestor == owner);
        if (!isOwner) {
            // Non-owner pass: owner was 0 (no plugin claimed yet) OR the
            // intercept branch above didn't take (rare). Worth logging more
            // verbosely so we can see boot-time / pre-plugin writes.
            static volatile LONG s_nonOwnerPassCount = 0;
            LONG np = InterlockedIncrement(&s_nonOwnerPassCount);
            if (np <= 40 || (np % 50) == 1) {
                TFFA_LOG(
                    "TFFAUsbFilter: URB #%ld NONOWNER_PASS fn=0x%04X (%s) len=%lu pid=%lu owner=%lu  %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X\n",
                    np, urbFunction, TFFAUrbFunctionName(urbFunction), writeLen,
                    requestor, owner,
                    pad[0], pad[1], pad[2], pad[3],
                    pad[4], pad[5], pad[6], pad[7],
                    pad[8], pad[9], pad[10], pad[11]);
            }
        } else {
            // Owner (plugin) writes: throttle hard, this is the high-volume
            // TF stream + LED writes + echoed game queries.
            static volatile LONG s_ownerPassCount = 0;
            LONG op = InterlockedIncrement(&s_ownerPassCount);
            if ((op % 500) == 1) {
                TFFA_LOG(
                    "TFFAUsbFilter: URB #%ld OWNER_PASS fn=0x%04X (%s) len=%lu pid=%lu  %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X\n",
                    op, urbFunction, TFFAUrbFunctionName(urbFunction), writeLen,
                    requestor,
                    pad[0], pad[1], pad[2], pad[3],
                    pad[4], pad[5], pad[6], pad[7],
                    pad[8], pad[9], pad[10], pad[11]);
            }
        }
    }

    WDF_REQUEST_SEND_OPTIONS opts;
    WDF_REQUEST_SEND_OPTIONS_INIT(&opts, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
    WDFIOTARGET target = WdfDeviceGetIoTarget(WdfIoQueueGetDevice(Queue));
    if (!WdfRequestSend(Request, target, &opts)) {
        NTSTATUS s = WdfRequestGetStatus(Request);
        TFFA_LOG("TFFAUsbFilter: WdfRequestSend failed 0x%X\n", s);
        WdfRequestComplete(Request, s);
    }
}

VOID
TFFAUsbEvtIoDefault(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request
)
{
    WDF_REQUEST_SEND_OPTIONS opts;
    WDF_REQUEST_SEND_OPTIONS_INIT(&opts, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
    WDFIOTARGET target = WdfDeviceGetIoTarget(WdfIoQueueGetDevice(Queue));
    if (!WdfRequestSend(Request, target, &opts)) {
        WdfRequestComplete(Request, WdfRequestGetStatus(Request));
    }
}

// ---------- Control device handlers -------------------------------------

VOID
TFFAUsbEvtControlFileCreate(
    _In_ WDFDEVICE     Device,
    _In_ WDFREQUEST    Request,
    _In_ WDFFILEOBJECT FileObject
)
{
    UNREFERENCED_PARAMETER(Device);

    ULONG caller = HandleToULong(PsGetCurrentProcessId());
    PTFFAUSB_FILE_CONTEXT fileCtx = TFFAUsbGetFileContext(FileObject);
    if (fileCtx != NULL) fileCtx->ClaimingPid = caller;

    WdfSpinLockAcquire(g_OwnerLock);
    ULONG previousOwner = g_OwnerPid;
    g_OwnerPid = caller;
    WdfSpinLockRelease(g_OwnerLock);

    if (previousOwner == 0) {
        TFFA_LOG("TFFAUsbFilter: control device CLAIMED by pid=%lu\n", caller);
    } else if (previousOwner == caller) {
        TFFA_LOG("TFFAUsbFilter: control device re-opened by owner pid=%lu\n", caller);
    } else {
        TFFA_LOG("TFFAUsbFilter: control device TAKEOVER by pid=%lu (was pid=%lu)\n",
                 caller, previousOwner);
    }
    WdfRequestComplete(Request, STATUS_SUCCESS);
}

VOID
TFFAUsbEvtControlFileCleanup(
    _In_ WDFFILEOBJECT FileObject
)
{
    PTFFAUSB_FILE_CONTEXT fileCtx = TFFAUsbGetFileContext(FileObject);
    ULONG thisHandleClaimer = fileCtx != NULL ? fileCtx->ClaimingPid : 0;

    WdfSpinLockAcquire(g_OwnerLock);
    BOOLEAN released = FALSE;
    if (g_OwnerPid != 0 && g_OwnerPid == thisHandleClaimer) {
        g_OwnerPid = 0;
        released = TRUE;
    }
    ULONG ownerSnapshot = g_OwnerPid;
    WdfSpinLockRelease(g_OwnerLock);

    if (released) {
        TFFA_LOG("TFFAUsbFilter: control device RELEASED (was owner=%lu)\n", thisHandleClaimer);
        if (g_InterceptedQueue != NULL) {
            WdfIoQueuePurgeSynchronously(g_InterceptedQueue);
            WdfIoQueueStart(g_InterceptedQueue);
        }
    } else if (thisHandleClaimer != 0) {
        TFFA_LOG("TFFAUsbFilter: control stale-close ignored (this pid=%lu, current owner=%lu)\n",
                 thisHandleClaimer, ownerSnapshot);
    }
}

VOID
TFFAUsbEvtControlIoDeviceControl(
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
            if (OutputBufferLength < sizeof(ULONG)) {
                WdfRequestComplete(Request, STATUS_BUFFER_TOO_SMALL);
                return;
            }
            PVOID  outBuf = NULL;
            size_t outLen = 0;
            NTSTATUS s = WdfRequestRetrieveOutputBuffer(Request, sizeof(ULONG), &outBuf, &outLen);
            if (!NT_SUCCESS(s)) { WdfRequestComplete(Request, s); return; }
            *(PULONG)outBuf = TFFA_PING_MAGIC;
            WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, sizeof(ULONG));
            return;
        }
        case IOCTL_TFFA_RECV: {
            if (OutputBufferLength == 0) {
                WdfRequestComplete(Request, STATUS_INVALID_PARAMETER);
                return;
            }
            NTSTATUS s = WdfRequestForwardToIoQueue(Request, g_InterceptedQueue);
            if (!NT_SUCCESS(s)) {
                TFFA_LOG("TFFAUsbFilter: RECV forward-to-queue failed 0x%X\n", s);
                WdfRequestComplete(Request, s);
            }
            return;
        }
        default:
            WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
            return;
    }
}

// ---------- Registry fallback (ProtectedPid) ----------------------------
//
// Identical contract to TFFAFilter: when no control-device owner is set,
// we can fall back to a ProtectedPid value under our Parameters key. Used
// by scripted tests; harmless if absent.

static ULONG
TFFAUsbGetProtectedPid(VOID)
{
    UNICODE_STRING keyPath = RTL_CONSTANT_STRING(
        L"\\Registry\\Machine\\SYSTEM\\CurrentControlSet\\Services\\TFFAUsbFilter\\Parameters");
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
