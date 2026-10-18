# Tangerine: File Converter (macOS): clean-room behavioural findings

Research date: 2026-10-18. Target: *Tangerine: File Converter* by Leap Studio (tangerineformac.com, Mac App Store id6813034065).
Method: public sources only. No app binary was downloaded, installed, decompiled or inspected.

**Sources used**
- S1 Home page: https://tangerineformac.com
- S2 Press kit: https://tangerineformac.com/press
- S3 Privacy policy: https://tangerineformac.com/privacy
- S4 App Store listing: https://apps.apple.com/us/app/tangerine-file-converter/id6813034065?mt=12
- S5 Landing demo video, 4.07 s, 3130×2160: https://tangerineformac.com/videos/tangerine-landing-demo.mp4
- S6 Press product video, 22.5 s, 1316×1080: https://tangerineformac.com/videos/tangerine-demo.mp4
- S7 Press screenshots: https://tangerineformac.com/press/screenshots/{conversion-wheel,advanced-tools,edit-photo,add-background,audio-tools}.png
- S8 **The website's own interactive demo script and stylesheet**: https://tangerineformac.com/assets/home-BU_Sezi2.js and https://tangerineformac.com/assets/style-DGEuNh-I.css. These are the marketing page's browser simulation ("All operations on this page are simulated"), not the app. Copies are saved at `/workspace/projects/6efe2038-6cd5-47e3-8593-b1f3cd6dce26/web/downloads/tangerine/`. The script's format tables add up to exactly **275** conversion paths and its tool table has exactly **26** tools. Both match the published counts, so the tables are a strong but not certain mirror of the real app.
- S9 Product Hunt: https://www.producthunt.com/products/tangerine-zero-click-file-converter (launched 15 Sep 2026, #17 of the day, 95 points, 1 review, 4.0)
- S10 Maker launch post on X (@th2049, 7 Sep 2026): https://x.com/th2049/status/2097015553508725184
- S11 REA README: https://github.com/morluto/rea

**Version drift you need to know about.** v1.x (App Store description, Product Hunt, product video S6) uses **Option+Shift** (or "hold Option") for tools. The v2.0 site and press kit use **Command** for tools and **Control** for actions. v2.0 shipped about 3 days before this research ("2.0 3d ago" on S4). Design against v2.0.

---

## 1. Activation model

| Trigger | Result | Source |
|---|---|---|
| Start dragging a file, then **tap Shift** | Opens the **Convert** wheel of output formats around the pointer | S1, S2, S4 |
| Drag, then **tap Command** | Opens the **File tools** wheel, showing tools for that file type | S1, S2 |
| Drag, then **tap Control** | Opens the **File actions** wheel (AirDrop, Float, Copy, Move, Copy Path, Terminal) | S1, S2, release notes for 2.0 on S4 |
| v1.x only: drag, then hold **Option+Shift** | Tools wheel. Visible as "option"+"shift" keycaps in S6 at about 10 s and 19 s | S4 description, S6, S9 |
| Release the key | The wheel **stays open** until you drop or cancel. "Release the key and keep dragging" | S1, S2 |
| Switch keys while the wheel is open | Switches wheel. Hint text: "Drop onto a {format/tool/action}. Tap Shift, Command, or Control to switch wheels." | S8 |
| A modifier counts only when it is the **only** key held | The demo maps a key set of size 1 to Shift→conversions, Meta→tools, Control→actions, anything else→none | S8 (`K()` function) |
| Drop onto a segment | Runs the conversion or tool. Hint: "Let go to make a MP3" / "Let go to compress" | S8 |
| Drop outside the wheel ring, or with no wheel open | Ordinary move. Hint: "Drop to move. Tap Control for file actions." | S8 |
| **Enter on a focused file** (keyboard path) | Opens the format menu centred on the file. Then Command or Control switches wheel | S1 |
| Arrow keys / Home / End | Move the highlight, wrapping. Right and Down = next, Left and Up = previous | S1, S8 |
| Enter or Space | Apply. Hint becomes "Press Enter to …" | S1, S8 |
| Escape | Cancel and close the wheel, returning focus to the file. Tab also closes it | S1, S8 |
| Touch screen | Drag = ordinary move. **Hold about 450 ms before dragging** = conversion wheel | S1 (text), S8 (`setTimeout(…,450)`) |
| Configurable keys | "Change the keys in Settings." The available options are not published | S1 |
| Empty wheel | "No conversions for this format. Tap Control for file actions." or "No editing tools for this format. Tap Shift to convert or Control for file actions." | S8 |
| Before any key is pressed | A floating keycap prompt "Tap Shift on your keyboard" appears above the dragged file, but only when the type has conversions and the pointer is not touch | S8, S5 |
| Where it lives | A menu bar app that "stays out of the way until the next drag" | S4 |

