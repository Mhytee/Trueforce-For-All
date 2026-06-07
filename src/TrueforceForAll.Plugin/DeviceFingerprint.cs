// B5/F07: Stable device fingerprint for session binding. We hash
// HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid (a Windows-install
// identifier that survives reboots but differs between machines) +
// the lowercased username, so an AuthSession copied from one
// machine/user to another fails the device check in GetAccessTokenAsync.
// Registry read is wrapped in try/catch: on unusual setups without a
// MachineGuid we return an empty fingerprint and the caller skips the
// device check (fail-open: don't lock the user out, but no extra
// protection either).

using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace TrueforceForAll.Plugin
{
    internal static class DeviceFingerprint
    {
        /// <summary>Compute a stable device fingerprint from MachineGuid
        /// (HKLM\SOFTWARE\Microsoft\Cryptography) + lowercased username.
        /// Returns SHA256 hex (lowercase). Falls back to empty string if
        /// registry is inaccessible (e.g., unusual Windows setup).</summary>
        public static string Compute()
        {
            try
            {
                string machineGuid = "";
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                    {
                        if (key != null)
                        {
                            var val = key.GetValue("MachineGuid");
                            if (val != null) machineGuid = val.ToString();
                        }
                    }
                }
                catch { }
                string username = Environment.UserName?.ToLowerInvariant() ?? "";
                string combined = machineGuid + "|" + username;
                if (string.IsNullOrEmpty(machineGuid) && string.IsNullOrEmpty(username)) return "";
                using (var sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(combined));
                    var sb = new StringBuilder(hash.Length * 2);
                    foreach (byte b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return ""; }
        }
    }
}
