# Features

Status words: **Shipped** = on `main` and covered by an automated test or CI self-test; **Real-PC verified** =
also confirmed on the owner's Windows 11 laptop; **Unverified** = code exists but nothing has proven it on real
hardware; **Planned** = not implemented. Source of truth for the live operation list: `orange matrix`.

## 1. Conversions and tools (122 operations, `src/Orange.Core/Providers/`)

All of these passed the end-to-end sweep on Windows CI (run 59) except `image.to.heic` (skipped: no HEIC encoder in
the Windows ImageMagick build; it is hidden from the wheel).

| Kind | Convert to | Tools |
|---|---|---|
| Image (PNG, JPG, WebP, HEIC read, TIFF, BMP, GIF, AVIF, ICO, JXL, camera RAW, safe SVG in) | AVIF, BMP, GIF, ICO, JPG, JPEG XL, PDF, PNG, TIFF, WebP | compress (quality or target size), resize, crop, rotate, grayscale, edit (exposure… saturation), add background, redact, annotate, collage, create PDF, extract icons, metadata view/edit, remove metadata (lossless for JPEG/PNG), read QR |
| Video (MP4, MOV, MKV, WebM, AVI, WMV) | AVI, GIF, M4A, MKV, MOV, MP3, MP4, WAV, WebM | compress (target size), crop, join, metadata, mute, resize, snapshot, speed, split, remove metadata, trim |
| Audio (MP3, WAV, FLAC, AAC, M4A, OGG, Opus, AIFF, WMA) | AAC, AIFF, FLAC, M4A, MP3, OGG, Opus, WAV, WMA | bleep, channels, compress, merge, metadata, normalize, trim silence, speed, split, remove metadata, trim, visualize (→ MP4) |
| PDF | DOCX, JPG pages, Markdown, PNG pages, PPTX, TXT | compress (levels or target size; built in, Ghostscript used when installed), extract images, extract pages, merge, reorder, rotate, split, remove metadata |
| Documents (DOCX, ODT, RTF, TXT, Markdown, HTML, EPUB, Word 97 DOC) | DOCX, EPUB, HTML, Markdown, ODT, PDF, RTF, TXT (`doc.native.*`, and `doc.to.*` which uses Pandoc/LibreOffice when installed) | word count |
| E-books (EPUB, MOBI, FB2, CBZ) | EPUB, PDF, TXT | — |
| Spreadsheets (XLSX, XLS, ODS, CSV, TSV) | CSV, ODS, PDF, TSV, XLSX (CSV formula neutralising) | — |
| Presentations (PPTX, PowerPoint 97 PPT) | PDF, PNG slides, PPTX | — |
| Subtitles (SRT, VTT) | SRT, TXT, VTT | shift timing |
| Archives (ZIP, TAR, GZIP, RAR, 7z) and folders | 7z, TAR, ZIP | extract (RAR/7z via bundled 7-Zip) |
| Fonts (TTF, OTF, WOFF) | TTF/OTF, WOFF, WOFF2 | — |

Built-in engine limits (`docs/native-engines.md`): "structure, not pixels" for Office → PDF; legacy .doc/.ppt keep
text, paragraphs, tables and slide titles/bullets, not pictures or exact formatting; no footnotes, citations or
maths; no PostScript input.

## 2. Windows app (`src/Orange.App`)

