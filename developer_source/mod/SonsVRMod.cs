using MelonLoader;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.Injection;
using System.Security;
using UnityEngine;
using UnityEngine.SceneManagement;
using Valve.VR;
using Il2Cpp;

// MelonLoader mod metadata
[assembly: MelonInfo(typeof(SonsVRMod.SonsVRMod), "SonsVR_Mod", "1.0.0", "Anthony", null)]
[assembly: MelonGame("Endnight", "SonsOfTheForest")]

namespace SonsVRMod
{
    /// <summary>
    /// Helper struct to associate a loaded Scene with its main Camera (if needed for VR rendering).
    /// Currently unused but kept for potential future use (e.g., multi-scene VR handling).
    /// </summary>
    public struct ActiveSceneWithCamera
    {
        public Scene scene;
        public Camera camera;
    }

    /// <summary>
    /// Main entry point of the Sons of the Forest VR mod.
    /// Handles VR initialization, OpenVR setup, IL2CPP injection, and per-frame input injection.
    /// </summary>
    public class SonsVRMod : MelonMod
    {
        /// <summary>
        /// Singleton instance of this mod for global access.
        /// </summary>
        public static SonsVRMod Instance { get; private set; }

        /// <summary>
        /// Indicates whether VR subsystem has been successfully initialized and is active.
        /// </summary>
        internal static bool vrEnabled;

        /// <summary>
        /// Event triggered whenever a new scene is loaded.
        /// </summary>
        public delegate void OnSceneLoadedEvent(int buildIndex, string sceneName);
        public static OnSceneLoadedEvent onSceneLoaded;

        /// <summary>
        /// Flag to conditionally disable in-game VR player rendering on-screen (e.g., for debugging or compatibility).
        /// </summary>
        public static bool blockVRplayerOnScreen = false;

        /// <summary>
        /// Instance responsible for translating VR gestures into simulated keyboard/mouse input.
        /// </summary>
        private KeyboardHack kh = new KeyboardHack();

        /// <summary>
        /// When true, the mod never synthesizes keyboard/mouse events and never polls OpenVR
        /// action state for buttons. Sons Of The Forest already has native Xbox controller
        /// support (it's a console-ported title), so a real physical gamepad's input reaches
        /// the game directly and untouched. This is what stops the on-screen prompt icons from
        /// flickering between keyboard glyphs and Xbox glyphs: previously KeyboardHack queued a
        /// synthetic Keyboard StateEvent every single frame (even when every value it wrote was
        /// 0/unpressed), which kept telling Unity's Input System "the keyboard was just used",
        /// fighting every frame against the real Gamepad device the Xbox controller registers as.
        /// Flip this to false to go back to full VR-motion-controller mode (SteamVR controllers
        /// translated into keyboard/mouse gestures).
        /// </summary>
        public static bool GamepadOnlyMode = true;

        /// <summary>
        /// When true, the mod never drives the inventory/cooking cursor from head gaze and
        /// never disables the game's native cursor components (LayoutItemCursorSnapping,
        /// VirtualMouseInput, InventoryCursorController). The real Xbox controller's right
        /// stick is left to move the cursor exactly like it does outside VR. Mixing a
        /// continuous gaze-driven cursor with the native gamepad-driven one was causing
        /// multiple inventory items to highlight while looking around, and the highlighted
        /// item to get stuck the moment the stick was touched. Flip to false to go back to
        /// forehead-gaze cursor control.
        /// </summary>
        public static bool GamepadCursorMode = true;

