# Stream catch — playmate.to + vidwara.cc in WDM

## 1. Goal
Make WDM reliably download media streams behind:
- `https://playmate.to/embed/ntZc9WC4cv7Mb`
- `https://vidwara.cc/e/dUsEPNH5u0DiJ`

Primary path: browser capture. Fallback: pasted embed URL via native resolver.

## 2. What we verified

### Embeds hide the stream, they are not direct files
- `playmate.to/embed/...` → JWPlayer shell + `/assets/js/player-core.min.js`
  (obfuscated, XOR + `pako` + `crypto-js`), `window.__PM videoId:246275`,
  `/api/vpn` gate, `googima.js` adblock check. Stream URL is resolved at runtime in-page.
- `vidwara.cc/e/...` → JWPlayer, `POST /api/stream {filecode,device}` →
  `{streaming_url,subtitles,title}` → `player.setup({sources:[{file:streaming_url}]})`.
  Direct server-side repro returns `400 Missing or invalid filecode` — needs
  browser Referer/Origin/cookies/query context.

### WDM today
- Paste embed URL → miss. `src/WDM/Services/MediaResolver.cs:45` is YouTube-only,
  `src/WDM/AddDownloadDialog.xaml.cs:168` gates yt-dlp on that,
  `src/WDM/Services/DownloadEngine.cs:361 RunSessionAsync` → `ProbeAsync` saves HTML as garbage.
- Browser capture → maybe. Extension is `all_frames:true`
  (`src/WDM.BrowserExtension/manifest.json:55`), `webRequest` + isolated `fetch/XHR`
  (`src/WDM.BrowserExtension/media_sniffer.js:538`) + MAIN-world hook
  (`src/WDM.BrowserExtension/wdm_hook.js:36`) →
  `POST 127.0.0.1:17530/download` with `{url,referer,headers:{Referer,Origin,Cookie,UA}}`.
  `src/WDM/Services/HlsDownloader.cs:332` is extension-agnostic (handles `.js`-segment case),
  `AES-128:507`, `EXT-X-MAP:320`, `BYTERANGE:342`.
- Drops: `src/WDM/Services/CaptureServer.cs:166` drops `StreamType/VideoUrl/AudioUrl`;
  no auto-`Origin`; `src/WDM/Services/DownloadEngine.cs:664,736 LooksLikeHlsUrl`
  needs literal `.m3u8` in path/query; `HlsDownloader:201 depth<3`, best-variant only,
  AES-128 only, no `#EXTM3U` content-sniff, output stays `.ts` (`RunHlsAsync:954`).
- Extension misses: `POST /api/stream` JSON bodies (`streaming_url` inside response,
  not URL), tokenized manifests without `.m3u8` literal, `blob:`/MSE, Widevine.
  `src/WDM.BrowserExtension/background.js:127 IDM_HLS_RE` is narrower than
  `src/WDM.BrowserExtension/media_sniffer.js:13`.

## 3. Build plan

### Phase A — Verify protocol (no code, 10 min)
1. Run WDM + load unpacked `src/WDM.BrowserExtension`, open each embed, Play,
   check for overlay `Download this video` / Add dialog `HLS stream` badge
   (`AddDownloadDialog.cs:461`).
2. Record: captured URL + headers vs no-hint vs 403 vs encrypted-garbage.
   This decides if Phase B alone suffices.

### Phase B — Extension capture fix
- `background.js:127`: widen `IDM_HLS_RE` to
  `(\.m3u8|\/hls\/|\/playlist|\/manifest|\/master\.|\/stream\b|[\?&](format|ext)=m3u8|mime=.*mpegurl)`,
  same for DASH; forward `Origin` (derive from referrer if missing) in `sendToWdm:355`.
