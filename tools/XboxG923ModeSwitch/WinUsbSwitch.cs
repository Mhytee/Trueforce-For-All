// Send the Xbox-to-PC mode switch to a G923 over WinUSB.
//
// G HUB binds WinUSB to the Xbox-mode wheel (whole device, class
// LGHUBWinUSB). WinUSB is exclusive: one open handle at a time, so this fails
// while G HUB holds the wheel, and G HUB cannot take it while we hold it.
// The handle is closed before returning either way.
//
// Opening a WinUSB device takes a device interface path. Two candidates, tried
// in order: the interface GUIDs the driver's INF registered (read from the
// device's "Device Parameters" registry key), then the generic
// GUID_DEVINTERFACE_USB_DEVICE every USB device carries. Every attempt is
// logged, because which one works is part of what this test is for.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using static TrueforceForAll.Tools.XboxG923ModeSwitch.Program;

namespace TrueforceForAll.Tools.XboxG923ModeSwitch
{
    internal static class WinUsbSwitch
    {
        // The usb_modeswitch recipe for this wheel: one GIP frame to OUT
        // endpoint 0x01 (command 0x0F, flags 0x00, sequence 0x01, length 0x01,
        // payload 0x42).
        private const byte OutEndpoint = 0x01;
        private static readonly byte[] SwitchFrame = { 0x0F, 0x00, 0x01, 0x01, 0x42 };

        private static readonly Guid GuidUsbDevice = new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED");

        internal enum Outcome { NotOpened, WriteRefused, Sent }

        /// <summary>Developer check: find 046D:{pid} under the generic USB
        /// interface and try to open it the same way Send does. Sends nothing.
        /// On a non-WinUSB device the open or WinUsb_Initialize is expected to
        /// fail; the point is that the device is found and the path is sane.</summary>
        internal static bool Probe(string pid)
        {
            string match = "vid_046d&pid_" + pid.ToLowerInvariant();
            var paths = InterfacePaths(GuidUsbDevice).Where(p => p.ToLowerInvariant().Contains(match)).ToList();
            Log($"Probe {match}: {paths.Count} path(s).");
            foreach (var path in paths)
            {
                Log("  " + path);
                using (var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                          IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, IntPtr.Zero))
                {
                    if (h.IsInvalid) { Log("    Open: " + Win32(Marshal.GetLastWin32Error())); continue; }
                    Log("    Open: ok");
                    if (WinUsb_Initialize(h, out var iface))
                    {
                        Log("    WinUSB: ok");
                        ReportInterfaces(iface);
                        FreeInterfaces(iface);
                    }
                    else Log("    WinUSB: " + Win32(Marshal.GetLastWin32Error()));
                }
            }
            return paths.Count > 0;
        }

        internal static Outcome Send(string pnpDeviceId)
        {
            var guids = new List<(Guid Guid, string Source)>();
            foreach (var g in RegisteredInterfaceGuids(pnpDeviceId)) guids.Add((g, "registered by the driver"));
            if (!guids.Any(g => g.Guid == GuidUsbDevice)) guids.Add((GuidUsbDevice, "generic USB device"));

            string match = "vid_046d&pid_c26d";
            foreach (var (guid, source) in guids)
            {
                var paths = InterfacePaths(guid).Where(p => p.ToLowerInvariant().Contains(match)).ToList();
                Log($"  Interface {guid:B} ({source}): {paths.Count} path(s) for the wheel.");
                foreach (var path in paths)
                {
                    var r = TryPath(path);
                    if (r != Outcome.NotOpened) return r;
                }
            }
            return Outcome.NotOpened;
        }

