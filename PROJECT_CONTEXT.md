# Project context

_State as of main `3306a0e` (PR #14 merged 2026-10-20, CI run 61 green). Update the "Current state" section with every merged PR._

## Background

- **Owner:** Hemanth Kundrapu (GitHub `Hem456anth`). Repo: https://github.com/Hem456anth/Orange (public, branch `main`).
- **Goal:** a Windows 11/10 equivalent of Tangerine for Mac's workflow: drag files, tap Shift, pick a format on a
  circular wheel, drop, and get the converted file beside the original, all offline on the user's PC.
- **Clean room:** Orange was designed from Tangerine's public website, videos and press (findings in
  `docs/research/tangerine-findings.md`) and an inventory of a Tangerine 2.0.2 bundle the owner supplied
  (`docs/research/tangerine-binary-findings.md`, method in `docs/reverse-engineering-report.md`, tool
  `tools/re/bundle_inventory.py`). No Tangerine code, binaries or assets are in the repo.
- **Licence:** MIT for Orange's code (`LICENSE`). Third-party notices: `THIRD-PARTY-NOTICES.md`, analysis: `docs/licensing.md`.

## History in one paragraph

0.1.0 (2026-10-18/19, commits `1323588` to `75c19ca`) built the engine, CLI, WinUI app, wheel, drag watcher,
installer, CI with self-test and evidence publishing. The owner tested a nightly on his own Windows 11 laptop on
2026-10-18: the wheel opened mid-drag. 0.2.0 was planned as 18 items (`PLAN.md`) and delivered as PRs #3–#13 on
2026-10-19/20, followed by PR #13 (field fixes: ffprobe discovery, tray crash, performance, size) and PR #14
(built-in clean-room document/legacy-Office/PDF engines plus an end-to-end converter sweep). Version in the
csproj files is `0.2.0`; no `v*` tag has been pushed yet, so there is no non-nightly release.

## Current state

| Area | State | Evidence |
|---|---|---|
| Engine (`Orange.Core`) | 122 operations in 10 providers; 257 tests (255 pass, 2 skip locally) | `orange-cli matrix`, `dotnet test` |
| End-to-end sweep | Windows CI, installed app: 121 pass, 1 skip (`image.to.heic`), 0 fail, both with bundled tools only and with optional engines installed | `ci-evidence/run-59/sweep/*.json` |
| Windows app | Compiles, publishes, self-test passes on Windows Server 2025 CI desktop; screenshots of every wheel, panel and settings page | `ci-evidence/run-N/selftest/` |
| Size | App on disk 389.1 MB (limit 400 MB); installer 101.4 MB (target < 90 MB, not met); portable zip ~169 MB; bundled FFmpeg 159.8 MB | run 59 `summary.txt` |
| Real PC | Only "wheel opens mid-drag" is confirmed on the owner's laptop (2026-10-18). Drop-to-convert, panels, double-Shift and Shift+Ctrl mid-drag are **unverified** on real hardware | `docs/audit.md` §4 |
| Distribution | Rolling pre-release tag `nightly` (installer + portable zip) refreshed by every green CI run on `main` | https://github.com/Hem456anth/Orange/releases/tag/nightly |
| Signing | Not signed; wiring exists and skips when secrets are absent | `tools/sign/Sign-Files.ps1`, ci.yml |

## Known field report

2026-10-20, owner's photos: audio/video operations were hidden because `ffprobe` was not found beside the bundled
`ffmpeg` (`tools\ffmpeg\bin` inside the installed app folder). Fixed in PR #13; the self-test now runs with `ORANGE_BUNDLED_ONLY=1` so a tool on PATH
cannot hide a missing bundled one.

## Owner preferences that affect the code

- Orange colour scheme only, never blue (2026-10-19).
- Honest status: nothing is "Done" without a test or real-PC proof (`STATUS.md` header).
- One PR per item, merged on green CI.
- Keys: Shift = Convert, Shift twice = Tools, Shift+Ctrl = Actions (0.2 defaults, remappable).

## Where to continue

See [TASKS.md](TASKS.md) for the ordered next steps and [ROADMAP.md](ROADMAP.md) for priorities.
