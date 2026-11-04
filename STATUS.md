# Orange 0.2.0 status

Done = shipped on main **and** proven by a test or a real-PC check. Partial = code merged, proof incomplete.
Blocked = waiting on something outside the repo. Updated with every merged PR.

| # | Item | Status | Evidence | Next step |
|---|---|---|---|---|
| 1 | Real-PC drag-and-drop + debug log + checklist | Partial | PR #3, CI run 29: button-up fallback + cursor-poll highlight, opt-in log (`LocalLogTests`), `Run-OrangeChecklist.ps1` parses in CI and ships | Owner runs the checklist on Win11 (and Win10) and sends the report |
| 2 | Key scheme (Shift / Shift×2 / Shift+Ctrl) | Partial | PR #4, CI run 30: `WheelKeyInterpreter` + `WheelKeyTests`, v3 migration, hub hint (`wheel-key-hint.png`); drop target answers Copy only | Real-PC check mid-drag (checklist steps 3-4) |
| 3 | LGPL FFmpeg, installer < 90 MB, on-demand engines | Partial | PRs #5, #13, #14: LGPL shared FFmpeg (CI fails on GPL), Pandoc unbundled, Settings > Engines, Ghostscript/LibreOffice engines; installer 101.4 MB, app 389.1 MB (run 59) | Finish `build-ffmpeg.yml` small build to get under 90 MB |
| 4 | Code signing | Blocked | Wiring merged in PR #10 (skips without secrets) | Owner adds a PFX or Azure Trusted Signing secrets |
| 5 | Presets + wheel editor | Partial | PR #6: `PresetTests`, settings-presets.png | Real-PC check |
| 6 | CLI + watch folders | Partial | PR #7: `CliTests`, `WatchTests`; CLI runs in CI as `Orange.exe --cli` | Real-PC check of tray watch folders |
| 7 | Office without LibreOffice | Partial | PRs #8, #14: `OfficeTests`, `NativeEngineTests`, sweep 121/122 on Windows CI | Real-PC spot check of output quality |
| 8 | More formats | Partial | PR #9: `FormatTests`, sweep; HEIC write unsupported (D-9) | — |
| 9 | Win11 top-level context menu | Blocked | PR #10: DLL + sparse MSIX built in CI | Needs signing (item 4) |
| 10 | Updates + languages | Partial | PR #11: `UpdateTests`, `LocalisationTests`, settings-telugu.png | Native-speaker review of hi-IN/te-IN |
| 11 | Offline AI tools | Not started | — | See ROADMAP.md |
| 12 | Float shelf | Partial | PR #12: shelf.png | Real-PC drag in/out |
| 13 | Wheel polish | Partial | PR #12: wheel-*.png | Real-PC check (HC, DPI, Narrator) |
| 14 | Main window grouping | Partial | PR #12: main-*.png | — |
| 15 | Settings sections | Partial | PR #11: settings-*.png | — |
| 16 | Onboarding | Partial | PR #12: welcome.png | Real-PC check of practice drop zone |
| 17 | Progress summary | Partial | PR #12: progress-errors.png | — |
| 18 | CI self-test coverage | Done | PR #12: self-test screenshots of tray menu, progress, QR card, shelf, settings sections and panels; sweep in CI (PR #14) | — |

## Built-in engines (PR #14, CI run 59)
- Orange's own clean-room engines now cover the Pandoc, LibreOffice and Ghostscript features it uses: DOCX/ODT/RTF/EPUB/HTML/Markdown/TXT/DOC between each other and to PDF, legacy .doc/.ppt reading, PDF compression and rendering. See docs/native-engines.md.
- Windows CI, installed app with bundled tools only: end-to-end sweep 121 pass, 1 skip (HEIC encode, not in the Windows ImageMagick build), 0 fail. After `orange engines install all` (Pandoc 3.5, Ghostscript 10.08.0, LibreOffice 26.8.1, 66 s): 121 pass, 1 skip.
- App on disk 389.1 MB (limit 400), installer 101.4 MB. Optional engines live outside the app: LibreOffice 1,504 MB, Pandoc 209 MB, Ghostscript 88 MB.
- Not reproduced natively: pixel-exact Office layout, legacy .doc/.ppt pictures and formatting, footnotes/citations/maths, PostScript, OCR. Unverified on a real PC.
