// WDM Download Catcher — popup toggle for capture on/off.
const webext = typeof browser !== "undefined" ? browser : chrome;
const STORAGE_KEY = "captureEnabled";

const toggle = document.getElementById("enabled");
const statusEl = document.getElementById("status");

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

async function load() {
  if (!toggle) return;
  const data = await webext.storage.local.get(STORAGE_KEY);
  toggle.checked = data[STORAGE_KEY] !== false;
  updateStatus();
}

async function save() {
  if (!toggle) return;
  await webext.storage.local.set({ [STORAGE_KEY]: toggle.checked });
  updateStatus();
}

function updateStatus() {
  if (!toggle || !statusEl) return;
  statusEl.textContent = toggle.checked ? "● Capturing is ON — downloads go to WDM" : "○ Capturing is OFF — browser downloads normally";
}