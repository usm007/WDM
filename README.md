# WDM — Windows Download Manager

<img width="1774" height="887" alt="banner" src="https://github.com/user-attachments/assets/07e7e14d-ab92-462c-af6f-7784a038bef6" />


An IDM-inspired no-nonsense download manager for Windows, written in C# / WPF / .NET 8.

- **Multi-threaded downloads** — dynamic segmentation with a shared chunk pool: the file is divided into small byte-range chunks that any worker thread can pick up as they finish, so slower segments don't idle threads.
- **Pause / Resume** — the set of completed chunks is persisted to a `*.wdmstate` file next to the target; resuming skips chunks already on disk and continues from there, even across restarts.
- **Automatic retry** — transient failures and HTTP 408/429/5xx responses are retried with exponential backoff.
- **Browser download catching** — a small Chrome/Edge/Firefox extension (Manifest V3) intercepts downloads and hands them to WDM over a localhost server (`http://127.0.0.1:17530`).
- **YouTube & media sites** — powered by [yt-dlp](https://github.com/yt-dlp/yt-dlp): resolve videos, audio and playlists, pick a quality tier (up to 4K, or audio-only MP3/M4A), and authenticate with cookies from your browser or a built-in WebView2 YouTube sign-in.
- **HLS streaming downloads** — `.m3u8` manifests are downloaded as a single media file: master/media playlists, AES-128 segment decryption, TS and fMP4 segments, 8 concurrent segments.
- **Speed limiting** — per-download and global throughput limits.
- **Priorities & categories** — Low/Normal/High priority that reorders the queue, and automatic categorization by file extension with optional per-category save folders.
- **Post-download actions** — optional SHA-256 checksum computation and a script/command to run on completion.
- **Light & dark themes**, two visual styles, tray icon with progress flyout, and automatic update checks against GitHub Releases.

## Structure

```
WDM/
  WDM.sln
  build-xpi.ps1             # packages the Firefox extension as wdm-catcher.xpi
  src/
    WDM/                    # WPF app (.NET 8)
      Models/               # DownloadTask, TaskStatus, PriorityLevel, DownloadCategory
      Services/             # DownloadEngine (chunked downloads), HlsDownloader,
                            # MediaResolver + YtDlpRunner + EngineManager (yt-dlp/ffmpeg),
                            # CaptureServer (localhost listener), TaskStore (JSON persistence),
                            # SpeedGovernor, ThemeService, TrayIcon, UpdateChecker
      ViewModels/           # MainViewModel, commands, converters
      Controls/             # custom window chrome
      Themes/               # dark/light palettes + two UI themes
    WDM.BrowserExtension/   # Manifest V3 extension for Chrome/Edge
      firefox/              # Firefox-specific variant (background event page)
    WDM.Setup/
      installer.iss         # Inno Setup installer script
```

## Build

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and Windows.

```
dotnet build WDM.sln
dotnet run --project src/WDM
```

The app listens on `http://127.0.0.1:17530` for browser download requests.

### Media engines

YouTube/HLS features rely on `yt-dlp.exe`, `ffmpeg.exe`/`ffprobe.exe` and optionally `qjs.exe`
(QuickJS). WDM looks for them next to the app (`engines\` seed folder), in `%LocalAppData%\WDM\bin`,
and on `PATH`; anything missing is downloaded automatically on first use.

## Install the browser extension

WDM has no effect on the browser until the extension is installed.

- **Chrome / Edge**: go to `chrome://extensions` (Edge: `edge://extensions`), enable *Developer mode*, click *Load unpacked*, and select `src/WDM.BrowserExtension`. WDM also ships a copy of the extension in its output folder (`BrowserExtension\`) and shows a step-by-step guide from within the app.
- **Firefox**: go to `about:debugging#/runtime/this-firefox`, click *Load Temporary Add-on*, and select `src/WDM.BrowserExtension/firefox/manifest.json`.

With WDM running, any download the browser starts is cancelled by the extension and sent to WDM instead. If WDM is not running, the download falls back to the browser's own downloader.

### Building the Firefox XPI

```
powershell -File build-xpi.ps1 [-SignedXpi <path-to-signed.xpi>]
```

Produces `staging/BrowserExtension/wdm-catcher.xpi`. The self-built XPI is **unsigned** and will not install in release builds of Firefox — upload it to [addons.mozilla.org](https://addons.mozilla.org/developers/) (self-distribution is free) and pass the signed file back via `-SignedXpi` to bundle it.

## Installer (optional)

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php). Publish the app into `staging\`, then compile:

```
ISCC.exe src\WDM.Setup\installer.iss
```

Output: `output\WDM_Setup_<version>.exe`. Per-user install into `%LocalAppData%\WDM`, with optional desktop icon and start-with-windows (launches minimized to the tray via `/minimized`) tasks.

## How the download engine works

1. `DownloadEngine.ProbeAsync` sends a `HEAD` request, falling back to a ranged `GET bytes=0-0`, to learn the file size and whether the server supports ranges.
2. If ranges are supported, the file is divided into chunks (`totalBytes / (chunks × 8)`, clamped to 128 KiB–16 MiB). A shared counter hands out chunk indexes to N worker threads, so any thread picks up the next incomplete chunk the moment it finishes one.
3. Each chunk is fetched with `Range: bytes=from-to` and streamed into the preallocated output file at its byte offset; the completion bitmap is flushed to `*.wdmstate` (JSON) so partial progress survives crashes.
4. On resume, `ChunkState.Load` validates the state file's magic, total bytes, chunk size and chunk count, then workers skip already-completed chunks.
5. `RunSingleStreamAsync` is the fallback when the server doesn't support ranges or the size is unknown; it also retries on failure. `.m3u8` URLs are routed to `HlsDownloader` instead.
6. A timer samples bytes/sec to drive the speed and ETA shown in the UI.

## Updates

The app checks `github.com/usm007/WDM/releases` on startup (and via *Check now* in Settings) and offers to open the latest installer when a newer version exists.

## Caveats

- Resume needs the server to support byte ranges; otherwise a resumed download restarts the file.
- Only Windows is supported (by design).

## Screenshots

<img width="929" height="579" alt="Screenshot_1" src="https://github.com/user-attachments/assets/481539b2-c661-475a-a746-dd0702d4eab3" />

<img width="698" height="513" alt="Screenshot_3" src="https://github.com/user-attachments/assets/c9eaaebc-6537-4c9b-b6a7-16eb99fe005c" />

<img width="605" height="404" alt="Screenshot_4" src="https://github.com/user-attachments/assets/19ecf8ce-f65e-4e6b-b510-21779d81fea7" />

<img width="631" height="475" alt="Screenshot_5" src="https://github.com/user-attachments/assets/e0e22a7f-eded-4a78-b932-d6142cd36c8d" />
