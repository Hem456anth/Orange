# Setup

Build, run, test, debug and release. Deeper release/signing notes: [docs/build-and-release.md](docs/build-and-release.md).

## 1. Requirements

| Machine | Needs | Can do |
|---|---|---|
| Windows 11 / 10 1809+ x64 | .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`); optional Inno Setup 6, Visual Studio 2022 with C++ (for `Orange.ShellExt`) | Everything |
| Linux / macOS | .NET 8 SDK | `Orange.Core`, `Orange.Cli`, `Orange.Core.Tests`, the sweep. **Not** the WinUI app or shell extension |

Optional tools for full engine tests (otherwise those tests skip): FFmpeg + ffprobe, Pandoc, Ghostscript,
LibreOffice, 7-Zip on PATH. On Windows: `winget install Gyan.FFmpeg JohnMacFarlane.Pandoc ArtifexSoftware.GhostScript TheDocumentFoundation.LibreOffice 7zip.7zip`.

Package versions are pinned in the csproj files (`src/Orange.Core/Orange.Core.csproj`: DocumentFormat.OpenXml 3.3.0,
ExcelDataReader 3.7.0, Magick.NET-Q8-AnyCPU 15.0.0, PdfPig 0.1.16, PDFsharp/PDFsharp-MigraDoc **[6.2.4]**,
PDFtoImage 4.1.1, System.Text.Encoding.CodePages 8.0.0, ZXing.Net 0.16.9; `src/Orange.App/Orange.App.csproj`:
Microsoft.WindowsAppSDK 1.8.260921001, Microsoft.Windows.SDK.BuildTools 10.0.28000.2705).

## 2. Build and test

```bash
git clone https://github.com/Hem456anth/Orange.git && cd Orange
dotnet test tests/Orange.Core.Tests                       # engine tests
dotnet build src/Orange.Cli -c Release                     # dev CLI: dotnet src/Orange.Cli/bin/Release/net8.0/orange-cli.dll <command>
```

```powershell
# Windows: app, bundled tools layout, self-test
dotnet publish src/Orange.App/Orange.App.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained -o out/Orange
# copy ffmpeg.exe + ffprobe.exe (LGPL build) to out/Orange/tools/ffmpeg/bin and 7z.exe + 7z.dll to out/Orange/tools/7zip
out\Orange\Orange.exe --selftest out\selftest
out\Orange\Orange.exe --cli sweep tests\fixtures --json out\sweep.json
```

The app is unpackaged and self-contained (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`). Do not
enable trimming: it broke the CLI and WinUI (commit `1aeeee5`).

## 3. Environment variables

| Variable | Read in | Effect |
|---|---|---|
| `ORANGE_BUNDLED_ONLY=1` | `src/Orange.Core/Tools.cs` | Ignore Program Files and PATH; only bundled tools and installed engines (simulates a clean PC; used by CI) |
| `ORANGE_REQUIRE_TOOLS=1` | `tests/Orange.Core.Tests/TestSupport.cs` | Tests fail instead of skipping when a tool is missing |
| `ORANGE_ALLOW_UNSANDBOXED_PANDOC=1` | `Providers/DocumentProvider.cs` | Linux dev only: allow Pandoc without `--sandbox` when its data files are missing (tests set it automatically on non-Windows). Never on Windows |
| `ORANGE_DEBUG_LOG=1` | `Services/AppSettings.cs` | Turn on the local debug log without the Settings toggle |
| `ORANGE_TEST_EVENTS=<file>` | `Services/DragWatcher.cs` | Test trace of hook/drag events (CI self-test and DragProbe) |
| `ORANGE_FIXTURES=<dir>` | `SelfTest.cs` | Fixtures folder for `--selftest` |

No configuration file or `.env` is needed; user settings live in `%LOCALAPPDATA%\Orange` ([DATABASE.md](DATABASE.md)).

## 4. Debugging

- Engine bugs: reproduce with `orange-cli run <op-id> <file> --opt k=v -o /tmp/out`, then add a test.
- Which operations exist / are available: `orange-cli matrix`, `orange-cli ops <file>`, `orange-cli tools list`.
- App: enable Settings → Troubleshooting (log in `%LOCALAPPDATA%\Orange\logs`), check `crash.log`, or run the
  owner's checklist script `tools/checklist/Run-OrangeChecklist.ps1`.
- CI: read branch `ci-evidence`, folder `run-N/`: `summary.txt`, `app-build-errors.txt`, `app-build.log`,
  `install.log`, `selftest/selftest.json` and screenshots, `sweep/*.json`, `probe/probe.json`, `engine/`.

## 5. CI, release and secrets

- `.github/workflows/ci.yml` runs on pushes to `main`, `v*` tags, pull requests and manual dispatch.
- Every green `main` run refreshes the `nightly` pre-release (installer + portable zip). A `v*` tag creates a release.
- Repository secrets (names only; values are never stored in the repo): `SIGNING_CERT_PFX_BASE64`,
  `SIGNING_CERT_PASSWORD`, `SIGNING_CERT_SUBJECT`, `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`,
  `TRUSTED_SIGNING_ENDPOINT`, `TRUSTED_SIGNING_ACCOUNT`, `TRUSTED_SIGNING_PROFILE`. None are set today, so
  signing steps skip. `GITHUB_TOKEN` is used for evidence and releases.
- Installer: `installer/Orange.iss` (Inno Setup, per-user, tasks: Send To, context menu, PATH `orange` off by
  default, start at sign-in). Built in CI with `ISCC.exe /DAppVersion=0.2.0`.

## 6. Hark sandbox notes (for agent sessions)

- Repo: `/workspace/projects/6efe2038-6cd5-47e3-8593-b1f3cd6dce26/orange`; SDK: `/workspace/.dotnet`.
- Push with the GitHub connector credentials; commit as `Hemanth Kundrapu <183790673+Hem456anth@users.noreply.github.com>`.
- The sandbox once restored an older snapshot and lost unpushed local commits: push often; if files vanish,
  `git fetch origin <branch>` and check out from `FETCH_HEAD`.
