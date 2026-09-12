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
        try { VelopackApp.Build().SetAutoApplyOnStartup(true).Run(); }
        catch (Exception ex)
        {
            // Log to debug output — silent in release, visible under debugger
            System.Diagnostics.Debug.WriteLine($"[Velopack] Bootstrap failed: {ex}");
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
