# Decisions (ADR-lite)

1. **Self-contained .NET 8, Windows-only.** FACT: `--self-contained true` default (`build-velopack.ps1`);
   README: "no runtime install needed". INFERENCE: accepts ~70–150MB artifacts to kill install friction.
2. **Chunk-pool engine, not fixed segments.** FACT: shared counter hands next incomplete chunk to any
   free worker (`DownloadEngine`), `*.wdmstate` bitmap survives restarts. INFERENCE: optimizes for
   uneven segment speeds vs. naive N-way split.
3. **yt-dlp + ffmpeg instead of in-house extractors.** FACT: `MediaResolver/YtDlpRunner/EngineManager`.
   INFERENCE: trades binary-management complexity (seed/download/cache) for site coverage.
4. **Loopback capture server (`:17530`) as browser bridge.** FACT: `CaptureServer` TCP + extension POST.
   INFERENCE: avoids native-messaging registry complexity; pays CORS/SSRF hardening cost instead.
5. **Velopack delta preferred, Inno full fallback.** FACT: `VelopackUpdateService` (delta ~0.2–5MB,
   `MaximumDeltasBeforeFallback=1`, flavor guard) → `UpdateChecker` full exe. INFERENCE: hybrid
   releases serve both (`WDM_Setup_*` + `*-full/delta.nupkg` + `RELEASES`/`releases.win.json`).
6. **Separate data dir (`WDM-Data`) never wiped.** FACT: `27d9c95` + `installer.iss` comments; migration
   from legacy `%LocalAppData%\WDM`. INFERENCE: update/install bugs around data loss drove this.
7. **Silent update = `--silent` alone.** FACT: uncommitted change to `UpdateChecker.LaunchInstaller`
   (working tree, 2026-09-14): Velopack bundle uses clap-style parsing; `/VERYSILENT` drops to
   interactive dialog. Do not "fix" by re-adding Inno flags.
8. **Generic embed parsers, no per-site code.** FACT: `EmbedParsers` shape-based (obfuscated-JSON,
   token-blob, pass_md5, AES-GCM). INFERENCE: from `stream_catch.md` playmate/vidwara work — keep generic.
9. **Single UA + forwarded browser context.** FACT: `docs/cookie-handoff-investigation.md` (2026-08-25):
   `Chrome/125` UA + merged cookies + referrer-origin fix after Cloudflare 403s. Preserve UA/cookie
   plumbing end-to-end when touching capture or request building.
