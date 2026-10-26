// Extension smoke harness — exercises the WDM background worker under a
// stubbed WebExtension API. No browser required.
//
//   node scripts/extension-smoke.js            # Chrome + Firefox variants
//   node scripts/extension-smoke.js <file.js>  # one variant
//   node scripts/extension-smoke.js --offline  # WDM closed: queue/notify path
//
// It verifies what the right-click surface must do: create the menu entries,
// hand a link/media URL to WDM with the page's referer and cookies, queue (or
// report) when WDM is closed, and flip a site on/off in the same block list
// automatic capture honours.
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const REPO = path.resolve(__dirname, "..");
const VARIANTS = [
  path.join(REPO, "src", "WDM.BrowserExtension", "background.js"),
  path.join(REPO, "src", "WDM.BrowserExtension", "firefox", "background.js"),
];

const args = process.argv.slice(2);
const offline = args.includes("--offline");
const explicit = args.filter((a) => !a.startsWith("--"));
const files = explicit.length > 0 ? explicit : VARIANTS;

const listeners = new Map();
const menuItems = [];
const notifications = [];
const requests = [];
const tabMessages = [];
const storage = {};

const realSetTimeout = require("timers").setTimeout;
const wait = () => new Promise((r) => realSetTimeout(r, 20));

let failures = 0;
function fail(msg) {
  failures++;
  console.error("  FAIL: " + msg);
}
function ok(msg) {
  console.log("  ok - " + msg);
}

function ev(name) {
  if (!listeners.has(name)) listeners.set(name, []);
  const list = listeners.get(name);
  return { addListener: (fn) => list.push(fn), removeListener: () => {}, hasListener: () => list.length > 0 };
}

function res(status = 200, body = {}) {
  return { ok: status >= 200 && status < 300, status, json: async () => body, text: async () => JSON.stringify(body) };
}

async function fetchStub(url, options) {
  const href = String(url);
  requests.push({ url: href, options: options || {} });
  if (offline) {
    if (href.endsWith("/ping")) return res(503, {});
    throw new Error("offline");
  }
  if (href.endsWith("/ping")) {
    return res(200, { status: "ok", minCatchBytes: 0, catchTypes: [], skipTypes: [], altSkip: true });
  }
  if (href.endsWith("wdm-session-hosts.json")) return res(200, { hosts: ["site.example"] });
  if (href.endsWith("/download")) return res(200, { accepted: 1 });
  return res(200, {});
}

function makeApi() {
  return {
    runtime: {
      getURL: (p) => "chrome-extension://wdm-test/" + p,
      getManifest: () => ({ version: "1.5.0" }),
      onMessage: ev("runtime.onMessage"),
      onInstalled: ev("runtime.onInstalled"),
      onStartup: ev("runtime.onStartup"),
      sendMessage: async () => ({ blocked: { domains: [], urls: [] } }),
    },
    storage: {
      local: {
        get: async (key) => {
          if (typeof key === "string") return { [key]: storage[key] };
          if (Array.isArray(key)) return Object.fromEntries(key.map((k) => [k, storage[k]]));
          return Object.assign({}, storage);
        },
        set: async (obj) => { Object.assign(storage, obj); },
        remove: async (k) => { delete storage[k]; },
      },
      onChanged: ev("storage.onChanged"),
    },
    contextMenus: {
      create: (props) => { menuItems.push(props); return props.id; },
      update: async () => {},
      remove: async () => {},
      removeAll: (cb) => { menuItems.length = 0; if (typeof cb === "function") cb(); return Promise.resolve(); },
      onClicked: ev("contextMenus.onClicked"),
    },
    tabs: {
      query: async () => [{ id: 1, url: "https://site.example/page", title: "Example Page" }],
      get: async (id) => ({ id, url: "https://site.example/page", title: "Example Page" }),
      sendMessage: async (id, msg) => { tabMessages.push({ id, msg }); return {}; },
      onActivated: ev("tabs.onActivated"),
      onUpdated: ev("tabs.onUpdated"),
      onRemoved: ev("tabs.onRemoved"),
    },
    action: {
      setTitle: async () => {},
      setBadgeText: async () => {},
      setBadgeBackgroundColor: async () => {},
      onClicked: ev("action.onClicked"),
    },
    downloads: {
      onCreated: ev("downloads.onCreated"),
      pause: async () => {},
      resume: async () => {},
      cancel: async () => {},
      erase: async () => {},
      search: async () => [],
    },
    webRequest: {
      onSendHeaders: ev("webRequest.onSendHeaders"),
      onHeadersReceived: ev("webRequest.onHeadersReceived"),
      onBeforeRequest: ev("webRequest.onBeforeRequest"),
    },
    webNavigation: { onCommitted: ev("webNavigation.onCommitted") },
    proxy: { settings: { onChange: ev("proxy.settings.onChange"), get: async () => ({}) } },
    notifications: {
      create: (opts) => { notifications.push(opts); return "n1"; },
      clear: async () => {},
    },
    cookies: { getAll: async () => [{ name: "session", value: "abc123" }] },
  };
}

