# Build, install, sign and release Orange

## Prerequisites (developer machine)

- Windows 11 (or 10 1809+) x64
- .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`)
- Optional, for full engine tests: `winget install Gyan.FFmpeg JohnMacFarlane.Pandoc ArtifexSoftware.GhostScript TheDocumentFoundation.LibreOffice`
- Optional, for the installer: Inno Setup 6 (`winget install JRSoftware.InnoSetup`)
- Visual Studio 2022 with the *Windows application development* workload is optional (the CLI is enough).

## Build and test

```powershell
git clone https://github.com/Hem456anth/Orange.git; cd Orange
dotnet test tests/Orange.Core.Tests -c Release            # set ORANGE_REQUIRE_TOOLS=1 to fail instead of skip when a converter is missing
dotnet publish src/Orange.App/Orange.App.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained -o out/Orange
# ARM64: -r win-arm64 -p:Platform=ARM64
```

Bundle the converters next to the app (the CI workflow does exactly this):

```
out/Orange/tools/ffmpeg/bin/ffmpeg.exe, ffprobe.exe, LICENSE.txt, SOURCE.txt   (BtbN win64 GPL build)
out/Orange/tools/pandoc/pandoc.exe, COPYING*, SOURCE.txt                         (official release zip)
```

Orange looks in `tools\` first, then PATH (absolute directories only), then the path set in *Settings → Converters*. Ghostscript and LibreOffice are detected in their default install folders and are never bundled.

## Verify on a real desktop

```powershell
out\Orange\Orange.exe --selftest out\selftest      # exit code 0 = required checks passed; see selftest.json and wheel-*.png
dotnet build tests/Orange.DragProbe -c Release -o out\probe
out\probe\Orange.DragProbe.exe out\Orange\Orange.exe tests\fixtures out\probe-results   # moves your mouse! don't touch it for ~30 s
```

Manual checklist (do once per release on Windows 11 and Windows 10, 100 % and 150 %/200 % scaling, light and dark):

1. Drag a PNG in Explorer, tap Shift → wheel under cursor; hover wedges (orange highlight, centre label changes); drop on WebP → `name.webp` beside the original, original untouched.
2. Same while *holding* Shift before the drag. Then Ctrl (tools) and Alt (actions); switch keys while the wheel is open; Esc closes it.
3. Drop outside the wheel → nothing converted, nothing moved.
4. Select 3 images → Ctrl wheel → Create PDF; select 2 PDFs → Merge PDFs.
5. Ctrl+Alt+O with a selection → keyboard wheel; ←/→, Enter, Tab, 1–9, Esc. Narrator reads each wedge ("Convert to WebP").
6. Right-click → Show more options → Convert with Orange (multi-select → one wheel). Send to → Orange.
7. Trim a video with the settings sheet; cancel a long conversion; a damaged file shows an error with Details.
8. Desktop drag; drag from a non-Explorer app with *Drag sources = Any app*.

## Installer and portable build

```powershell
Compress-Archive out/Orange/* out/Orange-0.1.0-win-x64-portable.zip
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DAppVersion=0.1.0 installer/Orange.iss   # → out/Orange-0.1.0-win-x64-setup.exe
```

The installer is per-user (`%LOCALAPPDATA%\Programs\Orange`, `PrivilegesRequired=lowest`), offers Send To, the right-click verb and start-at-sign-in, and removes all of them on uninstall. Settings in `%LOCALAPPDATA%\Orange` are kept unless the user deletes them from *About & privacy*.

## Code signing

Unsigned builds work but SmartScreen warns on first run. Sign `Orange.exe` (and ideally all first-party DLLs) and the setup executable.

**Option A — certificate (.pfx, OV/EV) in CI.** Add repository secrets `SIGNING_CERT_PFX_BASE64` (base64 of the .pfx) and `SIGNING_CERT_PASSWORD`. The `app` job's *Optional code signing* step then runs:

```powershell
signtool sign /f cert.pfx /p <password> /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 out\Orange\Orange.exe out\*.exe
```

Sign before packaging the zip if you want the portable build signed too (move the step above *Package*), and sign the installer after ISCC (Inno can also call signtool via `SignTool=` in `[Setup]`).

**Option B — Azure Trusted Signing** (no hardware token): create a Trusted Signing account and certificate profile, add `azure/trusted-signing-action` with `files-folder: out` and `file-digest: SHA256`, using a federated (OIDC) credential.

Verify: `signtool verify /pa /v out\Orange\Orange.exe`.

## Release

1. Bump `<Version>` in `src/Orange.App/Orange.App.csproj`, `AppVersion` in CI and the installer.
2. Update THIRD-PARTY-NOTICES.md if a dependency changed (check NuGet license fields; PDFsharp must stay < 6.5 or the app becomes AGPL).
3. Push to `main`; CI runs engine tests, builds, runs the self-test and drag probe, packages and uploads *orange-win-x64*.
4. Run the manual checklist above on real hardware.
5. Create a GitHub release with the zip, the installer, SHA-256 hashes (`Get-FileHash`), and the FFmpeg/Pandoc source links (GPL offer).

## Signing (0.2)

`tools/sign/Sign-Files.ps1` signs the app, CLI, context-menu DLL, sparse MSIX and installer. Configure **one** of:

| Secret | Meaning |
|---|---|
| `SIGNING_CERT_PFX_BASE64`, `SIGNING_CERT_PASSWORD` | a code-signing .pfx (base64) and its password |
| `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `TRUSTED_SIGNING_ENDPOINT`, `TRUSTED_SIGNING_ACCOUNT`, `TRUSTED_SIGNING_PROFILE` | Azure Trusted Signing |
| `SIGNING_CERT_SUBJECT` | the certificate subject (e.g. `CN=Hemanth Kundrapu`), written into the sparse package's Publisher |

Without secrets every signing step prints "signing skipped" and the build stays green. The Windows 11 top-level
menu needs the signed sparse package; unsigned builds keep the classic "Show more options" verb and Send To.
Pushing a tag `v0.2.0` builds, signs (when configured) and publishes a GitHub release with CHANGELOG.md as notes.
