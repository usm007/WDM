# WDM — Project Overview (compact, read first)

> Token budget: this file is the entry point. Load detail files only for the subsystem you touch.

**FACT:** WDM (Windows Download Manager) is an IDM-inspired download manager for Windows only.
C# / WPF / .NET 8 (`net8.0-windows`), solution `WDM.sln` with one buildable project
`src/WDM/WDM.csproj` (v2.7.2). `src/WDM.BrowserExtension/` (Manifest V3, Chrome+Firefox variant
under `firefox/`) and `src/WDM.Setup/installer.iss` (Inno Setup 6) are content/packaging, not
separate .NET projects.

**FACT:** Entry point: `src/WDM/Program.cs` (`WDM.Program.Main`, STAThread) →
`VelopackApp.Build().SetAutoApplyOnStartup(true).Run()` → `App.xaml.cs` (single-instance Mutex,
DPI awareness, CLI args) → `MainWindow` (WPF-UI `FluentWindow`) + `ViewModels/MainViewModel.cs`.
MVVM: `Models/DownloadTask.cs` + `ViewModels/MainViewModel.cs` + XAML dialogs in `src/WDM/*.xaml`.

**FACT:** Core subsystems and where they live:
- Download engine → `src/WDM/Services/DownloadEngine.cs` (chunked HTTP, pause/resume, retry)
- HLS/DASH/embed → `HlsDownloader.cs`, `MediaResolver.cs` + `YtDlpRunner.cs` + `EngineManager.cs`,
  `Services/Embed/`, `Services/TitleSync/TitleFetcher.cs`
- Browser capture → `Services/CaptureServer.cs` (TCP loopback `127.0.0.1:17530`) + `src/WDM.BrowserExtension/`
- Persistence → `Services/TaskStore.cs` (`%LocalAppData%\WDM-Data\tasks.json`, `settings.json`)
- Updates → `Services/VelopackUpdateService.cs` (delta, preferred) + `Services/UpdateChecker.cs` (Inno fallback)
- Shell/UI services → `ThemeService.cs`, `TrayIcon.cs`, `SpeedGovernor.cs`, `BrowserIntegration.cs`

**FACT:** External binaries (not in git): `yt-dlp.exe`, `ffmpeg.exe`/`ffprobe.exe`, optional `qjs.exe`,
resolved by `EngineManager` from `%LocalAppData%\WDM-Data\bin` → seed `engines\` next to exe → `PATH`,
auto-downloaded on first use. Requires WebView2 Runtime, .NET 8 SDK + Windows to build.

**FACT:** Build/run: `dotnet build WDM.sln` · `dotnet run --project src/WDM` · `RunWDM.bat` (dev helper).
Installer: publish to `staging\` then `ISCC.exe src\WDM.Setup\installer.iss`.
Velopack: `powershell -File build-velopack.ps1`. Firefox XPI: `powershell -File build-xpi.ps1`.

**INFERENCE:** No test project exists in the repo (no `*Test*.csproj`, no test dirs found) —
verification is manual build + run.

**UNCERTAINTY:** `.github/workflows/` exists but is empty; CI status unknown.
`docs/` is local-only (gitignored, deployed to Vercel) — do not rely on it being present in clones.

Next step: identify your subsystem in `architecture.md` / `modules.md`, then read only that detail file.
