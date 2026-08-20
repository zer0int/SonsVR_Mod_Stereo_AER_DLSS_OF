using Il2CppInterop.Runtime;
using Il2CppSystem.Collections.Generic;
using MelonLoader;
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using Valve.VR;
using Il2CppSimpleMouseRotator = Il2Cpp.SimpleMouseRotator;
using Il2CppFirstPersonCharacter = Il2Cpp.FirstPersonCharacter;
using Il2CppPlayerAnimationControl = Il2Cpp.PlayerAnimatorControl;
using static SonsVRMod.SonsVRMod;
using MelonCoroutines = MelonLoader.MelonCoroutines;
using Il2CppMono;
using System.Reflection;
using Il2CppSons.Input;
using Il2CppSons.Inventory;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using UnityEngine.UIElements;
using Image = UnityEngine.UI.Image;
using Mathf = UnityEngine.Mathf;

namespace SonsVRMod
{
    /// <summary>
    /// Manages the VR player representation by:
    /// - Creating a VR origin rig (VROrigin + Body + Head),
    /// - Linking it to the game's FPS camera and LocalPlayer,
    /// - Aligning player rotation to HMD direction,
    /// - Handling snap-turn and locomotion input,
    /// - Disabling mouse-based look controls.
    /// </summary>
    internal class VRPlayer : MonoBehaviour
    {
        public VRPlayer(IntPtr value) : base(value)
        {
        }

        /// <summary>
        /// Singleton instance for global access.
        /// </summary>
        public static VRPlayer Instance { get; set; }

        /// <summary>
        /// True only while the player is driving a character through one of their own cameras.
        /// Gameplay input must not be injected outside of that: menus and cutscenes are not places
        /// where a movement key or a jump gesture means anything.
        /// </summary>
        public static bool PlayerHasControl { get; private set; }

        /// <summary>
        /// The rig root and the neutral head pose, exposed so anything mapping a tracked device into
        /// the world goes through the same transform the head does, rather than repeating it.
        /// </summary>
        public static Transform RigOrigin { get; private set; }

        public static Vector3 HeadNeutral { get; private set; }

        /// <summary>
        /// Root transform of the VR coordinate system (world anchor for the player).
        /// This is moved to match the game's FPS camera position.
        /// </summary>
        public Transform Origin { get; private set; }

        /// <summary>
        /// Body transform (child of Origin) that holds the StereoRender rig.
        /// Represents the player's torso/base in VR.
        /// </summary>
        public Transform Body { get; private set; }

        /// <summary>
        /// Reference to the main VR camera rig (usually unused directly; managed by StereoRender).
        /// </summary>
        public Camera Camera { get; private set; }

        /// <summary>
        /// Reference to the original game's FPS camera (used as position source).
        /// </summary>
        public Camera FPSCam { get; private set; }

        /// <summary>
        /// The custom stereo rendering component that drives left/right eye rendering.
        /// </summary>
        public StereoRender StereoRender { get; private set; }

        /// <summary>
        /// Name of the last camera used for debugging or fallback logic.
        /// </summary>
        public string lastCameraUsed = "";

        // Prevents multiple concurrent setup coroutines
        private static bool setupLock = false;

        /// <summary>
        /// True when the game is in UI/menu mode (e.g., inventory, map) and VR look should be disabled.
        /// Currently unused but reserved for future use.
        /// </summary>
        public bool isUIMode = false;

        /// <summary>
        /// Reference to the in-game LocalPlayer GameObject (root of the player character).
        /// </summary>
        public GameObject LocalPlayer;

        /// <summary>
        /// Internal reference to the currently used FPS camera (from game logic).
        /// </summary>
        private Camera UsedCamera = null;

        /// <summary>
        /// Name of the scene currently in use (for debugging).
        /// </summary>
        private string UsedSceneName = "";

        /// <summary>
        /// Name of the last camera assigned (for logging/debugging).
        /// </summary>
        public string lastCameraName = "";

        /// <summary>
        /// Indicates whether the VR rig has been fully initialized.
        /// </summary>
        private bool isSetupComplete = false;

        /// <summary>
        /// Cached reference to the game's first-person character controller.
        /// Used to control movement, running, and animation states.
        /// </summary>
        private Il2CppFirstPersonCharacter firstPersonCharacter;

        // === CONFIGURABLE ALIGNMENT OFFSETS ===

        /// <summary>
        /// Offset applied to the VR origin, expressed in the yaw space of the rig itself
        /// (X = right, Y = up, Z = forward relative to the current snap-turn yaw).
        /// <para>
        /// It is deliberately NOT expressed in the space of the game camera. The game camera is
        /// driven by the HMD, so an offset rotated by the camera made the world translate every time
        /// the player turned their head, which is the "swimming world" symptom of the old rig.
        /// </para>
        /// </summary>
        public Vector3 positionOffset = new Vector3(0.0f, 0.0f, 0.0f);

        /// <summary>
        /// Constant yaw offset in degrees, added on top of the snap-turn yaw.
        /// Useful to correct a rig that is installed facing the wrong way.
        /// </summary>
        public float yawOffset = 0f;

        /// <summary>
        /// Maximum speed, in degrees per second, at which the character body yaw chases the world
        /// yaw of the headset. Zero or less means instant. Uses RotateTowards rather than a Slerp
        /// factor, so the behaviour does not change with the frame rate.
        /// </summary>
        public float bodyRotationSpeed = 720f;

        /// <summary>
        /// Eye height used to place the rig when no camera anchor is available.
        /// </summary>
        public float fallbackEyeHeight = 1.7f;

        /// <summary>
        /// Transform the rig is anchored to: the parent of the game camera.
        /// <para>
        /// We overwrite the transform of the game camera every frame, so the camera itself cannot be
        /// used as a position source without creating a feedback loop. The parent is driven purely by
        /// the game and never written by the mod, which makes it a stable anchor.
        /// </para>
        /// </summary>
        private Transform _cameraAnchor;

        /// <summary>Vertical offset of the game camera inside its parent, captured when it is bound.</summary>
        private float _anchorEyeHeight;

        /// <summary>
        /// Eye height per camera, keyed by instance id. A single remembered camera was not enough:
        /// opening and closing the inventory alternates between two cameras, so a one slot cache was
        /// invalidated on every switch and the height was re-measured every time.
        /// </summary>
        private readonly System.Collections.Generic.Dictionary<int, float> _anchorEyeHeights =
            new System.Collections.Generic.Dictionary<int, float>();

        /// <summary>Accumulated snap-turn yaw of the rig, in degrees.</summary>
        private float _rigYaw;

        /// <summary>
        /// Tracked head position captured while the player stands in their neutral pose.
        /// <para>
        /// OpenVR reports the head in the standing tracking universe, so its Y is the height above
        /// the physical floor, around 1.7 m. The camera of the game is already at the eye position of
        /// the character, so adding the tracked position straight on top of it puts the view a whole
        /// body height too high. Subtracting this neutral vector puts the neutral standing pose right
        /// on the eyes of the character while leaving every real movement of the head intact.
        /// </para>
        /// </summary>
        private Vector3 _headNeutral = Vector3.zero;

        private bool _headNeutralCaptured;

        /// <summary>Set when a new camera is bound, so the rig faces where that camera faced, once.</summary>
        private bool _alignYawOnBind;

        private bool _lastPlayerInControl;

        /// <summary>
        /// Cameras the player is meant to be driving. Anything else, cutscene cameras above all,
        /// belongs to the game: the rig follows it instead of steering it.
        /// </summary>
        private static readonly string[] PlayerCameraNames = { "MainCameraFP" };

        private bool IsPlayerControlledCamera()
        {
            return UsedCamera != null && Array.IndexOf(PlayerCameraNames, UsedCamera.name) >= 0;
        }

        // === INPUT SMOOTHING ===

        private Vector2 smoothedInput = Vector2.zero;
        private float inputSmoothSpeed = 8f;

        // === RUN STATE MANAGEMENT ===

        private bool isRunning = false;
        private float lastRunActivationTime = 0f;
        private float runActivationCooldown = 0.25f; // Prevents accidental run activation

        // === SNAP TURN CONFIGURATION ===

        private bool snapTurnReset = true; // Prevents continuous rotation while stick is held
        private float snapAngle = 30f; // Degrees per snap turn
        private float stickThreshold = 0.7f; // Minimum stick deflection to trigger turn

        /// <summary>
        /// Cached reference to the player's animation controller (unused but reserved).
        /// </summary>
        private Il2CppPlayerAnimationControl _cachedAnimController;

        /// <summary>
        /// Indicates whether Harmony patches (if any) are ready.
        /// Currently unused.
        /// </summary>
        private bool harmonyIsReady = false;



        public bool InventoryCamIsActive = false;
        private Renderer[] _playerRenderers;
        private Il2CppSystem.Collections.Generic.Dictionary<Renderer, bool> _originalRendererStates;
        private bool _lastInventoriCameraIsActive = false;
        private GameObject _softwareCursor;
        private InventoryCursorController _cursorController;
        private GameObject _inventorySystem;
        private GameObject _FakeGround;
        private Canvas _screenSpaceCanvas;
        private bool _canvasConvertedToWorldSpace = false;
        private bool canUpdate = false;
        private Canvas _playerMiniMapCanvas;
        private bool _canvasMinimapConvertedToWorldSpace = false;
        private Transform _PlayerMiniMapTransf;
        private Transform _DebugCanvas;

        private LayoutItemCursorSnapping _layoutCursorSnapping;
        private VirtualMouseInput _virtualMouseInput;
        private InputCursorState _inputCursorState;

        
        private bool _cursorDisableTimerStarted = false;
        private float _cursorDisableStartTime = 0f;
        private const float CursorDisableDelay = 3.0f;

        
        private bool _popupDisableTimerStarted = false;
        private float _popupDisableStartTime = 0f;
        private const float PopupDisableDelay = 1.0f;

        public KeyboardHack kh;

        /// <summary>Turns the flat menus into a panel in the world and points at them.</summary>
        private readonly VRMenu _vrMenu = new VRMenu();

        /// <summary>Drives the arms of the character from the motion controllers.</summary>
        private readonly VRHands _vrHands = new VRHands();