## 2. Wheel and menu UI (frames, press screenshots, site CSS)

Frames are in `frames/` (listed at the end).

- **Shape.** A full-circle **donut** of equal wedge "petals" with rounded corners. The ring sits on a circular frosted-glass backdrop. The centre is a hollow hub. Site geometry, in a 272×272 SVG viewBox: inner radius about 48.6, outer about 119.5, backdrop about 125.9, gap between petals 4, corner radius about 23 (S8 constant `m`). The real app in S6 and S7 looks the same, just larger.
- **Segment count.** Equal to the number of valid targets, with no fixed number: 8 for audio (S5: MP3, M4A, FLAC, OGG, OPUS, AIFF…), 8 for PNG (S7: JPG, WEBP, HEIC, TIFF, AVIF, BMP, PDF, DOCX), 7 for MOV (S6: MP4, MKV, M4A?, MP3, GIF…), 7 for image tools (S7), 8 for video tools (S6). Over 6 targets the site adds an `is-dense` style.
- **Ordering.** Segment 0 is centred at **12 o'clock**, and the rest go **clockwise**. With exactly 3 targets the first one is at 9 o'clock (S8 `F()`).
- **Labels.** Conversion petals carry an uppercase format name only (bold, rounded sans; "GZIP" for gz). Tool and action petals carry an **SF Symbol-style icon above an uppercase label** (COMPRESS, METADATA/REMOVE META, EDIT, ANNOTATE, ADD BG, CROP, REDACT, TRIM, MUTE, SPEED, SNAPSHOT, SPLIT…). The site lists the symbol names, for example `arrow.down.right.and.arrow.up.left` for compress (S8).
- **Centre.** The dragged file's thumbnail sits in the hub. A pill label overlays it: by default the **file size** ("2.2 MB" in S7). While a petal is highlighted it shows the **highlighted target's name** (OPUS, AIFF, MP3, SNAPSHOT, SPEED…; S5 and S6). The pill is frosted white with 12 px rounded text, and smaller (8.5 px semibold) for tools (S8 CSS `.fan-center-label`).
- **Colours.** Idle petals are translucent glass: a white→#f3f6f7 radial gradient at about 60–80 % opacity with a hairline stroke, taking a warm or peach tint from the wallpaper behind. The highlighted petal is solid **tangerine #FF5200** with white label and icon (S8 CSS `.fan-target.is-highlighted .petal`). The progress bar is the same orange.
- **Animation states** (S5 and S6 frame sequence):
  1. The file is picked up and a drag ghost follows the cursor. A 3-D "shift" keycap prompt appears below.
  2. Tapping Shift makes the wheel **bloom outward from the cursor**: empty petals scale up first, then the labels fade in (S5 0.8–1.2 s).
  3. Hovering a petal fills it orange with about a 120 ms fill transition and plays a short **highlight tick sound** (`/sounds/highlight.wav`). The centre pill swaps to the target name. The drag ghost shows a green "+" copy badge.
  4. On drop the wheel collapses and a **progress toast** appears near the file: "Converting to MP3 / sample.wav · 100% / orange bar", with a close ✕ button. Batch: "1 of 1 saved".
  5. The new file appears beside the original in Finder, selected or revealed.
