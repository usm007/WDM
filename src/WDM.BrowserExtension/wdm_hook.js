// WDM MAIN-world Hook — intercepts fetch/XHR in the page context to detect
// media streams that the isolated-world content script cannot see due to CSP.
// Injected by media_sniffer.js via a <script> tag (web_accessible_resources).
(function () {
  "use strict";
  if (window.__WDM_HOOK__) return;
  window.__WDM_HOOK__ = true;

  var MEDIA_EXTS = /\.(mp4|m4v|m4s|webm|mkv|avi|mov|flv|m4v|mp3|m4a|aac|ogg|opus|flac|wav)(\?|$)/i;
  var HLS_RE = /(\.m3u8|\/hls\/|[\?&]format=m3u8|[\?&]ext=m3u8|mime=.*mpegurl)/i;
  var DASH_RE = /(\.mpd|\/dash\/|[\?&]format=mpd|[\?&]ext=mpd|mime=.*dash)/i;
  var STREAM_URL_RE = /(\.m3u8|\.mpd|\.mp4|\.webm|\/manifest|\/playlist|\/master\.|\/stream\b)/i;

  function notify(url, hint) {
    try {
      window.postMessage({ type: "WDM_HOOK_MEDIA", url: url, hint: hint }, "*");
    } catch {}
  }

  function classify(url) {
    if (HLS_RE.test(url)) return "HLS";
    if (DASH_RE.test(url)) return "DASH";
    if (MEDIA_EXTS.test(url)) return "Video";
    if (STREAM_URL_RE.test(url)) return "Stream";
    return null;
  }

  function checkUrl(url) {
    if (!url || typeof url !== "string") return;
    if (url.startsWith("data:") || url.startsWith("blob:")) return;
    var hint = classify(url);
    if (hint) notify(url, hint);
  }

  // Hook fetch()
  var origFetch = window.fetch;
  if (origFetch) {
    window.fetch = function () {
      try {
        var req = arguments[0];
        var url = typeof req === "string" ? req : (req && req.url);
        checkUrl(url);
      } catch {}
      return origFetch.apply(this, arguments);
    };
  }

  // Hook XMLHttpRequest.open()
  var origOpen = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function (method, url) {
    try {
      checkUrl(url);
    } catch {}
    return origOpen.apply(this, arguments);
  };
})();
