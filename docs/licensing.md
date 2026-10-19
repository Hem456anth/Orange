# Licensing and attribution report

Orange's own code: MIT (see `LICENSE`). Third-party components and their exact use are listed in
[`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md), which ships next to `Orange.exe`.

## 1. What Orange ships, and why each licence is compatible

| Component | Licence | How it is used | Compatible with MIT app? |
|---|---|---|---|
| Windows App SDK / WinUI 3 | MIT + Microsoft redistributable terms | linked, self-contained | yes (redistribution allowed) |
| .NET 8 runtime | MIT | self-contained | yes |
| Magick.NET + ImageMagick 7 (+ libheif, libde265, libwebp, libaom, libjpeg-turbo, libpng, libtiff) | Apache-2.0 / ImageMagick licence; libde265 LGPL-3.0 | in-process native DLL, dynamically loaded, unmodified | yes; LGPL part stays a replaceable DLL |
| PDFsharp **6.2.4** | MIT | linked | yes. 6.5+ is AGPL, so the version is pinned `[6.2.4]` |
| PdfPig 0.1.16 | Apache-2.0 | linked | yes |
| ZXing.Net 0.16.9 | Apache-2.0 | linked, decode only | yes |
| FFmpeg (BtbN "gpl" build) | GPL-3.0 | separate executables, run as child processes, unmodified; source links in notices | yes (aggregation, not linking) |
| Pandoc | GPL-2.0+ | separate executable, child process | yes (aggregation) |
| Ghostscript | AGPL-3.0 | **not bundled**; used only if the user installs it | n/a |
| LibreOffice | MPL-2.0 | **not bundled**; optional | n/a |
| 7-Zip | LGPL-2.1 + unRAR restriction | **not bundled**; optional, for RAR/7z extraction | n/a |
| Inno Setup (installer build tool) | Inno Setup licence | build-time only | yes |

Checks done: no AGPL/GPL code is linked into Orange's process; GPL tools are invoked as independent programs with
their licence and source location in the notices; versions with licence changes (PDFsharp) are pinned.

## 2. Tangerine components observed in its bundle (for comparison, none reused)

| Component in Tangerine 2.0.2 | Licence (from its bundled files) | Reused in Orange? |
|---|---|---|
| FFmpeg static, LGPL configuration | LGPL-2.1/3 | No; Orange uses its own FFmpeg build |
| LAME | LGPL | No |
| libwebp / cwebp | BSD-3-Clause | No (Magick.NET's libwebp) |
| ExifTool | Perl Artistic / GPL | No |
| Apache POI, PDFBox, xmlbeans, commons-* + Java 21 runtime | Apache-2.0; GPLv2 + Classpath Exception | No |
| docx.js, pako | Apache-2.0 / MIT | No |
| cupertino-files | MIT | No |
| RevenueCat SDK | MIT | No (Orange has no purchases) |
| Tangerine's own Swift code, scripts (`tangerine_*.pl`), `tangerine-powerpoint.jar`, `PagesWriter.js`, icons, sounds, asset catalog | proprietary | **No.** Not decompiled, copied or redistributed |

## 3. Clean-room statement

- Tangerine's executable was inventoried (strings, plist, file list) but not disassembled or run.
- Behaviour was reimplemented from observation. Orange's UI text, layout code, colours, icon and sounds are
  original. Short descriptive labels that name a function (e.g. "Compress", "Trim", "Add BG") are generic terms.
- The bundle and the raw string dump stay outside the repository (`/workspace/rea-tangerine`, not committed).
- Orange's About page states it is independent and not affiliated with Tangerine's developer.
