// sniffer_probe.js — executes the REAL extension JS (media_sniffer.js +
// wdm_hook.js) under minimal DOM stubs, drives fetch/XHR/hook vectors and
// reports which media the sniffer classified. No browser needed; run with:
//   node sniffer_probe.js <path-to-src/WDM.BrowserExtension>
// Exit 0 = every check passed. Stdout = JSON {checks, detected, hookPosts}.
'use strict';
const fs = require('fs');
const path = require('path');

const extDir = process.argv[2];
if (!extDir) { console.error('usage: node sniffer_probe.js <extension-dir>'); process.exit(2); }

const checks = [];
const check = (name, pass, detail) => checks.push({ name, pass: !!pass, detail: detail || '' });

// ---- minimal DOM stubs (just enough for the sniffer IIFE to boot) ----
function makeElement() {
  return {
    style: {}, children: [],
    setAttribute() {}, appendChild() {}, addEventListener() {},
    querySelector() { return null; }, querySelectorAll() { return []; },
    remove() {}, matches() { return false; },
  };
}
const detected = [];   // mediaDetected streams seen by the background stub
const verifyReqs = []; // verifyMedia requests (opaque urls)
let msgListener = null;

const snifferWindow = {
  fetch: () => Promise.resolve({ clone: () => ({ text: () => Promise.resolve('') }) }),
  addEventListener: (type, fn) => { if (type === 'message') msgListener = fn; },
  postMessage: () => {},
};
global.window = snifferWindow;
global.location = { hostname: 'videos.test', href: 'https://videos.test/watch/1', origin: 'https://videos.test' };
global.document = {
  readyState: 'complete', title: 'Watch My Film - SomeSite',
  head: { appendChild() {} }, documentElement: { appendChild() {} },
  createElement: () => makeElement(), getElementById: () => null,
  querySelectorAll: () => [], addEventListener() {},
  createTreeWalker: () => ({ nextNode: () => null }),
};
global.NodeFilter = { SHOW_ELEMENT: 1 };
global.MutationObserver = class { observe() {} disconnect() {} };
global.setInterval = () => 0;   // keep node from hanging; intervals aren't under test
global.setTimeout = () => 0;
class XHRStub {
  open(method, url) { (this._opens || (this._opens = [])).push({ method, url }); }
  addEventListener() {} send() {}
}
global.XMLHttpRequest = XHRStub;
global.chrome = {
  runtime: {
    getURL: (f) => f, lastError: undefined, onMessage: { addListener() {} },
    sendMessage: (msg, cb) => {
      if (msg && msg.action === 'mediaDetected' && msg.stream) detected.push(msg.stream);
      else if (msg && msg.action === 'verifyMedia') verifyReqs.push(msg.url);
      const res = msg && msg.action === 'ping' ? { active: true } : { received: true, success: true };
      if (typeof cb === 'function') { try { cb(res); } catch {} }
      return Promise.resolve(res);
    },
  },
  tabs: { sendMessage: () => Promise.resolve(), get: () => Promise.resolve({}) },
  action: { setBadgeText() {}, setBadgeBackgroundColor() {} },
  storage: { local: { get: () => Promise.resolve({}), set: () => Promise.resolve() }, onChanged: { addListener() {} } },
};

const tick = () => new Promise((r) => setImmediate(r));
const has = (url, type) => detected.find((d) => d.url === url && (!type || d.type === type));

