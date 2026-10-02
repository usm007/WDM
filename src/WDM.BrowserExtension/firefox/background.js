// WDM Download Catcher — cross-browser (Chrome MV3 + Firefox MV3).
const webext = typeof browser !== "undefined" ? browser : chrome;
const WDM_HOST = "http://127.0.0.1:17530";

// Loopback auth token (BUG-016 fix): the desktop app writes wdm-token.json next
// to the deployed extension files. Sent as X-WDM-Token on every capture call so
// bare loopback clients cannot drive downloads. Absent during dev (repo copy) —
// calls then rely on the server's extension-Origin migration grace.
let WDM_TOKEN = null;
let WDM_EXT_VERSION = null;
function tokenFileUrl() {
  try {
    if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.getURL)
      return chrome.runtime.getURL("wdm-token.json");
    if (typeof browser !== "undefined" && browser.runtime && browser.runtime.getURL)
      return browser.runtime.getURL("wdm-token.json");
  } catch {}
  return null;
}
// Re-reads the deployed token file. The file can rotate under a long-lived
// worker (reinstall = new server token); without a refresh every capture
// 401s until the extension is reloaded. Returns true when the token changed.
async function refreshWdmToken() {
  try {
    const u = tokenFileUrl();
    if (!u) return false;
    const r = await fetch(u);
    if (!r.ok) return false;
    const j = await r.json();
    if (j && typeof j.token === "string" && j.token.length >= 32) {
      const changed = WDM_TOKEN !== j.token;
      WDM_TOKEN = j.token;
      return changed;
    }
  } catch {}
  return false;
}
try { refreshWdmToken().catch(() => {}); } catch {}
function wdmAuthHeaders(extra) {
  const h = Object.assign({}, extra || {});
  if (WDM_TOKEN) h["X-WDM-Token"] = WDM_TOKEN;
  // Client version for the server's version-gated grace (stale pre-token
  // clients are rejected). Best-effort: never breaks the call when missing.
  try {
    if (!WDM_EXT_VERSION) {
      const m = (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.getManifest)
        ? chrome.runtime.getManifest()
        : (typeof browser !== "undefined" && browser.runtime && browser.runtime.getManifest
          ? browser.runtime.getManifest() : null);
      if (m && typeof m.version === "string") WDM_EXT_VERSION = m.version.slice(0, 32);
    }
    if (WDM_EXT_VERSION) h["X-WDM-ExtVersion"] = WDM_EXT_VERSION;
  } catch {}
  return h;
}
// Active size/filename probe (pre-dialog headers, IDM-style): HEAD first,
// ranged GET bytes=0-0 fallback for servers that refuse HEAD. Short timeout,
// per-URL cached. Background fetch carries the URL domain's cookies
// (credentials:include — the default same-origin mode would omit them
// cross-origin); captured Authorization replays like the download path.
// HTML responses are discarded (probe hit a page, not a file).
const probeCache = new Map(); // url -> { time, size, fileName }
const PROBE_TTL_MS = 10 * 60 * 1000;
const PROBE_TIMEOUT_MS = 6000;
const PROBE_CACHE_MAX = 200;
function probeCacheRead(url) {
  try {
    const e = probeCache.get(url);
    if (e && (Date.now() - e.time) < PROBE_TTL_MS) return e;
    if (e) probeCache.delete(url);
  } catch {}
  return null;
}
function probeCacheWrite(url, entry) {
  try {
    if (probeCache.size >= PROBE_CACHE_MAX) {
      const oldest = probeCache.keys().next().value;
      if (oldest) probeCache.delete(oldest);
    }
    probeCache.set(url, Object.assign({ time: Date.now() }, entry));
  } catch {}
}
function probeSizeFromHeaders(headers) {
  // Content-Range: bytes 0-0/2848208 (206) preferred; else Content-Length.
  try {
    const cr = getHeader(headers, "content-range") || "";
    const m = /\/(\d+)\s*$/.exec(cr);
    if (m) {
      const total = parseInt(m[1], 10);
      if (total > 0 && total < 64 * 1024 * 1024 * 1024) return total;
    }
  } catch {}
  try {
    const cl = parseInt(getHeader(headers, "content-length") || "0", 10);
    if (cl > 0 && cl < 64 * 1024 * 1024 * 1024) return cl;
  } catch {}
  return null;
}
async function fetchWithTimeout(url, options, ms) {
  let timer = null;
  try {
    if (typeof AbortController !== "undefined") {
      const ctrl = new AbortController();
      timer = setTimeout(() => { try { ctrl.abort(); } catch {} }, ms || PROBE_TIMEOUT_MS);
      const res = await fetch(url, Object.assign({}, options || {}, { signal: ctrl.signal }));
      return res;
    }
    return await fetch(url, options);
  } finally {
    try { if (timer) clearTimeout(timer); } catch {}
  }
}
async function probeMediaHeaders(url, reqHeaders) {
  const hit = probeCacheRead(url);
  if (hit) return { size: hit.size, fileName: hit.fileName };
  const out = { size: null, fileName: null };
  const headers = {};
  try {
    const stash = reqHeaders ||
      ((url && pendingAuth.get(url)) ? { "Authorization": pendingAuth.get(url).auth } : null);
    if (stash && stash.Authorization) headers["Authorization"] = stash.Authorization;
  } catch {}
  const plainHeaders = (r) => {
    // webRequest shape ([{name, value}]) so the shared getHeader helper works.
    const arr = [];
    try {
      r.headers.forEach((v, k) => { arr.push({ name: String(k), value: String(v) }); });
    } catch {}
    return arr;
  };
  const adopt = (r) => {
    // Returns "file" (adopted), "page" (definitive non-file, no fallback),
    // or null (no info — worth the ranged-GET fallback).
    if (!r) return null;
    let ctype = "";
    try { ctype = (r.headers.get("content-type") || "").toLowerCase(); } catch {}
    if (ctype.startsWith("text/html")) return "page"; // a page, not a file
    const h = plainHeaders(r);
    const size = probeSizeFromHeaders(h);
    let fileName = null;
    try { fileName = parseCdFileName(getHeader(h, "content-disposition") || ""); } catch {}
    if (size) out.size = size;
    if (fileName) out.fileName = fileName;
    return (size || fileName) ? "file" : null;
  };
  try {
    // HEAD first (header-only, no bytes).
    try {
      const head = await fetchWithTimeout(url, { method: "HEAD", headers, credentials: "include" }, PROBE_TIMEOUT_MS);
      if (head && head.ok && adopt(head) === "page") {
        probeCacheWrite(url, out);
        return out;
      }
    } catch {}
    // Ranged GET fallback (HEAD refused/empty) — 206 gives Content-Range,
    // 200-with-ignored-Range still gives Content-Length.
    if (!out.size && !out.fileName) {
      try {
        const range = await fetchWithTimeout(url,
          { method: "GET", headers: Object.assign({ "Range": "bytes=0-0" }, headers), credentials: "include" },
          PROBE_TIMEOUT_MS);
        if (range && (range.ok || range.status === 206)) adopt(range);
      } catch {}
    }
  } catch {}
  probeCacheWrite(url, out);
  return out;
}
// Loopback fetch with one token-refresh retry on 401: covers the rotate-under-
// worker case above. Only retries when the refresh actually produced a new
// token, so a genuinely wrong token still fails fast after one extra call.
async function fetchWdmWithRetry(url, options) {
  const sendOnce = () => {
    const opt = Object.assign({}, options || {});
    opt.headers = wdmAuthHeaders(opt.headers);
    return fetch(url, opt);
  };
  let res = await sendOnce();
  if (res && res.status === 401) {
    const before = WDM_TOKEN;
    try {
      if (await refreshWdmToken()) {
        if (WDM_TOKEN !== before) res = await sendOnce();
      }
    } catch {}
  }
  return res;
}

