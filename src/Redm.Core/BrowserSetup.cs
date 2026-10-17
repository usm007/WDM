namespace Redm.Core;

public record BrowserSetupResult(bool Ok, string Message);

public static class BrowserSetup
{
    public static string ManifestDir()
        => System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "ReDM");

    public static BrowserSetupResult InstallChrome(string msgHostExe, IEnumerable<string>? extraIds = null)
    {
        try
        {
            var ids = new List<string>(extraIds ?? Enumerable.Empty<string>());
            if (ids.Count == 0) ids.AddRange(new[] { "ngpampappnmepgilojfohadhhmbhlaek", "llbjbkhnmlidjebalopleeepgdfgcpec" });
            string dir = ManifestDir();
            System.IO.Directory.CreateDirectory(dir);
            string manifest = System.IO.Path.Combine(dir, "chrome-host.json");
            string origins = string.Join(",", ids.Select(id => $"\"chrome-extension://{id}/\""));
            string json = "{\"name\":\"com.redm.host\",\"description\":\"ReDM extension helper\",\"type\":\"stdio\",\"allowed_origins\":[" + origins + "],\"path\":\"" + msgHostExe.Replace("\\", "\\\\") + "\"}";
            System.IO.File.WriteAllText(manifest, json);
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Google\Chrome\NativeMessagingHosts\com.redm.host");
            key?.SetValue("", manifest);
            using var edge = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.redm.host");
            edge?.SetValue("", manifest);
            return new(true, "Chrome and Edge helper registered. Load the extension next.");
        }
        catch (Exception ex) { return new(false, ex.Message.Split('\n')[0]); }
    }

    public static BrowserSetupResult InstallFirefox(string msgHostExe)
    {
        try
        {
            string dir = ManifestDir();
            System.IO.Directory.CreateDirectory(dir);
            string manifest = System.IO.Path.Combine(dir, "firefox-host.json");
            string json = "{\"name\":\"com.redm.host\",\"description\":\"ReDM extension helper\",\"type\":\"stdio\",\"allowed_extensions\":[\"redm@redm.app\"],\"path\":\"" + msgHostExe.Replace("\\", "\\\\") + "\"}";
            System.IO.File.WriteAllText(manifest, json);
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Mozilla\NativeMessagingHosts\com.redm.host");
            key?.SetValue("", manifest);
            return new(true, "Firefox helper registered. Add the extension next.");
        }
        catch (Exception ex) { return new(false, ex.Message.Split('\n')[0]); }
    }
}
