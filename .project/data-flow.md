# Data Flow

## 1. HTTP download (`DownloadEngine.RunSessionAsync`)
**FACT (`src/WDM/Services/DownloadEngine.cs`):** `ProbeAsync` = `HEAD`, fallback ranged
`GET bytes=0-0` → size + range support + filename hints (`Content-Disposition`/MIME via `FileNameHelper`).
Routable: `X-WDM-StreamType` header (`HLS/DASH/Stream/page`), `X-WDM-PageTitle`, `LooksLikeHlsUrl`
(`.m3u8/hls/playlist/manifest/master/stream`), `LooksLikeDashUrl` (`.mpd/dash/manifest`),
64KB sniff for `#EXTM3U` → `HlsDownloader`; DASH → ffmpeg; YouTube flag → `RunYouTubeSessionAsync`
(yt-dlp). Else chunked (`AutoChunkCount`: <1MB→1, <5→2, <25→4, <100→8, <500→16, else 32;
`chunkSize=total/(count*8)` clamped 128KiB–16MiB, ≤100k chunks) with shared-counter pool over N
workers (`Range:` + preallocated file at offsets) or `RunSingleStreamAsync` fallback.
`ChunkState` (`WDMSTATE1` JSON `{Magic,TotalBytes,ChunkSize,ChunkCount,Bits(base64)}`) at
`{FullPath}.wdmstate` via `AtomicFile`, dirty-flush ~1s. Retry: `SendWithRetry` on 408/429/5xx,
backoff `min(8000,500*2^attempt)`; 250ms timer drives B/s + ETA. Events: `TaskChanged/TaskCompleted/
ChunkProgressUpdated/CloudflareBlocked/EmbedInteractionRequired`.

## 2. Media resolve → download
**FACT:** `MediaResolver.ResolveAsync` → `YtDlpRunner.RunJsonAsync`
(`--dump-single-json --flat-playlist --socket-timeout 20`, 20MB cap) → playlist `entries[]`
(+ `hqdefault.jpg` thumbs) or single `formats[]→BuildQualityOptions` → tier `FormatArg`
(`bestvideo[height<=N]+bestaudio/best…`, Audio=-1). `EngineManager` locates `yt-dlp/ffmpeg/ffprobe/qjs`
(`AppDir\bin` → `engines\` seed → `PATH`, MZ+100KB check) or downloads (HTTPS allow-list;
caps 100/300/50MB). Embeds: `EmbedResolver.TryResolveAsync` (strategies scored 40–90, HEAD/Range-verify,
emits Referer/Origin/Cookie) or throws `EmbedInteractionRequiredException`. `TitleFetcher`
best-effort renames opaque files (oEmbed→og→JSON-LD, 8s).

## 3. Browser capture (`:17530`)
**FACT (`CaptureServer.cs`, extension `background.js`/`media_sniffer.js`):** Extension cancels the
browser download, merges cookies (URL domain + referrer origin) + `navigator.userAgent`, and
`POST /download {Url,FileName,Referer,PageTitle,Headers,StreamType,VideoUrl,AudioUrl,PageUrl}`
(body ≤10MB, url ≤2048 http(s)-only, filename sanitized, header allow-list 20×8KB, no CRLF) →
server injects `X-WDM-StreamType/VideoUrl/AudioUrl/PageTitle` + `Origin` → `AddDownloadDialog`
(fromCapture) or `RefreshLinkDialog.OnLinkCaptured`. `GET /resolve?url=` (30s CTS; embed-first unless
YouTube; fallback 1080/720/480/360/audio) → `ResolveResponse`. `GET /ping` health.
CORS: only `chrome-extension://`/`moz-extension://`; SSRF: blocks loopback/private/metadata/≥224/IPv6-special.

## 4. Persistence / settings
**FACT (`TaskStore.cs`):** `%LocalAppData%\WDM-Data\`: `settings.json` (concurrent 1–16, retries 0–20,
chunks 0–32, limits, `CategoryFolders` under `%USERPROFILE%\Downloads`), `tasks.json` (+`.bak`),
`youtube_cookies.txt`, `bin\`, WebView2 profile. Load: fast parse → backup → per-record salvage
(`TasksLoadFailed` blocks save). Save maps `DownloadTask→TaskRecord` via `AtomicFile`.

## 5. Updates
**FACT:** Velopack first (`RepoUrl=github.com/usm007/WDM`, `GithubSource`, `MaximumDeltasBeforeFallback=1`,
flavor guard `<20MB full over self-contained = refuse`), `DownloadUpdatesAsync` (0–100) →
`ApplyAndRestart/ApplyAndExit/WaitExitThenApply`. Else `UpdateChecker` GitHub-latest fallback:
find `WDM_Setup_*.exe`, download to `%TEMP%` (500MB cap, MZ+PE+SHA256 log), `LaunchInstaller(--silent)`.
