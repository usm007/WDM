using System.Security.Cryptography;

namespace WDM.Services;

/// <summary>Per-install shared secret between the desktop app and its browser
/// extension. Closes the unauthenticated-loopback hole: bare
/// <c>curl http://127.0.0.1:17530/download</c> with no token is rejected, while
/// the deployed extension (which ships <c>wdm-token.json</c>, written by
/// <see cref="BrowserIntegration.DeployExtension"/>) authenticates every call.
/// The server-side secret (<c>capture.token</c>) is DPAPI-encrypted to the
/// Windows user account; the deployed copy stays plaintext because the
/// extension's JS cannot decrypt DPAPI (both live in the user's own profile,
/// and Origin/SSRF checks remain as defense in depth).</summary>
public static class CaptureAuth
{
    public const string HeaderName = "X-WDM-Token";
    public const string TokenFileName = "capture.token";
    public const string ExtensionTokenFileName = "wdm-token.json";

    public static string TokenFilePath => Path.Combine(TaskStore.AppDir, TokenFileName);

    private static string? _cached;

    // Fixed DPAPI entropy (not a key — just binds the blob to this app).
    // Storage now delegates to the shared WDM.Services.DataProtector.
    internal static string ProtectToken(string token) => DataProtector.ProtectToBase64(token);

    internal static string? TryUnprotectToken(string stored) =>
        DataProtector.TryUnprotectFromBase64(stored) is string s && s.Length >= 32 ? s : null;

    /// <summary>Loads the install token, creating and persisting one on first run.
    /// Legacy plaintext files are migrated to DPAPI on read.</summary>
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
                string? token = TryUnprotectToken(existing);
                if (token is null && existing.Length >= 32)
                {
                    // Legacy plaintext install: adopt, then protect at rest.
                    token = existing;
                    try { File.WriteAllText(path, ProtectToken(token)); }
                    catch { }
                }
                if (token is not null)
                {
                    _cached = token;
                    return _cached;
                }
            }
            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            string fresh = Convert.ToHexString(bytes).ToLowerInvariant();
            try { File.WriteAllText(path, ProtectToken(fresh)); }
            catch { /* read-only profile: memory-only token for this session */ }
            _cached = fresh;
            return fresh;
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
