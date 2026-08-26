// WDM YouTube Quality Bar — Content Script
// Injects a premium download panel below the YouTube video player.
// Fetches quality tiers from WDM's /resolve endpoint (powered by yt-dlp).

(function () {
  "use strict";

  if (!location.hostname.includes("youtube.com")) return;

  const WDM_HOST = "http://127.0.0.1:17530";
  const PANEL_ID = "wdm-yt-panel";

  let currentVideoId = null;
  let isResolving = false;
  let wdmActive = false;

  // --- WDM connectivity ---
  async function checkWdm() {
    try {
      const r = await fetch(`${WDM_HOST}/ping`, { method: "GET" });
      wdmActive = r.ok;
    } catch {
      wdmActive = false;
    }
    return wdmActive;
  }
  checkWdm();
  setInterval(checkWdm, 5000);

  // --- Extract current video ID ---
  function getVideoId() {
    const p = new URLSearchParams(location.search);
    return p.get("v") || null;
  }

  // --- Send a download request to WDM ---
  async function downloadVia(videoId, formatArg, label) {
    const url = `https://www.youtube.com/watch?v=${videoId}`;
    try {
      const r = await fetch(`${WDM_HOST}/download`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          url,
          fileName: null,
          referer: location.href,
          headers: { Referer: location.href },
          youtubeFormatArg: formatArg,
        }),
      });
      return r.ok;
    } catch {
      return false;
    }
  }

  // --- Build the panel HTML ---
  function createPanel() {
    const panel = document.createElement("div");
    panel.id = PANEL_ID;
    panel.style.cssText = `
      box-sizing: border-box;
      width: 100%;
      background: var(--yt-spec-base-background, #0f0f0f);
      border-top: 1px solid var(--yt-spec-10-percent-layer, #272727);
      border-bottom: 1px solid var(--yt-spec-10-percent-layer, #272727);
      padding: 10px 16px;
      margin: 0 0 8px 0;
      font-family: "Roboto", "Arial", sans-serif;
    `;
    panel.innerHTML = `
      <div style="display:flex;align-items:center;gap:12px;flex-wrap:wrap;">
        <div style="display:flex;align-items:center;gap:8px;flex-shrink:0;">
          <div id="wdm-logo" style="width:28px;height:28px;border-radius:6px;background:linear-gradient(135deg,#3b82f6,#8b5cf6);display:flex;align-items:center;justify-content:center;">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
              <path d="M21 15v4a2 2 0 01-2 2H5a2 2 0 01-2-2v-4"/>
              <polyline points="7 10 12 15 17 10"/>
              <line x1="12" y1="15" x2="12" y2="3"/>
            </svg>
          </div>
          <span style="font-size:12px;font-weight:700;color:#fff;letter-spacing:0.2px;">WDM</span>
        </div>
        <div id="wdm-yt-buttons" style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;flex:1;">
          <div id="wdm-yt-loading" style="display:flex;align-items:center;gap:8px;color:#aaa;font-size:12px;">
            <div id="wdm-spinner" style="width:14px;height:14px;border:2px solid #444;border-top-color:#3b82f6;border-radius:50%;animation:wdm-spin 0.7s linear infinite;"></div>
            Analyzing video formats…
          </div>
        </div>
        <div id="wdm-yt-status" style="font-size:11px;color:#71717a;margin-left:auto;"></div>
      </div>
      <style>
        @keyframes wdm-spin { to { transform: rotate(360deg); } }
        .wdm-btn {
          display: inline-flex;
          align-items: center;
          gap: 5px;
          border: 1px solid #3f3f46;
          border-radius: 20px;
          padding: 5px 13px;
          font-size: 12px;
          font-weight: 600;
          cursor: pointer;
          transition: background 0.15s, border-color 0.15s, transform 0.1s;
          white-space: nowrap;
          background: #1c1c1e;
          color: #e4e4e7;
          font-family: "Roboto", "Arial", sans-serif;
        }
        .wdm-btn:hover {
          background: #27272a;
          border-color: #52525b;
        }
        .wdm-btn:active {
          transform: scale(0.96);
        }
        .wdm-btn.wdm-btn-primary {
          background: linear-gradient(135deg, #2563eb, #7c3aed);
          border-color: transparent;
          color: #fff;
        }
        .wdm-btn.wdm-btn-primary:hover {
          background: linear-gradient(135deg, #1d4ed8, #6d28d9);
          border-color: transparent;
        }
        .wdm-btn.wdm-btn-audio {
          background: #1c1c1e;
          border-color: #8b5cf6;
          color: #c4b5fd;
        }
        .wdm-btn.wdm-btn-audio:hover {
          background: #2e1065;
          border-color: #7c3aed;
        }
        .wdm-btn.wdm-btn-sent {
          background: #14532d;
          border-color: #22c55e;
          color: #86efac;
          cursor: default;
        }
        .wdm-size-hint {
          font-size: 10px;
          font-weight: 400;
          opacity: 0.65;
        }
      </style>
    `;
    return panel;
  }

  // --- Render quality buttons into the panel ---
  function renderQualities(panel, data) {
    const container = panel.querySelector("#wdm-yt-buttons");
    const loading = panel.querySelector("#wdm-yt-loading");
    const status = panel.querySelector("#wdm-yt-status");

    if (loading) loading.remove();

    const qualities = data.qualities || [];

    if (qualities.length === 0) {
      container.innerHTML = `<span style="color:#71717a;font-size:12px;">No downloadable formats found.</span>`;
      return;
    }

    qualities.forEach((q, i) => {
      const btn = document.createElement("button");
      const isAudio = q.label.toLowerCase().includes("audio");
      const isBest = i === 0;

      btn.className = `wdm-btn ${isBest ? "wdm-btn-primary" : ""} ${isAudio ? "wdm-btn-audio" : ""}`;

      const sizeHint = q.estimatedSizeText
        ? `<span class="wdm-size-hint">~${q.estimatedSizeText}</span>`
        : "";

      if (isAudio) {
        btn.innerHTML = `
          <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
            <path d="M9 18V5l12-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="18" cy="16" r="3"/>
          </svg>
          ${q.label} ${sizeHint}`;
      } else {
        btn.innerHTML = `
          <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
            <polygon points="23 7 16 12 23 17 23 7"/><rect x="1" y="5" width="15" height="14" rx="2" ry="2"/>
          </svg>
          ${q.label} ${sizeHint}`;
      }

      btn.title = `Download with WDM: ${q.label}`;

      btn.addEventListener("click", async () => {
        if (btn.classList.contains("wdm-btn-sent")) return;
        btn.classList.add("wdm-btn-sent");
        btn.classList.remove("wdm-btn-primary", "wdm-btn-audio");
        btn.innerHTML = `
          <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
            <polyline points="20 6 9 17 4 12"/>
          </svg>
          Sent!`;

        const success = await downloadVia(currentVideoId, q.formatArg, q.label);
        if (!success) {
          btn.classList.remove("wdm-btn-sent");
          btn.classList.add("wdm-btn-primary");
          btn.textContent = "Retry";
          status.textContent = "Could not reach WDM.";
          status.style.color = "#f87171";
        } else {
          status.textContent = `"${q.label}" queued in WDM`;
          status.style.color = "#86efac";
          setTimeout(() => { status.textContent = ""; }, 5000);
          // Reset button after delay
          setTimeout(() => {
            btn.classList.remove("wdm-btn-sent");
            if (i === 0) btn.classList.add("wdm-btn-primary");
            if (isAudio) btn.classList.add("wdm-btn-audio");
            btn.innerHTML = isBest
              ? `<svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polygon points="23 7 16 12 23 17 23 7"/><rect x="1" y="5" width="15" height="14" rx="2" ry="2"/></svg> ${q.label} ${sizeHint}`
              : btn.innerHTML;
          }, 3000);
        }
      });

      container.appendChild(btn);
    });

    // Show video title as tooltip if available
    if (data.title) {
      status.textContent = data.title.length > 60 ? data.title.slice(0, 57) + "…" : data.title;
      status.title = data.title;
    }
  }

  // --- Fetch resolution from WDM ---
  async function resolveVideo(videoId, panel) {
    if (isResolving) return;
    isResolving = true;

    const status = panel.querySelector("#wdm-yt-status");

    try {
      const url = encodeURIComponent(`https://www.youtube.com/watch?v=${videoId}`);
      const r = await fetch(`${WDM_HOST}/resolve?url=${url}`, { method: "GET" });
      if (!r.ok) throw new Error(`HTTP ${r.status}`);
      const data = await r.json();
      if (data.error) throw new Error(data.error);
      renderQualities(panel, data);
    } catch (e) {
      const container = panel.querySelector("#wdm-yt-buttons");
      const loading = panel.querySelector("#wdm-yt-loading");
      if (loading) loading.remove();
      container.innerHTML = `<span style="color:#f87171;font-size:12px;">Could not load formats: ${e.message}</span>
        <button class="wdm-btn" id="wdm-retry" style="margin-left:8px;">Retry</button>`;
      container.querySelector("#wdm-retry")?.addEventListener("click", () => {
        container.innerHTML = `<div id="wdm-yt-loading" style="display:flex;align-items:center;gap:8px;color:#aaa;font-size:12px;">
          <div style="width:14px;height:14px;border:2px solid #444;border-top-color:#3b82f6;border-radius:50%;animation:wdm-spin 0.7s linear infinite;"></div>
          Analyzing video formats…</div>`;
        isResolving = false;
        resolveVideo(videoId, panel);
      });
    } finally {
      isResolving = false;
    }
  }

  // --- Inject panel below the video ---
  function injectPanel(videoId) {
    // Remove existing panel
    document.getElementById(PANEL_ID)?.remove();

    // Try insertion points (YouTube DOM can differ)
    const targets = [
      "#below",                    // standard watch page
      "#primary-inner",            // alternate layout
      "ytd-watch-flexy #primary",  // flex layout
    ];

    let anchor = null;
    for (const sel of targets) {
      anchor = document.querySelector(sel);
      if (anchor) break;
    }

    if (!anchor) return;

    const panel = createPanel();
    anchor.insertBefore(panel, anchor.firstChild);

    resolveVideo(videoId, panel);
  }

  // --- Observe YouTube SPA navigation ---
  function onNavigate() {
    const vid = getVideoId();
    if (!vid || vid === currentVideoId) return;
    currentVideoId = vid;
    isResolving = false;

    // Wait briefly for YouTube's DOM to settle
    setTimeout(async () => {
      const active = await checkWdm();
      if (!active) return; // WDM not running, don't show panel
      injectPanel(vid);
    }, 1800);
  }

  // Initial check
  onNavigate();

  // YouTube is an SPA, watch for URL changes via history patches
  const origPushState = history.pushState.bind(history);
  history.pushState = function (...args) {
    origPushState(...args);
    onNavigate();
  };
  const origReplaceState = history.replaceState.bind(history);
  history.replaceState = function (...args) {
    origReplaceState(...args);
    onNavigate();
  };
  window.addEventListener("popstate", onNavigate);

  // Also watch yt-navigate-finish event (YouTube fires this on navigation)
  document.addEventListener("yt-navigate-finish", onNavigate);
})();
