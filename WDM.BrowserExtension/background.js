// WDM Download Catcher — sends browser downloads to WDM's localhost server.
const WDM_HOST = "http://127.0.0.1:17530";

// URLs we just re-issued to the browser (fallback after WDM is unreachable),
// so we don't recursively catch our own re-download.
const loopGuard = new Map();

chrome.downloads.onCreated.addListener((item) => {
  if (!item.url || item.url.startsWith("file:")) {
    return;
  }
  if (loopGuard.get(item.url) > Date.now()) {
    loopGuard.delete(item.url);
    return;
  }

  // Cancel the browser's own download first, then hand it to WDM.
  chrome.downloads.cancel(item.id, () => {
    void chrome.downloads.erase({ id: item.id });
  });

  sendToWdm(item.url, item.filename, item.referrer)
    .then(() => {})
    .catch(() => {
      // WDM is not running — restore the original browser download.
      loopGuard.set(item.url, Date.now() + 15000);
      void chrome.downloads.download({ url: item.url, filename: item.filename });
    });
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
