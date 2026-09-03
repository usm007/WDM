# Investigation: browser cookie handoff & the failing `https://link.testfile.org/500MB` download

**Date:** 2026-08-25 · **Status:** fixes applied, one open issue (see §5)

## 1. Symptom

A download started from the browser for `https://link.testfile.org/500MB` fails in WDM.

## 2. What the link actually is

`link.testfile.org` sits behind a **Cloudflare bot challenge**. Verified with curl:

```
HTTP/1.1 403 Forbidden
Cf-Mitigated: challenge
Server: cloudflare
```

A real browser silently solves the challenge and receives a `cf_clearance` cookie.
**Cloudflare validates that cookie against the exact User-Agent (and IP) that solved
the challenge.** Any request with the cookie but a different UA gets a fresh 403.

## 3. Root causes found

### 3.1 The extension never forwarded the browser's User-Agent

`background.js` captured cookies for the download URL but not the UA. WDM then sent
its hardcoded default UA (`Chrome/125`), which cannot match the user's actual browser
in 2026 → `cf_clearance` rejected → 403 challenge → WDM fails the task with the
"Cloudflare blocked this download" error.

### 3.2 Engine bug: a task-supplied User-Agent would have been mangled

`DownloadEngine.BuildRequest` applied custom headers via
`TryAddWithoutValidation("User-Agent", …)`, which tokenizes the value (a test showed
one UA string split into **7 separate header values**), *and* the client-level
default UA was sent alongside it — two broken UA headers on the wire.

### 3.3 Extension only captured cookies for the download URL's own domain

Redirector/CDN download URLs often carry no cookies of their own; the session that
authorizes the download frequently lives on the referring page's origin.

## 4. Verified as NOT a problem

- **Cookie propagation across redirects works.** A local echo test replicating the
  engine's `SocketsHttpHandler` config (`AllowAutoRedirect = true`, raw `Cookie`
  header via `TryAddWithoutValidation`) confirmed the Cookie header is re-sent on the
  redirected request (`/start` → 302 → `/file` both received it).
- The capture → `CaptureServer` → pre-filled Add-dialog → `task.Headers` → `BuildRequest`
  path is intact; prefill headers (incl. Cookie) land in the task.

## 5. Fixes applied

| File | Change |
|---|---|
| `src/WDM.BrowserExtension/background.js` | Send `navigator.userAgent` as a `User-Agent` header; merge cookies for the download URL **and** the referrer origin (URL-domain wins on name conflicts). |
| `src/WDM.BrowserExtension/firefox/background.js` | Same two changes for the Firefox variant. |
| `src/WDM/Services/DownloadEngine.cs` | Removed client-level default headers. `BuildRequest` now sets **exactly one** UA per request: the task's UA when present (case-insensitive lookup via new `TaskUserAgent` helper), `DefaultUserAgent` constant otherwise. The generic custom-header loop skips `User-Agent`. HLS downloads get a per-download `HttpClient` wrapping the shared handler with a `UserAgentHandler` that stamps the task's UA on every segment/playlist/key request. |

## 6. Verification status

- **Echo test (fixed logic): PASS** — exactly one `User-Agent` header equal to the
  task's UA, one `Cookie` header with the captured cookies, correct `Referer`.
- **End-to-end capture flow: PARTIAL** — POSTing an extension-style payload
  (`url`, `referer`, `headers{User-Agent, Cookie}`) to `127.0.0.1:17530/download`
  opened the pre-filled Add dialog; Enter (default button) created and started the
  task (status `Downloading`).

## 7. Open issue

In the end-to-end run the **engine's own requests never reached the local test
server** — only the Add-dialog's cosmetic URL probe did (that probe uses its own
throwaway `HttpClient` with UA `…WDM/1.0`, no cookies/referer; see
`AddDownloadDialog.ProbeUrlAsync`). The task stayed in `Downloading` with
`TotalBytes = -1` and no engine request observed on the wire within 45 s.

Next steps:

1. Re-run the E2E with a longer window and `wdm_error.log` capture; the engine probe
   may simply still have been inside its first 60 s `HttpClient` timeout/retry cycle.
2. Check `RunSessionAsync`/`ProbeAsync` for a pre-wire stall (dispatcher deadlock or
   handler-level block) — the dialog probe proves loopback connectivity was fine.
3. Consider making the dialog probe reuse the task's captured headers + UA, or label
   it as informational only — for cookie-gated URLs its "URL ready" badge can lie.
4. Full CF pass can only be proven with a real `cf_clearance` from the user's browser
   (capture the download once with the updated extension and confirm the task lives).

## 8. Repro / test artifacts

- curl probe of the URL (403 challenge) — §2
- Local redirect echo test + fixed-`BuildRequest` echo test — §4, §6
- E2E harness: `POST /download` → Add dialog → Enter → poll `tasks.json`
  (scripts kept in `%TEMP%\opencode\`, not committed)
