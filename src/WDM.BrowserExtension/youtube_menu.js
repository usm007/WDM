// WDM YouTube Quality Bar & Floating Controller
// Ghost Downloader 3 style: Injects quality buttons below YouTube player and a floating quick-download FAB.

(function () {
  "use strict";

  if (!location.hostname.includes("youtube.com")) return;

  const WDM_HOST = "http://127.0.0.1:17530";
  const PANEL_ID = "wdm-yt-panel";
  const FAB_ID = "wdm-yt-fab";

  let currentVideoId = null;
  let isResolving = false;
  let wdmActive = false;
  let resolvedData = null;

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
  setInterval(checkWdm, 4000);

  function getVideoId() {
    const p = new URLSearchParams(location.search);
    return p.get("v") || null;
  }

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

  function createPanel() {
    const panel = document.createElement("div");
    panel.id = PANEL_ID;
    panel.style.cssText = `
      box-sizing: border-box;
      width: 100%;
      background: #121214;
      border: 1px solid #27272a;
      border-radius: 12px;
      padding: 12px 16px;
      margin: 12px 0;
      font-family: "YouTube Noto", Roboto, Arial, sans-serif;
      box-shadow: 0 4px 20px rgba(0,0,0,0.5);
    `;
    panel.innerHTML = `
      <div style="display:flex;align-items:center;gap:12px;flex-wrap:wrap;">
        <div style="display:flex;align-items:center;gap:8px;flex-shrink:0;">
          <div style="width:30px;height:30px;border-radius:8px;background:linear-gradient(135deg,#3b82f6,#8b5cf6);display:flex;align-items:center;justify-content:center;">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
              <path d="M21 15v4a2 2 0 01-2 2H5a2 2 0 01-2-2v-4"/>
              <polyline points="7 10 12 15 17 10"/>
              <line x1="12" y1="15" x2="12" y2="3"/>
            </svg>
          </div>
          <span style="font-size:13px;font-weight:700;color:#fff;letter-spacing:0.3px;">WDM Download Manager</span>
        </div>
        <div id="wdm-yt-buttons" style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;flex:1;">
          <div id="wdm-yt-loading" style="display:flex;align-items:center;gap:8px;color:#a1a1aa;font-size:12px;">
            <div style="width:14px;height:14px;border:2px solid #3f3f46;border-top-color:#3b82f6;border-radius:50%;animation:wdm-spin 0.7s linear infinite;"></div>
            Analyzing video qualities & audio streams…
          </div>
        </div>
        <div id="wdm-yt-status" style="font-size:12px;color:#a1a1aa;margin-left:auto;"></div>
      </div>
      <style>
        @keyframes wdm-spin { to { transform: rotate(360deg); } }
        .wdm-btn {
          display: inline-flex;
          align-items: center;
          gap: 6px;
          border: 1px solid #3f3f46;
          border-radius: 20px;
          padding: 6px 14px;
          font-size: 12px;
          font-weight: 600;
          cursor: pointer;
          transition: all 0.15s ease;
          white-space: nowrap;
          background: #18181b;
          color: #f4f4f5;
        }
        .wdm-btn:hover {
          background: #27272a;
          border-color: #52525b;
          transform: translateY(-1px);
        }
        .wdm-btn:active {
          transform: scale(0.96);
        }
        .wdm-btn.wdm-btn-primary {
          background: linear-gradient(135deg, #2563eb, #7c3aed);
          border-color: transparent;
          color: #fff;
          box-shadow: 0 2px 10px rgba(37,99,235,0.3);
        }
        .wdm-btn.wdm-btn-primary:hover {
          background: linear-gradient(135deg, #1d4ed8, #6d28d9);
        }
        .wdm-btn.wdm-btn-audio {
          background: #2e1065;
          border-color: #7c3aed;
          color: #ddd6fe;
        }
        .wdm-btn.wdm-btn-audio:hover {
          background: #3b0764;
          border-color: #a855f7;
        }
        .wdm-btn.wdm-btn-sent {
          background: #14532d !important;
          border-color: #22c55e !important;
          color: #86efac !important;
          cursor: default;
        }
        .wdm-size-hint {
          font-size: 10px;
          opacity: 0.7;
        }
      </style>
    `;
    return panel;
  }

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

    container.innerHTML = "";
    qualities.forEach((q, i) => {
      const btn = document.createElement("button");
      const isAudio = q.label.toLowerCase().includes("audio");
      const isBest = i === 0;

      btn.className = `wdm-btn ${isBest ? "wdm-btn-primary" : ""} ${isAudio ? "wdm-btn-audio" : ""}`;

      const sizeHint = q.estimatedSizeText ? `<span class="wdm-size-hint">~${q.estimatedSizeText}</span>` : "";

      if (isAudio) {
        btn.innerHTML = `
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
            <path d="M9 18V5l12-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="18" cy="16" r="3"/>
          </svg>
          ${q.label} ${sizeHint}`;
      } else {
        btn.innerHTML = `
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
            <polygon points="23 7 16 12 23 17 23 7"/><rect x="1" y="5" width="15" height="14" rx="2" ry="2"/>
          </svg>
          ${q.label} ${sizeHint}`;
      }

      btn.title = `Download format ${q.label} with WDM`;
      btn.addEventListener("click", async () => {
        if (btn.classList.contains("wdm-btn-sent")) return;
        btn.classList.add("wdm-btn-sent");
        btn.innerHTML = `
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
            <polyline points="20 6 9 17 4 12"/>
          </svg>
          Queued in WDM`;

        const ok = await downloadVia(currentVideoId, q.formatArg, q.label);
        if (!ok) {
          btn.classList.remove("wdm-btn-sent");
          btn.textContent = "Retry";
          status.textContent = "Failed to connect to WDM.";
          status.style.color = "#f87171";
        } else {
          status.textContent = `"${q.label}" queued successfully`;
          status.style.color = "#86efac";
          setTimeout(() => { status.textContent = ""; }, 5000);
          setTimeout(() => {
            btn.classList.remove("wdm-btn-sent");
            if (isBest) btn.classList.add("wdm-btn-primary");
            if (isAudio) btn.classList.add("wdm-btn-audio");
            btn.innerHTML = isBest
              ? `<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polygon points="23 7 16 12 23 17 23 7"/><rect x="1" y="5" width="15" height="14" rx="2" ry="2"/></svg> ${q.label} ${sizeHint}`
              : btn.innerHTML;
          }, 4000);
        }
      });

      container.appendChild(btn);
    });

    if (data.title) {
      status.textContent = data.title.length > 50 ? data.title.slice(0, 47) + "…" : data.title;
    }
  }

  // Injects floating FAB quick button
  function ensureFab(videoId) {
    let fab = document.getElementById(FAB_ID);
    if (!fab) {
      fab = document.createElement("div");
      fab.id = FAB_ID;
      fab.style.cssText = `
        position: fixed;
        bottom: 24px;
        right: 24px;
        z-index: 2147483647;
        background: linear-gradient(135deg, #2563eb, #7c3aed);
        color: #fff;
        border-radius: 30px;
        padding: 10px 18px;
        font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
        font-size: 13px;
        font-weight: 700;
        display: flex;
        align-items: center;
        gap: 8px;
        cursor: pointer;
        box-shadow: 0 8px 25px rgba(37,99,235,0.45);
        transition: transform 0.2s, box-shadow 0.2s;
      `;
      fab.innerHTML = `
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
          <path d="M21 15v4a2 2 0 01-2 2H5a2 2 0 01-2-2v-4"/>
          <polyline points="7 10 12 15 17 10"/>
          <line x1="12" y1="15" x2="12" y2="3"/>
        </svg>
        <span>Download with WDM</span>
      `;
      fab.addEventListener("click", async () => {
        const active = await checkWdm();
        if (!active) {
          alert("Windows Download Manager (WDM) is not running on your PC. Please start WDM app.");
          return;
        }
        // Direct best quality download
        const ok = await downloadVia(currentVideoId, "bestvideo+bestaudio/best", "Best Quality");
        if (ok) {
          fab.style.background = "#16a34a";
          fab.querySelector("span").textContent = "Queued in WDM!";
          setTimeout(() => {
            fab.style.background = "linear-gradient(135deg, #2563eb, #7c3aed)";
            fab.querySelector("span").textContent = "Download with WDM";
          }, 3000);
        }
      });
      document.body.appendChild(fab);
    }
  }

  async function resolveVideo(videoId, panel) {
    if (isResolving) return;
    isResolving = true;

    try {
      const url = encodeURIComponent(`https://www.youtube.com/watch?v=${videoId}`);
      const r = await fetch(`${WDM_HOST}/resolve?url=${url}`, { method: "GET" });
      if (!r.ok) throw new Error(`HTTP ${r.status}`);
      const data = await r.json();
      if (data.error) throw new Error(data.error);
      resolvedData = data;
      renderQualities(panel, data);
    } catch (e) {
      const container = panel.querySelector("#wdm-yt-buttons");
      const loading = panel.querySelector("#wdm-yt-loading");
      if (loading) loading.remove();
      container.innerHTML = `<span style="color:#f87171;font-size:12px;">Could not resolve qualities: ${e.message}</span>
        <button class="wdm-btn" id="wdm-retry" style="margin-left:8px;">Retry</button>`;
      container.querySelector("#wdm-retry")?.addEventListener("click", () => {
        isResolving = false;
        resolveVideo(videoId, panel);
      });
    } finally {
      isResolving = false;
    }
  }

  function injectPanel(videoId) {
    if (!wdmActive) return;
    document.getElementById(PANEL_ID)?.remove();

    const selectors = [
      "#above-the-fold",
      "#top-row",
      "#below",
      "#primary-inner",
      "ytd-watch-flexy #primary",
      "#meta"
    ];

    let target = null;
    for (const sel of selectors) {
      target = document.querySelector(sel);
      if (target) break;
    }

    if (target) {
      const panel = createPanel();
      target.insertBefore(panel, target.firstChild);
      resolveVideo(videoId, panel);
    }

    ensureFab(videoId);
  }

  function checkNavigation() {
    const vid = getVideoId();
    if (!vid) {
      document.getElementById(PANEL_ID)?.remove();
      document.getElementById(FAB_ID)?.remove();
      currentVideoId = null;
      return;
    }
    if (vid !== currentVideoId) {
      currentVideoId = vid;
      isResolving = false;
      setTimeout(() => injectPanel(vid), 1200);
    }
  }

  setInterval(checkNavigation, 1000);
  document.addEventListener("yt-navigate-finish", checkNavigation);
})();