        /// <summary>The mod's own settings, on a panel in the room.</summary>
        private readonly VRSettingsPanel _settingsPanel = new VRSettingsPanel();

        /// <summary>Shows the loading screen in the headset instead of freezing the view.</summary>
        private readonly VRLoadingScreen _loadingScreen = new VRLoadingScreen();

        /// <summary>Where the viewpoint was when a menu came up, and where it stays until it closes.</summary>
        private Vector3 _menuViewpoint;
        
        
        /// <summary>
        /// Unity callback called when the object is initialized.
        /// Prevents duplicate instances and starts asynchronous setup.
        /// </summary>
        private void Awake()
        {
            if (Instance != null)
            {
                MelonLogger.Error("[VRPlayer] ## Duplicate VRPlayer detected! Destroying instance. ##");
                GameObject.Destroy(gameObject);
                return;
            }

            MelonLogger.Msg("[VRPlayer] ## Creating VRPlayer instance ##");
            Instance = this;

            // Subscribe to scene load events (currently unused but kept for extensibility)
            SonsVRMod.onSceneLoaded += OnSceneLoaded;

            setupLock = false;
            isSetupComplete = false;

            // Start async setup after a short delay to ensure game systems are ready
            MelonCoroutines.Start(Setup());
        }

        /// <summary>
        /// Callback triggered when a new scene is loaded.
        /// Currently unused but reserved for future scene-specific logic.
        /// </summary>
        private void OnSceneLoaded(int buildIndex, string sceneName)
        {
            // Intentionally empty
        }

        /// <summary>
        /// Asynchronous setup coroutine that:
        /// - Destroys any existing StereoRender,
        /// - Creates the VR rig hierarchy (Origin → Body → StereoRender),
        /// - Marks the object to survive scene changes.
        /// </summary>
        public IEnumerator Setup()
        {
            if (setupLock) yield break;
            setupLock = true;

            // Clean up previous StereoRender if it exists
            if (StereoRender != null)
            {
                Destroy(StereoRender);
                MelonLogger.Msg("[VRPlayer] #### STEREORENDER DESTROYED ####");
            }

            // Wait for game to stabilize (e.g., camera setup)
            yield return new WaitForSeconds(2.0f);

            Body = transform;
            Origin = transform.parent;

            // Ensure the Origin exists as the world root of the VR rig
            if (Origin == null)
            {
                Origin = new GameObject("[VROrigin]").transform;
                transform.SetParent(Origin, false);
            }

            // The rig hierarchy is Origin -> Body -> VRHead -> eyes. Only Origin carries placement
            // (position from the anchor, yaw from snap turn); everything below it must stay neutral
            // so the tracked head pose is the only thing moving the eyes.
            Body.localPosition = Vector3.zero;
            Body.localRotation = Quaternion.identity;
            Body.localScale = Vector3.one;
            Origin.localScale = Vector3.one;

            // Ensure VR rig persists across scene loads
            DontDestroyOnLoad(Origin);

            // Attach stereo rendering system to the body
            StereoRender = Body.gameObject.AddComponent<StereoRender>();

            // A camera may have been bound while the rig was still being built; SetSceneAndCamera
            // early-outs on an unchanged camera, so the mirroring has to be applied here.
            if (UsedCamera != null)
                StereoRender.BindGameCamera(UsedCamera);

            isSetupComplete = true;
        }

        /// <summary>
        /// Links this VR player to a specific game camera and scene.
        /// Finds the LocalPlayer object from the camera hierarchy and disables mouse look.
        /// </summary>
        /// <param name="activeSceneAndCam">Scene and camera data from game logic.</param>
        public void SetSceneAndCamera(ActiveSceneWithCamera activeSceneAndCam)
        {
            if (activeSceneAndCam.camera == UsedCamera)
            {
                return;
            }

            canUpdate = false;
            UsedSceneName = activeSceneAndCam.scene.name;
            UsedCamera = activeSceneAndCam.camera;
            if (UsedCamera != null)
            {
                // targetTexture is owned by StereoRender.ApplyMirrorMode from here on: it points the
                // game camera at a tiny dummy target so its full-screen passes stop costing anything.

                if (UsedCamera.name.Equals("InventoryCamera"))
                {
                    InventoryCamIsActive = true;
                }
                else
                {
                    InventoryCamIsActive = false;
                }

                // Anchor the rig to the parent of the camera. That transform is owned by the game and
                // never written by the mod, unlike the camera itself, which we overwrite every frame.
                _cameraAnchor = UsedCamera.transform.parent;

                // Measure the eye height of a camera once, ever, and remember it per camera.
                //
                // Two things make a fresh reading untrustworthy on any later bind. The world
                // transform of the gameplay camera is overwritten every frame, so re-reading it
                // measures our own writes rather than where the game put it. And the reading has to
                // be taken in world space, because GetAnchorPosition adds it back along world up: a
                // local space reading taken under a parent that is tilted, which the head bone is,
                // comes out short, and the view sinks a little further on every measurement.
                int cameraId = UsedCamera.GetInstanceID();
                if (!_anchorEyeHeights.TryGetValue(cameraId, out _anchorEyeHeight))
                {
                    _anchorEyeHeight = _cameraAnchor != null
                        ? UsedCamera.transform.position.y - _cameraAnchor.position.y
                        : 0f;

                    _anchorEyeHeights[cameraId] = _anchorEyeHeight;
                    MelonLogger.Msg($"[VRPlayer] Eye height of '{UsedCamera.name}' measured: {_anchorEyeHeight:F3} m");
                }

                // Treat every camera change as a recentre, in position and in heading.
                _headNeutralCaptured = false;
                _alignYawOnBind = true;

                // The menus anchor themselves in the world, so their anchor belongs to the scene it
                // was measured in. Only a real scene change invalidates it.
                _vrMenu.OnSceneChanged(activeSceneAndCam.scene.name);

                if (StereoRender != null)
                {
                    if (InventoryCamIsActive)
                    {
                        // The inventory camera draws a handful of layers on a flat background: it is
                        // there to show the contents, not the world. Adopting its configuration would
                        // stop the eyes drawing the world at all. Keep the gameplay setup and simply
                        // let its layers through as well.
                        StereoRender.AddCullingLayers(UsedCamera.cullingMask);
                    }
                    else
                    {
                        // Mirror the render configuration of this camera onto the two eye cameras, so
                        // the VR view shows exactly the layers and post effects the flat view would.
                        StereoRender.BindGameCamera(UsedCamera);
                    }
                }

                LocalPlayer = FindLocalPlayerFromCamera(UsedCamera, InventoryCamIsActive);
                if (LocalPlayer != null)
                {
                    firstPersonCharacter = LocalPlayer.GetComponent<Il2CppFirstPersonCharacter>();

                    // Groundwork for driving the arms from the controllers: report what is there
                    // before anything is built on assumptions about it.
                    VRArmRecon.Run(LocalPlayer);
                    _vrHands.Reset();
                    if (kh != null)
                    {
                        kh.LocalPlayer = LocalPlayer;
                    }
                }

                canUpdate = true;
            }
        }

