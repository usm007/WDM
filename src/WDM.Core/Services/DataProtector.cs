using System;
using System.Security.Cryptography;
using System.Text;

namespace WDM.Services;

/// <summary>DPAPI user-scope protection for on-disk secrets (loopback tokens,
/// browser cookies). Windows-only by design (CurrentUser scope ties blobs to
/// this user on this machine — copied files are useless elsewhere).</summary>
public static class DataProtector
{
    // Fixed entropy binds blobs to this app; the actual key stays in DPAPI.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WDM.DataProtector.v1");

    public static string ProtectToBase64(string plain)
    {
        byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }

    /// <summary>Returns the plaintext, or null when the input is not a
    /// blob this user can open (foreign machine/user, corruption, or legacy
    /// plaintext — callers decide the fallback).</summary>
    public static string? TryUnprotectFromBase64(string? stored)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(stored))
                return null;
            byte[] cipher = Convert.FromBase64String(stored.Trim());
            if (cipher.Length == 0 || cipher.Length > 4 * 1024 * 1024)
                return null;
            string plain = Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser)).Trim();
            return plain.Length > 0 ? plain : null;
        }
        catch { return null; }
    }
}
