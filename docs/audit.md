# Orange — security, privacy and compatibility audit

Scope: Orange 0.1 (engine, CLI, WinUI app, installer). Each claim is tied to code and to a test; "verified" means an automated test passed on the stated platform. CI evidence for every run (logs, self-test JSON, drag-probe JSON, screenshots) is pushed to the `ci-evidence` branch.

## 1. Threat model

Input files are untrusted (downloads, attachments). The user is trusted. Attacker goals: run code via a crafted file or file name, write outside the chosen folder, overwrite or destroy user files, exhaust disk/memory/CPU, leak data off the machine.

## 2. Controls

| Risk | Control | Where | Test |
|---|---|---|---|
| Command injection via file names/options | No shell anywhere. Child processes get `ProcessStartInfo.ArgumentList`; NUL rejected; options validated against typed specs (ranges, choice lists) before use | `Tools.cs` ProcessRunner, `OptionValidator` | `Shell_metacharacters_in_names_are_just_names`, `Shell_metacharacters_reach_ffmpeg_safely` (`a & b; echo $(id) \`x\` %PATH% 'q'.wav`), `Option_validation_rejects_out_of_range_values` — verified Linux + Windows |
| Option injection into tools (a file named `-o x`) | All paths absolute; FFmpeg inputs prefixed `file:`; Pandoc gets `--` before the input | MediaProvider, DocumentProvider | covered by the metacharacter tests |
| FFmpeg protocol abuse (`concat:`, `http:`, HLS playlists inside media) | `-protocol_whitelist file,pipe`, `file:` prefix, `-nostdin` | MediaProvider | design review; truncated/hostile media test |
| ImageMagick coder abuse (MVG/MSL/SVG/URL renamed to .png — "ImageTragick") | Content signature must match the extension (else refused); every read names its decoder explicitly; SVG input disabled | FileTypeDetector, `ImageProvider.DecoderFor` | `Content_that_contradicts_extension_is_refused(mvg-disguised.png)` |
| Ghostscript PostScript abuse | Always `-dSAFER -dBATCH -dNOPAUSE`; input is a validated PDF | PdfProvider.Gs | PDF compress/render tests |
| Pandoc reading other files / network | `--sandbox` always on Windows. Linux dev builds lacking embedded data may opt out only with `ORANGE_ALLOW_UNSANDBOXED_PANDOC=1` | DocumentProvider | Pandoc tests ran sandboxed on Windows CI |
| Plain text interpreted as markup | TXT is HTML-escaped before Pandoc | DocumentProvider | `Plain_text_is_escaped_not_interpreted` |
| LibreOffice macros / profile leakage | Headless, throwaway `-env:UserInstallation` profile in the private temp dir, `--norestore`; macros not enabled | DocumentProvider | runs only where LibreOffice is installed (engine CI job) |
| Zip-slip / absolute / drive paths in archives | `..`, `:` and rooted entries stop extraction; each target re-checked with `IsInside` | ArchiveProvider | `Zip_slip_is_stopped_and_nothing_escapes` |
| Symlink/hardlink/device entries | Skipped | ArchiveProvider | `Tar_symlinks_are_skipped` |
| Zip bombs | Entry-count cap, total expanded-size cap, per-entry ratio cap (1000:1), streamed copy capped at declared size | ArchiveProvider, EngineLimits | `Zip_bomb_is_rejected` |
| Image decompression bombs | Magick.NET `ResourceLimits` (memory, area, width/height, time) + pixel cap checked after read | ImageProvider | design review |
| Runaway processes | Timeouts, cancellation kills the whole process tree, below-normal priority, stdout/stderr capped at 1 MB | ProcessRunner | `Cancellation_stops_ffmpeg` |
| Overwriting originals / other files | Output never equals any input; default *Keep both* appends " 2"; *Replace* only replaces a non-input file; results written to temp first, validated (re-decoded), then moved | SafePaths.PlanOutput/Commit | `Plans_tangerine_style_names_and_never_overwrites`, `Running_twice_never_overwrites` |
| Half-written outputs | Atomic move from temp after validation; failed/cancelled jobs leave nothing | SafePaths.Commit, providers' `Validate*` | `Cancellation_stops_ffmpeg` asserts no output |
| Temp leaks | Per-job private temp dirs, deleted in `finally`; stale dirs (>24 h) purged at start | SafePaths.TempWorkspace | `Temp_workspaces_are_removed` |
| Windows reserved names, trailing dots, invalid chars | Sanitised (`CON` → `_CON`) | SafePaths.SanitizeFileName | `Sanitizes_windows_names` |
| Damaged inputs | Actionable errors ("…looks damaged"), technical detail behind *Details* | providers | `Damaged_pdf…`, `Damaged_video…` |
| Explorer drag semantics (Shift = move) | Wheel answers COPY only; the source is never moved | WheelWindow.OnDragOver | asserted in the drag probe (environment-limited, see §4) |
| Global input hooks | Observe-only, pass every event on, react only to button/movement and Shift/Ctrl/Alt/Esc, no keystroke logging; hooks on a dedicated thread; can be disabled in Settings | DragWatcher | code review |
| Elevation | `asInvoker`, per-user installer, HKCU only | app.manifest, Orange.iss | — |