- Hit-testing: a petal counts only when the pointer is between the inner and outer radius and outside the angular gap (S8 `Ke()`). The wheel is anchored where the key was tapped and is clamped to stay on screen.
- Tool windows: small floating panels titled with the tool name and closed with ✕. Example: "Crop Video" has a preview, a draggable crop box, an Aspect ratio dropdown (Freeform…), Reset, a "960 × 1,152 px" readout, Width % and Height % sliders, a frame scrubber and timecodes, and an orange **Apply** button. A progress toast then shows "Cropping video" (S6 at 11–17 s). Photo editor, background editor and audio tool windows appear in S7.

## 3. Category and format matrix (as published, plus site-demo tables)

Ten categories appear on the site, each with an icon: Images, Video, Audio, PDF, Documents, Slides, Spreadsheets, EPUB, Subtitles, Archives (S1).

### 3a. Conversion outputs per input (S8 `z()`; the sum is 275, matching "275 conversion options")

| Input(s) | Outputs | Note |
|---|---|---|
| JPG, PNG, WebP, HEIC, TIFF, AVIF, BMP | the other 6 rasters + **PDF** (7 each) | |
| SVG | JPG PNG WebP HEIC TIFF AVIF BMP PDF (8) | SVG is input only |
| MP3, M4A, WAV, FLAC, OGG, Opus, AIFF, WMA | the other 7 audio formats (7 each) | |
| MP4, MOV, MKV, WebM, AVI, WMV | the other 5 video formats + **GIF** + **MP3** (7 each) | |
| GIF | MP4 MOV MKV WebM AVI WMV (6) | |
| PDF | DOCX, PAGES, PPTX, XLSX, JPG, PNG, EPUB, TXT, MD (9) | All pages; images at 300 DPI; local OCR for scans |
| PPT, PPTX | PDF, JPG, PNG (3) | PPT is input only |
| XLSX, ODS, CSV, TSV | the other 3 + PDF (4 each) | |
| XLS | XLSX ODS CSV TSV PDF (5) | XLS is input only |
| DOCX, PAGES, MD | the other docs + PDF, TXT, HTML, EPUB, PNG, JPG (8 each) | |
| RTF, RTFD, .textClipping | DOCX PAGES PDF TXT MD HTML EPUB PNG JPG (9 each) | Input only |
| EPUB | PDF DOCX PAGES TXT MD HTML (6) | |
| TXT | DOCX PAGES PDF MD HTML EPUB JPG PNG (8) | |
| SRT ↔ VTT | the other + TXT (2 each) | Cue timing kept |
| ZIP, TAR, GZIP (.tar.gz) | the other two (2 each) | |
| RAR | ZIP TAR GZIP (3) | RAR is input only |

Discrepancies: (a) The press screenshot (S7) shows **DOCX** on a PNG conversion wheel, but S8 has no image→DOCX path (probably OCR, maybe added or removed between versions). (b) An App Store review on 29 Sep says PDF→EPUB was not supported yet. v2.0 added it (S1 and S8 list it). (c) Search snippets of an older home page said "249 conversion options"; the current page says 275.

### 3b. The 26 file tools (S8 tool table `L`; site wheel titles shown in caps)

