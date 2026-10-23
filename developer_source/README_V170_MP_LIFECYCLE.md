# SonsVR V170 — multiplayer loading/menu/title lifecycle

Self-contained source kit based on the community V169L release. No HUD compositor,
world-space HUD layout, AER reconstruction, DLSS, or optical-flow code was changed.

## Evidence (2026-10-23 multiplayer reproduction)

- `SonsMainLoading` started a V146 loading-cover epoch at 10:59:55.942.
- At 11:00:45.856, tracked `MainCameraFP` belonged to `DontDestroyOnLoad`.
- At 11:00:55, both VR-eye masks remained `0x80000000` and HDRP volume masks
  were zero even though the world was interactive. The cover had never released.
- In an earlier multiplayer session, F9 identified an active
  `PlayerStandin/PlayerUI/ScreenSpaceCanvas/PauseMenu/PauseMenuPostProcessingVolume`
  (weight 1), but all six V145 global `FindObjectsOfType<Volume>()` searches
  missed it.
- V159's title yaw correction was armed only by AER gameplay readiness, which
  multiplayer never reached under the original `SonsMain` camera-owner gate.

## Targeted source changes

1. Recognize **both** `SonsMain` and `DontDestroyOnLoad` as valid owners of
   `MainCameraFP`, **while retaining every V146 spawn/ground/floor/motion gate**.
   A multiplayer release now logs `V170 LoadingCover RELEASE`.
2. Arm deferred AER in multiplayer only *after* a verified V146 release. The
   single-player V43 camera/player gate remains unchanged. Keep the deferred
   HDRP-camera-copy and end-of-frame AER transition.
3. Restore native `MainCameraFP` HDRP eye profile for either ownership scene.
4. Arm the existing one-shot V159 title-yaw correction after a native gameplay
   camera has been adopted, even when AER readiness was previously unavailable.
5. Resolve the *specific* pause blur Volume via the known cached pause-menu
   Transform rather than allocating expensive scene-wide Volume scans. Suppress
   only that Volume, keeping V169A lifetime semantics.
6. Add throttled `V170 LoadingCover WAIT_CONTEXT` logging (once per 3 seconds)
   when the tracked camera/player gate is missing during an active loading epoch.

## Build on Windows

Prerequisites: installed game + MelonLoader IL2CPP interop assemblies, Visual
Studio/MSBuild setup from prior builds, NVIDIA OF SDK/native build prerequisites,
and Unity Editor 2022.2.16f1 for the bundled shader AssetBundle.

From this directory, run:

```
BUILD_RELEASE.bat "D:\SteamLibrary\steamapps\common\Sons Of The Forest"
```

Adjust path/`UNITY_EXE` as required. After a successful build, install the two
DLLs from `OUTPUT` into the game's `Mods` folder by your usual process.
There is deliberately **no old V169L DLL** in this kit: it would be misleading
because these V170 changes are in managed source and must be compiled.

## Single-player and multiplayer HMD verification

1. Multiplayer, self-hosted, saved game: Load and leave hands/controller still
   until the spawning transition finishes. The overlay should disappear after
   the V146 physical gate; the log should say `V170 LoadingCover RELEASE` and
   `V114 LoadingPerf gameplay gate ready` (without ever needing to press B).
2. Multiplayer, menu: open pause menu. Blur should remain OFF. The V145 log
   should show `found=True` and a very small lookup cost; no broad volume search.
3. Multiplayer, title: Exit to main menu. Title scene and yaw should face the
   native menu, with one `V159 TitlePreUI ALIGNED` if the game changed heading.
4. Multiplayer, selected AER mode: After world loads, `AER V170 gameplay gate
   ARMED` should appear; activation itself remains deferred until HDRP copy/EOF.
5. Single-player regression: Repeat Load Game, New Game cutscene, pause-menu
   blur, return-to-title, and both 2E/AER modes. Preserve the V116 HUD path.

If the loading cover still persists, take F9 after a short walk and return
`Latest.log` plus the F9 snapshot; `WAIT_CONTEXT` / `WAIT_SPAWN` identifies
which precise gate is blocking release.

## Validation status

Source-level patch and static checks only in the provided Linux workspace.
Compilation, MelonLoader IL2CPP injection, and in-HMD outcomes require the
Windows game/build environment. Treat V170 as an untested diagnostic fix until
these checks pass; V169L remains the rollback.
