# Tangerine 2.0.2 — findings from the app bundle

**Subject.** The file supplied as "Tangerine-v1.5.1.apk" was a ZIP of `Tangerine.app`, the Mac App Store build
(not an Android APK). Info.plist: `com.th.Tangerine`, version **2.0.2 (build 9)**, macOS 13+, `LSUIElement = true`
(no Dock icon, menu-bar app), category Utilities. Main executable: Mach-O arm64, 11.2 MB, SHA-256 `7691b34c…2928`.

**Method (static, read-only).**
1. REA 6.5.0 (`/workspace/tools/rea`, morluto/rea) was run against the bundle. `inspect-artifact` completed
   (artifact inventory, evidence id `ev_3949f763…`). The deep native providers (`inspect-macho`, `inspect-plist`,
   `inspect-asset-catalog`, `decode-interface-builder`) refused with `capability_unavailable: Native macOS utilities
   require macOS`, so they could not run on the Linux analysis host. REA also suggests `open_binary` with a native
   provider (Hopper/Rizin/Cutter); none is installed here.
2. Python stdlib replacements for what REA could not do: plist parsing, file inventory with hashes, and a string
   inventory of the executable (`tools/re/bundle_inventory.py`, committed). 25,704 distinct strings were read.
3. License files, JAR names, bundled tool configure flags and the privacy manifest were read as plain files.

**What was not done, on purpose.** No disassembly or decompilation of Tangerine's own code; no execution of the app
or its helpers; Tangerine's Perl scripts (`tangerine_privacy.pl`, `tangerine_metadata.pl`), `tangerine-powerpoint.jar`,
`PagesWriter.js` and `OfficePreview.js` were not reused. Only their file names, sizes, licenses and the first comment
line were noted. Nothing from the bundle (code, strings dump, icons, sounds, asset catalog) is committed here.

Confidence labels: **V** = verified in the bundle; **I** = inferred from verified names (behaviour not executed);
**P** = from public sources only (website, demo videos; see `tangerine-findings.md`).

## 1. Architecture

| Area | Finding | |
|---|---|---|
| Language/UI | Swift + SwiftUI/AppKit (`View.task @ Tangerine/WelcomeView.swift`, NSPanel subclasses) | V |
| Source files named in the binary | AnnotationEditorView, ConversionOverlay, ConversionTaskStore, DocumentConverter (+ `Morph/DocumentConverter`), DragGestureMonitor, FileClipboardController, FileMoveAction, FileToolOptionsModel, FloatingFileWindowController, FolderAccess, LoginItemController, OperationProgressWindowController, PaywallWindowController, Purchases, QRCodeResultsWindowController, SettingsWindowController, TerminalFileAction, Unlock, WelcomeModel, WelcomeView | V |
| Drag detection | `DragGestureMonitor`; log lines "Drag shortcut monitor started", "Drag modifiers: %@", "Monitoring drag-to-convert gestures" | V |
| Wheel | `ConversionOverlayPanel/View`, `FanBackdropView`, `NativeGlassFanView`, `PetalTintView`, `FanCenterLabelView`, `HighlightSound.wav/.mp3` (tick on hover) | V |
| Activation keys | Settings keys `Shift`, `Option`, `Command`, `Control`; onboarding text "Tap the chosen modifier combination while dragging files." → wheels are bound to user-chosen modifier *combinations* | V (strings) / I (defaults) |
| Job model | `ConversionTaskStore`, `OperationProgressPanel`, `OrangeIndeterminateProgressView`; "Tangerine is working", "Finished with errors", "Operation failures" | V |
| Output | "Results are saved next to the originals"; sandboxed, so it asks for folder access once (`FolderAccess`, `WelcomeFolderAccessView`, Settings → Folders) | V |
| Verification | "The image could not be verified. No output was saved."; privacy script compares `ImageDataHash` before/after | V |
| Float shelf | `FloatingFilePanel`, "Create a New Float", "Add Files to an Existing Float", attach to screen edge, drag out to carry files | V |
| Monetisation | RevenueCat SDK (`RevenueCatAPIKey`, api.revenuecat.com hosts), `PaywallPanel`, "free uses left", "One-time purchase", lifetime unlock | V |
| Network | Entitlement `com.apple.security.network.client` (used by RevenueCat/StoreKit); FFmpeg built `--disable-network` | V |
| Privacy manifest | Collects purchase history only, for app functionality, not linked to tracking | V |

## 2. Bundled engines