| Feature | Code | Status |
|---|---|---|
| Wheel opens mid-drag in Explorer/desktop on the wheel key | `Services/DragWatcher.cs`, `Views/WheelWindow.cs` | **Real-PC verified** 2026-10-18 (opened with Shift, Ctrl, Alt in 0.1 keys) |
| 0.2 keys: Shift = Convert, Shift twice = Tools, Shift+Ctrl = Actions; remappable; v3 settings migration; key hint in hub | `Input/WheelKeyInterpreter.cs`, `AppSettings.cs` | Shipped (`WheelKeyTests`, self-test), mid-drag **unverified** |
| Drop on a wedge converts; button-up fallback when OLE delivers no Drop; always Copy | `WheelWindow.cs` | Shipped (self-test), **unverified** on a real PC |
| Keyboard wheel on Explorer selection (Ctrl+Alt+O, arrows, Enter, Tab, Esc) | `TrayAndHotkey.cs`, `WheelWindow.cs` | Shipped (hotkey registers in self-test) |
| Classic context menu "Convert with Orange" and Send To | `installer/Orange.iss`, `App.xaml.cs` | Shipped (self-test installs/removes) |
| Windows 11 top-level context menu (IExplorerCommand + sparse MSIX) | `src/Orange.ShellExt` | Built in CI; registers only when signed → **inactive** (unsigned) |
| Tray app, start at sign-in | `TrayAndHotkey.cs` | Shipped (self-test tray capture) |
| Main window: search all operations, favourites, recents, grouped by input kind | `Views/MainWindow.cs` | Shipped (screenshots) |
| Settings: Activation, Output, Presets, Engines, Language, Updates, Privacy/About, Troubleshooting, Watch folders | `MainWindow.cs`, `PresetsSettings.cs` | Shipped (screenshots) |
| 11 editor panels (Edit Photo, Add BG, Redact, Annotate, Crop, Trim/Bleep/Channels with waveform, Metadata, Reorder pages, QR result) | `Views/Editors.cs` | Shipped (panel-*.png), interaction **unverified** |
| Progress window, notifications, "Finished with N errors", Retry failed, Show in folder | `SmallWindows.cs`, `ConversionService.cs` | Shipped (screenshots) |
| Presets (6 defaults), pinned on the wheel, wheel editor, import/export JSON | `Core/Presets/*`, `PresetsSettings.cs` | Shipped (`PresetTests`) |
| Watch folders run by the tray app, pause/resume | `WatchService.cs`, `Core/Watch/*` | Shipped (`WatchTests`) |
| Float shelf (drag in/out) | `Views/ShelfWindow.cs` | Shipped (shelf.png), **unverified** |
| Welcome + practice drop zone | `Views/WelcomeWindow.cs` | Shipped (welcome.png) |
| Languages en-US, hi-IN, te-IN | `Services/L.cs`, `Strings/*` | Shipped (`LocalisationTests`); translations not reviewed by native speakers |
| Opt-in update check | `Core/Updates/UpdateChecker.cs` | Shipped (`UpdateTests`), off by default |
| Optional engines UI (install/cancel/uninstall) | Settings → Engines | Shipped (settings-engines.png); CLI install verified in CI |
| Opt-in local debug log; laptop checklist script | `Core/Logging/LocalLog.cs`, `tools/checklist/Run-OrangeChecklist.ps1` | Shipped (`LocalLogTests`, script parses in CI) |
| Power-aware parallelism (EcoQoS) | `Core/PowerAware.cs` | Shipped |
| Accessibility (UIA names, high contrast, PerMonitorV2) | views | Implemented; manual check pending |

## 3. Command line

`orange` = `Orange.exe --cli` (see [docs/CLI.md](docs/CLI.md) and [API.md](API.md)). Shipped, tested by `CliTests`
and CI.

## 4. Not supported / planned

| Item | Status |
|---|---|
| Offline AI (OCR, whisper transcription, background removal) — roadmap item 11 | **Planned**, not started |
| Code signing (needs certificate or Azure Trusted Signing secrets) — item 4 | Wired, **blocked** on secrets |
| HEIC writing | Not supported (no LGPL encoder; see DECISIONS.md) |
| OCR of scanned PDFs, creating RAR, Apple Pages/Keynote | Not supported |
| Pixel-exact Office layout | Only with optional LibreOffice engine |
| ARM64 build | Configurable (`-r win-arm64`), untested |
| Windows 10 | Targeted (min 10.0.17763), never run in CI or on a real PC |