| # | id | Wheel label | Full name | Applies to (single file) | Multi-file? |
|---|---|---|---|---|---|
| 1 | compress | COMPRESS | Compress (target size, **percentage**, or preset; can resize) | images, video, audio, PDF | yes |
| 2 | removeMetadata | METADATA | Edit metadata / remove all | images, video, audio, PDF, GIF | yes |
| 3 | editImage | EDIT | Edit Photo (exposure, colour, detail, effects) | jpg png webp heic tiff | no |
| 4 | annotateImage | ANNOTATE | Annotate Photo (arrows, shapes, text, numbered callouts) → PNG | images | no |
| 5 | frameImage | ADD BG | Add a background (solid, gradient or image; aspect, spacing, corner radius) | images | no |
| 6 | cropImage | CROP | Crop Image (aspect ratio or exact px) | images | yes |
| 7 | redactImage | REDACT | Redact Photo (local face/text detection; solid fill or blur) | images | no |
| 8 | createPDF | CREATE PDF | Images → one PDF, a page per image | images incl. SVG | **multi only** |
| 9 | createCollage | COLLAGE | Collage (grid, row, column, featured) | images incl. SVG | **multi only** |
| 10 | muteVideo | MUTE | Remove Audio | mp4 mov mkv | yes |
| 11 | trimVideo | TRIM | Trim Video (handles, frame stepping) | video | no |
| 12 | cropVideo | CROP | Crop Video | video | no |
| 13 | changeVideoSpeed | SPEED | Change Video Speed (pitch preserved) | video | no |
| 14 | joinVideos | JOIN | Join Videos (MP4/MOV/MKV, choose order) → one MP4 | video | **multi only** |
| 15 | videoSnapshots | SNAPSHOT | Video Snapshots → full-res PNGs | video | no |
| 16 | splitVideo | SPLIT | Split Video at points → clips in a new folder | video | no |
| 17 | redactVideo | REDACT | Redact Video (areas over all or part of the timeline) | video | no |
| 18 | normalizeAudio | NORMALIZE | Normalize Volume | mp3 m4a wav flac | yes |
| 19 | trimAudio | TRIM | Trim Audio (waveform; auto-remove silence at the ends) | audio | no |
| 20 | audioChannels | CHANNELS | Convert Audio Channels (mono ↔ stereo) | audio | no |
| 21 | audioToVideo | VISUALIZE | Audio Visualizer → MP4 (landscape, portrait or square; orange waveform on black or an image) | audio | no |
| 22 | redactAudio | BLEEP | Bleep Audio (mark ranges, replace with a bleep) | audio | no |
| 23 | splitPDF | SPLIT | Split PDF → one PDF per page in a new folder | PDF | yes |
| 24 | mergePDF | MERGE PDF | Merge PDFs (selection order) | PDF | **multi only** |
| 25 | organizePDF | ORGANIZE | Organize PDF (reorder, rotate, duplicate, delete pages) | PDF | no |
| 26 | extractArchive | EXTRACT | Extract Archive → folder beside the original | zip tar gz rar | yes |

The App Store copy also mentions **"Read QR codes"** and a tool to "aim for an exact file size". QR reading is not among the 26 ids on the site (UNKNOWN whether it is a 27th tool or part of another). The wheel shows at most 7–9 tools per type. For a multi-file selection the wheel shows only the multi-capable tools. Example from S8: images ×N → Compress, Metadata, Crop, Create PDF, Collage.

### 3c. The 6 file actions (S8 table `M`; S1, S2)

| Action | Behaviour (site description) |
|---|---|
| AirDrop | Share files or folders through the macOS recipient picker |
| Float | Keep files on a floating shelf; pin it to a screen edge and drag files into another app |
| Copy | Copy the files to the clipboard, ready to paste in Finder |
| Move | Choose a destination folder and move the originals there |
| Copy Path | Copy full paths as text, one per line for a selection |
| Terminal | Open the folder, or the file's containing folder, in Terminal or iTerm2 |

Actions work on folders, mixed selections and types with no tools, and they **do not count against the free allowance** (S1).

## 4. Output handling, batch, progress, errors

