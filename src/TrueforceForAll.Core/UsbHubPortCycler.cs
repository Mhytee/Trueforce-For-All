// Software "unplug and replug" of one USB device, by asking its parent hub to
// cycle the port the device is on (IOCTL_USB_HUB_CYCLE_PORT). The device is
// disconnected and re-enumerated exactly like a physical replug, so it gets a
// brand-new device PDO. That is what USBPcap needs to attach a capture filter
// when it missed the device at boot (see BlindCaptureClassifier for why).
//
// Why a hub port cycle and not a PnP restart: a PnP restart (pnputil
// /restart-device, Device Manager disable/enable) is refused while another
// process holds the device's HID handle, which SimHub always does; it returned
// "System reboot is needed" on the owner rig while leaving the capture blind.
// The hub port cycle succeeds regardless of who holds the device (validated on
// the owner's G PRO, 2026-08-30: a real ~5 s disconnect/reconnect while SimHub
// was streaming, after which the plugin recovered on its own).
//
// Requires the process to be elevated (the hub control device is opened for
// write). SimHub already requires admin for the USBPcap FFB tap, so callers
// are elevated whenever the tap runs.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrueforceForAll.Core
{
    public static class UsbHubPortCycler
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfo, IntPtr devInfoData, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devInfo, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int detailSize, out int required, IntPtr devInfoData);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

        private const int DIGCF_PRESENT = 0x2;
        private const int DIGCF_DEVICEINTERFACE = 0x10;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;

        // CTL_CODE(FILE_DEVICE_USB = 0x22, function, METHOD_BUFFERED, FILE_ANY_ACCESS)
        private const uint IOCTL_USB_GET_NODE_INFORMATION = 0x220408;               // fn 258
        private const uint IOCTL_USB_HUB_CYCLE_PORT = 0x220444;                     // fn 273
        private const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX = 0x220448; // fn 274

        private static readonly Guid GUID_DEVINTERFACE_USB_HUB =
            new Guid("F18A0E88-C30C-11D0-8815-00A0C906BED8");

        // USB_NODE_CONNECTION_INFORMATION_EX is byte-packed: the 18-byte
        // USB_DEVICE_DESCRIPTOR sits at offset 8, so idVendor is at 8+8=16... in
        // the *unpacked* C layout. The wire buffer the hub returns is packed:
        // idVendor at 12, idProduct at 14 (validated against live devices on the
        // owner rig, 2026-08-30). ConnectionIndex is echoed at offset 0.
        private const int OFF_ID_VENDOR = 12;
        private const int OFF_ID_PRODUCT = 14;

        private sealed class PortLocation
        {
            public string HubPath;
            public int Port;
        }

        /// <summary>
        /// Find the wheel by VID/PID and cycle its hub port. Returns true only
        /// when the port-cycle IOCTL succeeded. Never throws; failures are
        /// reported through <paramref name="log"/> and a false return.
        /// </summary>
        public static bool TryCycleDevicePort(ushort vid, ushort pid, Action<string> log, out string detail)
        {
            detail = null;
            try
            {
                var loc = FindPort(vid, pid, log);
                if (loc == null)
                {
                    detail = $"device {vid:X4}:{pid:X4} not found on any hub port";
                    return false;
                }

                using (var h = CreateFile(loc.HubPath, GENERIC_WRITE, FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid)
                    {
                        detail = $"could not open hub for write (error {Marshal.GetLastWin32Error()})";
                        return false;
                    }

                    // USB_CYCLE_PORT_PARAMS: ConnectionIndex (ULONG) in, plus a
                    // StatusReturned (ULONG) out. Eight bytes covers both.
                    var prm = new byte[8];
                    BitConverter.GetBytes(loc.Port).CopyTo(prm, 0);
                    bool ok = DeviceIoControl(h, IOCTL_USB_HUB_CYCLE_PORT, prm, prm.Length, prm, prm.Length, out _, IntPtr.Zero);
                    int err = Marshal.GetLastWin32Error();
                    int statusReturned = BitConverter.ToInt32(prm, 4);
                    if (ok)
                    {
                        detail = $"cycled port {loc.Port} (StatusReturned=0x{statusReturned:x8})";
                        return true;
                    }
                    detail = $"cycle of port {loc.Port} failed (Win32 error {err}, StatusReturned=0x{statusReturned:x8})";
                    return false;
                }
            }
            catch (Exception ex)
            {
                detail = "port cycle threw: " + ex.Message;
                return false;
            }
        }

        private static PortLocation FindPort(ushort vid, ushort pid, Action<string> log)
        {
            foreach (var hub in EnumerateHubs())
            {
                using (var h = CreateFile(hub, GENERIC_WRITE, FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) continue;

                    var node = new byte[80];
                    if (!DeviceIoControl(h, IOCTL_USB_GET_NODE_INFORMATION, node, node.Length, node, node.Length, out _, IntPtr.Zero))
                        continue;
                    int ports = node[6]; // USB_HUB_INFORMATION.HubDescriptor.bNumberOfPorts

                    for (int p = 1; p <= ports; p++)
                    {
                        var ci = new byte[4096];
                        BitConverter.GetBytes(p).CopyTo(ci, 0); // ConnectionIndex
                        if (!DeviceIoControl(h, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX, ci, ci.Length, ci, ci.Length, out _, IntPtr.Zero))
                            continue;
                        int dvid = BitConverter.ToUInt16(ci, OFF_ID_VENDOR);
                        int dpid = BitConverter.ToUInt16(ci, OFF_ID_PRODUCT);
                        if (dvid == vid && dpid == pid)
                            return new PortLocation { HubPath = hub, Port = p };
                    }
                }
            }
            return null;
        }

        private static List<string> EnumerateHubs()
        {
            var hubs = new List<string>();
            Guid g = GUID_DEVINTERFACE_USB_HUB;
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return hubs;
            try
            {
                for (int i = 0; ; i++)
                {
                    var data = new SP_DEVICE_INTERFACE_DATA();
                    data.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref data)) break;

                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int required, IntPtr.Zero);
                    if (required <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(required);
                    try
                    {
                        // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on 64-bit
                        // (4-byte int + 2-byte WCHAR + 2 pad), 6 on 32-bit.
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, buf, required, out required, IntPtr.Zero))
                        {
                            string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                            if (!string.IsNullOrEmpty(path)) hubs.Add(path);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return hubs;
        }
    }
}
