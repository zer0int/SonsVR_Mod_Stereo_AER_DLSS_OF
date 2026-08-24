using MelonLoader;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.Injection;
using System.Security;
using UnityEngine;
using UnityEngine.SceneManagement;
using Valve.VR;
using Il2Cpp;

// MelonLoader mod metadata
[assembly: MelonInfo(typeof(SonsVRMod.SonsVRMod), SonsVRMod.ModInfo.Name, SonsVRMod.ModInfo.Version, SonsVRMod.ModInfo.Author, null)]
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
            MelonLogger.Warning("********************************************************");
            MelonLogger.Warning($"**** LOAD AMP Sons VR Mod {UnityEngine.Application.version}         ****");
            MelonLogger.Warning("********************************************************");

            Instance = this;

            // Load the runtime knobs first: they gate parts of the VR pipeline below.
            VRConfig.Load();

            // Started before anything else, so a freeze during setup is reported too.
            VRWatchdog.Start();

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
            VRProfiler.BeginFrame();
            VRWatchdog.Beat();

            // First thing in the frame, before the mod writes any keyboard state of its own, so a
            // real key press cannot be overwritten by a synthetic one on its way in.
            VRUiDump.CheckHotkeys();

            if (vrEnabled)
            {
                // Blocking WaitGetPoses for this frame. It has to happen before anything reads a
                // pose, and exactly once, so every VR consumer works off the same snapshot.
                //
                // Skipped while the rig is standing down. A scene load pushes frame times from
                // milliseconds to seconds, and blocking on the compositor once per frame on top of
                // that turns a slow load into one that looks like it will never end.
                var stereo = StereoRender.Instance;
                if (stereo == null || stereo.RenderingActive)
                {
                    VRProfiler.Begin();
                    VRFrame.WaitAndUpdate();
                    VRProfiler.End(VRProfiler.Section.Poses);
                }

                // Update OpenVR action states and hand velocities
                VRProfiler.Begin();
                NativeVRInput.Update();
                VRProfiler.End(VRProfiler.Section.Input);
            }

            // Inject simulated keyboard/mouse input based on VR gestures.
            //
            // Everywhere except a menu.
            //
            // The problem being avoided is gameplay input reaching a menu: the jump gesture sends a
            // space press that the menu acts on, triggered by nothing more than putting the
            // controllers down. Requiring full player control was a stricter test than that needed,
            // and it took the movement keys away everywhere the player is not behind their own
            // camera, cutscenes included.
            if (!VRMenu.Active && !VRSettingsPanel.AnyOpen)
            {
                VRProfiler.Begin();
                kh?.InjectMovement();
                VRProfiler.End(VRProfiler.Section.Keyboard);
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
        /// <para>
        /// The eye textures are normally submitted at end of frame, after Unity has rendered the eye
        /// cameras. Submitting from here would always hand the compositor the image of the previous
        /// frame, so this only acts as a safety net if the end-of-frame coroutine is not running.
        /// </para>
        /// </summary>
        public override void OnLateUpdate()
        {
            if (VRPlayer.Instance == null)
                return;

            VRPlayer.Instance.kh = kh;

            if (VRPlayer.Instance.StereoRender != null)
                VRPlayer.Instance.StereoRender.SubmitFallbackIfNeeded();

            VRProfiler.EndFrame();
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