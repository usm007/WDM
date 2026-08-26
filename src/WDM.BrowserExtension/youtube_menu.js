// WDM YouTube Subtle Controller — Content Script
// Seamlessly integrates into YouTube's native action bar (next to Share / Clip / Save).
// Proxies all RPC calls via background service worker to prevent PNA permission popups.

(function () {
  "use strict";

  if (!location.hostname.includes("youtube.com")) return;

  const BUTTON_ID = "wdm-yt-native-btn";
  const POPOVER_ID = "wdm-yt-popover";
  const webext = typeof browser !== "undefined" ? browser : (typeof chrome !== "undefined" ? chrome : null);
  if (!webext || !webext.runtime) return;

  let currentVideoId = null;
  let wdmActive = false;
  let isResolving = false;
  let resolvedQualities = null;

  function checkWdm() {
    try {
      webext.runtime.sendMessage({ action: "ping" }, (res) => {
        if (webext.runtime.lastError) { wdmActive = false; return; }
        wdmActive = !!res?.active;
      });
    } catch {
      wdmActive = false;
    }
  }
  checkWdm();
  setInterval(checkWdm, 5000);

  function getVideoId() {
    const p = new URLSearchParams(location.search);
    return p.get("v") || null;
  }

  function sendDownload(formatArg, label) {
    if (!currentVideoId) return;
    const url = `https://www.youtube.com/watch?v=${currentVideoId}`;
    webext.runtime.sendMessage({
      action: "download",
      payload: {
        url,
        fileName: null,
        referer: location.href,
        headers: { Referer: location.href },
        youtubeFormatArg: formatArg,
      }
    });
  }

  function fetchQualities(videoId, callback) {
    if (resolvedQualities && currentVideoId === videoId) {
      callback(resolvedQualities);
      return;
    }
    if (isResolving) return;
    isResolving = true;

    const url = `https://www.youtube.com/watch?v=${videoId}`;
    webext.runtime.sendMessage({ action: "resolve", url }, (res) => {
      isResolving = false;
      if (webext.runtime.lastError || !res?.success) {
        // Fallback quality tiers
        resolvedQualities = [
          { label: "1080p (Full HD)", formatArg: "bestvideo[height<=1080]+bestaudio/best" },
          { label: "720p (HD)", formatArg: "bestvideo[height<=720]+bestaudio/best" },
          { label: "480p", formatArg: "bestvideo[height<=480]+bestaudio/best" },
          { label: "360p", formatArg: "bestvideo[height<=360]+bestaudio/best" },
          { label: "Audio Only (MP3)", formatArg: "bestaudio/best" }
        ];
      } else {
        resolvedQualities = res.data?.qualities || [];
      }
      callback(resolvedQualities);
    });
  }

  function togglePopover(anchorBtn) {
    let popover = document.getElementById(POPOVER_ID);
    if (popover) {
      popover.remove();
      return;
    }

    popover = document.createElement("div");
    popover.id = POPOVER_ID;
    popover.style.cssText = `
      position: absolute;
      top: 100%;
      right: 0;
      margin-top: 6px;
      z-index: 9999;
      background: var(--yt-spec-menu-background, #212121);
      border: 1px solid var(--yt-spec-10-percent-layer, #383838);
      border-radius: 12px;
      padding: 8px 0;
      min-width: 200px;
      box-shadow: 0 4px 24px rgba(0,0,0,0.4);
      font-family: Roboto, Arial, sans-serif;
      font-size: 13px;
      color: var(--yt-spec-text-primary, #f1f1f1);
    `;

    popover.innerHTML = `
      <div style="padding:6px 14px 8px 14px;font-size:11px;font-weight:500;color:var(--yt-spec-text-secondary, #aaa);border-bottom:1px solid var(--yt-spec-10-percent-layer, #2e2e2e);margin-bottom:4px;display:flex;align-items:center;justify-content:space-between;">
        <span>WDM Download</span>
        <span id="wdm-pop-status">Resolving…</span>
      </div>
      <div id="wdm-pop-items" style="display:flex;flex-direction:column;">
        <div style="padding:10px 14px;color:#aaa;font-size:12px;">Loading qualities…</div>
      </div>
    `;

    // Position popover relatively below anchor button
    if (getComputedStyle(anchorBtn.parentNode).position === "static") {
      anchorBtn.parentNode.style.position = "relative";
    }
    anchorBtn.parentNode.appendChild(popover);

    fetchQualities(currentVideoId, (qualities) => {
      const itemsContainer = popover.querySelector("#wdm-pop-items");
      const statusEl = popover.querySelector("#wdm-pop-status");
      if (!itemsContainer) return;

      if (statusEl) statusEl.textContent = "Select format";
      itemsContainer.innerHTML = "";

      if (qualities.length === 0) {
        itemsContainer.innerHTML = `<div style="padding:8px 14px;color:#aaa;">No qualities found</div>`;
        return;
      }

      qualities.forEach((q) => {
        const item = document.createElement("div");
        item.style.cssText = `
          padding: 8px 14px;
          cursor: pointer;
          display: flex;
          align-items: center;
          justify-content: space-between;
          transition: background 0.15s;
        `;
        item.onmouseenter = () => { item.style.background = "var(--yt-spec-badge-chip-background, rgba(255,255,255,0.1))"; };
        item.onmouseleave = () => { item.style.background = "transparent"; };

        const sizeText = q.estimatedSizeText ? `<span style="font-size:11px;opacity:0.6;margin-left:8px;">~${q.estimatedSizeText}</span>` : "";
        item.innerHTML = `<span>${q.label}</span>${sizeText}`;

        item.addEventListener("click", () => {
          sendDownload(q.formatArg, q.label);
          item.style.color = "#22c55e";
          item.innerHTML = `<span>✓ Queued in WDM</span>`;
          setTimeout(() => popover.remove(), 1200);
        });

        itemsContainer.appendChild(item);
      });
    });

    // Close on click outside
    const onClickOutside = (e) => {
      if (!popover.contains(e.target) && !anchorBtn.contains(e.target)) {
        popover.remove();
        document.removeEventListener("click", onClickOutside);
      }
    };
    setTimeout(() => document.addEventListener("click", onClickOutside), 100);
  }

  function injectNativeButton() {
    if (document.getElementById(BUTTON_ID)) return;

    // Look for YouTube native action bar containers next to Share/Save
    const targets = [
      "#top-level-buttons-computed",
      "ytd-watch-flexy #menu #top-level-buttons-computed",
      "ytd-menu-renderer #top-level-buttons-computed",
      "#actions #menu #top-level-buttons-computed",
      "#owner #menu"
    ];

    let bar = null;
    for (const sel of targets) {
      bar = document.querySelector(sel);
      if (bar) break;
    }

    if (!bar) return;

    const btn = document.createElement("button");
    btn.id = BUTTON_ID;
    btn.style.cssText = `
      display: inline-flex;
      align-items: center;
      gap: 6px;
      height: 36px;
      padding: 0 16px;
      border: none;
      border-radius: 18px;
      background: var(--yt-spec-badge-chip-background, rgba(255, 255, 255, 0.1));
      color: var(--yt-spec-text-primary, #f1f1f1);
      font-family: Roboto, Arial, sans-serif;
      font-size: 14px;
      font-weight: 500;
      cursor: pointer;
      margin-left: 8px;
      transition: background 0.2s;
    `;
    btn.onmouseenter = () => { btn.style.background = "var(--yt-spec-button-chip-background-hover, rgba(255, 255, 255, 0.2))"; };
    btn.onmouseleave = () => { btn.style.background = "var(--yt-spec-badge-chip-background, rgba(255, 255, 255, 0.1))"; };

    btn.innerHTML = `
      <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <path d="M21 15v4a2 2 0 01-2 2H5a2 2 0 01-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/>
      </svg>
      <span>Download</span>
      <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" style="margin-left:2px;">
        <polyline points="6 9 12 15 18 9"/>
      </svg>
    `;

    btn.addEventListener("click", (e) => {
      e.stopPropagation();
      togglePopover(btn);
    });

    bar.appendChild(btn);
  }

  function checkNavigation() {
    const vid = getVideoId();
    if (!vid) {
      document.getElementById(BUTTON_ID)?.remove();
      document.getElementById(POPOVER_ID)?.remove();
      currentVideoId = null;
      resolvedQualities = null;
      return;
    }
    if (vid !== currentVideoId) {
      currentVideoId = vid;
      resolvedQualities = null;
      document.getElementById(BUTTON_ID)?.remove();
      document.getElementById(POPOVER_ID)?.remove();
    }
    if (wdmActive) {
      injectNativeButton();
    }
  }

  setInterval(checkNavigation, 1000);
  document.addEventListener("yt-navigate-finish", checkNavigation);
})();
