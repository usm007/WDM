# AGENTS.md — WDM

WDM = IDM-inspired download manager, Windows-only, C#/WPF/.NET 8 (`net8.0-windows`).
Single buildable project: `src/WDM/WDM.csproj` (v2.7.2). Extension (`src/WDM.BrowserExtension/`)
+ Inno installer (`src/WDM.Setup/installer.iss`) are packaging, not .NET projects.

## Layout
- `src/WDM/Program.cs` → `App.xaml.cs` → `MainWindow` + `ViewModels/MainViewModel.cs` (entry chain)
- `src/WDM/Services/`: `DownloadEngine.cs` (chunked HTTP), `HlsDownloader.cs`, `MediaResolver.cs`+
  `YtDlpRunner.cs`+`EngineManager.cs` (yt-dlp/ffmpeg), `CaptureServer.cs` (`:17530`), `TaskStore.cs`
  (persistence), `VelopackUpdateService.cs`+`UpdateChecker.cs` (updates), `Embed/`, `TitleSync/`
- `src/WDM/Models/DownloadTask.cs`, `ViewModels/` (converters + `RelayCommand`), dialogs `*.xaml`
- `.project/` = agent knowledge base (read `overview.md` first); full map in `.project/modules.md`

## Commands (Windows, PS 5.1-compatible)
- Build: `dotnet build WDM.sln` · Run: `dotnet run --project src/WDM` (`RunWDM.bat` helper)
- Velopack: `powershell -File build-velopack.ps1` · XPI: `powershell -File build-xpi.ps1`
- Installer: publish to `staging\`, then `ISCC.exe src\WDM.Setup\installer.iss`
- Knowledge: `/project-knowledge-status` (staleness), `/project-knowledge-refresh` (incremental update)
- Tests: none exist (no test project/harness); verify via build + manual E2E (see `.project/test-map.md`)

## Conventions
- `Nullable=enable`, `ImplicitUsings=disable` (explicit usings; check `GlobalUsings.cs` first)
- Async suffix + `CancellationToken`; static service classes; `AtomicFile.Write` for state (never raw write)
- Network filenames via `SanitizeFileName`/`FileNameHelper`; embed parsers stay generic (no per-host branches)
- Commits: `fix:`/`chore:`/`release:` style; scripts stay PS 5.1-compatible

## Danger / do not touch
- `CaptureServer` CORS/SSRF allow-lists, UA/cookie forwarding (Cloudflare-sensitive, see
  `.project/decisions.md` §9); silent update arg must stay `--silent` alone (Velopack parsing)
- Never wipe `%LocalAppData%\WDM-Data` (`tasks.json`/`settings.json`/cookies/engines)
- Generated, never edit/commit: `bin/`, `obj/`, `publish*/`, `releases*/`, `release_upload/`,
  `staging/`, `output/`, `*.wdmstate`, user-data JSON (per `.gitignore`)

## Config requirements
.NET 8 SDK + Windows; WebView2 Runtime; loopback `:17530` free; engines (`yt-dlp`/`ffmpeg`)
auto-provision to `%LocalAppData%\WDM-Data\bin`. Version bumps touch 3 places: `WDM.csproj` +
`installer.iss` + extension `manifest.json`.