        /// <summary>
        /// HDRP anti-aliasing mode applied to both VR eye cameras via HDAdditionalCameraData
        /// (reflection-based, since this mod doesn't take a compile-time dependency on the HDRP
        /// interop assembly). Sons Of The Forest uses HDRP, which handles anti-aliasing through
        /// this per-camera component rather than the legacy RenderTexture.antiAliasing (MSAA)
        /// property StereoRender already sets - HDRP mostly ignores that for its own internal
        /// rendering. Without this, the picture (especially this game's dense foliage) can look
        /// noticeably grainy/aliased, more visible at lower render resolutions. Valid values:
        /// "TemporalAntialiasing", "SubpixelMorphologicalAntiAliasing", "FastApproximateAntialiasing",
        /// or "None" to leave HDRP's default. TAA is also the mode HDRP's own DLSS integration is
        /// built on top of (DLSS-specific camera properties only apply once Post Anti-Aliasing is
        /// set to TAA), so this is worth keeping on even if not using DLSS.
        /// </summary>
        public static string HdrpAntialiasingMode = "TemporalAntialiasing"; // reverted from the previous diagnostic - irrelevant while StereoRender.SkipHdrpCameraDataEntirely is true, since the whole function is skipped regardless of this value

        /// <summary>
        /// When true, explicitly enables DLSS + dynamic resolution on both VR eye cameras via
        /// HDAdditionalCameraData (also reflection-based). This is a genuinely separate switch
        /// from the game's own in-game "Dynamic Resolution: DLSS" graphics setting - that setting
        /// most likely only configures the game's own native camera, not these separately-created
        /// eye cameras, since allowDeepLearningSuperSampling is a per-camera HDRP property.
        /// Without this, DLSS may not actually be active for what you see in the headset even
        /// with it turned on in the graphics menu. Keep the in-game setting on too.
        /// </summary>
        public static bool EnableDlssOnEyeCameras = true; // V91: real HDRP DLSS is explicitly enabled on both persistent eye cameras.

        /// <summary>
        /// V91 REAL DLSS. Uses Sons Of The Forest's own DynamicResUpscaleManager / HDRP NVIDIA
        /// integration; the mod does not integrate NGX itself. Quality is the conservative first
        /// test because this project is specifically sensitive to distant high-frequency detail.
        /// These are deliberately top-level research knobs for the later in-HMD overlay.
        /// </summary>
        public static bool EnableV91RealDlss = true;
        public static string V91DlssQuality = "Quality";

        /// <summary>
        /// NVOFA motion-field resolution relative to the final eye target. V90d proved that full
        /// 3141x3317 optical flow is visually useful but catastrophically expensive in this D3D11
        /// integration (~15 FPS). 0.5 linear = 0.25 pixels; optical flow estimates motion, not final
        /// texture detail, so this is the correct first performance recovery experiment.
        /// </summary>
        public static float V91NvofaResolutionScale = 0.50f;

        #region DLL Loading Helpers

        /// <summary>
        /// Sets the directory where Windows searches for DLL dependencies.
        /// Used to help locate OpenVR dependencies.
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string path);

        /// <summary>
        /// Dynamically loads a native DLL at runtime.
        /// Used to explicitly load openvr_api.dll before initializing SteamVR.
        /// </summary>
        [SuppressUnmanagedCodeSecurity]
        [DllImport("Kernel32.dll", EntryPoint = "LoadLibrary", CallingConvention = CallingConvention.Winapi)]
        public static extern IntPtr LoadLibrary(string lpFileName);

        #endregion

        
        private float _lastMouseUpdateTime = 0f;
        private const float MouseUpdateInterval = 0.5f; // 100 ms → 10 Hz
        
