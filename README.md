# Orange 🍊 — drag, tap Shift, drop

Orange is a local file converter for Windows 11 and 10. Start dragging files in File Explorer, tap **Shift**, and a wheel of formats opens under the cursor. Drop on a wedge and the result is saved next to the original. Nothing is uploaded.

Orange is an independent project inspired by the workflow of [Tangerine for Mac](https://tangerineformac.com). It is not affiliated with Tangerine or Leap Studio and contains none of its code or assets.

## Using it

| Do this | Get this |
|---|---|
| Drag files in Explorer (or on the desktop), tap **Shift** | Convert wheel: formats for those files |
| …hold **Alt** and tap **Shift** (Tangerine's Option+Shift) | Tools wheel: Compress, Metadata, Edit, Annotate, Add BG, Crop, Redact, Trim, Bleep, Merge… |
| …tap **Ctrl** | Actions wheel: Share, Zip, Copy, Move to…, Copy path, Terminal, Show in folder |
| Drag a **folder** and tap Shift | ZIP |
| Select files, press **Ctrl+Alt+O** | The same wheel, keyboard-driven: ←/→, Enter, Tab to switch wheels, Esc |
| Right-click → *Convert with Orange* (Windows 11: *Show more options*), or *Send to → Orange* | The wheel for the selection |
| Open Orange from the tray | Search every operation, favourites, recent conversions, settings |

Keys, output folder, overwrite behaviour, theme, parallelism and size limits are in Settings. Holding the key before you start dragging works too.

## Editing panels

Tools that need a choice open a panel first, with a live preview made by the same code that writes the file:
**Edit Photo** (exposure, brightness, contrast, highlights, shadows, saturation, rotate, before/after), **Add BG** (gradients, colours, custom colour, your own image, padding, rounded corners), **Redact** (black / pixelate / blur boxes), **Annotate** (boxes, arrows, text), **Crop** (free or fixed aspect), **Trim Audio/Video** (waveform with drag handles, play selected range, trim silent ends), **Bleeps** (drag across the waveform, preview with bleeps), **Channels** (mono/stereo with output preview), **Metadata** (what the file carries, sensitive items flagged, remove location/camera/all, edit fields), **Reorder pages**. **Read QR** shows the decoded text and never opens it.

Compress takes a **target size** (KB/MB) for images, video, audio and PDFs: Orange searches quality/bitrate and re-encodes until the file is at or just under it, or tells you the closest it got.

## What it converts

`orange-cli matrix` prints the exact list for your PC. In a standard build:

- **Images** (PNG, JPG, WebP, HEIC\*, TIFF, BMP, GIF, AVIF, ICO, SVG in) → each other and PDF; compress (or to a target size), resize, crop, rotate/flip, grayscale, edit light, add background, redact, annotate, metadata view/edit/remove, read QR; several images → one PDF or a collage. SVGs are rendered only when self-contained (no DOCTYPE, external links or scripts). \*HEIC is read everywhere; writing HEIC isn't supported by the bundled image library and is hidden.
- **Video** (MP4, MOV, MKV, WebM, AVI, WMV) → MP4, MOV, MKV, WebM, AVI, GIF, MP3, M4A, WAV; compress, trim, mute, resize, crop, speed, snapshot, split, join, remove metadata.
- **Audio** (MP3, WAV, FLAC, AAC, M4A, OGG, Opus, AIFF, WMA) → each other; trim, bleep, trim silence, split, speed, merge, normalize, mono/stereo, compress (or to a target size), edit/remove tags.
- **PDF**: merge, split, extract pages, reorder, rotate, compress\*\* (or to a target size), extract images, remove metadata; → TXT, Markdown, PNG/JPG pages\*\*.
- **Documents** (DOCX, ODT, RTF, TXT, Markdown, HTML, EPUB) → DOCX, ODT, Markdown, HTML, TXT, RTF, EPUB; → PDF\*\*\*.
- **Subtitles** SRT ⇄ VTT, → TXT, shift timing. **Archives** ZIP/TAR/GZIP: extract, repack; any files or folders → ZIP; RAR/7z extract when [7-Zip](https://www.7-zip.org) is installed.

\*\* needs [Ghostscript](https://ghostscript.com) installed (AGPL, not bundled). \*\*\* needs [LibreOffice](https://www.libreoffice.org) installed.
Not supported: PDF → DOCX/PPTX/XLSX, OCR of scanned PDFs, creating RAR, writing HEIC, Apple Pages/Keynote, spreadsheets and slides.

## Install

- **Installer**: `Orange-x.y.z-win-x64-setup.exe` — per-user, no admin rights. Choose Send To, right-click menu and start-at-sign-in during setup.
- **Portable**: unzip `Orange-x.y.z-win-x64-portable.zip` anywhere and run `Orange.exe`.

Builds come from GitHub Actions (`CI` workflow → *orange-win-x64* artifact). Unsigned builds trigger SmartScreen; see `docs/build-and-release.md` for signing.

## Build

```powershell
dotnet test tests/Orange.Core.Tests                         # engine tests (needs ffmpeg, pandoc, gs on PATH, or they skip)
dotnet publish src/Orange.App -c Release -r win-x64 -p:Platform=x64 --self-contained -o out/Orange
out\Orange\Orange.exe --selftest out\selftest               # real-Windows self-test, writes selftest.json + wheel screenshots
```

More: [architecture](docs/architecture.md) · [reverse-engineering report](docs/reverse-engineering-report.md) · [build, install, sign, release](docs/build-and-release.md) · [security, privacy and compatibility audit](docs/audit.md) · [third-party licenses](THIRD-PARTY-NOTICES.md)

## Privacy

Orange runs entirely on your PC: no account, no analytics, no telemetry, no update checks, no network requests. Settings and history live in `%LOCALAPPDATA%\Orange`.

## License

MIT for Orange's code. Bundled FFmpeg and Pandoc are separate GPL programs; see THIRD-PARTY-NOTICES.md.
