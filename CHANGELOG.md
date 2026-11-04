# Changelog

## 0.2.0 (in progress, not tagged; builds on the `nightly` pre-release)

### Built-in engines and verification (PR #14, 2026-10-20)
- Clean-room ODT and RTF reader/writer, Word 97 DOC and PowerPoint 97 PPT readers ([MS-CFB]/[MS-DOC]/[MS-PPT]); every document format converts to every other and to PDF without Pandoc or LibreOffice (`doc.to.*` and `ppt.to.pdf` fall back to the built-in engine).
- PDF compression falls back to the built-in compressor when Ghostscript can't shrink a file.
- Optional Ghostscript 10.08.0 and LibreOffice 26.8.1 engines (NSIS extracted with 7-Zip, MSI as an administrative image), `orange engines list|install|uninstall`.
- `orange sweep` runs every operation end to end; it runs in unit tests (clean PC) and twice in Windows CI (bundled only, then with engines). Run 59: 121 pass, 1 skip.

### Field fixes, performance and size (PR #13, 2026-10-20)
- ffprobe found beside the bundled ffmpeg (audio/video operations were hidden on the owner's PC); self-test runs with `ORANGE_BUNDLED_ONLY=1`.
- Tray window-procedure crash fixed; PDF → PNG/JPG via PDFium and PDF compress without Ghostscript; power-aware thread caps and EcoQoS; faster main list.
- CLI is `Orange.exe --cli` (`cli\orange.cmd`); `orange-cli.exe` is no longer shipped. Trimming was tried and reverted.

### UX items (PRs #11, #12)
- Opt-in update check, English/Hindi/Telugu, Settings sections; float shelf, wheel polish, grouped main window, onboarding with practice zone, progress summary with Retry failed.

### Drag and drop
- Dropping on a wedge now also works when Windows never delivers the drop to the wheel: releasing the mouse over a wedge runs it on the files the drag carried (or the Explorer selection it started from). The source is always treated as a copy.
- New default keys: Shift = Convert, Shift twice = Tools, Shift+Ctrl = Actions (plain Ctrl and Alt are Explorer's copy/link keys). Users on the old defaults are migrated; custom keys are kept. The wheel shows the keys in its centre for the first three drags.
- Optional local debug log (Settings → Troubleshooting) and a laptop checklist script (`Run-OrangeChecklist.ps1`).

### Formats and engines
- FFmpeg is now the LGPL build (no GPL parts); H.264/HEVC use Windows Media Foundation. Pandoc is no longer bundled: install it from Settings → Engines (pinned SHA-256, uninstallable).
- Office without LibreOffice: DOCX/Markdown/TXT/HTML ↔ each other and PDF; XLSX/XLS/ODS/CSV/TSV ↔ each other and PDF; PPTX → PDF/PNG; PDF → DOCX/PPTX.
- Camera RAW input, JPEG XL, multi-size ICO and icon extraction, e-books (EPUB/MOBI/FB2/CBZ → EPUB/PDF/TXT), fonts (TTF/OTF ↔ WOFF, → WOFF2), 7z creation and RAR extraction with bundled 7-Zip.

### Presets, CLI, watch folders
- Presets with options, metadata keep/strip and naming rules; six defaults (WhatsApp video, Discord video, Email PDF, Web image, WebP for web, Podcast MP3); pinned on the wheel; import/export as JSON; wheel editor.
- `orange` command line (convert with globs, presets, JSON output, exit codes) and watch folders in the tray.

### Windows integration
- Windows 11 top-level context menu (IExplorerCommand + sparse package; active when the build is signed). Classic verb and Send To remain.
- Release signing with a PFX or Azure Trusted Signing, skipped when no secret is configured.

## 0.1.0 (2026-10-18/19, commit `75c19ca`, CI run 25)

- Engine with Image, Media, PDF, Document, Subtitle and Archive providers, registry, CLI and tests.
- WinUI 3 app: tray, low-level drag watcher, Convert/Tools/Actions wheels as an OLE drop target, editing panels, main window, settings, welcome, progress, context-menu verb, Send To, Ctrl+Alt+O, `--selftest`.
- Lossless JPEG/PNG metadata removal, compress never returns a bigger file, audio visualizer.
- Inno Setup per-user installer, portable zip, CI with evidence branch and nightly pre-release.
- First real-PC check (owner's Windows 11 laptop, 2026-10-18): the wheel opens mid-drag.