- `media_sniffer.js:538 hookNetworkRequests` + `wdm_hook.js:36`: add response-body hook —
  clone `fetch` responses for `/api/stream|/api/|player`, parse JSON/text for
  `streaming_url|source|file` containing `.m3u8/.mpd/.mp4|/playlist|/manifest|/hls`,
  call `registerMediaStream()` on hits. Keep existing URL-pattern hooks.
- `media_sniffer.js:407 registerMediaStream`: accept `Stream` type as downloadable
  (already does via `STREAM_URL_RE:15`), don't filter `/api/stream` itself.

### Phase C — Desktop handoff fix
- `CaptureServer.cs:157,302`: preserve `StreamType`; if `HLS/DASH/Stream`, inject
  `headers["X-WDM-StreamType"]`; if `Origin` absent but `Referer` present, add
  `Origin: scheme://host`.
- `HlsDownloader.cs:153 ApplyHeaders`: allow `Origin` through (already does — verify),
  derive `Origin` from `referer` if still missing.
- `DownloadEngine.cs:664,736,752`: force `isHls=true` when `X-WDM-StreamType:HLS/Stream`;
  widen `LooksLikeHlsUrl` to `/hls/|/playlist|/manifest|/master|/stream` +
  content-sniff fallback: if probe body starts with `#EXTM3U`, treat as HLS.

### Phase D — Native embed resolver (pasted-URL fallback)
- New `Services/Extractors/EmbedExtractors.cs`:
  `IsEmbedUrl()` for `playmate.to/embed/|vidwara.cc/e/`;
  `TryResolveAsync(url,referer,headers)`:
  - Vidwara: `GET` embed HTML (with Referer/UA) → extract `filecode` from path →
    `POST {origin}/api/stream {filecode,device:web}` with `Referer+Origin+Cookie` →
    parse `streaming_url`.
  - Playmate: `GET` embed HTML → regex
    `streaming_url|file\s*:\s*"https?[^"]+\.m3u8[^"]*"|source[^}]*\.m3u8` +
    `__PM videoId` API probes; return null if obfuscation defeats static parse
    (capture remains primary).
  - Generic fallback: scan HTML/JS for first `.m3u8/.mpd` URL, resolve relative.
- Wire in `DownloadEngine.cs:361 RunSessionAsync` before `ProbeAsync`:
  `if IsEmbedUrl → TryResolve → rewrite task.Url (+Referer/Headers) → proceed as HLS`.
  Wire badge in `AddDownloadDialog.xaml.cs:168` to show `Embed stream` when resolved.
- Explicit non-goals: no Widevine/SAMPLE-AES, no live/DVR polling,
  no HLS quality picker (still best-variant), no TS→MP4 remux in this pass.

### Phase E — QA
- Matrix: both embeds via capture; both via pasted URL; regression: plain `.m3u8`,
  AES-128, `.js` segments (`seaguli1` link), normal file.
- `dotnet build WDM.sln`, manual capture test, check output plays end-to-end,
  size ~ `bitrate × duration`.

## 4. Tradeoffs to confirm
- TS-concat stays `.ts` — accept, or add optional `ffmpeg -c copy` remux to `.mp4` in same pass?
- If Playmate static parse fails, accept capture-only for it and do full native only for Vidwara?

---

# Update-icon plan (icon-only, animated ArrowDownload24)

Goal: top-bar About `Info24` becomes animated `ArrowDownload24` when update available. No startup auto-popup. Click opens `AboutDialog.ShowAvailableUpdate()`.

## 1. ViewModel `src/WDM/ViewModels/MainViewModel.cs`
Add near `ThemeSymbol (:285)`, same `INotifyPropertyChanged` pattern:
- `bool IsUpdateAvailable` + `ReleaseInfo? PendingRelease` + `Velopack.UpdateInfo? PendingVelopack`
- `AboutButtonSymbol => IsUpdateAvailable ? ArrowDownload24 : Info24`
- `AboutButtonToolTip => IsUpdateAvailable ? "Update available - click to install" : "About Windows Download Manager"`
- Sidebar `FilterKind.About` untouched.