        /// <summary>
        /// Drives the whole VR rig for this frame, in a fixed order. Unity gives no ordering guarantee
        /// between MonoBehaviours, so every step that depends on another one lives here rather than in
        /// separate LateUpdate methods.
        /// <list type="number">
        /// <item>apply the tracked head pose;</item>
        /// <item>apply snap turn to the rig yaw;</item>
        /// <item>turn the character body towards the world yaw of the headset;</item>
        /// <item>place the rig on the anchor, now that the body has already turned;</item>
        /// <item>write the head pose onto the game camera (one-way, never read back).</item>
        /// </list>
        /// </summary>
        /// <param name="followAnchor">
        /// False while a secondary camera (inventory) is active: the rig then stays where it was, so
        /// the player keeps looking at the scene from the last gameplay position.
        /// </param>
        private void UpdateVRRig(bool followAnchor)
        {
            if (StereoRender == null || StereoRender.Head == null || Origin == null)
                return;

            // The desktop mirror optimisation strips the game camera down to nothing, so it may only
            // run once we are actually in the world. In the menus that camera is the only thing
            // drawing anything at all, and blanking it would leave the player with a black monitor.
            StereoRender.SetMirrorActive(LocalPlayer != null && IsPlayerControlledCamera() && !InventoryCamIsActive);

            // 1. Tracked head pose, from the snapshot taken once at the start of the frame.
            StereoRender.ApplyHmdPose();

            if (!_headNeutralCaptured)
                CaptureHeadNeutral();

            RigOrigin = Origin;
            HeadNeutral = _headNeutral;

            // Face whatever the newly bound camera was facing, once. From then on the rig keeps its
            // own heading: the game is free to swing its camera around without dragging the player.
            if (_alignYawOnBind)
            {
                // The heading of the view is the rig yaw plus the yaw of the headset, so the rig has
                // to be set to the difference. Assigning the camera heading straight into the rig
                // counted the headset twice, and the view came back from the inventory turned by
                // however far the player happened to have their head round.
                float headsetYaw = StereoRender.Head.localRotation.eulerAngles.y;
                _rigYaw = Mathf.Repeat(UsedCamera.transform.eulerAngles.y - headsetYaw, 360f);
                _alignYawOnBind = false;

                MelonLogger.Msg($"[VRPlayer] Rig aligned to '{UsedCamera.name}': " +
                                $"camera {UsedCamera.transform.eulerAngles.y:F0} deg, " +
                                $"headset {headsetYaw:F0} deg, rig {_rigYaw:F0} deg");
            }

            // The player is only in control behind their own cameras. A cutscene camera belongs to
            // the game even when the player object exists, so head movement must not reach the
            // character there, and the camera must not be overwritten.
            bool playerInControl = LocalPlayer != null && IsPlayerControlledCamera();
            PlayerHasControl = playerInControl;

            if (playerInControl != _lastPlayerInControl)
            {
                _lastPlayerInControl = playerInControl;
                MelonLogger.Msg($"[VRPlayer] Control: {(playerInControl ? "player" : "game")} " +
                                $"(camera '{UsedCamera.name}')");
            }

            // Outside of gameplay the game owns its camera: the title screen drives a scripted
            // fly-through, cutscenes drive their own framing. Overwriting that camera with the head
            // pose freezes it wherever the headset happens to point, which reads as a hung menu.
            // Follow it read-only instead, so the flat view keeps working and the headset still gets
            // a stereo picture of whatever the game is showing.
            if (InventoryCamIsActive)
            {
                // The rig holds the position gameplay left it at: that is what puts the contents of
                // the inventory on the ground in front of the player rather than under a camera
                // parked somewhere else.
                UpdateSnapTurn();
                Origin.rotation = Quaternion.Euler(0f, _rigYaw + yawOffset, 0f);

                // The inventory camera does have to be driven, though. The game picks out items by
                // projecting through that camera, so the pointer only lands on what the player is
                // looking at while the camera sits on the head. Parked where the game puts it, above
                // the item plane, the pointer projects somewhere unrelated and selects nothing.
                if (VRConfig.DriveInventoryCamera)
                {
                    UsedCamera.transform.SetPositionAndRotation(
                        StereoRender.Head.position, StereoRender.Head.rotation);
                }

                LogInventoryDrift();
                return;
            }

            if (!playerInControl)
            {
                // Follow the position of the game camera, never its rotation.
                //
                // The opening cutscene is a helicopter crash: that camera tumbles. Copying its
                // heading onto the rig spins the whole world around a head that is not moving, which
                // is both disorienting and, in a headset, actively unpleasant. Orientation comes from
                // the headset alone, plus whatever snap turns the player makes to reorient.
                // The stick drives the menu cursor here, so it must not also turn the player.
                if (!_vrMenu.IsSteering)
                    UpdateSnapTurn();

                Origin.rotation = Quaternion.Euler(0f, _rigYaw + yawOffset, 0f);

                if (VRMenu.Active)
                {
                    // Held still while a menu is up. The title screen flies its camera slowly across
                    // the island, and following it walks the viewpoint straight through a panel that
                    // is, by design, standing still in the room. If the panel is anchored, so is the
                    // place it is being read from.
                    Origin.position = _menuViewpoint;
                }
                else
                {
                    // Followed exactly, with no raise.
                    //
                    // The raise exists so the viewpoint clears the scenery while a menu is being read
                    // over it. Applying it to every game owned camera lifted the cutscenes too, and
                    // half a metre is enough to put the player through the roof of the helicopter
                    // they are supposed to be sitting in.
                    Origin.position = UsedCamera.transform.position;

                    // Where the viewpoint would sit if a menu came up from here.
                    _menuViewpoint = Origin.position + Vector3.up * VRConfig.MenuHeightOffset;
                }

                if (VRConfig.MenuInWorldSpace)
                    _vrMenu.Update(StereoRender.Head, StereoRender.LeftCam);

                return;
            }

            // Back in the world: no menu to point at, so the mouse goes to whoever else wants it.
            // This belongs here and not earlier in the frame, where it ran unconditionally and threw
            // the panel anchor away once per frame, leaving the menu pinned to the head after all.
            _vrMenu.Release();

            if (followAnchor)
            {
                // 2. Snap turn.
                UpdateSnapTurn();

                // 3. Rig heading, applied before the body is aligned. The other way round the body
                //    would be matched against the head yaw of the previous frame, so a snap turn only
                //    reached the character a frame late.
                Origin.rotation = Quaternion.Euler(0f, _rigYaw + yawOffset, 0f);

                // 4. Body yaw. Done before the placement so step 5 reads an up-to-date anchor.
                AlignBodyToHead();

                // 5. Rig placement. Rotation comes only from our own yaw, position only from the
                //    anchor, so nothing here depends on what we wrote to the camera last frame.
                // The offset is in the rig's own yaw space, so sideways stays sideways as the player
                // turns rather than swinging with the world.
                Vector3 offset = new Vector3(VRConfig.PlayerOffsetX, 0f, VRConfig.PlayerOffsetZ) + positionOffset;
                Origin.position = GetAnchorPosition() + Origin.rotation * offset;
            }

            // 5. The game camera follows the headset. Purely a write: no rig input is derived from it.
            if (UsedCamera != null)
                UsedCamera.transform.SetPositionAndRotation(StereoRender.Head.position, StereoRender.Head.rotation);

            // 6. The arms follow the controllers, mapped through the rig that has just been placed,
            //    so hands and head are positioned from the same origin in the same frame.
            _vrHands.Update(LocalPlayer, Origin, _headNeutral);
        }

        /// <summary>
        /// Records where the head sits when the player is standing normally, so that pose can be
        /// mapped onto the eye position of the character. Re-taken whenever the rig binds a new
        /// camera, which also makes every scene change act as a recentre.
        /// </summary>
        public void CaptureHeadNeutral()
        {
            if (!VRFrame.HmdPoseValid)
                return;

            _headNeutral = VRFrame.HmdPosition;

            // A manual height wins over the measurement: useful when the play space floor is not
            // where the runtime thinks it is, which would otherwise sink or raise the whole view.
            if (VRConfig.EyeHeightOverride > 0f)
                _headNeutral.y = VRConfig.EyeHeightOverride;

            _headNeutralCaptured = true;

            // The rig places its origin on the head of the character; StereoRender measures real head
            // movement away from this neutral pose and clamps how far it is allowed to get.
            StereoRender.SetHeadNeutral(_headNeutral);

            MelonLogger.Msg($"[VRPlayer] Head neutral captured: {_headNeutral} " +
                            $"(eye height {_headNeutral.y:F2} m, " +
                            $"tracking range {VRConfig.PositionalTrackingRange:F2} m)");
        }

        /// <summary>
        /// World position the rig is pinned to. Only the vertical component of the camera offset is
        /// used, so the result does not depend on the rotation of the anchor.
        /// </summary>
        private Vector3 GetAnchorPosition()
        {
            // The extra lift is not a comfort setting: the game hangs its camera nearer the neck than
            // the eyes, which never shows on a flat screen but reads as shoulder height once the body
            // and arms are visible in a headset.
            if (_cameraAnchor != null)
                return _cameraAnchor.position + Vector3.up * (_anchorEyeHeight + VRConfig.PlayerEyeHeightOffset);

            if (LocalPlayer != null)
                return LocalPlayer.transform.position + Vector3.up * fallbackEyeHeight;

            return Origin.position;
        }

        /// <summary>
        /// Accumulates snap-turn yaw from the horizontal axis of the right thumbstick.
        /// The yaw is stored on the rig, not applied directly to a transform, so it survives the
        /// per-frame rewrite of the rig placement.
        /// </summary>
        private void UpdateSnapTurn()
        {
            // The settings panel is using the sticks to point at itself.
            if (_settingsPanel.IsOpen)
                return;

            Vector2 rightStick = NativeVRInput.GetVector2("ThumbstickRight");

            if (VRConfig.SmoothTurn)
            {
                // Continuous turning: the stick is a rate, not a trigger. Below the deadzone nothing
                // happens, so a resting thumb cannot drift the view.
                if (Mathf.Abs(rightStick.x) > 0.2f)
                {
                    _rigYaw += rightStick.x * VRConfig.SmoothTurnSpeed * Time.deltaTime;
                    _rigYaw = Mathf.Repeat(_rigYaw, 360f);

                    // The character follows a deliberate turn here too, exactly as it does for a
                    // snap: the player asked to be turned either way.
                    _forceBodyAlignFrames = ForceBodyAlignFrames;
                }

                return;
            }

            if (Mathf.Abs(rightStick.x) > stickThreshold)
            {
                if (snapTurnReset)
                {
                    _rigYaw += (rightStick.x > 0f) ? VRConfig.SnapTurnAngle : -VRConfig.SnapTurnAngle;
                    _rigYaw = Mathf.Repeat(_rigYaw, 360f);
                    snapTurnReset = false;

                    // A snap turn is the player deliberately asking to be turned, so the character
                    // follows unconditionally. It must not be filtered by the shoulder reading, which
                    // is there to interpret real movement, not to second guess an explicit command.
                    _forceBodyAlignFrames = ForceBodyAlignFrames;
                }
            }
            else if (Mathf.Abs(rightStick.x) < 0.2f)
            {
                snapTurnReset = true;
            }
        }

        private bool _lastBodyFollow;
        private bool _shouldersLeadBody;

        /// <summary>Frames left in which the character must follow the head no matter what.</summary>
        private int _forceBodyAlignFrames;

        /// <summary>
        /// Long enough for the character to turn through a snap turn at <see cref="bodyRotationSpeed"/>
        /// with room to spare, short enough not to bleed into ordinary looking around.
        /// </summary>
        private const int ForceBodyAlignFrames = 20;

        /// <summary>
        /// Angle between where the head points and where the shoulders point, in degrees.
        /// <para>
        /// The two controllers stand in for the shoulders: the line between them turns when the
        /// player turns their torso, and stays put when they only turn their head. A small angle
        /// therefore means the whole body has rotated in the room and the character should rotate
        /// with it; a large one means the player is just glancing over their shoulder.
        /// </para>
        /// </summary>
        /// <returns>False when the hands give no usable reading and the caller must fall back.</returns>
        private bool TryGetHeadShoulderAngle(out float angle)
        {
            angle = 0f;

            if (!VRFrame.TryGetRawHmdYaw(out float headYaw))
                return false;

            Pose left = NativeVRInput.GetPose("PoseLeft");
            Pose right = NativeVRInput.GetPose("PoseRight");

            // GetPose reports an exactly zero position when a controller is not being tracked.
            if (left.position == Vector3.zero || right.position == Vector3.zero)
                return false;

            Vector3 across = right.position - left.position;
            across.y = 0f;

            // Hands held close together make the line between them, and so its direction, meaningless.
            float minSeparation = VRConfig.MinHandSeparation;
            if (across.sqrMagnitude < minSeparation * minSeparation)
                return false;

            // Perpendicular to the line between the hands, on the horizontal plane: the way the
            // shoulders face.
            Vector3 shoulderForward = Vector3.Cross(across.normalized, Vector3.up);
            float shoulderYaw = Quaternion.LookRotation(shoulderForward, Vector3.up).eulerAngles.y;

            angle = Mathf.Abs(Mathf.DeltaAngle(shoulderYaw, headYaw));
            return true;
        }

