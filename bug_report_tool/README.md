# SonsVR bug-report collector (Windows)

The **F9 capture feature is included in SonsVR itself** (V169L and later compatible builds).
These two scripts only gather existing diagnostics into one ZIP; no new mod/DLL is installed.

## How to report a bug

1. When something goes wrong in Sons Of The Forest VR, press **plain F9** once in-game (not Ctrl+Alt+F9). A brief hitch is normal while the snapshot is written. You can press it again for another incident.
2. Make a note of **what happened and the local time** shown in the FPS overlay if available. An optional screenshot is helpful.
3. Extract this archive anywhere on Windows, **keeping `COLLECT_SONSVR_LOGS.bat` and `COLLECT_SONSVR_LOGS.ps1` together**.
4. Double-click **`COLLECT_SONSVR_LOGS.bat`**. When prompted, paste your Sons Of The Forest installation folder, e.g. `D:\SteamLibrary\steamapps\common\Sons Of The Forest`. If it detects the right folder, press Enter. If you don't know the folder, press Enter to collect user-data diagnostics only.
5. Wait for `SUCCESS`. The collector creates **`SONSVR_DIAGNOSTICS_YYYYMMDD_HHMMSS_mmm.zip`** in the SAME folder as the scripts.
6. **Review the ZIP before sharing.** Then open a GitHub issue with the description, approximate time, SonsVR version, whether AER/2E was active, reproduction steps (if known), and the ZIP as an attachment. A screenshot can be attached separately.

The collector works even after the game is closed, provided the logs and snapshots are still on disk. Collect soon after an incident so that rotating logs haven't been overwritten.

## What it collects

- F9 UI/camera-state snapshots from Windows' Unity user-data folder (`AppData\LocalLow\Endnight\SonsOfTheForest\SonsVR_Diagnostics`)
- Selected recent `MelonLoader` and `Mods\SonsVR_*` traces from your game installation
- Unity's `Player.log` and selected SonsVR user-data telemetry
- A `MANIFEST.txt` detailing which files were included, missing, or skipped

It does **not** collect save games, screenshots, crash dumps, or unrelated files. It does not modify your game, install hooks, upload anything automatically, or require admin privileges. It limits individual files to 50 MiB and the total staged collection to 250 MiB; a ZIP can still be larger than a website's attachment limit. If the ZIP is too large, contact the maintainers rather than posting a truncated archive without explanation.

**Privacy:** Logs can contain your Windows user name, game install path, computer details, and other personal context. Open the ZIP to inspect it before publishing it in a public issue. The batch file launches Windows PowerShell with a process-local `-ExecutionPolicy Bypass`; it does not change persistent PowerShell settings.

## Troubleshooting

- **F9 did nothing:** Check that you're running a build with the F9 snapshot feature and that the game window received plain F9. Try checking `AppData\LocalLow\Endnight\SonsOfTheForest\SonsVR_Diagnostics` for a recent `V169L_UI_*.txt` file.
- **No diagnostic files found:** Check that you ran the game under the same Windows account and entered the *game installation* folder, not its `Mods` directory. Check the collector output for missing folders.
- **Collector failed:** Copy the error displayed in the console into the issue (or take a screenshot).

The collector scripts are the unchanged, already-used SonsVR Collector v2 scripts; only this public-facing README was added to the standalone package.
