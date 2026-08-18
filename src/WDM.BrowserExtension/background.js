// WDM Download Catcher — cross-browser (Chrome MV3 + Firefox MV3).
const webext = typeof browser !== "undefined" ? browser : chrome;
const WDM_HOST = "http://127.0.0.1:17530";

// Re-entrance guard for URLs handed off to WDM
const loopGuard = new Map();

// Capture on/off, persisted in storage so the toggle survives restarts.
const STORAGE_KEY = "captureEnabled";
let captureEnabled = true;
async function loadCaptureState() {
  try {
    const data = await webext.storage.local.get(STORAGE_KEY);
    captureEnabled = data[STORAGE_KEY] !== false;
    updateBadge();
  } catch {
    captureEnabled = true;
  }
}
function updateBadge() {
  if (!captureEnabled) {
    try { webext.action.setBadgeText({ text: "OFF" }); } catch {}
  } else {
    try { webext.action.setBadgeText({ text: "" }); } catch {}
  }
}
webext.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && changes[STORAGE_KEY]) {
    captureEnabled = changes[STORAGE_KEY].newValue !== false;
    updateBadge();
  }
});
loadCaptureState();

// Periodic ping to verify WDM connection status
let isWdmActive = false;
async function checkWdm() {
  try {
    const res = await fetch(`${WDM_HOST}/ping`, { method: "GET" });
    isWdmActive = res.ok;
  } catch {
    isWdmActive = false;
  }
}
checkWdm();
setInterval(checkWdm, 4000);

webext.downloads.onCreated.addListener(async (item) => {
  // 0. If capturing is switched off, let the browser download naturally.
  if (!captureEnabled) {
    return;
  }

  // 1. Ignore historical/restored items when browser/PC restarts
  if (item.state && item.state !== "in_progress") {
    return;
  }

  // 2. Ignore items created in the past (more than 10 seconds ago)
  if (item.startTime && (Date.now() - new Date(item.startTime).getTime()) > 10000) {
    return;
  }

  // 3. Ignore non-downloadable protocols (file:, blob:, data:, chrome:, about:)
  if (!item.url || !/^https?:\/\//i.test(item.url)) {
    return;
  }

  // 4. Ignore URLs in loop guard
  if (loopGuard.get(item.url) > Date.now()) {
    loopGuard.delete(item.url);
    return;
  }

  // 5. Ping WDM first BEFORE cancelling the browser download.
  // If WDM is not running, let the browser download naturally!
  await checkWdm();
  if (!isWdmActive) {
    return;
  }

  // Mark URL in loop guard
  loopGuard.set(item.url, Date.now() + 15000);

  // Cancel browser download and hand over to WDM
  try {
    await webext.downloads.cancel(item.id);
    await webext.downloads.erase({ id: item.id }).catch(() => {});
  } catch {
    // If cancel failed, continue anyway
  }

  try {
    await sendToWdm(item.url, item.filename, item.referrer);
  } catch (err) {
    console.warn("WDM handoff failed:", err);
  }
});

async function sendToWdm(url, filename, referrer) {
  const response = await fetch(`${WDM_HOST}/download`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ url, fileName: filename || null, referer: referrer || null }),
  });
  if (!response.ok) {
    throw new Error(`WDM responded ${response.status}`);
  }
}