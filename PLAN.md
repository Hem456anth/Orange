# Orange 0.2.0 plan

Baseline: 0.1.0 at commit 75c19ca (CI run 25 green). Status per item lives in [STATUS.md](STATUS.md); evidence in
`docs/audit.md` §4 and the `ci-evidence` branch. One PR per item, merged only on green CI.

## What exists (0.1.0)

| Area | State |
|---|---|
| Engine (`Orange.Core`) | Providers Image, ImageStudio, Media, Pdf, Document (Pandoc/LibreOffice), Subtitle, Archive (ZIP/TAR native, RAR/7z via installed 7-Zip). `OperationRegistry` feeds wheel, main window and CLI. Security controls in `docs/audit.md` §2. 136 tests. |
| App (`Orange.App`, WinUI 3, unpackaged) | Tray, low-level hooks (`DragWatcher`), three wheels (Shift / Alt+Shift / Ctrl), OLE drop target (`WheelWindow`), 11 editor panels, main window (search, favourites, recent), settings, welcome, progress, context-menu verb + Send To, Ctrl+Alt+O shortcut, `--selftest`. |
| CLI (`Orange.Cli`) | `tools`, `matrix`, single conversions. No presets, globs, JSON or watch. |
| Packaging | Inno Setup per-user installer 190 MB, portable zip 281 MB. Bundles GPL FFmpeg (static, x264/x265) and Pandoc. Unsigned. |
| Tests | xUnit engine tests (Linux + Windows CI), app `--selftest` (screenshots), `Orange.DragProbe` (SendInput; can't work on hosted runners). |
| Verified on a real PC | Wheel opens mid-drag (owner, 2026-10-18). **Not** verified: drop-on-wedge produces output. |

## Order of work and what each item touches

| # | Item | Touches | Depends on / risk |
|---|---|---|---|
| 1 | Real-PC drag-and-drop + debug log + laptop checklist | `DragWatcher`, `WheelWindow` (cursor-poll highlight, button-up fallback when OLE drop never arrives), new `Orange.Core/Diagnostics/LocalLog.cs` (rotating local log, no network), Settings toggle, `tools/checklist/Run-OrangeChecklist.ps1` | Only the owner's laptop can prove it. |
| 2 | Key scheme (Shift / Shift×2 / Shift+Ctrl), always Copy, key hint in hub | new pure `Orange.Core/Input/WheelKeyInterpreter.cs` (unit-tested), `DragWatcher`, `AppSettings` (migration v3), settings UI, `WheelControl` hint, welcome text, DragProbe | After 1 (same files). |
| 3 | LGPL FFmpeg + installer < 90 MB + on-demand engines | `ci.yml` (BtbN `lgpl-shared` build instead of `gpl`), `MediaProvider` encoder choice (`h264_mf`/`hevc_mf`/hardware, no libx264), `tools/ffmpeg/SOURCE.txt`, Pandoc → optional engine, new `EngineManager` (download, SHA-256, uninstall) + Settings > Engines, `Orange.iss` (lzma2/ultra) | Size budget: .NET + WinAppSDK ≈ 55 MB compressed; FFmpeg LGPL shared ≈ 25 MB; Magick ≈ 12 MB. Pandoc must leave the installer. |
| 4 | Code signing | `ci.yml` + new `release.yml` (tag `v*`): Azure Trusted Signing action or PFX secret; signs app, CLI, installer, MSIX; skipped when secrets are absent | **Blocked on a certificate / Azure account** (owner). Wiring can be done and tested for the skip path. |
| 5 | Presets + wheel editor | `Orange.Core/Presets/*` (model, JSON import/export, output rule `{name} {preset}.{ext}`, conflict policy, 6 defaults incl. size-targeted WhatsApp/Email/Discord), `SafePaths` name patterns, wheel pinning, Settings > Presets editor with live preview | Size targets reuse target-size compress. |
| 6 | CLI + watch folders | `Orange.Cli` (System.CommandLine: convert/tools/presets/watch, globs, `--json`, exit codes), `Orange.Core/Watch/FolderWatcher.cs` (settle detection, ignore own outputs, persistence), tray pause/resume, `docs/CLI.md`, installer PATH task | Filee's FolderWatcher/OutputPathResolver may be ported (MIT, notice). |
| 7 | Office without LibreOffice | new `OfficeProvider`: DocumentFormat.OpenXml (MIT) + PdfPig + PDFsharp(6.2.4)/MigraDoc for DOCX↔PDF/HTML/MD/TXT, sheets (XLSX/ODS/CSV/TSV via ClosedXML or ExcelDataReader for XLS), PPTX→PDF/images (render limited: text+images layout), PDF→TXT/MD/DOCX/PPTX | Fidelity of native DOCX/PPTX→PDF is the hard part; label "simple layout". |
| 8 | More formats | Image (RAW via Magick/LibRaw delegate, JXL, multi-size ICO), HEIC write (FFmpeg libx265 is GPL → only if an LGPL encoder exists; otherwise stays Not supported), e-books (EPUB/FB2/CBZ native; MOBI read), archives via bundled 7za (LGPL), fonts (WOFF/WOFF2 via managed code or fonttools-free port). One fixture test per format | HEIC write likely **Blocked by licence** under LGPL-only. |
| 9 | Win11 top-level context menu | new C++/WinRT or C# COM `IExplorerCommand` DLL, sparse MSIX manifest, installer registration; classic verb + Send To stay | Sparse package must be **signed** → depends on 4. |
| 10 | Updates + languages | opt-in GitHub Releases check (off by default), release notes view; `.resw` en / hi / te, every string externalised | Translations need a native-speaker review. |
| 11 | Offline AI | `AiProvider`: Windows.Media.Ocr (built in), whisper.cpp CLI (downloaded engine), ONNX Runtime + U²-Net/RMBG model (downloaded); progress + cancel | Model licences (RMBG-1.4 is non-commercial → use U²-Net, Apache-2.0). Uses EngineManager from 3. |
| 12 | Float shelf | `Views/ShelfWindow.cs` (topmost, drag in/out, clear), Actions wheel item | — |
| 13 | Wheel polish | hub colour from theme, labels ≥ 13 px, HC brushes, Narrator names, arrows/Enter/Esc | — |
| 14 | Main window grouping | group by input kind with icons | — |
| 15 | Settings sections | Activation, Output, Presets, Engines, Language, Updates, Privacy, About | After 3, 5, 10. |
| 16 | Onboarding | 3 cards + practice drop zone with a bundled sample | After 2. |
| 17 | Progress summary | "Finished with N errors", per-file reasons, Retry failed, Show in folder | — |
| 18 | CI self-test coverage | tray menu, progress, QR card, presets, watch folders screenshots | After 5, 6. |

## Blockers noted up front

- **4 / 9**: no signing certificate or Azure Trusted Signing account in the repo secrets. Workflows skip signing cleanly; sparse MSIX can't register unsigned on a normal PC.
- **8 (HEIC write)**: no LGPL HEVC encoder in BtbN LGPL builds (x265 is GPL; Media Foundation HEVC needs the paid Store extension). Decision pending.
- **11**: whisper.cpp models (MIT) and U²-Net (Apache-2.0) are downloadable from upstream; download URLs + SHA-256 pinned in code.
- **Definition of done** needs the owner's Windows 10 *and* 11 run of `tools/checklist/Run-OrangeChecklist.ps1`. No Windows 10 machine has been offered yet.
