using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TrueforceForAll.Plugin
{
    // Centralized URL and content validation gate for community backend,
    // GitHub release downloads, and bundled installer signature verification.
    // Imported by CommunityAuth, CommunityClient, PresetSharingClient,
    // UpdateChecker, and the USBPcap reinstall path.
    internal static class ChannelValidation
    {
        // Hard 5 MB ceiling for preset/pack/engine body downloads. Larger
        // payloads almost certainly mean a misconfigured row or a hostile
        // server response and should be refused before being parsed.
        private const long MAX_PRESET_BODY_BYTES = 5_000_000L;

        internal static long MaxPresetBodyBytes => MAX_PRESET_BODY_BYTES;

        // Accept only https://*.supabase.co (or supabase.co bare). Rejects
        // http://, custom domains, and any other host. Trims and case-folds
        // so settings with stray whitespace still pass.
        internal static bool IsTrustedSupabaseUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            url = url.Trim();
            if (url.Length == 0) return false;

            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return false;

            string host = ExtractHost(url);
            if (string.IsNullOrEmpty(host)) return false;

            host = host.ToLowerInvariant();
            if (host.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase))
                return true;
            if (host.Equals("supabase.co", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        // Accept only release assets served from the canonical Mhytee
        // Trueforce-For-All GitHub repo. Any other owner/repo or any path
        // outside /releases/download/ is refused.
        private static readonly Regex GitHubReleaseRegex = new Regex(
            @"^/Mhytee/Trueforce-For-All/releases/download/[^/]+/.+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static bool IsTrustedGitHubReleaseUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            url = url.Trim();
            if (url.Length == 0) return false;

            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
                return false;

            string path = ExtractPath(url);
            if (string.IsNullOrEmpty(path)) return false;
            return GitHubReleaseRegex.IsMatch(path);
        }

        // Authenticode signature gate for the bundled USBPcap installer.
        // Release builds always require a valid signature. Debug builds may
        // opt in to an unsigned local test binary via the TRUEFORCE_DEV_UNSIGNED
        // environment variable; the entire escape hatch is compiled out of
        // Release so shipping binaries cannot be tricked into trusting an
        // arbitrary exe with an env-var flag.
        internal static bool VerifyAuthenticodeSignature(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
                return false;

            try
            {
#if DEBUG
                string devFlag = System.Environment.GetEnvironmentVariable("TRUEFORCE_DEV_UNSIGNED");
                if (!string.IsNullOrEmpty(devFlag)) return true;
#endif
                return WinVerifyTrust(filePath);
            }
            catch
            {
                return false;
            }
        }

        private static string ExtractHost(string url)
        {
            try
            {
                var uri = new Uri(url, UriKind.Absolute);
                return uri.Host;
            }
            catch { return null; }
        }

        private static string ExtractPath(string url)
        {
            try
            {
                var uri = new Uri(url, UriKind.Absolute);
                return uri.AbsolutePath;
            }
            catch { return null; }
        }

        private static bool WinVerifyTrust(string filePath)
        {
            try
            {
                var wvt = new WinVerifyTrustApi();
                return wvt.VerifyFile(filePath);
            }
            catch { return false; }
        }
    }

    internal sealed class WinVerifyTrustApi
    {
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true, CallingConvention = CallingConvention.StdCall)]
        private static extern uint WinVerifyTrust(IntPtr hWnd, IntPtr pgActionID, IntPtr pWVTData);

        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new Guid("00aac56b-cd44-11d0-8cc2-00c04fc295ee");
        private const uint ERROR_SUCCESS = 0;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pUnionData;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }

        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;

        internal bool VerifyFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;

            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                pcwszFilePath = filePath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };

            var wtData = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pUnionData = Marshal.AllocHGlobal(Marshal.SizeOf(fileInfo)),
                dwStateAction = WTD_STATEACTION_VERIFY,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = null,
                dwProvFlags = 0,
                dwUIContext = 0,
            };

            try
            {
                Marshal.StructureToPtr(fileInfo, wtData.pUnionData, false);
                IntPtr pgActionID = Marshal.AllocHGlobal(Marshal.SizeOf(WINTRUST_ACTION_GENERIC_VERIFY_V2));
                Marshal.StructureToPtr(WINTRUST_ACTION_GENERIC_VERIFY_V2, pgActionID, false);

                try
                {
                    IntPtr pWVTData = Marshal.AllocHGlobal(Marshal.SizeOf(wtData));
                    Marshal.StructureToPtr(wtData, pWVTData, false);

                    try
                    {
                        uint hr = WinVerifyTrust(new IntPtr(-1), pgActionID, pWVTData);
                        return hr == ERROR_SUCCESS;
                    }
                    finally
                    {
                        wtData.dwStateAction = WTD_STATEACTION_CLOSE;
                        Marshal.StructureToPtr(wtData, pWVTData, true);
                        WinVerifyTrust(new IntPtr(-1), pgActionID, pWVTData);
                        Marshal.FreeHGlobal(pWVTData);
                    }
                }
                finally { Marshal.FreeHGlobal(pgActionID); }
            }
            finally { Marshal.FreeHGlobal(wtData.pUnionData); }
        }
    }
}
