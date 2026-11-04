# Data storage

**Orange has no database.** It keeps a few JSON/text files per Windows user under `%LOCALAPPDATA%\Orange` (no
registry for settings, no cloud). The engine and the app share the same files. Writers use write-to-temp then
rename where noted. Deleting the folder resets Orange (Settings → About & privacy also offers this).

```
%LOCALAPPDATA%\Orange\
├─ settings.json            app preferences, recents, favourites, wheel layout          (Orange.App AppSettings)
├─ presets.json             user presets; absent = the six defaults                     (Orange.Core PresetStore)
├─ presets.json.bad         copy of an unreadable presets.json (then defaults are used)
├─ watches.json             watch folders                                               (Orange.Core WatchStore)
├─ menu.txt                 pinned presets for the Windows 11 menu DLL                  (ConversionService)
├─ engines\<id>\<version>\  optional engines + engine.json                              (EngineManager)
├─ logs\orange-yyyyMMdd.log opt-in debug log, 2 MB per file, 7 days                     (LocalLog)
├─ preview\                 editor preview PNGs, removed when the panel closes          (Editors.cs)
└─ crash.log                unhandled exceptions, local only                            (App.xaml.cs)
```

Temporary job workspaces live in the system temp folder, are private per job, deleted in `finally`, and stale ones
(> 24 h) are purged at start (`SafePaths.TempWorkspace`).

## settings.json — `src/Orange.App/Services/AppSettings.cs`

| Field | Type / default | Meaning |
|---|---|---|
| `SettingsVersion` | int, 3 | Schema version. Files < 3 are migrated once on load to the 0.2 keys only if the user kept the old defaults |
| `DragActivation` | bool, true | Shift-drag on/off |
| `ConvertKey`, `ToolsKey`, `ActionsKey` | `WheelKey`: Shift, ShiftDouble, ShiftCtrl | Wheel gestures (`Shift, Ctrl, Alt, AltShift, ShiftDouble, ShiftCtrl`) |
| `KeyHintsShown` | int | Hub key hint shown for the first drags |
| `DragSources` | `DragSourceFilter`, ExplorerAndDesktop | Which drag sources open the wheel |
| `Hotkey`, `HotkeyEnabled` | "Ctrl+Alt+O", true | Keyboard wheel |
| `OutputFolder` | string?, null | null = beside the original |
| `Overwrite` | `OverwritePolicy` KeepBoth / Replace / Fail | Name clash rule |
| `RevealWhenDone`, `NotifyWhenDone`, `AskOptionsForTools`, `PlaySounds`, `StartWithWindows` | bool, true | Behaviour toggles |
| `Theme` | `ThemeChoice`, System | |
| `WelcomeShown` | bool | First-run window shown |
| `DebugLog` | bool, false | Local log on/off |
| `Language` | string?, "" | "" follows Windows; en-US, hi-IN, te-IN |
| `CheckForUpdates`, `LastUpdateCheck` | bool false, DateTimeOffset? | Opt-in update check |
| `MaxParallelJobs` | int, half the CPU count (≥ 1) | |
| `MaxInputGigabytes` | double, 8 | Input size limit |
| `ToolPaths` | map tool → path | Manual tool locations |
| `LastOptions` | map op id → (option → value) | Remembered options ("WebP at 70") |
| `Favorites` | list of op ids | |
| `Layout` | `WheelLayout { Order, Hidden }`, keys like `"Image/Convert"` | Wheel editor |
| `Recent` | list of `RecentEntry(OperationId, Title, Category, When, Files, Outputs)`, max 50 | Recent conversions |

## presets.json — `src/Orange.Core/Presets/Preset.cs`, `PresetStore.cs`

`{ "Version": 1, "Presets": [ Preset… ] }`, enums as strings. Same format for import/export.

| Preset field | Meaning |
|---|---|
| `Name` (unique, case-insensitive) | Shown on the wheel |
| `OperationId` | Must exist in the registry (validated on load/import) |
| `Options` | op option → value, validated against the op's `OptionSpec`s |
| `Metadata` | `AsOperation` or `Strip` |
| `Output` | `{ Location: BesideOriginal\|Subfolder, Subfolder: "Orange", NamePattern: "{name} {preset}.{ext}", Conflict: KeepBoth\|Replace\|Skip }` |
| `PinnedOnWheel` | bool, true |

Defaults (`DefaultPresets.All`): WhatsApp video, Discord video, Email PDF, Web image, WebP for web, Podcast MP3.

## watches.json — `src/Orange.Core/Watch/FolderWatcher.cs` (`WatchStore`)

A JSON array of `WatchSpec { Folder, Preset, MoveOriginals, Paused }`. Written via `watches.json.tmp` + move.

## engines\<id>\<version>\engine.json — `src/Orange.Core/Engines/EngineManager.cs`

`InstalledEngine { Id, Version, ExePath, InstalledAt }`. `EngineManager.Installed()` enumerates these files;
`Register()` points `ToolLocator` at each `ExePath`. Uninstall deletes the folder.

## menu.txt

One `preset|Name|kind` line per pinned preset, read by `src/Orange.ShellExt/OrangeShellExt.cpp`.

## Relationships

- A preset references an operation id; a watch references a preset by name; `Favorites`, `LastOptions`,
  `Layout` and `Recent` reference operation ids. Unknown ids are refused (presets) or ignored (UI lists).
- Engines change which tool `doc.to.*`, `ppt.to.pdf` and `pdf.compress` use; without them the built-in engines run.
