# Dependencies

**FACT (`src/WDM/WDM.csproj`):** `net8.0-windows`, `UseWPF+UseWindowsForms`, `Nullable=enable`,
`ImplicitUsings=disable`, `PerMonitorV2`, `StartupObject=WDM.Program`, `Version=2.7.2`. Only 3 NuGet:
- `Microsoft.Web.WebView2 1.0.4129.50` — YouTube sign-in (`YouTubeSignInWindow`), cookie export
  (`YouTubeCookieExporter`), `CloudflareChallengeWindow`. Needs WebView2 Runtime on machine.
- `Velopack 0.0.1297` — delta updates + install hooks (`VelopackApp.Build…Run()` in `Program.cs`).
- `WPF-UI 4.3.0` — `FluentWindow`, `SymbolRegular`, `ThemesDictionary`/`ControlsDictionary`.

**FACT (external binaries, not NuGet):** `yt-dlp.exe` (resolve + YouTube/quirk-site downloads),
`ffmpeg.exe`/`ffprobe.exe` (HLS `.ts→.mp4` remux `-c copy +faststart`, DASH capture), optional
`qjs.exe` (QuickJS `--js-runtimes` for yt-dlp). Sources: `AppDir\bin` → `engines\` seed → `PATH` →
auto-download (GitHub `yt-dlp`, `quickjs-ng`, `gyan.dev`/BtbN ffmpeg). No runtime install needed for
self-contained publishes (bundles .NET 8).

**FACT (extension):** No npm deps. Chrome MV3 permissions:
`downloads,storage,cookies,tabs,webRequest,webNavigation,declarativeNetRequest,scripting,notifications`,
hosts `<all_urls>` + `127.0.0.1:17530`/`localhost:17530`. Firefox variant drops
`declarativeNetRequest/scripting`, uses `background.scripts`, adds `gecko{id: wdm-catcher@wdm.app,
strict_min_version: 140.0}`.

**FACT (tooling):** .NET 8 SDK + Windows; Inno Setup 6 (`ISCC.exe`) for installer; `vpk` CLI
(`dotnet tool install -g vpk`) for Velopack; PowerShell 5.1-compatible scripts.

**INFERENCE:** Dependabot/renovate absent; version bumps are manual (`WDM.csproj` + `installer.iss`
`MyAppVersion` + extension `manifest.json` — recently bumped together in `d757dad`).
When bumping, check all three.
