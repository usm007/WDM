// WDM Download Catcher — cross-browser (Chrome MV3 + Firefox MV3).
const webext = typeof browser !== "undefined" ? browser : chrome;
const WDM_HOST = "http://127.0.0.1:17530";

// Re-entrance guard for URLs handed off to WDM
const loopGuard = new Map();
setInterval(() => {
  const now = Date.now();
  for (const [url, exp] of loopGuard.entries()) {
    if (exp <= now) loopGuard.delete(url);
  }
}, 30000);

// Capture on/off, persisted in storage so the toggle survives restarts.
const STORAGE_KEY = "captureEnabled";
let captureEnabled = true;

async function loadCaptureState() {
  try {
    const data = await webext.storage.local.get(STORAGE_KEY);
    captureEnabled = data[STORAGE_KEY] !== false;
    updateBadge();
  } catch {
    captureEnabled = true;
  }
}

function updateBadge() {
  if (!captureEnabled) {
    try { webext.action.setBadgeText({ text: "OFF" }); } catch {}
  } else {
    try { webext.action.setBadgeText({ text: "" }); } catch {}
  }
}

// Map of tabId -> Map of url -> streamInfo
const tabMediaMap = new Map();

function updateBadge(tabId) {
  if (!captureEnabled) {
    try { webext.action.setBadgeText({ text: "OFF", tabId }); } catch {}
    try { webext.action.setBadgeBackgroundColor({ color: "#ef4444", tabId }); } catch {}
    return;
  }
  
  if (typeof tabId === "number" && tabMediaMap.has(tabId)) {
    const count = tabMediaMap.get(tabId).size;
    try {
      webext.action.setBadgeText({ text: count > 0 ? String(count) : "", tabId });
      webext.action.setBadgeBackgroundColor({ color: "#2563eb", tabId });
    } catch {}
  } else {
    try { webext.action.setBadgeText({ text: "" }); } catch {}
  }
}

// Clean up media when tab is closed or navigated
webext.tabs.onRemoved.addListener((tabId) => {
  tabMediaMap.delete(tabId);
});
webext.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.status === "loading") {
    tabMediaMap.delete(tabId);
    updateBadge(tabId);
  }
});
webext.tabs.onActivated.addListener((activeInfo) => {
  updateBadge(activeInfo.tabId);
});

webext.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && changes[STORAGE_KEY]) {
    captureEnabled = changes[STORAGE_KEY].newValue !== false;
    updateBadge();
  }
});
loadCaptureState();