(async () => {
  try {
    eval(fs.readFileSync(path.join(extDir, 'media_sniffer.js'), 'utf8')); // eslint-disable-line no-eval

    await snifferWindow.fetch('https://cdn.test/hls/master.m3u8');
    await snifferWindow.fetch('https://cdn.test/video/movie-720p.mp4');
    await snifferWindow.fetch('https://cdn.test/audio/song.mp3'); // must be denied
    new XMLHttpRequest().open('GET', 'https://cdn.test/dash/manifest.mpd');
    if (!msgListener) throw new Error('sniffer did not register its window message bridge');
    msgListener({ source: snifferWindow, origin: 'https://videos.test', data: { type: 'WDM_HOOK_MEDIA', url: 'https://cdn.test/hook/stream.m3u8?token=abc', hint: 'HLS' } });
    msgListener({ source: snifferWindow, origin: 'https://videos.test', data: { type: 'WDM_BLOB_MEDIA', url: 'blob:https://videos.test/550e8400-e29b-41d4' } });
    await tick(); await tick();

    check('fetch .m3u8 -> HLS mediaDetected', !!has('https://cdn.test/hls/master.m3u8', 'HLS'), JSON.stringify(detected));
    check('fetch .mp4 -> Video mediaDetected', !!has('https://cdn.test/video/movie-720p.mp4', 'Video'));
    check('fetch .mp3 denied (audio never listed)', !detected.some((d) => d.url.includes('song.mp3')) && !verifyReqs.some((u) => u.includes('song.mp3')));
    check('XHR .mpd -> DASH mediaDetected', !!has('https://cdn.test/dash/manifest.mpd', 'DASH'));
    check('MAIN-hook bridge message -> HLS registered', !!has('https://cdn.test/hook/stream.m3u8?token=abc', 'HLS'));
    const blob = detected.find((d) => d.url.startsWith('blob:'));
    check('same-origin blob registered as Video with page-title label', !!blob && blob.type === 'Video' && blob.label === 'My Film', JSON.stringify(blob));
    check('exactly 5 streams detected (no dupes, no audio)', detected.length === 5, `count=${detected.length}`);
    check('generic basename labeled from page title, not master.m3u8',
      (has('https://cdn.test/hls/master.m3u8') || {}).label === 'My Film');

    // ---- wdm_hook.js (MAIN world): fresh window, its postMessage is the signal ----
    const hookPosts = [];
    const hookWindow = {
      location: { origin: 'https://videos.test', hostname: 'videos.test' },
      postMessage: (m) => hookPosts.push(m),
      fetch: () => Promise.resolve({}),
    };
    global.window = hookWindow;
    eval(fs.readFileSync(path.join(extDir, 'wdm_hook.js'), 'utf8')); // eslint-disable-line no-eval
    await hookWindow.fetch('https://cdn.test/hls/master.m3u8');
    await hookWindow.fetch('https://cdn.test/hls/low.m3u8?token=abc&exp=9');
    await hookWindow.fetch('https://cdn.test/audio/song.mp3');
    new XMLHttpRequest().open('GET', 'https://cdn.test/player/stream/42');
    await tick();

    const hookMedia = hookPosts.filter((m) => m && m.type === 'WDM_HOOK_MEDIA');
    check('hook: fetch .m3u8 posts WDM_HOOK_MEDIA/HLS',
      hookMedia.some((m) => m.url === 'https://cdn.test/hls/master.m3u8' && m.hint === 'HLS'), JSON.stringify(hookMedia));
    check('hook: tokenized query string preserved verbatim',
      hookMedia.some((m) => m.url === 'https://cdn.test/hls/low.m3u8?token=abc&exp=9' && m.hint === 'HLS'));
    check('hook: fetch .mp3 posts nothing', !hookMedia.some((m) => m.url.includes('song.mp3')));
    // Truthful note: the "Stream" fallback in classify() is shadowed — HLS_RE
    // already covers /stream, so such URLs arrive as HLS, not "Stream".
    check('hook: /stream path classified HLS (Stream fallback shadowed by HLS_RE)',
      hookMedia.some((m) => m.url === 'https://cdn.test/player/stream/42' && m.hint === 'HLS'));

    const failed = checks.filter((c) => !c.pass);
    console.log(JSON.stringify({ checks, detected, hookPosts }));
    if (failed.length > 0) { console.error('FAILED: ' + failed.map((f) => f.name).join('; ')); process.exit(1); }
    process.exit(0);
  } catch (e) {
    console.error('probe crashed: ' + (e && e.stack || e));
    process.exit(1);
  }
})();
