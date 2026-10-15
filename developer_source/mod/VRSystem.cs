using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SonsVRMod
{
    /// <summary>
    /// Central manager responsible for detecting the active gameplay scene and camera,
    /// and spawning/refreshing the VR player rig when needed.
    /// Acts as the bridge between the game's dynamic scene/camera system and the VR subsystem.
    /// </summary>
    internal class VRSystem : MonoBehaviour
    {
        public VRSystem(IntPtr value) : base(value) { }

        /// <summary>
        /// Singleton instance for global access.
        /// </summary>
        public static VRSystem Instance { get; private set; }

        // Frame counter to limit how often we scan for scene/camera changes (performance optimization)
        private int UpdateCounter = 0;

        /// <summary>
        /// Reference to the currently active in-game camera (e.g., MainCameraFP).
        /// Used as the positional source for the VR rig.
        /// </summary>
        private Camera UsedCamera = null;

        /// <summary>
        /// Name of the scene currently being used for gameplay.
        /// Helps detect scene transitions (e.g., loading screen → main world).
        /// </summary>
        private string UsedSceneName = "";

        /// <summary>
        /// Flag indicating whether the player rig needs to be respawned after a scene change.
        /// Currently unused but reserved for future logic.
        /// </summary>
        private bool RespawnPlayer = false;

        /// <summary>
        /// Indicates whether the VR camera rig has already been created.
        /// Prevents duplicate rig instantiation.
        /// </summary>
        private bool RigCreated = false;

        /// <summary>
        /// Unity callback called when the object is initialized.
        /// Sets up singleton, ensures persistence across scenes, and subscribes to scene events.
        /// </summary>
        private void Awake()
        {
            MelonLogger.Msg("########### INITIALIZING VR SYSTEM #############");

            // Enforce singleton pattern
            if (Instance != null)
            {
                MelonLogger.Error("Trying to create duplicate VRSystem instance! Disabling this one.");
                enabled = false;
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject); // Survive scene transitions

            // Subscribe to scene load events (currently used for cleanup/extensibility)
            SonsVRMod.onSceneLoaded += OnSceneLoaded;
        }

        /// <summary>
        /// Main logic loop, executed after all Update() calls.
        /// Periodically scans for the active gameplay scene and camera,
        /// and triggers VR rig creation or update when they change.
        /// </summary>
        private void LateUpdate()
        {
            UpdateCounter++;

            // Only check every ~50 frames (~0.8s at 60 FPS) to reduce overhead
            if (UpdateCounter >= 50)
            {
                UpdateCounter = 0;

                // Locate the most relevant active scene and its main camera
                var sceneAndCam = FindActiveGameplayScene();

                // CONFIRMED via test: with zero scene filtering, the rig tries to initialize
                // on the literal first possible frame (the "Splash" bootstrap scene), before
                // Unity's graphics/OpenVR context is fully ready - Setup() runs and logs
                // success, but frames never actually reach the compositor correctly
                // afterward ("SteamVR waiting forever"). Targeted fix: skip ONLY the Splash
                // scene specifically, allow everything else (including early init at the title
                // screen, and all gameplay map names like "IceCaves") - restores safe early
                // timing without the "wait for the real gameplay camera" delay that caused the
                // GPU allocation crash under heavy load.
                if (sceneAndCam.scene.name == "Splash")
                {
                    return;
                }

                {
                    var activeCam = sceneAndCam.camera;
                    if (activeCam != null)
                    {
                        // Detect scene or camera change
                        if (sceneAndCam.scene.name != UsedSceneName || UsedCamera == null || activeCam != UsedCamera)
                        {
                            // CONFIRMED via log: a transient, not-yet-positioned placeholder
                            // camera (exact coordinates like (0,0,0) or (0,10,0) - Unity's
                            // default spawn position for a freshly-created object) can get
                            // caught mid-instantiation by this periodic scan and replace a
                            // perfectly good, working camera reference - this was the real
                            // cause of "view far from body" even after tracking was otherwise
                            // fixed. Only accept a new camera if it's one of the known-good
                            // names, OR we don't currently have a working camera at all -
                            // never downgrade a good reference for an unrecognized one.
                            bool isKnownGoodName = activeCam.name.Equals("MainCameraFP") || activeCam.name.Equals("InventoryCamera");
                            bool haveNoCurrentCamera = UsedCamera == null;

                            // CONFIRMED via log: transient placeholder cameras sit at suspicious
                            // exact positions like (0,0,0) or (0,10,0) - X and Z both exactly
                            // zero, which real scene content essentially never does by
                            // coincidence. This can slip through even the name/reference checks
                            // above when the previous camera reference briefly goes null during
                            // a scene transition (Unity's "destroyed object" comparison quirk).
                            Vector3 camPos = activeCam.transform.position;
                            bool looksLikePlaceholder = Mathf.Abs(camPos.x) < 0.01f && Mathf.Abs(camPos.z) < 0.01f;

                            if ((isKnownGoodName || haveNoCurrentCamera) && !looksLikePlaceholder)
                            {
                            MelonLogger.Warning($"[VRSystem] Scene or camera changed — respawning VR player rig");
                            UsedSceneName = sceneAndCam.scene.name;
                            UsedCamera = activeCam;

                            // Restored early creation timing (accept any active camera, not
                            // just MainCameraFP specifically) - strong evidence now points to
                            // the DELAYED timing itself (waiting until fully loaded into a busy
                            // gameplay scene before ever allocating the large stereo textures)
                            // as what's destabilizing GPU allocation, not which camera triggers
                            // it. The camera-TRACKING fix below (which is what actually solved
                            // the multiplayer "stuck below map" bug) stays intact regardless -
                            // if the wrong camera triggers creation early, SetSceneAndCamera
                            // still correctly switches over once the real MainCameraFP appears.
                            if (!RigCreated)
                            {
                                CreateCameraRig(UsedCamera);
                            }
                            else if (VRPlayer.Instance != null)
                            {
                                // The rig already exists (this isn't the first camera we've
                                // seen) - make sure VRPlayer actually finds out about the new
                                // camera too, otherwise it silently keeps using the old one
                                // forever. This was the real cause of "view stuck below map":
                                // the initial fallback camera (before your character spawns)
                                // got locked in, and the switch to the real MainCameraFP once
                                // you actually spawned was never propagated here.
                                VRPlayer.Instance.SetSceneAndCamera(sceneAndCam);
                            }
                            }
                        }
                        else
                        {
                            // Scene and camera are stable — just refresh the VR player's reference
                            if (VRPlayer.Instance != null && sceneAndCam.camera != null)
                            {
                                VRPlayer.Instance.SetSceneAndCamera(sceneAndCam);
                            }
                        }
                    }
                    else
                    {
                        MelonLogger.Msg($"[VRSystem] No active camera found in scene: {sceneAndCam.scene.name}");
                    }
                }
            }
        }

        /// <summary>
        /// Scans all loaded scenes to find the one containing an active gameplay camera.
        /// Prioritizes "MainCameraFP" (the game's standard first-person camera),
        /// but falls back to any enabled camera if not found.
        /// </summary>
        /// <returns>A struct containing the scene and its active camera (or null if none found).</returns>
        private ActiveSceneWithCamera FindActiveGameplayScene()
        {
            // Confirmed via diagnostic: MainCameraFP isn't reachable through per-scene root
            // traversal (it likely lives under a persist-across-scene-loads object) - the
            // broader FindObjectsOfType search already proven to work in the CameraScan
            // diagnostic finds it fine regardless of which scene/hierarchy it's actually under.
            Camera[] allCameras = UnityEngine.Object.FindObjectsOfType<Camera>();

            Camera fallback = null;
            foreach (var cam in allCameras)
            {
                if (cam == null || !cam.isActiveAndEnabled) continue;

                // CONFIRMED cause of the head-detachment bug: our OWN VR rig cameras
                // (VRLeftEye/VRRightEye, created by StereoRender.Setup()) get picked up by this
                // same broad search once they exist, get mistaken for "the player's camera
                // changed", and VRPlayer starts tracking its own eye camera in a feedback loop.
                // Exclude them explicitly - they should never be candidates here.
                if (cam.name.Equals("VRLeftEye") || cam.name.Equals("VRRightEye")) continue;

                if (cam.name.Equals("MainCameraFP"))
                {
                    var found = new ActiveSceneWithCamera();
                    found.scene = cam.gameObject.scene;
                    found.camera = cam;
                    return found;
                }
                if (fallback == null) fallback = cam;
            }

            if (fallback != null)
            {
                var found = new ActiveSceneWithCamera();
                found.scene = fallback.gameObject.scene;
                found.camera = fallback;
                return found;
            }

            // Nothing found anywhere - return whatever the last valid scene was, with no camera.
            var result = new ActiveSceneWithCamera();
            int sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded) result.scene = scene;
            }
            return result;
        }

        /// <summary>
        /// Cleans up any pre-existing VR camera rig under this object to avoid duplicates.
        /// Looks for children named "[VRCameraRig]" and destroys them.
        /// </summary>
        private void CleanupExistingRigs()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child.name == "[VRCameraRig]")
                {
                    MelonLogger.Msg("Cleaning up old VRCameraRig");
                    GameObject.Destroy(child.gameObject);
                }
            }
        }

        /// <summary>
        /// Creates a new VR player rig as a child of this object and links it to the provided camera.
        /// Only creates one rig (guarded by RigCreated flag).
        /// </summary>
        /// <param name="usedCamera">The in-game camera to use as positional reference.</param>
        public void CreateCameraRig(Camera usedCamera)
        {
            CleanupExistingRigs();

            if (VRPlayer.Instance == null)
            {
                MelonLogger.Warning($"[VRSystem] #### CREATING NEW VR CAMERA RIG ####");
                GameObject rig = new GameObject("[VRCameraRig]");
                rig.transform.SetParent(transform, false);

                // Removed: this used to also set the rig object's own position/rotation to
                // match usedCamera. That's a DIFFERENT transform than "Origin" (which is
                // actually this rig's PARENT, VRSystem's own transform, set later in
                // VRPlayer's setup coroutine) - setting both was compounding into a double
                // rotation (child rotated to match the camera, then parent ALSO rotated to
                // match the same camera, doubling the effect) which broke the title-screen
                // hands-facing fix. Origin is now the single source of truth for this.

                rig.AddComponent<VRPlayer>();
                RigCreated = true;
            }
        }

        /// <summary>
        /// Callback triggered when a new scene finishes loading.
        /// Currently unused but kept for future scene-specific logic or cleanup.
        /// </summary>
        private void OnSceneLoaded(int buildIndex, string sceneName)
        {
            // Intentionally empty
        }

        /// <summary>
        /// Temporarily toggles rendering of the VR eye cameras by changing their culling mask.
        /// Useful for debugging or disabling VR view in UI contexts.
        /// </summary>
        /// <param name="toggle">If true, hides all layers (mask = 0); if false, restores default visibility.</param>
        private void TogglePlayerCam(bool toggle)
        {
            if (VRPlayer.Instance == null || VRPlayer.Instance.StereoRender == null)
                return;

            int mask = toggle ? 0 : StereoRender.defaultCullingMask;
            VRPlayer.Instance.StereoRender.LeftCam.cullingMask = mask;
            VRPlayer.Instance.StereoRender.RightCam.cullingMask = mask;
        }

        /// <summary>
        /// Cleanup when the object is destroyed.
        /// Unsubscribes from events to prevent memory leaks.
        /// </summary>
        private void OnDestroy()
        {
            SonsVRMod.onSceneLoaded -= OnSceneLoaded;
        }

        /// <summary>
        /// Called when the component is enabled.
        /// </summary>
        private void OnEnable()
        {
            MelonLogger.Msg("[VRSystem] VRSystem ENABLED");
        }

        /// <summary>
        /// Called when the component is disabled.
        /// </summary>
        private void OnDisable()
        {
            MelonLogger.Msg("[VRSystem] VRSystem DISABLED");
        }
    }
}