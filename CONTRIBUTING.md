# Contributing

## Workflow

1. Branch from `main`: `itemN/<slug>` for roadmap items, `fix/<slug>`, `docs/<slug>`, `native/<slug>`.
2. Keep one concern per PR. Update [STATUS.md](STATUS.md), [CHANGELOG.md](CHANGELOG.md) and any doc the change
   makes stale (see the table in [CLAUDE.md](CLAUDE.md)).
3. Push and open a PR. The `CI` workflow must be green; read `ci-evidence/run-N/` for app build errors and
   self-test results. Merge with a merge commit.
4. Windows behaviour is only "verified" with CI self-test/probe evidence or a real-PC report.

## Coding standards (as practised in the code)

- C# 12 / .NET 8, nullable enabled, file-scoped namespaces, `sealed` classes and `record`s for data.
- The app's UI is built **in C#**, not XAML (only `App.xaml` exists). Use `Brand` colours (`Views/Brand.cs`);
  orange scheme only.
- Engine code (`Orange.Core`) has no UI or Windows-only dependencies so it builds and tests on Linux. Guard
  Windows-only calls with `OperatingSystem.IsWindows()`.
- Errors the user can act on: `throw new OrangeException("plain sentence", technicalDetail)`. Messages are short,
  friendly and actionable ("…looks damaged"); details go in `Detail`.
- Never use a shell. Run tools through `ProcessRunner` with argument lists and absolute paths; prefix FFmpeg
  inputs with `file:`; put `--` before Pandoc inputs.
- Read untrusted files defensively: check signatures (`FileTypeDetector`), size limits (`EngineLimits`/`SafePaths`),
  caps on loops and allocations (see `Office/LegacyOffice.cs` for OLE parsing limits).
- Write outputs only via `SafePaths` temp workspace → validate → atomic commit. Never overwrite an input.
- Options: declare them as `OptionSpec`s; read via `OperationRequest.OptInt/OptDouble/...` so they are validated.
- User-visible strings: `L.T("English text")` + `tools/l10n/translations.json` + `python3 tools/l10n/gen_resw.py`.
- Comments explain *why* (constraints, licences, Windows quirks), not what.

## Tests

- Every new operation gets a fixture (licence noted in `THIRD-PARTY-NOTICES.md`) and is covered by the sweep
  automatically; add a focused xUnit test for its specific behaviour and for hostile input.
- Tool-dependent tests use `SkippableFact` and `TestSupport` so they skip without the tool (fail with
  `ORANGE_REQUIRE_TOOLS=1`).
- App features: add a check or screenshot to `src/Orange.App/SelfTest.cs` (`Try(name, required, …)`).

## Licences and clean room

- No code copied from GPL/AGPL/MPL projects or from Tangerine. Implement from public specifications and note the
  spec in the file's summary comment.
- New dependencies: permissive licence, measured size impact, entry in `THIRD-PARTY-NOTICES.md` and
  `docs/licensing.md`. PDFsharp stays at 6.2.4.

## Commits

Author `Hemanth Kundrapu <183790673+Hem456anth@users.noreply.github.com>`. Imperative, specific subject lines
(see `git log`). Never commit `bin/`, `obj/`, secrets, Tangerine material or `docs/research/frames/`.