## 3. Privacy

- No network code in Orange. No telemetry, analytics, crash upload, update check, account or licence server. External tools are invoked with local paths only (FFmpeg protocol whitelist, Pandoc sandbox).
- Data stored: `%LOCALAPPDATA%\Orange\settings.json` (preferences, last options per operation, favourites, last 50 recent conversions with output paths) and `crash.log` (local only). *About & privacy → Delete settings and history* removes them.
- Optional debug log (Settings → Troubleshooting, off by default): `%LOCALAPPDATA%\Orange\logs\orange-yyyyMMdd.log`, 2 MB cap per file, 7 days kept, control characters stripped so names can't forge lines. Records hook/key events, drag data formats, drop targets, file paths and results; never contents; never sent anywhere (`LocalLogTests`).
- The test-only trace (`ORANGE_TEST_EVENTS`) is off unless that environment variable is set; it records event names and file paths, never contents.

## 4. Verification status (honest)

| Item | Status | Evidence |
|---|---|---|
| Engine: 136 tests (134 pass, 2 skip when an optional tool is absent) incl. real FFmpeg/Pandoc/Ghostscript conversions, lossless metadata removal with image-data hash check, audio visualizer, and all security cases | **Verified** on Linux (sandbox) and Windows (GitHub `windows-latest`) | CI job *engine* |
| App compiles, publishes self-contained x64, packages portable zip + installer | **Verified** on Windows CI | CI job *app* |
| Hooks install; hotkey registers; tray icon; context-menu verb and Send To install/remove; wheel shows context-aware items; hit-testing; light/dark rendering; conversions through the app's own service (PNG→WebP, PDF merge, MP4→MP3) | **Verified** on Windows Server 2025 (build 26100) CI desktop via `--selftest` | `ci-evidence/run-N/selftest/` |
| Shift-drag end to end (real mouse, OLE drag, File Explorer source) | **Partly verified on a real Windows 11 laptop (2026-10-18, nightly build from 6e0dfaf), by the owner, with a folder dragged from the desktop:** the wheel opened under the cursor during a real drag for each wheel key. A folder showed the empty "Folders: use Actions" state, and the Actions wheel showed all six actions. Still open: dragging files and converting by dropping on a wedge, and a possible Ctrl/Alt mix-up being checked. Hosted CI can't verify this, because injected input reaches no low-level hook there | owner's photos, issue thread |
| Reading the Explorer selection | Not verified on CI (Explorer window not foreground on the runner) | self-test, optional check |
| HEIC writing | **Not supported** (Magick.NET's Windows build has no HEIC encoder); hidden in UI | self-test optional check fails as expected |
| WinUI controls in unpackaged build | A crash in NavigationView/NumberBox/InfoBar/ProgressBar was traced to missing `resources.pri` and fixed by `EnableMsixTooling=true`; Orange also draws its own progress bar | CI UI bisect |
| Narrator / high contrast / 150–200 % DPI | Implemented (UIA names, live region, theme brushes, PerMonitorV2); manual check pending | — |
| Windows 10 | Targeted (min 10.0.17763); not run in CI | — |

### Added 2026-10-19 (from the Tangerine 2.0.2 bundle pass)

| Item | Status |
|---|---|
| Lossless JPEG/PNG metadata removal (segments dropped, scan/IDAT data byte-identical, orientation kept, refuses if the image-data hash changes) | Verified by engine tests |
| Compress refuses to save a bigger file | Verified by engine test |
| Audio visualizer (wave / bars / spectrum → MP4) | Verified by engine tests with real FFmpeg |
| First-run Welcome window | Opens and renders in CI self-test (run 25, welcome.png) |
| Damaged JPEG/PNG input to the metadata cleaner | Refused with a message (test) |

## 5. Compatibility

- Windows 11 and Windows 10 1809+ (x64; ARM64 builds with `-r win-arm64`, untested).
- Windows 11's modern context menu shows the verb under *Show more options*.
- Drag sources: File Explorer windows and the desktop by default; any app optionally. Elevated sources can't drop into Orange (UIPI). Virtual items (inside ZIP views, phones) aren't files and are refused with an explanation.
- Security tools that block global hooks disable drag activation; Orange reports it and the shortcut, context menu and Send To keep working.
- Long paths: manifest `longPathAware`; the tools themselves may still be limited to 260 characters.

## 6. Residual risks / follow-ups

- Third-party decoders (ImageMagick delegates, FFmpeg demuxers, PDF parsers) remain the largest attack surface; keep them updated. Consider running FFmpeg/Ghostscript in an AppContainer or job object with no network.
- PDFsharp must stay at 6.2.x (MIT); 6.5+ is AGPL.
- Bundled FFmpeg is the GPL build; distributing it obliges providing source (links in `tools\ffmpeg\SOURCE.txt`).
- Unsigned binaries trigger SmartScreen until signed.
