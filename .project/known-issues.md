# Known Issues

- **FACT (working tree, uncommitted 2026-09-14):** `src/WDM/Services/UpdateChecker.cs` modified —
  `LaunchInstaller` silent args changed `/VERYSILENT /SUPPRESSMSGBOXES --silent` → `--silent` + doc
  comment. INTENT per diff: Velopack bundle needs clap-style `--silent` alone. Verify installer behavior
  before commit; do not revert blindly.
- **FACT (`docs/cookie-handoff-investigation.md`, open):** E2E `POST 127.0.0.1:17530/download` reached
  the Add dialog but engine request unobserved in 45s. If capture "works" but nothing downloads, start here.
- **FACT (README caveats):** Resume needs server range support; Windows-only by design.
- **FACT (`build-xpi.ps1`):** self-built XPI is unsigned — won't install in release Firefox; needs AMO
  signing (`-SignedXpi` passthrough exists).
- **INFERENCE (`stream_catch.md` gaps):** tokenized manifests without literal `.m3u8`, `POST /api/stream`
  JSON bodies (`streaming_url` in response), `blob:`/MSE, Widevine are extension misses;
  `HlsDownloader` depth<3 / best-variant-only / AES-128-only / `.ts` output are engine limits.
  Confirm against current source before quoting — file is a working note.
- **UNCERTAINTY:** `.github/workflows/` is empty — CI coverage unknown; assume none.
- **UNCERTAINTY:** `crash.log` / `stdout.txt` / `stderr.txt` at root are gitignored local debug output;
  may be stale — check timestamps before using for diagnosis.
