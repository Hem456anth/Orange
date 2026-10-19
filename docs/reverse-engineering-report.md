# Orange — reverse-engineering report and feature parity matrix

Two evidence passes, kept separate:

1. **Public pass (2026-10-17/18):** Tangerine's website, press kit, demo videos, App Store page and the site's own
   JavaScript simulation. Log: [`research/tangerine-findings.md`](research/tangerine-findings.md).
2. **Bundle pass (2026-10-19):** the Mac App Store bundle of **Tangerine 2.0.2** supplied by the user (it arrived
   named as an APK; it is a macOS `.app`). Static, read-only: REA 6.5.0 artifact inventory, plist, file and string
   inventory, licence files, helper-tool configure flags. Log: [`research/tangerine-binary-findings.md`](research/tangerine-binary-findings.md).
   REA's Mach-O, asset-catalog and Interface Builder providers require macOS and refused on the Linux host; that is
   recorded there with what replaced them.

Clean-room rule for both passes: Orange contains no Tangerine code, strings dump, icons, sounds or assets.
Behaviour is reimplemented from observation, in Orange's own words, layout details, icon and colours.

Labels: **V** verified in the bundle · **P** public sources only · **I** inferred · Orange: ✅ built and covered by
automated tests (engine tests and/or Windows CI self-test) · 🟡 built, verified partly or only on the user's PC ·
❌ not built · ➖ not applicable on Windows.

## 1. Reconstructed architecture (Tangerine) and the Windows equivalent

| Tangerine (macOS) | Evidence | Orange (Windows) |
|---|---|---|
| Menu-bar agent (`LSUIElement`) | V | Tray app (`Shell_NotifyIcon`), no taskbar button while idle |
| `DragGestureMonitor` watches modifier taps during a system drag | V | `DragWatcher`: low-level mouse + keyboard hooks, real drag confirmed via OLE drag state; CI SendInput probe |
| Wheel = transparent NSPanel with glass fan (`NativeGlassFanView`, `PetalTintView`), tick sound | V | `WheelWindow` (borderless, circular region, DWM frame removed) + `WheelControl` (rounded petals, gradient hot petal) |
| `ConversionTaskStore` + `OperationProgressPanel` | V | `JobRunner` (bounded parallelism, cancel) + `ProgressWindow` (per-file %, retry, sizes, reveal) |
| FFmpeg (LGPL, VideoToolbox) | V | FFmpeg BtbN GPL build as a child process, `file:` paths only, `-protocol_whitelist file` |
| cwebp | V | Magick.NET (libwebp) in-process |
| ExifTool + lossless privacy script | V | `MetadataReader` + `LosslessMetadata` (own JPEG/PNG segment code, image-data hash check) + Magick.NET for edits |
| Java POI/PDFBox for PPTX/XLSX → PDF | V | LibreOffice (optional, not bundled) |
| docx.js / own DOCX & EPUB writers | V | Pandoc (bundled) |
| Apple Vision (QR, OCR), PDFKit, Quartz filter | I | ZXing.Net (QR), PDFsharp/PdfPig, Ghostscript (optional) for PDF compress/render; no OCR |
| Sandbox + folder bookmarks | V | Not needed: Windows desktop apps write beside the source with the user's own rights |
| RevenueCat paywall, network client | V | None. Orange makes no network connections at all |

## 2. Feature parity matrix

### Interaction and system integration

