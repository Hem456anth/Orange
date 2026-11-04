# Architecture

How Orange is put together at `3306a0e`. Research on why Shift-drag can work inside File Explorer, and its limits,
is in [docs/architecture.md](docs/architecture.md) §2; security controls are in [docs/audit.md](docs/audit.md) §2.

## 1. Layers

```
                ┌──────────────────────────── src/Orange.App (WinUI 3, Windows only) ───────────────────────────┐
 input  ──►     │ DragWatcher (low-level hooks) ─► WheelWindow + WheelControl (OLE drop target, wedges)          │
 (drag, hotkey, │ TrayAndHotkey (tray icon, Ctrl+Alt+O) · Explorer (selection) · Program (single instance, args)│
  context menu, │ MainWindow (search, favourites, recent, Settings) · OptionsWindow · Editors (11 panels)       │
  Send To,      │ ConversionService ─► ProgressWindow / notifications / recents · FileActions (Actions wheel)   │
  watch folders)│ WatchService · ShelfWindow · WelcomeWindow · SelfTest (--selftest) · L (localised strings)    │
                └───────────────┬──────────────────────────────────────────────────────────────┬──────────────┘
                                │ OperationRegistry / JobRunner / PresetRunner                 │ Orange.exe --cli
                ┌───────────────▼──────────── src/Orange.Core (net8.0, no UI) ─────────────────▼──────────────┐
                │ Abstractions · OperationRegistry · JobRunner · FileTypeDetector · SafePaths · Tools         │
                │ Providers: Image · ImageStudio · Media · Pdf · Office · Ebook · Font · Document · Subtitle ·  │
                │            Archive                                                                          │
                │ Office/ (built-in engines) · Presets/ · Watch/ · Engines/ · Input/ · Updates/ · Logging/ ·  │
                │ Cli/CliApp · Testing/ConverterSweep · PowerAware · Metadata · LosslessMetadata · SvgSafety   │
                └───────────────┬─────────────────────────────────────────────────────────────────────────────┘
                                │ child processes (ProcessRunner, no shell)          in-process libraries
                                ▼                                                    ▼
             bundled: FFmpeg + ffprobe (LGPL), 7-Zip            Magick.NET, PDFsharp 6.2.4, PdfPig, PDFtoImage
             optional user downloads: Pandoc, Ghostscript,      (PDFium), DocumentFormat.OpenXml, ExcelDataReader,
             LibreOffice (EngineManager)                         ZXing.Net
```

`src/Orange.Cli/Program.cs` is a thin host around `Orange.Core.Cli.CliApp` used for development and tests.
The shipped command line is `Orange.exe --cli …` (`src/Orange.App/Program.cs` line 18), wrapped by
`installer/cli/orange.cmd`; `orange-cli.exe` is not shipped.

## 2. Core concepts (`src/Orange.Core`)

