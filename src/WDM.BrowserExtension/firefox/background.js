// WDM Download Catcher — cross-browser (Chrome MV3 + Firefox MV3).
const webext = typeof browser !== "undefined" ? browser : chrome;
const WDM_HOST = "http://127.0.0.1:17530";

// Loopback auth token (BUG-016 fix): the desktop app writes wdm-token.json next
// to the deployed extension files. Sent as X-WDM-Token on every capture call so
// bare loopback clients cannot drive downloads. Absent during dev (repo copy) —
// calls then rely on the server's extension-Origin migration grace.
let WDM_TOKEN = null;
try {
  const tokenUrl = (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.getURL)
    ? chrome.runtime.getURL("wdm-token.json")
    : (typeof browser !== "undefined" && browser.runtime && browser.runtime.getURL
      ? browser.runtime.getURL("wdm-token.json") : null);
  if (tokenUrl) {
    fetch(tokenUrl).then(r => r.ok ? r.json() : null).then(j => {
      if (j && typeof j.token === "string" && j.token.length >= 32) WDM_TOKEN = j.token;
    }).catch(() => {});
  }
} catch {}
function wdmAuthHeaders(extra) {
  const h = Object.assign({}, extra || {});
  if (WDM_TOKEN) h["X-WDM-Token"] = WDM_TOKEN;
  return h;
}

// Re-entrance guard for URLs handed off to WDM
const loopGuard = new Map();
// Media found per tab (url -> { url, label, type, time })
const tabMediaMap = new Map();
// Opaque URLs awaiting verification via observed response headers
// (url -> { tabId, time }). Confirmed video only; everything else is dropped.
const pendingVerify = new Map();
setInterval(() => {
  const now = Date.now();
  for (const [url, exp] of loopGuard.entries()) {
    if (exp <= now) loopGuard.delete(url);
  }
  for (const [url, p] of pendingVerify.entries()) {
    if (now - p.time > 8000) pendingVerify.delete(url);
  }
}, 15000);

// Capture on/off, persisted in storage so the toggle survives restarts.
const STORAGE_KEY = "captureEnabled";
let captureEnabled = true;

// Blocked domains/URLs (1DM PageResource block-domain/block-URL equivalent).
// Applies to AUTOMATIC captures (sniffer, download catcher). Explicit per-item
// "Download" clicks from the popup bypass it (user intent overrides).
const BLOCK_KEY = "wdmBlocked";
let blockedCache = { domains: [], urls: [] };
function normalizeBlocked(b) {
  const out = { domains: [], urls: [] };
  try {
    if (b && Array.isArray(b.domains)) out.domains = b.domains.map(d => String(d).toLowerCase().trim()).filter(Boolean);
    if (b && Array.isArray(b.urls)) out.urls = b.urls.map(u => String(u).trim()).filter(Boolean);
  } catch {}
  return out;
}
async function loadBlocked() {
  try {
    const data = await webext.storage.local.get(BLOCK_KEY);
    if (data && data[BLOCK_KEY]) blockedCache = normalizeBlocked(data[BLOCK_KEY]);
  } catch {}
}
function blockedHostCheck(host, domains) {
  try {
    host = String(host || "").toLowerCase();
    return (domains || []).some(d => host === d || host.endsWith("." + d));
  } catch { return false; }
}
function blockedHost(host) {
  return blockedHostCheck(host, blockedCache.domains);
}
function isBlockedCapture(url) {
  try {
    const u = new URL(url);
    if (blockedCache.urls.includes(u.href)) return true;
    return blockedHost(u.hostname);
  } catch { return false; }
}
function purgeBlockedFromMaps(pred) {
  try {
    for (const [tabId, map] of tabMediaMap.entries()) {
      for (const [url] of map.entries()) {
        try { if (pred(url)) map.delete(url); } catch {}
      }
      try { updateBadge(tabId); } catch {}
    }
  } catch {}
}

async function loadCaptureState() {
  try {
    const data = await webext.storage.local.get(STORAGE_KEY);
    captureEnabled = data[STORAGE_KEY] !== false;
    updateBadge();
  } catch {
    captureEnabled = true;
  }
}

function updateBadge(tabId) {
  const details = typeof tabId === "number" ? { tabId } : {};
  if (!captureEnabled) {
    try { webext.action.setBadgeText({ text: "OFF", ...details }); } catch {}
    try { webext.action.setBadgeBackgroundColor({ color: "#ef4444", ...details }); } catch {}
    return;
  }
  try { webext.action.setBadgeText({ text: "", ...details }); } catch {}
}

webext.tabs.onActivated.addListener((activeInfo) => {
  updateBadge(activeInfo.tabId);
});

