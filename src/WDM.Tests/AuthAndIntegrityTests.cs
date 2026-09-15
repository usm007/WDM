using System.Reflection;
using System.Security.Cryptography;
using WDM.Services;

namespace WDM.Tests;

public sealed class AuthAndIntegrityTests
{
    [Fact]
    public void CaptureToken_RoundTrips()
    {
        string token = CaptureAuth.GetOrCreateToken();
        Assert.True(token.Length >= 32);
        Assert.True(CaptureAuth.Validate(token));
        Assert.True(CaptureAuth.Validate("Bearer " + token));
        Assert.False(CaptureAuth.Validate("wrong-token"));
        Assert.False(CaptureAuth.Validate(null));
        Assert.False(CaptureAuth.Validate(""));
    }

    [Fact]
    public void CaptureServer_RejectsBareLoopbackClients()
    {
        var m = typeof(CaptureServer).GetMethod("IsAuthorized",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        string token = CaptureAuth.GetOrCreateToken();
        // Valid token passes with no Origin (service-worker fetch).
        Assert.True((bool)m.Invoke(null, new object?[] { null, token })!);
        // Bare curl-like client (no token, no Origin) is rejected.
        Assert.False((bool)m.Invoke(null, new object?[] { null, null })!);
        // Wrong token never passes, even with an extension Origin.
        Assert.False((bool)m.Invoke(null, new object?[] { "chrome-extension://abc", "nope" })!);
        // Migration grace: extension Origin without token still passes.
        Assert.True((bool)m.Invoke(null, new object?[] { "chrome-extension://abc", null })!);
        // Web pages stay forbidden regardless of token path (checked separately).
        var web = typeof(CaptureServer).GetMethod("IsBrowserWebOrigin",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True((bool)web.Invoke(null, new object?[] { "https://evil.test" })!);
    }

    [Fact]
    public void VerifyInstallerIntegrity_EnforcesPublishedHash()
    {
        var m = typeof(UpdateChecker).GetMethod("VerifyInstallerIntegrity",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        string path = Path.Combine(Path.GetTempPath(), "wdm-test-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            // Minimal valid PE: MZ + e_lfanew + PE\0\0, padded past 1MB.
            using (var fs = new FileStream(path, FileMode.CreateNew))
            {
                var header = new byte[64];
                header[0] = (byte)'M'; header[1] = (byte)'Z';
                BitConverter.GetBytes(64).CopyTo(header, 0x3C);
                fs.Write(header, 0, header.Length);
                fs.Write(new byte[] { (byte)'P', (byte)'E', 0, 0, 0, 0 }, 0, 6);
                fs.SetLength(1024 * 1024 + 16);
            }
            string good;
            using (var sha = SHA256.Create())
            using (var s = File.OpenRead(path))
            {
                good = Convert.ToHexString(sha.ComputeHash(s));
            }
            // Matching hash passes.
            m.Invoke(null, new object?[] { path, good });
            // Mismatched hash throws and deletes the file.
            var ex = Assert.Throws<TargetInvocationException>(() => m.Invoke(null, new object?[] { path, new string('0', 64) }));
            Assert.IsType<InvalidOperationException>(ex.InnerException);
            Assert.False(File.Exists(path));
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void SingleInstancePipe_NameIsSessionScoped()
    {
        string a = SingleInstancePipe.PipeNameForCurrentSession();
        Assert.StartsWith("WDM.SingleInstance.", a);
        Assert.DoesNotContain("\\", a);
    }
}