// Periodic ping to verify WDM connection status
let isWdmActive = false;
async function checkWdm() {
  try {
    const res = await fetch(`${WDM_HOST}/ping`, { method: "GET" });
    isWdmActive = res.ok;
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
    const tabId = sender.tab ? sender.tab.id : null;
    if (tabId && message.stream && message.stream.url) {
      if (!tabMediaMap.has(tabId)) {
        tabMediaMap.set(tabId, new Map());
      }
      tabMediaMap.get(tabId).set(message.stream.url, message.stream);
      updateBadge(tabId);
    }
    sendResponse({ success: true });
    return true;
  }

  if (message.action === "getMediaList") {
    const tabId = message.tabId;
    const list = (tabId && tabMediaMap.has(tabId)) 
      ? Array.from(tabMediaMap.get(tabId).values()) 
      : [];
    sendResponse({ media: list, wdmActive: isWdmActive });
    return true;
  }

  if (message.action === "resolve") {
    fetch(`${WDM_HOST}/resolve?url=${encodeURIComponent(message.url)}`)
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
          const cookie = await getCookieHeaderForUrl(p.url, p.referer || p.referer);
          if (cookie) {
            p.headers = p.headers || {};
            if (!p.headers["Cookie"] && !p.headers["cookie"]) p.headers["Cookie"] = cookie;
          }
        } catch {}
        p.headers = p.headers || {};
        if (!p.headers["User-Agent"] && !p.headers["user-agent"]) p.headers["User-Agent"] = navigator.userAgent;
        const r = await fetch(`${WDM_HOST}/download`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
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
// MIME -> extensions map (IDM Aa) subset for detection
const IDM_MIME_MAP = {
  "video/mp4":"MP4|M4V|M4S","video/mpeg":"MPG|MPEG","video/mpg4":"MP4|M4V","video/quicktime":"MOV|QT","video/webm":"WEBM","video/x-flash-video":"FLV","video/x-matroska":"MKV","video/avi":"AVI","video/msvideo":"AVI","video/x-msvideo":"AVI","video/3gpp":"3GP",
  "audio/mp4":"M4A|MP4|M4S","audio/mpeg":"MP3","audio/mp3":"MP3","audio/webm":"WEBM","audio/wav":"WAV","audio/x-wav":"WAV","audio/ogg":"OGG|OPUS",
  "application/dash+xml":"MPD","application/vnd.apple.mpegurl":"M3U8","application/x-mpegurl":"M3U8","application/x-mpegURL":"M3U8","audio/mpegurl":"M3U|M3U8","video/mp2t":"TS|M3U8","application/octet-stream-m3u8":"M3U8",
  "application/x-7z-compressed":"7Z","application/zip":"ZIP","application/x-rar":"RAR","application/x-rar-compressed":"RAR","application/pdf":"PDF","application/octet-stream":"BIN","application/x-msi":"MSI"
};
const IDM_HLS_RE = /(\.m3u8|\/hls\/|[\?&]format=m3u8|mime=.*mpegurl)/i;
const IDM_DASH_RE = /(\.mpd|\/dash\/|[\?&]format=mpd|mime=.*dash)/i;
const IDM_MEDIA_EXTS = /\.(mp4|m4v|m4s|webm|mkv|avi|mov|flv|mpg|mpeg|3gp|3gpp|wmv|asf|ts|m2ts|mp3|m4a|aac|ogg|opus|flac|wav|zip|rar|7z|pdf|exe|msi)(\?|$)/i;

function getHeader(headers, name) {
  if (!headers) return null;
  const n = name.toLowerCase();
  for (const h of headers) if (h.name.toLowerCase() === n) return h.value || null;
  return null;
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
  const status = details.statusCode || 0;
  if (status && status !== 200 && status !== 206 && status !== 304) return false;
  const type = (details.type || "").toLowerCase();
  const headers = details.responseHeaders || [];
  const ctype = (getHeader(headers, "content-type") || "").toLowerCase().split(";")[0].trim();
  const cdisp = (getHeader(headers, "content-disposition") || "").toLowerCase();
  const clen = parseInt(getHeader(headers, "content-length") || "0", 10);
  const ext = getFileExt(url);

  // Filter tiny segments (IDM Hc skips small .ts/.m4s; we also skip small mp3 beacons)
  if (/\.(ts|m4s|mp3|aac)(\?|$)/i.test(url) && clen > 0 && clen < 80_000) return false;

  // 1) URL pattern quick win
  if (IDM_HLS_RE.test(url) || IDM_DASH_RE.test(url)) return true;
  // Media exts only for master manifests or direct files, not segments
  if (IDM_MEDIA_EXTS.test(url) && !/\.(ts|m4s)(\?|$)/i.test(url)) {
    // skip tiny audio beacons detected as .mp3 with small content-length
    if (/\.(mp3|aac|ogg|opus)(\?|$)/i.test(url) && clen > 0 && clen < 120_000) return false;
    return true;
  }
  // 2) Content-Type mapping
  if (ctype) {
    if (ctype.startsWith("video/")) return true;
    if (ctype.startsWith("audio/") && !ctype.includes("audio/mpegurl")) {
      // allow audio but not tiny beacons
      if (type === "media" || type === "xmlhttprequest" || type === "other" || ext) return true;
    }
    if (IDM_MIME_MAP[ctype]) return true;
    if (ctype.includes("mpegurl") || ctype.includes("dash+xml") || ctype.includes("mp2t")) return true;
  }
  // 3) Content-Disposition attachment with media ext
  if (cdisp.includes("attachment")) {
    const m = cdisp.match(/filename[^;=\n]*=(?:[^"]*"([^"]+)"|([^\s;]+))/i);
    const fn = m ? (m[1] || m[2] || "") : "";
    const fext = fn ? (fn.split(".").pop() || "").toUpperCase() : "";
    if (fext && /^(MP4|M4V|M4S|MP3|M4A|FLV|WEBM|MKV|AVI|MOV|MPD|M3U8|ZIP|RAR|7Z|PDF|EXE)$/i.test(fext)) return true;
    if (ext && /^(MP4|M4V|M4S|MP3|M4A|FLV|WEBM|MKV|AVI|MOV|MPD|M3U8)$/i.test(ext)) return true;
  }
  // 4) Segment filtering: very small .ts/.m4s are segments, not master
  if (/\.(ts|m4s)(\?|$)/i.test(url) && clen > 0 && clen < 50_000) return false;
  return false;
}

function classifyUrl(url) {
  if (IDM_HLS_RE.test(url)) return "HLS";
  if (IDM_DASH_RE.test(url)) return "DASH";
  if (IDM_MEDIA_EXTS.test(url)) return "Video";
  return "media";
}

function registerTabMedia(tabId, url, hint) {
  if (!url || !tabId || tabId < 0) return;
  if (isYouTubeUrl(url)) return;
  try { url = new URL(url, "http://dummy").href; } catch {}
  try { if (/^https?:/i.test(url)) url = new URL(url).href; } catch {}
  // de-duplicate generic test beacons + DASH inits
  try {
    const p = new URL(url).pathname.toLowerCase();
    if (/(^|\/)(failure|no_input|open|success)\.mp3$/i.test(p)) return;
    if (/(^|\/)init\.mp4(\?|$)/i.test(p)) return;
  } catch {}
  if (!tabMediaMap.has(tabId)) tabMediaMap.set(tabId, new Map());
  const map = tabMediaMap.get(tabId);
  if (map.has(url)) return;
  let label;
  try { label = new URL(url).pathname.split("/").pop() || ""; } catch { label = url; }
  if (label.includes("?")) label = label.split("?")[0];
  const info = { url, label: label || "Media", type: hint || classifyUrl(url), time: Date.now() };
  map.set(url, info);
  updateBadge(tabId);
  try { webext.tabs.sendMessage(tabId, { action: "wdmMediaHint", url, hint: info.type }).catch(()=>{}); } catch {}
}

// Hook webRequest for media streams (background side, covers workers/CSP bypass)
try {
  if (webext.webRequest && webext.webRequest.onHeadersReceived) {
    webext.webRequest.onHeadersReceived.addListener((details) => {
      try {
        if (isMediaResponse(details)) {
          registerTabMedia(details.tabId, details.url, classifyUrl(details.url));
        }
      } catch {}
    }, { urls: ["<all_urls>"] }, ["responseHeaders"]);
  }
  if (webext.webRequest && webext.webRequest.onBeforeRequest) {
    webext.webRequest.onBeforeRequest.addListener((details) => {
      try {
        const url = details.url || "";
        if (isYouTubeUrl(url)) return;
        if (IDM_HLS_RE.test(url) || IDM_DASH_RE.test(url)) {
          if (details.type === "xmlhttprequest" || details.type === "media" || details.type === "other") {
            registerTabMedia(details.tabId, url, classifyUrl(url));
          }
        } else if (IDM_MEDIA_EXTS.test(url) && !/\.(ts|m4s)(\?|$)/i.test(url)) {
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

// Gather all cookies for a URL's domain, referrer, and parent domains to build a complete "Cookie: ..." header string.
async function getCookieHeaderForUrl(url, referrer) {
  try {
    const cookieMap = new Map();
    const urls = [url];
    if (referrer && /^https?:\/\//i.test(referrer)) {
      urls.push(referrer);
    }
    
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

  if (loopGuard.get(downloadUrl) > Date.now()) {
    loopGuard.delete(downloadUrl);
    return;
  }

  if (!isWdmActive) return;

  loopGuard.set(downloadUrl, Date.now() + 15000);

  try {
    await webext.downloads.cancel(item.id);
    await webext.downloads.erase({ id: item.id }).catch(() => {});
  } catch {}

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
  } catch (err) {
    console.warn("WDM handoff failed:", err);
  }
});

async function sendToWdm(url, filename, referrer, headers, pageTitle) {
  const response = await fetch(`${WDM_HOST}/download`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
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
