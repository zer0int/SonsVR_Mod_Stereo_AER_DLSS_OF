# SonsVR Bug Reporter V3 — privacy-reduced and full collection

The F9 diagnostic capture is already part of the SonsVR mod. These scripts only
read existing files and create a local ZIP. They never upload anything, edit
the game, copy saves/screenshots/crash dumps, or need administrator rights.

## Which button should I use?

| Batch file | Recommended for | ZIP name |
| --- | --- | --- |
| **`COLLECT_SONSVR_LOGS_PRIVATE.bat`** | **Most non-crash mod bugs**: stuck loading overlay, UI placement, pause blur, title rotation | `SONSVR_PRIVATE_<timestamp>.zip` |
| `COLLECT_SONSVR_LOGS.bat` | Crash/startup failure, loader conflicts, GPU/driver/DLSS investigations, or when explicitly requested | `SONSVR_DIAGNOSTICS_<timestamp>.zip` |

The privacy-reduced option is the recommended starting point. It intentionally
omits system information and old sessions, even if doing so costs diagnostic
detail. If a special bug needs additional information, a maintainer can ask for
just that file later.

## How to report a bug

1. When the issue is visible, press **plain F9** once in-game; note the local
   time in the FPS overlay. The snapshot may briefly hitch. Do not use
   Ctrl+Alt+F9 (the settings shortcut).
2. Extract this kit into a **writable folder** (preferably on D: if available)
   and leave its `.bat` and `.ps1` files together.
3. Double-click **`COLLECT_SONSVR_LOGS_PRIVATE.bat`**. If asked, paste the game
   installation folder containing `SonsOfTheForest.exe` (e.g.
   `D:\SteamLibrary\steamapps\common\Sons Of The Forest`). You can also pass
   that folder as the batch file's first argument.
4. When `SUCCESS` appears, open the resulting `SONSVR_PRIVATE_*.zip` next to
   the scripts and **inspect it** before posting publicly.
5. Create a GitHub issue explaining reproduction steps, local incident time,
   SonsVR version, selected AER/2E mode, and what you saw. Attach the ZIP.

Collect soon after the issue. The filtered `Latest.log` always comes from the
current or most recent session; opening the game again may overwrite it.

## What each diagnostic file can expose

| File/category in a **full** ZIP | Potentially sensitive information | Needed for ordinary non-crash mod bugs? |
| --- | --- | --- |
| `Unity/Player.log` | GPU model, VRAM, driver, Unity/graphics info, screen resolutions, absolute install paths, command-line arguments, possibly subsequent game logs | **No**; omit the entire file |
| `MelonLoader/Latest.log` | OS/runtime info, full installation and library paths, loaded mod names/hashes, settings, arbitrary game/mod messages | **Yes, but only the mod-related lines** |
| `MelonLoader_OldLogs/*.log` | Everything above, across earlier sessions | No; omit by default |
| `Mods_Traces/SonsVR_*` | Detailed AER/DLSS/timing measurements, file paths/settings, potentially high-resolution display dimensions and scene names | Only on request for performance-specific faults |
| `F9_Snapshots/V*_UI_*.txt` | Scene and camera state, *world coordinates*, object names, volume/canvas details; the full snapshot also includes the **actual contents of UI text**, potentially chat or player names | Useful, but a reduced selection suffices for initial triage |
| `F9_Diagnostics/*`, `Unity_Telemetry/SonsVR_*` | Various specialized diagnostics; may contain file paths, rendered dimensions, state, or player-related strings | Not by default |
| `MANIFEST.txt` | **Full mode includes every original absolute source file path**, even if a log was skipped | Private mode lists **only archive categories and file names** |

Removing only the startup section of `Player.log` is not a dependable way to
sanitize it: private information may appear anywhere later. If a developer
does not specifically need Unity's full log, **delete the whole `Player.log`**
before posting an older full collector ZIP. Also review `MANIFEST.txt`,
`Latest.log`, and F9's UI text: these are independent disclosure channels.

## What the privacy-reduced collector actually includes

- Up to **three newest F9 snapshots** (which may be from earlier sessions),
  keeping only the header plus `PLAYER`,
  `CAMERAS`, `VOLUMES`, and `CANVASES` sections. It excludes the `TMP_TEXT`,
  `UGUI_TEXT`, UI hierarchy, and black-surface scans.
- Only **SonsVR-related lines from the current**
  `MelonLoader/Latest.log`, with any lines matching common absolute paths,
  hardware, account, IP/MAC address, email, GUID, or Steam64 ID patterns omitted.
- A minimal `MANIFEST.txt` with no absolute source paths.

It does **not** include `Player.log`, full MelonLoader headers, rotated old
MelonLoader logs, or `Mods_Traces` performance tables. Filtering takes place in a
separate staging folder; **the originals are never modified**. The temporary
folder is removed when collection finishes. The ZIP is created next to the
scripts and can be opened with Windows Explorer before uploading.

### Important privacy limitation

**Privacy-reduced does not mean anonymous or guaranteed free of identifiers.**
No generic filter can reliably interpret arbitrary text emitted by every game
mod, including unusual naming conventions. The retained camera data includes
in-game coordinates, and object names or free-form diagnostic messages can
still reveal information. Users should always inspect the ZIP before posting
it publicly; omit anything they are not comfortable sharing.

For crashes, loader conflicts, headset/graphics-driver failures, or detailed
performance analysis, the **full collector** may be necessary, but attach
those additional files only when the issue actually requires them.

## Troubleshooting

- **F9 created no snapshot:** check that the build includes F9 support, and
  that the game had focus. Snapshots are stored under the game's
  `AppData\LocalLow\Endnight\SonsOfTheForest\SonsVR_Diagnostics` directory.
- **No files found:** verify the installation folder and that Windows is using
  the same account under which the game generated its logs.
- **The ZIP contains no MelonLoader log:** the game path may not have been
  supplied. Rerun the private collector and enter the installation folder.
- **Collector failed:** report the error printed in the console. You can also
  attach the exact F9 snapshot after checking its contents manually.

The batch launcher uses `-ExecutionPolicy Bypass` for that PowerShell process
only. It does not change the machine's persistent policy.
