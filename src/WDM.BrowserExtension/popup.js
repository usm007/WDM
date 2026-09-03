// WDM Download Catcher — popup toggle for capture on/off.
const webext = typeof browser !== "undefined" ? browser : chrome;
const STORAGE_KEY = "captureEnabled";

const toggle = document.getElementById("enabled");
const statusEl = document.getElementById("status");

async function load() {
  const data = await webext.storage.local.get(STORAGE_KEY);
  toggle.checked = data[STORAGE_KEY] !== false;
  updateStatus();
}

async function save() {
  await webext.storage.local.set({ [STORAGE_KEY]: toggle.checked });
  updateStatus();
}

function updateStatus() {
  statusEl.textContent = toggle.checked ? "● Capturing is ON — downloads go to WDM" : "○ Capturing is OFF — browser downloads normally";
}

toggle.addEventListener("change", save);

// Live-update the status while the popup is open and sync after storage changes.
load();
webext.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && changes[STORAGE_KEY]) {
    toggle.checked = changes[STORAGE_KEY].newValue !== false;
    updateStatus();
  }
});

// Load detected media streams on the active tab
async function loadMediaList() {
  try {
    const tabs = await webext.tabs.query({ active: true, currentWindow: true });
    if (!tabs || !tabs[0]) return;
    const tab = tabs[0];

    webext.runtime.sendMessage({ action: "getMediaList", tabId: tab.id }, (res) => {
      if (webext.runtime.lastError || !res) return;
      const media = res.media || [];
      const section = document.getElementById("media-section");
      const countEl = document.getElementById("media-count");
      const listEl = document.getElementById("media-list");

      if (media.length === 0) {
        section.style.display = "none";
        return;
      }

      section.style.display = "block";
      countEl.textContent = String(media.length);
      listEl.innerHTML = "";

      for (const m of media) {
        const item = document.createElement("div");
        item.className = "media-item";
        item.innerHTML = `
          <div class="media-item-info">
            <div class="media-item-title" title="${m.label || m.url}">${m.label || m.url}</div>
            <div class="media-item-type">${m.type} Stream</div>
          </div>
          <button class="media-item-btn">Download</button>
        `;

        const btn = item.querySelector(".media-item-btn");
        btn.addEventListener("click", () => {
          webext.runtime.sendMessage({
            action: "download",
            payload: {
              url: m.url,
              fileName: m.label || null,
              referer: tab.url,
              headers: { "Referer": tab.url, "Origin": new URL(tab.url).origin },
              pageTitle: tab.title || null,
              streamType: m.type
            }
          }, () => {
            btn.textContent = "Sent!";
            btn.style.background = "#22c55e";
          });
        });

        listEl.appendChild(item);
      }
    });
  } catch (err) {
    console.warn("Failed to load media list:", err);
  }
}
loadMediaList();