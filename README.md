# Orange 🍊 — drag, tap Shift, drop

Orange is a local file converter for Windows 11 and 10. Start dragging files in File Explorer, tap **Shift**, and a wheel of formats opens under the cursor. Drop on a wedge and the result is saved next to the original. Nothing is uploaded.

Orange is an independent project inspired by the workflow of [Tangerine for Mac](https://tangerineformac.com). It is not affiliated with Tangerine or Leap Studio and contains none of its code or assets.

## Using it

| Do this | Get this |
|---|---|
| Drag files in Explorer (or on the desktop), tap **Shift** | Convert wheel: formats for those files |
| …tap **Shift twice** quickly | Tools wheel: Compress, Metadata, Edit, Annotate, Add BG, Crop, Redact, Trim, Bleep, Merge… |
| …press **Shift+Ctrl** | Actions wheel: Share, Zip, Copy, Move to…, Copy path, Terminal, Show in folder |
| Drag a **folder** and tap Shift | ZIP |
| Select files, press **Ctrl+Alt+O** | The same wheel, keyboard-driven: ←/→, Enter, Tab to switch wheels, Esc |
| Right-click → *Convert with Orange* (Windows 11: *Show more options*), or *Send to → Orange* | The wheel for the selection |
| Open Orange from the tray | Search every operation, favourites, recent conversions, settings |

All keys can be changed in Settings → Activation. Orange always accepts a drop as a *copy*, so Explorer's Shift = move never moves your file.

Keys, output folder, overwrite behaviour, theme, parallelism and size limits are in Settings. Holding the key before you start dragging works too.

## Editing panels

Tools that need a choice open a panel first, with a live preview made by the same code that writes the file:
**Edit Photo** (exposure, brightness, contrast, highlights, shadows, saturation, rotate, before/after), **Add BG** (gradients, colours, custom colour, your own image, padding, rounded corners), **Redact** (black / pixelate / blur boxes), **Annotate** (boxes, arrows, text), **Crop** (free or fixed aspect), **Trim Audio/Video** (waveform with drag handles, play selected range, trim silent ends), **Bleeps** (drag across the waveform, preview with bleeps), **Channels** (mono/stereo with output preview), **Metadata** (what the file carries, sensitive items flagged, remove location/camera/all, edit fields), **Reorder pages**. **Read QR** shows the decoded text and never opens it.

Compress takes a **target size** (KB/MB) for images, video, audio and PDFs: Orange searches quality/bitrate and re-encodes until the file is at or just under it, or tells you the closest it got.

## What it converts

`orange matrix` (see [docs/CLI.md](docs/CLI.md)) prints the exact list for your PC: 122 operations in a standard
build. Everything below works without installing anything else; the full table is in [FEATURES.md](FEATURES.md).

- **Images** (PNG, JPG, WebP, HEIC in, TIFF, BMP, GIF, AVIF, ICO, JPEG XL, camera RAW in, self-contained SVG in) → each other and PDF; compress (or to a target size), resize, crop, rotate, grayscale, edit light, add background, redact, annotate, metadata view/edit/remove, read QR; several images → one PDF or a collage. Writing HEIC isn't supported and is hidden.
- **Video** (MP4, MOV, MKV, WebM, AVI, WMV) → MP4, MOV, MKV, WebM, AVI, GIF, MP3, M4A, WAV; compress, trim, mute, resize, crop, speed, snapshot, split, join, remove metadata.
- **Audio** (MP3, WAV, FLAC, AAC, M4A, OGG, Opus, AIFF, WMA) → each other; trim, bleep, trim silence, split, speed, merge, normalize, mono/stereo, visualize, compress (or to a target size), edit/remove tags.
- **PDF**: merge, split, extract pages, reorder, rotate, compress (or to a target size), extract images, remove metadata; → TXT, Markdown, DOCX, PPTX, PNG/JPG pages.
- **Documents** (DOCX, ODT, RTF, TXT, Markdown, HTML, EPUB, Word 97 DOC) → DOCX, ODT, RTF, Markdown, HTML, TXT, EPUB, PDF. **Spreadsheets** (XLSX, XLS, ODS, CSV, TSV) → each other and PDF. **Slides** (PPTX, PowerPoint 97 PPT) → PDF, PNG, PPTX. **E-books** (EPUB, MOBI, FB2, CBZ) → EPUB, PDF, TXT. **Fonts** TTF/OTF ⇄ WOFF, → WOFF2.
- **Subtitles** SRT ⇄ VTT, → TXT, shift timing. **Archives** ZIP/TAR/GZIP/RAR/7z: extract; files or folders → ZIP, TAR, 7z.

Documents, Office files and PDFs are handled by Orange's own clean-room engines ([docs/native-engines.md](docs/native-engines.md)): layout is "structure, not pixels". For exact layout, Settings → Engines can download Pandoc, Ghostscript or LibreOffice (optional, checked against a pinned SHA-256, never bundled).
Not supported: OCR of scanned PDFs, creating RAR, writing HEIC, Apple Pages/Keynote.

## Install

- **Installer**: `Orange-x.y.z-win-x64-setup.exe` — per-user, no admin rights. Choose Send To, right-click menu and start-at-sign-in during setup.
- **Portable**: unzip `Orange-x.y.z-win-x64-portable.zip` anywhere and run `Orange.exe`.

Builds come from GitHub Actions; the latest is the [nightly pre-release](https://github.com/Hem456anth/Orange/releases/tag/nightly). Unsigned builds trigger SmartScreen; see `docs/build-and-release.md` for signing.

## Build

```powershell
dotnet test tests/Orange.Core.Tests                         # engine tests (tool-dependent ones skip without ffmpeg/pandoc/gs)
dotnet publish src/Orange.App -c Release -r win-x64 -p:Platform=x64 --self-contained -o out/Orange
out\Orange\Orange.exe --selftest out\selftest               # real-Windows self-test, writes selftest.json + screenshots
```

Developers and Claude Code: start with [CLAUDE.md](CLAUDE.md). More: [project context](PROJECT_CONTEXT.md) · [architecture](ARCHITECTURE.md) · [features](FEATURES.md) · [setup](SETUP.md) · [tasks](TASKS.md) · [roadmap](ROADMAP.md) · [decisions](DECISIONS.md) · [contributing](CONTRIBUTING.md) · [security, privacy and verification audit](docs/audit.md) · [build, sign, release](docs/build-and-release.md) · [third-party licences](THIRD-PARTY-NOTICES.md)

## Privacy

Orange runs entirely on your PC: no account, no analytics, no telemetry. It makes no network request unless you turn on the update check (off by default) or press Install on an optional engine. Settings and history live in `%LOCALAPPDATA%\Orange`.

## License

MIT for Orange's code. Bundled FFmpeg (LGPL build) and 7-Zip (LGPL) are separate programs; optional engines are downloaded separately under their own licences. See THIRD-PARTY-NOTICES.md and docs/licensing.md.