webext.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && changes[STORAGE_KEY]) {
    captureEnabled = changes[STORAGE_KEY].newValue !== false;
    updateBadge();
  }
  if (area === "local" && changes[BLOCK_KEY]) {
    blockedCache = normalizeBlocked(changes[BLOCK_KEY].newValue);
  }
});
loadCaptureState();
loadBlocked();

// Periodic ping to verify WDM connection status. Also picks up the
// auto-catch size gate (minCatchBytes, 0 = catch everything).
let isWdmActive = false;
let minCatchBytes = 0;
async function checkWdm() {
  try {
    const res = await fetch(`${WDM_HOST}/ping`, { method: "GET" });
    isWdmActive = res.ok;
    if (res.ok) {
      try {
        const j = await res.json();
        if (j && typeof j.minCatchBytes === "number" && j.minCatchBytes >= 0) {
          minCatchBytes = Math.floor(j.minCatchBytes);
        }
      } catch {}
    }
  } catch {
    isWdmActive = false;
  }
}
checkWdm();
setInterval(checkWdm, 4000);

// RPC message handler for content scripts & popup
webext.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || typeof message !== "object") return false;

  if (message.action === "ping") {
    checkWdm().then(() => sendResponse({ active: isWdmActive }));
    return true;
  }

  if (message.action === "mediaDetected") {
    // Content-script observations ride along: key URL + probed quality/size
    // merge into the tracked entry so the popup + payloads show them (B6b).
    try {
      const s = message.stream;
      if (s && s.url) {
        for (const map of tabMediaMap.values()) {
          const e = map.get(s.url);
          if (!e) continue;
          if (s.keyUrl && !e.keyUrl) e.keyUrl = s.keyUrl;
          if (s.fileName && !e.fileName) e.fileName = s.fileName;
          if (s.quality && !e.quality) e.quality = s.quality;
          if (s.resolution && !e.resolution) e.resolution = s.resolution;
          if (s.size && !e.size) e.size = s.size;
          if (s.label && (e.label === "Video" || GENERIC_MEDIA_RE.test(e.label) || PROVIDER_TITLE_RE.test(e.label)) && !PROVIDER_TITLE_RE.test(s.label)) {
            e.label = s.label;
          }
          if (s.pageTitle && !e.pageTitle) e.pageTitle = s.pageTitle;
        }
        // Content scripts in embed iframes report the real top title they
        // learned via wdmMediaHint — adopt it for sibling entries still stuck
        // on the provider shell.
        if (s.pageTitle && !PROVIDER_TITLE_RE.test(s.pageTitle) && sender && sender.tab && sender.tab.id != null) {
          const map = tabMediaMap.get(sender.tab.id);
          if (map) {
            for (const e of map.values()) {
              if (PROVIDER_TITLE_RE.test(e.label || "")) { e.label = s.pageTitle; e.pageTitle = s.pageTitle; }
              else if (!e.pageTitle) e.pageTitle = s.pageTitle;
            }
          }
        }
      }
    } catch {}
    sendResponse({ success: true });
    return true;
  }

  // Opaque element-src verification: the content script can't read response
  // headers, so background watches the next webRequest for this URL.
  if (message.action === "verifyMedia") {
    try {
      const url = message.url;
      const tabId = sender && sender.tab ? sender.tab.id : null;
      if (url && /^https?:\/\//i.test(url) && tabId != null && tabId >= 0 &&
          !isYouTubeUrl(url) && !IDM_AUDIO_RE.test(url) && !ARCHIVE_EXT_RE.test(url)) {
        if (!pendingVerify.has(url)) pendingVerify.set(url, { tabId, time: Date.now() });
      }
    } catch {}
    sendResponse({ received: true });
    return true;
  }

  if (message.action === "getMediaList") {
    (async () => {
      try {
        const tabs = await webext.tabs.query({ active: true, currentWindow: true });
        const tabId = tabs && tabs[0] ? tabs[0].id : null;
        const map = tabId != null ? tabMediaMap.get(tabId) : null;
        const media = map
          ? Array.from(map.values()).map(i => ({ url: i.url, label: i.label, type: i.type, time: i.time, keyUrl: i.keyUrl || null, quality: i.quality || parseQualityFromUrl(i.url) || null, resolution: i.resolution || null, size: i.size || null, sizeText: i.size ? formatBytes(i.size) : null, pageTitle: i.pageTitle || null, fileName: i.fileName || null }))
          : [];
        sendResponse({ media, wdmActive: isWdmActive });
      } catch (err) {
        sendResponse({ media: [], wdmActive: isWdmActive, error: err && err.message });
      }
    })();
    return true;
  }

  // Block a domain or exact URL from automatic capture (1DM blockResourceDomain/Url).
  if (message.action === "blockDomain" || message.action === "blockUrl") {
    (async () => {
      try {
        const raw = String(message.value || "").trim();
        if (!raw) { sendResponse({ success: false, error: "empty value" }); return; }
        const data = await webext.storage.local.get(BLOCK_KEY);
        const cur = normalizeBlocked(data && data[BLOCK_KEY]);
        if (message.action === "blockDomain") {
          let host = raw;
          try { host = new URL(raw).hostname; } catch {}
          host = host.toLowerCase();
          if (host && !cur.domains.includes(host)) cur.domains.push(host);
          purgeBlockedFromMaps(u => {
            try { return blockedHostCheck(new URL(u).hostname, cur.domains); } catch { return false; }
          });
        } else {
          let href = raw;
          try { href = new URL(raw).href; } catch {}
          if (href && !cur.urls.includes(href)) cur.urls.push(href);
          purgeBlockedFromMaps(u => cur.urls.includes(u));
        }
        await webext.storage.local.set({ [BLOCK_KEY]: cur });
        blockedCache = cur;
        sendResponse({ success: true, blocked: cur });
      } catch (err) {
        sendResponse({ success: false, error: err && err.message });
      }
    })();
    return true;
  }

  if (message.action === "getBlocked") {
    sendResponse({ blocked: blockedCache });
    return true;
  }

  if (message.action === "downloadBatch") {
    (async () => {
      try {
        const result = await sendBatchToWdm(message.items || []);
        sendResponse({ success: true, result });
      } catch (err) {
        sendResponse({ success: false, error: err && err.message });
      }
    })();
    return true;
  }

  // Blob chunk relay: page bytes stream to the desktop assembler in order.
  if (message.action === "blobChunk") {
    (async () => {
      try {
        const r = await fetch(`${WDM_HOST}/download/blob-chunk`, {
          method: "POST",
          headers: wdmAuthHeaders({ "Content-Type": "application/json" }),
          body: JSON.stringify(message.chunk || {})
        });
        let j = null;
        try { j = await r.json(); } catch {}
        sendResponse({ success: r.ok, status: r.status, result: j });
      } catch (err) {
        sendResponse({ success: false, error: err && err.message });
      }
    })();
    return true;
  }

  // MEGA session id forwarding (desktop stores 24h, mega.nz hosts only).
  if (message.action === "megaSid") {
    (async () => {
      try {
        const r = await fetch(`${WDM_HOST}/download/mega-sid`, {
          method: "POST",
          headers: wdmAuthHeaders({ "Content-Type": "application/json" }),
          body: JSON.stringify({ sid: message.sid, host: message.host || null })
        });
        sendResponse({ success: r.ok, status: r.status });
      } catch (err) {
        sendResponse({ success: false, error: err && err.message });
      }
    })();
    return true;
  }

  if (message.action === "resolve") {
    fetch(`${WDM_HOST}/resolve?url=${encodeURIComponent(message.url)}`, { headers: wdmAuthHeaders() })
      .then(r => r.ok ? r.json() : { error: `HTTP ${r.status}` })
      .then(data => sendResponse({ success: true, data }))
      .catch(err => sendResponse({ success: false, error: err.message }));
    return true;
  }

  if (message.action === "download") {
    (async () => {
      try {
        const p = message.payload || {};
        // Enrich with Cookie + User-Agent if not already present (IDM does Cookie replay via V())
        try {
          const cookie = await getCookieHeaderForUrl(p.url, p.referer || p.pageUrl);
          if (cookie) {
            p.headers = p.headers || {};
            if (!p.headers["Cookie"] && !p.headers["cookie"]) p.headers["Cookie"] = cookie;
          }
        } catch {}
        p.headers = p.headers || {};
        if (!p.headers["User-Agent"] && !p.headers["user-agent"]) p.headers["User-Agent"] = navigator.userAgent;
        const r = await fetch(`${WDM_HOST}/download`, {
          method: "POST",
          headers: wdmAuthHeaders({ "Content-Type": "application/json" }),
          body: JSON.stringify(p)
        });
        sendResponse({ success: r.ok });
      } catch (err) {
        sendResponse({ success: false, error: err.message });
      }
    })();
    return true;
  }

  return false;
});

