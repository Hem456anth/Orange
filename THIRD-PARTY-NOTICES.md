# Third-party components

Orange's own code is MIT-licensed (see LICENSE). It uses these components. Versions are pinned in the project files.

| Component | Used for | License | How it ships |
|---|---|---|---|
| .NET 8 runtime | app runtime | MIT | bundled (self-contained) |
| Windows App SDK / WinUI 3 | user interface | MIT (Microsoft Software License for some binaries) | bundled (self-contained) |
| Magick.NET (ImageMagick 7, libheif, libde265, libwebp, libaom, libjpeg-turbo, libpng, libtiff…) | images | Apache-2.0 (Magick.NET, ImageMagick); each native library under its own permissive license (LGPL for libde265) | NuGet native DLL, loaded in-process |
| PDFsharp 6.2.4 | PDF merge, split, rotate, pages, images→PDF | MIT | NuGet. **6.5 and later are AGPL; Orange pins 6.2.4 on purpose.** |
| ZXing.Net 0.16.9 | QR code reader (decode only) | Apache-2.0 | NuGet. https://github.com/micjahn/ZXing.Net |
| 7-Zip (7z.exe + 7z.dll) | 7z creation, RAR/7z extraction | LGPL-2.1 + unRAR licence restriction (RAR code may not be used to recreate the RAR compression algorithm) | bundled in `tools\7zip\`, run as a separate process; source: https://www.7-zip.org |
| DocumentFormat.OpenXml 3.3 | DOCX/XLSX/PPTX reading and writing | MIT | NuGet |
| PDFsharp-MigraDoc 6.2.4 | PDF layout for documents and spreadsheets | MIT (pinned like PDFsharp) | NuGet |
| ExcelDataReader 3.7 | legacy XLS reading | MIT | NuGet |
| PDFtoImage 4.1 + PDFium (bblanchon build) + SkiaSharp | rendering PDF pages to pictures (PDF → PPTX, slides → PNG) | MIT / BSD-3-Clause / MIT | NuGet, native DLLs loaded in-process |
| PdfPig 0.1.16 | PDF text and image extraction | Apache-2.0 | NuGet |
| FFmpeg 8.1 (BtbN "lgpl-shared" Windows build) | video and audio | LGPL-2.1-or-later (no GPL or nonfree parts; H.264/HEVC via Windows Media Foundation) | separate executables + DLLs in `tools\ffmpeg\`, run as child processes; source offer in `tools\ffmpeg\SOURCE.txt` |
| Pandoc 3.5 (optional engine) | Markdown/HTML/EPUB/ODT/RTF conversion | GPL-2.0-or-later | **not bundled**; downloaded on request from Settings → Engines (github.com/jgm/pandoc release, SHA-256 pinned), run as a separate program |
| Ghostscript | PDF compression and PDF→images | AGPL-3.0 | **not bundled**. Used only if you install it yourself (https://ghostscript.com). |
| LibreOffice | DOCX/ODT/RTF/HTML/Markdown → PDF | MPL-2.0 | **not bundled**. Used only if you install it yourself. |

FFmpeg and Pandoc (when installed) are independent programs that Orange starts with arguments; Orange does not link to them.
Their licenses and the corresponding source locations are included in `tools\*\LICENSE*` in every build.
