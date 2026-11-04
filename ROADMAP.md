# Roadmap

The 0.2.0 plan with its 18 items is in [PLAN.md](PLAN.md) (kept as the historical brief); item-by-item status is
in [STATUS.md](STATUS.md). This file sets priorities from here. Concrete next steps are in [TASKS.md](TASKS.md).

## Now: finish 0.2.0 (release blocker order)

1. **Real-PC proof.** The owner runs `tools/checklist/Run-OrangeChecklist.ps1` on Windows 11 (and Windows 10 if
   available) with the `nightly` build and sends the report: drop-to-convert, double-Shift, Shift+Ctrl mid-drag,
   editor panels, shelf. Fix whatever fails. Nothing in STATUS.md becomes Done without this.
2. **Installer size < 90 MB** (now 101.4 MB). Main lever: finish the small LGPL FFmpeg build
   (`.github/workflows/build-ffmpeg.yml` → release `ffmpeg-orange`; CI picks it up automatically). Keep the
   installed app < 400 MB (now 389.1 MB).
3. **Code signing** (item 4) once the owner adds a PFX or Azure Trusted Signing secrets; this also activates the
   Windows 11 top-level context menu (item 9).
4. **Docs/UI consistency** small fixes listed in TASKS.md (CLI usage text, CA1416 warnings).
5. Tag `v0.2.0` → CI publishes a release.

## Next: 0.3

- **Offline AI tools** (item 11, not started): Windows.Media.Ocr for images/PDF OCR, whisper.cpp as an optional
  engine for transcription, U²-Net (Apache-2.0) background removal via ONNX Runtime; downloads through
  `EngineManager` with pinned hashes. Avoid non-commercial models (RMBG-1.4).
- HEIC writing only if an LGPL/permissive encoder path appears (see DECISIONS.md D-9).
- Better fidelity for legacy .doc/.ppt (pictures, basic formatting) in `Office/LegacyOffice.cs`.
- Smaller LibreOffice engine footprint (1,504 MB extracted) or a lighter optional route.

## Later / ideas (not committed)

- ARM64 build tested in CI.
- Sandboxing FFmpeg/Ghostscript in an AppContainer or job object with no network (`docs/audit.md` §6).
- Native-speaker review of hi-IN and te-IN strings.