        /// <summary>
        /// Called after the game has finished its initial startup sequence.
        /// This is where mod initialization begins.
        /// </summary>
        public override void OnApplicationStart()
        {
            MelonLogger.Msg("[SonsVR V116] Loaded: V115 WorldSpace HUD retained; minimap alignment now preserves authored ScreenMask/vitals-group pivot delta instead of rect centers. Legacy nested tarp ItemInfo WorldSpace conversion disabled under unified HUD. Tarp LayoutItems are session-cached (no 5Hz global scans). Fatal V114 camera/CustomPass audit retired. Added passive VR-eye HDCamera viewport truth logging for DLSS quality validation. Black loading + native-camera dummy-layer optimization remain intact.");
            MelonLogger.Warning("[SonsVR V128] EXPERIMENT: native Endnight DLSS receiver reroute in 2E; V127 telemetry retained; V116 AER/HUD frozen.");
            MelonLogger.Warning("********************************************************");
            MelonLogger.Warning($"**** LOAD AMP Sons VR Mod {UnityEngine.Application.version}         ****");
            MelonLogger.Warning("********************************************************");

            Instance = this;
            ManualPauseFpsTraceV144.Initialize(); // Manual-only startup FPS/pause transition experiment
            NativeDlssCameraScaleV136.Initialize(); // V136: patch only native HDRP CameraState.ScaleFn, no global DRS writes

            // V105: load persisted VR runtime settings BEFORE VR cameras/resources are created,
            // so startup uses the selected DLSS/OF/render-scale/clip configuration directly.
            RuntimeSettingsV105.LoadEarly();
            // V141: V140 automatic A/B retired; no setup countdown or quality/mode cycling.
            // V139C forensic calibration remains present in source but disabled by default.
            CameraCompositorAuditV114.Initialize();
            PerformanceForensicsV106.Initialize();

            // V78: passive HDRP/DLSS/RTHandle forensics. Read-only.
            HDRPDlssRuntimeTraceV78.Initialize();
            TransitionDlssAuditV117.Initialize(); // V128: keep the passive V117 exit watchdog.

            // Initialize VR subsystem (OpenVR + input + IL2CPP types)
            InitVR();
        }

        /// <summary>
        /// Initializes the VR subsystem by:
        /// 1. Loading openvr_api.dll manually,
        /// 2. Initializing SteamVR,
        /// 3. Setting up IL2CPP class injections for custom VR components.
        /// </summary>
        private void InitVR()
        {
            MelonLogger.Msg("Initializing VR subsystem...");

            // Step 1: Load OpenVR native library
            if (!LoadOpenVrApi())
            {
                MelonLogger.Error("Failed to load openvr_api.dll. VR disabled.");
                vrEnabled = false;
                return;
            }

            // Step 2: Initialize SteamVR runtime
            try
            {
                Valve.VR.SteamVR.InitializeStandalone(Valve.VR.EVRApplicationType.VRApplication_Scene);

                if (Valve.VR.OpenVR.System == null)
                {
                    MelonLogger.Error("OpenVR System is null. SteamVR not detected.");
                    vrEnabled = false;
                    return;
                }

                // Initialize our custom OpenVR input layer
                NativeVRInput.Initialize();

                // Optional: log info about SteamVR action arrays (for debugging)
                MelonLogger.Msg($"[SonsVR_Mod] Total actions: {SteamVR_Input.actions?.Length ?? -1}");
                MelonLogger.Msg($"[SonsVR_Mod] Boolean actions: {SteamVR_Input.actionsBoolean?.Length ?? -1}");
                MelonLogger.Msg($"[SonsVR_Mod] Vector2 actions: {SteamVR_Input.actionsVector2?.Length ?? -1}");
                MelonLogger.Msg($"[SonsVR_Mod] Vector1 actions: {SteamVR_Input.actionsVector1?.Length ?? -1}");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Error($"SteamVR initialization failed: {ex.Message}");
                vrEnabled = false;
                return;
            }

            // Step 3: Inject custom C# types into the game's IL2CPP domain
            SetupIL2CPPClassInjections();

            vrEnabled = true;
            MelonLogger.Msg("✅ VR subsystem initialized successfully.");
        }

        /// <summary>
        /// Attempts to load 'openvr_api.dll' from the mod's 'Libs' folder.
        /// This ensures the correct version of OpenVR is used, even if the game doesn't ship with it.
        /// </summary>
        /// <returns>True if the DLL was loaded successfully; otherwise, false.</returns>
        private bool LoadOpenVrApi()
        {
            string pluginPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                "Libs",
                "openvr_api.dll"
            );

            if (!System.IO.File.Exists(pluginPath))
            {
                MelonLogger.Error($"openvr_api.dll not found at: {pluginPath}");
                return false;
            }

