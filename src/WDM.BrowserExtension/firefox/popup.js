// WDM Download Catcher — popup: capture toggle + page-resource list
// (1DM PageResourceListActivity equivalent: copy / download / block domain / block URL).
const webext = typeof browser !== "undefined" ? browser : chrome;
const STORAGE_KEY = "captureEnabled";

const toggle = document.getElementById("enabled");
const statusEl = document.getElementById("status");
const mediaListEl = document.getElementById("mediaList");
const refreshBtn = document.getElementById("refreshMedia");
const downloadAllBtn = document.getElementById("downloadAll");

let currentMedia = [];

if (!toggle || !statusEl) {
  console.warn("WDM popup: expected controls not found in markup.");
} else {
  toggle.addEventListener("change", save);

  // Live-update the status while the popup is open and sync after storage changes.
  load();
  webext.storage.onChanged.addListener((changes, area) => {
    if (area === "local" && changes[STORAGE_KEY]) {
      toggle.checked = changes[STORAGE_KEY].newValue !== false;
      updateStatus();
    }
  });
}

if (refreshBtn) refreshBtn.addEventListener("click", loadMedia);
if (downloadAllBtn) downloadAllBtn.addEventListener("click", downloadAll);

loadMedia();

async function load() {
  if (!toggle) return;
  const data = await webext.storage.local.get(STORAGE_KEY);
  toggle.checked = data[STORAGE_KEY] !== false;
  updateStatus();
}

function updateStatus() {
  if (!toggle || !statusEl) return;
  statusEl.textContent = toggle.checked ? "● Capturing is ON — downloads go to WDM" : "○ Capturing is OFF — browser downloads normally";
}

async function save() {
  if (!toggle) return;
  await webext.storage.local.set({ [STORAGE_KEY]: toggle.checked });
  updateStatus();
}

async function loadMedia() {
  if (!mediaListEl) return;
  try {
    const res = await webext.runtime.sendMessage({ action: "getMediaList" });
    currentMedia = (res && res.media) || [];
    renderMedia(res && res.wdmActive);
  } catch {
    currentMedia = [];
    renderMedia(false);
  }
}

function esc(s) {
  return String(s == null ? "" : s).replace(/[&<>"']/g, c => (
    { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

function renderMedia(wdmActive) {
  if (!mediaListEl) return;
  mediaListEl.textContent = "";
  if (currentMedia.length === 0) {
    const d = document.createElement("div");
    d.className = "empty";
    d.textContent = wdmActive === false
      ? "WDM app not detected — start WDM, then reload the page."
      : "No media detected on this page yet.";
    mediaListEl.appendChild(d);
    if (downloadAllBtn) downloadAllBtn.disabled = true;
    return;
  }
  for (const m of currentMedia) {
    const row = document.createElement("div");
    row.className = "media-row";

    const info = document.createElement("div");
    info.className = "media-info";
    const name = document.createElement("div");
    name.className = "media-name";
    name.textContent = m.label || m.url;
    name.title = m.url;
    const meta = document.createElement("div");
    meta.className = "media-meta";
    meta.textContent = (m.type || "Video") + " • " + ageText(m.time);
    info.appendChild(name);
    info.appendChild(meta);

    const btns = document.createElement("div");
    btns.className = "media-btns";
    btns.appendChild(mkBtn("⤓", "Download with WDM", () => downloadOne(m)));
    btns.appendChild(mkBtn("⧉", "Copy URL", () => copyUrl(m.url)));
    btns.appendChild(mkBtn("⊘D", "Block this domain", () => blockDomain(m.url)));
    btns.appendChild(mkBtn("⊘U", "Block this exact URL", () => blockUrl(m.url)));

    row.appendChild(info);
    row.appendChild(btns);
    mediaListEl.appendChild(row);
  }
  if (downloadAllBtn) downloadAllBtn.disabled = false;
}

function mkBtn(text, title, onClick) {
  const b = document.createElement("button");
  b.textContent = text;
  b.title = title;
  b.addEventListener("click", onClick);
  return b;
}

function ageText(t) {
  if (!t) return "just now";
  const s = Math.max(0, Math.round((Date.now() - t) / 1000));
  if (s < 5) return "just now";
  if (s < 60) return s + "s ago";
  return Math.floor(s / 60) + "m ago";
}

async function copyUrl(url) {
  try {
    await navigator.clipboard.writeText(url);
  } catch {
    const ta = document.createElement("textarea");
    ta.value = url;
    document.body.appendChild(ta);
    ta.select();
    try { document.execCommand("copy"); } catch {}
    ta.remove();
  }
}

function pageContext() {
  return { referer: null, pageTitle: null };
}

async function downloadOne(m) {
  try {
    await webext.runtime.sendMessage({
      action: "download",
      payload: { url: m.url, fileName: m.label || null, headers: {}, keyUrl: m.keyUrl || null, ...pageContext() }
    });
    window.close();
  } catch (err) {
    console.warn("WDM download failed:", err);
  }
}

async function downloadAll() {
  if (currentMedia.length === 0) return;
  try {
    await webext.runtime.sendMessage({
      action: "downloadBatch",
      items: currentMedia.map(m => ({ url: m.url, fileName: m.label || null, headers: {}, keyUrl: m.keyUrl || null }))
    });
    window.close();
  } catch (err) {
    console.warn("WDM batch download failed:", err);
  }
}

async function blockDomain(url) {
  try {
    await webext.runtime.sendMessage({ action: "blockDomain", value: url });
    await loadMedia();
  } catch (err) {
    console.warn("WDM block failed:", err);
  }
}

async function blockUrl(url) {
  try {
    await webext.runtime.sendMessage({ action: "blockUrl", value: url });
    await loadMedia();
  } catch (err) {
    console.warn("WDM block failed:", err);
  }
}
