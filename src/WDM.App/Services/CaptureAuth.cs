using System.Security.Cryptography;

namespace WDM.Services;

/// <summary>Per-install shared secret between the desktop app and its browser
/// extension. Closes the unauthenticated-loopback hole: bare
/// <c>curl http://127.0.0.1:17530/download</c> with no token is rejected, while
/// the deployed extension (which ships <c>wdm-token.json</c>, written by
/// <see cref="BrowserIntegration.DeployExtension"/>) authenticates every call.
/// A local process can still read the token file — like any loopback secret —
/// so the SSRF allow-list and Origin checks remain as defense in depth.</summary>
public static class CaptureAuth
{
    public const string HeaderName = "X-WDM-Token";
    public const string TokenFileName = "capture.token";
    public const string ExtensionTokenFileName = "wdm-token.json";

    public static string TokenFilePath => Path.Combine(TaskStore.AppDir, TokenFileName);

    private static string? _cached;

    /// <summary>Loads the install token, creating and persisting one on first run.</summary>
    public static string GetOrCreateToken()
    {
        if (!string.IsNullOrEmpty(_cached))
            return _cached;
        try
        {
            Directory.CreateDirectory(TaskStore.AppDir);
            string path = TokenFilePath;
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 32)
                {
                    _cached = existing;
                    return _cached;
                }
            }
            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            string token = Convert.ToHexString(bytes).ToLowerInvariant();
            try { File.WriteAllText(path, token); }
            catch { /* read-only profile: memory-only token for this session */ }
            _cached = token;
            return token;
        }
        catch
        {
            _cached ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            return _cached;
        }
    }

    /// <summary>Reloads the token from disk (rotation / AppDir redirection).</summary>
    public static void Reload()
    {
        _cached = null;
        GetOrCreateToken();
    }

    /// <summary>Constant-time comparison against the install token.</summary>
    public static bool Validate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        // Strip a "Bearer " prefix when callers pass the raw Authorization value.
        string got = candidate.Trim();
        if (got.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            got = got["Bearer ".Length..].Trim();
        string expected = GetOrCreateToken().Trim();
        if (!SlowEquals(expected, got))
        {
            // Token may have rotated (or AppDir redirected, e.g. tests):
            // re-read once before rejecting (BUG-022).
            Reload();
            expected = (_cached ?? "").Trim();
            return SlowEquals(expected, got);
        }
        return true;
    }

    private static bool SlowEquals(string expected, string got)
    {
        if (expected.Length == 0 || expected.Length != got.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(got));
    }
}
