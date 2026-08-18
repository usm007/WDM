// WDM Download Catcher — Firefox MV3 background script.
const webext = typeof browser !== "undefined" ? browser : chrome;
const WDM_HOST = "http://127.0.0.1:17530";

// Re-entrance guard for URLs handed off to WDM
const loopGuard = new Map();

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