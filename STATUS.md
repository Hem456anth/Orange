# Orange 0.2.0 status

Done = shipped on main **and** proven by a test or a real-PC check. Partial = code merged, proof incomplete.
Blocked = waiting on something outside the repo. Updated with every merged PR.

| # | Item | Status | Evidence | Next step |
|---|---|---|---|---|
| 1 | Real-PC drag-and-drop + debug log + checklist | Partial | PR #3, CI run 29 green: button-up fallback + cursor-poll highlight, opt-in log (`LocalLogTests`), `Run-OrangeChecklist.ps1` parses in CI and ships in the zip/installer | Owner runs the checklist on Win11 and Win10 and sends the report |
| 2 | Key scheme (Shift / Shift×2 / Shift+Ctrl) | Partial | PR #4, CI run 30 green: `WheelKeyInterpreter` + 12 `WheelKeyTests`, v3 migration, hub hint (self-test `wheel-key-hint.png`); drop target answers Copy only | Real-PC check of double-tap and Shift+Ctrl mid-drag (checklist steps 3-4) |
| 3 | LGPL FFmpeg, installer < 90 MB, on-demand engines | Partial | `EngineManager` (HTTPS allow-list, size cap, pinned SHA-256, safe extract, uninstall) with 7 `EngineManagerTests`; Pandoc 3.5 pinned. Installer still 190 MB (run 30) | Swap CI to BtbN LGPL shared FFmpeg, drop Pandoc from the bundle, Settings > Engines UI, measure size |
| 4 | Code signing | Blocked | No certificate or Azure Trusted Signing account in repo secrets | Wire skip-when-absent signing; owner adds secrets |
| 5 | Presets + wheel editor | Not started | — | See PLAN.md |
| 6 | CLI + watch folders | Not started | — | See PLAN.md |
| 7 | Office without LibreOffice | Not started | — | See PLAN.md |
| 8 | More formats | Not started | — | See PLAN.md |
| 9 | Win11 top-level context menu | Not started | — | See PLAN.md |
| 10 | Updates + languages | Not started | — | See PLAN.md |
| 11 | Offline AI tools | Not started | — | See PLAN.md |
| 12 | Float shelf | Not started | — | See PLAN.md |
| 13 | Wheel polish | Not started | — | See PLAN.md |
| 14 | Main window grouping | Not started | — | See PLAN.md |
| 15 | Settings sections | Not started | — | See PLAN.md |
| 16 | Onboarding | Not started | — | See PLAN.md |
| 17 | Progress summary | Not started | — | See PLAN.md |
| 18 | CI self-test coverage | Not started | — | See PLAN.md |

## Built-in engines (PR #14, CI run 59)
- Orange's own clean-room engines now cover the Pandoc, LibreOffice and Ghostscript features it uses: DOCX/ODT/RTF/EPUB/HTML/Markdown/TXT/DOC between each other and to PDF, legacy .doc/.ppt reading, PDF compression and rendering. See docs/native-engines.md.
- Windows CI, installed app with bundled tools only: end-to-end sweep 121 pass, 1 skip (HEIC encode, not in the Windows ImageMagick build), 0 fail. After `orange engines install all` (Pandoc 3.5, Ghostscript 10.08.0, LibreOffice 26.8.1, 66 s): 121 pass, 1 skip.
- App on disk 389.1 MB (limit 400), installer 101.4 MB. Optional engines live outside the app: LibreOffice 1,504 MB, Pandoc 209 MB, Ghostscript 88 MB.
- Not reproduced natively: pixel-exact Office layout, legacy .doc/.ppt pictures and formatting, footnotes/citations/maths, PostScript, OCR. Unverified on a real PC.
