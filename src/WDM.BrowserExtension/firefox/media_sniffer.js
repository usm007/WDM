// WDM Media Sniffer — IDM-Grade Media Stream Capture & Overlay
// Intercepts HLS (.m3u8), DASH (.mpd), segmented media, and direct streams.
// Injects an IDM-style floating "Download this video" button onto active video players.
// Communicates with background script to sync detected media and send tasks to WDM.

(function () {
  "use strict";

  // Strict domain guard: exclude YouTube (handled by youtube_menu.js + yt-dlp)
  if (location.hostname.includes("youtube.com") || location.hostname.includes("youtu.be")) return;

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
  try {
    const w = typeof browser !== "undefined" ? browser : chrome;
    if (w && w.runtime && w.runtime.onMessage) {
      w.runtime.onMessage.addListener((msg) => {
        if (msg && msg.action === "wdmMediaHint" && msg.url) registerMediaStream(msg.url, msg.hint || null);
      });
    }
  } catch {}
  injectMainHook();

  const detectedStreams = new Map(); // url -> { url, label, type, size, quality, resolution }
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

  // 2. Dispatch download to WDM
  function sendToWdm(url, label, streamType) {
    // Page-local blob: fetch here (only this context can read it) and stream
    // base64 chunks to the desktop app, which imports the bytes as a file.
    if (typeof url === "string" && url.startsWith("blob:")) {
      sendBlobToWdm(url, label).catch((e) => console.warn("[WDM] Blob handoff failed:", e));
      return;
    }
    const payload = {
      url: url,
      fileName: label || null,
      referer: location.href,
      headers: {
        "Referer": location.href,
        "Origin": location.origin
      },
      pageTitle: document.title || null,
      streamType: streamType || "auto",
      keyUrl: playlistKeyHints.get(url) || null
    };

    try {
      if (webext && webext.runtime && webext.runtime.sendMessage) {
        webext.runtime.sendMessage({ action: "download", payload });
      } else {
        window.postMessage({ type: "WDM_DOWNLOAD_REQ", payload }, "*");
      }
    } catch (e) {
      console.warn("[WDM] Error sending download:", e);
    }
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
        top: 12px;
        right: 12px;
        display: inline-flex;
        align-items: center;
        gap: 6px;
        background: rgba(20, 20, 24, 0.88);
        backdrop-filter: blur(10px);
        -webkit-backdrop-filter: blur(10px);
        border: 1px solid rgba(255, 255, 255, 0.15);
        border-radius: 6px;
        padding: 5px 10px;
        color: #ffffff;
        font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
        font-size: 12px;
        font-weight: 500;
        box-shadow: 0 4px 16px rgba(0, 0, 0, 0.4);
        cursor: pointer;
        opacity: 0;
        pointer-events: none;
        transition: opacity 0.2s ease, transform 0.2s ease;
        user-select: none;
        -webkit-user-select: none;
      }
      .wdm-player-overlay.wdm-visible {
        opacity: 1;
        pointer-events: auto;
      }
      .wdm-player-overlay:hover {
        background: rgba(30, 32, 40, 0.96);
        border-color: #3b82f6;
        transform: translateY(-1px);
      }
      .wdm-player-overlay-icon {
        width: 16px;
        height: 16px;
        flex-shrink: 0;
        display: inline-block;
      }
      .wdm-player-overlay-label {
        white-space: nowrap;
        color: #f3f4f6;
      }
      .wdm-player-overlay-close {
        margin-left: 6px;
        width: 18px;
        height: 18px;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        background: rgba(255,255,255,0.10);
        border: none;
        border-radius: 4px;
        color: #e5e7eb;
        font-size: 11px;
        line-height: 1;
        cursor: pointer;
        flex-shrink: 0;
      }
      .wdm-player-overlay-close:hover {
        background: rgba(239,68,68,0.90);
        color: #fff;
      }
      .wdm-player-overlay-dropdown {
        position: absolute;
        top: 100%;
        right: 0;
        margin-top: 4px;
        background: #18181b;
        border: 1px solid #3f3f46;
        border-radius: 6px;
        box-shadow: 0 8px 24px rgba(0,0,0,0.5);
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
        color: #e4e4e7;
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 8px;
        border-bottom: 1px solid rgba(255,255,255,0.06);
        transition: background 0.15s;
        cursor: pointer;
      }
      .wdm-dropdown-item:last-child { border-bottom: none; }
      .wdm-dropdown-item:hover { background: #2563eb; color: #ffffff; }
      .wdm-dropdown-badge {
        font-size: 10px;
        padding: 2px 5px;
        border-radius: 4px;
        background: rgba(255,255,255,0.12);
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

    overlay.innerHTML = `
      ${svgIcon}
      <span class="wdm-player-overlay-label">Download this video</span>
      <span style="font-size:9px; opacity:0.7; margin-left:2px;">▼</span>
      <button class="wdm-player-overlay-close" title="Hide">✕</button>
      <div class="wdm-player-overlay-dropdown"></div>
    `;

    const closeBtn = overlay.querySelector(".wdm-player-overlay-close");
    const dropdown = overlay.querySelector(".wdm-player-overlay-dropdown");
    closeBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      e.preventDefault();
      videoEl._wdmDismissed = true;
      overlay.remove();
      playerOverlays.delete(videoEl);
      try { if (videoEl._wdmRO) videoEl._wdmRO.disconnect(); } catch {}
      try { if (videoEl._wdmIO) videoEl._wdmIO.disconnect(); } catch {}
    });
    // Quality parsed from the URL for rendition labels (".../1080p/...", "movie-720p.mp4").
    function qualityOf(s) {
      try {
        const m = s.url.match(/(\d{3,4})p/i);
        if (m) return m[1] + "p";
        if (/(^|[^a-z])4k([^a-z]|$)/i.test(s.url)) return "4K";
      } catch {}
      return "";
    }
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
          sendPageToWdm();
          flashOverlayLabel(overlay, "Sent page to WDM!");
        });
        dropdown.appendChild(item);
        return;
      }

      // Show the grouped video entries with quality badges
      const toShow = streams.slice(0, 6);
      for (const s of toShow) {
        const item = document.createElement("div");
        item.className = "wdm-dropdown-item";
        const q = qualityOf(s);
        let pretty = (s.label && s.label.startsWith("master-")) ? "Main video (DASH)" : (s.label || "Video");
        let badge = s.type || "Video";
        if (q && !pretty.includes(q)) badge = badge + " " + q;
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
        item.addEventListener("click", (e) => {
          e.stopPropagation();
          dropdown.classList.remove("wdm-open");
          sendToWdm(s.url, s.label, s.type);
          const label = overlay.querySelector(".wdm-player-overlay-label");
          if (label) { label.textContent = "Sent to WDM!"; setTimeout(() => { label.textContent = "Download this video"; }, 2000); }
        });
        dropdown.appendChild(item);
      }
    }

    overlay.addEventListener("click", (e) => {
      if (e.target === closeBtn) return;
      e.stopPropagation();
      e.preventDefault();
      const streams = Array.from(detectedStreams.values());
      // No captured stream yet: hand the player page to WDM for resolving.
      if (streams.length === 0) {
        sendPageToWdm();
        flashOverlayLabel(overlay, "Sent page to WDM!");
        return;
      }
      if (streams.length === 1) {
        sendToWdm(streams[0].url, streams[0].label, streams[0].type);
        const label = overlay.querySelector(".wdm-player-overlay-label");
        if (label) { label.textContent = "Sent to WDM!"; setTimeout(() => { label.textContent = "Download this video"; }, 2000); }
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
        if (cs.objectFit === "cover" && el.videoWidth && el.videoHeight) return false;
      } catch {}
      const r = el.getBoundingClientRect();
      if (r.width < 120 || r.height < 90) return false;
      if (r.bottom <= 0 || r.top >= window.innerHeight || r.right <= 0 || r.left >= window.innerWidth) return false;
      return true;
    }
    function positionOverlay() {
      if (!videoEl.isConnected) {
        overlay.remove();
        playerOverlays.delete(videoEl);
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
      overlay.style.top = Math.max(8, rect.top + 10) + "px";
      overlay.style.right = Math.max(8, window.innerWidth - rect.right + 10) + "px";
      overlay.style.zIndex = "2147483647";
      if (overlay.parentElement !== document.body) document.body.appendChild(overlay);
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
  function registerMediaStream(url, hintType, customLabel) {
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

    // Resolve relative URLs
    try {
      url = new URL(url, location.href).href;
    } catch {
      return;
    }

    if (AUDIO_RE.test(url)) return; // never list audio, even from <video> tags
    if (detectedStreams.has(url)) return;

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
    // Blob URLs carry opaque UUID paths: use the page title, not the UUID.
    if (!label && url.startsWith("blob:")) label = cleanTitleText(document.title) || "Video";
    if (!label) {
      try {
        const u = new URL(url);
        label = u.pathname.split("/").pop() || "";
        if (label.includes("?")) label = label.split("?")[0];
        try { label = decodeURIComponent(label); } catch {}
      } catch {
        label = "";
      }
      // Generic manifest/segment basenames ("master.m3u8") become the page title
      // so WDM saves "My Film.mp4" instead of "master.ts".
      if (!label || GENERIC_MEDIA_RE.test(label)) {
        label = cleanTitleText(document.title) || "Video";
      }
    }

    const streamInfo = { url, label, type, time: Date.now() };
    detectedStreams.set(url, streamInfo);

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
    const inspectElement = (el) => {
      if (el.tagName.toLowerCase() !== "video") return;
      checkElementSrc(el.currentSrc || el.src);
      // Check child <source> elements (including <video><source>)
      el.querySelectorAll("source").forEach((s) => { if (s.src) checkElementSrc(s.src); });
      getOrCreateOverlay(el);
      el.addEventListener("play", () => {
        if (el.currentSrc) checkElementSrc(el.currentSrc);
        getOrCreateOverlay(el);
      });
      el.addEventListener("loadedmetadata", () => {
        if (el.currentSrc) checkElementSrc(el.currentSrc);
        getOrCreateOverlay(el);
      });
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
        webext.runtime.sendMessage({
          action: "download",
          payload: {
            url: location.href,
            fileName: null,
            referer: location.href,
            headers: { "Referer": location.href, "Origin": location.origin },
            pageTitle: document.title || null,
            streamType: "page",
            pageUrl: location.href
          }
        });
      }
    } catch {}
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
      label.textContent = detectedStreams.size > 0 ? "Download this video" : "Resolve in WDM";
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

  // Bootstrap
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", () => {
      observeDomVideos();
      hookNetworkRequests();
    });
  } else {
    observeDomVideos();
    hookNetworkRequests();
  }
})();
