// WDM Media Sniffer — Content Script
// Detects media streams on any web page and offers to download them via WDM.
// Communicates with background script to eliminate browser PNA local network access prompts.

(function () {
  "use strict";

  const MEDIA_EXTS = /\.(mp4|webm|mkv|avi|mov|flv|m4v|mp3|m4a|aac|ogg|opus|flac|wav|ts|m2ts|mts)(\?|$)/i;
  const HLS_RE = /\.(m3u8)(\?|$)/i;
  const DASH_RE = /\.(mpd)(\?|$)/i;
  const MIN_SIZE_HINT = 1024 * 1024;

  const foundUrls = new Set();
  let wdmActive = false;
  let badgeContainer = null;

  const webext = typeof browser !== "undefined" ? browser : (typeof chrome !== "undefined" ? chrome : null);
  if (!webext || !webext.runtime) return;

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

  function sendToWdm(url, label) {
    try {
      webext.runtime.sendMessage({
        action: "download",
        payload: {
          url,
          fileName: null,
          referer: location.href,
          headers: { Referer: location.href },
        }
      });
    } catch (e) {
      console.warn("[WDM] Failed to send to WDM via background:", e);
    }
  }

  function ensureBadgeContainer() {
    if (badgeContainer) return;
    badgeContainer = document.createElement("div");
    badgeContainer.id = "wdm-media-badge";
    badgeContainer.style.cssText = `
      position: fixed;
      bottom: 16px;
      right: 16px;
      z-index: 2147483647;
      display: flex;
      flex-direction: column;
      gap: 8px;
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
      pointer-events: none;
    `;
    document.documentElement.appendChild(badgeContainer);
  }

  function addBadge(url, label, type) {
    if (!wdmActive) return;
    if (foundUrls.has(url)) return;
    foundUrls.add(url);

    ensureBadgeContainer();

    const badge = document.createElement("div");
    badge.style.cssText = `
      display: flex;
      align-items: center;
      gap: 10px;
      background: #18181b;
      border: 1px solid #3f3f46;
      border-radius: 10px;
      padding: 10px 14px;
      max-width: 340px;
      box-shadow: 0 8px 32px rgba(0,0,0,0.45);
      pointer-events: all;
      animation: wdm-slide-in 0.2s ease-out;
    `;

    const iconColor = type === "hls" ? "#f59e0b" : type === "dash" ? "#8b5cf6" : "#3b82f6";
    const typeLabel = type === "hls" ? "HLS" : type === "dash" ? "DASH" : "Media";
    const iconUrl = (webext.runtime && webext.runtime.getURL) ? webext.runtime.getURL("icon32.png") : "";

    badge.innerHTML = `
      <style>
        @keyframes wdm-slide-in { from { opacity:0; transform:translateX(20px); } to { opacity:1; transform:translateX(0); } }
      </style>
      <div style="width:34px;height:34px;border-radius:8px;background:${iconColor}22;display:flex;align-items:center;justify-content:center;flex-shrink:0;">
        <img src="${iconUrl}" width="22" height="22" style="width:22px;height:22px;border-radius:4px;object-fit:contain;" alt="WDM" onerror="this.style.display='none'" />
      </div>
      <div style="flex:1;min-width:0;">
        <div style="font-size:11px;font-weight:700;color:#38bdf8;text-transform:uppercase;letter-spacing:0.5px;margin-bottom:2px;">WDM · ${typeLabel} Detected</div>
        <div style="font-size:12px;color:#e4e4e7;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;" title="${label}">${label}</div>
      </div>
      <button data-wdm-dl style="background:#2563eb;color:#fff;border:none;border-radius:7px;padding:6px 12px;font-size:12px;font-weight:600;cursor:pointer;flex-shrink:0;white-space:nowrap;transition:background 0.2s;">Download with WDM</button>
      <button data-wdm-close style="background:none;border:none;color:#71717a;cursor:pointer;font-size:16px;padding:2px 4px;flex-shrink:0;line-height:1;" title="Dismiss">✕</button>
    `;

    badge.querySelector("[data-wdm-dl]").addEventListener("click", () => {
      sendToWdm(url, label);
      badge.querySelector("[data-wdm-dl]").textContent = "Sent!";
      badge.querySelector("[data-wdm-dl]").style.background = "#22c55e";
      setTimeout(() => badge.remove(), 1500);
    });
    badge.querySelector("[data-wdm-close]").addEventListener("click", () => badge.remove());

    badgeContainer.appendChild(badge);
    setTimeout(() => { if (badge.parentNode) badge.remove(); }, 15000);
  }

  function reportUrl(url, size) {
    if (!url || url.startsWith("blob:") || url.startsWith("data:")) return;
    if (foundUrls.has(url)) return;
    try { new URL(url); } catch { return; }

    let name = "";
    try { name = new URL(url).pathname.split("/").pop() || url; } catch { name = url; }

    if (HLS_RE.test(url)) addBadge(url, name, "hls");
    else if (DASH_RE.test(url)) addBadge(url, name, "dash");
    else if (MEDIA_EXTS.test(url)) {
      if (!size || size > MIN_SIZE_HINT) addBadge(url, name, "media");
    }
  }

  function observeMediaElements() {
    const handle = (el) => {
      const src = el.src || el.currentSrc;
      if (src) reportUrl(src, null);
      el.addEventListener("loadstart", () => {
        const s = el.src || el.currentSrc;
        if (s) reportUrl(s, null);
      });
    };

    document.querySelectorAll("video, audio").forEach(handle);

    const observer = new MutationObserver((mutations) => {
      for (const m of mutations) {
        for (const node of m.addedNodes) {
          if (node.nodeType !== Node.ELEMENT_NODE) continue;
          if (node.matches && node.matches("video, audio")) handle(node);
          else if (node.querySelectorAll) node.querySelectorAll("video, audio").forEach(handle);
        }
      }
    });

    observer.observe(document.documentElement, { childList: true, subtree: true });
  }

  function hookFetchAndXhr() {
    const origFetch = window.fetch;
    if (origFetch) {
      window.fetch = async function (...args) {
        const req = args[0];
        const url = typeof req === "string" ? req : req?.url;
        if (url) reportUrl(url, null);
        return origFetch.apply(this, args);
      };
    }

    const origOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (method, url, ...rest) {
      if (typeof url === "string") reportUrl(url, null);
      return origOpen.call(this, method, url, ...rest);
    };
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", () => {
      observeMediaElements();
      hookFetchAndXhr();
    });
  } else {
    observeMediaElements();
    hookFetchAndXhr();
  }
})();