- **Location.** Output is saved **beside the original** (S2, S3, S4). macOS asks permission before saving into a folder, and the grant is kept locally (S3, which implies a sandboxed app using security-scoped bookmarks: INFERRED).
- **Never overwrites.** "Conversions and edits save a new copy" (S2). Originals are kept.
- **Naming** (S8, plus "geese Cropped.mp4" seen in S6):
  - Conversion: `{basename}.{ext}`. On collision it becomes `{basename} 2.{ext}`, `… 3`. A gz target is written as `.tar.gz`. When reading the basename, `.tar.gz` and `.tgz` count as one extension.
  - Tool: `{basename} {Suffix}.{ext}`, with suffixes Compressed, No Metadata, Edited, Annotated (→png), Background, Cropped, Redacted, Normalized, Muted, Trimmed, Bleeped, "Speed 2x", Snapshot (→png), Clips/ (folder), Pages/ (folder), Organized (→pdf), Extracted/ (folder), Visualizer (→mp4), Mono. On collision a ` 2` goes before the extension.
  - Compressing a WAV produces FLAC (S8).
- **Batch.** "Drag several compatible files at once to convert or process them in one go" (S4). The 2.0 release notes add "percentage compression for individual files and batches".
- **Progress.** "Each job opens a small progress window and reveals the finished files in Finder" (S4). Seen in frames: "Converting to MP4 · geese.mov · bar", "1 of 1 saved", "Cropping video", and a ✕ to cancel.
- **Errors.** Nothing published beyond the empty-wheel messages and a 2.0 fix for "conversion reliability" (UNKNOWN).

## 5. Privacy, pricing, requirements

- **Local processing.** "Everything happens locally on your Mac"; "Your files are never uploaded… it has no server to upload them to"; no analytics, advertising or tracking (S1, S3). App Privacy on S4: only "Purchases" data, not linked to the user. Purchases go through Apple and RevenueCat (S3 meta description). OCR and face/text detection run locally (S8 card copy), which suggests Apple Vision (INFERRED).
- **Pricing.** Free download. **Free for the first 20 files.** Then a one-time IAP "Tangerine Full Unlock" at **$29.99**, with no subscription (S2, S4). File actions don't consume the allowance (S1).
- **Requirements.** macOS 13.0 or later, Apple silicon (M1 or later), 97.6 MB, Utilities category, 4+ (S4, S2).
- **Timeline.** Public launch 7 Sep 2026 (S2, S10). Product Hunt 15 Sep 2026 (S9). v1.4 on 24 Sep. v2.0 about 15 Oct (Actions wheel, EPUB/Word/Pages/PowerPoint/spreadsheet support, Float, percentage compression, a fix for highlight lag after idle). v2.0.1 about 17 Oct (a fix for hidden conversion options) (S4).
- **Reception.** The X launch post had about 118 K views and 1.4 K likes (S10). App Store reviews praise the HEIC→JPG speed, PDF/video metadata access and the one-time price (S4).

## 6. Claim status

