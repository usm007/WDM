// WDM YouTube Integration — Content Script
// Injects a branded native-styled "WDM Download" pill with WDM logo into YouTube's action bar.
// Clicking it sends the video to WDM to open the Add Download Dialog (where the user chooses quality).

(function () {
  "use strict";

  if (!location.hostname.includes("youtube.com")) return;

  const BUTTON_ID = "wdm-yt-native-btn";
  const STYLE_ID = "wdm-yt-styles";
  const webext = typeof browser !== "undefined" ? browser : (typeof chrome !== "undefined" ? chrome : null);
  if (!webext || !webext.runtime) return;

  let currentVideoId = null;

  function injectGlobalStyles() {
    if (document.getElementById(STYLE_ID)) return;
    const style = document.createElement("style");
    style.id = STYLE_ID;
    style.textContent = `
      #${BUTTON_ID} {
        display: inline-flex !important;
        align-items: center !important;
        gap: 7px !important;
        height: 36px !important;
        padding: 0 14px !important;
        border-radius: 18px !important;
        font-family: Roboto, Arial, sans-serif !important;
        font-size: 14px !important;
        font-weight: 500 !important;
        margin-left: 8px !important;
        cursor: pointer !important;
        border: 1px solid rgba(0, 0, 0, 0.08) !important;
        outline: none !important;
        flex-shrink: 0 !important;
        background: rgba(0, 0, 0, 0.05) !important;
        color: #0f0f0f !important;
        transition: background 0.2s ease, opacity 0.2s ease, transform 0.1s ease !important;
      }
      #${BUTTON_ID}:hover {
        background: rgba(0, 0, 0, 0.1) !important;
      }
      #${BUTTON_ID}:active {
        transform: scale(0.97) !important;
      }
      html[dark] #${BUTTON_ID}, [dark] #${BUTTON_ID} {
        background: rgba(255, 255, 255, 0.1) !important;
        border: 1px solid rgba(255, 255, 255, 0.12) !important;
        color: #f1f1f1 !important;
      }
      html[dark] #${BUTTON_ID}:hover, [dark] #${BUTTON_ID}:hover {
        background: rgba(255, 255, 255, 0.18) !important;
      }
      #${BUTTON_ID} .wdm-btn-icon {
        width: 18px !important;
        height: 18px !important;
        border-radius: 4px !important;
        object-fit: contain !important;
        flex-shrink: 0 !important;
        display: inline-block !important;
      }
    `;
    document.head.appendChild(style);
  }

  function getVideoId() {
    const p = new URLSearchParams(location.search);
    return p.get("v") || null;
  }

  function findActionBar() {
    const selectors = [
      "#top-level-buttons-computed",
      "ytd-watch-metadata #top-level-buttons-computed",
      "ytd-watch-flexy #top-level-buttons-computed",
      "ytd-menu-renderer #top-level-buttons-computed",
      "#actions #top-level-buttons-computed",
      "#actions-inner",
      "#owner #menu",
      "#actions #menu"
    ];
    for (const sel of selectors) {
      const el = document.querySelector(sel);
      if (el) return el;
    }

    const likeBtn = document.querySelector("ytd-segmented-like-dislike-button-renderer, #like-button-renderer");
    if (likeBtn && likeBtn.parentNode) {
      return likeBtn.parentNode;
    }
    return null;
  }

  function injectNativeButton() {
    if (document.getElementById(BUTTON_ID)) return;

    const bar = findActionBar();
    if (!bar) return;

    injectGlobalStyles();

    const iconUrl = (webext.runtime && webext.runtime.getURL) ? webext.runtime.getURL("icon32.png") : "";

    const btn = document.createElement("button");
    btn.id = BUTTON_ID;
    btn.title = "Download video with WDM (Windows Download Manager)";

    if (iconUrl) {
      const img = document.createElement("img");
      img.src = iconUrl;
      img.className = "wdm-btn-icon";
      img.alt = "WDM";
      img.addEventListener("error", () => {
        img.style.display = "none";
      });
      btn.appendChild(img);
    }

    const span = document.createElement("span");
    span.textContent = "WDM Download";
    btn.appendChild(span);

    btn.addEventListener("click", (e) => {
      e.stopPropagation();
      const vid = getVideoId();
      if (!vid) return;

      const url = `https://www.youtube.com/watch?v=${vid}`;
      webext.runtime.sendMessage({
        action: "download",
        payload: {
          url,
          fileName: null,
          referer: location.href,
          headers: { Referer: location.href },
        }
      });

      span.textContent = "Opening in WDM…";
      btn.style.opacity = "0.7";
      setTimeout(() => {
        span.textContent = "WDM Download";
        btn.style.opacity = "1";
      }, 2000);
    });

    bar.appendChild(btn);
  }

  function checkNavigation() {
    const vid = getVideoId();
    if (!vid) {
      document.getElementById(BUTTON_ID)?.remove();
      currentVideoId = null;
      return;
    }
    if (vid !== currentVideoId) {
      currentVideoId = vid;
      document.getElementById(BUTTON_ID)?.remove();
    }
    injectNativeButton();
  }

  setInterval(checkNavigation, 500);
  document.addEventListener("yt-navigate-finish", checkNavigation);
})();