## 2. XAML `src/WDM/MainWindow.xaml:124-126`
Bind `Symbol="{Binding AboutButtonSymbol}"`, `ToolTip="{Binding AboutButtonToolTip}"`. Add `TranslateTransform Y` bounce via `DataTrigger IsUpdateAvailable=True` + `BeginStoryboard RepeatBehavior=Forever AutoReverse=True`, `0 to -3`, `0:0:0.55`, `SineEase`. `ExitActions StopStoryboard`. Transform-only, 1-line nav preserved.

## 3. Check logic `src/WDM/MainWindow.xaml.cs:467-541`
Velopack hit `:484-503` + GitHub hit `:526-540`: delete `RestoreWindow()` + `ShowDialog()`, dispatcher-set `PendingRelease/PendingVelopack/IsUpdateAvailable=true`. Keep throttle `:470-474`, Velopack-delta-only `:505-508`, settings gate `:148-149`.

## 4. Click `ShowAbout() :637-641`
If `IsUpdateAvailable && PendingRelease!=null`: `new AboutDialog{Owner=this}; ShowAvailableUpdate(pending, velopack); ShowDialog()`. Else plain About. Flag persists until install/restart.

## 5. Verify
`dotnet build WDM.sln`, force update, confirm no autopopup, icon bounces, click opens update panel, plain About otherwise.

---

# Generalized embed → direct-stream plan (firestream.to / dsvplay.com / voe.sx / bysezoxexe.com)

Constraints: (a) both paste-URL and catch-while-playing must work, (b) generalized — no per-host configs, (c) user-interaction-required → notify + open in WebView.

## 1. Goal
Make these resolve to downloadable streams without hardcoding hosts:
- `https://firestream.to/e/y_SYIVZS` → `POST /api/videos/{id}/resolve {blob}` → `signedVideoUrl`
- `https://dsvplay.com/e/23ihauw6plkl` → Dood shape: `GET {origin}/pass_md5/...` (Referer) → `prefix + rand10 + ?token=&expiry=ms`
- `https://voe.sx/e/ftvm1cblr1rq` → `sources.hls` (b64) or obfuscated `<script type="application/json">` (`rot13 → strip → b64 → -3 → reverse → b64 → JSON{source}`) → `.m3u8`, follow JS mirrors
- `https://bysezoxexe.com/e/64acsgfejfvs` → `GET .../embed/details` → `GET .../embed/playback` (`Referer` + `X-Embed-Parent`) → AES-GCM decrypt (`key = b64url(p0)+b64url(p1)`, `iv`, `payload`) → `sources[].url`

## 2. Current gaps (verified)
- `Services/MediaResolver.cs:45-52`, `AddDownloadDialog.xaml.cs:168,572`, `DownloadEngine.cs:364`: yt-dlp path is YouTube-only. `/e/` pages fall into HTML-probe → saved as `download_*.bin`.
- `HlsDownloader.cs:201-228` needs an already-direct manifest; no HTML→media upgrade.
- `CaptureServer.cs:178-243` `/resolve` expects YouTube schema (`Could not identify...YouTube video`, `i.ytimg.com` thumbs).
- Extension (`background.js:127-129,234-257`, `media_sniffer.js:13-15`, `wdm_hook.js:20-26`) only matches direct `.m3u8/.mpd/.mp4`; static pre-play HTML (esp. Byse encrypted payload) yields nothing, post-play Network does.