        /// <summary>
        /// Decides whether the character is allowed to turn with the head this frame.
        /// <para>
        /// The body follows the head only while the player is actually moving. Looking around while
        /// standing still leaves the character alone, which is both better in a headset and the thing
        /// that keeps scripted animations intact: a player lying on the ground playing a get-up
        /// animation is not pushing the stick, so glancing around no longer swivels their body.
        /// </para>
        /// <para>
        /// Two further guards catch scripted sequences that do overlap with input: a head bone tilted
        /// well away from upright means the character is in a pose it was put into rather than one it
        /// is standing in, and a disabled character controller means the game has taken over.
        /// </para>
        /// </summary>
        private bool ShouldBodyFollowHead()
        {
            bool follow = true;
            string reason = null;

            // A snap turn was just requested: let the character catch up to the new heading, whatever
            // else is going on. The window covers the time RotateTowards needs to close the angle.
            if (_forceBodyAlignFrames > 0)
            {
                _forceBodyAlignFrames--;
                reason = "snap turn";
            }
            else if (firstPersonCharacter != null && !firstPersonCharacter.enabled)
            {
                follow = false;
                reason = "character controller disabled";
            }
            else if (_cameraAnchor != null &&
                     Vector3.Angle(_cameraAnchor.up, Vector3.up) > VRConfig.MaxBodyAlignTilt)
            {
                follow = false;
                reason = "head bone is not upright";
            }
            else if (!VRConfig.BodyFollowsHeadWhileStill)
            {
                Vector2 move = NativeVRInput.GetVector2("ThumbstickLeft");

                if (move.magnitude >= 0.15f)
                {
                    reason = "moving";
                }
                else if (TryGetHeadShoulderAngle(out float shoulderAngle))
                {
                    // Hysteresis: one threshold to decide the body has turned, a wider one before
                    // deciding it has not. A single threshold would flicker whenever the player holds
                    // their head right at it.
                    float threshold = _shouldersLeadBody
                        ? VRConfig.ShoulderReleaseAngle
                        : VRConfig.ShoulderAlignAngle;

                    _shouldersLeadBody = shoulderAngle < threshold;

                    follow = _shouldersLeadBody;
                    reason = follow
                        ? $"body turned with head ({shoulderAngle:F0} deg apart)"
                        : $"head turned alone ({shoulderAngle:F0} deg apart)";
                }
                else
                {
                    // No usable reading from the hands: fall back to turning only while moving.
                    follow = false;
                    _shouldersLeadBody = false;
                    reason = "standing still, hands not readable";
                }
            }

            if (follow != _lastBodyFollow)
            {
                _lastBodyFollow = follow;
                MelonLogger.Msg($"[VRPlayer] Body follows head: {follow}" +
                                (reason != null ? $" ({reason})" : ""));
            }

            return follow;
        }

        /// <summary>
        /// Turns the character so that its forward matches the world yaw of the headset, which is what
        /// makes stick locomotion go where the player is looking.
        /// </summary>
        private void AlignBodyToHead()
        {
            if (LocalPlayer == null || StereoRender == null || StereoRender.Head == null)
                return;

            if (!ShouldBodyFollowHead())
                return;

            Transform head = StereoRender.Head;

            // Looking almost straight up or down makes the projected forward vector degenerate and the
            // resulting yaw random. In that case the up vector of the head carries the yaw instead.
            Vector3 flat = head.forward;
            if (Mathf.Abs(flat.y) > 0.95f)
                flat = -Mathf.Sign(flat.y) * head.up;

            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f)
                return;

            Quaternion target = Quaternion.LookRotation(flat.normalized, Vector3.up);

            LocalPlayer.transform.rotation = bodyRotationSpeed <= 0f
                ? target
                : Quaternion.RotateTowards(LocalPlayer.transform.rotation, target,
                    bodyRotationSpeed * Time.deltaTime);
        }

        /// <summary>
        /// Unity Update (called once per frame). Currently empty (logic in LateUpdate).
        /// </summary>
        private void Update()
        {
            // All VR logic runs in LateUpdate to ensure camera positions are final
        }

        /// <summary>
        /// Main VR logic loop, executed after all Update() calls.
        /// Handles:
        /// - Positioning the VR origin based on FPS camera,
        /// - Snap-turn input,
        /// - Body rotation alignment,
        /// - Run state management.
        /// </summary>
        private void LateUpdate()
        {
            VRProfiler.Begin();
            VRWatchdog.Stage(VRWatchdog.DrivingRig);
            HandleUpdate();
            DisableMouseRotation();
            VRWatchdog.Stage(VRWatchdog.Idle);
            VRProfiler.End(VRProfiler.Section.Rig);
        }

        /// <summary>
        /// Core update logic for VR player behavior.
        /// </summary>
        private int _idleFrames;

        /// <summary>Frames between attempts at the name based lookups that have not succeeded yet.</summary>
        private const int LookupRetryFrames = 30;

        private int _lookupCountdown;

        private void HandleUpdate()
        {
            if (canUpdate == false)
            {
                // While the rig is standing down every other log line goes quiet, which makes a slow
                // scene load and a hung process look identical. This keeps a pulse in the log.
                if (++_idleFrames % 100 == 0)
                {
                    MelonLogger.Msg($"[VRPlayer] Idle, waiting for a camera " +
                                    $"({_idleFrames} frames, {Time.unscaledDeltaTime * 1000f:F0} ms last frame)");
                }

                return;
            }

            _idleFrames = 0;

            // The rig is built asynchronously a couple of seconds after this component wakes up, while
            // a camera can be bound immediately. Nothing below means anything without the rig, and
            // reaching the canvas helpers before it exists throws once per frame until it appears.
            if (StereoRender == null || StereoRender.Head == null)
            {
                return;
            }

            // A scene load destroys the camera we are bound to. Driving a destroyed transform is
            // undefined, so stand down and let VRSystem rebind to whatever the next scene brings up.
            if (UsedCamera == null)
            {
                MelonLogger.Msg("[VRPlayer] Bound camera was destroyed, waiting for a new one");
                PlayerHasControl = false;

                // Kept rendering, in darkness, so the loading screen can be shown rather than the
                // view stopping on the last frame of a world that is being taken apart.
                if (VRConfig.LoadingScreenInVR)
                {
                    StereoRender.SetRenderingActive(true);
                    StereoRender.SetLoadingMode(true);
                    _loadingScreen.Begin(StereoRender.Head, StereoRender.LeftCam);
                }
                else
                {
                    StereoRender.SetRenderingActive(false);
                }

                canUpdate = false;
                _cameraAnchor = null;
                LocalPlayer = null;
                firstPersonCharacter = null;
                _idleFrames = 0;
                return;
            }

            StereoRender.SetRenderingActive(true);
            StereoRender.SetLoadingMode(false);
            _loadingScreen.End();
            WatchForFalling();

            // Head pose, rig placement and game camera sync, all in one deterministic pass.
            // Everything below only reads the rig, it never moves it.
            UpdateVRRig(!InventoryCamIsActive);

            // The mod's own settings panel, available wherever the player is: it exists to be
            // adjusted while the effect of the change is visible.
            _settingsPanel.Update(StereoRender.Head);
            UpdatePlayerVisibility();

            if (InventoryCamIsActive != _lastInventoriCameraIsActive)
            {
                _lastInventoriCameraIsActive = InventoryCamIsActive;

                Vector3 playerPos = LocalPlayer != null ? LocalPlayer.transform.position : Vector3.zero;

                if (InventoryCamIsActive)
                    _inventoryOpenedAt = Time.time;

                MelonLogger.Msg($"[VRPlayer] InventoryCamera {(InventoryCamIsActive ? "opened" : "closed")}, " +
                                $"player at {playerPos}");
                _playerPosAtInventoryChange = playerPos;
            }
            else if (InventoryCamIsActive && LocalPlayer != null)
            {
                // Nothing in this mod writes the player position, so any drift here comes from the
                // game reacting to something we do while the inventory is open. Report it once.
                float drift = Vector3.Distance(LocalPlayer.transform.position, _playerPosAtInventoryChange);
                if (drift > 1f && !_inventoryDriftReported)
                {
                    _inventoryDriftReported = true;
                    MelonLogger.Warning($"[VRPlayer] Player moved {drift:F1} m since the inventory opened, " +
                                        $"now at {LocalPlayer.transform.position}");
                }
            }

            if (!InventoryCamIsActive)
                _inventoryDriftReported = false;

            // GameObject.Find walks every object in every loaded scene comparing names. Retrying it
            // once a frame for something that may never appear, and there are several such lookups
            // here, is one of the few things this mod does that is genuinely expensive on the CPU.
            bool retryLookups = --_lookupCountdown <= 0;
            if (retryLookups)
                _lookupCountdown = LookupRetryFrames;

            if (_screenSpaceCanvas == null && retryLookups)
            {
                FindInventoryCanvas();
            }

            if (_screenSpaceCanvas != null && !_canvasConvertedToWorldSpace)
            {
                ConvertCanvasToWorldSpace();
            }

            if (_playerMiniMapCanvas == null && retryLookups)
            {
                FindMiniMapCanvas();
            }

            if (_playerMiniMapCanvas != null && !_canvasMinimapConvertedToWorldSpace)
            {
                ConvertMinimapCanvasToWorldSpace();
            }

            UpdateCanvasPositionForVR();


            
            if (InventoryCamIsActive == true)
            {
                
                if (!_popupDisableTimerStarted)
                {
                    // Primo frame: avvia il timer
                    _popupDisableTimerStarted = true;
                    _popupDisableStartTime = Time.time;
                }
                else if (VRConfig.SendInventoryPopupKey && !_popupKeyPressed &&
                         Time.time - _popupDisableStartTime >= PopupDisableDelay)
                {
                    // Once, not once per frame. The timer was never cleared, so this branch kept
                    // firing: around fifty interact presses a second, each starting its own release
                    // coroutine, for as long as the inventory stayed open.
                    _popupKeyPressed = true;
                    kh?.ExecutePress_E();
                    LogInventoryAction("popup dismiss key sent");
                }
                
                
                // The camera already follows the head, see UpdateVRRig.
                if (retryLookups)
                {
                    FindInventoryCursorController();
                    FindSoftwareCursor();
                }
                
                if (_cursorController != null && _cursorController != null)
                {
                    FixInventoryCursorController();
                    UpdateCursorPositionFromGaze();
                    SetFakeGroundVisible(false);
                    ApplyInventoryLift();
                }
            }

            if (InventoryCamIsActive == false)
            {
                _popupDisableTimerStarted = false;
                _popupDisableStartTime = 0.0f;
                _popupKeyPressed = false;
                SetFakeGroundVisible(true);

                // Put the layout back where the game had it before letting go of it. Leaving it
                // raised means the game finds its own inventory somewhere it did not put it, and the
                // player may be standing on whatever it carries.
                if (_inventoryBaseKnown && _inventorySystem != null)
                {
                    Transform root = _inventorySystem.transform;
                    root.position = root.parent != null
                        ? root.parent.TransformPoint(_inventoryBaseLocal)
                        : _inventoryBaseLocal;
                }

                // Let the game place the layout from scratch the next time it opens.
                _inventoryBaseKnown = false;
                
                if (_softwareCursor != null)
                {
                    _softwareCursor.active = false;
                }

                if (_cursorController != null)
                {
                    _cursorController.enabled = true;
                    _cursorDisableTimerStarted = false;
                    _cursorDisableStartTime = 0f;
                }
                
                // Rig placement, snap turn and body alignment are handled by UpdateVRRig.

                // --- LOCOMOTION & RUNNING ---
                if (firstPersonCharacter != null)
                {
                    UnityEngine.Vector2 rightStick = NativeVRInput.GetVector2("ThumbstickRight");
                    UnityEngine.Vector2 leftStick = NativeVRInput.GetVector2("ThumbstickLeft");

                    // Deadzone and clamping
                    if (leftStick.magnitude < 0.15f)
                        leftStick = Vector2.zero;
                    else
                        leftStick = Vector2.ClampMagnitude(leftStick, 1f);

                    // Activate running if right stick is pushed forward (y > 0.7) and not turning
                    if (!isRunning && rightStick.y > 0.7f && Mathf.Abs(rightStick.x) < 0.3f)
                    {
                        float currentTime = Time.time;
                        if (currentTime - lastRunActivationTime > runActivationCooldown)
                        {
                            isRunning = true;
                            lastRunActivationTime = currentTime;
                            MelonLogger.Msg("[VRPlayer] Running activated");
                        }
                    }

                    // Deactivate running if moving backward or stopping
                    if (isRunning && (leftStick.magnitude < 0.01f || leftStick.y < 0f))
                    {
                        isRunning = false;
                    }

                    // Forward run state to game character controller
                    firstPersonCharacter.SetRunning(isRunning);
                }
            }
        }

