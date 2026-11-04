# CLAUDE.md — instructions for Claude Code

Read this first, then [PROJECT_CONTEXT.md](PROJECT_CONTEXT.md) and [TASKS.md](TASKS.md). Every statement in these
docs was checked against the source at commit `3306a0e` (main, 2026-10-20). When the code and a doc disagree,
the code wins: fix the doc in the same PR.

## What this repo is

Orange is a local file converter for Windows 10/11, modelled on the workflow of Tangerine for Mac (drag files,
tap Shift, a wheel of formats appears, drop on a wedge, the result is saved beside the original). It is a
clean-room project: no Tangerine code or assets, no copied code from Pandoc, LibreOffice or Ghostscript.

| Project | Path | Target | Builds on Linux? |
|---|---|---|---|
| Engine (all conversion logic, no UI) | `src/Orange.Core` | `net8.0` | yes |
| Command line host | `src/Orange.Cli` | `net8.0`, outputs `orange-cli` | yes (dev/test only; not shipped) |
| Windows app (WinUI 3, UI built in C#) | `src/Orange.App` | `net8.0-windows10.0.19041.0`, x64/ARM64 | **no**, CI only |
| Windows 11 context-menu DLL (C++ IExplorerCommand) | `src/Orange.ShellExt` | MSVC | no, CI only |
| Engine tests (xUnit) | `tests/Orange.Core.Tests` | `net8.0` | yes |
| Real-input drag test | `tests/Orange.DragProbe` | Windows | no |

## Commands

```bash
# Linux sandbox / any OS with the .NET 8 SDK
dotnet build src/Orange.Cli -c Release
dotnet test tests/Orange.Core.Tests                  # ~257 tests; tool-dependent ones skip when ffmpeg/pandoc/gs are absent
ORANGE_REQUIRE_TOOLS=1 dotnet test tests/Orange.Core.Tests   # fail instead of skip
dotnet src/Orange.Cli/bin/Release/net8.0/orange-cli.dll matrix          # every operation and availability
dotnet src/Orange.Cli/bin/Release/net8.0/orange-cli.dll sweep tests/fixtures --work /tmp/sweep --json /tmp/sweep.json
ORANGE_BUNDLED_ONLY=1 dotnet src/Orange.Cli/bin/Release/net8.0/orange-cli.dll sweep tests/fixtures --work /tmp/sweep2   # simulate a clean PC
```

```powershell
# Windows only
dotnet publish src/Orange.App/Orange.App.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained -o out/Orange
out\Orange\Orange.exe --selftest out\selftest        # exit 0 = required checks passed
out\Orange\Orange.exe --cli matrix                   # the shipped CLI is Orange.exe --cli (installer/cli/orange.cmd)
```

In the Hark sandbox the SDK is at `/workspace/.dotnet` (`export PATH=/workspace/.dotnet:$PATH`).

## Rules you must follow

1. **Never claim a Windows behaviour works without evidence.** Only CI self-test/probe output or a report from the
   owner's PC counts. Mark everything else "unverified" in `docs/audit.md` §4 and [STATUS.md](STATUS.md).
2. **Workflow for app changes:** one branch + PR per item (`itemN/<slug>`, `fix/<slug>`, `docs/<slug>`), push, wait for the `CI`
   workflow on the PR, read `ci-evidence` branch `run-N/app-build-errors.txt` and `run-N/summary.txt`, fix, repeat;
   merge with a merge commit (not squash). Update STATUS.md in the same PR.
3. **Licences:** PDFsharp / PDFsharp-MigraDoc are pinned to `[6.2.4]` (6.5+ is AGPL). Never bundle Ghostscript
   (AGPL), Pandoc (GPL) or LibreOffice; they are optional user downloads via `EngineManager`. FFmpeg must stay an
   LGPL build (CI fails on `--enable-gpl`). Do not copy code from GPL/AGPL/MPL projects; implement from public specs.
4. **Size budget:** installed app < 400 MB (CI prints `app total MB`; 389.1 MB in run 59). Installer target < 90 MB
   (currently 101.4 MB). Don't add dependencies without measuring.
5. **Security model** (`docs/audit.md` §2): no shell; child processes via `ProcessRunner` with `ArgumentList`;
   content signature must match extension (`FileTypeDetector`); outputs go through `SafePaths` (never overwrite an
   input, temp + validate + atomic move); archives guarded against zip-slip and bombs; Pandoc always `--sandbox` on
   Windows; Ghostscript always `-dSAFER`.
6. **Privacy:** no network code except (a) the opt-in update check (`Updates/UpdateChecker.cs`, off by default) and
   (b) user-initiated engine downloads (`Engines/EngineManager.cs`, pinned SHA-256). No telemetry, ever.
7. **Colour rule (owner, 2026-10-19):** orange/coral/violet scheme and the orange icon only, never blue
   (`src/Orange.App/Views/Brand.cs`), even if a prompt asks for blue.
8. **Clean room:** `docs/research/` holds public-source findings about Tangerine. Never commit Tangerine binaries,
   website copies or frames (`docs/research/frames/` is gitignored).
9. **User-visible strings** go through `L.T("…")` (`src/Orange.App/Services/L.cs`); add translations to
   `tools/l10n/translations.json` and run `python3 tools/l10n/gen_resw.py` (generates `Strings/*/Resources.resw`).
10. **Commits:** author `Hemanth Kundrapu <183790673+Hem456anth@users.noreply.github.com>` (GitHub email privacy
    rejects other addresses). Never commit `bin/`, `obj/`, secrets or anything under `/workspace/rea-tangerine`.

## How to add a conversion

1. Pick or create a provider in `src/Orange.Core/Providers/` implementing `IOperationProvider`
   (`src/Orange.Core/Abstractions.cs`): `Describe()` returns `OperationDescriptor`s (`id` like `image.to.webp`),
   `IsAvailable()` says whether required tools exist, `ExecuteAsync()` does the work.
2. Write outputs only through `SafePaths.TempWorkspace` + the provider's validate step; return `OperationResult.Ok(paths)`.
3. Register new providers in `OperationRegistry.CreateDefault` (`src/Orange.Core/OperationRegistry.cs`). Order
   matters: the first provider that offers a format wins in `ForFiles` (`OfficeProvider` is before `DocumentProvider`).
4. Add a fixture to `tests/fixtures/` (with licence note in `THIRD-PARTY-NOTICES.md` if third-party) and a test.
   The sweep (`src/Orange.Core/Testing/ConverterSweep.cs`) automatically exercises every registered operation;
   add `RequiredOptions`/`PreferredInput` entries there if your op needs them.
5. The wheel, main window, CLI and presets pick the operation up from the registry. No UI wiring is needed unless
   the tool needs an editor panel (`src/Orange.App/Views/Editors.cs`).

## Debugging

- App events: set `ORANGE_TEST_EVENTS=<file>` (hook/drag trace) or turn on Settings → Troubleshooting
  (`%LOCALAPPDATA%\Orange\logs`, or `ORANGE_DEBUG_LOG=1`). Crashes: `%LOCALAPPDATA%\Orange\crash.log`.
- CI evidence: branch `ci-evidence`, folder `run-N/` (summary.txt, app-build-errors.txt, selftest/selftest.json,
  selftest/*.png, sweep/*.json, probe/probe.json, install.log).
- `Orange.DragProbe` always fails on GitHub-hosted runners (SendInput isn't delivered); treat it as informational.

## Where things are documented

| Need | File |
|---|---|
| Background, current state, people | [PROJECT_CONTEXT.md](PROJECT_CONTEXT.md) |
| Components and data flow | [ARCHITECTURE.md](ARCHITECTURE.md), deep Windows drag research in [docs/architecture.md](docs/architecture.md) |
| What works / doesn't | [FEATURES.md](FEATURES.md) |
| What to do next | [TASKS.md](TASKS.md), [ROADMAP.md](ROADMAP.md), [STATUS.md](STATUS.md) |
| CLI, providers, external services | [API.md](API.md), [docs/CLI.md](docs/CLI.md) |
| Files Orange stores (no database) | [DATABASE.md](DATABASE.md) |
| Environment and builds | [SETUP.md](SETUP.md), [docs/build-and-release.md](docs/build-and-release.md) |
| Conventions | [CONTRIBUTING.md](CONTRIBUTING.md) |
| Why things are the way they are | [DECISIONS.md](DECISIONS.md) |
| Security, verification evidence | [docs/audit.md](docs/audit.md) |
| Licences | [docs/licensing.md](docs/licensing.md), [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) |
| Built-in document/PDF engines | [docs/native-engines.md](docs/native-engines.md) |