| Feature | Tangerine | Orange | Notes |
|---|---|---|---|
| Wheel while dragging, by modifier | V Shift / Option / Command / Control combos | ✅🟡 0.2 defaults: Shift = convert, Shift twice = tools, Shift+Ctrl = actions (plain Ctrl/Alt are Explorer's copy/link keys), remappable | Gesture decoding unit-tested (`WheelKeyTests`); user confirmed single-key wheels open mid-drag on a Win11 laptop; new gestures await a real-PC check |
| Wheel follows pointer, stays open after key release | V/P | ✅ | |
| Hover tick sound | V | ❌ by design (silent; Windows sound scheme for done/error) | |
| Keyboard wheel | P (Enter in Finder) | ✅ Ctrl+Alt+O on the Explorer selection; arrows, 1–9, Tab, Esc | Explorer reserves Enter |
| Searchable full list ("More") | P | ✅ main window, type-to-search from the wheel | |
| Context menu | ➖ (macOS Services not used) | ✅ "Convert with Orange" (HKCU) + Send To | |
| Start at login | V | ✅ HKCU Run, offered on first run | |
| Onboarding | V (2 steps + login item + folder access) | ✅ Welcome window (3 steps + start at sign-in) | |
| Folder access prompts | V (sandbox) | ➖ | |
| Float shelf | V | ❌ | Largest UX gap; planned |
| Paywall / free-use counter | V | ➖ none — Orange is free and offline | |

### Conversions

| Input | Tangerine | Orange |
|---|---|---|
| Images (PNG, JPG, WebP, HEIC, TIFF, BMP, GIF, AVIF) | P | ✅ to each other + ICO + PDF; HEIC write only if the Magick build supports it (CI: no) |
| SVG in | P | ✅ rendered only when self-contained (no DOCTYPE, links, scripts) |
| Video (MP4, MOV, MKV, AVI, WebM, WMV in) | P | ✅ MP4/MOV/MKV/WebM/AVI/GIF + extract MP3/M4A/WAV; resolution, bitrate, H.264/HEVC |
| Audio (MP3, WAV, FLAC, AAC, M4A, OGG, Opus, AIFF, WMA) | P | ✅ all to each other |
| PDF → images / text | P | ✅ TXT, Markdown; PNG/JPG pages need Ghostscript |
| PDF → DOCX/EPUB (with OCR) | V | ❌ no OCR engine bundled |
| DOCX/RTF/ODT/HTML/MD/EPUB ↔ | P/V | ✅ via Pandoc; → PDF via LibreOffice (optional) |
| PPTX/XLSX → PDF | V (Java POI) | 🟡 LibreOffice if installed |
| Pages writer | V | ➖ (Apple format) |
| Subtitles SRT ⇄ VTT, → TXT | P | ✅ |
| Archives | P | ✅ ZIP/TAR/GZ extract/repack, files/folders → ZIP; RAR/7z extract with 7-Zip |

### Tools and editing panels

| Tool | Tangerine | Orange |
|---|---|---|
| Compress with preset | V | ✅ image/video/audio/PDF |
| Compress to a target size | V | ✅ iterative (quality search / bitrate correction / Ghostscript ladder) |
| Never save a bigger "compressed" file | V | ✅ image + PDF (new); video/audio report the size |
| Edit photo (exposure, brightness, contrast, highlights, shadows) | V | ✅ + saturation, rotate, before/after |
| Annotate (pen, arrow, highlight, text, numbered callouts, paste image) | V | 🟡 boxes, arrows, text; no freehand pen, callouts or pasted images yet |
| Add background (colours, gradients, image) | V | ✅ + padding, rounded corners |
| Redact photo | V | ✅ black / pixelate / blur |
| Redact video by time range | V | ❌ |
| Crop | V | ✅ free or fixed aspect, exact pixels |
| Collage | V | ✅ grid, gap, background, cover/contain |
| PDF organiser (reorder, delete, same width) | V | ✅ reorder/delete/reverse; ❌ "same width" |
| Merge / split / rotate / extract PDF pages | P | ✅ |
| Metadata view / edit / remove | V | ✅ sensitive entries flagged; lossless removal for JPEG/PNG |
| QR reader | V | ✅ text shown, copy, never opened |
| Audio trim with waveform, silent-end trim, range playback | V | ✅ |
| Bleep (drag sections, whole file) | V | ✅ sections, beep or silence, preview |
| Channels (mono/stereo, L/R volume, per-channel preview) | V | 🟡 mono/stereo with output preview; no L/R volume |
| Normalize with preview | V | 🟡 normalize (EBU R128); no preview |
| Audio visualizer | V | ✅ wave / bars / spectrum, colour, background colour or image, 4 sizes |
| Video trim, split, speed, snapshot | V | ✅ (form-based options; no frame-strip picker) |
| Video join, mute, resize, crop | P | ✅ |

### Actions

| Action | Tangerine | Orange |
|---|---|---|
| AirDrop | V | ✅ Windows Share sheet (Nearby Sharing) |
| Copy / Move / Copy paths | V | ✅ |
| Terminal here (configurable app) | V | ✅ Windows Terminal, falls back to PowerShell |
| Show in folder | P | ✅ |
| Zip | — | ✅ (Orange addition; also the folder conversion) |

## 3. Deliberate differences

- **Colour:** Orange keeps its own orange → coral → violet scheme (owner's decision, 2026-10-19: "orange only, not
  blue"); the accent ramp is in `App.xaml` and `Brand.cs`. Tangerine's exact colours weren't decodable (Assets.car).
- **Keys:** Windows has no Command key and Explorer uses Ctrl/Shift/Alt for copy/move/link during a drag, so Orange
  forces *Copy* on drop into the wheel and treats a lone tap as the trigger (not a held modifier on drop).
- **Offline and free:** no StoreKit/RevenueCat equivalent, no network permission needed, no telemetry.
- **Engines:** where Tangerine relies on Apple frameworks (Vision, PDFKit, Quartz filters, VideoToolbox), Orange uses
  ZXing.Net, PDFsharp/PdfPig, Ghostscript and FFmpeg's Media Foundation/x264 encoders.
