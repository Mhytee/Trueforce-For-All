# test-tffa-control.ps1 - smoke test for the TFFA control device, exposed identically by both TFFAUsbFilter and TFFAFilter.
#
# Opens \\.\TFFAControl (claims ownership for THIS PowerShell process),
# sends an IOCTL_TFFA_PING and verifies the magic number, then optionally
# loops on IOCTL_TFFA_RECV to dump any intercepted HID writes from other
# processes.
#
# Usage:
#   pwsh -File test-tffa-control.ps1                 (PING only, then exit)
#   pwsh -File test-tffa-control.ps1 -Recv -Seconds 20   (PING + RECV loop)
#
# While this script holds the handle open, PowerShell IS the wheel owner.
# Any other process (SimHub, G HUB, games, etc.) writing to the wheel will
# get its bytes intercepted and (if -Recv is set) dumped here. Their wheel
# writes never reach the wheel firmware.

param(
    [switch] $Recv = $false,
    [int]    $Seconds = 10
)

$ErrorActionPreference = 'Stop'

# CTL_CODE(FILE_DEVICE_UNKNOWN=0x22, function, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
# matches driver-side macros. Explicit [uint32] casts because PowerShell
# would otherwise infer signed Int32 and overflow on values with the high bit set.
[uint32] $IOCTL_TFFA_PING = 0x222000
[uint32] $IOCTL_TFFA_RECV = 0x222004
[uint32] $PING_MAGIC      = 0x54464641   # 'TFFA' in ASCII

$signature = @'
[DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
    string lpFileName, uint dwDesiredAccess, uint dwShareMode,
    System.IntPtr lpSecurityAttributes, uint dwCreationDisposition,
    uint dwFlagsAndAttributes, System.IntPtr hTemplateFile);

[DllImport("kernel32.dll", SetLastError=true)]
public static extern bool DeviceIoControl(
    Microsoft.Win32.SafeHandles.SafeFileHandle hDevice, uint dwIoControlCode,
    System.IntPtr lpInBuffer, uint nInBufferSize,
    byte[] lpOutBuffer, uint nOutBufferSize,
    out uint lpBytesReturned, System.IntPtr lpOverlapped);
'@

Add-Type -MemberDefinition $signature -Name K32 -Namespace TFFA -ErrorAction SilentlyContinue | Out-Null

# Open the control device. Windows PowerShell 5.x parses 0xC0000000 as a
# signed Int32 (which overflows the negative range); we need the unsigned
# value, so build it via Convert.ToUInt32 with an explicit hex string.
[uint32] $GENERIC_RW    = [Convert]::ToUInt32("C0000000", 16)
[uint32] $OPEN_EXISTING = 3
$h = [TFFA.K32]::CreateFileW("\\.\TFFAControl", $GENERIC_RW, [uint32]0, [System.IntPtr]::Zero, $OPEN_EXISTING, [uint32]0, [System.IntPtr]::Zero)
if ($h.IsInvalid) {
    $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
    if ($err -eq 170) {
        Write-Host "ERROR: device is BUSY (another process already owns it - close it first)" -ForegroundColor Red
    } elseif ($err -eq 2) {
        Write-Host "ERROR: device not found - is the TFFAFilter driver installed and loaded?" -ForegroundColor Red
    } else {
        Write-Host "CreateFile failed: Win32 error $err" -ForegroundColor Red
    }
    exit 1
}
Write-Host "Opened \\.\TFFAControl - this PowerShell PID ($PID) is now the wheel owner." -ForegroundColor Green

try {
    # PING test.
    $out = New-Object byte[] 4
    $bytes = 0
    $ok = [TFFA.K32]::DeviceIoControl($h, $IOCTL_TFFA_PING, [System.IntPtr]::Zero, 0, $out, 4, [ref]$bytes, [System.IntPtr]::Zero)
    if (-not $ok) {
        $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        Write-Host "PING failed: Win32 error $err" -ForegroundColor Red
        exit 2
    }
    $magic = [System.BitConverter]::ToUInt32($out, 0)
    if ($magic -eq $PING_MAGIC) {
        Write-Host ("PING ok: magic = 0x{0:X8} (channel is alive)" -f $magic) -ForegroundColor Green
    } else {
        Write-Host ("PING returned unexpected magic 0x{0:X8} (expected 0x{1:X8})" -f $magic, $PING_MAGIC) -ForegroundColor Yellow
    }

    if ($Recv) {
        Write-Host "Posting RECV requests for $Seconds seconds. Any non-PowerShell process writing to the wheel will dump its bytes here." -ForegroundColor Cyan
        $until = (Get-Date).AddSeconds($Seconds)
        $count = 0
        while ((Get-Date) -lt $until) {
            $buf = New-Object byte[] 256
            $b = 0
            $ok = [TFFA.K32]::DeviceIoControl($h, $IOCTL_TFFA_RECV, [System.IntPtr]::Zero, 0, $buf, $buf.Length, [ref]$b, [System.IntPtr]::Zero)
            if (-not $ok) {
                $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
                Write-Host "RECV failed: Win32 error $err" -ForegroundColor Red
                break
            }
            $count++
            $dump = ($buf[0..([Math]::Min($b - 1, 11))] | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
            Write-Host ("RECV #{0,-4} {1,3} bytes: {2}" -f $count, $b, $dump)
        }
        Write-Host "RECV loop ended. Total intercepted: $count" -ForegroundColor Cyan
    }
} finally {
    $h.Dispose()
    Write-Host "Released ownership (handle closed)." -ForegroundColor Green
}
