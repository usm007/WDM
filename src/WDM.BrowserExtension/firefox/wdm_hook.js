// WDM MAIN-world Hook — intercepts fetch/XHR in the page context to detect
// media streams that the isolated-world content script cannot see due to CSP.
// Injected by media_sniffer.js via a <script> tag (web_accessible_resources).
(function () {
  "use strict";
  if (window.__WDM_HOOK__) return;
  window.__WDM_HOOK__ = true;

  var VIDEO_FILE_RE = /\.(mp4|m4v|webm|mkv|avi|mov|flv)(\?|$)/i;
  var AUDIO_RE = /\.(mp3|m4a|aac|ogg|opus|flac|wav|wma)(\?|$)/i;
  var HLS_RE = /(\.m3u8|\/hls\/|\/playlist|\/manifest|\/master\.|\/stream\b|[\?&](format|ext)=m3u8|mime=.*mpegurl)/i;
  var DASH_RE = /(\.mpd|\/dash\/|\/manifest|\/master\.|[\?&](format|ext)=mpd|mime=.*dash)/i;

  function notify(url, hint) {
    try {
      var target = (window.location.origin && window.location.origin !== "null") ? window.location.origin : "*";
      window.postMessage({ type: "WDM_HOOK_MEDIA", url: url, hint: hint }, target);
    } catch {}
  }

  // Video only: audio is denied outright, and ambiguous stream-ish URLs get a
  // "Stream" hint so the content script routes them to background verification
  // instead of listing them blindly.
  function classify(url) {
    if (AUDIO_RE.test(url)) return null;
    if (/\.m3u8(\?|$)/i.test(url)) return "HLS";
    if (/\.mpd(\?|$)/i.test(url)) return "DASH";
    if (/\/dash\//i.test(url)) return "DASH";
    if (HLS_RE.test(url)) return "HLS";
    if (DASH_RE.test(url)) return "DASH";
    if (VIDEO_FILE_RE.test(url)) return "Video";
    if (/(\/manifest|\/playlist|\/master\.|\/stream\b)/i.test(url)) return "Stream";
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
