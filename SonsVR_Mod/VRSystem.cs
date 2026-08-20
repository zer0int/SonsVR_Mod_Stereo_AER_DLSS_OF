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

            // Normally check every ~50 frames (~0.8s at 60 FPS) to reduce overhead, but scan much
            // more often while there is no valid camera: that is the window right after a scene load,
            // when the rig is blind and every frame of delay is a frozen image in the headset.
            bool haveCamera = UsedCamera != null;

            // Scan often while there is no camera, which is the window a scene load sits in, and also
            // while the inventory is up. The game hands control back to the character before it drops
            // the inventory camera, and until that is noticed the rig is still frozen and still
            // driving a camera the player has already left behind.
            bool inventoryOpen = VRPlayer.Instance != null && VRPlayer.Instance.InventoryCamIsActive;
            int interval = (haveCamera && !inventoryOpen) ? 50 : 10;

            if (UpdateCounter < interval)
                return;

            UpdateCounter = 0;

            if (!haveCamera)
            {
                UsedCamera = null;
                UsedSceneName = "";
            }

            LogCameraCensus();

            var sceneAndCam = FindActiveGameplayScene();

            // No scene name check any more.
            //
            // Deciding by name meant keeping a list of scenes, and anything not on it was dropped in
            // silence: opening the options screen loads a scene that was not on the list, so the rig
            // stood down and stayed down, with nothing in the log to say why. What a camera draws is
            // a far better test than where it lives, and the filters below already apply it: a camera
            // that renders nothing, or only interface, is rejected on its own merits.
            var activeCam = sceneAndCam.camera;
            if (activeCam == null)
            {
                MelonLogger.Msg($"[VRSystem] No active camera found in scene: {sceneAndCam.scene.name}");
                return;
            }

            // The rig only has to exist once; the camera it follows is swapped at runtime instead of
            // rebuilding the rig, which would drop the tracked pose and the render targets.
            if (VRPlayer.Instance == null)
            {
                RigCreated = false;
                CreateCameraRig(activeCam);
            }

            if (sceneAndCam.scene.name != UsedSceneName || activeCam != UsedCamera)
            {
                MelonLogger.Warning($"[VRSystem] Active camera is now '{activeCam.name}' in scene '{sceneAndCam.scene.name}'");
                UsedSceneName = sceneAndCam.scene.name;
                UsedCamera = activeCam;
            }

            // SetSceneAndCamera early-outs when the camera has not actually changed.
            if (VRPlayer.Instance != null)
                VRPlayer.Instance.SetSceneAndCamera(sceneAndCam);
        }

        /// <summary>
        /// Finds the camera the game is currently rendering from, and the scene it belongs to.
        /// <para>
        /// Uses <c>Camera.allCameras</c>, which already contains only enabled cameras, instead of
        /// walking every root object of every loaded scene as the old implementation did. That scan
        /// ran <c>GetComponentsInChildren</c> over the whole world and showed up as a periodic hitch.
        /// </para>
        /// <para>
        /// Cameras belonging to the VR rig itself are skipped: they are enabled and would otherwise be
        /// picked as the camera to follow, making the rig chase its own eyes.
        /// </para>
        /// </summary>
        /// <returns>The scene and camera to drive the rig from; camera is null when none qualifies.</returns>
        private ActiveSceneWithCamera FindActiveGameplayScene()
        {
            ActiveSceneWithCamera result = new ActiveSceneWithCamera();

            Camera preferred = null;
            Camera fallback = null;

            var stereo = StereoRender.Instance;
            var bound = stereo != null ? stereo.HeadCam : null;

            var cameras = Camera.allCameras;
            foreach (var cam in cameras)
            {
                if (cam == null || IsVRRigCamera(cam))
                    continue;

                // A camera with an empty culling mask draws nothing, so it is never the camera the
                // game is really showing; following it feeds the headset a black image. The one
                // exception is the camera we ourselves emptied for the desktop mirror.
                if (cam.cullingMask == 0 && cam != bound)
                {
                    LogCandidateOnce(cam, "skipped, empty culling mask");
                    continue;
                }

                // Loading screens run a camera that only draws interface layers. Binding to it wakes
                // the whole VR pipeline up in the middle of the heaviest part of a scene load, and
                // gives the headset nothing to show anyway, since that interface is screen space.
                if (RendersOnlyInterface(cam))
                {
                    LogCandidateOnce(cam, "skipped, draws interface layers only");
                    continue;
                }

                if (cam.name.Equals("MainCameraFP"))
                {
                    preferred = cam;
                    break; // The first-person camera always wins, no need to keep looking.
                }

                if (cam.name.Equals("InventoryCamera"))
                {
                    preferred = cam;
                    continue; // Keep scanning in case MainCameraFP also shows up.
                }

                if (fallback == null)
                    fallback = cam;
            }

            result.camera = preferred != null ? preferred : fallback;
            result.scene = SceneManager.GetActiveScene();

            if (result.camera != null)
            {
                var camScene = result.camera.gameObject.scene;
                if (camScene.IsValid())
                    result.scene = camScene;
            }

            return result;
        }

        // Cameras already reported, so the diagnostic below cannot turn into a per-frame log flood.
        private readonly System.Collections.Generic.HashSet<string> _reportedCameras =
            new System.Collections.Generic.HashSet<string>();

        /// <summary>
        /// Reports a camera that was considered and rejected, once per camera name. Without this the
        /// only symptom of a bad pick is a black headset, with nothing in the log to explain it.
        /// </summary>
        private void LogCandidateOnce(Camera cam, string reason)
        {
            string key = cam.name + "|" + reason;
            if (!_reportedCameras.Add(key))
                return;

            MelonLogger.Msg($"[VRSystem] Camera '{cam.name}' {reason} " +
                            $"(mask 0x{cam.cullingMask:X}, depth {cam.depth}, target {(cam.targetTexture != null ? "RT" : "screen")})");
        }

        private int _censusCountdown;
        private int _lastCameraCount = -1;

        /// <summary>
        /// Reports every camera the engine is currently rendering.
        /// <para>
        /// The frame is bound by the processor, not the graphics card, and on this pipeline that cost
        /// is paid per camera: culling, the shadow atlas and the render graph are all built once for
        /// each one. Two eye cameras were added deliberately, but effects that attach a camera of
        /// their own, planar water reflections being the usual one, quietly multiply that by however
        /// many cameras exist. Counting them is the only way to know what is really being paid for.
        /// </para>
        /// </summary>
        private void LogCameraCensus()
        {
            if (--_censusCountdown > 0)
                return;

            _censusCountdown = 20;

            var cameras = Camera.allCameras;
            if (cameras.Length == _lastCameraCount)
                return;

            _lastCameraCount = cameras.Length;

            var names = new System.Text.StringBuilder();
            foreach (var cam in cameras)
            {
                if (cam == null)
                    continue;

                if (names.Length > 0)
                    names.Append(", ");

                names.Append($"{cam.name}(d{cam.depth:F0}{(cam.targetTexture != null ? ",rt" : "")})");
            }

            MelonLogger.Msg($"[VRSystem] Rendering {cameras.Length} cameras: {names}");
        }

        // Layers that carry no world geometry. Built once: LayerMask lookups are string comparisons.
        private static int _nonWorldLayers = -1;

        /// <summary>
        /// True when a camera draws nothing but interface layers, which makes it a loading screen or
        /// an overlay rather than the view the player is meant to be inside of.
        /// </summary>
        private static bool RendersOnlyInterface(Camera cam)
        {
            if (_nonWorldLayers == -1)
            {
                _nonWorldLayers = 0;
                foreach (string layerName in new[] { "UI", "TransparentFX", "Ignore Raycast" })
                {
                    int layer = LayerMask.NameToLayer(layerName);
                    if (layer >= 0)
                        _nonWorldLayers |= 1 << layer;
                }
            }

            return (cam.cullingMask & ~_nonWorldLayers) == 0;
        }

        /// <summary>True for the eye cameras created by <see cref="StereoRender"/>.</summary>
        private static bool IsVRRigCamera(Camera cam)
        {
            var stereo = StereoRender.Instance;
            if (stereo == null)
                return false;

            return cam == stereo.LeftCam || cam == stereo.RightCam;
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