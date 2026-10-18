# Orange 🍊 — drag, tap Shift, drop

Orange is a local file converter for Windows 11 and 10. Start dragging files in File Explorer, tap **Shift**, and a wheel of formats opens under the cursor. Drop on a wedge and the result is saved next to the original. Nothing is uploaded.

Orange is an independent project inspired by the workflow of [Tangerine for Mac](https://tangerineformac.com). It is not affiliated with Tangerine or Leap Studio and contains none of its code or assets.

## Using it

| Do this | Get this |
|---|---|
| Drag files in Explorer (or on the desktop), tap **Shift** | Convert wheel: formats for those files |
| …tap **Ctrl** | Tools wheel: compress, crop, trim, merge, split, rotate, remove metadata… |
| …tap **Alt** | Actions wheel: Share, Copy, Move to…, Copy path, Terminal, Show in folder |
| Select files, press **Ctrl+Alt+O** | The same wheel, keyboard-driven: ←/→, Enter, Tab to switch wheels, Esc |
| Right-click → *Convert with Orange* (Windows 11: *Show more options*), or *Send to → Orange* | The wheel for the selection |
| Open Orange from the tray | Search every operation, favourites, recent conversions, settings |

Keys, output folder, overwrite behaviour, theme, parallelism and size limits are in Settings. Holding the key before you start dragging works too.

## What it converts

`orange-cli matrix` prints the exact list for your PC. In a standard build:

- **Images** (PNG, JPG, WebP, HEIC\*, TIFF, BMP, GIF, AVIF, ICO) → each other and PDF; compress, resize, crop (aspect or exact), rotate/flip, grayscale, remove metadata; several images → one PDF. \*HEIC is read everywhere; writing HEIC isn't supported by the bundled image library and is hidden.
- **Video** (MP4, MOV, MKV, WebM, AVI, WMV) → MP4, MOV, MKV, WebM, AVI, GIF, MP3, M4A, WAV; compress, trim, mute, resize, crop, speed, snapshot, split, join, remove metadata.
- **Audio** (MP3, WAV, FLAC, AAC, M4A, OGG, Opus, AIFF, WMA) → each other; trim, merge, normalize, mono/stereo, compress, remove metadata.
- **PDF**: merge, split, extract pages, rotate, compress\*\*, extract images, remove metadata; → TXT, Markdown, PNG/JPG pages\*\*.
- **Documents** (DOCX, ODT, RTF, TXT, Markdown, HTML, EPUB) → DOCX, ODT, Markdown, HTML, TXT, RTF, EPUB; → PDF\*\*\*.
- **Subtitles** SRT ⇄ VTT, → TXT, shift timing. **Archives** ZIP/TAR/GZIP: extract, repack.

\*\* needs [Ghostscript](https://ghostscript.com) installed (AGPL, not bundled). \*\*\* needs [LibreOffice](https://www.libreoffice.org) installed.
Not supported: PDF → DOCX/PPTX/XLSX, OCR of scanned PDFs, RAR, Apple Pages/Keynote, spreadsheets and slides.

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