        private static Outcome TryPath(string path)
        {
            Log("  Opening " + path);
            using (var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                      IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, IntPtr.Zero))
            {
                if (h.IsInvalid)
                {
                    int err = Marshal.GetLastWin32Error();
                    Log($"    Open refused: {Win32(err)}" + Hint(err));
                    return Outcome.NotOpened;
                }
                if (!WinUsb_Initialize(h, out var iface))
                {
                    int err = Marshal.GetLastWin32Error();
                    Log($"    WinUSB would not take the handle: {Win32(err)}");
                    return Outcome.NotOpened;
                }
                try
                {
                    Log("    Opened through WinUSB.");
                    var pipes = ReportInterfaces(iface);

                    // Interface 0 first: that is where the Linux recipe's
                    // endpoint 0x01 lives. Fall back to any OUT pipe and say so.
                    var target = pipes.FirstOrDefault(p => p.PipeId == OutEndpoint);
                    if (target.Iface == IntPtr.Zero)
                    {
                        target = pipes.FirstOrDefault(p => (p.PipeId & 0x80) == 0);
                        if (target.Iface == IntPtr.Zero)
                        {
                            Log("    No OUT endpoint found. Nothing sent.");
                            return Outcome.WriteRefused;
                        }
                        Log($"    Endpoint 0x01 not found; using OUT endpoint 0x{target.PipeId:X2} instead.");
                    }

                    var inPipe = pipes.FirstOrDefault(p => p.Iface == target.Iface && (p.PipeId & 0x80) != 0);
                    if (inPipe.Iface != IntPtr.Zero) DrainIn(inPipe, "before sending", 2);

                    uint timeout = 1000;
                    WinUsb_SetPipePolicy(target.Iface, target.PipeId, PIPE_TRANSFER_TIMEOUT, 4, ref timeout);
                    Log($"    {Stamp()} Writing {Hex(SwitchFrame, SwitchFrame.Length)} to endpoint 0x{target.PipeId:X2}...");
                    if (!WinUsb_WritePipe(target.Iface, target.PipeId, SwitchFrame, (uint)SwitchFrame.Length, out uint written, IntPtr.Zero))
                    {
                        int err = Marshal.GetLastWin32Error();
                        Log($"    Write refused: {Win32(err)}");
                        return Outcome.WriteRefused;
                    }
                    Log($"    Write accepted ({written} of {SwitchFrame.Length} bytes).");

                    // The wheel may answer before it drops off the bus. Errors
                    // here are expected once it starts to detach.
                    if (inPipe.Iface != IntPtr.Zero) DrainIn(inPipe, "after sending", 4);
                    return Outcome.Sent;
                }
                finally
                {
                    FreeInterfaces(iface);
                    Log("    WinUSB handle closed.");
                }
            }
        }

        private struct Pipe
        {
            public IntPtr Iface;
            public byte InterfaceIndex;
            public byte PipeId;
        }

        private static readonly List<IntPtr> Associated = new List<IntPtr>();

        private static List<Pipe> ReportInterfaces(IntPtr first)
        {
            var pipes = new List<Pipe>();
            Associated.Clear();
            ReportOne(first, 0, pipes);
            for (byte i = 0; i < 8; i++)
            {
                if (!WinUsb_GetAssociatedInterface(first, i, out var assoc)) break;
                Associated.Add(assoc);
                ReportOne(assoc, (byte)(i + 1), pipes);
            }
            return pipes;
        }

        private static void ReportOne(IntPtr iface, byte index, List<Pipe> pipes)
        {
            if (!WinUsb_QueryInterfaceSettings(iface, 0, out var d))
            {
                Log($"    Interface #{index}: could not read settings: {Win32(Marshal.GetLastWin32Error())}");
                return;
            }
            Log($"    Interface #{index}: number {d.bInterfaceNumber}, class 0x{d.bInterfaceClass:X2} "
                + $"subclass 0x{d.bInterfaceSubClass:X2} protocol 0x{d.bInterfaceProtocol:X2}, {d.bNumEndpoints} endpoint(s)");
            for (byte p = 0; p < d.bNumEndpoints; p++)
            {
                if (!WinUsb_QueryPipe(iface, 0, p, out var info)) continue;
                string type = info.PipeType == 2 ? "bulk" : info.PipeType == 3 ? "interrupt" : info.PipeType == 1 ? "isochronous" : "control";
                Log($"      Endpoint 0x{info.PipeId:X2} {((info.PipeId & 0x80) != 0 ? "IN " : "OUT")} {type}, "
                    + $"max packet {info.MaximumPacketSize}, interval {info.Interval}");
                pipes.Add(new Pipe { Iface = iface, InterfaceIndex = index, PipeId = info.PipeId });
            }
        }

        private static void DrainIn(Pipe p, string when, int maxReads)
        {
            uint timeout = 300;
            WinUsb_SetPipePolicy(p.Iface, p.PipeId, PIPE_TRANSFER_TIMEOUT, 4, ref timeout);
            var buf = new byte[64];
            for (int i = 0; i < maxReads; i++)
            {
                if (!WinUsb_ReadPipe(p.Iface, p.PipeId, buf, (uint)buf.Length, out uint n, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_SEM_TIMEOUT) Log($"    Read {when} from 0x{p.PipeId:X2} stopped: {Win32(err)}");
                    else if (i == 0) Log($"    Nothing from the wheel on 0x{p.PipeId:X2} {when}.");
                    return;
                }
                Log($"    {Stamp()} Wheel sent on 0x{p.PipeId:X2} {when}: {Hex(buf, (int)n)}");
            }
        }

        private static void FreeInterfaces(IntPtr first)
        {
            foreach (var a in Associated) { try { WinUsb_Free(a); } catch { } }
            Associated.Clear();
            try { WinUsb_Free(first); } catch { }
        }

        // ---- finding the device path ----

        private static IEnumerable<Guid> RegisteredInterfaceGuids(string pnpDeviceId)
        {
            var found = new List<Guid>();
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + pnpDeviceId + @"\Device Parameters"))
                {
                    if (k == null) { Log("  No Device Parameters key for the wheel."); return found; }
                    var values = new List<string>();
                    if (k.GetValue("DeviceInterfaceGUIDs") is string[] many) values.AddRange(many);
                    if (k.GetValue("DeviceInterfaceGUID") is string one) values.Add(one);
                    foreach (var v in values)
                        if (Guid.TryParse(v, out var g) && !found.Contains(g)) found.Add(g);
                    Log("  Interface GUIDs the driver registered: "
                        + (found.Count == 0 ? "none" : string.Join(", ", found.Select(g => g.ToString("B")))));
                }
            }
            catch (Exception ex) { Log("  Could not read the wheel's registry key: " + Describe(ex)); }
            return found;
        }

        private static List<string> InterfacePaths(Guid guid)
        {
            var paths = new List<string>();
            var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == INVALID_HANDLE_VALUE) return paths;
            try
            {
                for (uint i = 0; ; i++)
                {
                    var did = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref did)) break;
                    SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
                    if (size == 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        // cbSize is the fixed part: a DWORD plus one WCHAR, padded
                        // to 6 on x86 and 8 on x64.
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref did, buf, size, out _, IntPtr.Zero))
                            paths.Add(Marshal.PtrToStringUni(buf + 4));
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return paths;
        }

        private static string Hint(int err)
        {
            if (err == 5 || err == 32)
                return ". Another program has the wheel open, most likely G HUB or its background service.";
            return "";
        }

        private static string Win32(int err) => $"error {err} ({new Win32Exception(err).Message})";

        private static string Hex(byte[] b, int n) =>
            string.Join(" ", b.Take(Math.Max(0, n)).Select(x => x.ToString("x2")));

        // ---- interop ----

        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        private const uint PIPE_TRANSFER_TIMEOUT = 0x03;
        private const int ERROR_SEM_TIMEOUT = 121;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct USB_INTERFACE_DESCRIPTOR
        {
            public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints,
                        bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINUSB_PIPE_INFORMATION
        {
            public int PipeType;
            public byte PipeId;
            public ushort MaximumPacketSize;
            public byte Interval;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfoData, ref Guid guid, uint index,
                                                               ref SP_DEVICE_INTERFACE_DATA did);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA did, IntPtr detail,
                                                                   uint detailSize, out uint requiredSize, IntPtr devInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
                                                        uint creation, uint flags, IntPtr template);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Initialize(SafeFileHandle device, out IntPtr iface);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Free(IntPtr iface);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_GetAssociatedInterface(IntPtr iface, byte index, out IntPtr associated);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryInterfaceSettings(IntPtr iface, byte altIndex, out USB_INTERFACE_DESCRIPTOR desc);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryPipe(IntPtr iface, byte altIndex, byte pipeIndex, out WINUSB_PIPE_INFORMATION info);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_SetPipePolicy(IntPtr iface, byte pipeId, uint policy, uint length, ref uint value);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_WritePipe(IntPtr iface, byte pipeId, byte[] buffer, uint length,
                                                    out uint transferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ReadPipe(IntPtr iface, byte pipeId, byte[] buffer, uint length,
                                                   out uint transferred, IntPtr overlapped);
    }
}