        /// <summary>
        /// Attempts to locate the LocalPlayer GameObject by walking up the camera's transform hierarchy.
        /// Expects a structure like: Camera → ... → LookObject → ... → LocalPlayer.
        /// </summary>
        /// <param name="camera">The FPS camera to start from.</param>
        /// <returns>The LocalPlayer GameObject, or null if not found.</returns>
        private GameObject FindLocalPlayerFromCamera(Camera camera, bool useInventoryCam)
        {
            if (camera == null) return null;
            MelonLogger.Msg($"[VRPlayer] Find Player For camera : {camera.name}");

            if (useInventoryCam == true)
            {
                Transform parent = camera.transform.parent;
                for (int ix = 0; ix < 20; ix++)
                {
                    if (parent.name == "LocalPlayer")
                    {
                        MelonLogger.Msg($"[VRPlayer] Found player root: {parent.name} For InventoryCam");
                        return parent.gameObject;
                    }

                    Transform tempParent = parent.parent;
                    parent = tempParent;
                }
            }
            else
            {
                Transform current = camera.transform;
                for (int i = 0; i < 3; i++)
                {
                    current = current.parent;
                    if (current == null) break;

                    if (current.name == "LookObject")
                    {
                        // Climb up until we find "LocalPlayer" or reach the root
                        while (current.parent != null && current.parent.name != "LocalPlayer" &&
                               current.parent.parent != null)
                        {
                            current = current.parent;
                        }

                        if (current.parent != null && current.parent.name == "LocalPlayer")
                        {
                            MelonLogger.Msg($"[VRPlayer] Found player root: {current.parent.name}");
                            return current.parent.gameObject;
                        }
                    }
                }
            }

            MelonLogger.Warning("[VRPlayer] Could not find LocalPlayer from camera.");
            return null;
        }

        /// <summary>
        /// Disables all instances of SimpleMouseRotator on the player and camera.
        /// Prevents mouse input from interfering with HMD-based look.
        /// </summary>
        private void DisableMouseRotation()
        {
            // Only while playing. The camera bound outside gameplay belongs to the menu or a
            // cutscene, and stripping its rotator breaks the presentation the game is running.
            if (LocalPlayer == null)
                return;

            {
                var rotator = LocalPlayer.GetComponent<Il2CppSimpleMouseRotator>();
                if (rotator != null)
                {
                    rotator.enabled = false;
                }
            }

            if (UsedCamera != null)
            {
                var camRotator = UsedCamera.GetComponent<Il2CppSimpleMouseRotator>();
                if (camRotator != null)
                {
                    camRotator.enabled = false;
                }
            }
        }

        /// <summary>
        /// Cleanup when the object is destroyed.
        /// Unsubscribes from events to prevent memory leaks.
        /// </summary>
        private void OnDestroy()
        {
            MelonLogger.Warning("[VRPlayer] *** VRPlayer DESTROYED ***");
            SonsVRMod.onSceneLoaded -= OnSceneLoaded;
        }

        /// <summary>
        /// Resets the VR origin to a default home position and rotation.
        /// Currently unused but kept for debugging or comfort features.
        /// </summary>
        private void SetOriginHome()
        {
            SetOriginPosRotScl(new Vector3(0f, 0f, 0f), new Vector3(0, 90, 0), new Vector3(1, 1, 1));
        }

        /// <summary>
        /// Sets the position, rotation (as Euler angles), and scale of the VR origin.
        /// </summary>
        public void SetOriginPosRotScl(Vector3 pos, Vector3 euler, Vector3 scale)
        {
            Origin.position = pos;
            Origin.localEulerAngles = euler;
            Origin.localScale = scale;
        }

        /// <summary>
        /// Sets a uniform scale on the VR origin (useful for comfort/height adjustment).
        /// </summary>
        public void SetOriginScale(float scale)
        {
            Origin.localScale = new Vector3(scale, scale, scale);
        }

        /// <summary>
        /// Returns the current forward direction of the HMD (including snap-turn rotation).
        /// </summary>
        public Vector3 GetWorldForward()
        {
            return StereoRender?.Head?.forward ?? Vector3.forward;
        }

        /// <summary>
        /// Flattens a direction vector onto the horizontal plane (Y = 0) and normalizes it.
        /// </summary>
        public Vector3 GetFlatForwardDirection(Vector3 forward)
        {
            forward.y = 0;
            return forward.normalized;
        }

        /// <summary>
        /// Returns the current eye height of the VR user (approximate player height).
        /// </summary>
        public float GetPlayerHeight()
        {
            if (StereoRender?.Head == null)
                return 1.8f; // Default human height

            return Mathf.Abs(StereoRender.Head.localPosition.y);
        }

        /// <summary>
        /// Called when the component is enabled.
        /// </summary>
        private void OnEnable()
        {
            MelonLogger.Msg("[VRPlayer] VRPlayer ENABLED");
        }

        /// <summary>
        /// Called when the component is disabled.
        /// </summary>
        private void OnDisable()
        {
            MelonLogger.Msg("[VRPlayer] VRPlayer DISABLED");
        }

        /// <summary>Sizes a converted interface panel to the configured height in metres.</summary>
        private static void ApplyHudScale(Canvas canvas)
        {
            if (canvas == null || canvas.renderMode != RenderMode.WorldSpace)
                return;

            float pixels = Screen.height;
            if (pixels <= 1f)
                return;

            float scale = VRConfig.HudSize / pixels;
            if (Mathf.Abs(canvas.transform.localScale.x - scale) > 1e-6f)
                canvas.transform.localScale = Vector3.one * scale;
        }

        /// <summary>
        /// How far a canvas has to be moved to keep one thing on it in place as its size changes.
        /// <para>
        /// A canvas is positioned by its origin, but the piece the player looks at hangs some
        /// distance from that origin, in canvas pixels. Scaling multiplies that distance, so the
        /// piece slides away from where it was put, by a different amount on each canvas. The map
        /// and the ring around it live on separate canvases, which is why they came apart.
        /// </para>
        /// <para>
        /// The offset is taken from the piece itself rather than from the extent of everything on
        /// the canvas. Under the map sits a four thousand pixel image scaled two hundred times,
        /// clipped away by a mask: invisible, but it dominated any measurement of the contents and
        /// put the horizontal offset out by hundreds of pixels while leaving the vertical roughly
        /// right, which is exactly how the fault looked.
        /// </para>
        /// <para>
        /// Only the change since the reference size is cancelled, so at that size the layout is
        /// exactly as it was tuned by hand, and away from it each piece grows about itself.
        /// </para>
        /// </summary>
        private static Vector3 HudScaleCompensation(Canvas canvas, Transform widget,
            Quaternion rotation, ref Vector3 offset, ref bool measured)
        {
            if (canvas == null || widget == null)
                return Vector3.zero;

            float pixels = Screen.height;
            if (pixels <= 1f)
                return Vector3.zero;

            if (!measured)
            {
                // Straight from the transform, so anchors, pivots and any nesting in between are
                // already accounted for: whatever the layout does, this is where the thing ended up.
                offset = canvas.transform.InverseTransformPoint(widget.position);
                offset.z = 0f;
                measured = true;

                MelonLogger.Msg($"[VRPlayer] '{widget.name}' hangs at {offset.x:F0}, {offset.y:F0} " +
                                $"pixels from the origin of '{canvas.name}'");
            }

            float scale = VRConfig.HudSize / pixels;
            float reference = VRConfig.HudReferenceSize / pixels;

            return rotation * (offset * (scale - reference));
        }