// ============ IDM-grade webRequest pipeline (generic, except youtube) ============
// MIME -> extensions map — only true media types (archives/binaries are handled by download catcher, not media)
const IDM_MIME_MAP = {
  "video/mp4":"MP4|M4V|M4S","video/mpeg":"MPG|MPEG","video/mpg4":"MP4|M4V","video/quicktime":"MOV|QT","video/webm":"WEBM","video/x-flash-video":"FLV","video/x-matroska":"MKV","video/avi":"AVI","video/msvideo":"AVI","video/x-msvideo":"AVI","video/3gpp":"3GP",
  "audio/mp4":"M4A|MP4|M4S","audio/mpeg":"MP3","audio/mp3":"MP3","audio/webm":"WEBM","audio/wav":"WAV","audio/x-wav":"WAV","audio/ogg":"OGG|OPUS",
  "application/dash+xml":"MPD","application/vnd.apple.mpegurl":"M3U8","application/x-mpegurl":"M3U8","application/x-mpegURL":"M3U8","audio/mpegurl":"M3U|M3U8","video/mp2t":"TS|M3U8","application/octet-stream-m3u8":"M3U8"
};
const IDM_HLS_RE = /(\.m3u8|\/hls\/|\/playlist(?=[\/?#]|$)|\/manifest(?=[\/?#]|$)|\/master(?=[\/?#]|$)|\/stream\b|[\?&](format|ext)=m3u8|mime=.*mpegurl)/i;
const IDM_DASH_RE = /(\.mpd|\/dash\/|\/manifest(?=[\/?#]|$)|\/master(?=[\/?#]|$)|[\?&](format|ext)=mpd|mime=.*dash)/i;
// Video-only: the floating button lists downloadable video, never audio.
// (m4s/ts segments, beacons and inits are filtered separately below.)
const IDM_VIDEO_RE = /\.(mp4|m4v|webm|mkv|avi|mov|flv)(\?|$)/i;
const IDM_AUDIO_RE = /\.(mp3|m4a|aac|ogg|opus|flac|wav|wma)(\?|$)/i;
// Archives/binaries must never be treated as media — let downloads.onCreated handle them
const ARCHIVE_EXT_RE = /\.(zip|rar|7z|tar|gz|bz2|xz|pdf|exe|msi|dmg|iso)(\?|$)/i;

function getHeader(headers, name) {
  if (!headers) return null;
  const n = name.toLowerCase();
  for (const h of headers) if (h.name.toLowerCase() === n) return h.value || null;
  return null;
}
// Content-Disposition filename at capture time (1DM CD parse): RFC 2231
// filename*= first, then plain filename=. Only labels the entry — the desktop
// probe re-parses authoritatively at download start.
function parseCdFileName(raw) {
  try {
    if (!raw || typeof raw !== "string") return "";
    let m = raw.match(/filename\*\s*=\s*(?:[^']*'[^']*')?([^;]+)/i);
    if (m && m[1]) {
      const cand = m[1].trim().replace(/^"|"$/g, "");
      try { const d = decodeURIComponent(cand); if (d) return d.slice(0, 180); } catch {}
      if (cand) return cand.slice(0, 180);
    }
    m = raw.match(/filename\s*=\s*"([^"]+)"|filename\s*=\s*([^;\s]+)/i);
    const fn = ((m && (m[1] || m[2])) || "").trim();
    return fn ? fn.slice(0, 180) : "";
  } catch { return ""; }
}
function getFileExt(url) {
  try {
    const p = new URL(url).pathname;
    const m = p.match(/\.([^.\/]+)$/);
    return m ? m[1].toUpperCase() : "";
  } catch { return ""; }
}
function isYouTubeUrl(url) {
  try { const h = new URL(url).hostname.toLowerCase(); return h.includes("youtube.com") || h.includes("youtu.be") || h.includes("youtube-nocookie.com"); } catch { return /youtube\.com|youtu\.be|youtube-nocookie/i.test(url); }
}
function isMediaResponse(details) {
  if (!captureEnabled) return false;
  if (details.tabId < 0) return false; // ignore background
  const url = details.url || "";
  if (!/^https?:\/\//i.test(url)) return false;
  if (url.startsWith(WDM_HOST) || url.startsWith("http://localhost:17530")) return false;
  if (isYouTubeUrl(url)) return false; // youtube handled by yt-dlp, not media catcher
  if (ARCHIVE_EXT_RE.test(url)) return false; // archives/binaries -> download catcher, not media
  const status = details.statusCode || 0;
  if (status && status !== 200 && status !== 206 && status !== 304) return false;
  const type = (details.type || "").toLowerCase();
  const headers = details.responseHeaders || [];
  const ctype = (getHeader(headers, "content-type") || "").toLowerCase().split(";")[0].trim();
  const cdisp = (getHeader(headers, "content-disposition") || "").toLowerCase();
  const clen = parseInt(getHeader(headers, "content-length") || "0", 10);
  const ext = getFileExt(url);

  // Audio is never floating-button media (hard deny beats every other signal).
  if (IDM_AUDIO_RE.test(url)) return false;
  if (ctype.startsWith("audio/") && !ctype.includes("audio/mpegurl")) return false;
  // Filter tiny segments (IDM Hc skips small .ts/.m4s)
  if (/\.(ts|m4s)(\?|$)/i.test(url) && clen > 0 && clen < 80_000) return false;

  // 1) URL pattern quick win — video files and manifests only
  if (IDM_HLS_RE.test(url) || IDM_DASH_RE.test(url)) return true;
  if (IDM_VIDEO_RE.test(url)) return true;
  // 2) Content-Type mapping (video + manifests; audio excluded above)
  if (ctype) {
    if (ctype.startsWith("video/")) return true;
    if (IDM_MIME_MAP[ctype] && !ctype.startsWith("audio/")) return true;
    if (ctype.includes("mpegurl") || ctype.includes("dash+xml") || ctype.includes("mp2t")) return true;
  }
  // 3) Content-Disposition attachment with a video ext
  if (cdisp.includes("attachment")) {
    const m = cdisp.match(/filename[^;=\n]*=(?:[^"]*"([^"]+)"|([^\s;]+))/i);
    const fn = m ? (m[1] || m[2] || "") : "";
    const fext = fn ? (fn.split(".").pop() || "").toUpperCase() : "";
    if (fext && /^(MP4|M4V|WEBM|MKV|AVI|MOV|FLV|MPD|M3U8)$/i.test(fext)) return true;
    if (ext && /^(MP4|M4V|WEBM|MKV|AVI|MOV|FLV|MPD|M3U8)$/i.test(ext)) return true;
  }
  // 4) Segment filtering: very small .ts/.m4s are segments, not master
  if (/\.(ts|m4s)(\?|$)/i.test(url) && clen > 0 && clen < 50_000) return false;
  return false;
}

function classifyUrl(url) {
  if (IDM_AUDIO_RE.test(url)) return null;
  // Explicit extensions and /dash/ vs /hls/ markers first: both manifest
  // regexes share /manifest|/master patterns, so bare path order would lie.
  if (/\.m3u8(\?|$)/i.test(url)) return "HLS";
  if (/\.mpd(\?|$)/i.test(url)) return "DASH";
  if (/\/dash\//i.test(url)) return "DASH";
  if (IDM_HLS_RE.test(url)) return "HLS";
  if (IDM_DASH_RE.test(url)) return "DASH";
  if (IDM_VIDEO_RE.test(url)) return "Video";
  return null;
}

// Basenames without title signal ("master.m3u8") are labeled from the tab
// title downstream so WDM never saves "master.ts".
function cleanTabTitle(title) {
  try {
    let t = (title || "").replace(/\s+/g, " ").trim();
    t = t.replace(/^\s*(watch|now playing)\s*[:\-–—]\s*/i, "");
    t = t.replace(/\s*[-–—|»•]\s*[^-–—|»•]*$/, "");
    t = t.replace(/^\s*(watch|now playing)\s+/i, "");
    if (t.length > 120) t = t.slice(0, 120).trim();
    return t.length >= 4 ? t : "";
  } catch { return ""; }
}
const GENERIC_MEDIA_RE = /^(master|index|playlist|chunklist|manifest|stream|play|video|media|file|download|index-v1-a\d+|seg-?\d*)(\.(m3u8|mpd|mp4|webm|mkv|mov|flv))?$/i;
// Embed-provider tab titles ("Viduki.net Api 1") are shells, not film names —
// kept as-is only when no cleaner title is available.
const PROVIDER_TITLE_RE = /(viduki|vidy\.st|vidfast|vidlink|vidrock|vidzee|voe|filemoon|dood|streamwish|vitacloud|mixdrop|uptostream|\bembed\b|\bplayer\b|\bapi\b)/i;
// Server filename -> label stem ("Film.2024.mkv" -> "Film.2024"). Only real
// media filenames qualify; generic stems fall back to the page title.
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

const API_BODY_RE = /(\/api\/stream|\/api\/videos?\b|\/api\/player|\/player-core|\/api\/streaming)\b/i;

// Broad rendition parse for tokenized player URLs (1080p, /1080/, ?res=720,
// hls-480, itag=22). Returns "" when the URL carries no quality signal.
function parseQualityFromUrl(url) {
  try {
    if (!url || typeof url !== "string") return "";
    let m = url.match(/(\d{3,4})p/i);
    if (m) return (/4k/i.test(url) && m[1] === "2160" ? "4K" : m[1] + "p");
    if (/(^|[^a-z])4k([^a-z]|$)/i.test(url)) return "4K";
    m = url.match(/[/?&=_-](2160|1440|1080|720|480|360|240)(?=[/?&=_\-.#]|$)/);
    if (m) return m[1] + "p";
    m = url.match(/(?:height|res|resolution|quality|q)[=:](2160|1440|1080|720|480|360|240)/i);
    if (m) return m[1] + "p";
    m = url.match(/hls[-_](2160|1440|1080|720|480|360)/i);
    if (m) return m[1] + "p";
  } catch {}
  return "";
}
function formatBytes(n) {
  try {
    n = Number(n);
    if (!(n > 0)) return "";
    const units = ["B", "KB", "MB", "GB"];
    let u = 0;
    while (n >= 1024 && u < units.length - 1) { n /= 1024; u++; }
    return u === 0 ? Math.round(n) + " " + units[u] : n.toFixed(1) + " " + units[u];
  } catch { return ""; }
}

function registerTabMedia(tabId, url, hint, extra) {
  if (!url || !tabId || tabId < 0) return;
  if (isBlockedCapture(url)) return; // user-blocked domain/URL: never listed
  if (isYouTubeUrl(url)) return;
  if (ARCHIVE_EXT_RE.test(url)) return;
  if (IDM_AUDIO_RE.test(url)) return;
  if (/\.(ts|m4s|m2ts)(\?|$)/i.test(url)) return; // Don't track individual stream fragments
  if (/(seg|chunk|segment)[-_0-9]+(\.|\?|$)/i.test(url)) return; // Skip chunk/segment URLs
  // Player API endpoints are body-scanned, never listed (same rule as sniffer).
  try {
    const _u = new URL(url);
    if (API_BODY_RE.test(_u.pathname) &&
        !/\.(m3u8|mpd|mp4|webm|mkv|avi|mov|flv)(\?|$)/i.test(url)) return;
  } catch {}
  try { url = new URL(url, "http://dummy").href; } catch {}
  try { if (/^https?:/i.test(url)) url = new URL(url).href; } catch {}
  // de-duplicate generic test beacons + DASH inits
  try {
    const p = new URL(url).pathname.toLowerCase();
    if (/(^|\/)(failure|no_input|open|success)\.mp3$/i.test(p)) return;
    if (/(^|\/)init\.mp4(\?|$)/i.test(p)) return;
  } catch {}
  const kind = hint === "HLS" || hint === "DASH" || hint === "Video" ? hint : classifyUrl(url);
  if (!kind) return; // unverified opaque URLs stay hidden until verified
  if (!tabMediaMap.has(tabId)) tabMediaMap.set(tabId, new Map());
  const map = tabMediaMap.get(tabId);
  const known = map.get(url);
  const extraQuality = extra && extra.quality ? extra.quality : parseQualityFromUrl(url);
  const extraSize = extra && extra.size ? extra.size : null;
  const cdName = extra && extra.fileName ? String(extra.fileName) : "";
  if (known) {
    if (!known.quality && extraQuality) known.quality = extraQuality;
    if (!known.size && extraSize) known.size = extraSize;
    if (!known.fileName && cdName) {
      known.fileName = cdName;
      const stem = cdLabelStem(cdName);
      if (stem) known.label = stem;
    }
    return;
  }
  if (map.size >= 25) {
    const oldestKey = map.keys().next().value;
    if (oldestKey) map.delete(oldestKey);
  }
  let label;
  try { label = new URL(url).pathname.split("/").pop() || ""; } catch { label = url; }
  if (label.includes("?")) label = label.split("?")[0];
  try { label = decodeURIComponent(label); } catch {}
  // Server filename (Content-Disposition) is authoritative — it wins over URL
  // basenames whenever it names a real media file.
  const cdStem = cdLabelStem(cdName);
  if (cdStem) label = cdStem;
  const quality = extraQuality || null;
  const size = extraSize || null;
  // Real stream name: cleaned top-tab title ("Land of Bad - 1Tube" -> "Land of
  // Bad"); the pageTitle rides along so iframe content scripts can drop their
  // provider-shell title ("Viduki.net Api 1").
  let pageTitle = "";
  const info = { url, label: label || "Video", type: kind, time: Date.now(), quality, size, pageTitle: "", fileName: cdName || null };
  map.set(url, info);
  updateBadge(tabId);
  // Resolve a display title for generic basenames without blocking registration.
  try {
    webext.tabs.get(tabId).then((tab) => {
      try {
        const rawTitle = tab && tab.title ? tab.title : "";
        const title = cleanTabTitle(rawTitle);
        if (!title) return;
        if (map.get(url) !== info) return;
        info.pageTitle = title;
        if (!label || GENERIC_MEDIA_RE.test(label) || PROVIDER_TITLE_RE.test(label)) info.label = title;
        // Forward the real name to every frame's sniffer so the overlay
        // dropdown (often running inside the embed iframe) shows the film,
        // not the provider shell.
        try {
          webext.tabs.sendMessage(tabId, { action: "wdmMediaHint", url, hint: info.type, pageTitle: title, quality: info.quality || null, size: info.size || null, fileName: info.fileName || null }).catch(() => {});
        } catch {}
      } catch {}
    }).catch(() => {
      try { webext.tabs.sendMessage(tabId, { action: "wdmMediaHint", url, hint: info.type, fileName: info.fileName || null }).catch(() => {}); } catch {}
    });
  } catch {}
  try { webext.tabs.sendMessage(tabId, { action: "wdmMediaHint", url, hint: info.type, quality: quality || null, size: size || null, fileName: info.fileName || null }).catch(()=>{}); } catch {}
}

// Hook webRequest for media streams (background side, covers workers/CSP bypass)
try {
  if (webext.webRequest && webext.webRequest.onHeadersReceived) {
    webext.webRequest.onHeadersReceived.addListener((details) => {
      try {
        // Observed Content-Length rides along as the size hint (direct mp4s
        // show "MP4 720p • 1.2 GB" instead of a bare "VIDEO").
        let observedSize = null;
        try {
          const cl = parseInt(getHeader(details.responseHeaders, "content-length") || "0", 10);
          if (cl > 0 && cl < 32 * 1024 * 1024 * 1024) observedSize = cl;
        } catch {}
        // Pending opaque URLs: confirm via observed content-type (passive, no
        // extra requests, no CORS issues). Video/HLS/DASH confirms, else drop.
        const pend = pendingVerify.get(details.url);
        const cdName = parseCdFileName(getHeader(details.responseHeaders, "content-disposition") || "");
        const hintExtra = (observedSize || cdName) ? { size: observedSize, fileName: cdName || null } : null;
        if (pend) {
          pendingVerify.delete(details.url);
          if (pend.tabId === details.tabId) {
            const ctype = (getHeader(details.responseHeaders, "content-type") || "").toLowerCase();
            if (ctype.startsWith("video/") || ctype.includes("mpegurl") || ctype.includes("m3u8") ||
                ctype.includes("dash+xml") || ctype.includes("mp2t")) {
              registerTabMedia(details.tabId, details.url, classifyUrl(details.url) || "Video", hintExtra);
              return;
            }
          }
        }
        if (isMediaResponse(details)) {
          registerTabMedia(details.tabId, details.url, classifyUrl(details.url), hintExtra);
        }
      } catch {}
    }, { urls: ["<all_urls>"] }, ["responseHeaders"]);
  }
  if (webext.webRequest && webext.webRequest.onBeforeRequest) {
    webext.webRequest.onBeforeRequest.addListener((details) => {
      try {
        const url = details.url || "";
        if (isYouTubeUrl(url)) return;
        if (IDM_AUDIO_RE.test(url)) return;
        if (IDM_HLS_RE.test(url) || IDM_DASH_RE.test(url) || IDM_VIDEO_RE.test(url)) {
          if (details.type === "xmlhttprequest" || details.type === "media" || details.type === "other") {
            registerTabMedia(details.tabId, url, classifyUrl(url));
          }
        }
      } catch {}
    }, { urls: ["<all_urls>"] });
  }
  // Clean on navigation
  if (webext.webNavigation && webext.webNavigation.onCommitted) {
    webext.webNavigation.onCommitted.addListener((details) => {
      if (details.frameId === 0) {
        tabMediaMap.delete(details.tabId);
        updateBadge(details.tabId);
      }
    });
  }
} catch {}

// ── Page Resources push (P1a): push tabMediaMap to the desktop app ──────
async function pushPageMedia() {
  try {
    for (const [tabId, map] of tabMediaMap.entries()) {
      if (!map || map.size === 0) continue;
      const resources = [];
      for (const [url, info] of map.entries()) {
        resources.push({
          url: info.url || url,
          label: info.label || "",
          type: info.type || "",
          size: info.size || null,
          quality: info.quality || null,
          pageTitle: info.pageTitle || "",
          time: info.time || 0,
        });
      }
      let tabTitle = "";
      let tabUrl = "";
      try {
        const tab = await webext.tabs.get(tabId);
        tabTitle = tab?.title || "";
        tabUrl = tab?.url || "";
      } catch {}
      const payload = JSON.stringify({ tabId: String(tabId), tabTitle, tabUrl, resources });
      fetch(WDM_HOST + "/download/page-media-batch", {
        method: "POST",
        headers: Object.assign({ "Content-Type": "application/json" }, WDM_TOKEN ? { "X-WDM-Token": WDM_TOKEN } : {}),
        body: payload,
      }).catch(() => {});
    }
  } catch {}
}
setInterval(pushPageMedia, 5000);

// Gather cookies for the download URL's own domain (plus parent domains).
// The referrer/page host is deliberately NOT merged: page-session cookies
// must never ride along to a third-party CDN (cross-origin cookie replay).
async function getCookieHeaderForUrl(url, referrer) {
  void referrer; // kept for call-site compatibility; intentionally unused
  try {
    const cookieMap = new Map();
    const urls = [url];
    
    for (const u of urls) {
      try {
        const cookies = await webext.cookies.getAll({ url: u });
        if (cookies) {
          for (const c of cookies) {
            cookieMap.set(c.name, c.value);
          }
        }
      } catch {}

      try {
        const parsed = new URL(u);
        const hostParts = parsed.hostname.split(".");
        while (hostParts.length >= 2) {
          const domain = hostParts.join(".");
          const domainCookies = await webext.cookies.getAll({ domain });
          if (domainCookies) {
            for (const c of domainCookies) {
              cookieMap.set(c.name, c.value);
            }
          }
          hostParts.shift();
        }
      } catch {}
    }

    if (cookieMap.size === 0) return null;
    return Array.from(cookieMap.entries()).map(([k, v]) => `${k}=${v}`).join("; ");
  } catch {
    return null;
  }
}

webext.downloads.onCreated.addListener(async (item) => {
  if (!captureEnabled) return;
  if (item.state && item.state !== "in_progress") return;
  if (item.startTime && (Date.now() - new Date(item.startTime).getTime()) > 10000) return;

  const downloadUrl = item.finalUrl || item.url;
  if (!downloadUrl || !/^https?:\/\//i.test(downloadUrl)) return;
  if (isBlockedCapture(downloadUrl)) return; // user-blocked: browser handles it
  // Minimum auto-catch size (Settings): files known-smaller stay in the browser.
  // Unknown sizes (fileSize <= 0) always pass — they can't be judged yet.
  if (minCatchBytes > 0 && typeof item.fileSize === "number" &&
      item.fileSize > 0 && item.fileSize < minCatchBytes) return;

  if (loopGuard.get(downloadUrl) > Date.now()) {
    loopGuard.delete(downloadUrl);
    return;
  }

  // Cold-start race: SW may wake for this event before the initial
  // checkWdm() at load finishes; await it rather than silently dropping.
  if (!isWdmActive) {
    await checkWdm();
    if (!isWdmActive) return;
  }

  loopGuard.set(downloadUrl, Date.now() + 15000);

  try {
    const headers = {};
    headers["User-Agent"] = navigator.userAgent;
    const cookieHeader = await getCookieHeaderForUrl(downloadUrl, item.referrer);
    if (cookieHeader) headers["Cookie"] = cookieHeader;
    if (item.referrer) headers["Referer"] = item.referrer;

    let pageTitle = "";
    try {
      const tabs = await webext.tabs.query({ active: true, currentWindow: true });
      if (tabs && tabs[0] && tabs[0].title) {
        pageTitle = tabs[0].title;
      }
    } catch {}

    await sendToWdm(downloadUrl, item.filename, item.referrer, headers, pageTitle);

    try {
      await webext.downloads.cancel(item.id);
      await webext.downloads.erase({ id: item.id }).catch(() => {});
    } catch {}
  } catch (err) {
    loopGuard.delete(downloadUrl);
    console.warn("WDM handoff failed:", err);
  }
});

async function sendToWdm(url, filename, referrer, headers, pageTitle) {
  headers = headers || {};
  // Derive Origin from the referrer when the content script didn't supply one,
  // so HLS/DASH CDNs that gate on Origin still accept the desktop request.
  try {
    if (!headers["Origin"] && !headers["origin"] && referrer && /^https?:\/\//i.test(referrer)) {
      headers["Origin"] = new URL(referrer).origin;
    }
  } catch {}
  const response = await fetch(`${WDM_HOST}/download`, {
    method: "POST",
    headers: wdmAuthHeaders({ "Content-Type": "application/json" }),
    body: JSON.stringify({
      url,
      fileName: filename || null,
      referer: referrer || null,
      headers: headers || {},
      pageTitle: pageTitle || null
    }),
  });
  if (!response.ok) {
    throw new Error(`WDM responded ${response.status}`);
  }
}

// Batch handoff for the page-resource list / "download all" (server cap: 50).
async function sendBatchToWdm(items) {
  const list = (items || []).slice(0, 50);
  const enriched = [];
  for (const it of list) {
    if (!it || !it.url || !/^https?:\/\//i.test(it.url)) continue;
    const headers = Object.assign({}, it.headers || {});
    try {
      const cookie = await getCookieHeaderForUrl(it.url, it.referer);
      if (cookie && !headers["Cookie"] && !headers["cookie"]) headers["Cookie"] = cookie;
    } catch {}
    if (!headers["User-Agent"] && !headers["user-agent"]) headers["User-Agent"] = navigator.userAgent;
    try {
      if (!headers["Origin"] && !headers["origin"] && it.referer && /^https?:\/\//i.test(it.referer)) {
        headers["Origin"] = new URL(it.referer).origin;
      }
    } catch {}
    enriched.push({
      url: it.url,
      fileName: it.fileName || null,
      referer: it.referer || null,
      headers,
      pageTitle: it.pageTitle || null,
      keyUrl: it.keyUrl || null
    });
  }
  if (enriched.length === 0) throw new Error("No valid URLs in batch");
  const response = await fetch(`${WDM_HOST}/download/batch`, {
    method: "POST",
    headers: wdmAuthHeaders({ "Content-Type": "application/json" }),
    body: JSON.stringify({ items: enriched }),
  });
  if (!response.ok) {
    throw new Error(`WDM responded ${response.status}`);
  }
  return response.json();
}
