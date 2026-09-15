# Architecture

## Style
**FACT:** Single-process WPF desktop app, MVVM-ish: `MainWindow.xaml(.cs)` (WPF-UI `FluentWindow`)
hosts `MainViewModel` (`INotifyPropertyChanged`, `RelayCommand`s, `ICollectionView` filtering).
`DownloadEngine` is the long-lived service owned by `MainViewModel` (`Engine` property); dialogs
(`Add/Progress/Complete/Options/...` in `src/WDM/*.xaml`) raise events the `MainWindow` wires to
engine/viewmodel calls. No DI container, no backend, no database — JSON files via `TaskStore`.

## Process / startup chain
**FACT:** `Program.Main` (`src/WDM/Program.cs`) → `App.xaml.cs` (`Mutex Local\WDM.SingleInstance…`,
`SetProcessDpiAwarenessContext`, crash log to `TaskStore.AppDir/wdm_error.log`,
`--capture-screenshots` headless path, `/minimized|/tray|…` args, `BrowserIntegration.DeployExtension()`,
`TaskStore.EnsureMigrated()`, `ThemeService.Apply`) → `WelcomeWindow` on first run else `MainWindow`.
`CaptureServer` (loopback TCP `:17530`) and `TrayIcon` (WinForms `NotifyIcon`) start with `MainWindow`.

## Key flows (detail in `data-flow.md`)
- HTTP download: `ProbeAsync` (HEAD → ranged GET) → `AutoChunkCount` → shared-counter chunk pool →
  ranged workers → preallocated file + `*.wdmstate` bitmap → timer-driven speed/ETA.
- Media: `.m3u8` → `HlsDownloader` (8 concurrent segments, AES-128, ffmpeg `.ts→.mp4` remux);
  DASH/YouTube → `yt-dlp`/`ffmpeg` via `YtDlpRunner`/`EngineManager`; embeds → `EmbedResolver`+`EmbedParsers`.
- Capture: extension `POST /download` / `GET /resolve?url=` on `:17530` → `AddDownloadDialog` / `RefreshLinkDialog`.
- Persistence: `tasks.json` + `settings.json` in `%LocalAppData%\WDM-Data` via `AtomicFile` (temp+rename).
- Updates: Velopack delta (`*.nupkg`+`RELEASES` on GitHub, `MaximumDeltasBeforeFallback=1`) →
  fallback `UpdateChecker` full `WDM_Setup_*.exe` to `%TEMP%`.

## Constraints
**FACT:** Windows-only by design (`UseWPF+UseWindowsForms`, `app.manifest` asInvoker/longPathAware,
`PerMonitorV2` DPI). Per-user install (`{localappdata}\WDM`); user data (`WDM-Data`) never wiped by installer.
**FACT:** Resume requires server byte-range support, else restart. Silent update launches must use
`--silent` alone (Velopack/clap parsing; `/VERYSILENT` breaks it — see `UpdateChecker.LaunchInstaller`).
**INFERENCE:** `CaptureServer` CORS/SSRF allow-lists (extension schemes only, loopback/private-IP blocks)
are load-bearing security boundaries — do not loosen without review.
