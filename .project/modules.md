# Modules

| Area | Path(s) | Key symbols | Notes |
|---|---|---|---|
| App bootstrap | `src/WDM/Program.cs`, `App.xaml(.cs)`, `app.manifest`, `AssemblyInfo.cs`, `GlobalUsings.cs` | `WDM.Program.Main`, `App.StartMinimized` | Velopack hook first; single-instance Mutex; `StartupObject=WDM.Program` |
| Domain model | `src/WDM/Models/DownloadTask.cs` | `DownloadTask`, `TaskStatus{Queued,Downloading,Paused,Completed,Failed}`, `PriorityLevel`, `DownloadCategory`, `Categorize()` | `INotifyPropertyChanged`, ctor takes `Dispatcher`; extension→category sets |
| Main VM | `src/WDM/ViewModels/MainViewModel.cs` (+9 converters, `RelayCommand.cs`) | `MainViewModel`, `FilterKind`, commands (`OpenAddDialog,Pause,Resume,Stop,Remove,Retry,MoveUp/Down,SetPriority,…`) | 1537 lines; queue ops, persistence scheduling, bulk selection |
| Download engine | `src/WDM/Services/DownloadEngine.cs` | `DownloadEngine{Start,Resume,Pause,PauseAll,ResumeAll,Stop,Remove,SetPriority,MoveQueued}`, `ChunkState(Magic=WDMSTATE1)`, `ProbeMeta`, `AutoChunkCount` | 2672 lines; `MaxConcurrent=3`, `MaxRetries=3`; UA `Chrome/125`; chunk `128KiB–16MiB` |
| HLS | `src/WDM/Services/HlsDownloader.cs` | `HlsDownloader.DownloadAsync`, `MaxConcurrentSegments=8`, `MaxRetries=4` | Master depth 3, best-variant, AES-128-CBC/PKCS7, `.wdmseg_*` temp dirs |
| Media/yt-dlp | `Services/MediaResolver.cs`, `YtDlpRunner.cs`, `EngineManager.cs` | `MediaResolver.ResolveAsync/Tiers`, `YtDlpRunner.RunJsonAsync`, `EngineManager.EnsureAsync/IsReady` | Tier format args; `--js-runtimes quickjs`; binaries in `AppDir\bin` |
| Embed | `Services/Embed/{EmbedModels,EmbedResolver,EmbedParsers}.cs` | `EmbedResolver.TryResolveAsync/IsEmbedCandidate`, `EmbedParsers.*`, `EmbedInteractionRequiredException` | Shape-based regex, no per-host code; 4-hop, 30s budget |
| Title sync | `Services/TitleSync/TitleFetcher.cs` | `TitleFetcher.TryFetchTitleAsync/ShouldAttempt` | oEmbed → og:title → JSON-LD; best-effort 8s |
| Capture server | `src/WDM/Services/CaptureServer.cs` | `CaptureServer(Port=17530)`, `CapturePayload`, `ResolveResponse` | Loopback TCP; `GET /ping`, `POST /download`, `GET /resolve`; 10MB body cap |
| Persistence | `Services/TaskStore.cs`, `Services/AtomicFile.cs` | `TaskStore{LoadSettings/SaveSettings,LoadTasks/SaveTasks}`, `TaskRecord`, `AppDir=%LocalAppData%/WDM-Data` | Lenient-enum JSON; `.bak` + per-record salvage; `AtomicFile` temp+rename |
| Updates | `Services/VelopackUpdateService.cs`, `Services/UpdateChecker.cs`, `Services/InstallState.cs` | `CheckForUpdatesAsync/DownloadUpdatesAsync/ApplyAndRestart`, `ReleaseInfo`, `LaunchInstaller` | Velopack delta preferred; GitHub `usm007/WDM` fallback |
| Throttle | `Services/SpeedGovernor.cs` | `SpeedGovernor.ThrottleAsync` | Token-bucket; per-download + global limits |
| UI shell | `src/WDM/MainWindow.xaml(.cs)`, dialogs (`Add/Progress/Complete/Options/RefreshLink/…`), `Controls/WdmWindow.cs`, `Themes/{Theme,Palette.Light,Palette.Dark}.xaml` | `WdmWindow` (custom chrome `WindowStyle=None`) | WPF-UI `FluentWindow`; Segoe UI Variable; light/dark + 2 styles |
| Tray/icons | `Services/TrayIcon.cs`, `Services/AppIcon.cs`, `TrayProgressPanel.xaml(.cs)` | `TrayIcon:IDisposable` | WinForms `NotifyIcon`; progress flyout |
| Browser glue | `Services/BrowserIntegration.cs`, `Services/YouTubeCookieExporter.cs` | `BrowserIntegration.DeployExtension`, `YouTubeCookieExporter` (→ `youtube_cookies.txt`) | Chromium+Firefox detect; WebView2 cookie export |
| Extension | `src/WDM.BrowserExtension/` (`manifest.json` v1.1.4, `background.js`, `media_sniffer.js`, `youtube_menu.js`, `wdm_hook.js`, `popup.*`, `firefox/` variant) | `WDM_HOST=127.0.0.1:17530` | MV3; `media_sniffer` IDM-grade (HLS/DASH/segmented); Firefox lacks `declarativeNetRequest/scripting`, uses `background.scripts` |
| Installer | `src/WDM.Setup/installer.iss` | `MyAppVersion 2.7.2.0`, `{localappdata}\WDM` | Inno6; kills processes on install; preserves `WDM-Data`; `[Registry]` empty by design |
| Scripts | `build-velopack.ps1`, `build-xpi.ps1`, `RunWDM.bat` | — | Velopack pack (self-contained default); XPI zip (unsigned); dev build+run |
| Screenshots | `src/WDM/Services/ScreenshotGenerator.cs` | `ScreenshotGenerator.Run()` | `--capture-screenshots` headless automation |

**INFERENCE:** `stream_catch.md` (repo root, local-only) is the freshest embed/capture gap analysis;
treat as working notes, not spec. **UNCERTAINTY:** `WDM.BrowserExtension` has no `.csproj` —
confirm packaging is purely via csproj `Content` link (`BrowserExtension\` in output) before changing it.
