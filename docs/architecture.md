# Orange for Windows — architecture and Windows constraints

## 1. What Orange reproduces, and how

| Tangerine (macOS, observed) | Orange (Windows) | Windows mechanism |
|---|---|---|
| Menu-bar app that waits for a drag | Notification-area (tray) app, starts at sign-in (opt-in) | `Shell_NotifyIcon`, `HKCU\…\Run` |
| Drag a file, tap **Shift** → convert wheel | Drag in File Explorer or on the desktop, tap (or hold) **Shift** → convert wheel | `WH_MOUSE_LL` + `WH_KEYBOARD_LL` observe-only hooks; wheel = top-most, non-activating WinUI 3 window that is an OLE drop target |
| **Command** → tools, **Control** → actions | **Ctrl** → tools, **Alt** → actions (all three remappable) | same hooks; keys switch wheels while it's open |
| Wheel stays open after the key is released, until drop/cancel | Same; Esc closes; a drop outside the wheel just closes it | `DragEnded` from the mouse hook + drop timeout |
| Drop on a petal runs it | Drop on a wedge runs it with remembered settings; tools with settings show a settings sheet first (switchable) | `DragOver` hit-testing by angle and radius; `Drop` reads `StorageItems` |
| Enter on a focused file → wheel; arrows/Enter/Esc | **Ctrl+Alt+O** (configurable) on the Explorer selection → wheel; ←/→, Home/End, Enter/Space, 1–9, Tab switches wheels, Esc; typing jumps to search | `RegisterHotKey`; selection read through `Shell.Application` automation |
| Saves beside the original, never overwrites, "name Suffix.ext", " 2" on clash | Identical naming rules; optional fixed output folder; Keep both / Replace / Stop | `SafePaths.PlanOutput` |
| Progress toast, reveal in Finder | Progress window bottom-right ("Converting to MP3 · 42%", "1 of 1 saved", Show in folder, Details), Windows notification, reveal in Explorer | `AppNotificationManager`, `SHOpenFolderAndSelectItems` |
| AirDrop, Float, Copy, Move, Copy Path, Terminal | Share (Windows share sheet), Copy (to clipboard as files), Move to…, Copy path, Terminal (Windows Terminal/PowerShell), Show in folder | `IDataTransferManagerInterop`, `Clipboard.SetContent`, `FolderPicker`, `wt.exe -d` |
| Not applicable | Right-click "Convert with Orange", Send To → Orange | `HKCU\Software\Classes\*\shell\Orange`, Send To `.lnk` |

## 2. Can Shift + drag work inside File Explorer?

**Short answer: yes, as an overlay, not as an Explorer extension.** Findings, in order of how we checked them:

