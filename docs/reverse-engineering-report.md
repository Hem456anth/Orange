# Orange — reverse-engineering report (REA-style) and feature matrix

Method: **observe → understand → recreate → verify**, clean-room. Evidence is limited to Tangerine's public website, press kit, demo videos, App Store and Product Hunt pages, and the marketing site's own JavaScript simulation. No Tangerine binary was obtained, installed, decompiled or inspected; no Tangerine code, icons, sounds or copy are used. The detailed evidence log, with every finding tagged by source, is [`research/tangerine-findings.md`](research/tangerine-findings.md). (Frame grabs used during research are kept out of the public repository.)

## 1. Observed behaviour (summary)

| Area | Observed (source in findings) | Confidence |
|---|---|---|
| Activation | Drag file, **tap** Shift (convert) / Command (tools) / Control (actions); wheel stays open after release until drop or Esc; switching keys switches wheels; only a lone modifier counts | High (site, press, demo script) |
| Keyboard | Enter on a focused file opens the wheel; arrows/Home/End move with wrap; Enter/Space apply; Esc/Tab close | High (site text + demo script) |
| Touch | Hold ~450 ms before dragging | Medium (demo script constant) |
| Wheel | Frosted circular ring, equal wedges, first at 12 o'clock clockwise, 7–9 typical; hovered wedge solid orange (#FF5200) + tick sound; centre = thumbnail + size pill that shows the hovered target | High (video frames, CSS) |
| Output | Beside the original, never overwritten, tool suffixes ("Compressed"), numeric suffix on collision; revealed in Finder; progress toast "1 of 1 saved" | High / suffix wording Medium (demo script) |
| Scope | 275 conversion paths, 26 tools, 6 actions; batch and mixed selections where applicable | High (published counts match demo tables) |
| Privacy | Local processing, no analytics | High (privacy policy) |
| Unknown | Exact settings choices, error UI, retry behaviour, QR reading (App Store mentions it, site registry doesn't), image→DOCX (one screenshot only) | — |

## 2. Feature matrix: Tangerine (observed) vs Orange (built)

Legend: ✅ implemented and covered by automated tests · 🟡 implemented, verified only partly or only manually · ❌ not implemented · ➖ not applicable on Windows

### Interaction

| Feature | Tangerine | Orange | Notes |
|---|---|---|---|
| Wheel on modifier during drag | ✅ Shift/⌘/⌃ | 🟡 Shift/Ctrl/Alt (remappable) | Real-input drag probe in CI; see audit for the latest result |
| Hold key before dragging | ? | 🟡 | Same mechanism |
| Wheel stays open after key release | ✅ | 🟡 | |
| Switch wheels while open | ✅ | 🟡 keys; ✅ Tab in keyboard mode | |
| Keyboard wheel on selection | ✅ Enter | ✅ Ctrl+Alt+O | Explorer reserves Enter (open), so a global shortcut is used |
| Arrows/Home/End/Enter/Space/Esc | ✅ | ✅ (+1–9, type-to-search) | |
| Touch hold-to-open | ✅ | ❌ | Pen/touch drags in Explorer aren't distinguishable from mouse in the LL hook; planned |
| Context menu / Send To | ➖ | ✅ (classic menu) | Windows 11 top-level entry needs packaging: ❌ |
| Searchable full list, categories | ❌ (wheel only) | ✅ main window | Required by the Orange brief |
| Favorites / recents | ❌ | ✅ | Favorites, then recents, lead the wheel |
| Settings sheet before tools | ✅ (tool windows) | ✅ simple sheet; ❌ visual crop/trim editors | |
| Progress, cancel, errors with details | ✅ toast | ✅ window + notification | |
| Reveal output | ✅ Finder | ✅ Explorer, selected | |
| Light/dark, high contrast, DPI | ✅ | ✅ theme brushes, PerMonitorV2; 🟡 visual check via self-test screenshots | |
| Accessibility | ? | 🟡 UIA names on wedges + accessible list, live progress | Narrator pass is manual |
| Tick sound on hover | ✅ | ❌ (success/error beeps only) | |

### Conversions (counts are for a CI build with FFmpeg, Pandoc, Ghostscript; LibreOffice optional)

| Category | Tangerine paths | Orange | Gap |
|---|---|---|---|
| Raster images ↔ each other + PDF | 7×7 + SVG 8 | ✅ 9 inputs × 8–9 outputs (PNG JPG WebP TIFF BMP GIF AVIF ICO PDF); HEIC read ✅, HEIC write ❌ (no encoder in Magick.NET) | SVG input ❌ (deliberately off: script-capable format) |
| Audio ↔ audio | 8×7 | ✅ 9 formats (WMA when the FFmpeg build has wmav2) | |
| Video ↔ video + GIF + MP3 | 6×7, GIF→video | ✅ 7 inputs → MP4 MOV MKV WebM AVI GIF MP3 M4A WAV; GIF→video ❌ | WMV output ❌ |
| PDF → | DOCX PAGES PPTX XLSX JPG PNG EPUB TXT MD (OCR) | ✅ TXT, MD, PNG, JPG; ❌ DOCX/PPTX/XLSX/EPUB, OCR | No permissive, local, faithful PDF→Office converter |
| Documents | DOCX PAGES MD RTF TXT EPUB → each + PDF/HTML/PNG/JPG | ✅ DOCX ODT RTF TXT MD HTML EPUB ↔; → PDF via LibreOffice; ❌ Pages, → PNG/JPG | |
| Slides / spreadsheets | PPT(X)→PDF/img; XLSX/ODS/CSV/TSV | ❌ | Possible via LibreOffice later |
| Subtitles | SRT⇄VTT, →TXT | ✅ + shift timing | |
| Archives | ZIP TAR GZIP, RAR in | ✅ ZIP/TAR/GZIP extract + repack; RAR ❌ | |

### Tools (Tangerine's 26)

| Tangerine tool | Orange |
|---|---|
| Compress (image/video/audio/PDF) | ✅ all four |
| Metadata | ✅ Remove metadata (image, video, audio, PDF) |
| Crop Image / Crop Video | ✅ aspect presets + exact box (image); aspect presets (video) |
| Create PDF | ✅ |
| Mute, Trim Video, Speed, Join Videos, Snapshot, Split Video | ✅ |
| Normalize, Trim Audio, Channels | ✅ |
| Split PDF, Merge PDF, Organize PDF | ✅ split, merge; extract pages + rotate cover part of Organize; reorder ❌ |
| Extract Archive | ✅ |
| Edit, Annotate, Add Background, Redact Image, Collage, Redact Video, Visualize, Bleep | ❌ (need interactive editors / ML) |
| — | ➕ Resize, Rotate/flip, Grayscale (image); Resize (video); Merge audio; Extract images (PDF); Shift subtitle timing; Word count |

**Total in Orange 0.1:** 78 operations (45 conversion targets, 33 tools) before tool availability; `orange-cli matrix` prints the per-machine list.

### Actions (Tangerine's 6)

AirDrop → **Share** (Windows share sheet incl. Nearby Sharing) · Float → ❌ · Copy → ✅ · Move → ✅ Move to… · Copy Path → ✅ · Terminal → ✅ · ➕ Show in folder.

## 3. Deliberate design differences

- **Keys.** Command doesn't exist; Ctrl and Alt replace Command and Control. Shift keeps its role. All three are remappable.
- **Copy, never move.** Shift means "move" in Explorer; Orange's drop target always answers *copy*, so the source is never moved.
- **Licensing/pricing.** Tangerine has a 20-file trial then a one-time unlock; Orange has no trial, licensing or accounts.
- **Unsupported is visible.** Unavailable conversions are hidden from the wheel and listed with the reason in the main window instead of failing at run time.
