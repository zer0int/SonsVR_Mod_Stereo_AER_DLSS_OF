# Changelog — key community-fork changes

This summarizes major user-facing and architectural work relative to the [upstream mod](https://github.com/iPowerTech/SonsVR_Mod). It is not a log of every experimental build. Most implementation details and regression history are documented in source comments.

## Current source snapshot — V163H + V168G settings note

### Stereo rendering and performance

- Implemented configurable full stereo (`2E`) and alternate-eye rendering (`AER`) with GPU depth reprojection, optical-flow integration and scene-aware hole handling. A guarded depth-consistency fill eliminated major previous stereo wobble without filling across unrelated depth layers. See `mod/StereoRender.cs`, `mod/HudStereoReprojectV60.cs`, `mod/NvOpticalFlowV90.cs` and `shader_project/`.
- Integrated a native D3D11 NVIDIA Optical Flow bridge and related GPU pipelines. The game's own graphics menu controls DLSS quality; the mod no longer overrides it through its custom settings. See `native_nvof/` and `mod/DlssRuntimeV91.cs`.
- Cached expensive interaction-object lookups and corrected pause/loading camera lifecycle behavior that previously caused severe frame-rate drops. See `mod/VRPlayer.cs`, `mod/PerformanceForensicsV106.cs` and `mod/ManualPauseFpsTraceV144.cs`.

### Native 3D menu interaction

- Replaced empirically offset mouse targeting with gaze-driven native hover for **cooking**, the **backpack quick-select**, and **blueprint/tutorial booklets**. Typed IL2CPP item lookup, per-interaction collider ownership and native hover callbacks allow selecting ordinary items, blueprint tabs and nested subtabs while preserving the game's native action handling. See `mod/VRPlayer.cs` and `mod/BookletGazeV161.cs`.
- Preserved ordinary tarp/menu interaction and stabilized UI object caches without full-scene searches each frame. See `mod/VRPlayer.cs`.

### Loading, cinematic cameras and construction

- Improved title-to-game, new-game cinematic and save-load transitions. VR eyes are aligned at scripted cinematic camera cuts, while the user's independent head movement remains free. Returning to the title resets height/yaw before positioning the menu UI. See `mod/VRPlayer.cs` and `mod/StereoRender.cs`.
- Fixed invisible construction/blueprint previews by restoring eye-camera HDRP state using the *current* gameplay camera profile after the loading cover releases, rather than an outdated pre-loading snapshot. See `mod/VRPlayer.cs`.
- Retained the faster startup/loading presentation without prematurely exposing incorrect transitional world views. See `mod/VRPlayer.cs`.

### HUD and settings

- Unified and stabilized the existing world-space minimap/status HUD layout. Shifted the shared HUD reference point upward by **0.07 m** for easier visibility around held equipment; native scene occlusion intentionally remains. See `mod/UnifiedHudWorldPlaneV116.cs`.
- Added in-HMD runtime settings with recentering, per-overlay scaling, keyboard and controller shortcuts, persistent INI migration, and render/optical-flow options. See `mod/RuntimeSettingsV105.cs`.
- Exposed the original gesture mode as an **unsupported, opt-in, restart-only** toggle; leave OFF. Standard Xbox/gamepad input remains the validated path. See `mod/SonsVRMod.cs` and `mod/Input/KeyboardHack.cs`.

## Accepted limitations

- 3D objects can occlude the world-space HUD. Attempts to force UI depth-independent with special shaders (V164–V166) introduced regressions and are intentionally **not part of this release**.
- Some native action prompt icons remain intermittent.
- The optional legacy SteamVR gesture input path has not been verified alongside this fork; **keep it disabled**.