| Claim | Status | Evidence |
|---|---|---|
| Shift during drag = convert wheel | VERIFIED | S1 "Drag a file, then tap Shift for conversions" |
| Command = file tools (v2) | VERIFIED | S1 "Command for file tools"; S2 |
| Option+Shift = tools (v1.x) | VERIFIED (legacy) | S4 "Hold Option and Shift instead, and the wheel fills with tools"; S6 keycaps |
| Control = file actions | VERIFIED | S1 "Drag any file or folder, then tap Control." |
| Wheel stays open after key release until drop or cancel | VERIFIED | S1 "the wheel stays open until you drop or cancel" |
| Enter on a file opens the menu; arrows/Enter/Escape | VERIFIED | S1 "press Enter on a file to open the format menu… arrow keys to choose, Enter to apply, and Escape to cancel" |
| Touch: hold briefly before drag = convert | VERIFIED (site) | S1 text; 450 ms comes from S8 and may differ in the app |
| Keys configurable in Settings | VERIFIED; options UNKNOWN | S1 "Change the keys in Settings." |
| Modifier must be the only key held | INFERRED | S8 demo logic only |
| Donut wheel, segments = target count, first at 12 o'clock, clockwise | VERIFIED (frames) / geometry INFERRED from S8 | S5, S6, S7; S8 `F()` |
| Highlight colour #FF5200; centre pill shows size or target | VERIFIED (site CSS + frames) | S8 CSS; S7 "2.2 MB" |
| Highlight tick sound | VERIFIED (site); in-app UNKNOWN | S8 `/sounds/highlight.wav` |
| 275 conversions; matrix in 3a | VERIFIED count (S1); per-pair matrix INFERRED from S8 (sum = 275) | S1 "**275** Conversion options" |
| Image→DOCX | UNKNOWN (conflict) | Shown in S7, absent from S8 |
| 26 tools; list in 3b | VERIFIED count (S1); names INFERRED from S8 (26 ids) and partly VERIFIED by S4 and frames | S1 "**26** Advanced file tools"; S4 tool list |
| QR code reading | VERIFIED as claimed; placement UNKNOWN | S4 "Read QR codes" |
| 6 actions: AirDrop, Float, Copy, Move, Copy Path, Terminal | VERIFIED | S1, S2, S4 v2.0 notes |
| Actions don't use the allowance | VERIFIED | S1 "File actions do not use your conversion allowance." |
| Output saved beside the original | VERIFIED | S3 "Results are saved beside the original file." |
| Never overwrites; new copy | VERIFIED | S2 "Conversions and edits save a new copy." |
| Naming pattern & " 2" collision suffix | INFERRED (S8) + frame "geese Cropped.mp4" | S6 |
| Batch processing | VERIFIED | S4 "Drag several compatible files at once…" |
| Progress window + reveal in Finder | VERIFIED | S4 "Each job opens a small progress window and reveals the finished files in Finder." |
| Error handling details | UNKNOWN | — |
| Local-only, no analytics | VERIFIED | S3 "Tangerine contains no analytics, advertising, or tracking." |
| Free for the first 20 files; $29.99 one-time | VERIFIED | S4 "free for your first 20 files"; "Tangerine Full Unlock $29.99" |
| macOS 13+, Apple M1+ | VERIFIED | S4 "Requires macOS 13.0 or later and a Mac with Apple M1 chip or later." |
| Menu bar app | VERIFIED | S4 "Tangerine lives in your menu bar" |
| Uses Vision/OCR on-device; security-scoped bookmarks; ffmpeg-like engine | INFERRED | No public confirmation |

## 7. REA (morluto/rea) relevance

REA ("Reverse Engineer Anything") is an MIT-licensed MCP server and CLI (`npx rea-agents setup`). It connects coding agents (Claude Code, Codex, Cursor, Gemini CLI…) to local analysis tools: native binaries through Hopper, Ghidra or IDA (pseudocode, strings, symbols, xrefs); JS/Electron app and ASAR analysis; .NET; Android APK/JADX; website observation (DOM, scripts, network, screenshots, which needs Chrome); HAR/SAZ captures; Apple bundle/plist/package inventory; and process-behaviour capture on Linux/macOS (S11).

For this project:
- **Not applicable / out of bounds:** native-binary, Apple-bundle, package and process-capture tools. They need the Tangerine `.app`, which we are not obtaining.
- **Applicable:** the **website observation** and **static JavaScript analysis** tools, which apply to tangerineformac.com's public demo (S8). That is what this report already did by hand. They could also be used later to observe our own Windows clone.
- Note: REA's responsible-use terms require you to own the target or be authorised to examine it.

## Frames (`/workspace/projects/6efe2038-6cd5-47e3-8593-b1f3cd6dce26/orange/docs/research/frames/`)

01-landing-wheel-opening.png (bloom state), 02-landing-audio-wheel-8-segments.png, 03-landing-segment-highlight-center-label.png, 04-landing-progress-toast.png, 05-product-video-wheel-highlight-mov.png, 06-product-video-converting-progress.png, 07-product-video-tools-wheel-option-shift.png (v1 keycaps), 08-product-video-crop-video-window.png, 09-product-video-tools-wheel-highlight.png, 10-press-conversion-wheel-png.png (centre file-size pill), 11-press-advanced-tools-image.png (image tools wheel with icons).
