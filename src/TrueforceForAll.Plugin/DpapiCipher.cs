// B5/F03: DPAPI-encrypt AccessToken + RefreshToken at rest. Uses
// CurrentUser scope (a different Windows user cannot decrypt). The
// "dpapi:" marker lets SaveSession be idempotent and lets the load
// path detect legacy plaintext for silent migration.
//
// Encoding note: tokens are .NET strings (UTF-16 internally), but we
// encrypt/decrypt the UTF-8 byte representation symmetrically. Round
// trip is safe as long as the same encoding is used on both sides,
// which it is here. Settings are serialized as JSON by Newtonsoft,
// which preserves .NET string content verbatim, so no encoding drift.

using System;
using System.Security.Cryptography;
using System.Text;

namespace TrueforceForAll.Plugin
{
    internal static class DpapiCipher
    {
        public const string DpapiMarker = "dpapi:";

        /// <summary>Encrypt plaintext to a DPAPI-protected base64 string
        /// with the "dpapi:" prefix. Returns null on failure (caller may
        /// fall back to the original plaintext rather than dropping the
        /// session). Idempotent: passing an already-encrypted value
        /// (starting with "dpapi:") returns it unchanged.</summary>
        public static string Protect(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return null;
            // Already encrypted: avoid double-wrapping.
            if (plaintext.StartsWith(DpapiMarker, StringComparison.Ordinal)) return plaintext;
            try
            {
                byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
                byte[] encryptedBytes = ProtectedData.Protect(plaintextBytes, null, DataProtectionScope.CurrentUser);
                string base64 = Convert.ToBase64String(encryptedBytes);
                return DpapiMarker + base64;
            }
            catch { return null; }
        }

        /// <summary>Decrypt a DPAPI-protected string (with "dpapi:" prefix).
        /// Returns null if the prefix is absent (legacy plaintext: caller
        /// should fall back to the raw value) or if decryption fails
        /// (e.g., wrong user or corrupted data: caller should clear).</summary>
        public static string Unprotect(string ciphertext)
        {
            if (string.IsNullOrEmpty(ciphertext)) return null;
            if (!ciphertext.StartsWith(DpapiMarker, StringComparison.Ordinal)) return null;
            try
            {
                string base64 = ciphertext.Substring(DpapiMarker.Length);
                byte[] encryptedBytes = Convert.FromBase64String(base64);
                byte[] plaintextBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plaintextBytes);
            }
            catch { return null; }
        }

        /// <summary>True if the value looks DPAPI-wrapped (has the marker).
        /// Used by the load-time migration to skip already-encrypted
        /// tokens without a decrypt attempt.</summary>
        public static bool IsProtected(string value)
            => !string.IsNullOrEmpty(value) && value.StartsWith(DpapiMarker, StringComparison.Ordinal);
    }
}
