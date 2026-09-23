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

  // MSE recording (gap 5): URL-less players feed bytes via SourceBuffer, so
  // accumulate appendBuffer payloads per MediaSource and mint a Blob URL the
  // sniffer lists like any blob — download happens only on user click.
  // ponytail: 1 GiB cap per MediaSource, then stop (memory ceiling); raw
  // concat, no transmux (init+segments play for fMP4/TS).
  try {
    var MSE_CAP = 1024 * 1024 * 1024;
    var MSE_REMINT = 1024 * 1024;
    var mseStores = new WeakMap();
    function mseStore(ms) {
      var st = mseStores.get(ms);
      if (!st) { st = { mime: "", parts: [], bytes: 0, url: null, minted: 0, done: false }; mseStores.set(ms, st); }
      return st;
    }
    function mseMint(ms, st) {
      if (!ms || !st || st.done || st.bytes - st.minted < MSE_REMINT) return;
      try {
        var url = URL.createObjectURL(new Blob(st.parts, { type: st.mime || "video/mp4" }));
        if (st.url) { try { URL.revokeObjectURL(st.url); } catch (e) {} }
        st.url = url;
        st.minted = st.bytes;
        notifyBlob(url);
      } catch (e) {}
    }
    if (window.MediaSource && MediaSource.prototype && MediaSource.prototype.addSourceBuffer) {
      var origAddSB = MediaSource.prototype.addSourceBuffer;
      MediaSource.prototype.addSourceBuffer = function (mime) {
        var sb = origAddSB.apply(this, arguments);
        try {
          var ms = this, st = mseStore(ms);
          if (!st.mime && typeof mime === "string") st.mime = mime.split(";")[0];
          var origAppend = sb.appendBuffer.bind(sb);
          sb.appendBuffer = function (data) {
            try {
              if (!st.done && st.bytes < MSE_CAP && data) {
                var copy = null;
                if (data instanceof ArrayBuffer) copy = data.slice(0);
                else if (data && data.buffer instanceof ArrayBuffer) copy = data.buffer.slice(data.byteOffset || 0, (data.byteOffset || 0) + (data.byteLength || 0));
                if (copy && copy.byteLength) {
                  if (st.bytes + copy.byteLength > MSE_CAP) st.done = true;
                  else { st.parts.push(copy); st.bytes += copy.byteLength; }
                }
              }
            } catch (e) {}
            return origAppend(data);
          };
          sb.addEventListener("updateend", function () { try { mseMint(ms, st); } catch (e) {} });
          if (ms.addEventListener) ms.addEventListener("sourceended", function () { try { mseMint(ms, st); } catch (e) {} });
        } catch (e) {}
        return sb;
      };
    }
  } catch {}

  // MEGA sid: once shortly after install (late login) + every 2 minutes.
  try {
    setTimeout(reportMegaSid, 3000);
    setInterval(reportMegaSid, 120000);
  } catch {}
})();
