// WDM Media Sniffer — IDM-Grade Media Stream Capture & Overlay
// Intercepts HLS (.m3u8), DASH (.mpd), segmented media, and direct streams.
// Injects an IDM-style floating "Download this video" button onto active video players.
// Communicates with background script to sync detected media and send tasks to WDM.

(function () {
  "use strict";

  // Strict domain guard: exclude YouTube (handled by youtube_menu.js + yt-dlp)
  if (location.hostname.includes("youtube.com") || location.hostname.includes("youtu.be")) return;

  const MEDIA_EXTS = /\.(mp4|webm|mkv|avi|mov|flv|m4v|mp3|m4a|aac|ogg|opus|flac|wav)(\?|$)/i;
  const HLS_RE = /(\.m3u8|\/hls\/|[\?&]format=m3u8|[\?&]ext=m3u8|mime=.*mpegurl)/i;
  const DASH_RE = /(\.mpd|\/dash\/|[\?&]format=mpd|[\?&]ext=mpd|mime=.*dash)/i;
  const STREAM_URL_RE = /(\.m3u8|\.mpd|\.mp4|\.webm|\/manifest|\/playlist|\/master\.|\/stream\b)/i;
  const SEGMENT_RE = /\.(ts|m4s|m2ts)(\?|$)/i;

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
  // Bridge MAIN hook -> isolated world + background hint
  window.addEventListener("message", function (e) {
    if (e.source !== window) return;
    const d = e.data;
    if (!d || typeof d !== "object") return;
    if (d.type === "WDM_HOOK_MEDIA" && d.url) registerMediaStream(d.url, d.hint || null);
  });
  // Background webRequest hint (for worker/CSP streams not visible to content)
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
    const payload = {
      url: url,
      fileName: label || null,
      referer: location.href,
      headers: {
        "Referer": location.href,
        "Origin": location.origin
      },
      pageTitle: document.title || null,
      streamType: streamType || "auto"
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
    function renderDropdown() {
      dropdown.innerHTML = "";
      // Dedupe by master base (ignore token query) + hide inits already filtered, but keep fallback
      const seen = new Set();
      const filtered = [];
      for (const s of detectedStreams.values()) {
        try {
          const u = new URL(s.url);
          if (/(^|\/)init\.mp4$/i.test(u.pathname)) continue;
          const base = u.origin + u.pathname.split("/").slice(0, -1).join("/") + "/master";
          if (s.label && s.label.startsWith("master-") && seen.has(base)) continue;
          if (s.label && s.label.startsWith("master-")) seen.add(base);
        } catch {}
        filtered.push(s);
      }
      const streams = filtered.length ? filtered : Array.from(detectedStreams.values());
      if (streams.length === 0) {
        const item = document.createElement("div");
        item.className = "wdm-dropdown-item";
        item.textContent = "Waiting for video stream…";
        dropdown.appendChild(item);
        return;
      }

      // Show at most 5 masters (not every init), title-cased
      const toShow = streams.slice(0, 5);
      for (const s of toShow) {
        const item = document.createElement("div");
        item.className = "wdm-dropdown-item";
        const pretty = s.label && s.label.startsWith("master-") ? "Main video (DASH)" : (s.label || "Video");
        item.innerHTML = `
          <div style="overflow:hidden; text-overflow:ellipsis; white-space:nowrap; max-width:180px;" title="${s.url}">
            ${pretty}
          </div>
          <span class="wdm-dropdown-badge">${s.type}</span>
        `;
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
      if (streams.length === 0) return;
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
      if (isHovered && detectedStreams.size > 0) {
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

  // 6. Record and sync a discovered stream URL
  function registerMediaStream(url, hintType, customLabel) {
    if (!url || typeof url !== "string") return;
    if (url.startsWith("data:") || url.startsWith("blob:http://127.0.0.1") || url.startsWith("blob:http://localhost")) return;
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

    if (detectedStreams.has(url)) return;

    let type = hintType || "media";
    if (HLS_RE.test(url)) type = "HLS";
    else if (DASH_RE.test(url)) type = "DASH";
    else if (MEDIA_EXTS.test(url)) type = "Video";
    else if (SEGMENT_RE.test(url)) type = "Segment";

    let label = customLabel;
    if (!label) {
      try {
        const u = new URL(url);
        label = u.pathname.split("/").pop() || document.title || "Stream";
        if (label.includes("?")) label = label.split("?")[0];
      } catch {
        label = document.title || "Video Stream";
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

  // 7. Hook HTML5 <video> and <audio> elements — deep + shadow DOM aware (IDM scans with MutationObserver subtree)
  function queryAllVideos(root) {
    const out = [];
    try { root.querySelectorAll("video, audio").forEach(e => out.push(e)); } catch {}
    // shadow DOM
    try {
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
      let n;
      while ((n = walker.nextNode())) {
        if (n.shadowRoot) {
          try { n.shadowRoot.querySelectorAll("video, audio").forEach(e => out.push(e)); } catch {}
        }
      }
    } catch {}
    return out;
  }
  function observeDomVideos() {
    const inspectElement = (el) => {
      const src = el.currentSrc || el.src;
      if (src && !src.startsWith("blob:")) registerMediaStream(src, null, null);
      // Check child <source> elements (including <video><source>)
      el.querySelectorAll("source").forEach((s) => { if (s.src) registerMediaStream(s.src, null, null); });
      // Also check poster as hint for some tube sites
      if (el.poster) {
        // poster itself not a stream but confirms video element exists; overlay still created
      }
      if (el.tagName.toLowerCase() === "video") getOrCreateOverlay(el);
      el.addEventListener("play", () => {
        if (el.currentSrc && !el.currentSrc.startsWith("blob:")) registerMediaStream(el.currentSrc, null, null);
        if (el.tagName.toLowerCase() === "video") getOrCreateOverlay(el);
      });
      el.addEventListener("loadedmetadata", () => {
        if (el.currentSrc && !el.currentSrc.startsWith("blob:")) registerMediaStream(el.currentSrc, null, null);
        if (el.tagName.toLowerCase() === "video") getOrCreateOverlay(el);
      });
      el.addEventListener("error", () => {
        // Some sites set src after error retry with m3u8
        setTimeout(() => {
          const s2 = el.currentSrc || el.src;
          if (s2 && !s2.startsWith("blob:")) registerMediaStream(s2, null, null);
        }, 800);
      });
    };

    queryAllVideos(document).forEach(inspectElement);
    // Scan for dynamically inserted video wrappers (IDM scans with setInterval fallback for canvas players)
    const observer = new MutationObserver((mutations) => {
      for (const m of mutations) {
        for (const n of m.addedNodes) {
          if (n.nodeType !== Node.ELEMENT_NODE) continue;
          if (n.matches && n.matches("video, audio")) inspectElement(n);
          else if (n.querySelectorAll) {
            try { n.querySelectorAll("video, audio").forEach(inspectElement); } catch {}
            // If a container was added that will lazily create video, re-scan shortly
            if (n.matches && n.matches("[class*='player'], [class*='video'], [id*='player']")) {
              setTimeout(() => queryAllVideos(document).forEach(e => { if (!playerOverlays.has(e) && e.tagName.toLowerCase()==="video") getOrCreateOverlay(e); }), 600);
            }
          }
          if (n.shadowRoot) {
            try { n.shadowRoot.querySelectorAll("video, audio").forEach(inspectElement); } catch {}
          }
        }
      }
    });
    observer.observe(document.documentElement || document.body, { childList: true, subtree: true });
    // Polling fallback for sites that use canvas/WebGL player without <video> until play (like some tubes)
    setInterval(() => {
      queryAllVideos(document).forEach(e => {
        if (!playerOverlays.has(e) && e.tagName.toLowerCase()==="video") getOrCreateOverlay(e);
        const src = e.currentSrc || e.src;
        if (src && !src.startsWith("blob:") && !detectedStreams.has(src)) registerMediaStream(src, null, null);
      });
    }, 2000);
  }

  // 8. Hook Network Requests (fetch & XMLHttpRequest)
  function hookNetworkRequests() {
    // Hook fetch()
    const origFetch = window.fetch;
    if (origFetch) {
      window.fetch = async function (...args) {
        try {
          const req = args[0];
          const url = typeof req === "string" ? req : req?.url;
          if (url) {
            if (HLS_RE.test(url)) registerMediaStream(url, "HLS");
            else if (DASH_RE.test(url)) registerMediaStream(url, "DASH");
            else if (MEDIA_EXTS.test(url)) registerMediaStream(url, "Video");
            else if (STREAM_URL_RE.test(url)) registerMediaStream(url, "Stream");
          }
        } catch {}
        return origFetch.apply(this, args);
      };
    }

    // Hook XMLHttpRequest
    const origOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (method, url, ...rest) {
      try {
        if (typeof url === "string") {
          if (HLS_RE.test(url)) registerMediaStream(url, "HLS");
          else if (DASH_RE.test(url)) registerMediaStream(url, "DASH");
          else if (MEDIA_EXTS.test(url)) registerMediaStream(url, "Video");
          else if (STREAM_URL_RE.test(url)) registerMediaStream(url, "Stream");
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
