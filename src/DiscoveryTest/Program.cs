// Tiny harness for TrueforceForAll.Core.WheelUsbDiscovery.Find().
//
// Run:  dotnet run --project src\DiscoveryTest
//
// Prints every log line discovery emits, then the final result. Use this to
// verify discovery works on a given machine before launching SimHub — if it
// finds the wheel here, it'll find it from the plugin too.

using System;
using System.IO;
using TrueforceForAll.Core;

namespace TrueforceForAll.DiscoveryTest;

public static class Program
{
    public static int Main(string[] args)
    {
        string usbPcapCmd = LocateUsbPcapCmd();
        if (usbPcapCmd == null)
        {
            Console.Error.WriteLine("USBPcapCMD.exe not found in standard install paths.");
            Console.Error.WriteLine("Install USBPcap from https://desowin.org/usbpcap/ and try again.");
            return 1;
        }

        Console.Error.WriteLine($"Using USBPcapCMD: {usbPcapCmd}");
        Console.Error.WriteLine($"Supported wheels (VID 0x{WheelDiscovery.LogitechVid:X4}):");
        foreach (var (pid, model) in WheelDiscovery.SupportedPids)
            Console.Error.WriteLine($"  PID 0x{pid:X4}  {model}");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Scanning USBPcap interfaces...");
        Console.Error.WriteLine(new string('-', 60));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hit = WheelUsbDiscovery.Find(
            usbPcapCmd,
            log: msg => Console.Error.WriteLine($"  {msg}"));
        sw.Stop();

        Console.Error.WriteLine(new string('-', 60));
        Console.Error.WriteLine($"Discovery took {sw.ElapsedMilliseconds} ms");
        Console.Error.WriteLine();

        if (hit == null)
        {
            Console.Error.WriteLine("RESULT: no supported wheel found.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Things to check:");
            Console.Error.WriteLine("  - Wheel is plugged in and powered on");
            Console.Error.WriteLine("  - Logitech G HUB is closed (it can hold the HID interface)");
            Console.Error.WriteLine("  - USBPcap driver is installed (reboot after install)");
            Console.Error.WriteLine("  - If logs above mention 'Access is denied', re-run elevated");
            return 2;
        }

        Console.Error.WriteLine("RESULT: wheel found.");
        Console.WriteLine($"Model:     {hit.Model}");
        Console.WriteLine($"VID/PID:   0x{hit.Vid:X4} / 0x{hit.Pid:X4}");
        Console.WriteLine($"Interface: {hit.Interface}");
        Console.WriteLine($"Address:   {hit.DeviceAddress}");
        return 0;
    }

    private static string LocateUsbPcapCmd()
    {
        string fromEnv = Environment.GetEnvironmentVariable("USBPCAPCMD");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        string[] candidates =
        {
            @"C:\Program Files\USBPcap\USBPcapCMD.exe",
            @"C:\Program Files (x86)\USBPcap\USBPcapCMD.exe",
        };
        foreach (var p in candidates)
            if (File.Exists(p)) return p;
        return null;
    }
}
