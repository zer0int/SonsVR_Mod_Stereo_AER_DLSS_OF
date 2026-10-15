# V133: findings from shipped IL2CPP wrappers

Based on `V133_HDRP_WRAPPER_CONTRACT_20261014_004349.zip` (seven assemblies). These are **generated managed IL2CPP wrappers**, not native `GameAssembly.dll` or source for Unity's HDRP implementation; they establish API/type surfaces, not method bodies or runtime call order.

## Core RP: native DynamicResolutionHandler

- `DynamicResolutionHandler.get_m_MinScreenFraction() : float`
- `DynamicResolutionHandler.set_m_MinScreenFraction(float) : void`
- `DynamicResolutionHandler.get_s_ScalerContainers() : Il2CppReferenceArray<ScalerContainer>`
- `DynamicResolutionHandler.get_s_ActiveScalerSlot() : DynamicResScalerSlot`
- `DynamicResolutionHandler.SetDynamicResScaler(PerformDynamicRes, DynamicResScalePolicyType)`
- `DynamicResolutionHandler.SetSystemDynamicResScaler(PerformDynamicRes, DynamicResScalePolicyType)`
- `ScalerContainer.get_method() : PerformDynamicRes`
- `ScalerContainer.get_type() : DynamicResScalePolicyType`

**Implication:** V132's `GetField("m_MinScreenFraction")` and direct static-field scanner were not appropriate for the generated wrapper. The actual members are *properties/accessors*; the scaler callback and policy live together in a native container array, not necessarily exposed as standalone managed static fields. Because Unity has both user and system callbacks, safely replacing one requires identifying the selected slot, preserving the exact previous delegate and policy, and accounting for the *per-camera DLSS scale delegates* below.

## HDRP 14: dedicated NVIDIA per-camera/per-view data

`HDRenderPipeline.get_m_DLSSPass() : DLSSPass`

`DLSSPass`:
- `get_m_CameraStates() : Il2CppSystem.Collections.Generic.Dictionary<int, CameraState>`
- `SetupDRSScaling(...)`
- `InternalNVIDIASetupDRSScaling(...)`
- `InternalNVIDIABeginFrame(HDCamera)`
- `InternalNVIDIARender(...)`

`CameraState`:
- `get_ScaleDelegate() : PerformDynamicRes`
- `get_ViewStates() : Il2CppSystem.Collections.Generic.List<ViewState>`
- `ScaleFn() : float` (DO NOT invoke just to measure; potentially alters state)

`ViewState`:
- `get_m_Data() : DlssViewData` (fields `perfQuality`, `inputRes`, `outputRes`)
- `get_m_UsingOptimalSettings() : bool`
- `get_m_UseAutomaticSettings() : bool`
- `get_m_OptimalSettingsRequest() : OptimalSettingsRequest` (fields `quality`, `viewport`, `optimalSettings`)
- `get_DLSSContext() : UnityEngine.NVIDIA.DLSSContext`

`NVIDIAModule`:
- `DLSSContext.get_initData() : ref DLSSCommandInitializationData`
- `DLSSCommandInitializationData.get_quality() : DLSSQuality`
- `DLSSCommandInitializationData` also contains input/output width and height fields.
- `GraphicsDevice.GetOptimalSettings(uint,uint,DLSSQuality,out OptimalDLSSSettingsData)`

**Implication:** We can compare each eye's actual HDRP view quality, recommended resolution, actual input dimensions, and (where reflection supports by-ref property getters) NVIDIA context initialization quality. This should clarify whether a stale quality/default recommendation is injected before creating the feature.

## Why V132 could freeze video without freezing game logic

During V132 the per-camera `Optimal` flag was changed after DLSS contexts were initialized; direct measurements showed 50% -> 100% -> 50%, two CreateFeature and two DestroyFeature wrapper calls, and stalled VR presentation. The proposed V133 probe makes **no** such mutations. It does **not** fix the resolution issue yet; it is designed to obtain the exact feature-state boundary before deciding on an initialization-time intervention.

## V133 passive runtime contract

Source entry: `NativeDlssFeatureStateV133.cs`; installed by `BUILD_V133.bat`, only invoked during routed 2E, sampled every two seconds. V132 intervention disabled; V128 native receiver router and V129 render-boundary trace retained.

Potential caveats: `Dictionary<int,CameraState>` key may differ from `Camera.GetInstanceID()`, and `DLSSContext.initData` is by-ref. The probe is defensive and reports missing state instead of guessing. Cannot compile/run this Windows game mod in this environment; static-only audit passes.