## 3. Build: one generic pipeline
New `src/WDM/Services/Embed/` — origins always derived from the **response URL**, never hardcoded:
- `IEmbedStrategy.cs` — `Priority`, `TryResolveAsync(EmbedContext, ct)` → `StreamCandidate{Url, Headers{Referer,Origin,UA,Cookie}, Kind, Score}`.
- `EmbedResolver.cs` — normalize → follow redirects (HTTP + `window.location`/iframe/`/e/` rewrites, max 4) → fetch HTML → run strategies → verify (HEAD/range: `video/*`, `mpegurl`, mp4 magic, reject HTML) → rank (master `.m3u8` > largest `.mp4` > `.mpd`) → best + alternates. Re-resolve hook on 403/expiry at download start.
- Strategies (patterns, not domains):
  1. `DirectMediaTags` — `<video/src>`, `<source>`, `og:video:url`, bare `.m3u8/.mpd/.mp4`.
  2. `SourcesJson` — `sources={hls,mp4}` + b64-decode attempts (Voe shape).
  3. `ObfuscatedJson` — string-array decode chain, accept on JSON-with-URL (Voe 2024+ shape).
  4. `TokenApi` — `*-blob/token` script + same-origin `POST /api/videos/{seg}/resolve {blob}` → `signedVideoUrl|url|src` (Firestream shape).
  5. `PassMd5` — `(/pass_md5/...)` + `token=` → `GET` with `Referer` → suffix `rand10 + ?token&expiry` (Dood/dsvplay shape).
  6. `EmbedApi` — `.../embed/details` → `embed_frame_url` → `GET playback` (`Referer` + `X-Embed-Parent`) → plain `sources[]` or AES-GCM (`key_parts/iv/payload`) decrypt (Byse shape).
  7. `JsRedirect` — pre-pass frame/JS redirect follower for random mirrors (`voe` mirrors, `f75s.com`, `dsvplay→playmogo` 301).

## 4. Wiring (both entry points)
- `MediaResolver.cs:54 ResolveAsync` — keep YouTube; add generic branch when probe = `text/html` + small + no disposition. Return real `Title` (`<title>`/`og:title`/details.title) + real poster (`og:image`), not YouTube schema.
- `AddDownloadDialog.xaml.cs:172,627` — HTML probe shows "Resolving embed…", rewrites `Url=direct`, `Referer=origin`, merges headers; stores `SourcePageUrl` for re-resolve.
- `DownloadEngine.cs:361,538` — if `SourcePageUrl` set and 403/empty/expired, re-run `EmbedResolver` once (signed-URL `expiry`/`expires_at` handling).
- `CaptureServer.cs:157,178` — accept `pageUrl` fallback; `/resolve` returns generic `{title, qualities:[{label,url,headers}]}`. Extension: if player page has no direct media after N sec, offer "Send page to WDM" (page URL + Referer/Origin/Cookie/UA/pageTitle).

## 5. Interaction fallback (WebView rule)
New `EmbedInteractionWindow` (WebView2, reuse sign-in window pattern): on challenge/captcha/`attest`/repeated-403 → notify "needs one-time check → opened page" → load `SourcePageUrl` → user solves → auto-retry resolver with WebView cookies or capture played manifest. No hidden solver.

## 6. HTTP rules
Chrome UA, `UseCookies=false` raw-`Cookie` passthrough (`DownloadEngine.cs:91` pattern), forward `Referer/Origin/UA/Cookie` end-to-end (extension already sends them; `HlsDownloader.cs:153 ApplyHeaders` + `DownloadEngine.cs:1304 BuildRequest` already forward — verify `Origin` included). Cloudflare/Turnstile detectors abort to WebView instead of blind retry.

## 7. QA
- Unit (fixtures, no live net): anonymized HTML/API samples per shape; each strategy + AES-GCM + b64/obf + origin-after-redirect + challenge detection; negatives (`blob:`-only, ad beacons, login walls).
- Live (the 4 URLs): paste→real title/size; download completes (HLS/chunked + Referer); play→media hint; 403 simulation → re-resolve → WebView prompt.
- Regression: plain `.m3u8`, AES-128, `.js` segments, normal file. `dotnet build WDM.sln`.

## 8. Risks
Pattern drift (obfuscation tweaks, key-split change, `attest` JWT enforcement, Voe geo variance, mirror rotation). Mitigation: origin-derived probing + verify + re-resolve + WebView fallback + per-strategy logging.
