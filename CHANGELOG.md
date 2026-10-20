# Changelog

## 0.2.0 (in progress)

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
