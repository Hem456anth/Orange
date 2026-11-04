# API and integrations

Orange has **no server and no HTTP API**. Its programmable surfaces are the command line, the engine's .NET
interfaces, process arguments of `Orange.exe`, and a few Windows shell integrations. The only network calls are the
two user-initiated ones listed in §5.

## 1. Command line (`src/Orange.Core/Cli/CliApp.cs`)

Shipped as `Orange.exe --cli <command>` (`installer/cli/orange.cmd` → `orange`). Full reference with examples,
JSON shape and exit codes: [docs/CLI.md](docs/CLI.md).

| Command | Purpose |
|---|---|
| `convert <files|globs> --to <fmt> \| --preset <name> [-o dir] [--quality n] [--overwrite keep\|replace\|fail] [--json]` | Convert files |
| `run <op-id> <files> [--opt name=value]… [-o dir]` | Run any operation by id |
| `ops <files>` · `search <words>` · `matrix` · `tools list` | Discover operations and tools |
| `presets list \| import <file> \| export <file>` | Presets (same `presets.json` as the app) |
| `watch <dir> --preset <name> [--move-originals]` | Foreground watch folder |
| `sweep <fixtures-dir> [--work dir] [--json file]` | End-to-end test of every operation |
| `engines list \| install <id>\|all \| uninstall <id>` | Optional engines (pandoc, ghostscript, libreoffice) |
| `version` | Print version |

Exit codes: 0 done, 1 some failed, 2 usage, 3 nothing to do, 4 internal error, 130 cancelled.

## 2. `Orange.exe` arguments (`src/Orange.App/Program.cs`, `App.xaml.cs`)

| Argument | Effect |
|---|---|
| `--cli …` | Runs the command line in-process and exits (no window) |
| `--wheel <files…>` | Opens the wheel for these files (context menu, Send To) |
| `--to <format> <files…>` | Converts directly (Windows 11 menu, scripts) |
| `--preset <name> <files…>` | Runs a preset directly |
| `--background` | Starts in the tray without the main window (sign-in start) |
| `--selftest <outDir>` | Runs the in-process Windows self-test, writes `selftest.json` + screenshots, exits 0/1 |
| `--uitest <case> …` | CI WinUI feature bisect (`App.xaml.cs`) |

Second launches are redirected to the running instance.

## 3. Engine interfaces (`src/Orange.Core/Abstractions.cs`)

```csharp
public interface IOperationProvider
{
    string Name { get; }
    IEnumerable<OperationDescriptor> Describe();          // static list of operations
    bool IsAvailable(OperationDescriptor op);             // false hides the op (missing tool)
    Task<OperationResult> ExecuteAsync(OperationRequest request, IProgress<double>? progress, CancellationToken ct);
}
```

- `OperationDescriptor`: `Id`, `Title`, `Category` (`FileKind`), `Group` (Convert/Tool), `InputExtensions`,
  `OutputExtension` (null = same as input), `CombinesInputs`, `MinInputs`, `ProducesFolder`, `Options`
  (`OptionSpec`: name, label, type, default, min/max, choices), `Keywords`, `RequiresTool`, `Icon`.
- `OperationRequest`: `Operation`, `Inputs`, `OutputDirectory`, `Options`, `Overwrite`; typed getters
  `Opt/OptInt/OptDouble/OptBool/OptTime` validate against the spec.
- `OperationResult`: `Success`, `Outputs`, `Error` (user-facing), `Detail` (technical, or the text answer of
  read-only tools such as `image.qr-read`).
- Throw `OrangeException(message, detail)` for problems the user can act on; the message is shown as-is.
- Entry points: `OperationRegistry.CreateDefault()`, `registry.ForFiles(files, group)`, `registry.Search(text)`,
  `registry.Get(id)`, `JobRunner`, `PresetRunner`, `ConverterSweep.RunAsync(registry, fixturesDir, workDir)`.

## 4. External programs (child processes via `ProcessRunner`, `src/Orange.Core/Tools.cs`)

| Tool name | Executables searched | Source | Used by |
|---|---|---|---|
| `ffmpeg` (+ ffprobe) | `ffmpeg` | bundled `tools\ffmpeg\bin` (LGPL) | MediaProvider |
| `7zip` | `7z`, `7zz`, `7za` | bundled `tools\7zip` | ArchiveProvider, NSIS engine extraction |
| `pandoc` | `pandoc` | optional engine | DocumentProvider (`--sandbox`) |
| `ghostscript` | `gswin64c`, `gswin32c`, `gs` | optional engine / Program Files | PdfProvider (`-dSAFER`) |
| `libreoffice` | `soffice`, `soffice.com` | optional engine / Program Files | DocumentProvider (headless, throwaway profile) |

Search order: engine override → bundled `tools\` → (unless `ORANGE_BUNDLED_ONLY=1`) Program Files and PATH
(absolute directories only).

## 5. Network integrations (both opt-in, both HTTPS)

| Integration | Code | When | Details |
|---|---|---|---|
| Update check | `src/Orange.Core/Updates/UpdateChecker.cs` | Only if the user turns on Settings → Updates (`CheckForUpdates`, off by default) | GET `https://api.github.com/repos/Hem456anth/Orange/releases/latest`, 15 s timeout; release links are accepted only under `https://github.com/Hem456anth/Orange/` |
| Engine downloads | `src/Orange.Core/Engines/EngineManager.cs` | Only when the user presses Install (Settings → Engines or `orange engines install`) | URLs and SHA-256 pinned in `Catalog`; hosts allow-listed (`github.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com`) except LibreOffice, whose mirrors are trusted by the pinned hash; size cap; installs into `%LOCALAPPDATA%\Orange\engines\<id>\<version>` |

## 6. Windows shell integrations

| Integration | Code | Notes |
|---|---|---|
| Classic context-menu verb "Convert with Orange" | `installer/Orange.iss` (HKCU `Software\Classes\*\shell\Orange`), Settings | Runs `Orange.exe --wheel "%1"` |
| Send To → Orange | installer / Settings | `.lnk` in the user's SendTo folder |
| Windows 11 top-level menu | `src/Orange.ShellExt/OrangeShellExt.cpp` (IExplorerCommand), `AppxManifest.xml` (sparse package) | Reads `%LOCALAPPDATA%\Orange\menu.txt` (`preset|Name|kind` lines); needs a signed package |
| Global hotkey | `Services/TrayAndHotkey.cs` (`RegisterHotKey`) | Default Ctrl+Alt+O |
| Low-level mouse/keyboard hooks | `Services/DragWatcher.cs` | Observe-only, dedicated thread |
| Explorer selection | `Services/Explorer.cs` | `Shell.Application` automation |
| Share sheet, clipboard, Terminal, reveal | `Services/FileActions.cs` | Actions wheel |

## 7. GitHub (repository automation, not app code)

CI writes evidence to branch `ci-evidence` and maintains releases `nightly` (pre-release) and `v*`. Secrets are
listed in [SETUP.md](SETUP.md) §5 by name only.