// Re-entrance guard for URLs handed off to WDM
const loopGuard = new Map();
// Media found per tab (url -> { url, label, type, time })
const tabMediaMap = new Map();
// Opaque URLs awaiting verification via observed response headers
// (url -> { tabId, time }). Confirmed video only; everything else is dropped.
const pendingVerify = new Map();
// Per-request auth replay (FlowPick onSendHeaders pattern): tokenized CDNs
// gate on Authorization/Bearer headers that cookies.getAll never sees.
// url -> { auth, time }. Capped + TTL'd; merged into download headers.
const pendingAuth = new Map();
// POST-body replay (IDM Bc equivalent): form-gated downloads fail replay
// without the original body. Captured from onBeforeRequest requestBody,
// url -> { bodyB64, contentType, time }. Capped + TTL'd, 256KB body cap;
// merged into download payloads (never headers).
const pendingPost = new Map();
const MAX_POST_BYTES = 256 * 1024;
const POST_CONTENT_ALLOW = /^(application\/x-www-form-urlencoded|multipart\/form-data|application\/octet-stream|text\/plain)/i;
function u8ToB64Post(u8) {
  let s = "";
  const CH = 0x8000;
  for (let i = 0; i < u8.length; i += CH) {
    s += String.fromCharCode.apply(null, u8.subarray(i, i + CH));
  }
  try { return btoa(s); } catch { return null; }
}
// Encodes an onBeforeRequest requestBody into a replayable {bodyB64,
// contentType} pair (null when unusable). Pure function — unit-tested.
function encodePostBody(rb) {
  if (!rb) return null;
  let bytes = null;
  let ct = null;
  if (rb.formData && typeof rb.formData === "object") {
    try {
      const sp = new URLSearchParams();
      for (const k of Object.keys(rb.formData)) {
        const vals = rb.formData[k];
        if (Array.isArray(vals)) { for (const v of vals) sp.append(k, String(v)); }
        else sp.append(k, String(vals));
      }
      const str = sp.toString();
      bytes = new TextEncoder().encode(str);
      ct = "application/x-www-form-urlencoded";
    } catch { return null; }
  } else if (Array.isArray(rb.raw)) {
    // Local file references (upload forms) can't be replayed — skip.
    for (const part of rb.raw) { if (part && part.file) return null; }
    try {
      let total = 0;
      for (const part of rb.raw) {
        if (part && part.bytes) total += part.bytes.byteLength || 0;
      }
      if (total <= 0 || total > MAX_POST_BYTES) return null;
      const merged = new Uint8Array(total);
      let off = 0;
      for (const part of rb.raw) {
        if (part && part.bytes) {
          const v = new Uint8Array(part.bytes);
          merged.set(v, off);
          off += v.length;
        }
      }
      bytes = merged;
    } catch { return null; }
  } else {
    return null;
  }
  if (!bytes || bytes.length === 0 || bytes.length > MAX_POST_BYTES) return null;
  const b64 = u8ToB64Post(bytes);
  if (!b64) return null;
  return { bodyB64: b64, contentType: ct };
}
function prunePost() {
  try {
    const now = Date.now();
    for (const [url, p] of pendingPost.entries()) {
      if (now - p.time > 60000) pendingPost.delete(url);
    }
    while (pendingPost.size > 200) {
      const oldest = pendingPost.keys().next().value;
      if (!oldest) break;
      pendingPost.delete(oldest);
    }
  } catch {}
}
// Browser proxy mirror (IDM proxy.settings equivalent): when the browser
// routes through a proxy/VPN extension, direct desktop requests get refused.
// The effective proxy descriptor rides with download payloads; credentials
// are never read or forwarded.
let browserProxy = null; // { mode, host, port, type } | null
async function refreshBrowserProxy() {
  browserProxy = null;
  try {
    if (!webext.proxy || !webext.proxy.settings || !webext.proxy.settings.get) return;
    const cur = await webext.proxy.settings.get({});
    const v = cur && cur.value;
    if (!v || v.mode === "direct" || v.mode === "system") return;
    if (v.mode === "fixed_servers" && v.rules) {
      const single = v.rules.singleProxy || v.rules.http;
      const socks = v.rules.socks;
      const pick = socks || single;
      if (pick && pick.host && pick.port) {
        browserProxy = {
          mode: "fixed",
          host: String(pick.host),
          port: parseInt(pick.port, 10) || 0,
          type: socks ? ("socks" + (v.rules.socksVersion || 5)) : "http",
        };
        if (!browserProxy.port) browserProxy = null;
      }
    } else if (v.mode === "pac_script" && v.pacScript && v.pacScript.url) {
      browserProxy = { mode: "pac", host: "", port: 0, type: "pac", pacUrl: String(v.pacScript.url).slice(0, 2048) };
    }
  } catch {}
}
function proxyDescriptor() {
  return browserProxy ? Object.assign({}, browserProxy) : null;
}
// Approved full-session-replay hosts (per-site user opt-in, shipped by the
// desktop app as wdm-session-hosts.json next to the token file). Page-host
// cookies merge ONLY for these hosts; everywhere else the default-deny
// (file-host cookies only) holds.
let SESSION_HOSTS = [];
async function loadSessionHosts() {
  try {
    const u = tokenFileUrl();
    if (!u) return;
    const base = u.slice(0, u.lastIndexOf("/") + 1) + "wdm-session-hosts.json";
    const r = await fetch(base);
    if (!r.ok) return;
    const j = await r.json();
    if (j && Array.isArray(j.hosts)) {
      SESSION_HOSTS = j.hosts
        .filter((h) => typeof h === "string")
        .map((h) => h.trim().toLowerCase())
        .filter((h) => h.length > 0 && h.length <= 253);
    }
  } catch {}
}
function pageHostApproved(url, referrer) {
  try {
    if (typeof referrer !== "string" || !/^https?:\/\//i.test(referrer)) return false;
    if (!Array.isArray(SESSION_HOSTS) || SESSION_HOSTS.length === 0) return false;
    const host = new URL(referrer).hostname.toLowerCase();
    return SESSION_HOSTS.some((e) => host === e || host.endsWith("." + e));
  } catch { return false; }
}
try { loadSessionHosts().catch(() => {}); } catch {}
// Merges stashed POST body + proxy descriptor into an outgoing download
// payload (mutates and returns it). Call sites: "download" RPC, direct
// sendToWdm, batch enrichment.
function enrichHandoffPayload(p) {
  try {
    if (p && p.url && !p.postData) {
      const hit = pendingPost.get(p.url);
      if (hit && hit.bodyB64) {
        p.postData = hit.bodyB64;
        p.postContentType = hit.contentType || "application/x-www-form-urlencoded";
      }
    }
  } catch {}
  try {
    if (p && !p.proxy) {
      const d = proxyDescriptor();
      if (d) p.proxy = d;
    }
  } catch {}
  return p;
}
setInterval(() => {
  const now = Date.now();
  for (const [url, exp] of loopGuard.entries()) {
    if (exp <= now) loopGuard.delete(url);
  }
  for (const [url, p] of pendingVerify.entries()) {
    if (now - p.time > 8000) pendingVerify.delete(url);
  }
  for (const [url, p] of pendingAuth.entries()) {
    if (now - p.time > 60000) pendingAuth.delete(url);
  }
  if (pendingAuth.size > 500) {
    const oldest = pendingAuth.keys().next().value;
    if (oldest) pendingAuth.delete(oldest);
  }
  prunePost();
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
    updateActionTitle();
  } catch {
    captureEnabled = true;
  }
}

// Toolbar icon tooltip mirrors the toggle state (the icon IS the switch:
// there is no popup UI).
function updateActionTitle() {
  try {
    if (!webext.action || !webext.action.setTitle) return;
    const p = webext.action.setTitle({
      title: captureEnabled
        ? "WDM Download Catcher - capturing ON (click to pause)"
        : "WDM Download Catcher - capturing OFF (click to resume)"
    });
    if (p && typeof p.catch === "function") p.catch(() => {});
  } catch {}
}

// Icon click toggles download catching (no popup — onClicked only fires
// when no default_popup is declared).
async function toggleCapture() {
  captureEnabled = !captureEnabled;
  try {
    if (webext.storage && webext.storage.local) {
      await webext.storage.local.set({ [STORAGE_KEY]: captureEnabled });
    }
  } catch {}
  updateBadge();
  updateActionTitle();
}

try {
  if (webext.action && webext.action.onClicked) {
    webext.action.onClicked.addListener(() => {
      toggleCapture().catch(() => {});
    });
  }
} catch {}

function updateBadge(tabId) {
  if (!webext.action) return;
  const details = typeof tabId === "number" ? { tabId } : {};
  // chrome.action.* return promises in MV3: a stale tabId (closed/navigated
  // tab) rejects with "No tab with id" — try/catch alone can't catch async
  // rejections, so every call gets an explicit swallow.
  const safe = (p) => { try { if (p && typeof p.catch === "function") p.catch(() => {}); } catch {} };
  if (!captureEnabled) {
    try { safe(webext.action.setBadgeText({ text: "OFF", ...details })); } catch {}
    try { safe(webext.action.setBadgeBackgroundColor({ color: "#ef4444", ...details })); } catch {}
    return;
  }
  try { safe(webext.action.setBadgeText({ text: "", ...details })); } catch {}
}

try {
  if (webext.tabs && webext.tabs.onActivated) {
    webext.tabs.onActivated.addListener((activeInfo) => {
      updateBadge(activeInfo.tabId);
    });
  }
} catch {}

try {
  if (webext.storage && webext.storage.onChanged) {
    webext.storage.onChanged.addListener((changes, area) => {
      if (area === "local" && changes[STORAGE_KEY]) {
        captureEnabled = changes[STORAGE_KEY].newValue !== false;
        updateBadge();
        updateActionTitle();
      }
      if (area === "local" && changes[BLOCK_KEY]) {
        blockedCache = normalizeBlocked(changes[BLOCK_KEY].newValue);
      }
    });
  }
} catch {}
loadCaptureState();
loadBlocked();
try { refreshBrowserProxy().catch(() => {}); } catch {}
try {
  if (webext.proxy && webext.proxy.settings && webext.proxy.settings.onChange) {
    webext.proxy.settings.onChange.addListener(() => { try { refreshBrowserProxy().catch(() => {}); } catch {} });
  }
} catch {}

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

// File types that can never plausibly reach minCatchBytes (images, web
// pages, styles, scripts, fonts). An unknown-size file of one of these
// types stays in the browser — it can't satisfy the gate anyway.
const SMALL_CATCH_EXTS = new Set([
  "jpg", "jpeg", "png", "gif", "webp", "svg", "bmp", "ico", "avif",
  "tif", "tiff", "html", "htm", "xhtml", "shtml", "css",
  "js", "mjs", "cjs", "woff", "woff2", "ttf", "otf", "eot",
]);
function catchExtOf(name) {
  try {
    const base = String(name || "").split(/[?#]/)[0].split("/").pop().split("\\").pop();
    const dot = base.lastIndexOf(".");
    if (dot < 0) return "";
    return base.slice(dot + 1).toLowerCase();
  } catch { return ""; }
}
// True when filename/URL + MIME prove an unknown-size download is a small
// web asset that can never reach the minimum auto-catch size.
function looksSmallByType(filename, url, mime) {
  try {
    const m = String(mime || "").toLowerCase().split(";")[0].trim();
    if (m.startsWith("image/") || m.startsWith("font/")) return true;
    if (m === "text/html" || m === "text/css" ||
        m === "application/xhtml+xml" ||
        m === "text/javascript" || m === "application/javascript" ||
        m === "application/x-javascript") return true;
  } catch {}
  const ext = catchExtOf(filename) || catchExtOf(url);
  return SMALL_CATCH_EXTS.has(ext);
}

// RPC message handler for content scripts (sniffer, overlay, youtube button)
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
          if (s.drm && !e.drm) e.drm = true;
          if (s.reqHeaders && !e.reqHeaders) e.reqHeaders = s.reqHeaders;
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
          ? Array.from(map.values()).map(i => ({ url: i.url, label: i.label, type: i.type, time: i.time, keyUrl: i.keyUrl || null, quality: i.quality || parseQualityFromUrl(i.url) || null, resolution: i.resolution || null, size: i.size || null, sizeText: i.size ? formatBytes(i.size) : null, pageTitle: i.pageTitle || null, fileName: i.fileName || null, drm: !!i.drm, reqHeaders: i.reqHeaders || null }))
          : [];
        sendResponse({ media, wdmActive: isWdmActive });
      } catch (err) {
        sendResponse({ media: [], wdmActive: isWdmActive, error: err && err.message });
      }
    })();
    return true;
  }

  // On-demand header probe for overlay dropdowns: the content script asks for
  // sizes/filenames when it renders options (IDM-style badges). Bounded,
  // cached, never throws — unknown stays unknown.
  if (message.action === "probeSizes") {
    (async () => {
      try {
        const urls = (message.urls || [])
          .filter((u) => typeof u === "string" && /^https?:\/\//i.test(u))
          .slice(0, 8);
        const hints = (message.reqHeaders && typeof message.reqHeaders === "object") ? message.reqHeaders : {};
        const out = {};
        for (const u of urls) {
          try {
            const h = (hints[u] && typeof hints[u] === "object") ? hints[u] : null;
            out[u] = await probeMediaHeaders(u, h);
          } catch { out[u] = { size: null, fileName: null }; }
        }
        sendResponse({ success: true, probes: out });
      } catch (err) {
        sendResponse({ success: false, error: err && err.message });
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
        const r = await fetchWdmWithRetry(`${WDM_HOST}/download/blob-chunk`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
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
        const r = await fetchWdmWithRetry(`${WDM_HOST}/download/mega-sid`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
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
    fetchWdmWithRetry(`${WDM_HOST}/resolve?url=${encodeURIComponent(message.url)}`, { headers: wdmAuthHeaders() })
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
        // Full-session replay merges page-host cookies only for user-approved sites.
        const allowPage = pageHostApproved(p.url, p.referer || p.pageUrl);
        try {
          const cookie = await getCookieHeaderForUrl(p.url, p.referer || p.pageUrl, allowPage);
          if (cookie) {
            p.headers = p.headers || {};
            if (!p.headers["Cookie"] && !p.headers["cookie"]) p.headers["Cookie"] = cookie;
          }
        } catch {}
        if (allowPage) p.fullSession = true;
        p.headers = p.headers || {};
        if (!p.headers["User-Agent"] && !p.headers["user-agent"]) p.headers["User-Agent"] = navigator.userAgent;
        // Replay per-request Authorization captured by onSendHeaders so
        // token-gated CDNs accept the desktop request (cookies alone 403).
        try {
          const stash = (p.reqHeaders) || ((p.url && pendingAuth.get(p.url)) ? { "Authorization": pendingAuth.get(p.url).auth } : null);
          if (stash && stash.Authorization && !p.headers["Authorization"] && !p.headers["authorization"]) {
            p.headers["Authorization"] = stash.Authorization;
          }
        } catch {}
        // Replay the form POST body + browser proxy descriptor when present.
        enrichHandoffPayload(p);
        const r = await fetchWdmWithRetry(`${WDM_HOST}/download`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(p)
        });
        // Report the real outcome (status + server reason) so content-script
        // overlays can show success vs. unreachable vs. rejected instead of
        // assuming the click worked.
        let errText = null;
        if (!r.ok) {
          try {
            const j = await r.json();
            if (j && j.error) errText = String(j.error);
          } catch {}
          if (!errText) errText = `HTTP ${r.status}`;
        }
        sendResponse({ success: r.ok, status: r.status, error: errText });
      } catch (err) {
        sendResponse({ success: false, error: "unreachable" });
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

// Curated noise patterns (defexclist-style; mirrored in media_sniffer.js
// registerMediaStream — keep both lists in sync): player UI sounds,
// telemetry/analytics beacons, auto-updater pings. Deliberately substring,
// deliberately narrow. Safety valve: entries with observed response headers
// (size/CD filename/auth) bypass this entirely at the call site — a wrong
// drop only ever costs a listing, never a download.
const NOISE_URL_RES = [
  /beacon/i,
  /heartbeat/i,
  /keep-?alive/i,
  /telemetry/i,
  /analytics\/collect/i,
  /tracking\/pixel/i,
  /\/pixel(\?|$)/i,
  /impression\./i,
  /player-sound/i,
  /\bui-sound/i,
  /notification-sound/i,
  /alert-sound/i,
  /click-sound/i,
  /hover-sound/i,
  /auto-?update/i,
  /update-?check/i,
  /idmupdt/i,
  /omaha/i,
  /(^|\/)(click|hover|ding|pop|tick|beep|chime)-?[a-z0-9]*\.(mp3|wav|ogg|m4a)(\?|$)/i,
];
function isNoiseUrl(url) {
  try {
    if (!url || typeof url !== "string") return false;
    const u = url.toLowerCase();
    for (const re of NOISE_URL_RES) {
      try { if (re.test(u)) return true; } catch {}
    }
  } catch {}
  return false;
}
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
  // FlowPick-style 206 guard: ranged preview chunks (bytes N-, N>0) are not
  // downloadable items. Only byte-0 206s pass; manifests are 200 anyway.
  if (status === 206) {
    try {
      const cr = getHeader(headers, "content-range") || "";
      const m = /bytes\s+(\d+)-/i.exec(cr);
      if (m && parseInt(m[1], 10) > 0 && !/\.m3u8(\?|$)|\.mpd(\?|$)/i.test(url)) return false;
    } catch {}
  }

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
    m = url.match(/\b(?:\d{3,4})x(\d{3,4})\b/i);
    if (m) {
      const h = parseInt(m[1], 10);
      if (h >= 2160 && /4k/i.test(url)) return "4K";
      if (h >= 240) return h + "p";
    }
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

// Fire-and-forget hint to a tab. The tab may close/navigate between capture
// and delivery, so "No tab with id" rejections are swallowed, never surfaced.
function sendHintToTab(tabId, msg) {
  try {
    if (tabId == null || tabId < 0) return;
    if (!webext.tabs || !webext.tabs.sendMessage) return;
    const p = webext.tabs.sendMessage(tabId, msg);
    if (p && typeof p.catch === "function") p.catch(() => {});
  } catch {}
}

function registerTabMedia(tabId, url, hint, extra) {
  if (!url || !tabId || tabId < 0) return;
  if (isBlockedCapture(url)) return; // user-blocked domain/URL: never listed
  if (isYouTubeUrl(url)) return;
  if (ARCHIVE_EXT_RE.test(url)) return;
  if (IDM_AUDIO_RE.test(url)) return;
  if (/\.(ts|m4s|m2ts)(\?|$)/i.test(url)) return; // Don't track individual stream fragments
  if (/(seg|chunk|segment)[-_0-9]+(\.|\?|$)/i.test(url)) return; // Skip chunk/segment URLs
  // Curated noise (UI sounds, beacons, updaters) — evidence-free entries only.
  // Observed size/CD filename/captured auth always override the pattern list.
  try {
    if ((!extra || (!extra.size && !extra.fileName && !extra.reqHeaders)) && isNoiseUrl(url)) return;
  } catch {}
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
    if (extra && extra.reqHeaders && !known.reqHeaders) known.reqHeaders = extra.reqHeaders;
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
  const info = { url, label: label || "Video", type: kind, time: Date.now(), quality, size, pageTitle: "", fileName: cdName || null, reqHeaders: (extra && extra.reqHeaders) || null, drm: (extra && extra.drm) || false };
  map.set(url, info);
  updateBadge(tabId);
  // Resolve a display title for generic basenames without blocking registration.
  try {
    if (webext.tabs && webext.tabs.get) {
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
          sendHintToTab(tabId, { action: "wdmMediaHint", url, hint: info.type, pageTitle: title, quality: info.quality || null, size: info.size || null, fileName: info.fileName || null });
        } catch {}
      }).catch(() => {}); // tab closed/navigated before the title resolved
    }
  } catch {}
  sendHintToTab(tabId, { action: "wdmMediaHint", url, hint: info.type, quality: quality || null, size: size || null, fileName: info.fileName || null });
}

// Hook webRequest for media streams (background side, covers workers/CSP bypass)
try {
  // Stash per-request Authorization so token-gated CDNs replay on download.
  // Cheap filter: media-ish URLs or pending opaque verifications only.
  if (webext.webRequest && webext.webRequest.onSendHeaders) {
    try {
      webext.webRequest.onSendHeaders.addListener((details) => {
        try {
          const url = details.url || "";
          if (!/^https?:\/\//i.test(url) || isYouTubeUrl(url)) return;
          if (!pendingVerify.has(url) &&
              !IDM_HLS_RE.test(url) && !IDM_DASH_RE.test(url) && !IDM_VIDEO_RE.test(url)) return;
          const hdrs = details.requestHeaders || [];
          let auth = null;
          for (const h of hdrs) {
            try {
              const n = (h.name || "").toLowerCase();
              if (n === "authorization" && h.value) { auth = h.value; break; }
            } catch {}
          }
          if (auth) {
            pendingAuth.set(url, { auth, time: Date.now() });
            if (pendingAuth.size > 500) {
              const oldest = pendingAuth.keys().next().value;
              if (oldest) pendingAuth.delete(oldest);
            }
          }
        } catch {}
      }, { urls: ["<all_urls>"] }, ["requestHeaders"]);
    } catch {}
  }
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
        const authHit = pendingAuth.get(details.url);
        const reqHeaders = authHit && authHit.auth ? { "Authorization": authHit.auth } : null;
        const hintExtra = (observedSize || cdName || reqHeaders) ? { size: observedSize, fileName: cdName || null, reqHeaders } : null;
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
    // POST-body capture for form-gated downloads: the page's form POST
    // precedes the file response, so stash its body for replay at download
    // time. Separate listener (needs "requestBody"): runs for every POST,
    // bounded by the 256KB cap + map hygiene above.
    try {
      webext.webRequest.onBeforeRequest.addListener((details) => {
        try {
          if ((details.method || "").toUpperCase() !== "POST") return;
          const url = details.url || "";
          if (!/^https?:\/\//i.test(url) || isYouTubeUrl(url)) return;
          const rb = details.requestBody;
          if (!rb) return;
          const enc = encodePostBody(rb);
          if (!enc) return;
          pendingPost.set(url, { bodyB64: enc.bodyB64, contentType: enc.contentType, time: Date.now() });
          prunePost();
        } catch {}
      }, { urls: ["<all_urls>"] }, ["requestBody"]);
    } catch {}
    // Content-Type for stashed POST bodies (onBeforeRequest has no headers).
    try {
      webext.webRequest.onSendHeaders.addListener((details) => {
        try {
          const url = details.url || "";
          const entry = pendingPost.get(url);
          if (!entry || entry.contentType) return;
          for (const h of details.requestHeaders || []) {
            try {
              if ((h.name || "").toLowerCase() === "content-type" && h.value) {
                // Keep the full value (multipart boundary included — the bare
                // type alone is a malformed POST); the server allow-lists on
                // the bare type and caps the length.
                const full = String(h.value).trim().slice(0, 512);
                if (POST_CONTENT_ALLOW.test(full.split(";")[0].trim())) entry.contentType = full;
                break;
              }
            } catch {}
          }
        } catch {}
      }, { urls: ["<all_urls>"] }, ["requestHeaders"]);
    } catch {}
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
          drm: !!info.drm,
          reqHeaders: info.reqHeaders || null,
        });
      }
      let tabTitle = "";
      let tabUrl = "";
      try {
        const tab = await webext.tabs.get(tabId);
        tabTitle = tab?.title || "";
        tabUrl = tab?.url || "";
      } catch {
        // Tab closed/navigated: drop its stale entries so the map can't grow
        // with dead-tab media (and no further per-tab calls reject on it).
        try { tabMediaMap.delete(tabId); } catch {}
        continue;
      }
      const payload = JSON.stringify({ tabId: String(tabId), tabTitle, tabUrl, resources });
      fetchWdmWithRetry(WDM_HOST + "/download/page-media-batch", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: payload,
      }).catch(() => {});
    }
  } catch {}
}
setInterval(pushPageMedia, 5000);