        private Vector3 _hudContentCentre;
        private bool _hudContentMeasured;
        private Vector3 _miniMapContentCentre;
        private bool _miniMapContentMeasured;

        /// <summary>The ring of vitals that sits around the map, drawn on the main display canvas.</summary>
        private Transform _hudVitalsRing;

        /// <summary>The map itself, drawn on its own canvas inside the tracker.</summary>
        private Transform _miniMapFace;

        private bool _playerHiddenForSettings;

        /// <summary>
        /// Takes the character out of sight while the settings panel is up.
        /// <para>
        /// The panel is read at arm's length, which is exactly where the arms are: the beam comes out
        /// through the back of a hand, and the hand itself sits between the player and the text. The
        /// character is not doing anything while the panel is open, so there is nothing to lose by
        /// removing it for the duration.
        /// </para>
        /// </summary>
        private void UpdatePlayerVisibility()
        {
            bool hide = VRConfig.HidePlayerInSettings && _settingsPanel.IsOpen;

            if (hide == _playerHiddenForSettings)
                return;

            _playerHiddenForSettings = hide;

            if (hide)
                HidePlayerRenderers();
            else
                RestorePlayerRenderers();

            MelonLogger.Msg($"[VRPlayer] Character {(hide ? "hidden for the settings panel" : "visible again")}");
        }