1. **Explorer offers no extension point for "modifier pressed during a drag".** Shell extensions can add drop handlers for *targets* (file types you drop onto) and drag-drop handlers that add items to the right-drag menu, but nothing runs while a left-drag is in flight, and Shift already means "move" to Explorer. A shell extension can't reproduce the interaction.
2. **What works: observe input globally, show our own drop target.** A drag in Explorer is an OLE `DoDragDrop` loop. Low-level mouse and keyboard hooks still see the button and the key presses during that loop (the loop polls key state; it doesn't consume the hook chain). Orange watches for *left button down → moved past `SM_CXDRAG/SM_CYDRAG` → wheel key pressed*, then shows a top-most, non-activating window under the cursor. Because that window is a registered drop target, the cursor is already over it, so Explorer's own drag loop delivers `DragEnter/DragOver/Drop` to it. Nothing in Explorer is patched or injected.
3. **Modifier semantics.** Explorer turns Shift into a *move* and Alt into *create shortcut*. Orange's wheel only ever answers `DROPEFFECT_COPY` in `DragOver`, so Explorer copies (never moves or deletes) the source regardless of the keys held. The probe checks that the source file still exists after a Shift-drag.
4. **Which files are being dragged?** The hooks can't see the drag payload. The wheel pre-fills from the Explorer selection of the window the drag started in (Explorer drags the selection) and replaces it with the real payload as soon as `DragEnter` fires — which happens immediately because the wheel opens under the cursor.
5. **Limits we found and don't hide:**
   - **Elevated Explorer** (rare) or any elevated drag source can't drop into a non-elevated window (UIPI). Orange never elevates; the wheel simply won't receive the drop.
   - **Virtual items** (inside a ZIP folder view, phone/MTP, some cloud views) have no file-system path; Orange tells the user to copy them out.
   - **Security software** sometimes blocks global low-level hooks. If installing hooks fails, Orange says so and the shortcut, context menu and Send To paths keep working.
   - **Remote Desktop / some games in exclusive full screen** may not show the overlay.
   - The desktop's own selection isn't exposed through `Shell.Application`; for desktop drags the wheel learns the files from `DragEnter` instead.
6. **Alternatives built in, all reliable:** global shortcut on the Explorer selection, classic context-menu verb (Windows 11 shows it under *Show more options*; a top-level Windows 11 entry needs an `IExplorerCommand` in an MSIX or sparse package — not shipped), and Send To (one launch with the whole selection). Second launches are forwarded to the running instance (`AppInstance.FindOrRegisterForKey` / `RedirectActivationToAsync`) and gathered for 350 ms so a multi-file context-menu click becomes one wheel.

**Verification status** is reported per build in CI (see `docs/audit.md`): the self-test checks hook installation, hotkey registration, Explorer integration, the wheel window and its hit-testing, and conversions through the app service on real Windows; the drag probe drives the real mouse and keyboard with `SendInput` and performs (A) an OLE drag from a test window and (B) a drag from a real File Explorer window, and checks the wheel appeared, the drop landed on the intended wedge, the output exists and the source wasn't moved.

## 3. Components

```
Orange.sln
├─ src/Orange.Core        net8.0 — engine, no UI, cross-platform, fully unit-tested
│  ├─ Abstractions        OperationDescriptor / OptionSpec / IOperationProvider / OrangeException
│  ├─ OperationRegistry   all operations; context-aware subset per selection; search
│  ├─ JobRunner           validation, batch planning, bounded parallelism, progress, cancellation
│  ├─ FileTypeDetector    content signature first, extension second; mismatch → refuse
│  ├─ SafePaths           input checks, Windows-safe names, output planning, temp workspaces, atomic commit, zip-slip guard
│  ├─ Tools               ToolLocator (bundled → PATH, absolute dirs only), ProcessRunner (no shell, ArgumentList, timeouts, kill tree)
│  └─ Providers           Image (Magick.NET) · Media (FFmpeg) · Pdf (PDFsharp, PdfPig, Ghostscript) · Document (Pandoc, LibreOffice) · Subtitle · Archive
├─ src/Orange.Cli         orange-cli: same engine for scripts/CI (ops, search, run, matrix, tools)
├─ src/Orange.App         WinUI 3 (Windows App SDK 1.8), unpackaged, self-contained
│  ├─ Program             single instance + activation redirection
│  ├─ App                 tray, hotkey, drag watcher, argument queue
│  ├─ Services            DragWatcher, ExplorerSelection/Integration, TrayAndHotkey, FileActions, ConversionService, AppSettings
│  ├─ Views               WheelControl (vector wedges), WheelWindow (overlay + drop target + keyboard mode), OptionsWindow, ProgressWindow, MainWindow
│  └─ SelfTest            --selftest: in-process checks of the real Windows pieces
├─ tests/Orange.Core.Tests   xUnit: real conversions on fixtures, security cases
├─ tests/Orange.DragProbe    real-input end-to-end drag test (SendInput, OLE, File Explorer via UI Automation)
├─ tests/fixtures            sample media/docs + hostile files
└─ installer/Orange.iss      per-user Inno Setup installer
```

### Stack decisions

| Need | Chosen | Evaluated and why not (or not yet) |
|---|---|---|
| UI | **WinUI 3 / Windows App SDK**, C#; UI built in code | WPF (older look, no Mica/acrylic without work); C++ not needed — every shell API used is reachable from C# |
| Images | **Magick.NET** (ImageMagick 7 in-process, Apache-2.0): every format incl. AVIF, HEIC read, animated GIF/WebP, resource limits | WIC: native and fast, but no WebP/AVIF writer by default and HEIC needs paid store extensions; still the best route to HEIC *writing* later |
| Video/audio | **FFmpeg** as a child process | Media Foundation transcoding: fewer formats; kept only as `h264_mf` fallback encoder inside FFmpeg |
| PDF structure | **PDFsharp 6.2.4 (MIT)** | PDFsharp ≥ 6.5 is AGPL → pinned; iText (AGPL), PDFium (render only) |
| PDF text/images | **PdfPig** (Apache-2.0) | — |
| PDF compress/render | **Ghostscript** (AGPL), optional, user-installed, `-dSAFER` | PDFium via Docnet for rendering (possible future, permissive) |
| Documents | **Pandoc** (`--sandbox`), **LibreOffice** headless for → PDF (optional) | Word COM automation: only where Office is installed, and runs macros/add-ins → avoided |
| Archives | System.IO.Compression / System.Formats.Tar | RAR: no permissive decoder → not supported |

### Operation model

Each operation is a descriptor (`id`, category, wheel, input extensions, output extension or "same", options with type/range, keywords, required tool). The registry exposes:

- `ForFiles(files, wheel)` — only operations valid for *every* selected file, same kind, hiding conversions to the format the files already are, multi-file tools first for multi-selections;
- `Search(text)` — every term must match title, id, category, extension or keywords ("to mp3", "compress pdf").

The wheel shows at most eight wedges (favourites, then recently used, then default order) plus **More**, which opens the searchable list of everything in the main window. Unavailable operations (missing tool, encoder not in this FFmpeg build, HEIC writer missing) are hidden from the wheel and shown greyed with the reason in the list. `orange-cli matrix` prints the live matrix for a machine.
