# WDM — Windows Download Manager

<p align="center">
  <img width="1774" height="887" alt="ChatGPT Image Aug 18, 2026, 07_42_38 PM" src="https://github.com/user-attachments/assets/07643c60-d987-4859-bc99-a0bcbe099557" />

</p>

An IDM-inspired no nonsense download manager for Windows, written in C# / WPF / .NET 8.

- **Multi-threaded downloads** — dynamic segmentation with a shared chunk pool: the file is divided into small byte-range chunks that any worker thread can pick up as they finish, so slower segments don't idle threads.
- **Pause / Resume** — the set of completed chunks is persisted to a `*.wdmstate` file next to the target; resuming skips chunks already on disk and continues from there, even across restarts.
- **Automatic retry** — transient failures and HTTP 408/429/5xx responses are retried with exponential backoff (`MaxRetries`).
- **Browser download catching** — a small Chrome/Edge/Firefox extension (Manifest V3) intercepts downloads and hands them to WDM over a localhost server.
- **Speed limiting** — per-download and global throughput limits.
- **Priorities & categories** — Low/Normal/High priority that reorders the queue, and automatic categorization by file extension with optional per-category save folders.
- **Post-download actions** — optional SHA-256 checksum computation and a script/command to run on completion.

## Structure

```
WDM/
  WDM/                    # WPF app (.NET 8)
    Models/               # DownloadTask, TaskStatus, PriorityLevel, DownloadCategory
    Services/             # DownloadEngine (chunked downloads), CaptureServer (localhost listener),
                          # TaskStore (JSON persistence), SpeedGovernor, TrayIcon, HlsDownloader
    ViewModels/           # MainViewModel, commands, converters
    Themes/Theme.xaml     # dark theme
  WDM.BrowserExtension/   # Manifest V3 extension for Chrome/Edge/Firefox
```

## Build

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet build WDM.sln
dotnet run --project WDM
```

The app listens on `http://127.0.0.1:17530` for browser download requests.

## Install the browser extension

WDM has no effect on the browser until the extension is installed. Load it as an unpacked extension:

- **Chrome / Edge**: go to `chrome://extensions` (Edge: `edge://extensions`), enable *Developer mode*, click *Load unpacked*, and select the `WDM.BrowserExtension` folder.
- **Firefox**: go to `about:debugging#/runtime/this-firefox`, click *Load Temporary Add-on*, and select `WDM.BrowserExtension/manifest.json`.

With WDM running, any download the browser starts is cancelled by the extension and sent to WDM instead. If WDM is not running, the download falls back to the browser's own downloader.

## How the download engine works

1. `DownloadEngine.ProbeAsync` sends a `HEAD` request, falling back to a ranged `GET bytes=0-0`, to learn the file size and whether the server supports ranges.
2. If ranges are supported, the file is divided into chunks (`totalBytes / (chunks × 8)`, clamped to 128 KiB–16 MiB). A shared counter hands out chunk indexes to N worker threads, so any thread picks up the next incomplete chunk the moment it finishes one.
3. Each chunk is fetched with `Range: bytes=from-to` and streamed into the preallocated output file at its byte offset; the completion bitmap is flushed to `*.wdmstate` (JSON) so partial progress survives crashes.
4. On resume, `ChunkState.Load` validates the state file's magic, total bytes, chunk size and chunk count, then workers skip already-completed chunks.
5. `RunSingleStreamAsync` is the fallback when the server doesn't support ranges or the size is unknown; it also retries on failure.
6. A 500ms timer samples bytes/sec to drive the speed and ETA shown in the UI.

## Caveats

- Resume needs the server to support byte ranges; otherwise a resumed download restarts the file.
- Only Windows is supported (by design).
