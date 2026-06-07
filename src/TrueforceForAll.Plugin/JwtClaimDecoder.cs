// B5/F01: JWT email claim extractor. GetAccessTokenAsync compares
// the JWT's email claim against the persisted AuthSession.Email; a
// mismatch means the local Settings.AuthSession.Email was tampered
// with (or the JWT belongs to another account), so the session is
// cleared. Base64url padding handled the same way GetCurrentSessionInfo
// already does (see CommunityAuth.cs).

using System;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    internal static class JwtClaimDecoder
    {
        /// <summary>Decode the email claim from a JWT access token.
        /// Returns the email string (lowercased) or null if parsing fails
        /// or the claim is missing. Handles base64url padding gracefully.</summary>
        public static string DecodeEmailClaim(string accessToken)
        {
            if (string.IsNullOrEmpty(accessToken)) return null;
            try
            {
                string[] parts = accessToken.Split('.');
                if (parts.Length < 2) return null;
                string payload = parts[1];
                payload = payload.Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }
                byte[] decoded = Convert.FromBase64String(payload);
                string json = Encoding.UTF8.GetString(decoded);
                var claims = JObject.Parse(json);
                string email = claims["email"]?.ToString();
                return string.IsNullOrEmpty(email) ? null : email.ToLowerInvariant();
            }
            catch { return null; }
        }
    }
}
