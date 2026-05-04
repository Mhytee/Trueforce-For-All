// TrueforceForAll.LoopbackHelper
//
// Tiny child process spawned by the TrueforceForAll SimHub plugin to perform
// per-process audio loopback. Communicates with the parent via stdin/stdout.
//
// Wire protocol (binary):
//   parent → helper (stdin):  4 bytes little-endian uint32 = target PID.
//                              0 means "stop capture, wait for next PID".
//   helper → parent (stdout): raw 48 kHz / 2-channel / 32-bit IEEE float
//                              audio frames as captured.
//   helper → parent (stderr): one line of text per error/info message.
//
// The helper exits when stdin reaches EOF (parent closed the pipe), or when
// the parent process tracked via the first command-line arg dies.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using TrueforceForAll.LoopbackHelper;

if (args.Length < 1 || !int.TryParse(args[0], out int parentPid))
{
    Console.Error.WriteLine("usage: TrueforceForAll.LoopbackHelper <parentPid>");
    return 2;
}

// Background watchdog: if the parent process dies, exit immediately so we
// don't leave an orphan helper running.
new Thread(() =>
{
    try
    {
        using var p = Process.GetProcessById(parentPid);
        p.WaitForExit();
    }
    catch { /* parent already gone */ }
    Environment.Exit(0);
})
{ IsBackground = true, Name = "ParentWatchdog" }.Start();

var stdin  = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
var stdoutLock = new object();    // serialize concurrent writes from capture callbacks

ProcessLoopbackCapture current = null;
var pidBuf = new byte[4];

while (true)
{
    int got = ReadFull(stdin, pidBuf, 4);
    if (got < 4) break;  // EOF — parent closed stdin
    uint pid = (uint)pidBuf[0] | ((uint)pidBuf[1] << 8) | ((uint)pidBuf[2] << 16) | ((uint)pidBuf[3] << 24);

    try { current?.Dispose(); } catch { }
    current = null;

    if (pid == 0)
    {
        Console.Error.WriteLine("[helper] capture stopped");
        continue;
    }

    try
    {
        var capture = new ProcessLoopbackCapture((int)pid);
        capture.DataAvailable += (s, e) =>
        {
            try
            {
                lock (stdoutLock)
                {
                    stdout.Write(e.Buffer, 0, e.BytesRecorded);
                    stdout.Flush();
                }
            }
            catch
            {
                // stdout broken (parent died) → bail.
                Environment.Exit(0);
            }
        };
        capture.StartRecording();
        current = capture;
        Console.Error.WriteLine($"[helper] capture started PID {pid}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[helper] capture-failed PID {pid}: {ex.Message}");
        current = null;
    }
}

try { current?.Dispose(); } catch { }
return 0;

static int ReadFull(Stream s, byte[] buf, int count)
{
    int total = 0;
    while (total < count)
    {
        int n = s.Read(buf, total, count - total);
        if (n <= 0) return total;
        total += n;
    }
    return total;
}
