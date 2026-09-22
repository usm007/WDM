// WDM MAIN-world Hook — intercepts fetch/XHR in the page context to detect
// media streams that the isolated-world content script cannot see due to CSP.
// Injected by media_sniffer.js via a <script> tag (web_accessible_resources).
(function () {
  "use strict";
  if (window.__WDM_HOOK__) return;
  window.__WDM_HOOK__ = true;

  var VIDEO_FILE_RE = /\.(mp4|m4v|webm|mkv|avi|mov|flv)(\?|$)/i;
  var AUDIO_RE = /\.(mp3|m4a|aac|ogg|opus|flac|wav|wma)(\?|$)/i;
  var HLS_RE = /(\.m3u8|\/hls\/|\/playlist(?=[\/?#]|$)|\/manifest(?=[\/?#]|$)|\/master(?=[\/?#]|$)|\/stream\b|[\?&](format|ext)=m3u8|mime=.*mpegurl)/i;
  var DASH_RE = /(\.mpd|\/dash\/|\/manifest(?=[\/?#]|$)|\/master(?=[\/?#]|$)|[\?&](format|ext)=mpd|mime=.*dash)/i;

  function notify(url, hint) {
    try {
      var target = (window.location.origin && window.location.origin !== "null") ? window.location.origin : "*";
      window.postMessage({ type: "WDM_HOOK_MEDIA", url: url, hint: hint }, target);
    } catch {}
  }

  function notifyBlob(url) {
    try {
      var target = (window.location.origin && window.location.origin !== "null") ? window.location.origin : "*";
      window.postMessage({ type: "WDM_BLOB_MEDIA", url: url }, target);
    } catch {}
  }

  // MEGA session id (1DM sid-via-JS equivalent): page localStorage is only
  // readable here in the MAIN world. Re-sent periodically — login may happen
  // after page load, and the desktop store overwrites idempotently.
  function reportMegaSid() {
    try {
      var h = window.location.hostname || "";
      if (!/(^|\.)mega\.(nz|co\.nz)$/i.test(h)) return;
      var sid = null;
      try {
        sid = window.localStorage.getItem("sid") || window.localStorage.getItem("u_sid") || window.localStorage.getItem("m_sid");
      } catch (e) {}
      if (sid && typeof sid === "string" && sid.length >= 8) {
        var target = (window.location.origin && window.location.origin !== "null") ? window.location.origin : "*";
        window.postMessage({ type: "WDM_MEGA_SID", sid: sid }, target);
      }
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
    if (/(\/manifest|\/playlist|\/master)(?=[\/?#]|$)|\/stream\b/i.test(url)) return "Stream";
    return null;
  }

  function checkUrl(url) {
    if (!url || typeof url !== "string") return;
    if (url.startsWith("data:")) return;
    // Page-local blob: only the page context can read it — hand to the
    // content script for click-to-download (bytes never auto-exfiltrate).
    if (url.startsWith("blob:")) { notifyBlob(url); return; }
    var hint = classify(url);
    if (hint) notify(url, hint);
  }

  // 1DM handleM3u8Url equivalent: a playlist behind an opaque (tokenized)
  // URL still starts with #EXTM3U. Header check first (cheap), then a capped
  // text peek. Only unclassified URLs — classified ones already notified.
  function sniffBody(url, getText, getCtype) {
    try {
      if (!url || typeof url !== "string" || !/^https?:\/\//i.test(url)) return;
      if (AUDIO_RE.test(url) || classify(url)) return;
      var ct = "";
      try { ct = getCtype ? (getCtype() || "") : ""; } catch (e) {}
      if (/mpegurl|m3u8/i.test(ct)) { notify(url, "HLS"); return; }
      if (/dash\+xml/i.test(ct)) { notify(url, "DASH"); return; }
      if (ct && !/text\/|json|javascript|ecmascript/i.test(ct)) return;
      try {
        getText(function (text) {
          try {
            if (typeof text !== "string" || text.length < 7) return;
            var head = text.slice(0, 512).replace(/^[\s\uFEFF]*/, "");
            if (head.indexOf("#EXTM3U") === 0) notify(url, "HLS");
          } catch (e) {}
        });
      } catch (e) {}
    } catch (e) {}
  }

  // Hook fetch()
  var origFetch = window.fetch;
  if (origFetch) {
    window.fetch = function () {
      var reqUrl = null;
      try {
        var req = arguments[0];
        reqUrl = typeof req === "string" ? req : (req && req.url);
        checkUrl(reqUrl);
      } catch {}
      var promise = origFetch.apply(this, arguments);
      // Response-body sniff for opaque URLs (page context can read them).
      (function (seenUrl, p) {
        try {
          p.then(function (resp) {
            try {
              if (!resp || resp.type === "opaque") return resp;
              // ponytail: bodies over 256 KiB are never buffered for a peek;
              // switch to a streaming reader if a large manifest ever needs it.
              try {
                var clen = resp.headers ? resp.headers.get("content-length") : null;
                if (clen && parseInt(clen, 10) > 262144) return resp;
              } catch (e) {}
              sniffBody(seenUrl,
                function (cb) {
                  try { resp.clone().text().then(cb).catch(function () {}); } catch (e) {}
                },
                function () {
                  try { return (resp.headers && resp.headers.get("content-type")) || ""; } catch (e) { return ""; }
                });
            } catch (e) {}
            return resp;
          }).catch(function () {});
        } catch (e) {}
      })(reqUrl, promise);
      return promise;
    };
  }

  // Hook XMLHttpRequest.open()
  var origOpen = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function (method, url) {
    try {
      checkUrl(url);
      // Same-origin XHR exposes responseText on load — same sniff as fetch.
      if (typeof url === "string") {
        try {
          var xhr = this, sniffUrl = url;
          xhr.addEventListener("load", function () {
            try {
              sniffBody(sniffUrl, function (cb) {
                var t = null;
                try { t = xhr.responseText; } catch (e) { return; }
                cb(t);
              }, function () {
                try { return xhr.getResponseHeader("content-type") || ""; } catch (e) { return ""; }
              });
            } catch (e) {}
          });
        } catch (e) {}
      }
    } catch {}
    return origOpen.apply(this, arguments);
  };

  // MEGA sid: once shortly after install (late login) + every 2 minutes.
  try {
    setTimeout(reportMegaSid, 3000);
    setInterval(reportMegaSid, 120000);
  } catch {}
})();
