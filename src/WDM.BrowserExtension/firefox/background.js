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

// Gather all cookies for a URL's domain, referrer, and parent domains to build a complete "Cookie: ..." header string.
async function getCookieHeaderForUrl(url, referrer) {
  try {
    const cookieMap = new Map();
    const urls = [url];
    if (referrer && /^https?:\/\//i.test(referrer)) {
      urls.push(referrer);
    }
    
    for (const u of urls) {
      try {
        const cookies = await webext.cookies.getAll({ url: u });
        if (cookies) {
          for (const c of cookies) {
            cookieMap.set(c.name, c.value);
          }
        }
      } catch {}

      try {
        const parsed = new URL(u);
        const hostParts = parsed.hostname.split(".");
        while (hostParts.length >= 2) {
          const domain = hostParts.join(".");
          const domainCookies = await webext.cookies.getAll({ domain });
          if (domainCookies) {
            for (const c of domainCookies) {
              cookieMap.set(c.name, c.value);
            }
          }
          hostParts.shift();
        }
      } catch {}
    }

    if (cookieMap.size === 0) return null;
    return Array.from(cookieMap.entries()).map(([k, v]) => `${k}=${v}`).join("; ");
  } catch {
    return null;
  }
}

webext.downloads.onCreated.addListener(async (item) => {
  if (!captureEnabled) return;
  if (item.state && item.state !== "in_progress") return;
  if (item.startTime && (Date.now() - new Date(item.startTime).getTime()) > 10000) return;

  const downloadUrl = item.finalUrl || item.url;
  if (!downloadUrl || !/^https?:\/\//i.test(downloadUrl)) return;

  if (loopGuard.get(downloadUrl) > Date.now()) {
    loopGuard.delete(downloadUrl);
    return;
  }

  await checkWdm();
  if (!isWdmActive) return;

  loopGuard.set(downloadUrl, Date.now() + 15000);

  try {
    await webext.downloads.cancel(item.id);
    await webext.downloads.erase({ id: item.id }).catch(() => {});
  } catch {}

  try {
    const headers = {};
    headers["User-Agent"] = navigator.userAgent;
    const cookieHeader = await getCookieHeaderForUrl(downloadUrl, item.referrer);
    if (cookieHeader) {
      headers["Cookie"] = cookieHeader;
    }
    if (item.referrer) {
      headers["Referer"] = item.referrer;
    }
    await sendToWdm(downloadUrl, item.filename, item.referrer, headers);
  } catch (err) {
    console.warn("WDM handoff failed:", err);
  }
});

async function sendToWdm(url, filename, referrer, headers) {
  const response = await fetch(`${WDM_HOST}/download`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ url, fileName: filename || null, referer: referrer || null, headers: headers || {} }),
  });
  if (!response.ok) {
    throw new Error(`WDM responded ${response.status}`);
  }
}