async function runOne(file) {
  console.log(path.relative(REPO, file) + (offline ? "  [offline]" : ""));
  listeners.clear();
  menuItems.length = 0;
  notifications.length = 0;
  requests.length = 0;
  tabMessages.length = 0;
  for (const k of Object.keys(storage)) delete storage[k];

  const api = makeApi();
  const sandbox = {
    chrome: api,
    browser: api,
    console: { log: () => {}, warn: () => {}, error: () => {} },
    fetch: fetchStub,
    navigator: { userAgent: "SmokeUA/1.0", language: "en-US" },
    location: { hostname: "site.example", href: "https://site.example/page" },
    URL,
    URLSearchParams,
    Headers,
    TextEncoder,
    TextDecoder,
    AbortController,
    setTimeout: () => 0,
    clearTimeout: () => {},
    setInterval: () => 0,
    clearInterval: () => {},
    queueMicrotask,
    Promise,
    Map,
    Set,
    WeakMap,
    Date,
    Math,
    JSON,
    btoa: (s) => Buffer.from(s, "binary").toString("base64"),
    atob: (s) => Buffer.from(s, "base64").toString("binary"),
    Blob: class {},
    performance: { now: () => Date.now() },
  };
  sandbox.globalThis = sandbox;
  sandbox.self = sandbox;

  try {
    vm.createContext(sandbox);
    vm.runInContext(fs.readFileSync(file, "utf8"), sandbox, { filename: file });
  } catch (err) {
    fail("worker threw while loading: " + (err && err.message ? err.message : err));
    return;
  }

  const ids = menuItems.map((m) => m.id).filter(Boolean);
  for (const want of ["wdm-menu-download-link", "wdm-menu-download-media", "wdm-menu-site-toggle"]) {
    if (!ids.includes(want)) fail("menu item missing: " + want);
    else ok("menu item created: " + want);
  }
  const linkMenu = menuItems.find((m) => m.id === "wdm-menu-download-link");
  if (linkMenu && linkMenu.title !== "Download link with WDM") fail("link menu title: " + linkMenu.title);

  const clickers = listeners.get("contextMenus.onClicked") || [];
  if (clickers.length === 0) {
    fail("no contextMenus.onClicked listener");
    return;
  }
  ok("contextMenus.onClicked listener registered");
  const click = (info) => clickers[0](info, { id: 1, url: info.pageUrl, title: "Example Page" });

  // 1. Right-click a link: the page's referer and cookies travel with it.
  requests.length = 0;
  click({ menuItemId: "wdm-menu-download-link", linkUrl: "https://cdn.example/file.zip", pageUrl: "https://site.example/page" });
  await wait();

  const download = requests.find((r) => r.url.endsWith("/download"));
  if (offline) {
    if (download) fail("offline: nothing should reach /download");
    const last = notifications[notifications.length - 1];
    if (!last || !/not running/i.test(last.message)) fail("offline: no 'not running' notice: " + JSON.stringify(last));
    else ok("offline: user is told WDM is not running");
    if (Array.isArray(storage.wdmOutbox)) {
      if (storage.wdmOutbox.length !== 1) fail("offline: handoff not queued: " + JSON.stringify(storage.wdmOutbox));
      else ok("offline: handoff queued for when WDM starts");
    }
  } else if (!download) {
    fail("no /download request for a link click");
  } else {
    ok("link click posts /download");
    let body = null;
    try { body = JSON.parse(download.options.body); } catch { fail("body is not JSON"); }
    if (body) {
      if (body.url !== "https://cdn.example/file.zip") fail("url mismatch: " + body.url);
      if (body.referer !== "https://site.example/page") fail("referer mismatch: " + body.referer);
      const headers = body.headers || {};
      if (headers["User-Agent"] !== "SmokeUA/1.0") fail("user-agent mismatch: " + headers["User-Agent"]);
      if (!headers["Cookie"] || !headers["Cookie"].includes("session=abc123")) fail("cookie not forwarded: " + headers["Cookie"]);
      if (headers["Referer"] !== "https://site.example/page") fail("referer header mismatch: " + headers["Referer"]);
      if (body.fullSession !== true) fail("approved site should carry fullSession");
      ok("payload carries url, referer, one UA, cookie, fullSession");
    }
    if (notifications.length === 0) fail("no notification on success");
    else ok("success is reported: " + notifications[notifications.length - 1].message);
  }

  // 2. Site switch: off, then on again, in the automatic-catch block list.
  notifications.length = 0;
  click({ menuItemId: "wdm-menu-site-toggle", pageUrl: "https://site.example/page" });
  await wait();
  const blocked = storage.wdmBlocked;
  if (!blocked || !Array.isArray(blocked.domains) || !blocked.domains.includes("site.example")) {
    fail("site switch did not persist the domain: " + JSON.stringify(blocked));
  } else {
    ok("site switch persists site.example as disabled");
  }
  if (!tabMessages.some((m) => m.msg && m.msg.action === "wdmSiteOff")) fail("open tabs were not told the site is off");
  else ok("open tabs told the site is off");
  const off = notifications[notifications.length - 1];
  if (!off || !/stay quiet/i.test(off.message)) fail("disable notification missing: " + JSON.stringify(off));
  else ok("disable is reported to the user");

  click({ menuItemId: "wdm-menu-site-toggle", pageUrl: "https://site.example/page" });
  await wait();
  const again = storage.wdmBlocked;
  if (!again || again.domains.includes("site.example")) fail("site switch did not re-enable: " + JSON.stringify(again));
  else ok("site switch re-enables the site");
  if (!tabMessages.some((m) => m.msg && m.msg.action === "wdmSiteOn")) fail("open tabs were not told the site is on");
  else ok("open tabs told the site is on");

  // 3. Right-click a media element.
  requests.length = 0;
  click({ menuItemId: "wdm-menu-download-media", srcUrl: "https://cdn.example/movie.mp4", pageUrl: "https://site.example/watch" });
  await wait();
  const media = requests.find((r) => r.url.endsWith("/download"));
  if (offline) {
    if (media) fail("offline: media click should not reach /download");
    else ok("offline: media click is queued instead");
  } else if (!media) {
    fail("no /download request for a media click");
  } else {
    ok("media click posts /download");
  }

  // 4. A link pointing at the already-open page is not a file.
  requests.length = 0;
  click({ menuItemId: "wdm-menu-download-link", linkUrl: "https://site.example/page", pageUrl: "https://site.example/page" });
  await wait();
  if (requests.some((r) => r.url.endsWith("/download"))) fail("self-link should not be handed off");
  else ok("self-link is ignored");
}

(async () => {
  for (const file of files) {
    if (!fs.existsSync(file)) {
      fail("missing file: " + file);
      continue;
    }
    await runOne(path.resolve(file));
  }
  if (failures > 0) {
    console.error("\n" + failures + " check(s) failed");
    process.exit(1);
  }
  console.log("\nall extension smoke checks passed");
})();
