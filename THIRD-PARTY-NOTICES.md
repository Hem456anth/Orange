# Third-party components

Orange's own code is MIT-licensed (see LICENSE). It uses these components. Versions are pinned in the project files.

| Component | Used for | License | How it ships |
|---|---|---|---|
| .NET 8 runtime | app runtime | MIT | bundled (self-contained) |
| Windows App SDK / WinUI 3 | user interface | MIT (Microsoft Software License for some binaries) | bundled (self-contained) |
| Magick.NET (ImageMagick 7, libheif, libde265, libwebp, libaom, libjpeg-turbo, libpng, libtiff…) | images | Apache-2.0 (Magick.NET, ImageMagick); each native library under its own permissive license (LGPL for libde265) | NuGet native DLL, loaded in-process |
| PDFsharp 6.2.4 | PDF merge, split, rotate, pages, images→PDF | MIT | NuGet. **6.5 and later are AGPL; Orange pins 6.2.4 on purpose.** |
| PdfPig 0.1.16 | PDF text and image extraction | Apache-2.0 | NuGet |
| FFmpeg (BtbN "gpl" Windows build) | video and audio | GPL-3.0 (the build includes x264/x265) | separate executables in `tools\ffmpeg\`, run as child processes; source: https://github.com/BtbN/FFmpeg-Builds and https://ffmpeg.org |
| Pandoc | document conversion | GPL-2.0-or-later | separate executable in `tools\pandoc\`, run as a child process; source: https://github.com/jgm/pandoc |
| Ghostscript | PDF compression and PDF→images | AGPL-3.0 | **not bundled**. Used only if you install it yourself (https://ghostscript.com). |
| LibreOffice | DOCX/ODT/RTF/HTML/Markdown → PDF | MPL-2.0 | **not bundled**. Used only if you install it yourself. |

FFmpeg and Pandoc are independent programs that Orange starts with arguments; Orange does not link to them.
Their licenses and the corresponding source locations are included in `tools\*\LICENSE*` in every build.
