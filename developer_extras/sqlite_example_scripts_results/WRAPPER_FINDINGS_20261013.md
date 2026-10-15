# Sons Of The Forest DLSS/DRS wrapper contract — 2026-10-13

Basis: user's `SonsVR_DLSS_Wrapper_Contract_20261013_201201.zip`, parsed directly from CLR metadata tables (not just the `strings` output). Four generated IL2CPP wrapper DLLs; this is a method/type contract, **not** the game's IL2CPP native method bodies.

## Verified members

### `Il2CppSons.Dlss.DynamicResUpscaleManager` (static-only controller)
- `GetDynamicResolutionOption() -> string`
- `SetDynamicResolutionOption(string)`
- `ApplyDlssSetting(string)`
- `SetDynamicResUpscaleFilter(string)`
- `Register(DynamicResolutionSettingReceiver)` / `Unregister(...)`
- `CleanUpReceivers()`
- `_dynamicResolutionSettingReceivers : List<DynamicResolutionSettingReceiver>`
- `_activeDlssQualitySetting : string`

### `Il2CppAssemblies.Sons.Dlss.DynamicResolutionSettingReceiver : MonoBehaviour`
- `OnEnable()` / `OnDisable()` / `LateUpdate()` / `OnValidate()`
- `VerifyProperties()` / `RefreshActiveSetting()` / `RefreshDlssActiveSetting()`
- `ApplyDlssSetting(string)` / `SetDynamicResolutionOption(string)`
- `_targetCamera : Camera` / `_hdAdditionalCameraData : HDAdditionalCameraData`
- `_activeDlssSetting : string` / `_dynamicResolutionOption : string`
- `QualitySettingValue`, `BalancedSettingValue`, `PerformanceSettingValue`, `UltraPerformanceSettingValue : uint`

### `Il2CppSons.Quality.DynamicResolutionScaler`
- `OnEnable()` / `Update()` / `ScalerFunction()`
- static `SetActive(bool)`, `SetFpsTarget(int)`, `SetOverride(bool,float)`, `GetCurrentScale() -> float`
- `_active`, `_overrideScaler`, `_overrideScalerValue`, `_currentScalar`, `_currentFps`, `_fpsTrigger`, `_minScreenPercentage`, `_maxScreenPercentage` etc.

### `Il2CppSons.Gui.Options.GraphicsOptionsGui`
- `OnPostEnable()`, `OnPostUpdate()`, `ProcessDynamicResolutionSettings()`

## Supported conclusions

1. Endnight has **two separate controls**, a registered per-camera DLSS-quality receiver system and a framerate-oriented dynamic-resolution scaler. Actual runtime coupling is still unknown.
2. V126 creates its eye receivers on **inactive GameObjects** and calls `Register` manually. Inactive host components do not run ordinary `LateUpdate`; this bypasses an actual native receiver lifecycle. Cloning/registration without activation is not equivalent to the authored receiver's behavior.
3. The native receiver is equipped to refresh DLSS configuration over time; the scaler is equipped to update the global scale independently. Neither existence proves which actor caused the observed fallback.
4. The game graphics menu has a dedicated dynamic-resolution processing entry point. No current wrapper source reveals precisely how it calls the manager and scaler.

## Next runtime discrimination (V127)

On the **known-working V116 renderer**, observe, without mutations:
- manager option and quality
- per-eye HDRP `QCode`, `Optimal`, `CustomQ`, `DlssAllowed`, `CamDyn`/`HdDyn`, RenderTexture `useDynamicScale`
- `DynamicResolutionScaler.GetCurrentScale()`
- manager's actual receiver list/active-quality caches (every 4s only)

Change **Quality -> Ultra Performance -> Quality** in the actual graphics menu, in 2E; wait about 5s at each endpoint. The expected state transitions distinguish manager reset, receiver refresh, HDRP camera writer, and independent resolution scaling. If the reset occurs inside one frame, a 20Hz sampler can miss it; existing V116 HDRP viewport logs may help, and native hook-based tracing would be the next escalation.

The trace is not a DLSS reroute and should not be mistaken for a fix.
