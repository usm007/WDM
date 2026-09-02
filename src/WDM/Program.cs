using Velopack;

namespace WDM;

/// <summary>
/// Custom entry point so Velopack can handle install/update hooks before WPF bootstraps.
/// This is the recommended location for VelopackApp.Build().Run() per Velopack docs.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Handle Velopack lifecycle hooks (install, update, uninstall). No-op on dev/Inno builds.
        try { VelopackApp.Build().SetAutoApplyOnStartup(true).Run(); } catch { /* ignore velopack bootstrap failures in dev */ }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