| Component | Evidence | Used for | License |
|---|---|---|---|
| FFmpeg (static, arm64) | configure: `--disable-network --disable-ffprobe --enable-videotoolbox --enable-audiotoolbox --enable-libmp3lame --enable-libwebp`, no `--enable-gpl` | video/audio, hardware H.264/HEVC via VideoToolbox | LGPL-2.1+/3 (license files shipped) | 
| LAME | `licenses/lame/COPYING` | MP3 encoding | LGPL |
| cwebp (libwebp) | `Tools/cwebp` | WebP encoding | BSD-3 |
| ExifTool (Perl library) + 2 Tangerine scripts | `ExifTool/lib/Image/ExifTool/*.pm`, `Artistic.txt` | metadata read/edit, lossless privacy cleanup | Artistic/GPL (Perl terms) |
| Java 21 runtime (arm64 + x86_64) + Apache POI 5.5.1, PDFBox 3.0.6, xmlbeans, commons-* | `PowerPoint/lib/*.jar`, `release: JAVA_VERSION=21.0.12.1` | PPTX and spreadsheet → PDF (`TangerinePowerPoint`, `TangerineSpreadsheet` classes) | Apache-2.0 (+ GPLv2+CE runtime) |
| docx.js + pako | `OfficePreview.js`, its licence file | DOCX rendering (via WebKit) | Apache-2.0 / MIT |
| PagesWriter.js + PagesTemplate.pages | — | writes Apple Pages documents | Tangerine's own |
| cupertino-files 0.4.0 | `CupertinoFiles-Licenses.txt` | file-icon/UI helper | MIT |
| Apple frameworks (inferred) | Vision (QR, OCR: "Recognize scanned text with OCR"), PDFKit, Quartz filter `Reduce File Size.qfilter`, AVFoundation | QR, OCR, PDF, compression | system |

## 3. Screens and panels (from type names and their strings)

Welcome/onboarding (steps "Step 1. Drag files." / "Step 2. While dragging, tap …", launch at login, home-folder
access) · Conversion wheel (fan of petals, centre label/thumbnail) · Conversion Settings / File Tool Options panel
("Settings apply to every selected file") · Compression Settings (preset, mode, **target file size**, "Restore
Compression Defaults") · Image Adjustment ("Save Edited Copy") · Annotation (pen, arrow "drag from the tail",
highlight, text boxes, **numbered callouts**, image paste with ⌘V, "Save Annotated Copy") · Frame/Background ("Add BG",
"Save with Background", custom image) · Rectangle Redaction (photo, and **video with time ranges** "Show for Entire
Video", "Save Redacted Copy") · Crop (thumbnail strip, size fields) · Collage (style, background, "Fit whole images")
· PDF Organizer ("Save Organized PDF", "Make all pages the same width", "Preserve original pages") · Audio Trim
(waveform, "Drag the ends to trim", "Trim silent ends automatically", "Play Selected Range") · Audio Redaction/Bleep
(bleep track, "Bleep Entire Audio", "Save Bleeped Copy") · Audio Channels (per-channel waveform, left/right volume,
Mono/Stereo) · Audio Normalization (preview) · Audio Visualizer (style, image, "Create Visualization") · Video Trim ·
Video Split (split points ≥ 0.05 s apart, "Export One Video") · Video Speed · Video Snapshot (frame picker) · Metadata
editor · QR Code Results ("No QR codes found", copy text) · Operation Progress · Float shelf · Settings (General,
Folders, shortcuts, terminal app, appearance, sound) · Paywall/Unlock.

## 4. Behaviours confirmed by strings (selection)

- Compression refuses to save a bigger file: "Compressed output would exceed the original or target file size." / "…above the target. Try a larger target." (V)
- Metadata removal keeps encoded image samples intact (privacy script header comment) and verifies image data (V).
- Redaction is one still image at a time: "Redact one still photo at a time. Export the frames or pages first." (V)
- Crop minimum 2×2 px; split points ≥ 0.05 s; arrow keys move trim by 0.01 s (V).
- Actions: AirDrop, Copy, Move, Copy paths, Terminal (configurable app), Float (V).
- Documents: DOCX/EPUB writers with explicit hardening ("An EPUB output path escaped its workspace", size limits) (V).
- PDF → editable document with OCR or fixed-layout EPUB (V, strings; engine not executed).

## 5. Limits of this analysis

Short string literals (≤15 bytes, e.g. "PNG", "WebP") are stored inline in Swift code and don't appear in a
string scan, so the exact per-format conversion table could not be read from the binary; it is taken from the public
site (P). Default key bindings, timings and animation curves need execution or disassembly and remain P/unknown.
Assets.car (icons, colours) could not be decoded on Linux.