| Concept | File | Notes |
|---|---|---|
| `FileKind`, `OperationGroup` (Convert, Tool), `OperationDescriptor`, `OptionSpec`, `OperationRequest`, `OperationResult`, `IOperationProvider`, `OrangeException` | `Abstractions.cs` | An operation id looks like `image.to.webp`, `pdf.merge`. `RequiresTool` names an external program or is null. |
| Registry | `OperationRegistry.cs` | `CreateDefault(ToolLocator?)` registers providers in order; `ForFiles(files, group)` gives the wheel's context-aware list (every file must match, same kind, hides "to the same format", first provider for a format wins); `Search(text)`; `Get(id)`. |
| Jobs | `JobRunner.cs` | Validates inputs, plans one job per file (or one for combining ops), bounded parallelism, progress, cancellation. |
| Type detection | `FileTypeDetector.cs` | Content signature first, extension second; a contradiction is refused. |
| Paths | `SafePaths.cs` | Windows-safe names, Tangerine-style output names ("name Suffix.ext", " 2" on clash), private temp workspaces, validate then atomic move, `IsInside` checks. |
| Tools | `Tools.cs` | `ToolLocator` (bundled `tools\` → engine overrides → Program Files → PATH; `SearchSystem=false` when `ORANGE_BUNDLED_ONLY=1`), `ProcessRunner` (ArgumentList, timeouts, kill tree, capped output), `OptionValidator`. |
| Power | `PowerAware.cs` | Thread caps and EcoQoS on battery / for background work. |
| Wheel keys | `Input/WheelKeyInterpreter.cs` | Pure gesture decoder: `WheelKey { Shift, Ctrl, Alt, AltShift, ShiftDouble, ShiftCtrl }` → `WheelTarget { Convert, Tools, Actions }`. |
| Presets | `Presets/Preset.cs`, `PresetRunner.cs`, `PresetStore.cs`, `WheelLayout.cs` | Preset = operation + options + metadata policy + output rule; 6 defaults; shared by app, CLI and watch folders. |
| Watch folders | `Watch/FolderWatcher.cs` | Settle detection, ignores own outputs/partial downloads/`Originals`, serial processing; `WatchStore` persists. |
| Optional engines | `Engines/EngineManager.cs` | Catalog (Pandoc 3.5, Ghostscript 10.08.0, LibreOffice 26.8.1), HTTPS download, host allow-list or pinned-hash mirrors, size cap, SHA-256, safe extract (zip / NSIS via 7-Zip / MSI admin image), `Register()` overrides `ToolLocator`. |
| Updates | `Updates/UpdateChecker.cs` | Opt-in; reads GitHub Releases `latest` for this repo only. |
| Logging | `Logging/LocalLog.cs` | Opt-in local log, rotation, control characters stripped. |
| CLI | `Cli/CliApp.cs` | `convert`, `tools`, `presets`, `watch`, `ops`, `search`, `matrix`, `run`, `sweep`, `engines`, `version`. |
| Sweep | `Testing/ConverterSweep.cs` | Runs every registered operation end to end, chaining outputs to make missing input kinds. |

## 3. Providers

| Provider | File | Uses | Operations (prefix) |
|---|---|---|---|
| `ImageProvider` | `Providers/ImageProvider.cs` | Magick.NET | `image.to.*`, compress, resize, crop, rotate, grayscale, strip-metadata, create-pdf, ico-extract |
| `ImageStudioProvider` | `Providers/ImageStudioProvider.cs` | Magick.NET, ZXing.Net | edit, add-background, redact, annotate, collage, qr-read, metadata |
| `MediaProvider` | `Providers/MediaProvider.cs` | FFmpeg/ffprobe (bundled) | `video.*`, `audio.*` incl. bleep, visualize, target-size compress |
| `PdfProvider` | `Providers/PdfProvider.cs` | PDFsharp, PdfPig, PDFtoImage/PDFium, `Office/PdfCompress`, Ghostscript if installed | `pdf.*` |
| `OfficeProvider` | `Providers/OfficeProvider.cs` | `Office/*` built-in engines | `doc.native.*`, `sheet.to.*`, `slides.to.*`, `pdf.to.docx`, `pdf.to.pptx` |
| `EbookProvider`, `FontProvider` | `Providers/BooksAndFontsProviders.cs` | `Office/Ebooks.cs`, `Office/Fonts.cs` | `ebook.to.*`, `font.to.*` |
| `DocumentProvider` | `Providers/DocumentProvider.cs` | Pandoc / LibreOffice when installed, otherwise delegates to `OfficeProvider` | `doc.to.*`, `ppt.to.pdf`, `doc.word-count` |
| `SubtitleProvider` | `Providers/SubtitleAndArchiveProviders.cs` | managed code | `sub.*` |
| `ArchiveProvider` | same file | System.IO.Compression, System.Formats.Tar, 7-Zip | `archive.*` |

Built-in engines in `src/Orange.Core/Office/`: `DocModel` (shared block model), `DocxIO`, `OdtIO`, `RtfIO`,
`LegacyOffice` (`CompoundFile`, `LegacyDoc`, `LegacyPpt`), `Ebooks`, `Fonts`, `PdfIO`, `PdfCompress`, `Sheets`,
`Slides`. Specs used are listed in [docs/native-engines.md](docs/native-engines.md).

## 4. App flow: drag → wheel → file

1. `DragWatcher` (dedicated hook thread) sees left button down, movement beyond the drag threshold and the wheel
   key gesture (decoded by `WheelKeyInterpreter`), then raises an event on the UI thread.
2. `WheelWindow` opens top-most and non-activating under the cursor. It is an OLE drop target, so Explorer's drag
   loop delivers `DragEnter/DragOver/Drop`. It pre-fills files from the Explorer selection (`Explorer.cs`) and
   replaces them with the real payload on `DragEnter`. It always answers **Copy** so Explorer never moves the source.
3. `WheelControl` draws up to eight wedges from `OperationRegistry.ForFiles` (plus pinned presets and the user's
   `WheelLayout`), hit-tests by angle/radius, and highlights by cursor polling.
4. On drop (or on mouse-button-up over a wedge when Windows never delivers `Drop`), the operation runs. Tools with
   options open `OptionsWindow` or an editor panel first (`Editors.cs`) when `AskOptionsForTools` is on.
5. `ConversionService` plans jobs with `JobRunner`, shows `ProgressWindow`, sends a Windows notification, records
   `Recent`, and reveals the output (`SHOpenFolderAndSelectItems`) when `RevealWhenDone` is on.

Other entry points reach the same wheel: Ctrl+Alt+O on the Explorer selection, the classic context-menu verb
and Send To (`--wheel <files>`), the Windows 11 `IExplorerCommand` DLL (`src/Orange.ShellExt`, reads
`%LOCALAPPDATA%\Orange\menu.txt`, registered only when signed), and `--preset <name> <files>` / `--to <fmt> <files>`.
Second launches are redirected to the running instance (`Program.cs`).

## 5. Build and CI

`.github/workflows/ci.yml` jobs:

- **engine** (windows-latest): installs FFmpeg/Pandoc/Ghostscript/LibreOffice, runs `dotnet test`, prints the
  operation matrix, publishes engine evidence.
- **app** (windows-latest): builds and publishes the app, checks the CLI, bundles 7-Zip and FFmpeg (Orange's own
  LGPL build from release `ffmpeg-orange` if it exists, else BtbN LGPL shared; fails on GPL), builds the shell
  extension, runs a WinUI feature bisect, `--selftest`, the end-to-end sweep (bundled only, then after
  `engines install all`), the drag probe, packages the portable zip and Inno Setup installer, signs when secrets
  exist, publishes evidence to `ci-evidence`, refreshes the `nightly` pre-release on `main`, and creates a
  release for `v*` tags.

`.github/workflows/build-ffmpeg.yml` builds a smaller LGPL FFmpeg (`tools/ffmpeg/build-min.sh`) into release
`ffmpeg-orange`; its runs so far were cancelled, so CI currently ships the BtbN fallback.