            MelonLogger.Msg($"Loading openvr_api.dll from: {pluginPath}");
            IntPtr handle = LoadLibrary(pluginPath);

            if (handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                MelonLogger.Error($"Failed to load openvr_api.dll. Win32 error: {error}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Registers custom C# classes (VRSystem, VRPlayer, StereoRender) into the game's IL2CPP type system.
        /// This allows these classes to be instantiated and referenced by the game as if they were native.
        /// </summary>
        private void SetupIL2CPPClassInjections()
        {
            ClassInjector.RegisterTypeInIl2Cpp<VRSystem>();
            ClassInjector.RegisterTypeInIl2Cpp<VRPlayer>();
            ClassInjector.RegisterTypeInIl2Cpp<StereoRender>();
            EyeDepthCaptureCustomPassV81.RegisterInjectedType();
            // V100: V99 requested the HDRP motion CustomPass for telemetry, but the
            // IL2CPP type registration was still gated only by the old V89 display-path flag.
            // Register it whenever either the renderer wants motion applied OR the forensic
            // campaign wants to capture it.  Capture remains telemetry-only unless the
            // corresponding reconstruction candidate explicitly consumes it.
            if (StereoRender.AerTemporalObjectMotionV89Enabled ||
                (StereoRender.AerV99ForensicsEnabled && StereoRender.AerV99CaptureHdrpMotionVectors) ||
                (AerCalibrationV139.Enabled && StereoRender.AerV99CaptureHdrpMotionVectors))
                EyeMotionCaptureCustomPassV89.RegisterInjectedType();
        }

        /// <summary>
        /// Called after OnApplicationStart, once all game systems are fully initialized.
        /// Used here to ensure the VRSystem singleton exists in the scene.
        /// </summary>
        public override void OnApplicationLateStart()
        {
            MelonLogger.Msg("#### OnApplicationLateStart ####");

            // Ensure VRSystem component exists in the scene
            if (!VRSystem.Instance)
            {
                new GameObject("VR_Globals").AddComponent<VRSystem>();
            }
        }

        /// <summary>
        /// Called whenever a new scene finishes loading.
        /// Forwards the event to any subscribed listeners.
        /// </summary>
        /// <param name="buildIndex">Build index of the loaded scene.</param>
        /// <param name="sceneName">Name of the loaded scene.</param>
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            NativeDlssReceiverRouterV128.OnSceneLoaded(); // V128: release old native receiver/host after scene transition.
            onSceneLoaded?.Invoke(buildIndex, sceneName);
        }

        /// <summary>
        /// Called after a scene has been initialized (but before it's fully active).
        /// Currently unused, but kept as a hook for future features.
        /// </summary>
        /// <param name="buildIndex">Build index of the initialized scene.</param>
        /// <param name="sceneName">Name of the initialized scene.</param>
        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            // MelonLogger.Msg($"#### OnSceneWasInitialized: {buildIndex} | {sceneName} ####");
        }

