# Orange command line

The installer can add `orange` to your PATH (task *Add the orange command to PATH*, off by default). The portable zip
has `cli\orange.cmd` (it runs `Orange.exe --cli`) that you can put on PATH yourself. Everything runs on your PC.

```text
orange convert <files|globs> --to <format> | --preset <name> [-o <dir>] [--quality <1-100>] [--overwrite keep|replace|fail] [--json]
orange tools list
orange presets list | import <file.json> | export <file.json>
orange watch <dir> --preset <name> [--move-originals]
orange ops <files>          # what Orange offers for these files
orange search <words>       # search every operation
orange matrix               # every operation and whether it's available on this PC
orange run <op-id> <files> [--opt name=value]... [-o <dir>]
```

## Examples

```bat
orange convert *.png --to webp --quality 80 -o web
orange convert "C:\Videos\*.mp4" --preset "WhatsApp video"
orange convert report.pdf --preset "Email PDF" --json
orange presets export my-presets.json
orange watch "%USERPROFILE%\Downloads\To WebP" --preset "WebP for web" --move-originals
orange run image.resize photo.jpg --opt mode=longest --opt value=1600
```

Globs (`*`, `?`) expand in the file-name part. Outputs go beside the originals unless `-o` is given; Orange never
overwrites an input. With `--preset`, the preset's name pattern, subfolder and conflict rule apply (`-o` moves results
into that folder instead).

## JSON output

`--json` prints one object: `{"files":[{"input":"…","status":"done|failed|skipped|cancelled","output":"…","error":null}]}`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | every file was converted |
| 1 | some files failed or were skipped |
| 2 | usage error (unknown command, missing option, unknown preset) |
| 3 | nothing to do: no files matched, or none can be converted that way |
| 4 | internal error |
| 130 | cancelled with Ctrl+C |

## Watch folders

`orange watch` runs in the foreground until Ctrl+C. For watch folders that survive restarts, add them in
Orange → Settings → Watch folders; the tray app runs them, and the tray menu can pause and resume them.
A watcher waits until a new file stops growing and can be opened exclusively, ignores partial downloads
(`.crdownload`, `.part`, `.tmp`…), its own results and the `Originals` folder, and handles one file at a time.
