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
const MAX_OBSERVED_BYTES = 64 * 1024 * 1024 * 1024;
function isManifestUrl(u) {
  try { return /\.m3u8(\?|$)/i.test(u) || /\.mpd(\?|$)/i.test(u); } catch { return false; }
}
function isManifestContentType(ct) {
  try {
    const c = String(ct || "").toLowerCase();
    return c.includes("mpegurl") || c.includes("m3u8") || c.includes("dash+xml") || c.includes("mp2t") || c.includes("x-mpegurl");
  } catch { return false; }
}
function parseStrictPositiveInt(raw) {
  try {
    const s = String(raw == null ? "" : raw).trim();
    if (!/^\d+$/.test(s)) return null;
    const n = Number(s);
    if (!Number.isSafeInteger(n) || n <= 0 || n >= MAX_OBSERVED_BYTES) return null;
    return n;
  } catch { return null; }
}
function parseContentRangeTotal(raw) {
  try {
    const s = String(raw == null ? "" : raw).trim();
    let m = /^bytes\s+\d+\s*-\s*\d+\s*\/\s*(\d+)\s*$/i.exec(s);
    if (m) {
      const n = Number(m[1]);
      if (!Number.isSafeInteger(n) || n <= 0 || n >= MAX_OBSERVED_BYTES) return null;
      return n;
    }
    m = /^bytes\s+\*\/\s*(\d+)\s*$/i.exec(s);
    if (m) {
      const n = Number(m[1]);
      if (!Number.isSafeInteger(n) || n <= 0 || n >= MAX_OBSERVED_BYTES) return null;
      return n;
    }
  } catch {}
  return null;
}
function probeSizeFromHeaders(headers, status, contentType, url) {
  // Strict observed-size parser. Manifests never yield a final media size.
  // 206 -> Content-Range total only (never partial Content-Length).
  // 200 -> Content-Length only when valid, positive and in range.
  // Wildcard/malformed/negative/overflowing totals are unknown (null).
  try {
    const ct = contentType != null ? String(contentType) : String(getHeader(headers, "content-type") || "");
    const u = url != null ? String(url) : "";
    if ((u && isManifestUrl(u)) || (ct && isManifestContentType(ct))) return null;
    if (status === 206) {
      return parseContentRangeTotal(getHeader(headers, "content-range"));
    }
    if (status === 200 || status == null) {
      if (status === 200) {
        const ctype = ct.toLowerCase().split(";")[0].trim();
        if (ctype.startsWith("text/html") || ctype === "application/xhtml+xml") return null;
      }
      // Prefer a valid Content-Range total when present (some 200s echo it),
      // otherwise fall back to a strictly-parsed Content-Length.
      const crTotal = parseContentRangeTotal(getHeader(headers, "content-range"));
      if (crTotal) return crTotal;
      return parseStrictPositiveInt(getHeader(headers, "content-length"));
    }
    return null;
  } catch { return null; }
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
  const adopt = (r, status) => {
    // Returns "file" (adopted), "page" (definitive non-file, no fallback),
    // or null (no info — worth the ranged-GET fallback).
    if (!r) return null;
    let ctype = "";
    try { ctype = (r.headers.get("content-type") || "").toLowerCase(); } catch {}
    if (ctype.startsWith("text/html")) return "page"; // a page, not a file
    if (isManifestContentType(ctype) || isManifestUrl(url)) return null; // manifest bytes are not the media size
    const h = plainHeaders(r);
    const st = typeof status === "number" ? status : (r.status === 206 ? 206 : (r.ok ? 200 : null));
    const size = probeSizeFromHeaders(h, st, ctype, url);
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
      if (head && head.ok && adopt(head, head.status) === "page") {
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
        if (range && (range.ok || range.status === 206)) adopt(range, range.status);
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
let blockedLoaded = false;
async function loadBlocked() {
  try {
    const data = await webext.storage.local.get(BLOCK_KEY);
    if (data && data[BLOCK_KEY]) blockedCache = normalizeBlocked(data[BLOCK_KEY]);
  } catch {}
  // Remember that the stored list has been read once: page scripts ask for
  // it on every frame, and each answer must not cost a storage round trip.
  blockedLoaded = true;
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
// auto-catch size gate (minCatchBytes, 0 = catch everything), file-type
// allow/deny lists and the Alt-to-skip preference.
let isWdmActive = false;
let minCatchBytes = 0;
let catchTypes = [];
let skipTypes = [];
let altSkip = true;
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
        if (j && Array.isArray(j.catchTypes)) catchTypes = normalizeTypeList(j.catchTypes);
        if (j && Array.isArray(j.skipTypes)) skipTypes = normalizeTypeList(j.skipTypes);
        if (j && typeof j.altSkip === "boolean") altSkip = j.altSkip;
      } catch {}
      // A durable explicit handoff may have waited for WDM to start. Drain it
      // in the background; overlapping pings are coalesced by the guard.
      drainOutbox().catch(() => {});
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

// Normalizes the desktop-provided extension lists: lowercase, no leading dot,
// bounded and de-duplicated. Empty catchTypes means "every type".
function normalizeTypeList(raw) {
  const out = [];
  try {
    for (const entry of raw || []) {
      const ext = String(entry || "").trim().toLowerCase().replace(/^[*.]/, "");
      if (ext && ext.length <= 24 && out.indexOf(ext) < 0) out.push(ext);
      if (out.length >= 128) break;
    }
  } catch {}
  return out;
}

// Automatic catches only: explicit overlay/page-resource downloads always go
// through, regardless of these rules. Skip wins over allow.
function catchRuleAllows(filename, url) {
  const ext = catchExtOf(filename) || catchExtOf(url);
  if (ext && skipTypes.indexOf(ext) >= 0) return false;
  if (catchTypes.length > 0 && (!ext || catchTypes.indexOf(ext) < 0)) return false;
  return true;
}

// Alt+click bypass (IDM/1DM behavior). The content script reports the click;
// downloads.onCreated consumes the marker. A short any-download fallback covers
// JavaScript download buttons that have no anchor URL.
const ALT_SKIP_MS = 2500;
let altSkipAnyUntil = 0;
const altSkipUrls = new Map();
function markAltSkip(url) {
  if (!altSkip) return;
  const now = Date.now();
  const until = now + ALT_SKIP_MS;
  altSkipAnyUntil = until;
  try {
    if (url && /^https?:\/\//i.test(url)) altSkipUrls.set(url, until);
    if (altSkipUrls.size > 40) {
      for (const [key, exp] of altSkipUrls) {
        if (exp <= now) altSkipUrls.delete(key);
      }
    }
  } catch {}
}
function consumeAltSkip(url) {
  if (!altSkip) return false;
  const now = Date.now();
  try {
    const until = url ? altSkipUrls.get(url) : 0;
    if (until && until > now) {
      altSkipUrls.delete(url);
      altSkipAnyUntil = 0;
      return true;
    }
    if (altSkipAnyUntil > now) {
      altSkipAnyUntil = 0;
      return true;
    }
  } catch {}
  return false;
}

// Durable explicit-handoff spool. Automatic browser catches never enter it:
// if WDM is closed the browser keeps that download. A user's explicit
// "Download with WDM" click, however, is remembered and replayed when the
// desktop app next starts (including after a browser restart).
const OUTBOX_KEY = "wdmHandoffOutbox";
const OUTBOX_MAX = 50;
const OUTBOX_TTL_MS = 7 * 24 * 60 * 60 * 1000;
const OUTBOX_DRAIN_BATCH = 10;
let outboxDraining = false;
let outboxLock = Promise.resolve();

function withOutboxLock(fn) {
  const run = outboxLock.then(fn, fn);
  outboxLock = run.then(() => {}, () => {});
  return run;
}

async function readOutbox() {
  try {
    const data = await webext.storage.local.get(OUTBOX_KEY);
    const list = data && data[OUTBOX_KEY];
    return Array.isArray(list) ? list : [];
  } catch { return []; }
}

async function writeOutbox(list) {
  try {
    await webext.storage.local.set({ [OUTBOX_KEY]: Array.isArray(list) ? list : [] });
    return true;
  } catch { return false; }
}

async function queueHandoff(payload) {
  if (!payload || !payload.url || !/^https?:\/\//i.test(payload.url)) return false;
  return withOutboxLock(async () => {
    const now = Date.now();
    const list = (await readOutbox()).filter(entry =>
      entry && entry.payload && entry.payload.url &&
      entry.payload.url !== payload.url &&
      now - Number(entry.at || 0) < OUTBOX_TTL_MS);
    list.push({ payload, at: now });
    while (list.length > OUTBOX_MAX) list.shift();
    return writeOutbox(list);
  });
}

async function drainOutbox() {
  if (outboxDraining || !isWdmActive) return;
  outboxDraining = true;
  try {
    const list = await readOutbox();
    if (list.length === 0) return;
    const batch = list.slice(0, OUTBOX_DRAIN_BATCH);
    const removeKeys = new Set();
    const keyOf = (entry) => `${entry && entry.payload ? entry.payload.url : ""}|${entry && entry.at ? entry.at : 0}`;
    for (let i = 0; i < batch.length; i++) {
      const entry = batch[i];
      if (!entry || !entry.payload || !entry.payload.url ||
          Date.now() - Number(entry.at || 0) >= OUTBOX_TTL_MS) {
        removeKeys.add(keyOf(entry));
        continue;
      }
      let response;
      try {
        response = await fetchWdmWithRetry(`${WDM_HOST}/download`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(enrichHandoffPayload(entry.payload)),
        });
      } catch {
        break; // keep this and every following entry for the next ping
      }
      if (response.ok) {
        removeKeys.add(keyOf(entry));
        continue;
      }
      // Authentication/token rotation, rate limits and server startup are
      // transient: keep this entry and the rest for the next ping.
      if (response.status === 401 || response.status === 429 || response.status >= 500) break;
      // 400/403/404: the URL or replay data is permanently stale. Drop it
      // instead of blocking the queue forever.
      removeKeys.add(keyOf(entry));
    }
    if (removeKeys.size === 0) return;
    // Re-read under the queue lock: a user may have queued a new handoff while
    // the network calls above were running. Only remove the exact entries this
    // drain processed, never a newer same-URL entry.
    await withOutboxLock(async () => {
      const current = await readOutbox();
      const keep = current.filter(entry => !removeKeys.has(keyOf(entry)));
      await writeOutbox(keep);
    });
  } finally {
    outboxDraining = false;
  }
}

// RPC message handler for content scripts (sniffer, overlay, youtube button)
webext.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || typeof message !== "object") return false;

  if (message.action === "ping") {
    checkWdm().then(() => sendResponse({ active: isWdmActive }));
    return true;
  }

  if (message.action === "altSkip") {
    markAltSkip(typeof message.url === "string" ? message.url : "");
    sendResponse({ received: true });
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
    // A page can ask before the stored list finished loading; answer with the
    // truth rather than an empty list, and only read storage once.
    if (blockedLoaded) sendResponse({ blocked: blockedCache });
    else loadBlocked().then(() => sendResponse({ blocked: blockedCache }));
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
        // Attach the sniffed entry's observed size when the payload lacks one
        // (player overlay handoff). Manifests never contribute a size hint.
        try {
          if (p && p.url && p.observedSizeBytes == null && p.observedSize == null && !isManifestUrl(p.url)) {
            const direct = sanitizeObservedHint(p.size);
            if (direct != null) p.observedSizeBytes = direct;
            else {
              for (const map of tabMediaMap.values()) {
                const e = map && map.get(p.url);
                if (e && e.size) {
                  const h = sanitizeObservedHint(e.size);
                  if (h != null) { p.observedSizeBytes = h; break; }
                }
              }
            }
            if (p.observedSizeBytes != null && isManifestUrl(p.url)) delete p.observedSizeBytes;
            if (p.size != null) delete p.size;
          }
        } catch {}
        enrichHandoffPayload(p);
        // Explicit handoff: if the desktop app is closed, remember the
        // already-enriched payload and replay it on the next /ping success.
        // (Automatic catches deliberately do not queue — the browser keeps
        // those files instead.)
        if (!isWdmActive) {
          await checkWdm();
          if (!isWdmActive) {
            const queued = await queueHandoff(p);
            sendResponse({ success: queued, queued: queued, status: 0 });
            return;
          }
        }
        let r;
        try {
          r = await fetchWdmWithRetry(`${WDM_HOST}/download`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(p)
          });
        } catch (err) {
          const queued = await queueHandoff(p);
          sendResponse({ success: queued, queued: queued, error: queued ? null : "unreachable" });
          return;
        }
        if (r.status === 503) {
          const queued = await queueHandoff(p);
          if (queued) {
            sendResponse({ success: true, queued: true, status: r.status });
            return;
          }
        }
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
    // Existing entry: adopt a valid later header update, converge the
    // background map + content-script entry + visible overlay on the same
    // observed-size state. No duplicate messages when nothing changed;
    // stale events never overwrite newer valid metadata with unknown.
    let changed = false;
    if (!known.quality && extraQuality) { known.quality = extraQuality; changed = true; }
    if (extraSize && (!known.size || known.size !== extraSize)) { known.size = extraSize; changed = true; }
    if (extra && extra.reqHeaders && !known.reqHeaders) { known.reqHeaders = extra.reqHeaders; changed = true; }
    if (extra && typeof extra.drm !== "undefined" && extra.drm && !known.drm) { known.drm = true; changed = true; }
    if (!known.fileName && cdName) {
      known.fileName = cdName;
      const stem = cdLabelStem(cdName);
      if (stem) known.label = stem;
      changed = true;
    }
    if (changed) {
      try { known.time = Date.now(); } catch {}
      sendHintToTab(tabId, { action: "wdmMediaHint", url, hint: known.type, pageTitle: known.pageTitle || null, quality: known.quality || null, size: known.size || null, fileName: known.fileName || null });
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
        // Passive observation only: never issue HEAD/range probes here.
        // Eligible complete 200 direct-media responses adopt a strictly
        // parsed Content-Length; 206 adopts the Content-Range total only.
        // Manifests never produce a final-media size.
        const status = details.statusCode || 0;
        const ctypeRaw = getHeader(details.responseHeaders, "content-type") || "";
        const ctype = ctypeRaw.toLowerCase().split(";")[0].trim();
        const manifest = isManifestUrl(details.url) || isManifestContentType(ctypeRaw);
        let observedSize = null;
        try {
          if (!manifest) {
            if (status === 206) {
              observedSize = probeSizeFromHeaders(details.responseHeaders, 206, ctypeRaw, details.url);
            } else if (status === 200) {
              observedSize = probeSizeFromHeaders(details.responseHeaders, 200, ctypeRaw, details.url);
            }
          }
        } catch {}
        // Pending opaque URLs: confirm via observed content-type (passive, no
        // extra requests, no CORS issues). Video/HLS/DASH confirms, else drop.
        const pend = pendingVerify.get(details.url);
        const cdName = parseCdFileName(getHeader(details.responseHeaders, "content-disposition") || "");
        const authHit = pendingAuth.get(details.url);
        const reqHeaders = authHit && authHit.auth ? { "Authorization": authHit.auth } : null;
        // Manifest entries carry no size hint (playlist bytes are not media).
        const safeSize = manifest ? null : observedSize;
        const hintExtra = (safeSize || cdName || reqHeaders) ? { size: safeSize, fileName: cdName || null, reqHeaders } : null;
        if (pend) {
          pendingVerify.delete(details.url);
          if (pend.tabId === details.tabId) {
            const ctypeFull = (getHeader(details.responseHeaders, "content-type") || "").toLowerCase();
            if (ctypeFull.startsWith("video/") || ctypeFull.includes("mpegurl") || ctypeFull.includes("m3u8") ||
                ctypeFull.includes("dash+xml") || ctypeFull.includes("mp2t")) {
              registerTabMedia(details.tabId, details.url, classifyUrl(details.url) || "Video", hintExtra);
              return;
            }
          }
        }
        if (isMediaResponse(details)) {
          registerTabMedia(details.tabId, details.url, classifyUrl(details.url), hintExtra);
        } else if (safeSize || cdName) {
          // A valid later header update for an already-registered entry must
          // still converge even when the response no longer classifies as
          // new media (e.g. first seen via onBeforeRequest without headers).
          try {
            const map = tabMediaMap.get(details.tabId);
            if (map && map.has(details.url)) {
              registerTabMedia(details.tabId, details.url, (map.get(details.url) || {}).type || classifyUrl(details.url), hintExtra);
            }
          } catch {}
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
  if (consumeAltSkip(downloadUrl)) return;   // Alt+click: browser handles it
  if (!catchRuleAllows(item.filename, downloadUrl)) return; // type rules
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

    await sendToWdm(downloadUrl, item.filename, item.referrer, headers, pageTitle, sanitizeObservedHint(item.totalBytes));

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

function sanitizeObservedHint(v) {
  try {
    const n = Number(v);
    if (!Number.isSafeInteger(n) || n <= 0 || n >= MAX_OBSERVED_BYTES) return null;
    return n;
  } catch { return null; }
}

async function sendToWdm(url, filename, referrer, headers, pageTitle, observedSizeBytes) {
  headers = headers || {};
  // Derive Origin from the referrer when the content script didn't supply one,
  // so HLS/DASH CDNs that gate on Origin still accept the desktop request.
  try {
    if (!headers["Origin"] && !headers["origin"] && referrer && /^https?:\/\//i.test(referrer)) {
      headers["Origin"] = new URL(referrer).origin;
    }
  } catch {}
  const hint = sanitizeObservedHint(observedSizeBytes);
  // Attach a sniffed-media observed size when the caller looked one up.
  let sniffedHint = null;
  try {
    if (hint == null) {
      for (const map of tabMediaMap.values()) {
        const e = map && map.get(url);
        if (e && e.size) { sniffedHint = sanitizeObservedHint(e.size); break; }
      }
    }
  } catch {}
  const finalHint = hint != null ? hint : sniffedHint;
  const payload = {
    url,
    fileName: filename || null,
    referer: referrer || null,
    headers: headers || {},
    pageTitle: pageTitle || null,
    fullSession: pageHostApproved(url, referrer) || undefined
  };
  // Optional hint only: never bytesReceived/fileSize, never a manifest size.
  if (finalHint != null && !isManifestUrl(url)) payload.observedSizeBytes = finalHint;
  const response = await fetchWdmWithRetry(`${WDM_HOST}/download`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(enrichHandoffPayload(payload)),
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
    const sniffSize = sanitizeObservedHint(it.size != null ? it.size : (it.observedSizeBytes != null ? it.observedSizeBytes : null));
    const batchPayload = {
      url: it.url,
      fileName: it.fileName || null,
      referer: it.referer || null,
      headers,
      pageTitle: it.pageTitle || null,
      keyUrl: it.keyUrl || null,
      fullSession: allowPageItem || undefined
    };
    if (sniffSize != null && !isManifestUrl(it.url)) batchPayload.observedSizeBytes = sniffSize;
    enriched.push(enrichHandoffPayload(batchPayload));
  }
  if (enriched.length === 0) throw new Error("No valid URLs in batch");
  let response;
  try {
    response = await fetchWdmWithRetry(`${WDM_HOST}/download/batch`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ items: enriched }),
    });
  } catch (err) {
    // Explicit batch action while WDM is closed: spool each enriched item.
    for (const payload of enriched) await queueHandoff(payload);
    return { queued: enriched.length };
  }
  if (response.status === 503) {
    for (const payload of enriched) await queueHandoff(payload);
    return { queued: enriched.length };
  }
  if (!response.ok) {
    throw new Error(`WDM responded ${response.status}`);
  }
  return response.json();
}

// ---------------------------------------------------------------------------
// Right-click integration (IDM parity).
//
// IDM's extension adds a "Download with ..." entry to the browser's context
// menu plus a per-site switch. Both are explicit user intent, so they bypass
// the automatic-catch filters (size floor, type lists, blocked sites): the
// user picked this exact item.
//
// No popup is declared on purpose - the toolbar icon stays the capture
// toggle, and one-off actions live in the context menu.
const MENU_DOWNLOAD_LINK = "wdm-menu-download-link";
const MENU_DOWNLOAD_MEDIA = "wdm-menu-download-media";
const MENU_SEPARATOR = "wdm-menu-separator";
const MENU_SITE_TOGGLE = "wdm-menu-site-toggle";

function notifyUser(message) {
  try {
    if (!webext.notifications || !webext.notifications.create) return;
    const p = webext.notifications.create({
      type: "basic",
      iconUrl: "icon128.png",
      title: "WDM Download Catcher",
      message: String(message || "").slice(0, 250),
    });
    if (p && typeof p.catch === "function") p.catch(() => {});
  } catch {}
}

// contextMenus.create throws "duplicate id" when a worker rebuilds the menu
// without clearing first, so every build starts from removeAll.
function installContextMenus() {
  try {
    if (!webext.contextMenus || !webext.contextMenus.create) return;
    let built = false;
    const build = () => {
      if (built) return;
      built = true;
      const add = (props) => {
        try {
          const p = webext.contextMenus.create(props);
          if (p && typeof p.catch === "function") p.catch(() => {});
        } catch {}
      };
      add({ id: MENU_DOWNLOAD_LINK, title: "Download link with WDM", contexts: ["link"] });
      add({ id: MENU_DOWNLOAD_MEDIA, title: "Download media with WDM", contexts: ["video", "audio", "image"] });
      add({ id: MENU_SEPARATOR, type: "separator", contexts: ["page", "link", "video", "audio", "image"] });
      add({ id: MENU_SITE_TOGGLE, title: "Disable WDM on this site", contexts: ["page", "link", "video", "audio", "image"] });
    };
    try {
      const res = webext.contextMenus.removeAll(build);
      if (res && typeof res.then === "function") res.then(build, build);
    } catch { build(); }
  } catch {}
}

try {
  if (webext.runtime && webext.runtime.onInstalled) {
    webext.runtime.onInstalled.addListener(() => installContextMenus());
  }
  if (webext.runtime && webext.runtime.onStartup) {
    webext.runtime.onStartup.addListener(() => installContextMenus());
  }
} catch {}
installContextMenus();

function webHostOf(url) {
  try {
    const u = new URL(url);
    if (u.protocol !== "http:" && u.protocol !== "https:") return "";
    return u.hostname.toLowerCase();
  } catch { return ""; }
}

// The label follows the site of the tab the menu was opened on. A stale label
// is harmless: the click reads the stored list and toggles from truth.
async function syncSiteMenu(pageUrl) {
  try {
    if (!webext.contextMenus || !webext.contextMenus.update) return;
    let url = pageUrl;
    if (!url) {
      const tabs = await webext.tabs.query({ active: true, currentWindow: true });
      url = tabs && tabs[0] ? tabs[0].url : "";
    }
    const host = webHostOf(url);
    const off = !!host && blockedHost(host);
    const props = host
      ? { visible: true, title: off ? `Enable WDM on ${host}` : `Disable WDM on ${host}` }
      : { visible: false };
    const p = webext.contextMenus.update(MENU_SITE_TOGGLE, props);
    if (p && typeof p.catch === "function") p.catch(() => {});
  } catch {}
}

async function toggleSiteFor(pageUrl) {
  const host = webHostOf(pageUrl);
  if (!host) return null;
  const data = await webext.storage.local.get(BLOCK_KEY);
  const cur = normalizeBlocked(data && data[BLOCK_KEY]);
  const nowOff = !cur.domains.includes(host);
  cur.domains = nowOff ? cur.domains.concat([host]) : cur.domains.filter(d => d !== host);
  await webext.storage.local.set({ [BLOCK_KEY]: cur });
  blockedCache = cur;
  if (nowOff) {
    purgeBlockedFromMaps(u => {
      try { return blockedHostCheck(new URL(u).hostname, cur.domains); } catch { return false; }
    });
    // Open tabs of that site must stop advertising media they will no longer
    // hand over: tell their page scripts to drop the panel.
    try {
      const tabs = await webext.tabs.query({ url: [`*://${host}/*`] });
      for (const tab of tabs || []) sendHintToTab(tab.id, { action: "wdmSiteOff" });
    } catch {}
  } else {
    try {
      const tabs = await webext.tabs.query({ url: [`*://${host}/*`] });
      for (const tab of tabs || []) sendHintToTab(tab.id, { action: "wdmSiteOn" });
    } catch {}
  }
  await syncSiteMenu(pageUrl);
  notifyUser(nowOff ? `WDM will stay quiet on ${host}` : `WDM is catching downloads on ${host} again`);
  return nowOff;
}

// The sniffer may already know this URL's server name and size; reuse it so a
// right-click handoff shows the same facts an automatic catch would.
function sniffedFor(url) {
  try {
    for (const map of tabMediaMap.values()) {
      const entry = map.get(url);
      if (entry) return { fileName: entry.fileName || null, size: entry.size || null };
    }
  } catch {}
  return null;
}

async function handoffFromMenu(url, pageUrl, tab) {
  const referer = webHostOf(pageUrl) ? pageUrl : "";
  const headers = { "User-Agent": navigator.userAgent };
  try {
    const cookie = await getCookieHeaderForUrl(url, referer, pageHostApproved(url, referer));
    if (cookie) headers["Cookie"] = cookie;
  } catch {}
  if (referer) headers["Referer"] = referer;
  const sniffed = sniffedFor(url);
  const payload = {
    url,
    fileName: (sniffed && sniffed.fileName) || null,
    referer: referer || null,
    headers,
    pageTitle: (tab && tab.title) || null,
    fullSession: pageHostApproved(url, referer) || undefined,
  };
  const hint = sniffed ? sanitizeObservedHint(sniffed.size) : null;
  if (hint != null && !isManifestUrl(url)) payload.observedSizeBytes = hint;

  if (!isWdmActive) await checkWdm();
  if (!isWdmActive) {
    const queued = await queueHandoff(payload);
    notifyUser(queued
      ? "WDM is not running - the download is queued and starts when WDM opens."
      : "WDM is not running. Start WDM and try again.");
    return;
  }
  try {
    const response = await fetchWdmWithRetry(`${WDM_HOST}/download`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(enrichHandoffPayload(payload)),
    });
    if (!response.ok) throw new Error(`WDM responded ${response.status}`);
    notifyUser("Sent to WDM.");
  } catch {
    const queued = await queueHandoff(payload);
    notifyUser(queued
      ? "WDM did not answer - the download is queued and will be retried."
      : "Could not reach WDM.");
  }
}

async function onMenuClicked(info, tab) {
  if (!info) return;
  const pageUrl = info.pageUrl || (tab && tab.url) || "";
  if (info.menuItemId === MENU_SITE_TOGGLE) {
    await toggleSiteFor(pageUrl);
    return;
  }
  let url = "";
  if (info.menuItemId === MENU_DOWNLOAD_LINK) url = info.linkUrl || "";
  else if (info.menuItemId === MENU_DOWNLOAD_MEDIA) url = info.srcUrl || "";
  if (!url || !/^https?:\/\//i.test(url)) return;
  // A link that points at the page already open is the page, not a file.
  if (url === pageUrl) return;
  await handoffFromMenu(url, pageUrl, tab);
}

try {
  if (webext.contextMenus && webext.contextMenus.onClicked) {
    webext.contextMenus.onClicked.addListener((info, tab) => {
      onMenuClicked(info, tab).catch(() => {});
    });
  }
  if (webext.tabs && webext.tabs.onActivated) {
    webext.tabs.onActivated.addListener(() => { syncSiteMenu().catch(() => {}); });
  }
  if (webext.tabs && webext.tabs.onUpdated) {
    webext.tabs.onUpdated.addListener((tabId, changeInfo) => {
      if (changeInfo && (changeInfo.status === "complete" || changeInfo.url)) {
        syncSiteMenu().catch(() => {});
      }
    });
  }
} catch {}

try { loadBlocked().then(() => syncSiteMenu()); } catch {}
syncSiteMenu();