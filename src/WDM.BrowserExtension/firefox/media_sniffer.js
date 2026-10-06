// WDM Media Sniffer — IDM-Grade Media Stream Capture & Overlay
// Intercepts HLS (.m3u8), DASH (.mpd), segmented media, and direct streams.
// Injects an IDM-style floating "Download this video" button onto active video players.
// Communicates with background script to sync detected media and send tasks to WDM.

(function () {
  "use strict";

  // Strict domain guard: exclude YouTube (handled by youtube_menu.js + yt-dlp)
  if (location.hostname.includes("youtube.com") || location.hostname.includes("youtu.be")) return;

  // No floating button on major paid/subscription streaming services. Their
  // catalogs are DRM and/or ToS protected, so the button could only ever
  // fail there — and its presence looks out of place. UGC platforms
  // (Vimeo, Dailymotion, Twitch, social video) intentionally keep it.
  // Suffix-matched so regional and player subdomains are covered too.
  var NO_OVERLAY_HOSTS = [
    "netflix.com",
    "disneyplus.com",
    "hulu.com", "hulu.jp",
    "hbomax.com", "max.com",
    "primevideo.com",
    "paramountplus.com",
    "peacocktv.com",
    "tv.apple.com",
    "discoveryplus.com", "discoveryplus.in",
    "crunchyroll.com",
    "hotstar.com",
    "jiocinema.com",
    "zee5.com",
    "sonyliv.com",
    "mxplayer.in",
    "voot.com",
    "altbalaji.com",
    "sunnxt.com",
    "aha.video",
    "erosnow.com",
    "mubi.com",
    "britbox.com",
    "acorn.tv",
    "viu.com",
    "wetv.vip",
    "iqiyi.com", "iq.com",
    "tubitv.com",
    "pluto.tv",
    "spotify.com",
    "youtube-nocookie.com"
  ];
  var NO_OVERLAY_BLOCKED = (function () {
    var host = "";
    try { host = String(location.hostname || "").toLowerCase(); } catch (e) { return false; }
    for (var i = 0; i < NO_OVERLAY_HOSTS.length; i++) {
      var base = NO_OVERLAY_HOSTS[i];
      if (host === base || host.slice(-base.length - 1) === "." + base) return true;
    }
    return false;
  })();
  if (NO_OVERLAY_BLOCKED) return;
  // NOTE: a `return` here exits the whole sniffer IIFE: no hook injection,
  // no media registration, no overlay on these hosts.

  // Video-only allowlist: the floating button lists downloadable video, never audio.
  const VIDEO_FILE_RE = /\.(mp4|m4v|webm|mkv|avi|mov|flv)(\?|$)/i;
  const AUDIO_RE = /\.(mp3|m4a|aac|ogg|opus|flac|wav|wma)(\?|$)/i;
  const HLS_RE = /(\.m3u8|\/hls\/|\/playlist(?=[\/?#]|$)|\/manifest(?=[\/?#]|$)|\/master(?=[\/?#]|$)|\/stream\b|[\?&](format|ext)=m3u8|mime=.*mpegurl)/i;
  const DASH_RE = /(\.mpd|\/dash\/|\/manifest(?=[\/?#]|$)|\/master(?=[\/?#]|$)|[\?&](format|ext)=mpd|mime=.*dash)/i;
  const STREAM_URL_RE = /(\.m3u8|\.mpd|\.mp4|\.webm|\/manifest(?=[\/?#]|$)|\/playlist(?=[\/?#]|$)|\/master(?=[\/?#]|$)|\/stream\b)/i;
  const SEGMENT_RE = /\.(ts|m4s|m2ts)(\?|$)/i;
  // Basenames that carry no title (master.m3u8, index-v1-a1.m3u8, ...): labeled
  // from the page title instead so WDM never saves "master.ts".
  const GENERIC_MEDIA_RE = /^(master|index|playlist|chunklist|manifest|stream|play|video|media|file|download|index-v1-a\d+|seg-?\d*)(\.(m3u8|mpd|mp4|webm|mkv|mov|flv))?$/i;
  // Server filenames (Content-Disposition, observed by background) that name a
  // real media file. The stem replaces generic URL basenames / shell titles.
  const CD_MEDIA_EXT_RE = /\.(mp4|m4v|webm|mkv|avi|mov|flv|mpd|m3u8|mp3|m4a)(\?|$)/i;
  function cdLabelStem(fn) {
    try {
      let s = String(fn || "").split("?")[0].split("/").pop().split("\\").pop().trim();
      try { s = decodeURIComponent(s); } catch {}
      if (!s || !CD_MEDIA_EXT_RE.test(s)) return "";
      const dot = s.lastIndexOf(".");
      if (dot > 0) s = s.slice(0, dot);
      if (!s || GENERIC_MEDIA_RE.test(s)) return "";
      return s.slice(0, 120);
    } catch { return ""; }
  }
  // Embed-iframe titles carry no film name ("Viduki.net Api 1", "Vidl link Player"):
  // when the local document title looks like a provider shell, the top-page
  // title forwarded by background (pageTitleHint) wins instead.
  const EMBED_PROVIDER_TITLE_RE = /(viduki|vidy\.st|vidfast|vidlink|vidrock|vidzee|voe|filemoon|dood|streamwish|vitacloud|mixdrop|uptostream|\bembed\b|\bplayer\b|\bapi\b)/i;
  // API endpoints whose JSON bodies (not URLs) carry the real stream, e.g.
  // vidwara POST /api/stream -> {streaming_url}. Never treated as downloadable
  // items themselves — only scanned for embedded stream URLs.
  const API_BODY_RE = /(\/api\/stream|\/api\/videos?\b|\/api\/player|\/player-core|\/api\/streaming)\b/i;
  const BODY_STREAM_RE = /(https?:\\?\/\\?\/[^"'\s\\]+(?:\.m3u8|\.mpd|\.mp4|\.webm|\/playlist|\/manifest|\/master[^"'\s\\]*|\/hls[^"'\s\\]*|\/stream[^"'\s\\]*)(?:\?[^"'\s\\]*)?|"(?:streaming_url|source|file|hls|mp4|url)"\s*:\s*"[^"]+")/i;

  // IDM-grade MAIN-world hook injection (page-world fetch/XHR bypasses isolated world)
  function injectMainHook() {
    try {
      const url = (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.getURL)
        ? chrome.runtime.getURL("wdm_hook.js")
        : (typeof browser !== "undefined" && browser.runtime && browser.runtime.getURL)
          ? browser.runtime.getURL("wdm_hook.js") : null;
      if (!url) return;
      const s = document.createElement("script");
      s.src = url;
      s.onload = function () { s.remove(); };
      (document.head || document.documentElement).appendChild(s);
    } catch {}
  }
  // Bridge MAIN hook -> isolated world + background hint. Only confirmed video
  // kinds are registered; anything else goes to background verification.
  window.addEventListener("message", function (e) {
    if (e.source !== window) return;
    // Same-window hook only: a child iframe posting "*" must not be able to
    // forge media entries for the top page.
    try { if (location.origin && location.origin !== "null" && e.origin !== location.origin) return; } catch { return; }
    const d = e.data;
    if (!d || typeof d !== "object") return;
    if (d.type === "WDM_HOOK_MEDIA" && d.url) {
      if (typeof d.url !== "string" || !/^https?:\/\//i.test(d.url)) return;
      if (d.hint === "HLS" || d.hint === "DASH" || d.hint === "Video") registerMediaStream(d.url, d.hint);
      else sendVerifyMedia(d.url);
    }
    // MAIN-hook blob observation: page-local URL, verified same-origin below.
    // Registered for the overlay list — the bytes only move on user click.
    if (d.type === "WDM_BLOB_MEDIA" && typeof d.url === "string" && d.url.startsWith("blob:")) {
      try { if (new URL(d.url).origin !== location.origin) return; } catch { return; }
      registerMediaStream(d.url, "Video");
    }
    // MAIN-hook DRM signal (EME encrypted event / setMediaKeys in page world).
    if (d.type === "WDM_DRM_DETECT") {
      try {
        emeDrm = true;
        markDrmEntries();
      } catch {}
    }
    // MAIN-hook MEGA session id (page localStorage is invisible to this world).
    if (d.type === "WDM_MEGA_SID" && typeof d.sid === "string") {
      try {
        if (webext && webext.runtime && webext.runtime.sendMessage) {
          webext.runtime.sendMessage({ action: "megaSid", sid: d.sid, host: location.hostname });
        }
      } catch {}
    }
  });
  // Background webRequest hint (for worker/CSP streams not visible to content).
  // Background only forwards verified video kinds, so register directly.
  // The hint may carry the top-page title + observed quality/size so iframe
  // embeds (whose document.title is just "Viduki.net Api 1") still show the
  // real film name and rendition quality.
  try {
    const w = typeof browser !== "undefined" ? browser : chrome;
    if (w && w.runtime && w.runtime.onMessage) {
      w.runtime.onMessage.addListener((msg) => {
        if (msg && msg.action === "wdmMediaHint" && msg.url) {
          if (typeof msg.pageTitle === "string" && msg.pageTitle.trim().length >= 4) {
            pageTitleHint = msg.pageTitle.trim().slice(0, 120);
            // Backfill the real name onto entries stuck with a provider title.
            try {
              for (const e of detectedStreams.values()) {
                if (e && (isProviderTitle(e.label) || !e.label || e.label === "Video") && !isProviderTitle(pageTitleHint)) {
                  e.label = pageTitleHint;
                  e.pageTitle = pageTitleHint;
                }
              }
            } catch {}
          }
          registerMediaStream(msg.url, msg.hint || null, null, {
            quality: msg.quality || null,
            resolution: msg.resolution || null,
            size: msg.size || null,
            pageTitle: msg.pageTitle || null,
            fileName: msg.fileName || null
          });
        }
      });
    }
  } catch {}
  injectMainHook();

  const detectedStreams = new Map(); // url -> { url, label, type, size, quality, resolution, pageTitle }
  // Top-page title as told by background (tab.title). Set before the map is
  // used by the listener above at runtime (listener fires later), so `let`
  // placement here is safe — assignment happens only inside callbacks.
  let pageTitleHint = "";
  // Master playlist probe cache: masterUrl -> { best, variants: Map<absUrl, height> }.
  const masterProbeCache = new Map();
  // Live decoded frame height from the playing <video> (e.g. 1080). Used as a
  // last-resort quality signal when URLs are fully tokenized.
  let liveVideoHeight = 0;
  // EME DRM latch (media-sniffer/flowpick pattern): once any `encrypted`
  // event or MediaKeys request fires, Widevine/PlayReady protects playback —
  // blob/HLS entries without a keyUrl are tagged DRM-locked, not silently
  // listed as downloadable.
  let emeDrm = false;
  function markDrmEntries() {
    try {
      for (const e of detectedStreams.values()) {
        if (e && !e.keyUrl) e.drm = true;
      }
    } catch {}
  }
  // Isolated-world EME fallback (the MAIN hook also notifies via postMessage).
  try {
    document.addEventListener("encrypted", () => { try { emeDrm = true; markDrmEntries(); } catch {} }, true);
  } catch {}

  function isProviderTitle(t) {
    try {
      if (!t || typeof t !== "string") return true;
      const s = t.trim();
      if (s.length < 4) return true;
      if (s === "Video") return true;
      return EMBED_PROVIDER_TITLE_RE.test(s);
    } catch { return false; }
  }
  function realTitle() {
    try {
      const local = cleanTitleText(document.title);
      // Prefer the top-page title when the local one is a provider shell
      // ("Viduki.net Api 1") or missing entirely (bare embed iframe).
      if (pageTitleHint && !isProviderTitle(pageTitleHint)) {
        if (!local || isProviderTitle(local)) return pageTitleHint;
        // Both look real (edge: same-tab navigation) — the longer, more
        // specific one usually names the film, not the host.
        if (local.length < pageTitleHint.length - 12) return pageTitleHint;
        return local;
      }
      return local || pageTitleHint || "";
    } catch { return pageTitleHint || ""; }
  }
  // Snap a raw pixel height to the standard rendition ladder for badges.
  function snapHeight(h) {
    try {
      h = parseInt(h, 10);
      if (!(h > 0)) return 0;
      const ladder = [2160, 1440, 1080, 720, 480, 360, 240];
      for (const std of ladder) { if (h >= std - 40) return std; }
      return h;
    } catch { return 0; }
  }
  // Broad quality parse for tokenized player URLs: 1080p, /1080/, _720_,
  // ?height=1080, &res=720, itag-style and hls-XXX variants.
  function parseQualityFromUrl(url) {
    try {
      if (!url || typeof url !== "string") return "";
      let m = url.match(/(\d{3,4})p/i);
      if (m) {
        const h = snapHeight(m[1]);
        if (h) return h >= 2160 && /4k/i.test(url) ? "4K" : h + "p";
      }
      if (/(^|[^a-z])4k([^a-z]|$)/i.test(url)) return "4K";
      m = url.match(/\b(?:\d{3,4})x(\d{3,4})\b/i);
      if (m) {
        const h = snapHeight(m[1]);
        if (h) return h >= 2160 && /4k/i.test(url) ? "4K" : h + "p";
      }
      m = url.match(/[/?&=_-](2160|1440|1080|720|480|360|240)(?=[/?&=_\-.#]|$)/);
      if (m) return m[1] === "2160" && /4k/i.test(url) ? "4K" : m[1] + "p";
      m = url.match(/(?:height|res|resolution|quality|q)[=:](2160|1440|1080|720|480|360|240)/i);
      if (m) return m[1] + "p";
      m = url.match(/hls[-_](2160|1440|1080|720|480|360)/i);
      if (m) return m[1] + "p";
      m = url.match(/[?&]itag=(22|37|46|18|59|43|35|44|34)/i);
      if (m) {
        const itag = { "22": "720p", "37": "1080p", "46": "1080p", "18": "360p", "59": "480p", "43": "360p", "35": "480p", "44": "480p", "34": "360p" };
        return itag[m[1].toLowerCase()] || "";
      }
    } catch {}
    return "";
  }
  // Quality for rendition badges/labels. Priority: probed master playlist /
  // background observation > live <video> frame height > URL tokens.
  // Top-level scope: used by sendToWdm, refreshOverlayLabel and the overlay
  // renderer alike (a nested copy once left the outer callers throwing
  // "qualityOf is not defined").
  function qualityOf(s) {
    try {
      if (!s) return "";
      if (typeof s.quality === "string" && s.quality) return s.quality;
      if (s.resolution && parseInt(s.resolution, 10) > 0) {
        const h = snapHeight(s.resolution);
        if (h) return h >= 2160 ? "4K" : h + "p";
      }
      if (s.url) {
        const q = parseQualityFromUrl(s.url);
        if (q) return q;
      }
      if (liveVideoHeight && (s.type === "HLS" || s.type === "DASH")) {
        const h = snapHeight(liveVideoHeight);
        if (h) return h >= 2160 ? "4K" : h + "p";
      }
    } catch {}
    return "";
  }
  // Fetch a master .m3u8 and map its variants to heights (RESOLUTION wins,
  // BANDWIDTH orders). Resolves relative variant URIs against the master URL.
  // Best-effort: CORS or token expiry just leaves quality unknown.
  function probeHlsQuality(masterUrl) {
    try {
      if (!masterUrl || masterProbeCache.has(masterUrl)) {
        const c = masterProbeCache.get(masterUrl);
        if (c) applyMasterVariants(masterUrl, c);
        return;
      }
      if (!/\.m3u8(\?|$)/i.test(masterUrl)) return;
      masterProbeCache.set(masterUrl, null); // in-flight marker
      fetch(masterUrl, { credentials: "omit", cache: "no-store" }).then((r) => {
        if (!r || !r.ok) { masterProbeCache.delete(masterUrl); return null; }
        return r.text();
      }).then((text) => {
        try {
          if (!text || typeof text !== "string" || text.indexOf("#EXTM3U") < 0) { masterProbeCache.delete(masterUrl); return; }
          if (text.length > 65536) text = text.slice(0, 65536);
          const lines = text.split("\n");
          const variants = new Map();
          let best = 0;
          for (let i = 0; i < lines.length; i++) {
            const line = (lines[i] || "").trim();
            if (line.toLowerCase().indexOf("#ext-x-stream-inf:") !== 0) continue;
            let height = 0;
            const rm = /RESOLUTION\s*=\s*\d+\s*x\s*(\d+)/i.exec(line);
            if (rm) height = snapHeight(rm[1]);
            let uri = null;
            for (let j = i + 1; j < lines.length; j++) {
              const cand = (lines[j] || "").trim();
              if (!cand) continue;
              if (cand.charAt(0) === "#") { i = j; break; }
              uri = cand; i = j; break;
            }
            if (!uri) continue;
            let abs = uri;
            try { abs = new URL(uri, masterUrl).href; } catch {}
            if (height) {
              variants.set(abs, height);
              if (height > best) best = height;
            }
          }
          // No RESOLUTION tags: fall back to BANDWIDTH order (highest = best),
          // labeled from any quality token in the variant URIs.
          if (!best && variants.size === 0) {
            let bwBest = "";
            let bwBestVal = -1;
            for (let i = 0; i < lines.length; i++) {
              const line = (lines[i] || "").trim();
              if (line.toLowerCase().indexOf("#ext-x-stream-inf:") !== 0) continue;
              const bm = /BANDWIDTH\s*=\s*(\d+)/i.exec(line);
              const bw = bm ? parseInt(bm[1], 10) : -1;
              for (let j = i + 1; j < lines.length; j++) {
                const cand = (lines[j] || "").trim();
                if (!cand) continue;
                if (cand.charAt(0) === "#") { i = j; break; }
                if (bw > bwBestVal) { bwBestVal = bw; bwBest = cand; }
                i = j; break;
              }
            }
            if (bwBest) {
              let abs = bwBest;
              try { abs = new URL(bwBest, masterUrl).href; } catch {}
              const q = parseQualityFromUrl(abs);
              if (q) {
                const h = q === "4K" ? 2160 : (parseInt(q, 10) || 0);
                if (h) { variants.set(abs, h); best = h; }
              }
            }
          }
          if (!best) { masterProbeCache.delete(masterUrl); return; }
          const entry = { best, variants };
          masterProbeCache.set(masterUrl, entry);
          applyMasterVariants(masterUrl, entry);
        } catch { try { masterProbeCache.delete(masterUrl); } catch {} }
      }).catch(() => { try { masterProbeCache.delete(masterUrl); } catch {} });
    } catch {}
  }
  function applyMasterVariants(masterUrl, entry) {
    try {
      if (!entry || !entry.best) return;
      let changed = false;
      const bestQ = entry.best >= 2160 ? "4K" : entry.best + "p";
      for (const [variantUrl, h] of entry.variants.entries()) {
        const e = detectedStreams.get(variantUrl);
        if (e && !e.quality) {
          e.quality = h >= 2160 ? "4K" : h + "p";
          e.resolution = h;
          changed = true;
        }
      }
      // The master entry itself carries the ladder peak ("HLS 1080p").
      const m = detectedStreams.get(masterUrl);
      if (m && !m.quality) { m.quality = bestQ; m.resolution = entry.best; changed = true; }
      void changed;
    } catch {}
  }
  // Playlist URL -> observed EXT-X-KEY URI (1DM onPotentialM3u8AesKey equivalent).
  // Filled when a playlist response body is scanned; attached to the download
  // payload at click time. The desktop engine verifies by fetching — hint only.
  const playlistKeyHints = new Map();
  const KEY_URI_RE = /#EXT-X-KEY:[^\r\n]*URI="([^"]+)"/i;

  function scanPlaylistForKey(playlistUrl, text) {
    try {
      if (!playlistUrl || typeof text !== "string") return;
      if (text.length > 65536) text = text.slice(0, 65536);
      if (text.indexOf("#EXTM3U") < 0 || text.indexOf("#EXT-X-KEY:") < 0) return;
      const m = KEY_URI_RE.exec(text);
      if (!m || !m[1]) return;
      const keyUrl = new URL(m[1], playlistUrl).href;
      if (/^https?:\/\//i.test(keyUrl)) playlistKeyHints.set(playlistUrl, keyUrl);
    } catch {}
  }

  function isPlaylistUrl(url) {
    try {
      return /\.m3u8(\?|$)/i.test(url) || /\/(playlist|chunklist)[^?]*(\?|$)/i.test(url);
    } catch { return false; }
  }
  const playerOverlays = new Map();  // videoElement -> overlayElement
  let wdmActive = false;

  // Track cursor position globally to overcome transparent player shields
  let lastMouseX = -1;
  let lastMouseY = -1;
  window.addEventListener("mousemove", (e) => {
    lastMouseX = e.clientX;
    lastMouseY = e.clientY;
  }, { passive: true });

  const webext = typeof browser !== "undefined" ? browser : (typeof chrome !== "undefined" ? chrome : null);

  // 1. Connection check with WDM Desktop via Background script
  function checkWdm() {
    try {
      if (webext && webext.runtime && webext.runtime.sendMessage) {
        webext.runtime.sendMessage({ action: "ping" }, (res) => {
          if (webext.runtime.lastError) { wdmActive = false; return; }
          wdmActive = !!res?.active;
        });
      } else {
        window.postMessage({ type: "WDM_PING_REQ" }, "*");
      }
    } catch {
      wdmActive = false;
    }
  }
  checkWdm();
  setInterval(checkWdm, 5000);

  // 2. Dispatch download to WDM. Accepts either (url, label, type) or a
  // stream entry object — the entry form carries quality/pageTitle so the
  // desktop app saves "Land of Bad 1080p.mp4" instead of "master.m3u8".
  function buildDownloadFileName(entryOrLabel, quality) {
    try {
      let base = "";
      let q = quality || "";
      if (entryOrLabel && typeof entryOrLabel === "object") {
        base = entryOrLabel.label || entryOrLabel.pageTitle || "";
        q = entryOrLabel.quality || q;
      } else {
        base = entryOrLabel || "";
      }
      base = String(base || "").trim();
      if (isProviderTitle(base)) base = realTitle() || base;
      if (!base) base = realTitle() || "Video";
      if (q && !new RegExp(q.replace("4K", "4K"), "i").test(base)) {
        // Avoid "Land of Bad 1080p 1080p": append once.
        if (!/(\d{3,4}p|4K)\s*$/i.test(base)) base = base + " " + q;
      }
      return base.slice(0, 180);
    } catch { return (typeof entryOrLabel === "string" ? entryOrLabel : "Video"); }
  }
  // Compact byte count for overlay badges ("HLS 1080p • 78.9 MB").
  function formatBytes(n) {
    try {
      n = Number(n);
      if (!n || n <= 0) return "";
      if (n >= 1024 * 1024 * 1024) return (n / (1024 * 1024 * 1024)).toFixed(2).replace(/\.?0+$/, "") + " GB";
      if (n >= 1024 * 1024) return (n / (1024 * 1024)).toFixed(1).replace(/\.0$/, "") + " MB";
      if (n >= 1024) return (n / 1024).toFixed(1).replace(/\.0$/, "") + " KB";
      return n + " B";
    } catch { return ""; }
  }

  // Ask the background worker for header-probed sizes/filenames (HEAD, ranged
  // GET fallback, cached) and patch the rendered badges in place. Entries
  // already carrying an observed size keep it; probed filenames only replace
  // generic labels. Late responses for detached dropdowns are dropped.
  function enrichWithSizes(items) {
    try {
      const targets = (items || []).filter((it) =>
        it && it.s && typeof it.s.url === "string" && /^https?:\/\//i.test(it.s.url) &&
        it.badgeSpan && it.badgeSpan.isConnected);
      if (!targets.length) return;
      if (!webext || !webext.runtime || !webext.runtime.sendMessage) return;
      const urls = [];
      const hints = {};
      for (const it of targets) {
        urls.push(it.s.url);
        if (it.s.reqHeaders && typeof it.s.reqHeaders === "object") hints[it.s.url] = it.s.reqHeaders;
      }
      const apply = (res) => {
        try {
          const probes = (res && res.probes) || {};
          for (const it of targets) {
            if (!it.badgeSpan.isConnected) continue;
            const p = probes[it.s.url];
            if (!p) continue;
            if (p.size && !it.hasObservedSize) {
              const txt = formatBytes(p.size);
              if (txt) it.badgeSpan.textContent = it.badgeSpan.textContent + " • " + txt;
            }
            if (p.fileName && it.genericLabel && it.labelDiv && it.labelDiv.isConnected) {
              it.labelDiv.textContent = String(p.fileName);
              try { it.labelDiv.setAttribute("title", String(p.fileName).slice(0, 2048)); } catch {}
              try { it.s.label = String(p.fileName); } catch {}
            }
          }
        } catch {}
      };
      const ret = webext.runtime.sendMessage({ action: "probeSizes", urls, reqHeaders: hints }, apply);
      if (ret && typeof ret.then === "function") ret.then(apply, () => {});
    } catch {}
  }
  // Ambient fullscreen hero backgrounds (muted object-fit:cover video covering
  // most of the viewport) are decor, not user content: no overlay on them.
  // Listings are unaffected — a hero file stays downloadable, just unbadged.
  // Plain-object args so this stays unit-testable without a DOM.
  function isAmbientBackgroundVideo(rect, vpW, vpH, objectFit, muted) {
    try {
      if (!rect || !objectFit) return false;
      if (String(objectFit).toLowerCase() !== "cover") return false;
      if (!muted) return false;
      const w = rect.right - rect.left;
      const h = rect.bottom - rect.top;
      if (!(w > 0 && h > 0 && vpW > 0 && vpH > 0)) return false;
      if ((w * h) / (vpW * vpH) < 0.6) return false;
      return true;
    } catch { return false; }
  }
  function sendToWdm(url, label, streamType) {
    // Entry-object form: sendToWdm(entry). Returns a promise resolving to the
    // background response ({success, status, error}) so overlay callers can
    // report the real outcome instead of assuming the click worked.
    let entry = null;
    if (url && typeof url === "object") { entry = url; url = entry.url; label = entry.label; streamType = entry.type; }
    const quality = (entry && entry.quality) || (entry ? qualityOf(entry) : (typeof label === "string" ? "" : ""));
    // Page-local blob: fetch here (only this context can read it) and stream
    // base64 chunks to the desktop app, which imports the bytes as a file.
    if (typeof url === "string" && url.startsWith("blob:")) {
      try {
        return sendBlobToWdm(url, buildDownloadFileName(entry || label, quality)).then(
          () => ({ success: true }),
          (e) => {
            const msg = (e && e.message) || "blob handoff failed";
            try { console.warn("[WDM] Blob handoff failed:", e); } catch {}
            return { success: false, error: msg };
          });
      } catch (e) {
        try { console.warn("[WDM] Blob handoff failed:", e); } catch {}
        return Promise.resolve({ success: false, error: (e && e.message) || "blob handoff failed" });
      }
    }
    const title = realTitle() || document.title || null;
    const payload = {
      url: url,
      fileName: buildDownloadFileName(entry || label, quality) || null,
      referer: location.href,
      headers: {
        "Referer": location.href,
        "Origin": location.origin
      },
      pageTitle: title,
      streamType: streamType || "auto",
      keyUrl: (entry && entry.keyUrl) || playlistKeyHints.get(url) || null,
      drm: !!(entry && entry.drm)
    };

    try {
      if (webext && webext.runtime && webext.runtime.sendMessage) {
        return requestDownload(payload);
      }
      window.postMessage({ type: "WDM_DOWNLOAD_REQ", payload }, "*");
      // postMessage relay has no confirmation channel — keep it optimistic.
      return Promise.resolve({ success: true });
    } catch (e) {
      console.warn("[WDM] Error sending download:", e);
      return Promise.resolve({ success: false, error: (e && e.message) || "send-failed" });
    }
  }

  // Promise wrapper around the background "download" RPC: resolves with the
  // background response ({success, status, error}). Handles both chrome
  // (callback) and browser (promise) sendMessage shapes, plus a timeout so
  // a dead worker can never leave the overlay hanging.
  function requestDownload(payload) {
    return new Promise((resolve) => {
      let settled = false;
      const done = (res) => { if (!settled) { settled = true; resolve(res || {}); } };
      try {
        if (!webext || !webext.runtime || !webext.runtime.sendMessage) {
          done({ success: false, error: "no-messaging" });
          return;
        }
        const ret = webext.runtime.sendMessage({ action: "download", payload }, done);
        if (ret && typeof ret.then === "function") ret.then(done, () => done({ success: false, error: "no-response" }));
        setTimeout(() => done({ success: false, error: "timeout" }), 10000);
      } catch (e) { done({ success: false, error: (e && e.message) || "send-failed" }); }
    });
  }

  // Short user-facing reason for a failed handoff (shown on the overlay).
  function downloadErrorText(res) {
    if (!res) return "Send failed";
    if (res.status === 401 || (res.error && /unauthor|token/i.test(String(res.error))))
      return "Reload the WDM extension to reconnect";
    if (res.error === "unreachable" || res.error === "timeout" || res.error === "no-response")
      return "WDM app unreachable: is it running?";
    if (res.error) return String(res.error).slice(0, 80);
    if (res.status) return "Rejected (HTTP " + res.status + ")";
    return "Send failed";
  }

  // Report the real handoff outcome on the overlay: success flash, or the
  // failure reason long enough to read (4s) + console detail.
  function confirmOverlaySent(overlay, res) {
    if (res && res.success) {
      flashOverlayLabel(overlay, "Sent to WDM!");
      return;
    }
    const msg = downloadErrorText(res);
    try { console.warn("[WDM] Download handoff failed:", msg, res); } catch {}
    flashOverlayLabel(overlay, msg, 4000);
  }

  // Blob pipeline (1DM SaveBlobTask equivalent): the page owns its blob: URLs,
  // so the bytes are read here and posted as ordered base64 chunks. The blob:
  // URL itself never leaves the page — only bytes + page context travel.
  const blobInFlight = new Set();
  function u8ToB64(u8) {
    let s = "";
    const CH = 0x8000;
    for (let i = 0; i < u8.length; i += CH) {
      s += String.fromCharCode.apply(null, u8.subarray(i, i + CH));
    }
    return btoa(s);
  }
  async function sendBlobToWdm(blobUrl, label) {
    if (!blobUrl || blobInFlight.has(blobUrl)) return;
    if (!webext || !webext.runtime || !webext.runtime.sendMessage) return;
    blobInFlight.add(blobUrl);
    try {
      const resp = await fetch(blobUrl);
      const blob = await resp.blob();
      const mime = ((blob && blob.type) || "application/octet-stream").split(";")[0].trim() || "application/octet-stream";
      const buf = new Uint8Array(await blob.arrayBuffer());
      if (buf.length === 0) throw new Error("empty blob");
      const id = "b" + Date.now().toString(36) + Math.floor(Math.random() * 0xffffff).toString(36);
      const CHUNK = 1024 * 1024;
      const total = Math.ceil(buf.length / CHUNK);
      for (let i = 0; i < total; i++) {
        const slice = buf.subarray(i * CHUNK, Math.min(buf.length, (i + 1) * CHUNK));
        const res = await webext.runtime.sendMessage({ action: "blobChunk", chunk: {
          id, seq: i, last: i === total - 1, mime,
          data: u8ToB64(slice),
          fileName: label || null, referer: location.href, pageTitle: document.title || null
        }});
        if (!res || res.success === false) {
          throw new Error((res && (res.error || ("status " + res.status))) || "chunk refused");
        }
      }
    } finally {
      blobInFlight.delete(blobUrl);
    }
  }

  // 3. Inject global overlay CSS styles
  function injectStyles() {
    if (document.getElementById("wdm-media-sniffer-styles")) return;
    const style = document.createElement("style");
    style.id = "wdm-media-sniffer-styles";
    style.textContent = `
      .wdm-player-overlay {
        position: absolute;
        z-index: 2147483645;
        display: inline-flex;
        align-items: center;
        gap: 5px;
        background: #f5f5f5;
        border: 1px solid #d0d0d0;
        border-radius: 4px;
        padding: 3px 8px;
        color: #333333;
        font-family: Arial, Helvetica, sans-serif;
        font-size: 11px;
        font-weight: 400;
        line-height: 1.4;
        white-space: nowrap;
        box-shadow: 0 1px 3px rgba(0, 0, 0, 0.30);
        cursor: pointer;
        opacity: 0;
        pointer-events: none;
        transition: opacity 0.15s ease;
        user-select: none;
        -webkit-user-select: none;
      }
      .wdm-player-overlay.wdm-visible {
        opacity: 1;
        pointer-events: auto;
      }
      .wdm-player-overlay:hover {
        background: #e9e9e9;
        border-color: #bdbdbd;
      }
      .wdm-player-overlay-icon {
        width: 14px;
        height: 14px;
        flex-shrink: 0;
        display: inline-block;
      }
      .wdm-player-overlay-label {
        white-space: nowrap;
        color: #333333;
      }
      .wdm-player-overlay-close {
        margin-left: 2px;
        width: 16px;
        height: 16px;
        padding: 0;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        background: transparent;
        border: none;
        border-radius: 3px;
        color: #888888;
        font-size: 11px;
        line-height: 1;
        cursor: pointer;
        flex-shrink: 0;
      }
      .wdm-player-overlay-close:hover {
        background: #d5d5d5;
        color: #333333;
      }
      .wdm-player-overlay-dropdown {
        position: absolute;
        bottom: 100%;
        right: 0;
        margin-bottom: 4px;
        background: #ffffff;
        border: 1px solid #d0d0d0;
        border-radius: 4px;
        box-shadow: 0 2px 8px rgba(0,0,0,0.25);
        display: none;
        flex-direction: column;
        min-width: 180px;
        max-width: 280px;
        overflow: hidden;
        z-index: 2147483647;
      }
      .wdm-player-overlay-dropdown.wdm-open {
        display: flex;
      }
      .wdm-dropdown-item {
        padding: 8px 12px;
        font-size: 11px;
        color: #333333;
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 8px;
        border-bottom: 1px solid #eeeeee;
        transition: background 0.15s;
        cursor: pointer;
      }
      .wdm-dropdown-item:last-child { border-bottom: none; }
      .wdm-dropdown-item:hover { background: #e8f0fe; color: #1a73e8; }
      .wdm-dropdown-badge {
        font-size: 10px;
        padding: 2px 5px;
        border-radius: 4px;
        background: #eeeeee;
        color: #555555;
        font-weight: 600;
        text-transform: uppercase;
      }
    `;
    document.head ? document.head.appendChild(style) : document.documentElement.appendChild(style);
  }

  // 4. Create or update floating overlay for a video element
  function getOrCreateOverlay(videoEl) {
    if (playerOverlays.has(videoEl)) {
      return playerOverlays.get(videoEl);
    }

    injectStyles();

    const overlay = document.createElement("div");
    overlay.className = "wdm-player-overlay";

    // WDM icon SVG
    const svgIcon = `
      <svg class="wdm-player-overlay-icon" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
        <path d="M12 3V16M12 16L7 11M12 16L17 11" stroke="#38bdf8" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"/>
        <path d="M3 18V19C3 20.1046 3.89543 21 5 21H19C20.1046 21 21 20.1046 21 19V18" stroke="#38bdf8" stroke-width="2.5" stroke-linecap="round"/>
      </svg>
    `;

    // WDM logo (extension icon, same source as the YouTube button)
    const logoUrl = (webext.runtime && webext.runtime.getURL) ? webext.runtime.getURL("icon16.png") : "";
    const logoImg = logoUrl
      ? `<img class="wdm-player-overlay-icon" src="${logoUrl}" alt="WDM" />`
      : svgIcon;

    overlay.innerHTML = `
      ${logoImg}
      <span class="wdm-player-overlay-label">Download</span>
      <span style="font-size:9px; opacity:0.7; margin-left:2px;">▼</span>
      <button class="wdm-player-overlay-close" title="Hide">✕</button>
      <div class="wdm-player-overlay-dropdown"></div>
    `;

    // If the packaged icon is unreachable, fall back to the arrow glyph.
    const logoEl = overlay.querySelector(".wdm-player-overlay-icon");
    if (logoEl && logoEl.tagName === "IMG") {
      logoEl.addEventListener("error", () => {
        try {
          const tmp = document.createElement("div");
          tmp.innerHTML = svgIcon;
          const svg = tmp.firstChild;
          if (svg) logoEl.replaceWith(svg);
        } catch {}
      });
    }

    const closeBtn = overlay.querySelector(".wdm-player-overlay-close");
    const dropdown = overlay.querySelector(".wdm-player-overlay-dropdown");
    closeBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      e.preventDefault();
      videoEl._wdmDismissed = true;
      if (overlay._wdmInterval) { clearInterval(overlay._wdmInterval); overlay._wdmInterval = null; }
      overlay.remove();
      playerOverlays.delete(videoEl);
      try { if (videoEl._wdmRO) videoEl._wdmRO.disconnect(); } catch {}
      try { if (videoEl._wdmIO) videoEl._wdmIO.disconnect(); } catch {}
    });
    // Quality for rendition badges — see top-level qualityOf().
    function renderDropdown() {
      dropdown.innerHTML = "";
      // Group renditions by directory: master manifest first, then highest
      // quality first, max 3 entries per video so the list stays short.
      const groups = new Map();
      for (const s of detectedStreams.values()) {
        let key = s.url;
        try {
          const u = new URL(s.url);
          if (/(^|\/)init\.mp4$/i.test(u.pathname)) continue; // DASH init — not a video
          if (/\.(ts|m4s)(\?|$)/i.test(u.pathname)) continue; // segments are not items
          key = u.origin + u.pathname.split("/").slice(0, -1).join("/");
        } catch {}
        if (!groups.has(key)) groups.set(key, []);
        groups.get(key).push(s);
      }
      const qualityRank = (q) => q === "4K" ? 5000 : (parseInt(q, 10) || 0);
      const dirOf = (url) => {
        try { const u = new URL(url); return u.origin + u.pathname.split("/").slice(0, -1).join("/"); }
        catch { return url; }
      };
      const filtered = [];
      const perGroup = new Map();
      for (const items of groups.values()) {
        const sorted = items.slice().sort((a, b) => {
          const aMaster = /\/master\.m3u8/i.test(a.url) ? 0 : 1;
          const bMaster = /\/master\.m3u8/i.test(b.url) ? 0 : 1;
          if (aMaster !== bMaster) return aMaster - bMaster;
          return qualityRank(qualityOf(b)) - qualityRank(qualityOf(a));
        });
        // Prefer labeled/quality variants; max 3 entries per video.
        const seenQ = new Set();
        for (const s of sorted) {
          if (filtered.length >= 6) break;
          const g = dirOf(s.url);
          if ((perGroup.get(g) || 0) >= 3) continue;
          const q = qualityOf(s) || (/\/master\.m3u8/i.test(s.url) ? "master" : s.url);
          if (seenQ.has(q)) continue;
          seenQ.add(q);
          perGroup.set(g, (perGroup.get(g) || 0) + 1);
          filtered.push(s);
        }
        if (filtered.length >= 6) break;
      }
      const streams = filtered.length ? filtered : Array.from(detectedStreams.values()).slice(0, 3);
      if (streams.length === 0) {
        const item = document.createElement("div");
        item.className = "wdm-dropdown-item";
        const emptyLabel = document.createElement("div");
        emptyLabel.style.cssText = "overflow:hidden; text-overflow:ellipsis; white-space:nowrap; max-width:180px;";
        emptyLabel.textContent = "Resolve this page in WDM";
        try { emptyLabel.setAttribute("title", String(location.href || "").slice(0, 2048)); } catch {}
        const emptyBadge = document.createElement("span");
        emptyBadge.className = "wdm-dropdown-badge";
        emptyBadge.textContent = "Page";
        item.appendChild(emptyLabel);
        item.appendChild(emptyBadge);
        item.addEventListener("click", (e) => {
          e.stopPropagation();
          dropdown.classList.remove("wdm-open");
          flashOverlayLabel(overlay, "Sending…", 10000);
          Promise.resolve()
            .then(() => sendPageToWdm())
            .then((res) => confirmOverlaySent(overlay, res || { success: true }),
              (err) => confirmOverlaySent(overlay, { success: false, error: (err && err.message) || "send-failed" }));
        });
        dropdown.appendChild(item);
        return;
      }

      // Show the grouped video entries with quality badges. Labels show the
      // real stream name (top-page title), badges carry type + rendition
      // ("HLS 1080p") so identical provider shells become distinguishable.
      // Header-probed sizes land asynchronously ("HLS 1080p • 78.9 MB").
      const toShow = streams.slice(0, 6);
      const sizeTargets = [];
      for (const s of toShow) {
        const item = document.createElement("div");
        item.className = "wdm-dropdown-item";
        const q = qualityOf(s);
        let rawLabel = (s.label && s.label.startsWith("master-")) ? "Main video (DASH)" : (s.label || s.pageTitle || "Video");
        if (isProviderTitle(rawLabel)) rawLabel = s.pageTitle && !isProviderTitle(s.pageTitle) ? s.pageTitle : (realTitle() || rawLabel);
        let pretty = rawLabel;
        let badge = s.type || "Video";
        if (s.drm) badge = badge + " \uD83D\uDD12";
        if (q && pretty.toLowerCase().indexOf(q.toLowerCase()) < 0) badge = badge + " " + q;
        // Already-observed size (passive response headers) shows immediately;
        // probed sizes patch in async via enrichWithSizes below.
        if (s.size && s.size > 0) {
          const seen = formatBytes(s.size);
          if (seen) badge = badge + " • " + seen;
        }
        // Build DOM with textContent/setAttribute only — never innerHTML with
        // attacker-controlled URLs/labels (XSS via title="..." breakout).
        const labelDiv = document.createElement("div");
        labelDiv.style.cssText = "overflow:hidden; text-overflow:ellipsis; white-space:nowrap; max-width:180px;";
        labelDiv.textContent = String(pretty);
        try { labelDiv.setAttribute("title", String(s.url || "").slice(0, 2048)); } catch {}
        const badgeSpan = document.createElement("span");
        badgeSpan.className = "wdm-dropdown-badge";
        badgeSpan.textContent = String(badge);
        item.appendChild(labelDiv);
        item.appendChild(badgeSpan);
        sizeTargets.push({
          s,
          badgeSpan,
          labelDiv,
          hasObservedSize: !!(s.size && s.size > 0),
          genericLabel: !pretty || pretty === "Video" || isProviderTitle(rawLabel),
        });
        item.addEventListener("click", (e) => {
          e.stopPropagation();
          dropdown.classList.remove("wdm-open");
          flashOverlayLabel(overlay, "Sending…", 10000);
          Promise.resolve()
            .then(() => sendToWdm(s))
            .then((res) => confirmOverlaySent(overlay, res || { success: true }),
              (err) => confirmOverlaySent(overlay, { success: false, error: (err && err.message) || "send-failed" }));
        });
        dropdown.appendChild(item);
      }
      enrichWithSizes(sizeTargets);
    }

    overlay.addEventListener("click", (e) => {
      if (e.target === closeBtn) return;
      e.stopPropagation();
      e.preventDefault();
      const streams = Array.from(detectedStreams.values());
      // No captured stream yet: hand the player page to WDM for resolving.
      if (streams.length === 0) {
        flashOverlayLabel(overlay, "Sending…", 10000);
        Promise.resolve()
          .then(() => sendPageToWdm())
          .then((res) => confirmOverlaySent(overlay, res || { success: true }),
            (err) => confirmOverlaySent(overlay, { success: false, error: (err && err.message) || "send-failed" }));
        return;
      }
      if (streams.length === 1) {
        flashOverlayLabel(overlay, "Sending…", 10000);
        Promise.resolve()
          .then(() => sendToWdm(streams[0]))
          .then((res) => confirmOverlaySent(overlay, res || { success: true }),
            (err) => confirmOverlaySent(overlay, { success: false, error: (err && err.message) || "send-failed" }));
      } else {
        renderDropdown();
        dropdown.classList.toggle("wdm-open");
      }
    });
    document.addEventListener("click", () => dropdown.classList.remove("wdm-open"));

    // Positioning loop matching the video's bounding rect — IDM-grade: checks computedStyle + overflow clipping
    function isVisibleVideo(el) {
      if (!el || el.readyState === 0 && !el.src && !el.currentSrc) return false;
      try {
        const cs = window.getComputedStyle(el);
        if (cs.visibility === "hidden" || cs.display === "none" || cs.opacity === "0") return false;
        if (cs.objectFit === "cover" && el.autoplay && el.muted && el.loop && !el.controls && (cs.pointerEvents === "none" || parseInt(cs.zIndex || "0", 10) < 0)) return false;
        // Ambient fullscreen hero background (muted cover filling the viewport):
        // decor, not user content — no overlay regardless of controls/loop flags.
        try {
          if (isAmbientBackgroundVideo(el.getBoundingClientRect(), window.innerWidth, window.innerHeight, cs.objectFit, el.muted)) return false;
        } catch {}
      } catch {}
      const r = el.getBoundingClientRect();
      if (r.width < 120 || r.height < 90) return false;
      if (r.bottom <= 0 || r.top >= window.innerHeight || r.right <= 0 || r.left >= window.innerWidth) return false;
      return true;
    }
    function positionOverlay() {
      if (!videoEl.isConnected) {
        if (overlay._wdmInterval) { clearInterval(overlay._wdmInterval); overlay._wdmInterval = null; }
        overlay.remove();
        playerOverlays.delete(videoEl);
        try { if (videoEl._wdmRO) videoEl._wdmRO.disconnect(); } catch {}
        try { if (videoEl._wdmIO) videoEl._wdmIO.disconnect(); } catch {}
        return;
      }
      if (!isVisibleVideo(videoEl)) {
        overlay.classList.remove("wdm-visible");
        return;
      }
      const rect = videoEl.getBoundingClientRect();
      // Check if mouse cursor is inside the video rectangle (bypasses transparent player controls/shields)
      const isMouseInsideVideo = lastMouseX >= rect.left && lastMouseX <= rect.right &&
                                 lastMouseY >= rect.top && lastMouseY <= rect.bottom;
      const playerContainer = videoEl.closest(".player, [class*='player'], [class*='video'], [id*='player'], [id*='video'], .video-js, [class*='jw-']");
      const isContainerHovered = playerContainer ? playerContainer.matches(":hover") : false;
      const isHovered = isMouseInsideVideo ||
                        isContainerHovered ||
                        videoEl.matches(":hover") ||
                        (videoEl.parentElement && videoEl.parentElement.matches(":hover")) ||
                        overlay.matches(":hover");
      if (videoEl._wdmDismissed) {
        overlay.classList.remove("wdm-visible");
        dropdown.classList.remove("wdm-open");
        return;
      }
      // Extension-only flow: the button is always offered on hover — it downloads
      // captured streams, or resolves the page in WDM when nothing was captured.
      if (isHovered) {
        refreshOverlayLabel(overlay);
        overlay.classList.add("wdm-visible");
      } else {
        overlay.classList.remove("wdm-visible");
        dropdown.classList.remove("wdm-open");
      }
      overlay.style.position = "fixed";
      // Bottom-right of the player; if that would clip off-viewport, fall
      // back to the top edge instead of letting it disappear.
      const estH = overlay.offsetHeight > 0 ? overlay.offsetHeight : 30;
      let top = rect.bottom - estH - 10;
      if (top + estH > window.innerHeight - 8)
        top = window.innerHeight - estH - 8;
      if (top < 8)
        top = Math.max(8, rect.top + 10);
      overlay.style.top = top + "px";
      overlay.style.right = Math.max(8, window.innerWidth - rect.right + 10) + "px";
      overlay.style.zIndex = "2147483647";
      const fsElem = document.fullscreenElement || document.webkitFullscreenElement;
      const targetParent = (fsElem && fsElem.contains(videoEl)) ? fsElem : document.body;
      if (overlay.parentElement !== targetParent) targetParent.appendChild(overlay);
    }
    // IDM uses ResizeObserver + IntersectionObserver; emulate with both + polling fallback
    try {
      const ro = new ResizeObserver(positionOverlay);
      ro.observe(videoEl);
      videoEl._wdmRO = ro;
    } catch {}
    try {
      const io = new IntersectionObserver((entries) => {
        for (const en of entries) {
          if (en.target === videoEl) {
            if (en.intersectionRatio < 0.15) overlay.classList.remove("wdm-visible");
            else positionOverlay();
          }
        }
      }, { threshold: [0, 0.15, 0.5] });
      io.observe(videoEl);
      videoEl._wdmIO = io;
    } catch {}
    const interval = setInterval(positionOverlay, 400);
    overlay._wdmInterval = interval;
    videoEl.addEventListener("play", positionOverlay);
    videoEl.addEventListener("loadedmetadata", positionOverlay);
    videoEl.addEventListener("mouseenter", positionOverlay);
    videoEl.addEventListener("mouseleave", () => setTimeout(positionOverlay, 200));

    playerOverlays.set(videoEl, overlay);
    return overlay;
  }

  // 5. (removed) Corner notification — now floating button only

  // Ask background to verify an opaque URL via observed response headers.
  // Only confirmed video (video/*, HLS, DASH) comes back as a media hint.
  function sendVerifyMedia(url) {
    try {
      if (!url || typeof url !== "string") return;
      if (url.startsWith("data:") || url.startsWith("blob:")) return;
      if (webext && webext.runtime && webext.runtime.sendMessage) {
        try { url = new URL(url, location.href).href; } catch { return; }
        if (detectedStreams.has(url)) return;
        webext.runtime.sendMessage({ action: "verifyMedia", url });
      }
    } catch {}
  }

  // Page title cleaned for use as a filename stem ("Watch My Film - VOE" -> "My Film").
  function cleanTitleText(title) {
    try {
      let t = (title || "").replace(/\s+/g, " ").trim();
      t = t.replace(/^\s*(watch|now playing)\s*[:\-–—]\s*/i, "");
      t = t.replace(/\s*[-–—|»•]\s*[^-–—|»•]*$/, "");
      t = t.replace(/^\s*(watch|now playing)\s+/i, "");
      if (t.length > 120) t = t.slice(0, 120).trim();
      return t.length >= 4 ? t : "";
    } catch { return ""; }
  }

  // 6. Record and sync a discovered stream URL — video only. Anything that is
  // not provably HLS/DASH/direct-video is ignored here (opaque URLs go through
  // sendVerifyMedia and only return if background confirms video content).
  // extra carries background enrichment {quality, resolution, size, pageTitle}.
  function registerMediaStream(url, hintType, customLabel, extra) {
    if (!url || typeof url !== "string") return;
    if (url.startsWith("data:") || url.startsWith("blob:http://127.0.0.1") || url.startsWith("blob:http://localhost")) return;
    // Player API endpoints (e.g. POST /api/stream) are scanned for bodies,
    // never downloadable items themselves — even when the path contains a
    // stream-ish word. Only a real media extension lets an /api/ URL through.
    try {
      const _u = new URL(url, location.href);
      if (API_BODY_RE.test(_u.pathname) &&
          !/\.(m3u8|mpd|mp4|webm|mkv|avi|mov|flv)(\?|$)/i.test(url)) return;
    } catch {}
    if (/youtube\.com|youtu\.be|youtube-nocookie/i.test(url)) return;
    try {
      const p = new URL(url, location.href).pathname.toLowerCase();
      if (/(^|\/)(failure|no_input|open|success)\.mp3(\?|$)/i.test(p)) return;
      if (/(^|\/)init\.mp4(\?|$)/i.test(p)) return; // DASH init — not a video
    } catch {}
    if (/\.(ts|m4s)(\?|$)/i.test(url)) return; // segments are not downloadable items
    // Curated noise filter — mirrors background.js isNoiseUrl (keep in sync).
    // Content entries carry no observed headers, so only exact-filename UI
    // sounds + updater/beacon path segments are dropped here; anything with
    // real evidence is adopted by the background merge instead.
    try {
      const lu = url.toLowerCase();
      if (/(^|\/)(click|hover|ding|pop|tick|tock|beep|chime|alert|notification|message)-?[a-z0-9]*\.(mp3|wav|ogg|m4a)(\?|$)/.test(lu)) return;
      const pl = new URL(url, location.href).pathname.toLowerCase();
      if (/(^|\/)(auto-?update|update-?check|idmupdt|omaha|beacon|heartbeat|telemetry)(\/|$|\?|_|-)/.test(pl)) return;
    } catch {}

    // Resolve relative URLs
    try {
      url = new URL(url, location.href).href;
    } catch {
      return;
    }

    if (AUDIO_RE.test(url)) return; // never list audio, even from <video> tags
    const existing = detectedStreams.get(url);
    if (existing) {
      // Enrich a known entry (background hint arrived after first sighting).
      try {
        if (extra && typeof extra === "object") {
          if (!existing.quality && extra.quality) existing.quality = extra.quality;
          if (!existing.resolution && extra.resolution) existing.resolution = extra.resolution;
          if (!existing.size && extra.size) existing.size = extra.size;
          if (extra.fileName && !existing.fileName) {
            existing.fileName = extra.fileName;
            const stem = cdLabelStem(extra.fileName);
            if (stem) existing.label = stem;
          }
          if (extra.pageTitle && typeof extra.pageTitle === "string" && extra.pageTitle.trim().length >= 4) {
            pageTitleHint = extra.pageTitle.trim().slice(0, 120);
            if (isProviderTitle(existing.label)) { existing.label = pageTitleHint; existing.pageTitle = pageTitleHint; }
          }
        }
        if (!existing.quality && existing.type === "HLS") probeHlsQuality(url);
        if (emeDrm && (url.startsWith("blob:") || existing.type === "HLS" || existing.type === "DASH")) existing.drm = true;
      } catch {}
      return;
    }

    // Trusted background verification passes explicit kinds through; everything
    // else must match a known video pattern or it is dropped. Explicit
    // extensions and /dash/ vs /hls/ directory markers win over the looser
    // /manifest|/playlist|/master|/stream patterns both regexes share.
    let type = null;
    if (hintType === "HLS" || hintType === "DASH" || hintType === "Video") type = hintType;
    if (/\.m3u8(\?|$)/i.test(url)) type = "HLS";
    else if (/\.mpd(\?|$)/i.test(url)) type = "DASH";
    else if (/\/dash\//i.test(url)) type = "DASH";
    else if (HLS_RE.test(url)) type = "HLS";
    else if (DASH_RE.test(url)) type = "DASH";
    else if (VIDEO_FILE_RE.test(url)) type = "Video";
    if (!type) return;

    let label = customLabel;
    let pageTitle = (extra && extra.pageTitle) || pageTitleHint || "";
    // Background/top-page title wins over a provider-shell iframe title.
    if (typeof pageTitle === "string" && pageTitle.trim().length >= 4) {
      pageTitleHint = pageTitle.trim().slice(0, 120);
      pageTitle = pageTitleHint;
    } else {
      pageTitle = pageTitleHint;
    }
    // Blob URLs carry opaque UUID paths: use the real title, not the UUID.
    if (!label && url.startsWith("blob:")) label = realTitle() || "Video";
    if (!label) {
      try {
        const u = new URL(url);
        label = u.pathname.split("/").pop() || "";
        if (label.includes("?")) label = label.split("?")[0];
        try { label = decodeURIComponent(label); } catch {}
      } catch {
        label = "";
      }
      // Generic manifest/segment basenames ("master.m3u8") and provider-shell
      // iframe titles ("Viduki.net Api 1") become the real film title so WDM
      // saves "Land of Bad.mp4" instead of "master.ts".
      if (!label || GENERIC_MEDIA_RE.test(label) || isProviderTitle(label)) {
        label = realTitle() || "Video";
      }
    } else if (isProviderTitle(label)) {
      label = realTitle() || label;
    }
    // Server filename (Content-Disposition) is authoritative — it wins over
    // URL basenames and page titles whenever it names a real media file.
    const cdStem = (extra && extra.fileName) ? cdLabelStem(extra.fileName) : "";
    if (cdStem) label = cdStem;

    let quality = (extra && extra.quality) || parseQualityFromUrl(url);
    let resolution = (extra && extra.resolution) || null;
    let size = (extra && extra.size) || null;
    const cdFileName = (extra && extra.fileName) || null;
    const streamInfo = { url, label, type, time: Date.now(), quality: quality || null, resolution: resolution || null, size: size || null, pageTitle: realTitle() || null, fileName: cdFileName, drm: !!(emeDrm && (url.startsWith("blob:") || type === "HLS" || type === "DASH")) };
    detectedStreams.set(url, streamInfo);
    // Tokenized HLS masters hide rendition quality in the URL — probe the
    // playlist once for RESOLUTION so the badge can show "HLS 1080p".
    if (type === "HLS" && !quality) {
      try { probeHlsQuality(url); } catch {}
    }

    // Notify background script to update icon badge & media popup list
    try {
      if (webext && webext.runtime && webext.runtime.sendMessage) {
        webext.runtime.sendMessage({
          action: "mediaDetected",
          stream: streamInfo
        });
      }
    } catch {}

    // Attach overlay only when a video element exists — no corner popup
    const videos = document.querySelectorAll("video");
    if (videos.length > 0) videos.forEach(getOrCreateOverlay);
  }

  // 7. Hook HTML5 <video> elements — deep + shadow DOM aware (IDM scans with MutationObserver subtree).
  // <audio> is intentionally ignored: the floating button lists video only.
  // Element srcs with no recognizable video pattern are sent for background
  // verification instead of being listed blindly.
  function checkElementSrc(src) {
    if (!src || src.startsWith("data:")) return;
    // Same-origin blob: register for the overlay list; bytes move on click.
    if (src.startsWith("blob:")) {
      try { if (new URL(src).origin !== location.origin) return; } catch { return; }
      registerMediaStream(src, "Video", null);
      return;
    }
    if (HLS_RE.test(src) || DASH_RE.test(src) || VIDEO_FILE_RE.test(src)) registerMediaStream(src, null, null);
    else if (!AUDIO_RE.test(src)) sendVerifyMedia(src);
  }
  function queryAllVideos(root) {
    const out = [];
    try { root.querySelectorAll("video").forEach(e => out.push(e)); } catch {}
    // shadow DOM
    try {
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
      let n;
      while ((n = walker.nextNode())) {
        if (n.shadowRoot) {
          try { n.shadowRoot.querySelectorAll("video").forEach(e => out.push(e)); } catch {}
        }
      }
    } catch {}
    return out;
  }
  function observeDomVideos() {
    const noteResolution = (el) => {
      // Live decoded frame size is the ground truth when manifest URLs are
      // tokenized (".../playlist?token=abc" carries no 1080p token).
      try {
        const h = el && el.videoHeight ? el.videoHeight : 0;
        if (h >= 240 && h > liveVideoHeight) liveVideoHeight = h;
      } catch {}
    };
    const inspectElement = (el) => {
      if (el.tagName.toLowerCase() !== "video") return;
      noteResolution(el);
      checkElementSrc(el.currentSrc || el.src);
      // Check child <source> elements (including <video><source>)
      el.querySelectorAll("source").forEach((s) => { if (s.src) checkElementSrc(s.src); });
      getOrCreateOverlay(el);
      el.addEventListener("play", () => {
        noteResolution(el);
        if (el.currentSrc) checkElementSrc(el.currentSrc);
        getOrCreateOverlay(el);
      });
      el.addEventListener("loadedmetadata", () => {
        noteResolution(el);
        if (el.currentSrc) checkElementSrc(el.currentSrc);
        getOrCreateOverlay(el);
      });
      try { el.addEventListener("resize", () => noteResolution(el)); } catch {}
      el.addEventListener("error", () => {
        // Some sites set src after error retry with m3u8
        setTimeout(() => {
          const s2 = el.currentSrc || el.src;
          if (s2) checkElementSrc(s2);
        }, 800);
      });
    };

    queryAllVideos(document).forEach(inspectElement);
    // Scan for dynamically inserted video wrappers (IDM scans with setInterval fallback for canvas players)
    const observer = new MutationObserver((mutations) => {
      for (const m of mutations) {
        for (const n of m.addedNodes) {
          if (n.nodeType !== Node.ELEMENT_NODE) continue;
          if (n.matches && n.matches("video")) inspectElement(n);
          else if (n.querySelectorAll) {
            try { n.querySelectorAll("video").forEach(inspectElement); } catch {}
            // If a container was added that will lazily create video, re-scan shortly
            if (n.matches && n.matches("[class*='player'], [class*='video'], [id*='player']")) {
              setTimeout(() => queryAllVideos(document).forEach(e => { if (!playerOverlays.has(e)) getOrCreateOverlay(e); }), 600);
            }
          }
          if (n.shadowRoot) {
            try { n.shadowRoot.querySelectorAll("video").forEach(inspectElement); } catch {}
          }
        }
      }
    });
    observer.observe(document.documentElement || document.body, { childList: true, subtree: true });
    // Polling fallback for sites that use canvas/WebGL player without <video> until play (like some tubes)
    setInterval(() => {
      queryAllVideos(document).forEach(e => {
        if (!playerOverlays.has(e)) getOrCreateOverlay(e);
        try { if (e.videoHeight >= 240 && e.videoHeight > liveVideoHeight) liveVideoHeight = e.videoHeight; } catch {}
        const src = e.currentSrc || e.src;
        if (src && !detectedStreams.has(src)) checkElementSrc(src);
      });
    }, 2000);
  }

  // Scan a response body (JSON/text) for embedded stream URLs. Covers player
  // APIs such as vidwara POST /api/stream -> {"streaming_url": "...m3u8"}
  // where the request URL itself is never a media URL.
  function scanBodyForStreams(text) {
    if (!text || typeof text !== "string" || text.length > 2_000_000) return;
    // Fast path: skip bodies with no media signal at all.
    if (!BODY_STREAM_RE.test(text)) return;
    // 1) Quoted absolute URLs ending in (or containing) a stream pattern.
    const urlRe = /https?:[^"'\s\\]*?(?:\.m3u8|\.mpd|\.mp4|\.webm|\/playlist|\/manifest|\/master[^\s"'\\]*|\/hls[^\s"'\\]*|\/stream[^\s"'\\]*)(?:\?[^\s"'\\]*)?/gi;
    let m;
    let found = 0;
    while ((m = urlRe.exec(text)) !== null && found < 5) {
      let u = m[0].replace(/\\\//g, "/").replace(/\\u0026/gi, "&");
      if (AUDIO_RE.test(u)) continue;
      if (HLS_RE.test(u)) { registerMediaStream(u, "HLS"); found++; }
      else if (DASH_RE.test(u)) { registerMediaStream(u, "DASH"); found++; }
      else if (VIDEO_FILE_RE.test(u)) { registerMediaStream(u, "Video"); found++; }
    }
    // 2) JSON fields whose value is a relative stream path.
    try {
      const obj = JSON.parse(text);
      const fields = ["streaming_url", "source", "file", "hls", "url", "src", "playback_url"];
      const candidates = [];
      if (obj && typeof obj === "object") {
        for (const f of fields) {
          if (typeof obj[f] === "string") candidates.push(obj[f]);
        }
        if (Array.isArray(obj.sources)) {
          for (const s of obj.sources) {
            if (typeof s === "string") candidates.push(s);
            else if (s && typeof s.file === "string") candidates.push(s.file);
            else if (s && typeof s.src === "string") candidates.push(s.src);
          }
        }
      }
      for (const c of candidates) {
        if (typeof c !== "string") continue;
        if (HLS_RE.test(c) || DASH_RE.test(c) || VIDEO_FILE_RE.test(c)) {
          try { registerMediaStream(new URL(c, location.href).href, null); }
          catch { registerMediaStream(c, null); }
        }
      }
    } catch {}
  }

  // Fallback: hand the player page itself to WDM so the desktop embed
  // resolver can extract the stream (Byse pre-play, blocked embeds, API-only
  // players). Sent as streamType "page" — never as a direct download.
  function sendPageToWdm() {
    try {
      if (webext && webext.runtime && webext.runtime.sendMessage) {
        return requestDownload({
          url: location.href,
          fileName: null,
          referer: location.href,
          headers: { "Referer": location.href, "Origin": location.origin },
          pageTitle: realTitle() || document.title || null,
          streamType: "page",
          pageUrl: location.href
        });
      }
    } catch (e) {
      return Promise.resolve({ success: false, error: (e && e.message) || "send-failed" });
    }
    return Promise.resolve({ success: false, error: "no-messaging" });
  }
  // Manual fallback used when a video element exists but no stream was captured
  // (API-hidden manifest, blob/MSE, encrypted payload). The overlay button offers
  // "Resolve in WDM" and the desktop embed resolver extracts the stream — the
  // user never pastes URLs. Transient "Sent…" feedback avoids double-clicks.
  function flashOverlayLabel(overlay, text, ms) {
    try {
      const label = overlay.querySelector(".wdm-player-overlay-label");
      if (!label) return;
      overlay._wdmFeedbackUntil = Date.now() + (ms || 2000);
      label.textContent = text;
      setTimeout(() => {
        overlay._wdmFeedbackUntil = 0;
        refreshOverlayLabel(overlay);
      }, ms || 2000);
    } catch {}
  }
  function refreshOverlayLabel(overlay) {
    try {
      if (overlay._wdmFeedbackUntil && Date.now() < overlay._wdmFeedbackUntil) return;
      const label = overlay.querySelector(".wdm-player-overlay-label");
      if (!label) return;
      if (detectedStreams.size === 0) { label.textContent = "Resolve in WDM"; return; }
      // Show the peak known rendition on the button ("Download · 1080p").
      let best = 0;
      let bestQ = "";
      try {
        for (const s of detectedStreams.values()) {
          const q = qualityOf(s);
          if (!q) continue;
          const r = q === "4K" ? 5000 : (parseInt(q, 10) || 0);
          if (r > best) { best = r; bestQ = q; }
        }
      } catch {}
      label.textContent = bestQ ? "Download · " + bestQ : "Download";
    } catch {}
  }

  // 8. Hook Network Requests (fetch & XMLHttpRequest)
  function hookNetworkRequests() {
    // Hook fetch()
    const origFetch = window.fetch;
    if (origFetch) {
      window.fetch = async function (...args) {
        let reqUrl = null;
        try {
          const req = args[0];
          reqUrl = typeof req === "string" ? req : req?.url;
          if (reqUrl && !AUDIO_RE.test(reqUrl)) {
            if (HLS_RE.test(reqUrl)) registerMediaStream(reqUrl, "HLS");
            else if (DASH_RE.test(reqUrl)) registerMediaStream(reqUrl, "DASH");
            else if (VIDEO_FILE_RE.test(reqUrl)) registerMediaStream(reqUrl, "Video");
          }
        } catch {}
        const promise = origFetch.apply(this, args);
        // Clone API/player responses and scan bodies for embedded stream URLs.
        // Playlist responses are additionally scanned for EXT-X-KEY so the
        // observed key URL rides along as a verified-fetch hint (B6b).
        try {
          if (reqUrl && (API_BODY_RE.test(reqUrl) || isPlaylistUrl(reqUrl))) {
            promise.then((resp) => {
              try {
                resp.clone().text().then((text) => {
                  try {
                    if (isPlaylistUrl(reqUrl)) scanPlaylistForKey(reqUrl, text);
                    if (API_BODY_RE.test(reqUrl)) scanBodyForStreams(text);
                  } catch {}
                }).catch(() => {});
              } catch {}
              return resp;
            }).catch(() => {});
          }
        } catch {}
        return promise;
      };
    }

    // Hook XMLHttpRequest
    const origOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (method, url, ...rest) {
      try {
        if (typeof url === "string" && !AUDIO_RE.test(url)) {
          if (HLS_RE.test(url)) registerMediaStream(url, "HLS");
          else if (DASH_RE.test(url)) registerMediaStream(url, "DASH");
          else if (VIDEO_FILE_RE.test(url)) registerMediaStream(url, "Video");
          // Scan API/player response bodies on load.
          if (API_BODY_RE.test(url) || isPlaylistUrl(url)) {
            try {
              this.addEventListener("load", function () {
                try {
                  const bodyText = typeof this.responseText === "string" ? this.responseText
                    : (typeof this.response === "string" ? this.response : null);
                  if (typeof bodyText === "string") {
                    if (isPlaylistUrl(url)) scanPlaylistForKey(url, bodyText);
                    if (API_BODY_RE.test(url)) scanBodyForStreams(bodyText);
                  }
                } catch {}
              });
            } catch {}
          }
        }
      } catch {}
      return origOpen.call(this, method, url, ...rest);
    };
  }

  // 1DM logAnchorOrImageHit equivalent: a press on a link/thumbnail whose
  // href/src is direct media captures streams DOM scans never see. Passive —
  // never blocks or hijacks the click.
  function hookMediaClicks() {
    try {
      window.addEventListener("pointerdown", function (e) {
        try {
          const t = e.target && e.target.closest ? e.target.closest("a,img") : null;
          if (!t) return;
          let u = t.href || t.src || "";
          if ((!u || typeof u !== "string") && t.getAttribute) {
            u = t.getAttribute("href") || t.getAttribute("src") || "";
          }
          if (!u || typeof u !== "string" || /^data:/i.test(u)) return;
          if (AUDIO_RE.test(u)) return;
          if (HLS_RE.test(u)) registerMediaStream(u, "HLS");
          else if (DASH_RE.test(u)) registerMediaStream(u, "DASH");
          else if (VIDEO_FILE_RE.test(u)) registerMediaStream(u, "Video");
        } catch {}
      }, { capture: true, passive: true });
    } catch {}
  }

  // Bootstrap
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", () => {
      observeDomVideos();
      hookNetworkRequests();
      hookMediaClicks();
    });
  } else {
    observeDomVideos();
    hookNetworkRequests();
    hookMediaClicks();
  }
})();
