# Tasks

Ordered. Each line names the files involved. Move finished items to CHANGELOG.md and update STATUS.md in the same PR.

## P0: release blockers

- [ ] **Real-PC checklist run** (owner): `tools/checklist/Run-OrangeChecklist.ps1` with the `nightly` build; record
      results in `docs/audit.md` §4 and STATUS.md items 1, 2, 12, 13, 16, 17.
- [ ] **Installer < 90 MB**: get `.github/workflows/build-ffmpeg.yml` (`tools/ffmpeg/build-min.sh`) to finish
      (runs 1–2 were cancelled; check whether a dependency fetch hangs) and publish release `ffmpeg-orange`.
      ci.yml already prefers it and checks its SHA-256. Re-measure `installer MB` and `app total MB` in
      `ci-evidence/run-N/summary.txt`.
- [ ] **Signing** (blocked on owner secrets `SIGNING_CERT_PFX_BASE64`/`SIGNING_CERT_PASSWORD` or the Azure
      Trusted Signing set; see `docs/build-and-release.md`). Then verify the Windows 11 menu DLL registers.

## P1: correctness and docs

- [ ] `src/Orange.Core/Cli/CliApp.cs` `PrintUsage` omits `sweep`, `engines` and `version`; add them (docs/CLI.md
      already lists them).
- [ ] CA1416 warnings for PDFtoImage calls in `Office/PdfIO.cs` and `Providers/PdfProvider.cs` (guard with
      `OperatingSystem.IsWindows() || IsLinux() || IsMacOS()` or `[SupportedOSPlatform]`); CA1831 in `Office/Fonts.cs`;
      CS0108 `ShelfWindow.Current`; CS8602 in `Views/Editors.cs` lines ~230–256.
- [ ] `image.to.heic` is registered but always unavailable on Windows; decide: keep hidden (current) or drop it.
- [ ] `docs/reverse-engineering-report.md` and `docs/research/*` describe 0.1 keys (Ctrl/Alt); add a note pointing to
      the 0.2 keys if they are quoted elsewhere.
- [ ] Delete stray CI issue #1 on GitHub if it reappears (none open on 2026-11-04).

## P2: features

- [ ] Item 11 Offline AI (see ROADMAP.md): new `AiProvider`, engines in `EngineManager.Catalog`, editor UI.
- [ ] Legacy `.doc`/`.ppt`: pictures and bold/italic (`Office/LegacyOffice.cs`, [MS-DOC] CHPX, [MS-PPT] StyleTextPropAtom).
- [ ] LibreOffice engine footprint: prune `help/`, `share/gallery` etc. after MSI admin extraction
      (`Engines/EngineManager.cs` `ExtractMsiAsync`), verifying the sweep still passes.

## Known bugs and limitations

| Issue | Where | Notes |
|---|---|---|
| Drag probe fails on GitHub-hosted runners | `tests/Orange.DragProbe` | SendInput isn't delivered there; informational only |
| Reading Explorer selection not verifiable in CI | `Services/Explorer.cs` | Explorer window isn't foreground on runners |
| HEIC write unsupported | `ImageProvider` | No encoder in Magick.NET Windows build |
| Elevated drag sources can't drop on Orange (UIPI) | Windows | By design; documented |
| Hooks blocked by security software disable drag activation | `DragWatcher` | Shortcut, context menu, Send To still work |
| Office → PDF is "structure, not pixels" | `Office/*` | Install LibreOffice engine for exact layout |
| Optional LibreOffice engine is 1.5 GB extracted | `EngineManager` | Outside the app; user-initiated |
| Trimmed publish broke CLI and WinUI | csproj | Shipping untrimmed (commit `1aeeee5`) |
| Workspace snapshots in the Hark sandbox once reverted local commits | dev env | Always push; re-fetch branches from origin if files vanish |