        /// <summary>
        /// Called once per frame. Main loop for VR input processing.
        /// </summary>
        public override void OnUpdate()
        {
            ManualPauseFpsTraceV144.Tick(); // V144: record native Start/B and before/after state; never inject input
            
            NativeDlssContractTraceV127.Tick(); // V127 passive-only native DLSS contract telemetry
            NativeDlssLiveResolutionTraceV129.Tick(); // V129 read-only live 2E render-boundary and menu-state snapshots
            // V133: all prior 2E optimal-attribute interventions are inactive; V128 routing remains authoritative.
            NativeDlssFeatureStateV133.Tick(); // V133: passive DLSS per-camera/per-view state only
            NativeDlssCameraScaleV136.Tick(); // V137: scoped native quality-dependent scaling
            HDRPDlssRuntimeTraceV78.Tick();
            TransitionDlssAuditV117.Tick();
            RuntimeSettingsV105.Tick();
            CameraCompositorAuditV114.Tick();
if (vrEnabled)
            {
                // Keep OpenVR action-set polling alive (needed so head/rig tracking and the
                // right-thumbstick snap-turn code in VRPlayer keep working). This does NOT
                // touch Unity's Keyboard/Mouse/Gamepad devices, so it is not part of the
                // icon-flicker problem and is safe to leave running in GamepadOnlyMode.
                NativeVRInput.Update();
            }

            // Gamepad-only mode: never synthesize keyboard/mouse events. This is what was
            // causing the action-prompt icons to flicker between keyboard letters and Xbox
            // glyphs: KeyboardHack.InjectMovement() queued a synthetic Keyboard StateEvent
            // every single frame (even when every value it wrote was 0/unpressed), which kept
            // telling Unity's Input System "the keyboard was just used" and fought every frame
            // against the real Gamepad device the Xbox controller registers as. Sons Of The
            // Forest already has native Xbox controller support (it's a console-ported title),
            // so with nothing from this mod competing against it, the real controller's input
            // reaches the game directly and the prompt icons stay put.
            // V168G: never attempt legacy injection if OpenVR failed to initialize.
            if (vrEnabled && !GamepadOnlyMode)
            {
                kh?.InjectMovement();
            }
        }

        /// <summary>
        /// Called multiple times per frame (at fixed physics intervals).
        /// Currently unused since this mod doesn’t interact with physics directly.
        /// </summary>
        public override void OnFixedUpdate()
        {
        }

        /// <summary>
        /// Called after all Update and FixedUpdate methods have run.
        /// Used here to manually execute the stereo rendering pass if available.
        /// </summary>
        public override void OnLateUpdate()
        {
            if (VRPlayer.Instance != null && 
                VRPlayer.Instance.StereoRender != null && 
                VRPlayer.Instance.StereoRender.stereoRenderPass != null)
            {
                // V38 AER submits from StereoRender's WaitForEndOfFrame loop, after Unity/HDRP
                // has actually rendered this frame's selected eye. V37 proved this Melon callback
                // happens before StereoRender.LateUpdate, so executing here would re-introduce the
                // measured 1f/2f eye ages and also double-submit every frame. Preserve this old
                // path exactly whenever AER or the V38 EOF experiment is disabled.
                if (!(StereoRender.AlternateEyeRenderingEnabled && StereoRender.AerEndOfFrameSubmissionEnabled))
                    VRPlayer.Instance.StereoRender.stereoRenderPass.Execute();

                VRPlayer.Instance.kh = kh;
            }
        }

        /// <summary>
        /// Called multiple times per frame for immediate-mode GUI rendering.
        /// Unused in this mod (no debug UI implemented).
        /// </summary>
        public override void OnGUI()
        {
        }

        /// <summary>
        /// Called when the application is about to quit.
        /// Could be used for cleanup (e.g., shutting down OpenVR), but not required here.
        /// </summary>
        public override void OnApplicationQuit()
        {
        
            NativeHudCompositorV114.Shutdown();
            CameraCompositorAuditV114.Shutdown();
            RuntimeSettingsV105.Shutdown();
            ManualPauseFpsTraceV144.Shutdown();
            PerformanceForensicsV106.Shutdown();
            HDRPDlssRuntimeTraceV78.Shutdown();
            // V133: no mutating DLSS experiment or restoration required
            NativeDlssCameraScaleV136.Shutdown(); // V136: unpatch and disable scoped callback
            NativeDlssLiveResolutionTraceV129.Shutdown(); // V129 buffered TSV flush on exit
            TransitionDlssAuditV117.Shutdown();
}

        /// <summary>
        /// Called when MelonLoader saves user preferences.
        /// Unused in this version.
        /// </summary>
        public override void OnPreferencesSaved()
        {
        }

        /// <summary>
        /// Called when MelonLoader loads user preferences.
        /// Unused in this version.
        /// </summary>
        public override void OnPreferencesLoaded()
        {
        }
    }
}