// Gather cookies for the download URL's own domain (plus parent domains).
// The referrer/page host merges ONLY under explicit per-site full-session
// approval (pageHostApproved) — default-deny cross-origin cookie replay.
async function getCookieHeaderForUrl(url, referrer, allowPage) {
  try {
    const cookieMap = new Map();
    const urls = [url];
    if (allowPage) {
      try {
        if (typeof referrer === "string" && /^https?:\/\//i.test(referrer)) urls.push(referrer);
      } catch {}
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
  if (isBlockedCapture(downloadUrl)) return; // user-blocked: browser handles it
  // Minimum auto-catch size (Settings): files known-smaller stay in the browser.
  // Unknown sizes can't be judged by bytes, so judge by type instead: small
  // web assets (images, pages, styles, fonts) can never reach the gate.
  if (minCatchBytes > 0) {
    const knownSize = typeof item.fileSize === "number" && item.fileSize > 0;
    if (knownSize) {
      if (item.fileSize < minCatchBytes) return;
    } else if (looksSmallByType(item.filename, downloadUrl, item.mime)) {
      return;
    }
  }

  if (loopGuard.get(downloadUrl) > Date.now()) {
    loopGuard.delete(downloadUrl);
    return;
  }

  // MV3 cold-start race: SW may wake for this event before the initial
  // checkWdm() at load finishes; await it rather than silently dropping.
  if (!isWdmActive) {
    await checkWdm();
    if (!isWdmActive) return;
  }

  loopGuard.set(downloadUrl, Date.now() + 15000);

  // Pause-first race win (IDM pattern): freeze the browser's download BEFORE
  // the async handoff (cookie lookup, tab query) so tiny files can't complete
  // mid-handoff. If pausing fails because the file already completed, the
  // browser won — return without a duplicate handoff. On handoff failure the
  // download is resumed so the browser keeps the file (fallback preserved).
  let paused = false;
  try {
    await webext.downloads.pause(item.id);
    paused = true;
  } catch {
    try {
      const found = await webext.downloads.search({ id: item.id });
      if (found && found[0] && found[0].state === "complete") {
        loopGuard.delete(downloadUrl);
        return;
      }
    } catch {}
  }

  try {
    const headers = {};
    headers["User-Agent"] = navigator.userAgent;
    const allowPageCatch = pageHostApproved(downloadUrl, item.referrer);
    const cookieHeader = await getCookieHeaderForUrl(downloadUrl, item.referrer, allowPageCatch);
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
    try { if (paused) await webext.downloads.resume(item.id); } catch {}
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
  const response = await fetchWdmWithRetry(`${WDM_HOST}/download`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(enrichHandoffPayload({
      url,
      fileName: filename || null,
      referer: referrer || null,
      headers: headers || {},
      pageTitle: pageTitle || null,
      fullSession: pageHostApproved(url, referrer) || undefined
    })),
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
    const allowPageItem = pageHostApproved(it.url, it.referer);
    try {
      const cookie = await getCookieHeaderForUrl(it.url, it.referer, allowPageItem);
      if (cookie && !headers["Cookie"] && !headers["cookie"]) headers["Cookie"] = cookie;
    } catch {}
    if (!headers["User-Agent"] && !headers["user-agent"]) headers["User-Agent"] = navigator.userAgent;
    try {
      const stash = (it.reqHeaders) || ((it.url && pendingAuth.get(it.url)) ? { "Authorization": pendingAuth.get(it.url).auth } : null);
      if (stash && stash.Authorization && !headers["Authorization"] && !headers["authorization"]) {
        headers["Authorization"] = stash.Authorization;
      }
    } catch {}
    try {
      if (!headers["Origin"] && !headers["origin"] && it.referer && /^https?:\/\//i.test(it.referer)) {
        headers["Origin"] = new URL(it.referer).origin;
      }
    } catch {}
    enriched.push(enrichHandoffPayload({
      url: it.url,
      fileName: it.fileName || null,
      referer: it.referer || null,
      headers,
      pageTitle: it.pageTitle || null,
      keyUrl: it.keyUrl || null,
      fullSession: allowPageItem || undefined
    }));
  }
  if (enriched.length === 0) throw new Error("No valid URLs in batch");
  const response = await fetchWdmWithRetry(`${WDM_HOST}/download/batch`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ items: enriched }),
  });
  if (!response.ok) {
    throw new Error(`WDM responded ${response.status}`);
  }
  return response.json();
}
