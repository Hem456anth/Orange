# Decisions

Each entry: decision, why, and where it shows in the code. Add new ones at the end; don't rewrite history.

| # | Date | Decision | Why | Where |
|---|---|---|---|---|
| D-1 | 2026-10-18 | Clean-room design from public sources only; no Tangerine code, binaries or assets | Legal safety; Orange is MIT | `docs/research/`, `docs/reverse-engineering-report.md` |
| D-2 | 2026-10-18 | WinUI 3 (Windows App SDK), unpackaged, self-contained, UI in C# | Modern look, every shell API reachable from C#; unpackaged avoids MSIX install friction | `src/Orange.App/Orange.App.csproj` |
| D-3 | 2026-10-18 | Shift-drag via observe-only low-level hooks + a top-most OLE drop-target window, not an Explorer extension | Explorer has no extension point for modifier-during-drag | `Services/DragWatcher.cs`, `Views/WheelWindow.cs`, `docs/architecture.md` §2 |
| D-4 | 2026-10-18 | The wheel always answers DROPEFFECT_COPY | Explorer's Shift = move must never move the user's file | `WheelWindow.OnDragOver` |
| D-5 | 2026-10-18 | Engine as a UI-free `net8.0` library shared by app and CLI | Testable on Linux, one source of truth for operations | `src/Orange.Core` |
| D-6 | 2026-10-18 | Outputs beside the original with Tangerine-style names, never overwrite inputs, temp + validate + atomic move | Safety and expected behaviour | `SafePaths.cs` |
| D-7 | 2026-10-18 | PDFsharp pinned to 6.2.4 | 6.5+ is AGPL | `Orange.Core.csproj` |
| D-8 | 2026-10-18 | Ghostscript and LibreOffice never bundled | AGPL / size | `EngineManager`, docs |
| D-9 | 2026-10-19 | HEIC writing not supported | No LGPL/permissive HEVC encoder in the shipped stack (x265 is GPL; MF HEVC needs a paid Store extension); Magick.NET's Windows build has no HEIC encoder | `ImageProvider`, `image.to.heic` hidden |
| D-10 | 2026-10-19 | Orange colour scheme only, never blue (owner) | Brand decision; a blue palette was tried and reverted (`97e155a` → `4fcb0e9`) | `Views/Brand.cs` |
| D-11 | 2026-10-19 | 0.2 keys: Shift = Convert, Shift twice = Tools, Shift+Ctrl = Actions, migrated once (settings v3) | Plain Ctrl/Alt already mean copy/link to Explorer during a drag | `Input/WheelKeyInterpreter.cs`, `AppSettings.cs` |
| D-12 | 2026-10-19 | FFmpeg must be an LGPL build (CI fails on `--enable-gpl`); H.264/HEVC via Media Foundation encoders | Keep Orange MIT-compatible and smaller | `ci.yml`, `tools/ffmpeg/` |
| D-13 | 2026-10-19 | Pandoc unbundled; optional engines downloaded only on user request with pinned SHA-256 | Size, licence, privacy | `Engines/EngineManager.cs` |
| D-14 | 2026-10-19 | Network is opt-in only (update check off by default, engine downloads on click); no telemetry | Privacy promise | `UpdateChecker.cs`, `EngineManager.cs` |
| D-15 | 2026-10-19 | Built-in Office engine ("structure, not pixels") using OpenXml, PdfPig, PDFsharp/MigraDoc | Convert Office files without LibreOffice | `Office/*`, `OfficeProvider` |
| D-16 | 2026-10-20 | Ship the CLI as `Orange.exe --cli` (`cli\orange.cmd`), not a second executable | Avoid a second runtime copy; size | `Program.cs`, `installer/cli/orange.cmd` |
| D-17 | 2026-10-20 | Don't trim the publish | Trimming broke the CLI and WinUI (CI run 53) | commit `1aeeee5` |
| D-18 | 2026-10-20 | Self-test and CI sweep run with `ORANGE_BUNDLED_ONLY=1` | A tool on PATH hid a missing bundled ffprobe in the field | `Tools.cs`, `ci.yml` |
| D-19 | 2026-10-20 | Clean-room ODT, RTF, Word 97 DOC and PowerPoint 97 PPT readers/writers; `doc.to.*`/`ppt.to.pdf` fall back to them when Pandoc/LibreOffice are absent; Ghostscript compression falls back to the built-in compressor | Keep every converter working on a clean PC, app < 400 MB | `Office/OdtIO.cs`, `RtfIO.cs`, `LegacyOffice.cs`, `DocumentProvider.cs`, `PdfProvider.cs`, `docs/native-engines.md` |
| D-20 | 2026-10-20 | Size budgets: installed app < 400 MB (owner), installer < 90 MB (plan) | Owner requirement / plan item 3 | CI `summary.txt` |
| D-21 | 2026-10-20 | Every operation is exercised end to end by `orange sweep` in unit tests and CI | Catch broken converters before users do | `Testing/ConverterSweep.cs`, `NativeEngineTests`, `ci.yml` |
| D-22 | 2026-10-20 | One PR per item, merged on green CI with merge commits; STATUS.md never says Done without proof | Traceable, honest status | `PLAN.md`, `STATUS.md` |
