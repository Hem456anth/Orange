# Orange's built-in engines (replacing Pandoc, LibreOffice and Ghostscript for everyday conversions)

Orange 0.2 converts documents, legacy Office files and PDFs **without any external program**. The three projects
were studied from the outside (their documented behaviour, command-line options, and output formats), and the
conversion features Orange actually uses were rebuilt from **public file-format specifications**. This is a
clean-room implementation: no source code from Pandoc (GPL-2.0+), LibreOffice (MPL-2.0) or Ghostscript (AGPL-3.0)
was copied or translated, so Orange stays MIT-licensed and nothing extra ships in the installer.

| Replaces | Orange code | Specification used | What it does | Not reproduced |
|---|---|---|---|---|
| Pandoc readers/writers | `Office/DocModel.cs`, `DocxIO.cs`, `OdtIO.cs`, `RtfIO.cs`, `Ebooks.cs` | ECMA-376 (DOCX), OASIS ODF 1.3 (ODT), Microsoft RTF 1.9.1, EPUB 3.3, CommonMark/GFM, WHATWG HTML | DOCX, ODT, RTF, EPUB, HTML, Markdown, TXT, DOC → any of DOCX, ODT, RTF, EPUB, HTML, Markdown, TXT, PDF; headings, bold/italic, lists, tables, pictures, page breaks | citations, footnotes, maths, custom styles, Lua filters |
| LibreOffice (legacy Office) | `Office/LegacyOffice.cs` | [MS-CFB], [MS-DOC], [MS-PPT] open specifications | Word 97–2003 .doc text, paragraphs, tables, fields' results, page breaks; PowerPoint 97–2003 .ppt slide titles and bullets → PDF, PPTX, PNG | exact page layout, fonts, colours, embedded pictures of .doc/.ppt, Word 6/95 files, encrypted files |
| LibreOffice (modern Office) | `DocxIO.cs`, `Sheets.cs`, `Slides.cs`, `PdfIO.cs` | ECMA-376, ODF 1.3 | DOCX/XLSX/XLS/ODS/PPTX → PDF and between each other ("structure, not pixels") | pixel-identical rendering |
| Ghostscript | `Office/PdfCompress.cs`, PDFium via PDFtoImage (BSD-3) | ISO 32000 (PDF) | PDF compression by re-encoding pictures (levels printer/ebook/screen/tiny, target size), PDF → PNG/JPG rendering | font subsetting, PostScript input, PDF/A conversion |

The optional engines in **Settings → Engines** (Pandoc 3.5, Ghostscript 10.08.0, LibreOffice 26.8.1) remain
available for people who want those programs' exact output. They are downloaded only when the user presses
Install, checked against a SHA-256 pinned in the code, unpacked without running an installer (ZIP; NSIS via the
bundled 7-Zip; MSI as an administrative image) into `%LOCALAPPDATA%\Orange\engines`, and are never part of Orange
itself. When they are installed, the `doc.to.*`, `doc.to.pdf` and `ppt.to.pdf` operations use them; when they are not,
the same operation ids run on Orange's own engine, so presets and scripts keep working.

## Licensing check

| Component | Licence | In the installer? | Obligation |
|---|---|---|---|
| Orange (incl. the engines above) | MIT | yes | notice |
| PDFtoImage / PDFium | MIT / BSD-3 + Apache-2.0 | yes | notices in THIRD-PARTY-NOTICES.md |
| PDFsharp 6.2.4 | MIT (6.5+ is AGPL; pinned) | yes | notice |
| Magick.NET / ImageMagick | Apache-2.0 / ImageMagick licence | yes | notice |
| FFmpeg (LGPL build, shared DLLs) | LGPL-2.1+ | yes | licence + source offer in tools/ffmpeg |
| 7-Zip | LGPL-2.1 + unRAR restriction | yes | licence |
| Pandoc | GPL-2.0+ | no (optional download) | none for Orange |
| Ghostscript | AGPL-3.0 | no (optional download) | none for Orange |
| LibreOffice | MPL-2.0 | no (optional download) | none for Orange |

## How it is verified

`orange sweep <fixtures>` runs **every registered operation** end to end through the same job runner the app
uses, feeding files that earlier operations produced to the ones that need other formats, and checks that every
output exists and is non-empty. It runs:

* in the unit tests on a simulated clean PC (no external programs at all),
* on Windows CI against the packaged app with only the bundled tools (`ORANGE_BUNDLED_ONLY=1`),
* on Windows CI again after `orange engines install all`, exercising the Pandoc/Ghostscript/LibreOffice routes.

Results are published under `ci-evidence/run-N/sweep/`.
