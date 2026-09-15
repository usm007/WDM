# Test Map

**FACT:** No test project, test files, or test runner exist in the repo (searched `**/*Test*.*`,
`*test*` configs — none; `.github/workflows/` empty). Verification is manual.

| What | How | Command / path |
|---|---|---|
| Build | `dotnet` | `dotnet build WDM.sln` |
| Run (dev) | `dotnet` / helper | `dotnet run --project src/WDM` or `RunWDM.bat` |
| Publish (self-contained) | dotnet | `dotnet publish src/WDM/WDM.csproj -c Release -r win-x64 --self-contained true -o publish` |
| Velopack pack | `vpk` CLI | `powershell -File build-velopack.ps1` (installs `vpk` if missing) |
| Firefox XPI | PS zip | `powershell -File build-xpi.ps1 [-SignedXpi <path>]` |
| Installer | Inno 6 | `ISCC.exe src\WDM.Setup\installer.iss` (publish to `staging\` first) |
| Capture E2E (manual) | browser + app | Load `src/WDM.BrowserExtension` unpacked (or `firefox/manifest.json` temp add-on); run app; download in browser → WDM Add dialog; `GET http://127.0.0.1:17530/ping` health |
| Media E2E (manual) | app | Paste YouTube/HLS URL; engines auto-provision to `%LocalAppData%\WDM-Data\bin` |
| Screenshots (headless) | app flag | `WDM.exe --capture-screenshots` (`ScreenshotGenerator.Run()`) |
| Update flow (manual) | app | Velopack path vs `UpdateChecker` fallback; silent launch must pass `--silent` alone |

**INFERENCE:** When adding tests in future, natural seams: `EmbedParsers` (pure functions),
`FileNameHelper`, `DownloadTask.Categorize/FormatBytes`, `SpeedGovernor`, `CaptureServer` payload
sanitizers (SSRF/CORS), `HlsDownloader` playlist selection. No harness exists yet — creating one is
greenfield work, not "add a test file".