        private bool CacheOriginalRendererStates()
        {
            if (LocalPlayer == null)
            {
                return false;
            }

            _originalRendererStates = new Il2CppSystem.Collections.Generic.Dictionary<Renderer, bool>();

            var renderers = LocalPlayer.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r != null)
                    _originalRendererStates[r] = r.enabled;
            }

            return true;
        }

        private void HidePlayerRenderers()
        {
            bool Cached = true;
            if (_originalRendererStates == null)
            {
                Cached = false;
                Cached = CacheOriginalRendererStates();
            }

            if (Cached == true)
            {
                foreach (var kvp in _originalRendererStates)
                {
                    if (kvp.Key != null)
                        kvp.Key.enabled = false;
                }
            }
        }

        private void RestorePlayerRenderers()
        {
            if (_originalRendererStates == null)
            {
                return;
            }

            foreach (var kvp in _originalRendererStates)
            {
                if (kvp.Key != null)
                    kvp.Key.enabled = kvp.Value;
            }
        }




        private bool _popupKeyPressed;
        private bool _fakeGroundHidden;
        private Vector3 _playerPosAtInventoryChange;
        private bool _inventoryDriftReported;
        private int _inventoryLogCountdown;
        private float _inventoryOpenedAt;

        /// <summary>
        /// Tracks where the inventory camera and the player actually end up while the inventory is
        /// open, so a runaway shows up as numbers walking away rather than as a report of things
        /// floating in nowhere.
        /// </summary>
        private void LogInventoryDrift()
        {
            if (--_inventoryLogCountdown > 0)
                return;

            // Every quarter of a second or so. The character starts falling part way through a
            // session, so the sampling has to be fine enough to place that moment against the
            // actions this mod performs on a timer.
            _inventoryLogCountdown = 15;

            Vector3 playerPos = LocalPlayer != null ? LocalPlayer.transform.position : Vector3.zero;

            // Where the pieces of the pointing chain actually are.
            //
            // Selection works by the game projecting through the inventory camera, so that camera has
            // to sit on the head, the cursor object has to be out in front of it, and the software
            // cursor has to be somewhere the player can see. Four things that must line up, and a
            // report of "the raycast stopped working" does not say which one slipped.
            string pointer = "no cursor controller";
            if (_cursorController != null)
            {
                Vector3 head = StereoRender.Head.position;
                Vector3 controller = _cursorController.transform.position;

                pointer = $"camera off head {Vector3.Distance(UsedCamera.transform.position, head):F2} m, " +
                          $"cursor off head {Vector3.Distance(controller, head):F2} m, " +
                          $"controller {(_cursorController.enabled ? "on" : "off")}, " +
                          $"software cursor {(_softwareCursor != null ? (_softwareCursor.activeInHierarchy ? "visible" : "hidden") : "missing")}";
            }

            MelonLogger.Msg($"[VRPlayer] Inventory +{Time.time - _inventoryOpenedAt:F1}s: " +
                            $"player y {playerPos.y:F2}, rig y {Origin.position.y:F2}, {pointer}");
        }

        private float _lastWatchedY;
        private bool _lastWatchedYValid;
        private int _fallBurstFrames;
        private int _gameplayHeartbeat;

        /// <summary>
        /// Watches the height of the character during ordinary play and starts recording as soon as
        /// it begins to drop quickly.
        /// <para>
        /// The falls happen while the player is walking around, not while the inventory is open,
        /// which is the one situation that was being sampled. Waiting for a periodic log to catch the
        /// moment would need a log line every frame; this stays quiet until something goes wrong and
        /// then records a burst around it.
        /// </para>
        /// </summary>
        private void WatchForFalling()
        {
            if (LocalPlayer == null)
                return;

            float y = LocalPlayer.transform.position.y;

            if (_lastWatchedYValid && Time.deltaTime > 0f)
            {
                float descent = (_lastWatchedY - y) / Time.deltaTime;

                // Faster than a normal step down or a short drop, and not already recording.
                if (descent > 6f && _fallBurstFrames <= 0)
                {
                    _fallBurstFrames = 40;
                    MelonLogger.Warning($"[VRPlayer] Falling: {descent:F1} m/s at y {y:F2}, " +
                                        $"position {LocalPlayer.transform.position}");
                }
            }

            _lastWatchedY = y;
            _lastWatchedYValid = true;

            if (_fallBurstFrames > 0)
            {
                _fallBurstFrames--;
                if (_fallBurstFrames % 5 == 0)
                {
                    MelonLogger.Warning($"[VRPlayer] Falling: y {y:F2}, rig y {Origin.position.y:F2}, " +
                                        DescribePlayerPhysics());
                }
                return;
            }

            // A quiet pulse the rest of the time, so the state just before a fall is on record too.
            if (++_gameplayHeartbeat % 300 == 0)
            {
                MelonLogger.Msg($"[VRPlayer] Playing: y {y:F2}, rig y {Origin.position.y:F2}, " +
                                DescribePlayerPhysics());
            }
        }

        /// <summary>
        /// Snapshot of everything that decides whether the character stays up.
        /// <para>
        /// None of the actions this mod performs line up with the moment the character starts to
        /// fall, so watching our own behaviour is no longer useful. What matters is which of these
        /// flips at that moment: a controller switching off, a collider being disabled, or the ground
        /// underneath simply going away.
        /// </para>
        /// </summary>
        private string DescribePlayerPhysics()
        {
            if (LocalPlayer == null)
                return "player gone";

            var controller = LocalPlayer.GetComponent<CharacterController>();
            var body = LocalPlayer.GetComponent<Rigidbody>();

            if (_playerColliders == null)
                _playerColliders = LocalPlayer.GetComponentsInChildren<Collider>(true);

            int enabledColliders = 0;
            foreach (var collider in _playerColliders)
            {
                if (collider != null && collider.enabled && collider.gameObject.activeInHierarchy)
                    enabledColliders++;
            }

            string fakeGround = _FakeGround != null
                ? (_FakeGround.activeInHierarchy ? "on" : "off")
                : "none";

            return $"fpc {(firstPersonCharacter != null ? firstPersonCharacter.enabled.ToString() : "-")}, " +
                   $"cc {(controller != null ? controller.enabled + "/grounded " + controller.isGrounded : "-")}, " +
                   $"rb {(body != null ? (body.isKinematic ? "kinematic" : $"vel {body.velocity.y:F1}") : "-")}, " +
                   $"colliders {enabledColliders}/{_playerColliders.Length}, fakeGround {fakeGround}";
        }

        private Collider[] _playerColliders;

        /// <summary>
        /// Records an action this mod performs on the inventory, stamped with how long the inventory
        /// has been open. That stamp is what lines an action up against the moment things go wrong.
        /// </summary>
        private void LogInventoryAction(string action)
        {
            float playerY = LocalPlayer != null ? LocalPlayer.transform.position.y : 0f;
            MelonLogger.Msg($"[VRPlayer] Inventory +{Time.time - _inventoryOpenedAt:F1}s: {action} " +
                            $"(player y {playerY:F2}, {DescribePlayerPhysics()})");
        }

        /// <summary>
        /// Shows or hides the plane the inventory lays its contents out on.
        /// <para>
        /// Only the renderers are touched. The previous version deactivated the whole object, which
        /// took its colliders with it: anything resting on that plane, the player included, had
        /// nothing left to stand on and fell out of the world.
        /// </para>
        /// </summary>
        private void SetFakeGroundVisible(bool visible)
        {
            if (!VRConfig.HideInventoryFakeGround && !visible)
                return;

            if (_FakeGround == null || _fakeGroundHidden == !visible)
                return;

            _fakeGroundHidden = !visible;

            var renderers = _FakeGround.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer != null)
                    renderer.enabled = visible;
            }

            MelonLogger.Msg($"[VRPlayer] Inventory ground renderers {(visible ? "shown" : "hidden")} " +
                            $"({renderers.Length}), collision left intact");
        }

        private Vector3 _inventoryBaseLocal;
        private Vector3 _inventoryLastApplied;
        private bool _inventoryBaseKnown;

        /// <summary>Name of the collider the last probe landed on, for working out what it found.</summary>
        private string _lastProbeHitName = "?";

        /// <summary>
        /// Raises the whole inventory layout above where the game places it.
        /// <para>
        /// The game lays the contents out on the collision surface. On a flat beach that reads as
        /// resting on the ground, but deep snow and tall grass are drawn well above the collision
        /// they stand on, so the items end up buried under a layer the player cannot see through.
        /// </para>
        /// <para>
        /// The lift is recomputed every frame from a remembered base rather than added to the current
        /// position, so it cannot accumulate. If the game moves the layout itself, the difference
        /// against what we last wrote gives it away, and the base is taken again from the new spot,
        /// so the game keeps control of where the inventory goes and we only bias the height.
        /// </para>
        /// </summary>
        private void ApplyInventoryLift()
        {
            if (_inventorySystem == null || VRConfig.InventoryLift <= 0f)
                return;

            Transform root = _inventorySystem.transform;

            // Anything other than our own last write means the game repositioned the layout.
            if (!_inventoryBaseKnown || Vector3.Distance(root.position, _inventoryLastApplied) > 0.01f)
            {
                _inventoryBaseLocal = root.localPosition;
                _inventoryBaseKnown = true;

                Vector3 placed = root.position;
                string ground = TryProbeGround(placed, out float probed)
                    ? $"ground probed at y {probed:F2} ({probed - placed.y:+0.00;-0.00} m from placement) " +
                      $"on '{_lastProbeHitName}'"
                    : "no ground found, keeping the placement height";

                MelonLogger.Msg($"[VRPlayer] Inventory layout placed by the game at y {placed.y:F2}, " +
                                $"{ground}, clearance {VRConfig.InventoryLift:F2} m");
            }

            Vector3 basePosition = root.parent != null
                ? root.parent.TransformPoint(_inventoryBaseLocal)
                : _inventoryBaseLocal;

            // Start from where the game put it, then raise to the real ground if that is higher.
            // Never lower: the game knows where the inventory belongs, this only stops it sinking.
            float surfaceY = basePosition.y;
            if (VRConfig.InventoryAdaptiveLift && TryProbeGround(basePosition, out float probedY))
                surfaceY = Mathf.Max(surfaceY, probedY);

            // Lifted along world up, not along the local axis: the parent follows the character and
            // can be tilted, and the point of this is to clear a horizontal surface.
            _inventoryLastApplied = new Vector3(
                basePosition.x,
                surfaceY + VRConfig.InventoryLift,
                basePosition.z);

            root.position = _inventoryLastApplied;
        }

        /// <summary>
        /// Finds the highest solid surface under the inventory area.
        /// <para>
        /// Sampled over a small ring rather than at a single point, because the layout covers an area
        /// and a rise under one of its corners would otherwise swallow whatever sits there.
        /// </para>
        /// <para>
        /// A probe finds collision, not what is drawn. Deep snow is often displaced only visually and
        /// tall grass usually has no collider at all, so this cannot measure how thick that layer is:
        /// the clearance margin still has to cover it. What this does fix is the ground itself being
        /// somewhere other than where the layout was placed, on a slope, a step, or a fallen log.
        /// </para>
        /// </summary>
        private bool TryProbeGround(Vector3 around, out float groundY)
        {
            groundY = 0f;

            const float probeAbove = 2.0f;
            const float probeBelow = 5.0f;

            float radius = Mathf.Max(0f, VRConfig.InventoryProbeRadius);
            bool found = false;

            for (int sample = 0; sample < 5; sample++)
            {
                Vector3 offset = Vector3.zero;
                if (sample > 0 && radius > 0f)
                {
                    float angle = (sample - 1) * 90f * Mathf.Deg2Rad;
                    offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                }

                Vector3 origin = around + offset + Vector3.up * probeAbove;
                float remaining = probeAbove + probeBelow;

                // Single hit casts, stepping past anything of ours, rather than one call collecting
                // every hit. The array taking version writes its results into a copy the interop
                // makes of a managed array, so the results never came back and every probe reported
                // nothing at all.
                for (int step = 0; step < 4 && remaining > 0f; step++)
                {
                    if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, remaining,
                            ~0, QueryTriggerInteraction.Ignore))
                        break;

                    if (!IsOwnCollider(hit.collider))
                    {
                        if (!found || hit.point.y > groundY)
                        {
                            groundY = hit.point.y;
                            _lastProbeHitName = hit.collider.name;
                            found = true;
                        }
                        break;
                    }

                    // Ours: drop just below it and carry on down.
                    remaining -= (origin.y - hit.point.y) + 0.01f;
                    origin = new Vector3(origin.x, hit.point.y - 0.01f, origin.z);
                }
            }

            return found;
        }

        /// <summary>
        /// True for colliders belonging to the player or to the inventory itself. Hitting those would
        /// measure the fake ground the layout brings with it, or the character standing over it, and
        /// the layout would climb a little higher on every frame.
        /// </summary>
        private bool IsOwnCollider(Collider collider)
        {
            Transform t = collider.transform;

            if (LocalPlayer != null && t.IsChildOf(LocalPlayer.transform))
                return true;

            if (_inventorySystem != null && t.IsChildOf(_inventorySystem.transform))
                return true;

            return false;
        }

        /// <summary>
        /// Keeps the inventory arrow on screen, at a size that can be seen in a headset.
        /// <para>
        /// The game switches its cursor off when the pointer enters an item, which on a monitor is
        /// covered by the item highlighting instead. In a headset that leaves the player with no
        /// pointer at all and no way to get it back, so its visibility is re-asserted every frame
        /// rather than set once.
        /// </para>
        /// <para>
        /// It is also drawn last within its canvas, so the item art cannot cover it, and scaled up:
        /// a cursor sized for a monitor is a few pixels across on a panel a metre away.
        /// </para>
        /// </summary>
        private void ForceCursorVisible()
        {
            if (_softwareCursor == null)
                return;

            if (!_softwareCursor.activeSelf)
                _softwareCursor.SetActive(true);

            var graphics = _softwareCursor.GetComponentsInChildren<Graphic>(true);
            foreach (var graphic in graphics)
            {
                if (graphic == null)
                    continue;

                if (!graphic.enabled)
                    graphic.enabled = true;

                // A cursor faded out is invisible just as surely as one switched off.
                Color colour = graphic.color;
                if (colour.a < 0.95f)
                {
                    colour.a = 1f;
                    graphic.color = colour;
                }
            }

            _softwareCursor.transform.SetAsLastSibling();

            // Drawn above every other interface element, not just its own siblings.
            //
            // Sibling order only settles who wins inside one canvas. Hovering an item can bring up a
            // panel of its own, and that would cover the pointer regardless of where it sits in the
            // hierarchy. Its own canvas with overridden sorting puts it above all of them.
            var cursorCanvas = _softwareCursor.GetComponent<Canvas>();
            if (cursorCanvas == null)
                cursorCanvas = _softwareCursor.AddComponent<Canvas>();

            if (!cursorCanvas.overrideSorting)
                cursorCanvas.overrideSorting = true;

            if (cursorCanvas.sortingOrder < 30000)
                cursorCanvas.sortingOrder = 30000;

            // The two remaining ways an interface element gets hidden without being switched off:
            // a group faded to nothing, or a canvas disabled somewhere above it. Both are common
            // ways for a game to hide a cursor, and neither shows up as the object being inactive.
            var groups = _softwareCursor.GetComponentsInParent<CanvasGroup>(true);
            foreach (var group in groups)
            {
                if (group == null)
                    continue;

                if (group.alpha < 0.95f)
                    group.alpha = 1f;
            }

            var canvases = _softwareCursor.GetComponentsInParent<Canvas>(true);
            foreach (var canvas in canvases)
            {
                if (canvas != null && !canvas.enabled)
                    canvas.enabled = true;
            }

            ReportCursorState();

            float scale = Mathf.Max(0.1f, VRConfig.InventoryCursorScale);
            if (Mathf.Abs(_softwareCursor.transform.localScale.x - scale) > 0.01f)
                _softwareCursor.transform.localScale = Vector3.one * scale;
        }

        private string _lastCursorState;

        /// <summary>
        /// Describes everything that decides whether the arrow is on screen, and reports it whenever
        /// it changes.
        /// <para>
        /// The pointer vanishes over one particular item and not the others, which rules out both
        /// the game switching it off wholesale and anything covering it. Rather than guess at a third
        /// mechanism, this records the state so the change can be read directly.
        /// </para>
        /// </summary>
        private void ReportCursorState()
        {
            var image = _softwareCursor.GetComponentInChildren<Graphic>(true);

            float groupAlpha = 1f;
            var groups = _softwareCursor.GetComponentsInParent<CanvasGroup>(true);
            foreach (var group in groups)
            {
                if (group != null)
                    groupAlpha = Mathf.Min(groupAlpha, group.alpha);
            }

            string state = $"active {_softwareCursor.activeInHierarchy}, " +
                           $"graphic {(image != null ? image.enabled + "/" + image.color.a.ToString("F2") : "none")}, " +
                           $"group alpha {groupAlpha:F2}, " +
                           $"scale {_softwareCursor.transform.localScale.x:F2}, " +
                           $"layer {_softwareCursor.layer}";

            if (state == _lastCursorState)
                return;

            _lastCursorState = state;
            MelonLogger.Msg($"[VRPlayer] Cursor state: {state}");
        }

        private void FindInventoryCursorController()
        {
            if (LocalPlayer == null || _cursorController != null) return;
            _cursorController = LocalPlayer.GetComponentInChildren<InventoryCursorController>(true);
            if (_cursorController != null)
            {
                MelonLogger.Msg("[VRPlayer] InventoryCursorController trovato sotto LocalPlayer!");
                _cursorController._cursorMouseSpeed = 0.0f;
                _cursorController._cursorGamepadSpeed = 0.0f;
                _cursorController.ResetCursorPosition();
                _inventorySystem = _cursorController.transform.parent.gameObject;
                if (_inventorySystem)
                {
                    _FakeGround = _inventorySystem.transform.Find("FakeGround").gameObject;
                }
            }
        }
        
        private void FindSoftwareCursor()
        {
            if(_softwareCursor != null) {return;}
            GameObject SoftwareCursorTransform = GameObject.Find("PlayerStandin/PlayerUI/ScreenSpaceCanvas/SoftwareCursor");
            if (SoftwareCursorTransform != null)
            {
                SoftwareCursorTransform.layer = LayerMask.NameToLayer("Default");
                _softwareCursor = SoftwareCursorTransform.gameObject;
                MelonLogger.Msg("[VRPlayer] SoftwareCursor trovato nella scena!");
            }
        }

        private void UpdateCursorPositionFromGaze()
        {
            if (_cursorController == null || StereoRender.Head == null) return;

            Vector3 desiredPosition = StereoRender.Head.position + StereoRender.Head.forward * 1.5f;
            Quaternion desiredRotation = Quaternion.LookRotation(StereoRender.Head.forward, Vector3.up);
            _cursorController.gameObject.transform.SetPositionAndRotation(desiredPosition, desiredRotation);
            
        }


        private void FindInventoryCanvas()
        {
            if (_screenSpaceCanvas != null) return;

            GameObject playerStandin = GameObject.Find("PlayerStandin");
            if (playerStandin == null)
            {
                return;
            }

            Transform canvasTransform = playerStandin.transform.Find("PlayerUI/ScreenSpaceCanvas");
            if (canvasTransform != null)
            {
                canvasTransform.localScale = Vector3.one;
                _screenSpaceCanvas = canvasTransform.GetComponent<Canvas>();

                _hudVitalsRing = canvasTransform.Find("ToggleableHUD/PlayerVitalUiRenderTextureGroup");
                if (_hudVitalsRing == null)
                    MelonLogger.Warning("[VRPlayer] No vitals ring on the display: its size will not track the map");
            }
        }


        private void FindMiniMapCanvas()
        {
            if (_playerMiniMapCanvas != null) return;

            GameObject playerStandin = GameObject.Find("PlayerStandin");
            if (playerStandin == null)
            {
                return;
            }

            Transform playerUi = playerStandin.transform.Find("PlayerUI");
            if (playerUi != null && VRConfig.LogInterfaceLayout)
                VRHudRecon.DumpOnce(playerUi);

            _PlayerMiniMapTransf = playerStandin.transform.Find("PlayerUI/PlayerMiniMap");

            Transform minimapcanvasTransform =
                playerStandin.transform.Find("PlayerUI/PlayerMiniMap/GPSTrackerSystem/Canvas");
            if (minimapcanvasTransform != null)
            {
                _playerMiniMapCanvas = minimapcanvasTransform.GetComponent<Canvas>();

                // The mask, not the map under it: the map is far larger than what is shown and is
                // scrolled about behind the mask, so the mask is the part that stays put.
                _miniMapFace = minimapcanvasTransform.Find("ScreenMask");
                if (_miniMapFace == null)
                    MelonLogger.Warning("[VRPlayer] No map face found: its size will not track the ring");
            }

            Transform gpsTransform = playerStandin.transform.Find("PlayerUI/PlayerMiniMap/GPSTrackerSystem");
            if (gpsTransform != null)
            {
                gpsTransform.localScale = Vector3.one;
            }
        }

        private void ConvertCanvasToWorldSpace()
        {
            if (_screenSpaceCanvas == null || _canvasConvertedToWorldSpace) return;

            _screenSpaceCanvas.renderMode = RenderMode.WorldSpace;
            _screenSpaceCanvas.worldCamera = UsedCamera;
            _screenSpaceCanvas.gameObject.layer = LayerMask.NameToLayer("Default");

            float targetWorldHeight = VRConfig.HudSize;
            float estimatedCanvasHeightInPixels = Screen.height;
            float scale = targetWorldHeight / estimatedCanvasHeightInPixels;
            _screenSpaceCanvas.transform.localScale = Vector3.one * scale;

            var scaler = _screenSpaceCanvas.GetComponent<CanvasScaler>();
            if (scaler != null)
            {
                scaler.enabled = false;
            }

            _canvasConvertedToWorldSpace = true;
            _hudContentMeasured = false;
        }

        private void ConvertMinimapCanvasToWorldSpace()
        {
            if (_playerMiniMapCanvas == null || _canvasMinimapConvertedToWorldSpace) return;

            _playerMiniMapCanvas.renderMode = RenderMode.WorldSpace;
            _playerMiniMapCanvas.worldCamera = UsedCamera;
            _playerMiniMapCanvas.gameObject.layer = LayerMask.NameToLayer("Default");

            float targetWorldHeight = VRConfig.HudSize;
            float canvasHeightInPixels = Screen.height;

            float scale = targetWorldHeight / canvasHeightInPixels;
            _playerMiniMapCanvas.transform.localScale = Vector3.one * scale;

            var miniscaler = _playerMiniMapCanvas.GetComponent<CanvasScaler>();
            if (miniscaler != null)
            {
                miniscaler.enabled = false;
            }

            _canvasMinimapConvertedToWorldSpace = true;
            _miniMapContentMeasured = false;
        }

        private void UpdateCanvasPositionForVR()
        {
            if (StereoRender.Head == null) return;

            Vector3 headRight = StereoRender.Head.right;
            Vector3 headUp = StereoRender.Head.up;
            Vector3 headForward = StereoRender.Head.forward;

            // A world space canvas is depth tested against the scene, so anything nearer than this
            // covers it. The backpack sits close to the player and was hiding the pointer while the
            // containers, further away, did not: the distance has to clear the nearest thing the
            // player leans over.
            // One point, and everything on the display hangs off it: shifting it moves the map,
            // the ring and the messages together, without disturbing how they sit against each other.
            Vector3 desiredPosition = StereoRender.Head.position
                                      + headForward * VRConfig.InventoryCanvasDistance
                                      + headRight * VRConfig.HudOffsetX
                                      + headUp * VRConfig.HudOffsetY;

            // Scale re-applied every frame, not only at conversion, so the size can be adjusted from
            // the settings panel and the effect seen while the slider is moving.
            ApplyHudScale(_screenSpaceCanvas);
            ApplyHudScale(_playerMiniMapCanvas);

            if (_screenSpaceCanvas != null)
            {
                Quaternion desiredRotation = Quaternion.LookRotation(StereoRender.Head.forward, headUp);

                // The canvas is in world space, so worldCamera is only the event camera for raycasts.
                // The eye cameras are what actually draw it, hence no camera settings are touched here
                // any more: forcing depth/clearFlags/cullingMask back on the game camera would undo
                // the mirror optimisation and put a third full render back into every frame.
                if (_screenSpaceCanvas.worldCamera != UsedCamera)
                    _screenSpaceCanvas.worldCamera = UsedCamera;

                Vector3 hudShift = HudScaleCompensation(_screenSpaceCanvas, _hudVitalsRing,
                    desiredRotation, ref _hudContentCentre, ref _hudContentMeasured);

                _screenSpaceCanvas.transform.SetPositionAndRotation(desiredPosition - hudShift,
                    desiredRotation);
            }

            if (_playerMiniMapCanvas != null && _PlayerMiniMapTransf != null)
            {
                // Adjustable from the settings panel: these two numbers are the whole of what
                // lines the map up with the ring around it, and the right value is whatever looks
                // right in the headset.
                Vector3 offset =
                    headRight * VRConfig.MiniMapOffsetX +
                    headUp * VRConfig.MiniMapOffsetY;

                Vector3 finalPosition = desiredPosition + offset;

                Quaternion minidesiredRotation = Quaternion.LookRotation(StereoRender.Head.forward, headUp);

                // Same as above: world space canvas, event camera only, no render settings touched.
                if (_playerMiniMapCanvas.worldCamera != UsedCamera)
                    _playerMiniMapCanvas.worldCamera = UsedCamera;

                Vector3 mapShift = HudScaleCompensation(_playerMiniMapCanvas, _miniMapFace,
                    minidesiredRotation, ref _miniMapContentCentre, ref _miniMapContentMeasured);

                _playerMiniMapCanvas.transform.SetPositionAndRotation(finalPosition - mapShift,
                    minidesiredRotation);
            }
        }

        

        void FixInventoryCursorController()
        {
            if (_cursorController != null)
            {
                if (_layoutCursorSnapping == null)
                {
                    _layoutCursorSnapping = _cursorController.GetComponent<LayoutItemCursorSnapping>();
                }
                if (_virtualMouseInput == null)
                {
                    _virtualMouseInput = _cursorController.GetComponent<VirtualMouseInput>();
                }
                if (_inputCursorState == null)
                {
                    _inputCursorState = _cursorController.transform.Find("InventoryCursorState")
                        .GetComponent<InputCursorState>();
                }
                
                
                if (_layoutCursorSnapping != null && _layoutCursorSnapping.enabled)
                {
                    _layoutCursorSnapping.enabled = false;
                }
                
                if (_virtualMouseInput != null && _virtualMouseInput.enabled)
                {
                    _virtualMouseInput.enabled = false;
                }
                
                // Flagged by the original author as breaking the inventory, and its timing lines up
                // with the character starting to fall through the world, so it can now be switched
                // off from the configuration without a rebuild.
                if (VRConfig.DisableInventoryCursorController && _cursorController.enabled)
                {
                    if (!_cursorDisableTimerStarted)
                    {
                        _cursorDisableTimerStarted = true;
                        _cursorDisableStartTime = Time.time;
                        LogInventoryAction("cursor controller disable timer started");
                    }
                    else if (Time.time - _cursorDisableStartTime >= CursorDisableDelay)
                    {
                        _cursorController.enabled = false;
                        LogInventoryAction($"cursor controller disabled after {CursorDisableDelay:F0} s");
                    }
                }
                // =================================
                
                if (_inputCursorState != null && _inputCursorState.gameObject.active == false)
                {
                    _inputCursorState.enabled = true;
                    _inputCursorState.gameObject.SetActive(true);
                    _inputCursorState.gameObject.active = true;
                }
                
                if (_softwareCursor != null && _softwareCursor.active == false)
                {
                    _softwareCursor.active = true;
                }
                ForceCursorVisible();

                if (_cursorController.enabled == false)
                {
                    // Centred on its own canvas, not placed out in the world.
                    //
                    // The arrow is an interface element and belongs inside the canvas that draws it.
                    // Giving it a world position took it away from that canvas, which renders at a
                    // fixed scale for its own distance, so the arrow came out tiny and, with a
                    // rotation copied from elsewhere, sometimes facing away.
                    //
                    // It does not need a computed position anyway: the canvas is already put in front
                    // of the head and turned to face it every frame, so wherever the player looks is
                    // the middle of that canvas by construction.
                    var cursorRect = _softwareCursor.GetComponent<RectTransform>();
                    if (cursorRect != null)
                        cursorRect.anchoredPosition3D = Vector3.zero;
                    else
                        _softwareCursor.transform.localPosition = Vector3.zero;

                    _softwareCursor.transform.localRotation = Quaternion.identity;
                }
                
                
                /*
    

                             
                */
            }
        }
    }
}