using Il2CppInterop.Runtime;
using Il2CppSystem.Collections.Generic;
using MelonLoader;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering.HighDefinition;
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
using Il2CppSons.Gui;
using Il2CppSons.Save;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using UnityEngine.UIElements;
using Image = UnityEngine.UI.Image;
using Mathf = UnityEngine.Mathf;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.EventSystems;


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
        // V143: read-only gameplay camera gate; avoids prompting during unpausable intro cameras.
        internal bool IsNativeGameplayCameraReadyV143 => UsedCamera != null && UsedCamera.name == "MainCameraFP" && LocalPlayer != null;

        // V156: V110's 30 cm *title/loading* eye lift was accidentally applied to the
        // real helicopter cinematic because BlankScene remains active and AER gameplay
        // is not ready yet. The cutscene must use its authored camera position.
        internal bool IsNativeCinematicCameraV156 => UsedCamera != null &&
            (UsedCamera.name.IndexOf("cutscene", StringComparison.OrdinalIgnoreCase) >= 0 ||
             UsedCamera.name.IndexOf("custcene", StringComparison.OrdinalIgnoreCase) >= 0);

        internal bool SuppressTitleEyeLiftForCinematicV156 =>
            IsNativeCinematicCameraV156 || (_v146LoadingEpochActive && _v155IntroLatched);

        // V158: the native title camera is an explicit state, not merely the
        // absence of a gameplay-ready AER gate. The latter remains true after
        // returning from gameplay, which suppressed V110's proven 30 cm lift.
        internal bool IsNativeTitleCameraContextV158 =>
            UsedSceneName == "SonsTitleScene" && UsedCamera != null &&
            UsedCamera.name == "Main Camera";


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
        /// Position offset (in camera-local space) applied to align the VR origin
        /// with the player's body. Adjust Y for height, Z for forward/backward.
        /// </summary>
        public Vector3 positionOffset = new Vector3(0.0f, 0.05f, -0.10f);

        /// <summary>
        /// Yaw (Y-axis) rotation offset in degrees.
        /// Can be used to correct 180° flips or fine-tune HMD-to-body alignment.
        /// Currently unused in code but kept for future tuning.
        /// </summary>
        public float yawOffset = 0f;

        /// <summary>
        /// Speed at which the player's body rotates to match HMD direction (degrees/second).
        /// Higher values = faster alignment.
        /// </summary>
        public float bodyRotationSpeed = 10f;
        
        // === INPUT SMOOTHING ===

        private Vector2 smoothedInput = Vector2.zero;
        private float inputSmoothSpeed = 8f;

        // === RUN STATE MANAGEMENT ===

        private bool isRunning = false;
        private float lastRunActivationTime = 0f;
        private float runActivationCooldown = 0.25f; // Prevents accidental run activation

        // === SNAP TURN CONFIGURATION ===

        private float snapTurnOffset = 0f; // Cumulative yaw offset from snap turns
        private bool snapTurnReset = false; // Prevents continuous rotation while stick is held
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

        // Ground-tarp-sunken-into-terrain workaround: how much to nudge the player's logical Y
        // position up while the tarp/inventory screen is open, to give it clearance from the
        // real ground mesh (mimicking standing at elevation, which the user confirmed avoids
        // the issue). Based on logged comparison data: flat ground clearance was ~1.0m
        // (sunken), a small rock got to ~1.15-1.2m (still not enough), a golf cart reached
        // ~1.3m (worked) - starting at 0.4f as a middle-ground estimate; adjust and recompile
        // to tune, same as PistolPitchOffsetDegrees etc. Set to 0 to disable entirely.
        public static float TarpElevationWorkaroundOffset = 1.0f; // confirmed by user: clears vegetation even on slopes
        private float _tarpBasePlayerY = 0f;
        private float _tarpBaseOriginY = 0f;
        // zer0int v5: tarp X/Z recenter is temporary. Save the exact pre-tarp VR rig
        // anchor so later handheld menus (campfire/book/etc.) inherit the fork's original
        // geometry instead of the tarp-specific 0.55 m alignment.
        private Vector3 _tarpBaseOriginPosition = Vector3.zero;
        private bool _tarpBaseOriginPositionValid = false;
        // V147: bounded, read-only samples just after returning from the tarp. This
        // lets us distinguish a remaining game animation/physics hop from a VR
        // camera re-anchor without logging every frame during normal gameplay.
        private int _tarpExitTraceFramesV147 = 0;
        private int _tarpExitTraceIndexV147 = 0;
        // Capture the exact MainCameraFP instance immediately BEFORE entering the
        // tarp. _lastKnownMainCameraFP is not always populated when VR started
        // directly on the gameplay camera and never changed source cameras.
        private Camera _tarpPreviousGameplayCameraV147;
        private Renderer[] _playerRenderers;
        private Il2CppSystem.Collections.Generic.Dictionary<Renderer, bool> _originalRendererStates;

        // ZER0INT V27: keep the real player/Animator/physics rig fully alive during the ground-tarp
        // inventory, but suppress its renderers only. This is the clean version of "the character
        // is there while also not being there": crafting/equip animations can continue, colliders
        // and gameplay state remain untouched, but knees/torso/hands/clothes no longer occlude the
        // tarp or the bottom item rows. Renderer.forceRenderingOff is deliberately used instead of
        // Renderer.enabled so Sons can continue toggling clothing/body renderers normally.
        public static bool TarpHidePlayerVisuals = true;
        public static bool TarpRemoveExtraHeadLiftWhenPlayerHidden = true;
        private Il2CppSystem.Collections.Generic.Dictionary<Renderer, bool> _tarpRendererForceOffStatesV27;
        private bool _tarpPlayerVisualsHiddenV27 = false;
        private float _nextTarpRendererRescanV27 = 0f;

        // ZER0INT V28: extend the proven V27 renderer-only invisibility trick to the two
        // other held 3D inventory-style menus where the avatar/held prop is expendable:
        // the campfire GrabBag/cooking menu and backpack quick-select. The construction and
        // tutorial books are intentionally excluded by default because their visible book/page
        // geometry IS the menu content; hiding a skinned book would make them unusable.
        //
        // This uses the exact same Renderer.forceRenderingOff mechanism as V27. Native
        // Animators, colliders, interaction components and menu state continue running.
        public static bool HandHeldMenuHidePlayerVisuals = true;
        public static bool HandHeldMenuHideDuringGrabBag = false; // V142: restore cooking arm; renderer suppression was unrelated to DB thrash.
        public static bool HandHeldMenuHideDuringBackpackQuickSelect = true;
        public static bool HandHeldMenuHideDuringBooklet = false;
        public static bool HandHeldMenuHideDuringTutorialBook = false;
        private Il2CppSystem.Collections.Generic.Dictionary<Renderer, bool> _handHeldRendererForceOffStatesV28;
        private bool _handHeldPlayerVisualsHiddenV28 = false;
        private float _nextHandHeldRendererRescanV28 = 0f;
        private string _handHeldHideReasonV28 = "none";

        // ZER0INT V31: the V29 renderer census showed why V28 was not actually the same
        // intervention as V27. The tarp-support geometry lives in SkinnedMeshRenderers and
        // therefore disappeared with V27, while the cooking tray's large InventoryFoodBoard
        // is an ordinary Renderer and survived V28. Hide ONLY that structural board during
        // GrabBag/cooking while keeping all ingredient renderers, colliders, the CookingCog,
        // animations and interaction logic intact. This is intentionally a clean performance
        // test based on V28; none of the heavy V29/V30 diagnostic logging is included.
        public static bool GrabBagHideInventoryFoodBoard = false; // V32: V31 proved no perceived FPS gain; restore the board by default.
        private Renderer _grabBagFoodBoardRendererV31;
        private bool _grabBagFoodBoardOriginalForceOffV31 = false;
        private bool _grabBagFoodBoardHiddenV31 = false;
        private float _nextGrabBagFoodBoardSearchV31 = 0f;

        private bool _lastInventoriCameraIsActive = false;
        private bool manualBodyFreezeToggleActive = false;
        private Vector3 _inventoryInitialPos = Vector3.zero;
        private GameObject _softwareCursor;
        private InventoryCursorController _cursorController;
        private GameObject _inventorySystem;
        private GameObject _FakeGround;
        internal Canvas _screenSpaceCanvas;
        private Transform _worldScreenSpaceElementsTransform;

        // zer0int v8: enlarge only the two tiny HUD subtrees after the main canvas has already
        // been converted to world space.  Do NOT scale ScreenSpaceCanvas itself: v7 showed that
        // changing the root canvas scale during title/loading can wedge the game on the loading
        // screen.  These child-local scales leave the canvas geometry/loading path untouched.
        public static float SmallHudSubtreeScaleMultiplier = 1.00f;
        private Transform _smallHudPromptScaleTarget;
        private Vector3 _smallHudPromptBaseScale = Vector3.one;
        private Transform _smallHudMessageScaleTarget;
        private Vector3 _smallHudMessageBaseScale = Vector3.one;

        // ZER0INT V24: the campfire cooking recipe list is a separate HudGui subtree from the
        // generic WorldScreenSpaceElements/MessageList HUD.  The generated Il2CppSons wrapper
        // exposes the cooking-specific root as HudGui._cookingRecipeInfoView (RectTransform).
        // Scale that live root independently so recipe names/ingredients/stat icons are readable
        // in VR without touching the broad HUD canvas.
        public static float CookingRecipeOverlayScaleMultiplier = 1.00f;
        private Type _hudGuiTypeV24;
        private bool _hudGuiTypeSearchedV24 = false;
        private PropertyInfo _hudCookingRecipeInfoViewPropV24;
        private FieldInfo _hudCookingRecipeInfoViewFieldV24;
        private RectTransform _cookingRecipeInfoRtV24;
        private Vector3 _cookingRecipeInfoBaseScaleV24 = Vector3.one;
        private int _cookingRecipeInfoInstanceIdV24 = 0;
        private bool _cookingRecipeScaleLoggedV24 = false;
        private float _nextCookingRecipeResolveTimeV24 = 0f;

        // ZER0INT V25: three more HudGui-owned informational overlays use the same tiny
        // pancake-space presentation as the cooking recipe panel. Keep each multiplier
        // independent so these can later become live settings without touching the broad HUD.
        //
        // _availableRecipesView      = crafting-tarp recipe browser
        // _toolTipInfoView           = generic tooltip/info popup (strong candidate for world plants/mushrooms)
        // _quickSelectItemInfoView   = backpack quick-select item info
        public static float CraftingRecipeOverlayScaleMultiplier = 1.00f;
        public static float ToolTipInfoScaleMultiplier = 1.00f;
        public static float QuickSelectItemInfoScaleMultiplier = 1.00f;

        private PropertyInfo _hudAvailableRecipesViewPropV25;
        private FieldInfo _hudAvailableRecipesViewFieldV25;
        private PropertyInfo _hudToolTipInfoViewPropV25;
        private FieldInfo _hudToolTipInfoViewFieldV25;
        private PropertyInfo _hudQuickSelectItemInfoViewPropV25;
        private FieldInfo _hudQuickSelectItemInfoViewFieldV25;
        private bool _hudAdditionalMembersResolvedV25 = false;

        private RectTransform _availableRecipesRtV25;
        private Vector3 _availableRecipesBaseScaleV25 = Vector3.one;
        private int _availableRecipesInstanceIdV25 = 0;
        private bool _availableRecipesScaleLoggedV25 = false;

        private RectTransform _toolTipInfoRtV25;
        private Vector3 _toolTipInfoBaseScaleV25 = Vector3.one;
        private int _toolTipInfoInstanceIdV25 = 0;
        private bool _toolTipInfoScaleLoggedV25 = false;

        private RectTransform _quickSelectItemInfoRtV25;
        private Vector3 _quickSelectItemInfoBaseScaleV25 = Vector3.one;
        private int _quickSelectItemInfoInstanceIdV25 = 0;
        private bool _quickSelectItemInfoScaleLoggedV25 = false;

        private float _nextAdditionalHudResolveTimeV25 = 0f;

        // V169C: expose read-only cached HUD references to the on-demand F9 diagnostic.
        // These accessors never resolve a new HudGui, change UI state, or scan per frame.
        internal RectTransform DiagnosticCookingRecipeViewV169C => _cookingRecipeInfoRtV24;
        internal RectTransform DiagnosticTooltipViewV169C => _toolTipInfoRtV25;
        internal RectTransform DiagnosticAvailableRecipesViewV169C => _availableRecipesRtV25;
        internal RectTransform DiagnosticQuickSelectInfoViewV169C => _quickSelectItemInfoRtV25;


        private bool _canvasConvertedToWorldSpace = false;
        private bool canUpdate = false;
        internal Canvas _playerMiniMapCanvas;
        // ZER0INT V14: keep the minimap on the SAME physical HUD plane as the status/vitals ring.
        // V11 used 0.30m while the main ScreenSpaceCanvas (which owns PlayerVitalsUi) is at 0.50m;
        // enlarging the map made that pre-existing depth split painfully obvious in stereo.
        public static float MinimapForwardDistance = 0.5f;
        public static float MinimapScaleMultiplier = 1.00f;
        public static float VitalsUiScaleMultiplier = 1.00f;
        // V113: retired. HUD children are no longer independently re-positioned in 3D; the whole
        // native 2D sheet is placed once by NativeHudCompositorV114. Kept only so historical V112
        // helper code remains compilable as an inactive fallback.
        public static bool CenterMinimapVerticallyV112 = false;
        private bool _minimapVerticalCenterLoggedV112 = false;
        private bool _canvasMinimapConvertedToWorldSpace = false;
        private Transform _PlayerMiniMapTransf;
        private Transform _DebugCanvas;
        private Transform _playerVitalsUiTransf;
        private Transform _vitalsScaleTargetV14;
        private Vector3 _vitalsBaseScaleV14 = Vector3.one;
        private bool _vitalsScaleLoggedV14 = false;

        // ZER0INT V16: the visible small status ring is RenderTexture-composited.  Earlier
        // diagnostics already identified PlayerVitalUiRenderTextureGroup/VitalsRenderTexture,
        // while PlayerVitalsUiWorldSpace is merely the render SOURCE.  Scaling
        // ToggleableHUD/PlayerVitalsUi therefore never changed the displayed quad.  Target the
        // actual RawImage/RectTransform that presents the vitals RenderTexture instead.
        private RectTransform _vitalsRenderTextureRtV16;
        private Vector3 _vitalsRenderTextureBaseScaleV16 = Vector3.one;
        private int _vitalsRenderTextureInstanceIdV16 = 0;
        private bool _vitalsRenderTextureLoggedV16 = false;
        private bool _minimapVisualTargetLoggedV16 = false;
        // ZER0INT V60: NEVER center the ring on map-content children again. V16's fuzzy
        // name/texture heuristic selected GPSLocator(Clone)/IconOnly/Icon, which GPSTrackerSystem
        // intentionally moves with map/heading state. That accidentally made the ring orbit the
        // minimap. Anchor both displays to the stable ScreenBounds frame instead.
        private RectTransform _minimapStableFrameRtV60;
        private bool _minimapStableFrameLoggedV60 = false;

        private Transform _messageListTransf;
        private float _lastUiAlwaysOnTopTime = 0f;

        private LayoutItemCursorSnapping _layoutCursorSnapping;
        private VirtualMouseInput _virtualMouseInput;
        private InputCursorState _inputCursorState;

        /// <summary>
        /// The physical "grab bag" held in the left hand for campfire cooking (and similar
        /// held-item menus). This is not a Canvas or UIDocument - it's a plain 3D object under
        /// LeftHandItemLocators, which is why it was invisible to the canvas/cursor logic that
        /// only looked for the ground-tarp InventoryCamera. We search for it lazily and cache it.
        /// </summary>
        private Transform _grabBagHeld;
        private float _lastGrabBagSearchTime = 0f;
        private Transform _grabBagCursorState;

        // zer0int v11: the grab-bag/campfire menu is a real 3D held menu and now uses
        // head gaze successfully. Give it the same cheap far-clip optimization as the tarp,
        // and keep the VR Origin following MainCameraFP while the held-menu animation is
        // active. The generic !uiCursorActive positioning gate intentionally freezes Origin
        // for most menus; that is correct for flat/pause UI but wrong for this animated
        // first-person held object, where the native camera continues to move with the body.
        public static bool GrabBagFarClipEnabled = true;
        public static float GrabBagFarClipMeters = 50f;
        private bool _grabBagViewWasActiveV11 = false;

        // ===== Campfire cursor dead-reckoning (TUNE THESE based on testing) =====
        // Offset in the bag's own local space where the cursor starts when the bag opens,
        // based on the screenshot showing it starting roughly centered over an empty cell.
        // X = right/left across the bag, Y = up/down the bag. Both start at 0 (centered).
        private static readonly Vector2 CampfireCursorStartOffset = new Vector2(0f, 0f);
        // How fast the cursor moves per unit of stick deflection, in meters/second of local
        // offset. Guess to start - too slow means it lags behind the real cursor, too fast
        // means it overshoots. Tell me which one it looks like and we'll adjust this number.
        private const float CampfireCursorSpeed = 0.06f;
        // How far the cursor can travel from center before being clamped - a rough guess at
        // the bag's usable surface half-size in meters.
        private const float CampfireCursorMaxOffset = 0.12f;
        // If the dot moves the WRONG direction (e.g. left when you push right, or up/down
        // swapped), flip the corresponding sign here first before touching anything else.
        private const float CampfireCursorInvertX = 1f;  // set to -1f to flip left/right
        private const float CampfireCursorInvertY = 1f;  // set to -1f to flip up/down

        private Vector2 _campfireCursorOffset;
        private bool _grabBagCursorWasActive = false;
        private GameObject _gazeMarker;

        
        private bool _cursorDisableTimerStarted = false;
        private float _cursorDisableStartTime = 0f;
        private const float CursorDisableDelay = 3.0f;

        
        private bool _popupDisableTimerStarted = false;
        private float _popupDisableStartTime = 0f;
        private const float PopupDisableDelay = 1.0f;

        public KeyboardHack kh;

        /// <summary>
        /// When true, logs every Canvas that becomes active or inactive in the scene (with its
        /// full hierarchy path), throttled to twice a second. Use this to identify the
        /// campfire-cooking UI's canvas so it can be recognized as a menu the same way the
        /// ground-tarp inventory is. Safe to leave on - it only logs on change, not every frame.
        /// </summary>
        public static bool DebugLogCanvasChanges = false; // OFF again - investigation concluded, found LayoutItemCursorSnapping/QuickSelectLayoutItem for the backpack quick select.
        // Separate, cheap flag just for the Canvas/UIDocument scan (not the expensive
        // whole-scene keyword scan) - temporarily true to diagnose the pause-menu canvas.
        public static bool DebugScanCanvasesOnly = false; // back OFF - already found the LaptopIcons canvas, no longer need the broad scan
        private System.Collections.Generic.HashSet<string> _lastActiveCanvasNames = new System.Collections.Generic.HashSet<string>();
        private float _lastCanvasScanTime = 0f;

        /// <summary>
        /// True while ANY point-and-click menu is open - the ground-tarp inventory
        /// (InventoryCamIsActive) OR any other menu that unlocks the OS cursor (e.g. the
        /// campfire cooking screen, which never switches to a dedicated "InventoryCamera" so
        /// InventoryCamIsActive alone misses it). Sons Of The Forest reliably unlocks/shows the
        /// cursor for point-and-click menus and locks/hides it during normal first-person play,
        /// so this catches menus we don't otherwise recognize by camera name.
        /// </summary>
        /// <summary>
        /// True while ANY point-and-click menu is open - the ground-tarp inventory
        /// (InventoryCamIsActive), the campfire-cooking "grab bag" held in the left hand
        /// (GrabBagHeld), OR any other menu that unlocks the OS cursor. Sons Of The Forest
        /// reliably unlocks/shows the cursor for point-and-click menus and locks/hides it
        /// during normal first-person play, but for the grab bag specifically that unlock
        /// happens late (only once you start actively interacting), so we check the bag's
        /// own active state directly for instant detection instead of waiting on it.
        /// </summary>
        private bool IsUiCursorActive
        {
            get
            {
                if (InventoryCamIsActive) return true;
                if (Time.timeScale <= 0.0001f) return true; // pause/save menu - freeze body while open, same as other menus
                if (UnityEngine.Cursor.lockState != UnityEngine.CursorLockMode.Locked) return true;
                FindGrabBagHeld();
                if (_grabBagHeld != null && _grabBagHeld.gameObject.activeInHierarchy) return true;
                FindBookHeld();
                if (_bookHeld != null && _bookHeld.gameObject.activeInHierarchy) return true;
                FindBackpackQuickSelect();
                if (_backpackQuickSelect != null && _backpackQuickSelect.gameObject.activeInHierarchy) return true;
                FindTutorialBookHeld();
                if (_tutorialBookHeld != null && _tutorialBookHeld.gameObject.activeInHierarchy) return true;
                return false;
            }
        }

        private void FindGrabBagHeld()
        {
            if (_grabBagHeld != null || LocalPlayer == null) return;
            // Throttle: this only needs to succeed once, but retry periodically in case the
            // bag object doesn't exist yet (e.g. before it's ever been picked up this session).
            if (Time.time - _lastGrabBagSearchTime < 1.0f) return;
            _lastGrabBagSearchTime = Time.time;

            // Recursive DeepFindChild crashed while walking the full character bone hierarchy
            // (NullReferenceException from Il2Cpp's Transform enumerator on some child). Now
            // that the exact path is known from diagnostic logs, use a single safe, non-
            // recursive Transform.Find with a slash-separated path instead.
            const string grabBagPath =
                "PlayerAnimator/Root/Hips/Spine/Spine1/Spine2/LeftShoulder/LeftArm/LeftForeArm/" +
                "LeftForeArmTwistNew1/LeftForeArmTwistNew2/LeftForeArmTwistNew3/LeftHand/" +
                "LeftHandWeapon/leftHandHeld/LeftHandItemLocators/GrabBagHeld";

            Transform found = null;
            try
            {
                found = LocalPlayer.transform.Find(grabBagPath);
            }
            catch
            {
                found = null;
            }

            if (found != null)
            {
                _grabBagHeld = found;
                MelonLogger.Msg("[VRPlayer] GrabBagHeld found - campfire cooking bag will now be recognized as a menu.");
            }
        }

        private Transform _bookHeld;
        private float _lastBookSearchTime = 0f;

        /// <summary>
        /// Same technique and same parent path as FindGrabBagHeld - the construction book
        /// hangs off the identical LeftHandItemLocators hierarchy, just a different leaf name
        /// (BlueprintBookHeld instead of GrabBagHeld), confirmed via canvas scan.
        /// </summary>
        private void FindBookHeld()
        {
            if (_bookHeld != null || LocalPlayer == null) return;
            if (Time.time - _lastBookSearchTime < 1.0f) return;
            _lastBookSearchTime = Time.time;

            const string bookPath =
                "PlayerAnimator/Root/Hips/Spine/Spine1/Spine2/LeftShoulder/LeftArm/LeftForeArm/" +
                "LeftForeArmTwistNew1/LeftForeArmTwistNew2/LeftForeArmTwistNew3/LeftHand/" +
                "LeftHandWeapon/leftHandHeld/LeftHandItemLocators/BlueprintBookHeld";

            Transform found = null;
            try
            {
                found = LocalPlayer.transform.Find(bookPath);
            }
            catch
            {
                found = null;
            }

            if (found != null)
            {
                _bookHeld = found;
                MelonLogger.Msg("[VRPlayer] BlueprintBookHeld found - construction book will now be recognized as a menu.");
            }
        }

        private Transform _backpackQuickSelect;
        private float _lastBackpackQuickSelectSearchTime = 0f;

        /// <summary>
        /// Found via the whole-player activity scan: the quick-inventory hold goes through
        /// NEWITEMS/BackpackHeld/BackpackQuickSelect on the right hand - a different path
        /// structure than the left-hand items, but the same general held-menu pattern.
        /// </summary>
        private void FindBackpackQuickSelect()
        {
            if (_backpackQuickSelect != null || LocalPlayer == null) return;
            if (Time.time - _lastBackpackQuickSelectSearchTime < 1.0f) return;
            _lastBackpackQuickSelectSearchTime = Time.time;

            const string path =
                "PlayerAnimator/Root/Hips/Spine/Spine1/Spine2/RightShoulder/RightArm/RightForeArm/" +
                "RightForeArmTwistNew1/RightForeArmTwistNew2/RightForeArmTwistNew3/RightHand/" +
                "RightHandWeapon/rightHandHeld/NEWITEMS/BackpackHeld/BackpackQuickSelect";

            Transform found = null;
            try
            {
                found = LocalPlayer.transform.Find(path);
            }
            catch
            {
                found = null;
            }

            if (found != null)
            {
                _backpackQuickSelect = found;
                MelonLogger.Msg("[VRPlayer] BackpackQuickSelect found - quick inventory will now be recognized as a menu.");
            }
        }

        private Transform _tutorialBookHeld;
        private float _lastTutorialBookSearchTime = 0f;

        /// <summary>
        /// The right-hand construction/tutorial book, found the same way as everything else -
        /// goes through NEWITEMS on the right hand (same branch as BackpackHeld), different
        /// from the left-hand BlueprintBookHeld already handled.
        /// </summary>
        private void FindTutorialBookHeld()
        {
            if (_tutorialBookHeld != null || LocalPlayer == null) return;
            if (Time.time - _lastTutorialBookSearchTime < 1.0f) return;
            _lastTutorialBookSearchTime = Time.time;

            const string path =
                "PlayerAnimator/Root/Hips/Spine/Spine1/Spine2/RightShoulder/RightArm/RightForeArm/" +
                "RightForeArmTwistNew1/RightForeArmTwistNew2/RightForeArmTwistNew3/RightHand/" +
                "RightHandWeapon/rightHandHeld/NEWITEMS/TutorialBookHeld";

            Transform found = null;
            try
            {
                found = LocalPlayer.transform.Find(path);
            }
            catch
            {
                found = null;
            }

            if (found != null)
            {
                _tutorialBookHeld = found;
                MelonLogger.Msg("[VRPlayer] TutorialBookHeld found - construction/tutorial book will now be recognized as a menu.");
            }
        }
        

        
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
            // V145: Load Game / New Game / Return to Title closes the native menu
            // without necessarily sending B. A newly loaded gameplay/title root
            // must not inherit a paused volume or stale menu-canvas reference.
            // Additive streaming scenes are deliberately excluded.
            if (sceneName == "SonsMain" || sceneName == "SonsTitleScene" || sceneName == "BlankScene")
                ClosePauseOnSceneTransitionV145("scene-loaded:" + sceneName);

            // V146: the native scene handoff is not the completion of player spawning.
            // Keep the lightweight headset loading cover across scene changes.
            if (sceneName == "SonsTitleScene")
                ClearPreGameplayLoadingEpochV146("title-scene-loaded");
            else if (sceneName == "SonsMainLoading" ||
                (sceneName == "BlankScene" && UsedSceneName != "SonsMain"))
                BeginPreGameplayLoadingEpochV146("scene-loaded:" + sceneName);
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

            // CONFIRMED via log: this is the actual Origin used everywhere else in the mod
            // (HandleUpdate, SetSceneAndCamera, the desync watchdog) - previous fixes touched
            // the rig's OWN transform in CreateCameraRig, which is a DIFFERENT object and never
            // affected this one at all. Origin was sitting at whatever default position it
            // naturally spawned at (world origin) for the entire loading sequence, until the
            // first real SetSceneAndCamera call eventually corrected it. Align it with the
            // best available camera right here instead, so there's no long "far from body"
            // window during loading.
            if (UsedCamera != null)
            {
                Origin.position = UsedCamera.transform.position;
                Vector3 camForward = UsedCamera.transform.forward;
                camForward.y = 0f;
                if (camForward.sqrMagnitude > 0.001f)
                {
                    Origin.rotation = Quaternion.LookRotation(camForward.normalized, Vector3.up);
                }
            }

            // Ensure VR rig persists across scene loads
            DontDestroyOnLoad(Origin);

            // Attach stereo rendering system to the body
            StereoRender = Body.gameObject.AddComponent<StereoRender>();
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
            // V147: InventoryCamera -> the SAME known MainCameraFP is a tarp exit,
            // not a new gameplay-camera/spawn transition. The original SetSceneAndCamera
            // snap was overwriting Origin yaw with MainCameraFP yaw on EVERY tarp close,
            // and temporarily moving the eyes to a stale camera position while the
            // native player was still carrying the tarp's +1 m elevation offset.
            Camera previousCameraV147 = UsedCamera;
            Camera incomingCameraV147 = activeSceneAndCam.camera;
            if (incomingCameraV147 != null && incomingCameraV147.name == "InventoryCamera" &&
                previousCameraV147 != null && previousCameraV147.name == "MainCameraFP")
            {
                _tarpPreviousGameplayCameraV147 = previousCameraV147;
                MelonLogger.Msg($"[VRPlayer][V147 TarpExit] ENTRY remembered MainCameraFP id={previousCameraV147.GetInstanceID()} yaw={previousCameraV147.transform.eulerAngles.y:F1}");
            }
            bool returningFromTarpV147 = InventoryCamIsActive &&
                UsedCamera != null && UsedCamera.name == "InventoryCamera" &&
                incomingCameraV147 != null && incomingCameraV147.name == "MainCameraFP";
            bool returningToKnownGameplayCamV147 = returningFromTarpV147 &&
                incomingCameraV147 == _tarpPreviousGameplayCameraV147;
            string newMenuSceneV145 = activeSceneAndCam.scene.name;
            // V170: a multiplayer MainCameraFP lives in DontDestroyOnLoad and never
            // trips the older SonsMain/AER-ready gate. Track actual gameplay camera
            // adoption separately so the one-shot V159 title heading fix still runs.
            // Do not alter fresh-boot title, tarp, or cinematic transitions.
            bool returningToTitleV158 = newMenuSceneV145 == "SonsTitleScene" &&
                UsedSceneName != "SonsTitleScene" &&
                incomingCameraV147 != null && incomingCameraV147.name == "Main Camera" &&
                (global::SonsVRMod.StereoRender.AerGameplayReadyForActivation ||
                 _v170GameplayCameraSeenSinceTitle);
            // Camera changes between inventory and MainCameraFP are NOT native
            // pause exits. Only context changes into/out of title/gameplay qualify.
            if (!string.Equals(UsedSceneName, newMenuSceneV145, StringComparison.Ordinal) &&
                (newMenuSceneV145 == "SonsMain" || newMenuSceneV145 == "SonsTitleScene" ||
                 UsedSceneName == "SonsTitleScene"))
                ClosePauseOnSceneTransitionV145("camera-scene:" + UsedSceneName + "->" + newMenuSceneV145);
            if (newMenuSceneV145 == "SonsTitleScene")
                ClearPreGameplayLoadingEpochV146("title-camera-context");
            else if (newMenuSceneV145 == "SonsMainLoading" ||
                (newMenuSceneV145 == "BlankScene" && UsedSceneName == "SonsTitleScene"))
                BeginPreGameplayLoadingEpochV146("camera-scene:" + UsedSceneName + "->" + newMenuSceneV145);
            UsedSceneName = newMenuSceneV145;
            UsedCamera = activeSceneAndCam.camera;
            if (UsedSceneName == "SonsTitleScene" && UsedCamera != null &&
                UsedCamera.name == "Main Camera")
                _v170GameplayCameraSeenSinceTitle = false;

            // V159: the title's WorldSpace canvases take a ONE-TIME world-space heading
            // snapshot when converted. V158 delayed the yaw correction until *after*
            // those snapshots, leaving the menu behind the newly corrected view.
            // Align during native title-camera adoption, BEFORE HandleUpdate can place
            // any title or modal canvas. Never recenter continuously (title breathing).
            _v159PendingTitleCamera = null;
            if (returningToTitleV158 && Origin != null)
            {
                Vector3 beforeOrigin = Origin.position;
                Origin.position = UsedCamera.transform.position; // position once; no height lift
                if (!TryAlignTitleYawBeforeUiV159(UsedCamera, "camera-adoption"))
                {
                    // Only if the HMD/head is not ready yet. Title UI conversion waits
                    // for this to complete rather than capturing the old orientation.
                    _v159PendingTitleCamera = UsedCamera;
                    _v159TitlePendingSince = Time.realtimeSinceStartup;
                    MelonLogger.Warning("[VRPlayer][V159 TitlePreUI] PENDING: head pose unavailable at camera adoption; deferring title canvas placement");
                }
                MelonLogger.Msg("[VRPlayer][V159 TitlePreUI] ANCHORED originBefore=" +
                    beforeOrigin + " originAfter=" + Origin.position +
                    " nativeYaw=" + UsedCamera.transform.eulerAngles.y.ToString("F1") +
                    " titleLift=EYES_ONLY");
            }

            // V33 AER: a real native-camera transition (MainCameraFP <-> InventoryCamera,
            // title/gameplay, etc.) can otherwise leave one persistent eye texture showing the
            // previous camera context for one extra alternate-eye interval. Briefly render both
            // eyes again; no texture reallocation and no change to any menu-specific logic.
            if (StereoRender != null)
            {
                StereoRender.RequestAerResyncV33($"source camera -> {(UsedCamera != null ? UsedCamera.name : "null")}");
            }

            if (UsedCamera != null)
            {
                UsedCamera.targetTexture = null;
                //UsedCamera.pixelRect = new Rect(0, 0, 1080, 720); // Optional: small preview

                // Fully re-anchor the rig to the new camera whenever the camera genuinely
                // changes (e.g. transitioning from the title-screen camera to the real
                // MainCameraFP once gameplay starts). Without this, the rig stays permanently
                // stuck at whichever camera's transform it was first created with - causing a
                // stale, wrong rotation (tilted view) and stale position (body far away) once
                // you switch to a completely different camera/location.
                //
                // CONFIRMED via log: only do this for MainCameraFP specifically. InventoryCamera
                // is a separate camera the game positions wherever it wants for the flat-screen
                // tarp display - it has NOTHING to do with your actual position, and this mod
                // already has its own separate logic (elsewhere) that repositions InventoryCamera
                // to match your head. Snapping Origin to its raw, pre-repositioning, distant
                // default location was the actual cause of the far-away-body bug.
                if (Origin != null && UsedCamera.name.Equals("MainCameraFP"))
                {
                    if (!returningToKnownGameplayCamV147)
                    {
                        // Only correct YAW (horizontal facing direction) - never pitch/roll. A VR
                    // headset already handles up/down orientation correctly via its own
                    // hardware IMU; copying the camera's FULL rotation risked baking in an
                    // extreme, non-standing camera state (e.g. if this fires during the game's
                    // intro helicopter-drop sequence, which is tilted and high in the air) as
                    // the VR rig's PERMANENT base rotation, since nothing corrects it after.
                    Origin.position = UsedCamera.transform.position;
                    Vector3 campForward = UsedCamera.transform.forward;
                    campForward.y = 0f;
                    if (campForward.sqrMagnitude > 0.001f)
                    {
                        Origin.rotation = Quaternion.LookRotation(campForward.normalized, Vector3.up);
                    }

                    }
                    else
                    {
                        // Keep the current live tracked HMD/rig yaw. The tarp's pre-session
                        // Origin position is restored later in this same method; neither
                        // position nor rotation may be copied from the old game camera here.
                        MelonLogger.Msg($"[VRPlayer][V147 TarpExit] Reused MainCameraFP: skipped redundant Origin teleport/yaw snap. rigYaw={Origin.eulerAngles.y:F1} cameraYaw={UsedCamera.transform.eulerAngles.y:F1} deltaYaw={Mathf.DeltaAngle(Origin.eulerAngles.y, UsedCamera.transform.eulerAngles.y):F1} playerY={(LocalPlayer != null ? LocalPlayer.transform.position.y : float.NaN):F3} savedPlayerY={_tarpBasePlayerY:F3}");
                    }

                    // CONFIRMED BUG (user report): resetting on every transition to a camera
                    // NAMED "MainCameraFP" also fired every time the tarp/inventory screen was
                    // closed (which switches back to that same original camera), not just on a
                    // genuine reload - re-triggering the auto pause-menu open+close constantly
                    // during normal gameplay. A real new session gets a genuinely DIFFERENT
                    // MainCameraFP camera instance; closing the tarp just re-activates the exact
                    // same one that was already active before. Only reset when it's actually a
                    // different instance.
                    if (UsedCamera != _lastKnownMainCameraFP)
                    {
                        _lastKnownMainCameraFP = UsedCamera;
                        _gameplayReadySinceTime = -1f;
                        _headRotationWhenGameplayReady = null;
                        // Reset KeyboardHack's own success/in-progress state too, now that it owns
                        // that tracking directly - same intent as the old _hasAutoOpenedMenuOnce
                        // reset this replaced: allow the fix to retrigger on a genuinely new
                        // session, not just once per game process.
                        if (kh != null) kh.ResetAutoMenuFixState();
                    }
                }

                bool wasInventoryCamActive = InventoryCamIsActive;
                if (UsedCamera.name.Equals("InventoryCamera"))
                {
                    InventoryCamIsActive = true;
                }
                else
                {
                    InventoryCamIsActive = false;
                }

                // zer0int v4: inventory-only far clip. This updates camera/projection state only;
                // it does NOT recreate the eye RenderTextures.
                if (wasInventoryCamActive != InventoryCamIsActive && StereoRender != null)
                {
                    StereoRender.SetFarClipDistance(InventoryCamIsActive && TarpFarClipEnabled
                        ? TarpFarClipMeters
                        : global::SonsVRMod.StereoRender.GameplayFarClipMeters);
                }

                // V116: cache LayoutItems per tarp session, never on a timer. Reset on either
                // edge so stale/destroyed Unity wrappers cannot leak into the next inventory use.
                if (wasInventoryCamActive != InventoryCamIsActive)
                {
                    ResetTarpLayoutItemCacheV116(InventoryCamIsActive ? "tarp-open" : "tarp-close");
                }

                // DIAGNOSTIC: investigating the ground-tarp-sunken-into-terrain issue. Capturing
                // player/camera position and real ground clearance right when the tarp opens, so
                // we can directly compare a "sunken" case (flat ground) against a "clear" case
                // (elevated, e.g. standing on a rock) instead of guessing what to adjust.
                if (!wasInventoryCamActive && InventoryCamIsActive && LocalPlayer != null)
                {
                    Vector3 playerPos = LocalPlayer.transform.position;
                    string groundInfo = "no ground hit";
                    if (Physics.Raycast(playerPos, Vector3.down, out RaycastHit groundHit, 5f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        groundInfo = $"hit={groundHit.collider.gameObject.name} dist={groundHit.distance:F3}m groundY={groundHit.point.y:F3}";
                    }
                    MelonLogger.Msg($"[VRPlayer][TarpDiag] Tarp/inventory opened. playerPos={playerPos} invCamPos={UsedCamera.transform.position} invCamRot={UsedCamera.transform.eulerAngles} headPos={(StereoRender != null && StereoRender.Head != null ? StereoRender.Head.position.ToString() : "N/A")} groundBelowPlayer=[{groundInfo}]");

                    // V44 validity marker. This is deliberately NOT an activation trigger and does
                    // not touch AER at all. It only proves that this continuous telemetry run made
                    // it to a user-opened crafting tarp from ordinary player gameplay, so later
                    // telemetry can be distinguished from any weird pre-game/under-map phase.
                    if (global::SonsVRMod.StereoRender.AerGameplaySpatialTelemetryV44Enabled &&
                        global::SonsVRMod.StereoRender.AlternateEyeRenderingEnabled &&
                        !global::SonsVRMod.StereoRender.AerGameplaySpatialTelemetryTarpMarkerSeen)
                    {
                        global::SonsVRMod.StereoRender.AerGameplaySpatialTelemetryTarpMarkerSeen = true;
                        MelonLogger.Warning("[VRPlayer][AER V44][VALIDITY MARKER] First crafting-tarp open observed during active AER. Subsequent V44 telemetry windows are marked POST_TARP; no rendering state changed.");
                    }

                    // WORKAROUND for the ground-tarp-sunken-into-terrain issue. CONFIRMED via
                    // user testing that a single one-time nudge here doesn't work - the game's
                    // own gravity/grounding logic snaps the player back down almost immediately
                    // (visible as a brief upward jump that instantly falls back to sunken).
                    // Capturing the base Y once here; the actual elevation is now continuously
                    // reasserted every frame in UpdateTarpElevationWorkaround (called from
                    // HandleUpdate) for as long as the tarp stays open, fighting that grounding
                    // every frame rather than relying on a write that gets overwritten.
                    _tarpBasePlayerY = playerPos.y;
                    if (Origin != null)
                    {
                        _tarpBaseOriginY = Origin.position.y;
                        _tarpBaseOriginPosition = Origin.position;
                        _tarpBaseOriginPositionValid = true;
                        MelonLogger.Msg($"[VRPlayer][TarpRig] Saved pre-tarp Origin={_tarpBaseOriginPosition} for exact restore on close.");
                    }

                    // V27: hide only the player's renderers while the tarp is open. The entire
                    // character rig remains alive for crafting/equip animations and native game
                    // logic; only pixels are suppressed. This also lets us remove the old extra
                    // +0.55m "get my head above my own torso" lift below.
                    SetTarpPlayerVisualsHiddenV27(true);

                    // Reset so the tarp tutorial canvas gets freshly placed at wherever the
                    // player is looking THIS time the tarp opens, not stuck at wherever it was
                    // frozen on some earlier, possibly distant, tarp session.
                    _addItemsToMatPlacedOnce = false;

                    // Per user's own request: the VR rig's tracked head position can drift out of
                    // alignment with the character's own body over a session. Scheduling a
                    // one-time recenter, delayed per TarpRecenterDelaySeconds to give the tarp
                    // time to at least start unrolling first.
                    MelonCoroutines.Start(RecenterOriginToPlayerAfterDelay());
                }
                else if (wasInventoryCamActive && !InventoryCamIsActive && LocalPlayer != null)
                {
                    // V147: first remove the temporary elevation/recenter WHILE the avatar
                    // is still hidden. Restoring forceRenderingOff BEFORE moving the body down
                    // briefly exposed the tarp's elevated/jump-like third-person pose.
                    // Do not modify animator, controller locomotion or tarp-visible renderers.
                    if (TarpElevationWorkaroundOffset != 0f)
                        SetPlayerYSafely(_tarpBasePlayerY);
                    if (Origin != null)
                    {
                        // zer0int v5: restore the complete pre-tarp rig anchor, not only Y.
                        // v4 left the tarp-specific X/Z recenter behind after inventory closed,
                        // which moved later handheld menus into/behind the avatar body.
                        if (_tarpBaseOriginPositionValid)
                        {
                            Vector3 beforeRestore = Origin.position;
                            Origin.position = _tarpBaseOriginPosition;
                            MelonLogger.Msg($"[VRPlayer][TarpRig] Restored pre-tarp Origin {_tarpBaseOriginPosition} from {beforeRestore}; tarp X/Z/Y adjustment fully removed.");
                            _tarpBaseOriginPositionValid = false;
                        }
                        else
                        {
                            Vector3 originPos = Origin.position;
                            originPos.y = _tarpBaseOriginY;
                            Origin.position = originPos;
                        }
                    }

                    // Only unhide after world-space player/eye offsets are restored; leave
                    // the V116/V27 tarp DURING-menu visibility architecture untouched.
                    SetTarpPlayerVisualsHiddenV27(false);
                    _tarpPreviousGameplayCameraV147 = null;
                    _tarpExitTraceFramesV147 = 9;
                    _tarpExitTraceIndexV147 = 0;
                    MelonLogger.Msg($"[VRPlayer][V147 TarpExit] RESTORED reusedMain={returningToKnownGameplayCamV147} playerY={LocalPlayer.transform.position.y:F3} baselineY={_tarpBasePlayerY:F3} origin={(Origin != null ? Origin.position.ToString() : "null")} yaw={(Origin != null ? Origin.eulerAngles.y.ToString("F1") : "null")} head={(StereoRender != null && StereoRender.Head != null ? StereoRender.Head.position.ToString() : "null")}");
                }

                LocalPlayer = FindLocalPlayerFromCamera(UsedCamera, InventoryCamIsActive);
                if (LocalPlayer != null)
                {
                    firstPersonCharacter = LocalPlayer.GetComponent<Il2CppFirstPersonCharacter>();
                    if (kh != null)
                    {
                        kh.LocalPlayer = LocalPlayer;
                    }
                    if (UsedCamera.name == "MainCameraFP")
                        _v170GameplayCameraSeenSinceTitle = true;
                    TryArmDeferredGameplayV170("camera-adoption");
                }

                canUpdate = true;
            }
        }

        // V170: keep the V43 single-player gate unchanged. In multiplayer Unity
        // owns MainCameraFP from DontDestroyOnLoad, not SonsMain. Do not activate
        // deferred AER until the V146 *physical spawn* checks release the cover;
        // a cutscene or an under-map transitional player must not arm AER early.
        private bool _v170GameplayCameraSeenSinceTitle;
        private bool _v170MultiplayerSpawnConfirmed;

        private void TryArmDeferredGameplayV170(string reason)
        {
            if (!global::SonsVRMod.StereoRender.AerDeferredGameplayActivationEnabled ||
                global::SonsVRMod.StereoRender.AerGameplayReadyForActivation ||
                UsedCamera == null || UsedCamera.name != "MainCameraFP" || LocalPlayer == null)
                return;

            bool singlePlayer = string.Equals(UsedSceneName, "SonsMain", StringComparison.Ordinal);
            bool multiplayer = string.Equals(UsedSceneName, "DontDestroyOnLoad", StringComparison.Ordinal) &&
                _v170MultiplayerSpawnConfirmed && !_v146LoadingEpochActive;
            if (!singlePlayer && !multiplayer) return;

            global::SonsVRMod.StereoRender.AerGameplayReadyForActivation = true;
            MelonLogger.Warning("[VRPlayer][AER V170] gameplay gate ARMED: " +
                "owner=" + UsedSceneName + " camera=MainCameraFP LocalPlayer=True" +
                " multiplayerSpawnVerified=" + _v170MultiplayerSpawnConfirmed +
                " reason=" + reason +
                "; staying stereo until MainCameraFP HDRP copy and EOF.");
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
            HandleUpdate();
            DisableMouseRotation();
        }

        /// <summary>
        /// Core update logic for VR player behavior.
        /// </summary>
        // V156: passive evidence for the scripted beach 180-degree facing discrepancy.
        // This never changes headset/world yaw. A forced 180-degree recenter during the
        // spinning helicopter would be a severe comfort regression without phase data.
        private Camera _v156LastCinematicCamera;
        private float _v156NextCinematicPoseLog;
        private void TraceCinematicPoseV156()
        {
            if (!IsNativeCinematicCameraV156 || StereoRender == null ||
                StereoRender.LeftCam == null || StereoRender.RightCam == null ||
                Origin == null || StereoRender.Head == null)
            {
                _v156LastCinematicCamera = null;
                return;
            }
            float now = Time.realtimeSinceStartup;
            bool newCamera = UsedCamera != _v156LastCinematicCamera;
            if (!newCamera && now < _v156NextCinematicPoseLog) return;
            _v156LastCinematicCamera = UsedCamera;
            _v156NextCinematicPoseLog = now + 8f;
            Vector3 cameraPosition = UsedCamera.transform.position;
            Vector3 eyeMid = (StereoRender.LeftCam.transform.position +
                StereoRender.RightCam.transform.position) * 0.5f;
            Vector3 delta = eyeMid - cameraPosition;
            float nativeYaw = UsedCamera.transform.eulerAngles.y;
            float rigYaw = Origin.eulerAngles.y;
            float headYaw = StereoRender.Head.eulerAngles.y;
            MelonLogger.Msg("[VRPlayer][V156 CinematicPose] " +
                (newCamera ? "CAMERA_ADOPTED " : "SAMPLE ") +
                "camera=" + UsedCamera.name + "#" + UsedCamera.GetInstanceID() +
                " deltaWorld=" + delta + " separation=" + delta.magnitude.ToString("F3") +
                "m nativeYaw=" + nativeYaw.ToString("F1") +
                " rigYaw=" + rigYaw.ToString("F1") +
                " headYaw=" + headYaw.ToString("F1") +
                " nativeMinusRigYaw=" + Mathf.DeltaAngle(rigYaw, nativeYaw).ToString("F1") +
                " eyeMid=" + eyeMid + " nativePos=" + cameraPosition +
                " nativePitchRoll=" + UsedCamera.transform.eulerAngles.x.ToString("F1") +
                "," + UsedCamera.transform.eulerAngles.z.ToString("F1") +
                " loadingEpoch=" + _v146LoadingEpochActive +
                " introLatched=" + _v155IntroLatched);
        }

        // V157: VR cinematic orientation is a SHOT property, not a tracking property.
        // V156 proved that Origin yaw stayed 264 deg while two different native
        // MainCameraCustcene cameras were adopted with authored headings 115/145 deg.
        // Align once on camera adoption or a HARD per-frame rotation discontinuity.
        // Never chase smooth camera rotation (especially during a helicopter crash),
        // never set Head.localRotation, never touch gameplay/title/tarp cameras.
        private Camera _v157ShotCamera;
        private Quaternion _v157PreviousShotRotation = Quaternion.identity;
        private float _v157PreviousShotTime;
        private int _v157PreviousShotFrame = -1;
        private bool _v157HasPreviousShotPose;
        private bool _v157PendingShotAlign;
        private int _v157PendingShotFrame;
        private float _v157PendingShotTime;
        private string _v157PendingShotReason;
        private float _v157LastShotAlignTime = -100f;

        private void UpdateCinematicShotYawV157()
        {
            if (!IsNativeCinematicCameraV156 || Origin == null ||
                StereoRender == null || StereoRender.Head == null || UsedCamera == null)
            {
                _v157ShotCamera = null;
                _v157HasPreviousShotPose = false;
                _v157PendingShotAlign = false;
                return;
            }

            float now = Time.realtimeSinceStartup;
            int frame = Time.frameCount;
            Camera camera = UsedCamera;
            Quaternion nativeRotation = camera.transform.rotation;
            float nativeYaw = camera.transform.eulerAngles.y;
            bool adoptedCamera = camera != _v157ShotCamera;
            float stepAngle = 0f;
            float stepYaw = 0f;
            bool validAdjacentFrame = !adoptedCamera && _v157HasPreviousShotPose &&
                frame == _v157PreviousShotFrame + 1 &&
                now > _v157PreviousShotTime && now - _v157PreviousShotTime < 0.25f;

            if (validAdjacentFrame)
            {
                stepAngle = Quaternion.Angle(_v157PreviousShotRotation, nativeRotation);
                stepYaw = Mathf.Abs(Mathf.DeltaAngle(_v157PreviousShotRotation.eulerAngles.y, nativeYaw));
            }
            if (adoptedCamera)
            {
                _v157ShotCamera = camera;
                _v157PendingShotAlign = true;
                _v157PendingShotFrame = frame;
                _v157PendingShotTime = now;
                _v157PendingShotReason = "native-camera-adopted";
                MelonLogger.Msg("[VRPlayer][V157 ShotYaw] CANDIDATE camera=" +
                    camera.name + "#" + camera.GetInstanceID() +
                    " reason=native-camera-adopted nativeYaw=" + nativeYaw.ToString("F1") +
                    " eyeYaw=" + StereoRender.Head.eulerAngles.y.ToString("F1"));
            }
            else if (!_v157PendingShotAlign && validAdjacentFrame &&
                stepAngle >= 85f && stepYaw >= 85f &&
                now - _v157LastShotAlignTime > 0.70f)
            {
                // One-frame large YAW + quaternion step is strong evidence of an
                // authored hard cut. Not triggered by ordinary animated camera motion.
                _v157PendingShotAlign = true;
                _v157PendingShotFrame = frame;
                _v157PendingShotTime = now;
                _v157PendingShotReason = "hard-native-rotation-cut";
                MelonLogger.Msg("[VRPlayer][V157 ShotYaw] CANDIDATE camera=" +
                    camera.name + "#" + camera.GetInstanceID() +
                    " reason=hard-native-rotation-cut angularStep=" + stepAngle.ToString("F1") +
                    " yawStep=" + stepYaw.ToString("F1"));
            }

            // Deferred at least one frame so camera handoff/animation pose is real,
            // not the prior scene's last transform. Pending candidates expire safely.
            if (_v157PendingShotAlign && frame > _v157PendingShotFrame)
            {
                bool expired = now - _v157PendingShotTime > 0.55f;
                bool stableStep = validAdjacentFrame && stepAngle < 25f;
                if (stableStep)
                {
                    Vector3 intendedForward = camera.transform.forward;
                    Vector3 actualForward = StereoRender.Head.forward;
                    intendedForward.y = 0f;
                    actualForward.y = 0f;
                    if (intendedForward.sqrMagnitude > 0.10f &&
                        actualForward.sqrMagnitude > 0.10f)
                    {
                        intendedForward.Normalize();
                        actualForward.Normalize();
                        float correction = Vector3.SignedAngle(
                            actualForward, intendedForward, Vector3.up);
                        float beforeYaw = StereoRender.Head.eulerAngles.y;
                        if (Mathf.Abs(correction) > 5f)
                        {
                            // Rotate the *world VR origin* around Unity Y only. This
                            // preserves tracked head-relative movement and pitch/roll.
                            // There is no interpolation and NO continuous yaw chase.
                            Origin.rotation = Quaternion.AngleAxis(correction, Vector3.up) *
                                Origin.rotation;
                            _v157LastShotAlignTime = now;
                        }
                        MelonLogger.Msg("[VRPlayer][V157 ShotYaw] ALIGNED camera=" +
                            camera.name + "#" + camera.GetInstanceID() +
                            " reason=" + _v157PendingShotReason +
                            " yawCorrection=" + correction.ToString("F1") +
                            " nativeYaw=" + nativeYaw.ToString("F1") +
                            " eyeBefore=" + beforeYaw.ToString("F1") +
                            " eyeAfter=" + StereoRender.Head.eulerAngles.y.ToString("F1") +
                            " pitchRollUntouched=True oneShot=True");
                        _v157PendingShotAlign = false;
                    }
                    else
                    {
                        // Near-vertical camera pitch has no meaningful planar heading.
                        if (expired) _v157PendingShotAlign = false;
                    }
                }
                else if (expired)
                {
                    MelonLogger.Msg("[VRPlayer][V157 ShotYaw] SKIP unstable-shot camera=" +
                        camera.name + " reason=" + _v157PendingShotReason +
                        " angularStep=" + stepAngle.ToString("F1") +
                        " eyes remain HMD-controlled");
                    _v157PendingShotAlign = false;
                }
            }

            // Sampling is passive: one pose sample per frame, no physics queries.
            _v157PreviousShotRotation = nativeRotation;
            _v157PreviousShotTime = now;
            _v157PreviousShotFrame = frame;
            _v157HasPreviousShotPose = true;
        }

        // V159: title return yaw must precede world-space UI placement. Do NOT
        // wait for two camera frames: V158 showed menu conversion 114 ms BEFORE
        // its delayed yaw correction. The native title camera's heading is already
        // available in SetSceneAndCamera; all changes are one-shot and world-Y only.
        private Camera _v159PendingTitleCamera;
        private float _v159TitlePendingSince;

        private bool TryAlignTitleYawBeforeUiV159(Camera camera, string cause)
        {
            if (camera == null || UsedCamera != camera ||
                !IsNativeTitleCameraContextV158 || Origin == null ||
                StereoRender == null || StereoRender.Head == null) return false;

            Vector3 intended = camera.transform.forward;
            Vector3 current = StereoRender.Head.forward;
            intended.y = 0f;
            current.y = 0f;
            if (intended.sqrMagnitude < 0.10f || current.sqrMagnitude < 0.10f)
                return false; // avoid an arbitrary yaw if either camera points nearly vertically

            intended.Normalize();
            current.Normalize();
            float correction = Vector3.SignedAngle(current, intended, Vector3.up);
            float eyeBefore = StereoRender.Head.eulerAngles.y;
            if (Mathf.Abs(correction) > 1f)
                Origin.rotation = Quaternion.AngleAxis(correction, Vector3.up) * Origin.rotation;
            MelonLogger.Msg("[VRPlayer][V159 TitlePreUI] ALIGNED cause=" + cause +
                " frame=" + Time.frameCount + " camera=" + camera.name +
                "#" + camera.GetInstanceID() +
                " yawCorrection=" + correction.ToString("F1") +
                " nativeYaw=" + camera.transform.eulerAngles.y.ToString("F1") +
                " eyeBefore=" + eyeBefore.ToString("F1") +
                " eyeAfter=" + StereoRender.Head.eulerAngles.y.ToString("F1") +
                " BEFORE_TITLE_CANVAS=True oneShot=True");
            return true;
        }

        private void FinishPendingTitleYawBeforeUiV159()
        {
            Camera camera = _v159PendingTitleCamera;
            if (camera == null) return;
            if (!IsNativeTitleCameraContextV158 || UsedCamera != camera)
            {
                _v159PendingTitleCamera = null;
                return;
            }
            if (TryAlignTitleYawBeforeUiV159(camera, "head-ready-fallback"))
            {
                _v159PendingTitleCamera = null;
                return;
            }
            // Fail open, NEVER freeze the title menu if SteamVR pose is invalid.
            // Log the timeout rather than applying an unsafe arbitrary yaw.
            if (Time.realtimeSinceStartup - _v159TitlePendingSince > 2f)
            {
                MelonLogger.Warning("[VRPlayer][V159 TitlePreUI] TIMEOUT: title UI will display without forced yaw");
                _v159PendingTitleCamera = null;
            }
        }

        private void HandleUpdate()
        {
            if (canUpdate == false)
            {
                return;
            }

            // Many functions below access StereoRender.Head/LeftCam/etc without checking
            // StereoRender itself for null first (confirmed the cause of a crash-loop during
            // early loading, before stereo setup finishes). Single guard here is simpler and
            // safer than patching every individual call site.
            if (StereoRender == null)
            {
                return;
            }

            // V159: this must precede ConvertTitleMenuCanvasesToWorldSpace and
            // UpdateModalDialogCanvasForVR, never run after their placement.
            FinishPendingTitleYawBeforeUiV159();

            // V143: DISABLED auto Start/B FPS workaround entirely for the manual transition trace.
            // TryAutoOpenMenuOnceForFpsFix();
            UpdateTarpElevationWorkaround();
            MaintainTarpPlayerVisualSuppressionV27();

            DebugScanActiveCanvases();
            DebugScanActiveCameras();
            DebugInspectCraftMatWorldUI();
            DebugScanPostProcessVolumes();
            DebugScanKeywordObjects();
            DebugDumpSpecialActionsChildren();
            DebugDumpInteractStimuli();
            DebugDumpItemTemplatePickupGui();
            // FixOverlappingActionPrompts(); // CONFIRMED DEAD END (LinkUiElement.SetWorldSpaceOffset
            // has zero visual effect - real culprit was a LayoutGroup on the actual rendered
            // objects, now handled by FixWorldScreenSpaceElementOverlap instead) - left disabled
            DebugDumpHudPromptOverlap();
            DebugDumpCutscenePromptDetail();
            EnsureWorldScreenSpaceFixCoroutine();
            ConvertItemInfoPanelToWorldSpace();
            // ConvertBinocularsCanvasToWorldSpace(); // REVERTED per user request - back to
            // pristine pre-work state (binoculars Canvas untouched/native ScreenSpaceOverlay,
            // same as before this whole investigation started). Zoom was never solved despite
            // extensive investigation (see /areas/sonsvr-mod.md for the full history) and even
            // the safe "just show it correctly positioned, no zoom" version isn't worth keeping
            // active by itself per user's explicit request to fully back out.
            DebugDumpPauseMenuSubtree();
            DebugScanNewTopLevelRoots();
            DebugScanActiveCamerasForConfirmDialog();
            UpdateFlatMenuView();
            UpdateAimReticle();
            DebugDumpWeaponAttachments();
            // DebugScanSaveLoadTypes() disabled - no longer needed (already extracted what we
            // needed from it), and it was doing a large synchronous reflection scan (86 types,
            // hundreds of method lookups) right during the exact window VR stereo rendering
            // initializes - a plausible contributing factor to the crash given VR needs tight,
            // consistent frame timing right at that handshake with SteamVR.
            CheckQuickSaveInput();
            DebugScanLocalPlayerActivity();
            DebugScanHudMessages();
            CheckForStuckViewDesync();
            DebugLogVehicleCameraState();
            // DebugCompareCameraToPlayer() disabled - confirmed the multiplayer/inventory fix
            // works correctly, no longer need this logging every 3 seconds during normal play.
            // DebugScanActiveCameras() disabled - confirmed the multiplayer fix works, no
            // longer need a scene-wide camera search running every 2 seconds.
            // RE-ENABLED per user request, after extensive attempts at a flat-screenshot-quad
            // alternative proved unreliable (material property manipulation on the quad broke
            // its rendering entirely, with no clean fix found). This approach is known to
            // actually render the title menu correctly in VR (confirmed previously), but has a
            // KNOWN LIMITATION: it can get occluded by the title screen's own background scene
            // (e.g. the rising hands/water intro animation), since a world-space canvas respects
            // normal 3D depth testing unlike ScreenSpaceOverlay. Worth revisiting with an
            // always-on-top rendering trick (similar to the ZTest-Always recipe already proven
            // for the working menu cursors elsewhere in this file) as a follow-up once this
            // baseline is confirmed working again in the current build.
            ConvertTitleMenuCanvasesToWorldSpace();
            UpdateCustomLoadingScreen();

            bool uiCursorActive = IsUiCursorActive;
            if (uiCursorActive != _lastInventoriCameraIsActive)
            {
                MelonLogger.Msg($"[VRPlayer] UI cursor menu state changed: {uiCursorActive} (InventoryCamIsActive={InventoryCamIsActive}, Cursor.lockState={UnityEngine.Cursor.lockState})");
                _lastInventoriCameraIsActive = uiCursorActive;
            }

            if (_screenSpaceCanvas == null)
            {
                FindInventoryCanvas();
            }

            if (_playerMiniMapCanvas == null)
            {
                FindMiniMapCanvas();
            }

            // V115: V114's extra HDRP HUD camera is retired. Its preflight proved that the active
            // RGB111110 HDRP path forces empty pixels to alpha=1, while the trace also showed that
            // converting the ENTIRE interactive ScreenSpaceCanvas to ScreenSpaceCamera hijacks the
            // tarp/cooking GraphicRaycaster contract. Return to the older interaction-preserving
            // WorldSpace presentation, but make the two native HUD systems share one measured
            // 1920x1080 pixel-to-metre contract instead of independently hand-positioning them.
            // The physically-held GPS remains genuine 3D geometry and is never touched.
            if (UnifiedHudWorldPlaneV116.Enabled)
            {
                UnifiedHudWorldPlaneV116.Tick(
                    _screenSpaceCanvas,
                    _playerMiniMapCanvas,
                    _PlayerMiniMapTransf,
                    UsedCamera,
                    StereoRender != null ? StereoRender.Head : null);
            }
            else if (NativeHudCompositorV114.Enabled)
            {
                // Historical diagnostic fallback only. Disabled in V115.
                NativeHudCompositorV114.Tick(this, _screenSpaceCanvas, _playerMiniMapCanvas);
            }
            else
            {
                // Exact V108-style fallback retained for archaeology only.
                if (_screenSpaceCanvas != null && _screenSpaceCanvas.renderMode != RenderMode.WorldSpace)
                    ConvertCanvasToWorldSpace();
                if (_playerMiniMapCanvas != null && _playerMiniMapCanvas.renderMode != RenderMode.WorldSpace)
                    ConvertMinimapCanvasToWorldSpace();

                UpdateCanvasPositionForVR();
                UpdateSmallHudSubtreeScale();
                UpdateVitalsUiScaleV14();
            }

            // Intentional user-facing enlargements for modal/cooking/item-info views remain; they
            // are independent of the map/ring placement contract.
            UpdateCookingRecipeOverlayScaleV24();
            UpdateAdditionalHudInfoScalesV25();
            // V169L: source-pixel deadzone/settling before V169D placement; required-item unfolding unchanged.
            UiPlacementCorrectionsV169E.Tick(_screenSpaceCanvas,
                StereoRender != null ? StereoRender.Head : null);
            UpdateModalDialogCanvasForVR();
            UpdateAddItemsToMatCanvasForVR();
            // Loading screen investigation set aside for now (per user request, to prioritize the
            // pause-menu fix) - DiagNestedTitleMenuCanvases() and UpdateGenericLoadingScreenCanvases()
            // calls removed here, functions themselves left in place below for whenever this is
            // revisited. The old letter-by-letter loader remains hard-off; V114 uses the cheap dedicated-layer loader below.
            // REVERTED per user's own suggestion: four fix attempts on the native Y-skip prompt
            // (anchor re-centering, LayoutGroup disabling, alpha/Animator-IsActive forcing,
            // parent-active forcing, Canvas.ForceUpdateCanvases) each confirmed correct in the
            // underlying data via log inspection, yet had zero visible effect across many test
            // rounds. Rather than keep fighting an opaque native system - and risk side effects
            // across the other two skippable intro cutscenes that likely share this same object -
            // no longer touching the native prompt at all. See UpdateSkipHintOverlay below for
            // the replacement: a fully custom overlay we control end-to-end.
            // FixCutscenePromptPosition();
            // UpdateSkipPromptAlpha();
            UpdateSkipHintOverlay();
            UpdateAerV102HmdCueOverlay(); // V104: dormant in production; retained for future HMD-guided experiments.
            // A-CLOSE: RESOLVED. Root cause was a desynced gaze/interact camera
            // (MainCameraCustcene) - it drives the game's own gaze check for the close prompt but
            // was never wired up to track real VR head movement, confirmed via log (its rotation
            // stayed essentially fixed while the real tracked head swept far more broadly). Every
            // input-layer fix tried before this (gamepad-vs-keyboard detection, a disabled
            // Collider, raw synthetic button presses) was addressing symptoms downstream of this
            // gate, which is why none of them worked. Fix: NudgeCutsceneCameraDown applies a
            // single one-time local rotation to that camera the moment the laptop's last page is
            // reached - confirmed working by the user. See its own doc comment for the full
            // history, including a more aggressive full-transform-sync attempt that had to be
            // reverted after it broke the intro's outro camera transition.
            NudgeCutsceneCameraDown();
            UpdatePreGameplayEyeRenderSuppressionV107();
            UpdatePreGameplayLoadingOverlayV108();
            if (StereoRender != null) StereoRender.ApplyTitleLoadingEyeLiftV110();
            UpdateCinematicShotYawV157(); // one-shot native camera adoption / hard-cut recenter
            TraceCinematicPoseV156(); // V156 passive XYZ+orientation telemetry (now after V157 recenter)
            NeutralizeNativeCamera();
            RedirectNativeCameraFromScreen();
            ForceNativeCameraLowRes();
            DiagPauseMenuSignals();
            UpdateHdrpDataCopyFromNativeCamera();
            ConfigureNativeCameraAsTrashV107();
            ReassertPreGameplayBlackEyesV114();
            UpdateUiToolkitCapture();
            if (!UnifiedHudWorldPlaneV116.Enabled && !NativeHudCompositorV114.Enabled)
            {
                EnsureMinimapEndOfFrameCoroutine();
                EnsureVitalsEndOfFrameCoroutine();
                ForceMinimapAndVitalsAlwaysOnTop();
                EnsureAlwaysOnTopLayerAndCameras();
            }
            DebugScanForVitalsSceneWide();

            
            if (InventoryCamIsActive == true)
            {
                
                if (!_popupDisableTimerStarted)
                {
                    // Primo frame: avvia il timer
                    _popupDisableTimerStarted = true;
                    _popupDisableStartTime = Time.time;
                }
                else if (Time.time - _popupDisableStartTime >= PopupDisableDelay)
                {
                    // Skip the synthetic 'E' tap in gamepad-only mode so we never touch the
                    // Keyboard device and risk flipping the prompt icons back to keyboard glyphs.
                    if (!GamepadOnlyMode)
                    {
                        kh?.ExecutePress_E();
                    }
                }
                
                
                UsedCamera.transform.SetPositionAndRotation(StereoRender.Head.position, StereoRender.Head.rotation);
                FindInventoryCursorController();
                FindSoftwareCursor();

                if (_cursorController != null)
                {
                    // Gaze drives the real cursor directly - confirmed working (visible,
                    // aligned). Multi-select suppressed via a continuous synthetic "hold up"
                    // on the right stick for as long as the tarp is open - matches your
                    // discovery that holding UP/DOWN specifically (not left/right) avoids the
                    // rotation side-effect on exit, since it doesn't map to character turning.
                    FixInventoryCursorController();
                    UpdateCursorPositionFromGaze();
                    UpdateTarpCursorReticle();
                    // Confirmed: synthetic stick injection (both a single tap and a continuous
                    // hold) does NOT replicate what a genuine physical stick touch does here -
                    // whatever the game checks for this specific detection isn't triggered by
                    // programmatically injected input events. Manually pressing the stick
                    // (ideally UP/DOWN, which doesn't affect character facing) remains the
                    // practical solution - removed the non-functional injection code.
                    //
                    // ROOT CAUSE FOUND via item-side field dump: each LayoutItem runs Unity's
                    // BUILT-IN OnMouseEnter/OnMouseOver/OnMouseExit callbacks (confirmed via its
                    // own event backing fields), driven by Camera.main raycasting the literal
                    // desktop mouse position - entirely separate from every cursor-side
                    // component previously examined (collider, EventSystem, async task), which
                    // is why none of those attempts ever had any effect. Fix: rather than chase
                    // why Unity's own hover system misbehaves in VR, directly enforce exclusive
                    // selection ourselves every poll, using the item LayoutItemCursorSnapping
                    // already correctly tracks as the gaze target.
                    long tarpInteractionStartV116 = System.Diagnostics.Stopwatch.GetTimestamp();
                    EnforceExclusiveTarpItemHighlight();
                    RecordTarpInteractionCostV116(tarpInteractionStartV116);

                    // CONFIRMED via clean isolated test: FakeGround has ZERO effect on the
                    // vegetation clipping, even with its full biome-aware grass/decoration
                    // system (GrassDetails, AutumnDetails, BeachDetails, Cave, Bunker) intact.
                    // This mechanism is ruled out with confidence, not just inconclusive.
                    // Reverting to the stable baseline (disabled) since there's no benefit.
                    if (_FakeGround && _FakeGround.active)
                    {
                        _FakeGround.active = false;
                    }
                }
            }

            bool grabBagNowActive = _grabBagHeld != null && _grabBagHeld.gameObject.activeInHierarchy;
            UpdateGrabBagInteractionCacheLifecycleV107(grabBagNowActive);
            bool bookNowActive = _bookHeld != null && _bookHeld.gameObject.activeInHierarchy;
            bool backpackQuickSelectNowActive = _backpackQuickSelect != null && _backpackQuickSelect.gameObject.activeInHierarchy;
            bool tutorialBookNowActive = _tutorialBookHeld != null && _tutorialBookHeld.gameObject.activeInHierarchy;

            // V151: strictly read-only dual-book trace. Uses native active state, not an assumed D-pad/X sequence.
            BookletPassiveTraceV151.Tick(_bookHeld, _tutorialBookHeld, StereoRender.Head, _softwareCursor,
                (bookNowActive || tutorialBookNowActive) ? FindVirtualMouseDevice() : null,
                bookNowActive, tutorialBookNowActive);

            // V28: same renderer-only invisibility that made the tarp dramatically faster, now
            // applied to held 3D menu states where the avatar/prop can safely disappear. Use raw
            // held-object activity rather than uiCursorActive so a cursor-state transition cannot
            // flash the expensive body/held mesh back into existence for a frame.
            // V142: cooking arm wins over any briefly overlapping held-menu activation.
            bool hideHandHeldVisualsNowV28 = !grabBagNowActive && !InventoryCamIsActive && HandHeldMenuHidePlayerVisuals &&
                ((grabBagNowActive && HandHeldMenuHideDuringGrabBag) ||
                 (backpackQuickSelectNowActive && HandHeldMenuHideDuringBackpackQuickSelect) ||
                 (bookNowActive && HandHeldMenuHideDuringBooklet) ||
                 (tutorialBookNowActive && HandHeldMenuHideDuringTutorialBook));
            string hideHandHeldReasonV28 = grabBagNowActive && HandHeldMenuHideDuringGrabBag ? "GrabBag/cooking" :
                (backpackQuickSelectNowActive && HandHeldMenuHideDuringBackpackQuickSelect ? "BackpackQuickSelect" :
                (bookNowActive && HandHeldMenuHideDuringBooklet ? "Booklet" :
                (tutorialBookNowActive && HandHeldMenuHideDuringTutorialBook ? "TutorialBook" : "none")));
            UpdateHandHeldPlayerVisualSuppressionV28(hideHandHeldVisualsNowV28, hideHandHeldReasonV28);

            // V31: V27 accidentally hid the ground inventory's support surface as part of the
            // SkinnedMeshRenderer sweep, whereas V28 left the cooking tray's ordinary
            // InventoryFoodBoard Renderer alive. Hide just that board so the selectable foods
            // remain visible and interactive, but the close-up support surface no longer renders.
            UpdateGrabBagFoodBoardSuppressionV31(grabBagNowActive);

            // V11: the gaze-driven grab bag proved functionally correct, but opening it exposed
            // two independent presentation problems: the same open-world rendering load as the
            // tarp, and a stale/frozen VR Origin that leaves the animated shoulder drifting into
            // the headset. Apply the 50m menu clip and follow the native first-person camera
            // using the exact same positionOffset as ordinary gameplay. No rotation is copied;
            // HMD orientation remains fully tracked.
            UpdateGrabBagViewStateV11(grabBagNowActive);

            bool handHeldMenuActive = uiCursorActive && !InventoryCamIsActive && (grabBagNowActive || bookNowActive || backpackQuickSelectNowActive || tutorialBookNowActive);

            if (!(handHeldMenuActive && backpackQuickSelectNowActive && !grabBagNowActive && BackpackGazeV150Enabled))
                ClearBackpackGazeV150();

            // V161: booklet-only native head-gaze. Cached colliders belong to their *nearest*
            // HeldBookInteraction, so expanded submenu tabs cannot be stolen by parent tabs.
            // When inactive, immediately release all native hover state controlled by V161.
            bool bookGazeNowV161 = handHeldMenuActive && !grabBagNowActive &&
                (bookNowActive != tutorialBookNowActive) && BookletGazeV161.Enabled;
            if (!bookGazeNowV161) BookletGazeV161.Clear();

            if (handHeldMenuActive)
            {
                FindSoftwareCursor();

                if (grabBagNowActive)
                {
                    // ZER0INT V10: the campfire/grab-bag is a real 3D object held out in front of
                    // the player, much more like the tarp than the essentially-flat construction
                    // booklet. Stop reconstructing a fake VR pointer from the game's 2D
                    // VirtualMouse coordinates. Instead, head gaze raycasts the actual active
                    // GrabBagLayoutItem colliders and drives each item's own native
                    // OnMouseEnter/OnMouseOver/OnMouseExit lifecycle. The game's trigger input is
                    // left completely untouched; this experiment asks whether its own hover
                    // callbacks are sufficient to establish the real actionable selection.
                    UpdateGrabBagGazeHoverV10();
                }
                else if (backpackQuickSelectNowActive && BackpackGazeV150Enabled)
                {
                    // V150: identical interaction *principle* to the proven cooking tray:
                    // head-ray -> native LayoutItem hover callbacks -> original native action.
                    // Never write VirtualMouse coordinates or spoof snap points.
                    ClearGrabBagGazeHoverV10();
                    UpdateBackpackGazeV150();
                }
                else if (bookGazeNowV161)
                {
                    // V161: real native HeldBookInteraction hover; all input stays native.
                    // One-book-at-a-time, and no virtual-mouse position or snap writes.
                    ClearGrabBagGazeHoverV10();
                    BookletGazeV161.Tick(bookNowActive ? _bookHeld : _tutorialBookHeld,
                        StereoRender != null ? StereoRender.Head : null, _softwareCursor,
                        bookNowActive ? "BLUEPRINT" : "TUTORIAL");
                }
                else if (_softwareCursor != null)
                {
                    ClearGrabBagGazeHoverV10();
                    // With V161 disabled, construction/tutorial keep the exact V159
                    // empirical 460/460 or 1600/460 joystick-cursor fallback.
                    // Everything except the grab bag keeps the v9/original cursor behavior. In
                    // particular, the construction booklet remains a flat 2D-ish cursor problem
                    // where the original fixed-offset mapping is at least learnable and usable.
                    if (!_softwareCursor.active) _softwareCursor.active = true;
                    bool isRightHandItem = backpackQuickSelectNowActive || tutorialBookNowActive;
                    bool usesRaycastCursor = backpackQuickSelectNowActive;
                    Transform targetRoot = null;
                    float surfaceLift = usesRaycastCursor ? CampfireCursorSurfaceLift : 0f;
                    float fixedDistance = usesRaycastCursor ? 0f : (isRightHandItem ? RightHandCursorFixedDistance : BookletCursorFixedDistance);
                    float scale = (bookNowActive || tutorialBookNowActive) ? BookletCursorScale : -1f;

                    if (backpackQuickSelectNowActive)
                    {
                        UpdateBackpackQuickSelectCursorV061Style(BackpackQuickSelectCursorOffsetX, BackpackQuickSelectCursorOffsetY);
                    }
                    else if (isRightHandItem)
                    {
                        UpdateCampfireCursorFromRealMousePosition(RightHandCursorOffsetX, RightHandCursorOffsetY, surfaceLift, fixedDistance, targetRoot, scale);
                    }
                    else
                    {
                        // Construction booklet only here: retain the original 460/460 behavior.
                        UpdateCampfireCursorFromRealMousePosition(CampfireCursorOffsetX, CampfireCursorOffsetY, surfaceLift, fixedDistance, targetRoot, scale);
                    }
                }

                if (_gazeMarker != null) _gazeMarker.SetActive(false);
            }
            else
            {
                ClearGrabBagGazeHoverV10();
                ClearBackpackGazeV150();
                if (_gazeMarker != null) _gazeMarker.SetActive(false);
                if (_campfireCursorMarker != null) _campfireCursorMarker.SetActive(false);
            }
            _grabBagCursorWasActive = handHeldMenuActive;

                                        
                if (!uiCursorActive)
                {
                    // --- FIX: Detect the moment you close the inventory ---
                    if (_lastInventoriCameraIsActive == true)
                    {
                        MelonLogger.Msg("[VRPlayer] Inventory closed! Applying height safety bump to prevent falling through map.");
                        if (LocalPlayer != null)
                        {
                            // Bumps your actual physical character controller 0.5 meters upward
                            Vector3 safePos = LocalPlayer.transform.position;
                            safePos.y += 0.45f; 
                            LocalPlayer.transform.position = safePos;
                        }
                    }

                    _popupDisableTimerStarted = false;
                    _popupDisableStartTime = 0.0f;
                    
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
                    if (_lastToggledItem != null)
                    {
                        SetItemOutlinersEnabled(_lastToggledItem, false);
                        _lastToggledItem = null;
                    }
                    _lastSnapAnchorSearchItem = null;
                    _cursorStateGo = null;
                    if (_tarpCursorReticle != null && _tarpCursorReticle.activeSelf) _tarpCursorReticle.SetActive(false);
                    
                    // --- POSITIONING: Sync VR origin to game camera ---
                    // CONFIRMED via user report: the title screen's own camera has a subtle
                    // forward/backward "breathing" drift animation (likely intentional cinematic
                    // effect), and syncing every frame was dragging the player's VR viewpoint
                    // along with it, causing the view to slowly pass through the now-visible
                    // title menu. Skipping this specific continuous re-sync at the title screen
                    // only - the initial position is already set correctly when entering the
                    // scene (via SetSceneAndCamera), so this just stops it from continuing to
                    // chase the camera's ongoing drift afterward. Gameplay tracking (where this
                    // sync is essential) is untouched.
                    //
                    // DIAGNOSTIC: user reports the drift resumes as soon as they press D-pad to
                    // navigate/select a menu item - logging whenever the skip condition's inputs
                    // change, to see whether UsedSceneName or UsedCamera itself changes at that
                    // moment (which would explain the skip no longer applying) rather than
                    // guessing again.
                    bool syncSkippedForTitle = UsedSceneName == "SonsTitleScene";
                    if (syncSkippedForTitle != _lastTitleSyncSkipState || UsedCamera != _lastTitleSyncCamera)
                    {
                        _lastTitleSyncSkipState = syncSkippedForTitle;
                        _lastTitleSyncCamera = UsedCamera;
                        MelonLogger.Msg($"[VRPlayer][TitleDriftDiag] syncSkippedForTitle={syncSkippedForTitle} UsedSceneName=\"{UsedSceneName}\" UsedCamera={(UsedCamera != null ? UsedCamera.name : "NULL")}");
                    }
                    if (UsedCamera != null && Origin != null && UsedSceneName != "SonsTitleScene")
                    {
                        Vector3 basePosition = UsedCamera.transform.position;
                        // Revert your positionOffset back to standard (0.0f, 0.05f, -0.10f) so your neck feels natural
                        // V156: don't put the ordinary gameplay neck offset on cinematic
                        // camera viewpoints. Combined with V110's +0.30 m title eye lift,
                        // this lifted the player outside/above the helicopter. The authored
                        // camera origin is the center of the cinematic stereo viewpoint.
                        Vector3 finalPosition = IsNativeCinematicCameraV156
                            ? basePosition
                            : basePosition + UsedCamera.transform.TransformDirection(positionOffset);

                        // NEW: user's idea, simpler than chasing the cart's pitch (which caused
                        // motion sickness) - nudge the view further forward relative to the body
                        // specifically while driving, so a brief lag never visually manifests as
                        // "behind the shoulders". Only needed for the forward direction - backing
                        // up was never reported as a problem since nothing blocks the front view
                        // either way.
                        if (IsInGolfCart())
                        {
                            finalPosition += UsedCamera.transform.TransformDirection(Vector3.forward) * GolfCartForwardOffset;
                        }
                        // NEW: same shoulder-obstruction symptom reported for the glider - applying
                        // the identical fix.
                        if (IsInGlider())
                        {
                            finalPosition += UsedCamera.transform.TransformDirection(Vector3.forward) * GliderForwardOffset;
                        }

                        Origin.position = finalPosition;
                    }
                
                
                // Computed once per frame and reused below, instead of calling IsInGolfCart()
                // repeatedly (each call is cheap once cached, but this also lets us detect the
                // entry transition cleanly in one place).
                bool isInGolfCartNow = IsInGolfCart();
                if (isInGolfCartNow)
                {
                    if (!_wasInGolfCartLastFrame)
                    {
                        // NEW: user confirmed the view stays wherever it was facing right before
                        // entering the cart, requiring manual correction every time. One-time snap
                        // at the moment of entry: rotate the VR rig so the view immediately faces
                        // the same direction as the body, rather than continuously chasing it
                        // (which is exactly the unstable behavior that was just removed).
                        if (StereoRender != null && StereoRender.Head != null && LocalPlayer != null)
                        {
                            Vector3 headForwardFlat = StereoRender.Head.forward;
                            headForwardFlat.y = 0f;
                            Vector3 bodyForwardFlat = LocalPlayer.transform.forward;
                            bodyForwardFlat.y = 0f;
                            if (headForwardFlat.sqrMagnitude > 0.001f && bodyForwardFlat.sqrMagnitude > 0.001f)
                            {
                                headForwardFlat.Normalize();
                                bodyForwardFlat.Normalize();
                                float angleDelta = Vector3.SignedAngle(headForwardFlat, bodyForwardFlat, Vector3.up);
                                StereoRender.transform.Rotate(0, angleDelta, 0, Space.World);
                            }
                        }
                        _lastBodyYawInCart = LocalPlayer != null ? LocalPlayer.transform.eulerAngles.y : 0f;
                    }
                    else
                    {
                        // REVERTED from full quaternion tracking back to yaw-only: user confirmed
                        // the pitch-following (from the cart's genuine forward/backward tilt)
                        // made the view harder on the eyes and motion-sickness inducing. Yaw-only
                        // was already confirmed comfortable and correctly fixed the left/right
                        // following - the residual forward/backward "gets behind the shoulders"
                        // issue is instead addressed with a forward position offset while
                        // driving (see the POSITIONING block), rather than by chasing pitch.
                        if (LocalPlayer != null && StereoRender != null)
                        {
                            float currentBodyYaw = LocalPlayer.transform.eulerAngles.y;
                            float yawDelta = Mathf.DeltaAngle(_lastBodyYawInCart, currentBodyYaw);
                            if (Mathf.Abs(yawDelta) > 0.001f)
                            {
                                StereoRender.transform.Rotate(0, yawDelta, 0, Space.World);
                            }
                            _lastBodyYawInCart = currentBodyYaw;
                        }
                    }
                }

                // NEW: same "view locked to vehicle frame, must use right stick to look around"
                // symptom reported for the glider as golf cart originally had - applying the
                // identical entry-snap + yaw-chase pattern, kept as a fully separate block (own
                // "was in last frame" flag and yaw tracking) since the two vehicles can't overlap
                // but keeping the logic independent avoids any subtle cross-talk between them.
                bool isInGliderNow = IsInGlider();
                if (isInGliderNow)
                {
                    if (!_wasInGliderLastFrame)
                    {
                        if (StereoRender != null && StereoRender.Head != null && LocalPlayer != null)
                        {
                            Vector3 headForwardFlat = StereoRender.Head.forward;
                            headForwardFlat.y = 0f;
                            Vector3 bodyForwardFlat = LocalPlayer.transform.forward;
                            bodyForwardFlat.y = 0f;
                            if (headForwardFlat.sqrMagnitude > 0.001f && bodyForwardFlat.sqrMagnitude > 0.001f)
                            {
                                headForwardFlat.Normalize();
                                bodyForwardFlat.Normalize();
                                float angleDelta = Vector3.SignedAngle(headForwardFlat, bodyForwardFlat, Vector3.up);
                                StereoRender.transform.Rotate(0, angleDelta, 0, Space.World);
                            }
                        }
                        _lastBodyYawInGlider = LocalPlayer != null ? LocalPlayer.transform.eulerAngles.y : 0f;
                    }
                    else
                    {
                        if (LocalPlayer != null && StereoRender != null)
                        {
                            float currentBodyYaw = LocalPlayer.transform.eulerAngles.y;
                            float yawDelta = Mathf.DeltaAngle(_lastBodyYawInGlider, currentBodyYaw);
                            if (Mathf.Abs(yawDelta) > 0.001f)
                            {
                                StereoRender.transform.Rotate(0, yawDelta, 0, Space.World);
                            }
                            _lastBodyYawInGlider = currentBodyYaw;
                        }
                    }
                }
                _wasInGolfCartLastFrame = isInGolfCartNow;
                _wasInGliderLastFrame = isInGliderNow;
                // Shared by both skip-conditions below, so the same "physically turning your head
                // is the only way to look around" behavior applies to both vehicles identically.
                bool isInVehicleFreeLookNow = isInGolfCartNow || isInGliderNow;

                // --- SMOOTH TURN: Right thumbstick horizontal input ---
                UnityEngine.Vector2 rightStick = NativeVRInput.GetVector2("ThumbstickRight");
                // Fallback to Xbox controller if no VR thumbstick input
                if (rightStick == Vector2.zero && Gamepad.current != null)
                {
                    rightStick = Gamepad.current.rightStick.ReadValue();
                }
                // NEW: user's idea, now doable thanks to a confirmed reliable vehicle-detection
                // signal (IsInGolfCart(), see GolfCartActionInputState above) - while driving,
                // the only way to look around should be physically turning your head, the same
                // way it works in any other VR driving experience. Skipping the right-stick
                // smooth-turn's rotation effect entirely while in the cart. Extended to the
                // glider for the same reason - user wanted the same free-look behavior there too.
                if (!isInVehicleFreeLookNow)
                {
                    if (Mathf.Abs(rightStick.x) > stickThreshold)
                    {
                        // Adjust this number to make the smooth rotation faster or slower
                        float turnSpeed = 100.0f; 
                        // Calculate rotation continuously using Time.deltaTime for butter-smooth movement
                        float turnAmount = rightStick.x * turnSpeed * Time.deltaTime;
                        snapTurnOffset += turnAmount;
                        // Rotate the entire VR rig continuously
                        if (StereoRender != null)
                        {
                            StereoRender.transform.Rotate(0, turnAmount, 0, Space.World);
                        }
                    }
                    // (Optional) Kept for reference consistency, but reset flag is no longer strictly required for continuous movement
                    else if (Mathf.Abs(rightStick.x) < 0.2f)
                    {
                        snapTurnReset = true;
                    }
                }

                // --- BODY ALIGNMENT: Rotate player to match HMD forward direction ---
                if (LocalPlayer != null && StereoRender?.Head != null)
                {
                    // StereoRender.Head already includes snap-turn rotation (inherits from parent)
                    if (UsedCamera != null)
                    {
                        // Sync game camera rotation to HMD (for UI, weapon alignment, etc.)
                        // This stays active regardless of vehicle state - it's what makes
                        // physically turning your head actually look around, in or out of a
                        // vehicle.
                        UsedCamera.transform.rotation = StereoRender.Head.rotation;
                    }

                    // NEW: user's "lock to body while driving" idea. The chase-based rotation
                    // logic below was confirmed to become numerically unstable at steep look
                    // angles (see the 0.3f threshold fix), and separately, extensive testing
                    // confirmed there's no automatic vehicle-heading-to-view propagation
                    // anywhere in this system - this logic was the ONLY thing ever moving
                    // LocalPlayer's rotation, chasing head/stick direction regardless of
                    // context, including while driving. Skipping it entirely while in the cart:
                    // don't touch LocalPlayer's rotation at all, so the camera sync above
                    // (already reflecting real head rotation) is the only thing that changes
                    // your view - matching "only physically turning your head looks around"
                    // while driving. Extended to the glider for the same reason.
                    if (!isInVehicleFreeLookNow)
                    {
                        // Project HMD forward onto horizontal plane (ignore vertical look)
                        Vector3 headForward = StereoRender.Head.forward;
                        headForward.y = 0f;

                        // FIXED: 0.01f was far too small a threshold - confirmed via logged data
                        // that LocalPlayer's rotation became unstable (40-50 degree jumps between
                        // consecutive ~0.3s samples, plus spurious pitch/roll bleeding in) during
                        // extended driving, most likely from looking down at a steep angle (e.g. at
                        // the cart's controls or the road). At steep look angles the remaining
                        // horizontal vector shrinks toward zero, and normalizing a near-zero vector
                        // amplifies any tiny head jitter into a wildly different resulting direction
                        // once normalized. Raising this to 0.3f means the body stops updating its
                        // rotation cleanly once the look angle gets too steep to trust, rather than
                        // computing garbage from an increasingly noise-dominated vector - normal
                        // head pitch range is unaffected, this only cuts off well into the steep
                        // look-down/look-up zone that was never meant to drive body rotation anyway.
                        if (headForward.magnitude > 0.3f)
                        {
                            headForward.Normalize();
                            Quaternion targetRotation = Quaternion.LookRotation(headForward, Vector3.up);

                            // Smoothly rotate the player character toward HMD direction
                            LocalPlayer.transform.rotation = Quaternion.Slerp(
                                LocalPlayer.transform.rotation,
                                targetRotation,
                                Time.deltaTime * bodyRotationSpeed
                            );
                        }
                    }
                }

                // --- LOCOMOTION & RUNNING ---
                if (firstPersonCharacter != null)
                {
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
            LogTarpExitFramesV147();
        }

        private void LogTarpExitFramesV147()
        {
            if (_tarpExitTraceFramesV147 <= 0) return;
            int index = _tarpExitTraceIndexV147++;
            --_tarpExitTraceFramesV147;
            if (index != 0 && index != 1 && index != 2 && index != 4 && index != 8) return;
            float playerY = LocalPlayer != null ? LocalPlayer.transform.position.y : float.NaN;
            float cameraY = UsedCamera != null ? UsedCamera.transform.position.y : float.NaN;
            float rigY = Origin != null ? Origin.position.y : float.NaN;
            float rigYaw = Origin != null ? Origin.eulerAngles.y : float.NaN;
            float cameraYaw = UsedCamera != null ? UsedCamera.transform.eulerAngles.y : float.NaN;
            MelonLogger.Msg($"[VRPlayer][V147 TarpExit] FRAME idx={index} playerY={playerY:F3} baselineY={_tarpBasePlayerY:F3} cameraY={cameraY:F3} rigY={rigY:F3} rigYaw={rigYaw:F1} cameraYaw={cameraYaw:F1} cameraDeltaYaw={Mathf.DeltaAngle(rigYaw, cameraYaw):F1} inventory={InventoryCamIsActive} camera={(UsedCamera != null ? UsedCamera.name : "null")}");
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
            if (LocalPlayer != null)
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
            // Safety restore in case the VR rig is destroyed while a temporary menu hide is active.
            RestoreGrabBagFoodBoardV31();
            SetHandHeldPlayerVisualsHiddenV28(false, "destroy");
            SetTarpPlayerVisualsHiddenV27(false);
            ClosePauseOnSceneTransitionV145("destroy");
            BookletPassiveTraceV151.Shutdown("VRPlayer destroyed");
            BookletGazeV161.Clear();
            ClearBackpackGazeV150();
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
            // Same safety restore as OnDestroy: never strand the avatar invisible if a scene
            // transition disables the VRPlayer while any temporary menu suppression is active.
            RestoreGrabBagFoodBoardV31();
            SetHandHeldPlayerVisualsHiddenV28(false, "disable");
            SetTarpPlayerVisualsHiddenV27(false);
            ClosePauseOnSceneTransitionV145("disable");
            BookletPassiveTraceV151.Shutdown("VRPlayer disabled");
            BookletGazeV161.Clear();
            ClearBackpackGazeV150();
            MelonLogger.Msg("[VRPlayer] VRPlayer DISABLED");
        }

        /// <summary>
        /// ZER0INT V27: visually suppress the LocalPlayer while preserving the complete native
        /// rig. forceRenderingOff affects rendering only; Renderer.enabled, Animators, colliders,
        /// transforms, clothing state, crafting animation state, etc. remain owned by Sons.
        /// </summary>
        private void SetTarpPlayerVisualsHiddenV27(bool hidden)
        {
            if (hidden)
            {
                if (!TarpHidePlayerVisuals || LocalPlayer == null) return;

                // Fresh cache per tarp session so clothing/equipment changes made between
                // inventory opens are respected.
                _tarpRendererForceOffStatesV27 = new Il2CppSystem.Collections.Generic.Dictionary<Renderer, bool>();
                int count = 0;
                // Character body/clothing in Sons is skinned. Restrict the first pass to
                // SkinnedMeshRenderer so we do not accidentally suppress a static inventory/tarp
                // prop that might also happen to live somewhere under LocalPlayer.
                foreach (var r in LocalPlayer.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (r == null) continue;
                    if (ShouldKeepTarpSurfaceVisibleV32(r)) continue;
                    try
                    {
                        _tarpRendererForceOffStatesV27[r] = r.forceRenderingOff;
                        r.forceRenderingOff = true;
                        count++;
                    }
                    catch { }
                }

                _tarpPlayerVisualsHiddenV27 = count > 0;
                _nextTarpRendererRescanV27 = Time.unscaledTime + 0.5f;
                MelonLogger.Msg($"[VRPlayer][TarpInvisibleV27] hidden={_tarpPlayerVisualsHiddenV27} skinnedRenderers={count} extraHeadLiftEffective={GetEffectiveTarpHeadExtraYOffsetV27():F2}m");
                return;
            }

            if (_tarpRendererForceOffStatesV27 != null)
            {
                int count = 0;
                foreach (var kvp in _tarpRendererForceOffStatesV27)
                {
                    if (kvp.Key == null) continue;
                    try
                    {
                        kvp.Key.forceRenderingOff = kvp.Value;
                        count++;
                    }
                    catch { }
                }
                MelonLogger.Msg($"[VRPlayer][TarpInvisibleV27] hidden=false restoredRenderers={count}");
            }

            _tarpRendererForceOffStatesV27 = null;
            _tarpPlayerVisualsHiddenV27 = false;
        }

        /// <summary>
        /// Re-scan occasionally while inventory is open. This catches a clothing/equipment
        /// renderer instantiated after the tarp opened without interfering with Sons' own
        /// Renderer.enabled state.
        /// </summary>
        private void MaintainTarpPlayerVisualSuppressionV27()
        {
            if (!TarpHidePlayerVisuals || !InventoryCamIsActive || LocalPlayer == null) return;
            if (!_tarpPlayerVisualsHiddenV27)
            {
                SetTarpPlayerVisualsHiddenV27(true);
                return;
            }
            if (Time.unscaledTime < _nextTarpRendererRescanV27) return;
            _nextTarpRendererRescanV27 = Time.unscaledTime + 0.5f;

            if (_tarpRendererForceOffStatesV27 == null)
                _tarpRendererForceOffStatesV27 = new Il2CppSystem.Collections.Generic.Dictionary<Renderer, bool>();

            foreach (var r in LocalPlayer.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r == null) continue;
                if (ShouldKeepTarpSurfaceVisibleV32(r)) continue;
                try
                {
                    if (!_tarpRendererForceOffStatesV27.ContainsKey(r))
                        _tarpRendererForceOffStatesV27[r] = r.forceRenderingOff;
                    r.forceRenderingOff = true;
                }
                catch { }
            }
        }


        /// <summary>
        /// ZER0INT V32: V27's broad SkinnedMeshRenderer suppression also caught the active
        /// ground-inventory support rig. For this causal check, leave the already-active
        /// LocalPlayer/InventorySystem/Visuals/InventoryWrap subtree completely untouched while
        /// still suppressing the player body/clothing and every other skinned renderer. This
        /// does NOT SetActive/enable anything: inactive children remain inactive; we merely stop
        /// forcing the currently active inventory-wrap renderers off.
        /// </summary>
        private bool ShouldKeepTarpSurfaceVisibleV32(SkinnedMeshRenderer r)
        {
            if (r == null || r.gameObject == null) return false;
            try
            {
                string path = GetHierarchyPath(r.transform);
                return path.StartsWith("LocalPlayer/InventorySystem/Visuals/InventoryWrap/");
            }
            catch { return false; }
        }

        /// <summary>
        /// ZER0INT V28: toggle renderer-only invisibility for held 3D menus. This is intentionally
        /// a separate cache from the tarp cache: InventoryCamIsActive and these held-menu states
        /// are mutually exclusive, so each mode restores exactly the forceRenderingOff values it
        /// observed on entry without cross-contaminating the known-good V27 tarp behavior.
        /// </summary>
        private void SetHandHeldPlayerVisualsHiddenV28(bool hidden, string reason)
        {
            if (hidden)
            {
                if (!HandHeldMenuHidePlayerVisuals || LocalPlayer == null) return;
                if (_handHeldPlayerVisualsHiddenV28)
                {
                    _handHeldHideReasonV28 = reason ?? _handHeldHideReasonV28;
                    return;
                }

                _handHeldRendererForceOffStatesV28 = new Il2CppSystem.Collections.Generic.Dictionary<Renderer, bool>();
                int count = 0;
                foreach (var r in LocalPlayer.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (r == null) continue;
                    try
                    {
                        _handHeldRendererForceOffStatesV28[r] = r.forceRenderingOff;
                        r.forceRenderingOff = true;
                        count++;
                    }
                    catch { }
                }

                _handHeldPlayerVisualsHiddenV28 = count > 0;
                _handHeldHideReasonV28 = reason ?? "held-menu";
                _nextHandHeldRendererRescanV28 = Time.unscaledTime + 0.5f;
                MelonLogger.Msg($"[VRPlayer][HandHeldInvisibleV28] hidden={_handHeldPlayerVisualsHiddenV28} reason={_handHeldHideReasonV28} skinnedRenderers={count}");
                return;
            }

            if (_handHeldRendererForceOffStatesV28 != null)
            {
                int count = 0;
                foreach (var kvp in _handHeldRendererForceOffStatesV28)
                {
                    if (kvp.Key == null) continue;
                    try
                    {
                        kvp.Key.forceRenderingOff = kvp.Value;
                        count++;
                    }
                    catch { }
                }
                MelonLogger.Msg($"[VRPlayer][HandHeldInvisibleV28] hidden=false reason={_handHeldHideReasonV28} restoredRenderers={count}");
            }

            _handHeldRendererForceOffStatesV28 = null;
            _handHeldPlayerVisualsHiddenV28 = false;
            _handHeldHideReasonV28 = "none";
        }

        /// <summary>
        /// V28 per-frame state transition + 0.5s rescan. The rescan mirrors V27 and catches a
        /// jacket/arm/held skinned mesh that Sons instantiates or swaps after the menu opened.
        /// </summary>
        private void UpdateHandHeldPlayerVisualSuppressionV28(bool shouldHide, string reason)
        {
            if (!shouldHide)
            {
                if (_handHeldPlayerVisualsHiddenV28 || _handHeldRendererForceOffStatesV28 != null)
                    SetHandHeldPlayerVisualsHiddenV28(false, reason);
                return;
            }

            if (!_handHeldPlayerVisualsHiddenV28)
            {
                SetHandHeldPlayerVisualsHiddenV28(true, reason);
                return;
            }

            _handHeldHideReasonV28 = reason ?? _handHeldHideReasonV28;
            if (LocalPlayer == null || Time.unscaledTime < _nextHandHeldRendererRescanV28) return;
            _nextHandHeldRendererRescanV28 = Time.unscaledTime + 0.5f;

            if (_handHeldRendererForceOffStatesV28 == null)
                _handHeldRendererForceOffStatesV28 = new Il2CppSystem.Collections.Generic.Dictionary<Renderer, bool>();

            foreach (var r in LocalPlayer.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r == null) continue;
                try
                {
                    if (!_handHeldRendererForceOffStatesV28.ContainsKey(r))
                        _handHeldRendererForceOffStatesV28[r] = r.forceRenderingOff;
                    r.forceRenderingOff = true;
                }
                catch { }
            }
        }

        /// <summary>
        /// ZER0INT V31: hide only the cooking GrabBag's structural InventoryFoodBoard renderer.
        /// V29 showed this is a large ordinary Renderer (not a SkinnedMeshRenderer), so V28's
        /// player-visual suppression never touched it. Ingredients and CookingCog live on sibling
        /// objects and are deliberately left visible/interactive.
        /// </summary>
        private void UpdateGrabBagFoodBoardSuppressionV31(bool grabBagActive)
        {
            if (!GrabBagHideInventoryFoodBoard || !grabBagActive || _grabBagHeld == null)
            {
                RestoreGrabBagFoodBoardV31();
                return;
            }

            if (_grabBagFoodBoardRendererV31 == null)
            {
                if (Time.unscaledTime < _nextGrabBagFoodBoardSearchV31) return;
                _nextGrabBagFoodBoardSearchV31 = Time.unscaledTime + 0.5f;

                try
                {
                    foreach (var r in _grabBagHeld.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null || r.gameObject == null) continue;
                        if (!r.gameObject.name.Equals("InventoryFoodBoard")) continue;

                        _grabBagFoodBoardRendererV31 = r;
                        _grabBagFoodBoardOriginalForceOffV31 = r.forceRenderingOff;
                        break;
                    }
                }
                catch { }
            }

            if (_grabBagFoodBoardRendererV31 == null) return;

            try
            {
                _grabBagFoodBoardRendererV31.forceRenderingOff = true;
                if (!_grabBagFoodBoardHiddenV31)
                {
                    _grabBagFoodBoardHiddenV31 = true;
                    MelonLogger.Msg($"[VRPlayer][GrabBagBoardV31] hidden=true target={GetHierarchyPath(_grabBagFoodBoardRendererV31.transform)}");
                }
            }
            catch { }
        }

        private void RestoreGrabBagFoodBoardV31()
        {
            if (_grabBagFoodBoardRendererV31 != null)
            {
                try
                {
                    _grabBagFoodBoardRendererV31.forceRenderingOff = _grabBagFoodBoardOriginalForceOffV31;
                }
                catch { }
            }

            if (_grabBagFoodBoardHiddenV31)
                MelonLogger.Msg("[VRPlayer][GrabBagBoardV31] hidden=false restored InventoryFoodBoard");

            _grabBagFoodBoardRendererV31 = null;
            _grabBagFoodBoardOriginalForceOffV31 = false;
            _grabBagFoodBoardHiddenV31 = false;
            _nextGrabBagFoodBoardSearchV31 = 0f;
        }

        private float GetEffectiveTarpHeadExtraYOffsetV27()
        {
            return (_tarpPlayerVisualsHiddenV27 && TarpRemoveExtraHeadLiftWhenPlayerHidden)
                ? 0f
                : TarpHeadExtraYOffsetMeters;
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




        private System.Collections.Generic.HashSet<string> _lastActiveKeywordObjects = new System.Collections.Generic.HashSet<string>();
        private float _lastKeywordScanTime = 0f;
        private static readonly string[] _uiScanKeywords =
        {
            "cook", "campfire", "fire", "craft", "recipe", "wheel", "radial",
            "quickselect", "quick_select", "select", "cursor", "highlight",
            "pause", "save", "menu", "resume", "quit",
            "options", "settings", "graphics", "gameplay", "controls", "audio",
            "multiplayer", "multi_player", "lobby", "waiting", "host", "join", "server", "session",
            "loading", "load_screen", "loadscreen", "progress", "spinner",
            "confirm", "areyousure",
            "tooltip", "iteminfo", "itemdetail", "durability", "equip", "weaponinfo", "infopanel", "strength",
            "actionicon", "worldicon", "playername", "nametag", "playertag", "playerlabel",
            "notification", "toast", "popup", "hungry", "thirsty", "cold", "ammo", "pickup",
            "hudmessage", "infobubble", "alert", "warning"
        };

        private float _lastGrabBagItemScanTime = 0f;
        private System.Collections.Generic.Dictionary<string, Vector3> _lastGrabBagItemScales
            = new System.Collections.Generic.Dictionary<string, Vector3>();
        private System.Collections.Generic.HashSet<string> _lastGrabBagItemActive
            = new System.Collections.Generic.HashSet<string>();

        /// <summary>
        /// Scoped to just the grab bag's own (small) item hierarchy - logs any child whose
        /// active state OR scale changes noticeably, since selection highlighting is commonly
        /// done via a scale-up "pulse" rather than a simple active/inactive toggle. This should
        /// reveal what's actually changing as you scroll with the stick, without the lag of
        /// scanning the whole scene like the earlier full diagnostic did.
        /// </summary>
        private System.Collections.Generic.HashSet<string> _lastPlayerChildActive
            = new System.Collections.Generic.HashSet<string>();
        private float _lastPlayerScanTime = 0f;

        /// <summary>
        /// The guessed RightHandItemLocators path didn't exist - rather than guess again, scan
        /// the player's ENTIRE hierarchy for anything that activates/deactivates. Scoped to
        /// LocalPlayer only (not the whole scene, unlike the earlier expensive keyword scan),
        /// and uses the native GetComponentsInChildren rather than the custom recursive walker
        /// that crashed before, so this should be safe. Will be noisy on the very first pass
        /// (everything currently active gets logged once) but quiet afterward - only genuine
        /// changes get logged from then on.
        /// </summary>
        private float _lastDesyncCheckTime = 0f;
        private float _lastDesyncDiagnosticLogTime = 0f;
        private const float DesyncCheckDistance = 3f; // meters - lowered in case the real gap is smaller than first assumed

        /// <summary>
        /// Safety net for the multiplayer "view stuck below map" bug: normally the VR rig's
        /// Origin follows UsedCamera's live position every frame, but that sync is skipped
        /// while a menu/cursor state is considered active. If multiplayer's join/lobby sequence
        /// holds that state open while your character gets teleported to its real spawn point,
        /// the sync never catches up on its own. This runs unconditionally (regardless of menu
        /// state) and force-corrects if the VR view ends up far from where the real game camera
        /// actually is - the actual gameplay position (what other players see) is always
        /// trusted as correct.
        /// </summary>
        private float _lastCameraScanTime = 0f;

        /// <summary>
        /// Diagnostic: MainCameraFP is never being detected in multiplayer despite the desktop
        /// view being correct - meaning there IS a real, correctly-tracking camera somewhere,
        /// it may just not be named "MainCameraFP" in multiplayer. Log every active camera's
        /// name and position so we can identify the real one directly instead of guessing.
        /// </summary>
        private void DebugScanActiveCameras()
        {
            if (!DebugScanCanvasesOnly) return;
            if (Time.unscaledTime - _lastCameraScanTime < 2f) return;
            _lastCameraScanTime = Time.unscaledTime;

            Camera[] allCameras = UnityEngine.Object.FindObjectsOfType<Camera>();
            foreach (var cam in allCameras)
            {
                // WIDENED: was skipping disabled cameras entirely - if a minimap-rendering camera
                // exists but is only briefly activated periodically (a common perf optimization -
                // refresh the map texture occasionally rather than every frame), the scanner would
                // miss it entirely between those windows. Logging disabled cameras too now.
                if (cam == null) continue;
                MelonLogger.Msg($"[VRPlayer][CameraScan] {cam.name} enabled={cam.isActiveAndEnabled} at {cam.transform.position} (parent: {(cam.transform.parent != null ? cam.transform.parent.name : "none")}) fieldOfView={cam.fieldOfView} cullingMask={cam.cullingMask} stereoTargetEye={cam.stereoTargetEye} targetTexture={(cam.targetTexture != null ? cam.targetTexture.name : "null")} rect={cam.rect}");
            }

            // NEW: testing whether the binoculars zoom camera is hijacking Camera.main - the
            // tarp's native OnMouseEnter/Exit detection explicitly depends on it (per an existing
            // comment elsewhere in this file), and disabling the zoom camera when unused didn't
            // fix the reported tarp regression, so logging Camera.main directly to either confirm
            // or rule this theory out with certainty instead of guessing again.
            Camera mainCam = Camera.main;
            MelonLogger.Msg($"[VRPlayer][CameraScan] Camera.main = {(mainCam != null ? mainCam.name : "null")} (isOurZoomCamera={mainCam != null && _binocularsZoomCamera != null && mainCam.GetInstanceID() == _binocularsZoomCamera.GetInstanceID()})");
        }

        private float _handleUpdateFirstRunTime = 0f;
        private bool _handleUpdateFirstRunRecorded = false;
        private const float DesyncGracePeriodSeconds = 5f;

        private float _lastCameraPlayerDiagTime = 0f;
        private Vector3 _lastLoggedUsedCameraPos = Vector3.zero;

        private void DebugCompareCameraToPlayer()
        {
            if (UsedCamera == null) return;
            if (Time.unscaledTime - _lastCameraPlayerDiagTime < 3f) return;
            _lastCameraPlayerDiagTime = Time.unscaledTime;

            Vector3 camPos = UsedCamera.transform.position;
            float camMovedSinceLastCheck = Vector3.Distance(camPos, _lastLoggedUsedCameraPos);
            _lastLoggedUsedCameraPos = camPos;

            string playerPos = LocalPlayer != null ? LocalPlayer.transform.position.ToString() : "N/A (LocalPlayer null)";
            float distToPlayer = LocalPlayer != null ? Vector3.Distance(camPos, LocalPlayer.transform.position) : -1f;

            MelonLogger.Msg($"[VRPlayer][CamPlayerDiag] UsedCamera={camPos} (moved {camMovedSinceLastCheck:F2}m in 3s) LocalPlayer={playerPos} distance-between={distToPlayer:F1}m Origin={Origin?.position}");
        }

        public static bool EnableVehicleCameraDiagnostic = false; // pausing the remaining flicker investigation for now - turned back off, revisit later

        /// <summary>
        /// CONFIRMED via first round: UsedCamera stays "MainCameraFP" the whole time driving,
        /// no separate vehicle camera object. User also reports the camera view doesn't rotate
        /// to match steering direction (has to manually catch up with the right stick) - this
        /// and the flicker are very likely the same root cause: our per-frame
        /// "UsedCamera.transform.rotation = StereoRender.Head.rotation" (see the POSITIONING
        /// block in HandleUpdate) unconditionally overwrites world rotation every frame,
        /// fighting whatever the vehicle's own steering system is doing to the camera or one of
        /// its ancestors. Logging the camera's parent chain and LocalPlayer's own rotation to
        /// find exactly which transform the vehicle actually drives, so the fix can target the
        /// right one instead of guessing.
        /// </summary>
        private float _lastVehicleScanTime = 0f;

        /// <summary>
        /// Both the rotation-instability fix and the user's "lock view to body while driving"
        /// idea need the same missing prerequisite: a reliable way to detect "currently in a
        /// vehicle." Searching every component (by TYPE name, not GameObject name) under
        /// LocalPlayer for anything vehicle-related, dumping its properties to find a usable
        /// boolean/reference field - same technique that found other components earlier in this
        /// project. One-time scan, not per-frame.
        /// </summary>
        private Transform _golfCartInputStateTransform;
        private float _lastGolfCartSearchTime = 0f;
        private bool _wasInGolfCartLastFrame = false;
        public static float GolfCartForwardOffset = 0.4f; // untested starting guess - nudges the view forward relative to the body while driving, adjust to taste
        private float _lastBodyYawInCart = 0f;

        // NEW: same shoulder-obstruction and head-locked-view symptoms user reported for the
        // glider as were originally fixed for the golf cart - applying the same proven fix
        // pattern. Unlike golf cart (where "GolfCartActionInputState" was already a confirmed
        // exact name), the glider's equivalent object name isn't known yet - using a keyword
        // search instead of guessing a specific name, so this doesn't depend on getting an exact
        // string right on the first try.
        // FIXED: the first version searched LocalPlayer's ENTIRE hierarchy, which matched a false
        // positive - "BlueprintHangGliderLauncherHeldLocator", a static held-item attachment point
        // deep under the hand rig, active from the moment the game loads regardless of gliding
        // state at all. Confirmed via user report: right-stick was disabled immediately at load,
        // before ever touching a glider. Narrowing the search to the same "SpecialActions"
        // container golf cart's own real detection object lives in - held-item locators live under
        // the hand-rig hierarchy, a completely separate branch, so this should no longer catch them.
        private float _lastGliderSearchTime = 0f;
        private bool _wasInGliderLastFrame = false;
        public static float GliderForwardOffset = 0f; // confirmed working at 0f - shoulders aren't visible even without any forward push while gliding
        private float _lastBodyYawInGlider = 0f;

        private Transform _gliderControllerCandidate;
        private bool _gliderCandidatesFound = false;

        private Type _playerHangGliderActionType = null;
        private bool _playerHangGliderActionTypeSearched = false;
        private object _typedGliderAction = null;
        private PropertyInfo _isGlidingProperty = null;

        private void ResolvePlayerHangGliderActionType()
        {
            if (_playerHangGliderActionTypeSearched) return;
            _playerHangGliderActionTypeSearched = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type found = null;
                try { found = Array.Find(asm.GetTypes(), x => x.Name == "PlayerHangGliderAction"); }
                catch { continue; }
                if (found != null) { _playerHangGliderActionType = found; break; }
            }
            if (_playerHangGliderActionType != null)
            {
                _isGlidingProperty = _playerHangGliderActionType.GetProperty("IsGliding", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
        }

        /// <summary>
        /// CONFIRMED via a log spanning a full hold-drop-pickup-jump-glide-land cycle: the real
        /// component on "PlayerGliderController" is PlayerHangGliderAction, and its `IsGliding`
        /// property cleanly toggles False (holding/landed) -> True (actual flight, confirmed by
        /// timeInAir climbing steadily and heading/pitch/tilt genuinely changing) -> False again
        /// the instant landing happens (timeInAir stops, GliderWasDroppedOnLastUse=True). This is
        /// the reliable signal, the same role GolfCartActionInputState plays for the golf cart -
        /// unlike the GameObject-level checks tried before, which never toggled at all.
        /// </summary>
        private bool IsInGlider()
        {
            if (LocalPlayer == null) return false;
            if (!_gliderCandidatesFound)
            {
                if (Time.time - _lastGliderSearchTime < 1f) return false;
                _lastGliderSearchTime = Time.time;

                Transform specialActions = null;
                foreach (Transform t in LocalPlayer.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "SpecialActions")
                    {
                        specialActions = t;
                        break;
                    }
                }
                if (specialActions == null) return false;

                foreach (Transform t in specialActions.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "PlayerGliderController") _gliderControllerCandidate = t;
                }
                if (_gliderControllerCandidate == null) return false;
                _gliderCandidatesFound = true;
            }

            if (_typedGliderAction == null)
            {
                ResolvePlayerHangGliderActionType();
                if (_playerHangGliderActionType == null || _isGlidingProperty == null) return false;

                Component comp = _gliderControllerCandidate.GetComponent(_playerHangGliderActionType.Name);
                if (comp == null)
                {
                    // Fallback: type name lookup via GetComponent(string) can miss under IL2CPP
                    // interop - scan all components on this object for one whose native class
                    // matches, same technique used to identify it in the first place.
                    foreach (var c in _gliderControllerCandidate.GetComponents<Component>())
                    {
                        if (c == null) continue;
                        try
                        {
                            IntPtr classPtr = IL2CPP.il2cpp_object_get_class(c.Pointer);
                            string nativeTypeName = classPtr != IntPtr.Zero ? IL2CPP.il2cpp_class_get_name_(classPtr) : null;
                            if (nativeTypeName == "PlayerHangGliderAction") { comp = c; break; }
                        }
                        catch { }
                    }
                }
                if (comp == null) return false;

                try
                {
                    var castMethod = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("Cast")?.MakeGenericMethod(_playerHangGliderActionType);
                    _typedGliderAction = castMethod?.Invoke(comp, null);
                }
                catch (Exception e)
                {
                    MelonLogger.Msg($"[VRPlayer][GliderDiag] Cast<PlayerHangGliderAction>() failed: {e.Message}");
                    return false;
                }
                if (_typedGliderAction == null) return false;
            }

            try
            {
                object value = _isGlidingProperty.GetValue(_typedGliderAction);
                return value is bool b && b;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// CONFIRMED via logged data: LocalPlayer/ControllerObjects/SpecialActions/
        /// GolfCartActionInputState's activeSelf cleanly toggles True exactly while actually
        /// driving (verified: single clean False->True->False transition pair across an entire
        /// drive session, no noise/flicker) - unlike GolfCartDriverAction/GolfCartPassengerAction,
        /// which stay active regardless of actual driving state and turned out not to be useful
        /// signals. This is the vehicle-detection signal that gates the body-rotation-chase
        /// logic and right-stick smooth-turn while driving (see the POSITIONING block in
        /// HandleUpdate).
        /// </summary>
        private float _lastPickupGuiDumpTime = 0f;

        /// <summary>
        /// CONFIRMED via UIScan: DoubleBedStructure(Clone)/SleepAndSaveInteractLeft/
        /// ItemTemplatePickupGui/SleepInteractionElement/SaveInteractionElement - a generic,
        /// reusable-sounding template hosting both the sleep and save prompt icons as siblings.
        /// The user's reported "A and B icons overlap directly on top of each other" symptom is
        /// very plausibly a similar bug to the item info panel we already fixed (uninitialized
        /// scale/position math never properly applied for VR) - dumping every instance's full
        /// structure (components, transform, each child's own local position/scale) to check.
        /// </summary>
        private void DebugDumpItemTemplatePickupGui()
        {
            if (!DebugLogCanvasChanges) return;
            if (Time.time - _lastPickupGuiDumpTime < 2f) return;
            _lastPickupGuiDumpTime = Time.time;

            foreach (Transform t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t == null || t.name != "ItemTemplatePickupGui") continue;
                if (!t.gameObject.activeInHierarchy) continue;

                MelonLogger.Msg($"[VRPlayer][PickupGuiDiag] {GetHierarchyPath(t)}: pos={t.position} localPos={t.localPosition} localScale={t.localScale} lossyScale={t.lossyScale}");

                foreach (var comp in t.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    MelonLogger.Msg($"[VRPlayer][PickupGuiDiag]   component: {comp.GetType().Name} (ToString={comp})");
                }

                for (int ci = 0; ci < t.childCount; ci++)
                {
                    Transform child = t.GetChild(ci);
                    MelonLogger.Msg($"[VRPlayer][PickupGuiDiag]   child: {child.name} active={child.gameObject.activeSelf} localPos={child.localPosition} localScale={child.localScale}");
                    // NEW: the parent (ItemTemplatePickupGui) turned out to have nothing but a
                    // bare Transform - the actual rendering mechanism (Canvas, sprite, billboard
                    // script, etc.) must live on the child itself. Going one level deeper.
                    foreach (var childComp in child.GetComponents<Component>())
                    {
                        if (childComp == null) continue;
                        MelonLogger.Msg($"[VRPlayer][PickupGuiDiag]     child component: {childComp.GetType().Name} (ToString={childComp})");
                    }

                    // NEW: never actually logged this level before - only the fix code touched
                    // it, so there was no direct confirmation of whether the fix even ran on the
                    // right object, or whether the position genuinely changed. Logging both
                    // local AND world position, since if the fix IS applying but has zero visual
                    // effect, that points to the icon's screen position being computed/cached
                    // elsewhere rather than read from this transform every frame.
                    // FIXED: was `foreach (Transform grandchild in child)` - Transform's
                    // non-generic IEnumerable enumeration is unreliable under IL2CPP interop
                    // (Current comes back as a boxed Il2CppSystem.Object that the generated cast
                    // to Transform can throw on) - this was the actual InvalidCastException
                    // crashing this function (and FixOverlappingActionPrompts below) on every
                    // poll, silently, since Il2CppInterop swallows the exception at the
                    // trampoline boundary. Indexed GetChild() avoids that enumerator entirely.
                    for (int gi = 0; gi < child.childCount; gi++)
                    {
                        Transform grandchild = child.GetChild(gi);
                        MelonLogger.Msg($"[VRPlayer][PickupGuiDiag]     grandchild: {grandchild.name} active={grandchild.gameObject.activeSelf} localPos={grandchild.localPosition} worldPos={grandchild.position}");

                        // CONFIRMED by user: nudging this grandchild's localPosition (via
                        // FixOverlappingActionPrompts) had ZERO visual effect on the overlap even
                        // though the position data itself changed correctly (verified via this
                        // same log). That rules out Transform.localPosition as the thing driving
                        // on-screen placement for these prompts. Digging into the FULL subtree
                        // under each grandchild now (not just one level) to find whatever actually
                        // renders it - Canvas/RectTransform/billboard script/anything with
                        // "screen" or "anchor" in its type name are the likely suspects. Logging
                        // both GetType().FullName AND ToString() since generic "Component" type
                        // resolution has hidden the real class before, and ToString() sometimes
                        // reveals it when GetType() doesn't (known IL2CPP interop limitation).
                        foreach (var deepComp in grandchild.GetComponentsInChildren<Component>(true))
                        {
                            if (deepComp == null) continue;
                            // NEW: GetType()/ToString() only ever resolved to generic
                            // UnityEngine.Component here - the same "IL2CPP interop can't resolve
                            // the real managed type" wall hit before with PlayerInteractStimuli.
                            // This time going straight to the native IL2CPP class system instead
                            // of managed reflection: il2cpp_object_get_class gets the object's
                            // real native Il2CppClass*, and il2cpp_class_get_name_ reads its true
                            // name directly from the IL2CPP runtime, bypassing whatever generic
                            // wrapper type the managed side fell back to.
                            string nativeTypeName;
                            try
                            {
                                IntPtr classPtr = IL2CPP.il2cpp_object_get_class(deepComp.Pointer);
                                nativeTypeName = classPtr != IntPtr.Zero ? IL2CPP.il2cpp_class_get_name_(classPtr) : "<null class>";
                            }
                            catch (Exception e)
                            {
                                nativeTypeName = $"<lookup failed: {e.Message}>";
                            }
                            MelonLogger.Msg($"[VRPlayer][PickupGuiDiag]       [{grandchild.name}] deep component: managedType={deepComp.GetType().FullName} nativeType={nativeTypeName} (ToString={deepComp})");

                            // NEW: nativeType lookup broke through and identified the real class
                            // as "LinkUiElement" - name strongly suggests this is exactly the
                            // world-position-to-screen-anchor mechanism we've been looking for.
                            // Managed reflection on `deepComp` itself is useless here (it's typed
                            // as generic Component, so GetType().GetProperties() would only show
                            // Component's own members, not LinkUiElement's). Instead: find the
                            // REAL generated wrapper Type for "LinkUiElement" by scanning all
                            // loaded assemblies by simple name (no namespace guess needed, so this
                            // can't fail to compile even if we don't know where it lives), then use
                            // Il2CppObjectBase.Cast<T>() via reflection (MakeGenericMethod) to get
                            // a properly-typed instance and dump ITS real properties/methods - same
                            // GetProperties()/GetMethods() technique that worked for InteractStimuli.
                            if (nativeTypeName == "LinkUiElement")
                            {
                                DumpLinkUiElementDetails(deepComp, grandchild.name);
                            }
                        }
                    }
                }
            }
        }

        private Type _linkUiElementType = null; // cached once resolved - PDM (Possibly Dead Method) if never triggers a poll near an active prompt
        private bool _linkUiElementTypeSearched = false;

        private void DumpLinkUiElementDetails(Component comp, string ownerName)
        {
            try
            {
                ResolveLinkUiElementType();
                if (_linkUiElementType == null) return;

                var castMethod = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("Cast")?.MakeGenericMethod(_linkUiElementType);
                object typed = castMethod?.Invoke(comp, null);
                if (typed == null)
                {
                    MelonLogger.Msg($"[VRPlayer][LinkUiElementDiag] [{ownerName}] Cast<LinkUiElement>() returned null");
                    return;
                }

                var sb = new System.Text.StringBuilder();
                sb.Append($"[VRPlayer][LinkUiElementDiag] [{ownerName}] LinkUiElement properties: ");
                foreach (var prop in _linkUiElementType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (prop.GetIndexParameters().Length > 0 || !prop.CanRead) continue;
                    try
                    {
                        var value = prop.GetValue(typed);
                        sb.Append($"{prop.Name}={(value != null ? value.ToString() : "null")} | ");
                    }
                    catch { }
                }
                MelonLogger.Msg(sb.ToString());

                var fieldsSb = new System.Text.StringBuilder();
                fieldsSb.Append($"[VRPlayer][LinkUiElementDiag] [{ownerName}] LinkUiElement fields: ");
                foreach (var field in _linkUiElementType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var value = field.GetValue(typed);
                        fieldsSb.Append($"{field.Name}={(value != null ? value.ToString() : "null")} | ");
                    }
                    catch { }
                }
                MelonLogger.Msg(fieldsSb.ToString());

                var methodsSb = new System.Text.StringBuilder();
                methodsSb.Append($"[VRPlayer][LinkUiElementDiag] [{ownerName}] LinkUiElement methods: ");
                foreach (var method in _linkUiElementType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (method.IsSpecialName) continue; // skip property getters/setters, already covered above
                    methodsSb.Append($"{method.Name}({method.GetParameters().Length}) | ");
                }
                MelonLogger.Msg(methodsSb.ToString());

                // NEW: _uiGameObject field showed null in the property dump - suspicious, since
                // that's presumably the actual pooled screen UI object this component is meant to
                // control (SetWorldSpaceOffset had zero visual effect even at a full 1-meter
                // offset, so maybe nothing is reading the field it sets). Calling the live getter
                // instead of trusting the cached field, in case it's populated on demand rather
                // than kept current. If it resolves to a real GameObject, dumping its own
                // Transform/RectTransform/ancestor-Canvas directly - THAT may be the actual
                // on-screen-position source, separate from anything on LinkUiElement itself.
                try
                {
                    var getUiGameObjectMethod = _linkUiElementType.GetMethod("GetUiGameObject");
                    object uiGameObjectResult = getUiGameObjectMethod?.Invoke(typed, null);
                    if (uiGameObjectResult == null)
                    {
                        MelonLogger.Msg($"[VRPlayer][LinkUiElementDiag] [{ownerName}] GetUiGameObject() returned null");
                    }
                    else
                    {
                        MelonLogger.Msg($"[VRPlayer][LinkUiElementDiag] [{ownerName}] GetUiGameObject() returned: {uiGameObjectResult.GetType().FullName} (ToString={uiGameObjectResult})");
                        if (uiGameObjectResult is GameObject go)
                        {
                            Transform uiTransform = go.transform;
                            MelonLogger.Msg($"[VRPlayer][LinkUiElementDiag] [{ownerName}] UI GameObject '{GetHierarchyPath(uiTransform)}' pos={uiTransform.position} localPos={uiTransform.localPosition}");
                            var rt = go.GetComponent<RectTransform>();
                            if (rt != null)
                            {
                                MelonLogger.Msg($"[VRPlayer][LinkUiElementDiag] [{ownerName}] RectTransform anchoredPosition={rt.anchoredPosition} anchorMin={rt.anchorMin} anchorMax={rt.anchorMax} sizeDelta={rt.sizeDelta}");
                            }
                            var canvas = go.GetComponentInParent<Canvas>();
                            if (canvas != null)
                            {
                                MelonLogger.Msg($"[VRPlayer][LinkUiElementDiag] [{ownerName}] Ancestor Canvas '{canvas.name}' renderMode={canvas.renderMode}");
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Msg($"[VRPlayer][LinkUiElementDiag] [{ownerName}] GetUiGameObject dump failed: {e.Message}");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Msg($"[VRPlayer][LinkUiElementDiag] [{ownerName}] dump failed: {e.Message}");
            }
        }

        private float _lastPickupGuiFixTime = 0f;
        public static float NestedActionPromptSeparation = 0.08f; // meters - how far to nudge a nested action icon (e.g. Save) away from its parent (e.g. Sleep) so they no longer overlap

        /// <summary>
        /// SUPERSEDED - two prior theories, both disproven by direct user test:
        /// 1) ORIGINAL: assumed the two prompts sat at identical zero Transform.localPosition.
        ///    Disproven - SaveInteractionElement already had a baked-in (0.20,0,0) local offset.
        /// 2) REVISED: nudged whichever grandchild WAS at zero localPosition to (0,-0.08,0).
        ///    Ran cleanly (confirmed via log) but user confirmed ZERO visual change - Transform
        ///    position is not what drives on-screen placement for these prompts at all.
        /// BREAKTHROUGH via native class lookup + reflection dump: both grandchildren are really
        /// `Il2CppSons.Gui.Input.LinkUiElement` components (name hidden behind a generic
        /// UnityEngine.Component wrapper under plain managed reflection). Their OWN
        /// `_worldSpaceOffset` field - completely separate from Transform.localPosition - is
        /// IDENTICAL for both (0.00, 0.10, 0.00), which is a much stronger candidate for what
        /// actually drives the screen anchor (this class links a world point to a pooled
        /// screen-space UI element, per its uiElementId/RequestUiElement/ShowElement API and
        /// _maxDistance/_updateMethodType fields - none of that is Transform-based). NEW FIX:
        /// instead of touching Transform, find each active LinkUiElement under an
        /// ItemTemplatePickupGui/SleepInteractionElement-style parent, and give every one past the
        /// first a distinctly different _worldSpaceOffset via its own SetWorldSpaceOffset() method
        /// (found via the same runtime type-search + Cast<T> technique used in
        /// DumpLinkUiElementDetails, so no compile-time namespace guess). Uses a much larger step
        /// (0.3f) than the previous 0.08f attempt, in case the earlier failure was partly just too
        /// small a separation to read on screen at typical interaction distance, not only a
        /// wrong-property problem.
        /// </summary>
        private void FixOverlappingActionPrompts()
        {
            if (Time.time - _lastPickupGuiFixTime < 1f) return;
            _lastPickupGuiFixTime = Time.time;

            ResolveLinkUiElementType();
            if (_linkUiElementType == null) return;
            var castMethod = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("Cast")?.MakeGenericMethod(_linkUiElementType);
            var setOffsetMethod = _linkUiElementType.GetMethod("SetWorldSpaceOffset");
            if (castMethod == null || setOffsetMethod == null)
            {
                MelonLogger.Msg($"[VRPlayer][PickupGuiFix] castMethod or SetWorldSpaceOffset not found - castMethod={castMethod != null} setOffsetMethod={setOffsetMethod != null}");
                return;
            }

            foreach (Transform t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t == null || t.name != "ItemTemplatePickupGui") continue;
                if (!t.gameObject.activeInHierarchy) continue;

                for (int ci = 0; ci < t.childCount; ci++)
                {
                    Transform child = t.GetChild(ci);
                    if (child == null || !child.gameObject.activeSelf) continue;

                    int activeLinkIndex = 0;
                    for (int gi = 0; gi < child.childCount; gi++)
                    {
                        Transform grandchild = child.GetChild(gi);
                        if (grandchild == null || !grandchild.gameObject.activeSelf) continue;

                        foreach (var comp in grandchild.GetComponents<Component>())
                        {
                            if (comp == null) continue;
                            string nativeTypeName;
                            try
                            {
                                IntPtr classPtr = IL2CPP.il2cpp_object_get_class(comp.Pointer);
                                nativeTypeName = classPtr != IntPtr.Zero ? IL2CPP.il2cpp_class_get_name_(classPtr) : "";
                            }
                            catch { continue; }
                            if (nativeTypeName != "LinkUiElement") continue;

                            if (activeLinkIndex > 0)
                            {
                                try
                                {
                                    object typed = castMethod.Invoke(comp, null);
                                    // FIXED: this was accidentally still using the old
                                    // NestedActionPromptSeparation tunable (0.08f default), making
                                    // the actual applied offset only 0.18 total - nearly the same
                                    // tiny magnitude as the already-failed Transform.localPosition
                                    // attempt, and not a fair test of "was 0.08 just too small to
                                    // see". Using a deliberately large, unmissable 1.0f step per
                                    // index this time so the result is unambiguous either way.
                                    Vector3 newOffset = new Vector3(0f, 0.1f + activeLinkIndex * 1.0f, 0f);
                                    setOffsetMethod.Invoke(typed, new object[] { newOffset });
                                    MelonLogger.Msg($"[VRPlayer][PickupGuiFix] SetWorldSpaceOffset({newOffset}) on {GetHierarchyPath(grandchild)} (index={activeLinkIndex})");
                                }
                                catch (Exception e)
                                {
                                    MelonLogger.Msg($"[VRPlayer][PickupGuiFix] SetWorldSpaceOffset call failed on {GetHierarchyPath(grandchild)}: {e.Message}");
                                }
                            }
                            activeLinkIndex++;
                        }
                    }
                }
            }
        }

        private void ResolveLinkUiElementType()
        {
            if (_linkUiElementTypeSearched) return;
            _linkUiElementTypeSearched = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type found = null;
                try { found = Array.Find(asm.GetTypes(), x => x.Name == "LinkUiElement"); }
                catch { continue; }
                if (found != null) { _linkUiElementType = found; break; }
            }
            MelonLogger.Msg($"[VRPlayer][LinkUiElementDiag] Type search result: {(_linkUiElementType != null ? _linkUiElementType.AssemblyQualifiedName : "NOT FOUND in any loaded assembly")}");
        }

        private float _lastCutscenePromptDumpTime = 0f;

        /// <summary>
        /// BREAKTHROUGH via HudScan: the Y-skip prompt is confirmed real - it's
        /// "XboxNorthButton" under ScreenSpaceCanvas/CutscenePrompts/SkipCutscene/CutscenePrompt/
        /// AnimationRoot, on the SAME canvas whose main HUD elements already render fine in VR.
        /// But the earlier keyword-filtered dump (DebugDumpHudPromptOverlap) never caught it -
        /// "cutscene"/"skip" weren't in that keyword list - so its actual RectTransform/anchor
        /// data and real world position have never been captured. THEORY: this prompt is likely
        /// anchored for a bottom-of-screen flat layout (typical subtitle/skip-prompt placement);
        /// once ScreenSpaceCanvas gets forcibly recentered in front of the head every frame
        /// (UpdateCanvasPositionForVR), that anchor offset could land outside the headset's
        /// actual field of view - the exact same family of bug already confirmed and fixed once
        /// for vitals/minimap (a canvas-unit offset that was technically "there" but effectively
        /// invisible until repositioned). Dumping full RectTransform + computed world
        /// position/distance/facing relative to the real VR head, for every CutscenePrompts
        /// descendant, to test this directly instead of guessing further.
        /// </summary>
        private void DebugDumpCutscenePromptDetail()
        {
            if (!DebugLogCanvasChanges) return;
            if (Time.time - _lastCutscenePromptDumpTime < 0.5f) return;
            _lastCutscenePromptDumpTime = Time.time;
            if (_screenSpaceCanvas == null) return;

            Transform cutscenePrompts = _screenSpaceCanvas.transform.Find("CutscenePrompts");
            if (cutscenePrompts == null || !cutscenePrompts.gameObject.activeInHierarchy) return;

            Transform[] all = cutscenePrompts.GetComponentsInChildren<Transform>(true);
            foreach (var t in all)
            {
                if (t == null) continue;
                var rt = t.GetComponent<RectTransform>();
                if (rt == null) continue; // only care about the actual laid-out UI elements

                string line = $"[VRPlayer][CutscenePromptDiag] {GetHierarchyPath(t)} : activeSelf={t.gameObject.activeSelf} activeInHierarchy={t.gameObject.activeInHierarchy} anchoredPosition={rt.anchoredPosition} anchorMin={rt.anchorMin} anchorMax={rt.anchorMax} pivot={rt.pivot} sizeDelta={rt.sizeDelta} worldPos={rt.position} lossyScale={rt.lossyScale} localScale={rt.localScale}";

                // NEW: "AnimationRoot" strongly suggests a native fade/scale animation clip
                // drives this element's visibility independent of anything the mod touches - the
                // object stays logically active the whole time (confirmed via HudScan: no
                // deactivation event ever fires) yet visually flickers in sync with cutscene
                // timing, which points to alpha/scale being animated rather than the object being
                // toggled. Checking for CanvasGroup (alpha) and any Animation/Animator component
                // directly, plus its current clip/playing state, instead of guessing further.
                var cg = t.GetComponent<CanvasGroup>();
                if (cg != null)
                {
                    line += $" CanvasGroup.alpha={cg.alpha}";
                }
                var anim = t.GetComponent<Animation>();
                if (anim != null)
                {
                    string clipName = anim.clip != null ? anim.clip.name : "none";
                    line += $" Animation(clip={clipName} isPlaying={anim.isPlaying})";
                }
                var animator = t.GetComponent<Animator>();
                if (animator != null)
                {
                    line += $" Animator(enabled={animator.enabled})";

                    // NEW: alpha is confirmed to genuinely fade 0->1 via a native
                    // Animator-driven CanvasGroup - this is intended slide/fade behavior, not a
                    // bug. The open question is WHY it only ever triggers once (during the
                    // helicopter descent) instead of re-triggering on every later Y press like it
                    // does in flat mode. Dumping the live Animator state name + every parameter's
                    // current value, so we can see exactly what condition gates a re-trigger and
                    // whether our synthetic input is actually reaching it.
                    try
                    {
                        var stateInfo = animator.GetCurrentAnimatorStateInfo(0);
                        line += $" AnimatorState(hash={stateInfo.fullPathHash} normalizedTime={stateInfo.normalizedTime:F2} loop={stateInfo.loop})";
                        foreach (var p in animator.parameters)
                        {
                            string pv;
                            switch (p.type)
                            {
                                case AnimatorControllerParameterType.Bool: pv = animator.GetBool(p.name).ToString(); break;
                                case AnimatorControllerParameterType.Float: pv = animator.GetFloat(p.name).ToString("F2"); break;
                                case AnimatorControllerParameterType.Int: pv = animator.GetInteger(p.name).ToString(); break;
                                case AnimatorControllerParameterType.Trigger: pv = "<trigger>"; break;
                                default: pv = "?"; break;
                            }
                            line += $" [{p.name}({p.type})={pv}]";
                        }
                    }
                    catch (Exception e)
                    {
                        line += $" AnimatorState(<failed: {e.Message}>)";
                    }
                }

                if (StereoRender != null && StereoRender.Head != null)
                {
                    Vector3 toObj = rt.position - StereoRender.Head.position;
                    float dist = toObj.magnitude;
                    float dot = dist > 0.0001f ? Vector3.Dot(StereoRender.Head.forward, toObj.normalized) : 0f;
                    line += $" distanceFromHead={dist:F3}m dotWithHeadForward={dot:F3}";
                }

                var graphic = t.GetComponent<UnityEngine.UI.Graphic>();
                if (graphic != null)
                {
                    line += $" graphicType={graphic.GetType().Name} color={graphic.color} enabled={graphic.enabled}";
                }

                MelonLogger.Msg(line);
            }

            // Also log the canvas's own live transform, since every child's worldPos above is
            // downstream of it - if the canvas itself is somewhere unexpected, everything under
            // it will be too.
            MelonLogger.Msg($"[VRPlayer][CutscenePromptDiag] ScreenSpaceCanvas worldPos={_screenSpaceCanvas.transform.position} localScale={_screenSpaceCanvas.transform.localScale} rotation={_screenSpaceCanvas.transform.rotation.eulerAngles}");
        }

        private float _lastHudPromptOverlapDumpTime = 0f;

        /// <summary>
        /// PIVOT: everything touched on LinkUiElement (world Transform, then a full 1-meter
        /// SetWorldSpaceOffset) has had zero visual effect, and GetUiGameObject() always returns
        /// null on it - meaning it's just a request/anchor object, not the actual rendered thing.
        /// CRITICAL NEW FACT from user: without the mod at all, these two icons render correctly
        /// separated - so this is the mod breaking something that natively works, not a
        /// game-native bug. The mod's own ConvertCanvasToWorldSpace forcibly and continuously
        /// converts PlayerStandin/PlayerUI/ScreenSpaceCanvas (confirmed via existing code at the
        /// top of HandleUpdate) to RenderMode.WorldSpace during normal gameplay - and that canvas
        /// is confirmed (via the earlier ItemInfoPanel fix) to contain a "ToggleableHUD" child,
        /// exactly the kind of container that would host action-prompt icons. Theory: the actual
        /// rendered "screen.save"/"screen.sleepDisabled" icons live somewhere under
        /// ScreenSpaceCanvas/ToggleableHUD as ordinary RectTransform-based UI, and forcing their
        /// ancestor canvas to WorldSpace breaks whatever screen-relative layout math normally
        /// keeps multiple simultaneous prompts apart - which would explain why nothing done on the
        /// LinkUiElement request-side object ever had any visual effect. This function scans that
        /// subtree directly for anything name-matching (not the LinkUiElement side, the actual
        /// rendered UI side) to find the real objects and their real RectTransform data, so a fix
        /// can be aimed at the right layer instead of continuing to guess.
        /// </summary>
        private void DebugDumpHudPromptOverlap()
        {
            if (!DebugLogCanvasChanges) return;
            if (Time.time - _lastHudPromptOverlapDumpTime < 0.3f) return;
            _lastHudPromptOverlapDumpTime = Time.time;

            if (_screenSpaceCanvas == null) return;
            MelonLogger.Msg($"[VRPlayer][HudPromptDiag] ScreenSpaceCanvas renderMode={_screenSpaceCanvas.renderMode} worldCamera={(_screenSpaceCanvas.worldCamera != null ? _screenSpaceCanvas.worldCamera.name : "null")}");

            string[] keywords = { "save", "sleep", "prompt", "actionicon", "interact" };
            foreach (Transform t in _screenSpaceCanvas.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                string lname = t.name.ToLowerInvariant();
                bool matches = false;
                foreach (var kw in keywords) { if (lname.Contains(kw)) { matches = true; break; } }
                if (!matches) continue;

                var rt = t.GetComponent<RectTransform>();
                if (rt != null)
                {
                    // NEW: adding GetInstanceID() to directly test a new theory - these objects
                    // are named "(Clone)", meaning runtime-instantiated from a prefab, and
                    // LinkUiElement exposes RequestUiElement()/RemoveElement()/ShowElement()
                    // methods suggesting a request/refresh lifecycle. Our anchoredPosition fix
                    // logs success every time it runs, yet a dump moments later always shows it
                    // reverted to (0,0) - that could mean something resets THIS SAME object every
                    // frame (matching the tarp/item-info-panel pattern seen before), OR it could
                    // mean the object is being destroyed and a FRESH clone (with its prefab's
                    // default anchoredPosition=(0,0)) instantiated in its place periodically -
                    // a completely different problem needing a different fix. Comparing instance
                    // IDs across consecutive dumps in the log will tell us which.
                    MelonLogger.Msg($"[VRPlayer][HudPromptDiag] {GetHierarchyPath(t)} : instanceId={t.gameObject.GetInstanceID()} anchoredPosition={rt.anchoredPosition} anchorMin={rt.anchorMin} anchorMax={rt.anchorMax} pivot={rt.pivot} sizeDelta={rt.sizeDelta} worldPos={rt.position}");

                    // NEW: end-of-frame timing + ForceUpdateCanvases still may not fix this if the
                    // actual visible pixels aren't drawn from THIS RectTransform's own geometry at
                    // all. This game's vitals HUD is confirmed RenderTexture-composited
                    // (PlayerVitalUiRenderTextureGroup/VitalsRenderTexture, seen in an earlier
                    // scene dump) - if these prompt icons use the same technique, editing this
                    // RectTransform may be editing a "source" object whose position never reaches
                    // the actual displayed texture. Dumping every rendering-related component found
                    // on this object directly (Image/RawImage/CanvasRenderer/MeshRenderer/Camera)
                    // to test that theory directly instead of guessing further.
                    foreach (var rc in t.GetComponents<Component>())
                    {
                        if (rc == null) continue;
                        string rcNativeType;
                        try
                        {
                            IntPtr rcClassPtr = IL2CPP.il2cpp_object_get_class(rc.Pointer);
                            rcNativeType = rcClassPtr != IntPtr.Zero ? IL2CPP.il2cpp_class_get_name_(rcClassPtr) : "";
                        }
                        catch { rcNativeType = "<lookup failed>"; }
                        MelonLogger.Msg($"[VRPlayer][HudPromptDiag]   [{t.name}] rendering component: managedType={rc.GetType().Name} nativeType={rcNativeType}");
                    }
                }
                else
                {
                    MelonLogger.Msg($"[VRPlayer][HudPromptDiag] {GetHierarchyPath(t)} : no RectTransform, worldPos={t.position} localPos={t.localPosition}");
                }
            }
        }

        /// <summary>
        /// zer0int v9: enlarge only the small interaction/status subtrees, not the root HUD canvas.
        /// WorldScreenSpaceElements contains the floating controller/action prompts (e.g. the A
        /// glyph near a fire). ToggleableHUD/MessageList contains the small status/message family
        /// (e.g. thirst notifications). V86 restores these to native x1.00 after the earlier x1.75 enlargement while preserving the existing
        /// world-space HUD distance and avoids v7's loading-screen regression.
        /// </summary>
        private void UpdateSmallHudSubtreeScale()
        {
            if (_screenSpaceCanvas == null || _screenSpaceCanvas.renderMode != RenderMode.WorldSpace) return;
            // Do not touch title/loading UI. Only apply once a real LocalPlayer exists in gameplay.
            if (LocalPlayer == null) return;

            Transform promptTarget = _screenSpaceCanvas.transform.Find("WorldScreenSpaceElements");
            if (promptTarget != null)
            {
                if (_smallHudPromptScaleTarget != promptTarget)
                {
                    _smallHudPromptScaleTarget = promptTarget;
                    _smallHudPromptBaseScale = promptTarget.localScale;
                    MelonLogger.Msg($"[VRPlayer][SmallHudScale] WorldScreenSpaceElements base={_smallHudPromptBaseScale}; x{SmallHudSubtreeScaleMultiplier:F2}.");
                }
                Vector3 desired = _smallHudPromptBaseScale * SmallHudSubtreeScaleMultiplier;
                if ((promptTarget.localScale - desired).sqrMagnitude > 1e-10f) promptTarget.localScale = desired;
            }

            Transform messageTarget = _screenSpaceCanvas.transform.Find("ToggleableHUD/MessageList");
            if (messageTarget != null)
            {
                if (_smallHudMessageScaleTarget != messageTarget)
                {
                    _smallHudMessageScaleTarget = messageTarget;
                    _smallHudMessageBaseScale = messageTarget.localScale;
                    MelonLogger.Msg($"[VRPlayer][SmallHudScale] MessageList base={_smallHudMessageBaseScale}; x{SmallHudSubtreeScaleMultiplier:F2}.");
                }
                Vector3 desired = _smallHudMessageBaseScale * SmallHudSubtreeScaleMultiplier;
                if ((messageTarget.localScale - desired).sqrMagnitude > 1e-10f) messageTarget.localScale = desired;
            }
        }

        /// <summary>
        /// ZER0INT V24: enlarge only the cooking-specific recipe overlay.  Il2CppInterop
        /// generated wrappers expose serialized Unity fields as properties, so resolve
        /// HudGui._cookingRecipeInfoView reflectively and cache its original local scale.
        /// Re-apply every LateUpdate because native UI animation/layout code is allowed to
        /// rewrite transforms while the cooking menu opens/closes.
        /// </summary>
        private void UpdateCookingRecipeOverlayScaleV24()
        {
            if (LocalPlayer == null) return;

            try
            {
                if (!_hudGuiTypeSearchedV24)
                {
                    _hudGuiTypeSearchedV24 = true;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type[] types;
                        try { types = asm.GetTypes(); } catch { continue; }
                        foreach (var t in types)
                        {
                            if (t.Name == "HudGui")
                            {
                                _hudGuiTypeV24 = t;
                                break;
                            }
                        }
                        if (_hudGuiTypeV24 != null) break;
                    }

                    if (_hudGuiTypeV24 != null)
                    {
                        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                        _hudCookingRecipeInfoViewPropV24 = _hudGuiTypeV24.GetProperty("_cookingRecipeInfoView", flags);
                        _hudCookingRecipeInfoViewFieldV24 = _hudGuiTypeV24.GetField("_cookingRecipeInfoView", flags);
                        MelonLogger.Msg($"[VRPlayer][CookingRecipeScaleV24] HudGui={_hudGuiTypeV24.FullName} member property={(_hudCookingRecipeInfoViewPropV24 != null)} field={(_hudCookingRecipeInfoViewFieldV24 != null)} targetMultiplier=x{CookingRecipeOverlayScaleMultiplier:F2}");
                    }
                }

                if (_hudGuiTypeV24 == null) return;

                if (_cookingRecipeInfoRtV24 == null && Time.unscaledTime >= _nextCookingRecipeResolveTimeV24)
                {
                    _nextCookingRecipeResolveTimeV24 = Time.unscaledTime + 1.0f;
                    var il2cppType = Il2CppInterop.Runtime.Il2CppType.From(_hudGuiTypeV24);
                    foreach (var obj in UnityEngine.Object.FindObjectsOfType(il2cppType))
                    {
                        var comp = obj.TryCast<Component>();
                        if (comp == null || comp.gameObject == null) continue;

                        object typed = CastToType(comp, _hudGuiTypeV24);
                        if (typed == null) continue;

                        object value = null;
                        try
                        {
                            if (_hudCookingRecipeInfoViewPropV24 != null)
                                value = _hudCookingRecipeInfoViewPropV24.GetValue(typed);
                            else if (_hudCookingRecipeInfoViewFieldV24 != null)
                                value = _hudCookingRecipeInfoViewFieldV24.GetValue(typed);
                        }
                        catch { value = null; }

                        var rt = value as RectTransform;
                        if (rt == null)
                        {
                            var valueComp = value as Component;
                            if (valueComp != null) rt = valueComp.GetComponent<RectTransform>();
                        }
                        if (rt == null) continue;

                        int id = rt.GetInstanceID();
                        if (_cookingRecipeInfoInstanceIdV24 != id)
                        {
                            _cookingRecipeInfoRtV24 = rt;
                            _cookingRecipeInfoInstanceIdV24 = id;
                            _cookingRecipeInfoBaseScaleV24 = rt.localScale;
                            _cookingRecipeScaleLoggedV24 = false;
                        }
                        break;
                    }
                }

                if (_cookingRecipeInfoRtV24 == null) return;

                // V169A: no-op at x1; native UI can legitimately animate its scale.
                Vector3 desired = _cookingRecipeInfoBaseScaleV24 * CookingRecipeOverlayScaleMultiplier;
                if (Mathf.Abs(CookingRecipeOverlayScaleMultiplier - 1f) > 0.0001f &&
                    _cookingRecipeInfoBaseScaleV24.sqrMagnitude >= 1e-8f &&
                    (_cookingRecipeInfoRtV24.localScale - desired).sqrMagnitude > 1e-10f)
                    _cookingRecipeInfoRtV24.localScale = desired;

                if (!_cookingRecipeScaleLoggedV24)
                {
                    _cookingRecipeScaleLoggedV24 = true;
                    MelonLogger.Msg($"[VRPlayer][CookingRecipeScaleV24] target={GetHierarchyPath(_cookingRecipeInfoRtV24)} baseScale={_cookingRecipeInfoBaseScaleV24} desired={desired} rect={_cookingRecipeInfoRtV24.rect.size} pivot={_cookingRecipeInfoRtV24.pivot}");
                }
            }
            catch (Exception e)
            {
                if (!_cookingRecipeScaleLoggedV24)
                {
                    _cookingRecipeScaleLoggedV24 = true;
                    MelonLogger.Msg($"[VRPlayer][CookingRecipeScaleV24] failed: {e.GetBaseException().Message}");
                }
            }
        }

        // BUMPED: 60f is confirmed to now genuinely stick (anchoredPosition holds steady across
        // many consecutive polls, per log - the LayoutGroup disable fix worked), but the resulting
        // worldPos difference between two staggered elements was barely measurable - this canvas's
        // effective scale is apparently tiny (a likely side effect of the same forced WorldSpace
        // conversion breaking everything else in this saga), so 60 units in anchoredPosition space
        // translates to a visually negligible real-world offset. Using a deliberately huge,
        // unambiguous value this time (same "rule it in or out cleanly" approach that worked for
        // diagnosing the earlier LinkUiElement offset) rather than guessing a moderate increment.
        public static float WorldScreenSpaceElementSeparation = 5000f; // canvas units - how far apart to stagger overlapping WorldScreenSpaceElements clones


        /// <summary>
        /// ZER0INT V25: scale the three additional HudGui informational roots requested by the
        /// user.  HudGui itself remains the source of truth; we resolve its serialized members
        /// reflectively so this stays isolated from the broad ScreenSpaceCanvas.
        ///
        /// Like the proven V24 cooking panel fix, each target's original local scale is cached
        /// once per instantiated RectTransform and the configured scale is re-applied during LateUpdate.
        /// This intentionally does NOT touch InventoryItemInfoView, whose special world-space
        /// conversion has native animation/layout behavior that breaks under repeated scale writes.
        /// </summary>
        private void UpdateAdditionalHudInfoScalesV25()
        {
            if (LocalPlayer == null || _hudGuiTypeV24 == null) return;

            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                if (!_hudAdditionalMembersResolvedV25)
                {
                    _hudAdditionalMembersResolvedV25 = true;
                    _hudAvailableRecipesViewPropV25 = _hudGuiTypeV24.GetProperty("_availableRecipesView", flags);
                    _hudAvailableRecipesViewFieldV25 = _hudGuiTypeV24.GetField("_availableRecipesView", flags);
                    _hudToolTipInfoViewPropV25 = _hudGuiTypeV24.GetProperty("_toolTipInfoView", flags);
                    _hudToolTipInfoViewFieldV25 = _hudGuiTypeV24.GetField("_toolTipInfoView", flags);
                    _hudQuickSelectItemInfoViewPropV25 = _hudGuiTypeV24.GetProperty("_quickSelectItemInfoView", flags);
                    _hudQuickSelectItemInfoViewFieldV25 = _hudGuiTypeV24.GetField("_quickSelectItemInfoView", flags);

                    MelonLogger.Msg(
                        $"[VRPlayer][HudInfoScaleV25] HudGui={_hudGuiTypeV24.FullName} " +
                        $"availableRecipes(prop={(_hudAvailableRecipesViewPropV25 != null)},field={(_hudAvailableRecipesViewFieldV25 != null)}) " +
                        $"toolTip(prop={(_hudToolTipInfoViewPropV25 != null)},field={(_hudToolTipInfoViewFieldV25 != null)}) " +
                        $"quickSelect(prop={(_hudQuickSelectItemInfoViewPropV25 != null)},field={(_hudQuickSelectItemInfoViewFieldV25 != null)})");
                }

                // All three references live on the same HudGui instance. They normally exist even
                // while their visual roots are inactive, but retry once per second in case HudGui
                // initializes one of them lazily after scene load/respawn.
                if ((_availableRecipesRtV25 == null || _toolTipInfoRtV25 == null || _quickSelectItemInfoRtV25 == null)
                    && Time.unscaledTime >= _nextAdditionalHudResolveTimeV25)
                {
                    _nextAdditionalHudResolveTimeV25 = Time.unscaledTime + 1.0f;
                    var il2cppType = Il2CppInterop.Runtime.Il2CppType.From(_hudGuiTypeV24);
                    foreach (var obj in UnityEngine.Object.FindObjectsOfType(il2cppType))
                    {
                        var hudComp = obj.TryCast<Component>();
                        if (hudComp == null || hudComp.gameObject == null) continue;

                        object typed = CastToType(hudComp, _hudGuiTypeV24);
                        if (typed == null) continue;

                        if (_availableRecipesRtV25 == null)
                        {
                            object value = null;
                            try
                            {
                                if (_hudAvailableRecipesViewPropV25 != null) value = _hudAvailableRecipesViewPropV25.GetValue(typed);
                                else if (_hudAvailableRecipesViewFieldV25 != null) value = _hudAvailableRecipesViewFieldV25.GetValue(typed);
                            }
                            catch { value = null; }
                            var rt = HudMemberToRectTransformV25(value);
                            if (rt != null)
                            {
                                int id = rt.GetInstanceID();
                                if (_availableRecipesInstanceIdV25 != id)
                                {
                                    _availableRecipesRtV25 = rt;
                                    _availableRecipesInstanceIdV25 = id;
                                    _availableRecipesBaseScaleV25 = rt.localScale;
                                    _availableRecipesScaleLoggedV25 = false;
                                }
                            }
                        }

                        if (_toolTipInfoRtV25 == null)
                        {
                            object value = null;
                            try
                            {
                                if (_hudToolTipInfoViewPropV25 != null) value = _hudToolTipInfoViewPropV25.GetValue(typed);
                                else if (_hudToolTipInfoViewFieldV25 != null) value = _hudToolTipInfoViewFieldV25.GetValue(typed);
                            }
                            catch { value = null; }
                            var rt = HudMemberToRectTransformV25(value);
                            if (rt != null)
                            {
                                int id = rt.GetInstanceID();
                                if (_toolTipInfoInstanceIdV25 != id)
                                {
                                    _toolTipInfoRtV25 = rt;
                                    _toolTipInfoInstanceIdV25 = id;
                                    _toolTipInfoBaseScaleV25 = rt.localScale;
                                    _toolTipInfoScaleLoggedV25 = false;
                                }
                            }
                        }

                        if (_quickSelectItemInfoRtV25 == null)
                        {
                            object value = null;
                            try
                            {
                                if (_hudQuickSelectItemInfoViewPropV25 != null) value = _hudQuickSelectItemInfoViewPropV25.GetValue(typed);
                                else if (_hudQuickSelectItemInfoViewFieldV25 != null) value = _hudQuickSelectItemInfoViewFieldV25.GetValue(typed);
                            }
                            catch { value = null; }
                            var rt = HudMemberToRectTransformV25(value);
                            if (rt != null)
                            {
                                int id = rt.GetInstanceID();
                                if (_quickSelectItemInfoInstanceIdV25 != id)
                                {
                                    _quickSelectItemInfoRtV25 = rt;
                                    _quickSelectItemInfoInstanceIdV25 = id;
                                    _quickSelectItemInfoBaseScaleV25 = rt.localScale;
                                    _quickSelectItemInfoScaleLoggedV25 = false;
                                }
                            }
                        }
                        break;
                    }
                }

                ApplyHudRectScaleV25(_availableRecipesRtV25, _availableRecipesBaseScaleV25,
                    CraftingRecipeOverlayScaleMultiplier, "AvailableRecipes", ref _availableRecipesScaleLoggedV25);
                ApplyHudRectScaleV25(_toolTipInfoRtV25, _toolTipInfoBaseScaleV25,
                    ToolTipInfoScaleMultiplier, "ToolTipInfo", ref _toolTipInfoScaleLoggedV25);
                ApplyHudRectScaleV25(_quickSelectItemInfoRtV25, _quickSelectItemInfoBaseScaleV25,
                    QuickSelectItemInfoScaleMultiplier, "QuickSelectItemInfo", ref _quickSelectItemInfoScaleLoggedV25);
            }
            catch (Exception e)
            {
                MelonLogger.Msg($"[VRPlayer][HudInfoScaleV25] failed: {e.GetBaseException().Message}");
            }
        }

        private static RectTransform HudMemberToRectTransformV25(object value)
        {
            if (value == null) return null;

            // Direct RectTransform / Component / GameObject members (e.g. _availableRecipesView).
            var rt = value as RectTransform;
            if (rt != null) return rt;
            var comp = value as Component;
            if (comp != null) return comp.GetComponent<RectTransform>();
            var go = value as GameObject;
            if (go != null) return go.GetComponent<RectTransform>();

            // ToolTipInfoView and QuickSelectItemInfoView are lightweight serialized view
            // objects rather than MonoBehaviours. Their generated IL2CPP wrappers expose a
            // `_root` member that owns the actual UI GameObject. Resolve that root instead of
            // incorrectly assuming the view object itself has a Transform.
            try
            {
                Type valueType = value.GetType();
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                object root = null;
                var rootProp = valueType.GetProperty("_root", flags);
                if (rootProp != null) root = rootProp.GetValue(value);
                else
                {
                    var rootField = valueType.GetField("_root", flags);
                    if (rootField != null) root = rootField.GetValue(value);
                }

                var rootRt = root as RectTransform;
                if (rootRt != null) return rootRt;
                var rootComp = root as Component;
                if (rootComp != null) return rootComp.GetComponent<RectTransform>();
                var rootGo = root as GameObject;
                if (rootGo != null) return rootGo.GetComponent<RectTransform>();
            }
            catch { }

            return null;
        }

        private void ApplyHudRectScaleV25(RectTransform rt, Vector3 baseScale, float multiplier, string label, ref bool logged)
        {
            if (rt == null) return;
            // V169A: at x1, native pop-in animation / layout MUST own localScale.
            // Previous code cached scale while hidden (often zero), then wrote it
            // forever, making tooltips and item information invisible.
            if (Mathf.Abs(multiplier - 1f) < 0.0001f) return;
            if (baseScale.sqrMagnitude < 1e-8f) return; // avoid zero-scale poison
            Vector3 desired = baseScale * multiplier;
            if ((rt.localScale - desired).sqrMagnitude > 1e-10f) rt.localScale = desired;
            if (!logged)
            {
                logged = true;
                MelonLogger.Msg($"[VRPlayer][HudInfoScaleV25] {label} target={GetHierarchyPath(rt)} baseScale={baseScale} desired={desired} rect={rt.rect.size} pivot={rt.pivot}");
            }
        }

        /// <summary>
        /// V14/V15: the circular character/status ring seen around the minimap is NOT a child of
        /// PlayerMiniMap.  It is ToggleableHUD/PlayerVitalsUi on the main 0.50m HUD canvas.
        /// V15 additionally re-applies the scale at WaitForEndOfFrame, because the native UI can
        /// overwrite the normal Update-time root scale later in the same frame.  The end-of-frame
        /// positioning code also compensates for its non-centered pivot so scaling does not fling
        /// its visual center away.
        /// </summary>
        private void UpdateVitalsUiScaleV14()
        {
            // V16: keep resolving this object because other legacy diagnostics/layer handling use
            // it, but STOP scaling it.  V14 and V15 proved conclusively that multiplying this
            // RectTransform by 2.5 has zero effect on the actually visible ring.  The visible
            // presentation is a separate RenderTexture display targeted by
            // UpdateVitalsRenderTextureV16().
            if (_playerVitalsUiTransf == null && _screenSpaceCanvas != null)
            {
                _playerVitalsUiTransf = _screenSpaceCanvas.transform.Find("ToggleableHUD/PlayerVitalsUi");
            }
            if (_playerVitalsUiTransf == null) return;

            if (_vitalsScaleTargetV14 != _playerVitalsUiTransf)
            {
                _vitalsScaleTargetV14 = _playerVitalsUiTransf;
                _vitalsBaseScaleV14 = _playerVitalsUiTransf.localScale;
                _vitalsScaleLoggedV14 = false;
            }

            // Explicitly leave the old controller/source UI at its native scale.
            _playerVitalsUiTransf.localScale = _vitalsBaseScaleV14;
        }

        /// <summary>
        /// BREAKTHROUGH via DebugDumpHudPromptOverlap: found the REAL rendered objects at
        /// PlayerStandin/PlayerUI/ScreenSpaceCanvas/WorldScreenSpaceElements/ScreenSpace{Sleep,Save}Element(Clone)
        /// - a previously-undiscovered container, entirely distinct from every LinkUiElement-side
        /// object poked earlier in this investigation. Both clones confirmed via log to sit at
        /// IDENTICAL anchoredPosition=(0,0) and IDENTICAL worldPos to the centimeter - this is the
        /// actual overlap. Theory: in vanilla (non-VR) play, some layout mechanism spaces multiple
        /// simultaneous clones apart in real 2D screen-pixel space; forcing this canvas to
        /// WorldSpace (done unconditionally elsewhere in this file, confirmed pre-existing/working
        /// code, not something from this session) likely breaks whatever that mechanism depends on
        /// (probably real Screen.width/height), so clones default to stacking at (0,0). Fix: for
        /// each active WorldScreenSpaceElements child beyond the first sharing a near-identical
        /// world position with an earlier one this poll, apply a distinct anchoredPosition offset
        /// directly - these are plain UnityEngine.RectTransform, no reflection/IL2CPP-interop
        /// workaround needed here, unlike everything touched so far in this investigation.
        ///
        /// FOLLOW-UP FIX: confirmed via log the offset WAS being applied correctly
        /// (anchoredPosition=(60,0) logged right after a fix call) but a diagnostic dump well
        /// under a second later showed it already back at (0,0) - something native resets this
        /// every frame (same category of fight seen before with the tarp's MeshOutliner
        /// OnMouseEnter/Exit re-enabling other items every frame, and the item-info panel's own
        /// pop-in animation continuously resetting its scale). The 1-second throttle on the WRITE
        /// itself (not just the WorldScreenSpaceElements Transform lookup, which is fine to cache
        /// once) meant our fix was losing that fight almost immediately, invisible to the eye.
        /// Removed the throttle on the write so it re-asserts every poll, matching the pattern
        /// that worked for the tarp outline fix.
        ///
        /// SECOND FOLLOW-UP: confirmed via instanceId logging that this is the SAME persistent
        /// object every time (not destroyed/recreated as briefly theorized) - yet the fix keeps
        /// re-firing every ~30-40ms on that same instance, meaning something resets it between
        /// almost every one of our writes. Since our own write is a real, synchronous, immediate
        /// property assignment (confirmed working at the moment it runs), the value not surviving
        /// to actually render points to an EXECUTION-ORDER race: whatever resets this apparently
        /// runs AFTER our mod's own update hook within the same frame, so our correction gets
        /// silently overwritten before that frame is ever painted - we win the write but lose the
        /// last word. Fix: moved the actual write out of the regular per-poll HandleUpdate() call
        /// and into a dedicated end-of-frame coroutine (WaitForEndOfFrame), which is guaranteed by
        /// Unity to run after every other component's Update/LateUpdate has completed for that
        /// frame - nothing native can run after this and re-win the race.
        /// </summary>
        private bool _worldScreenSpaceFixCoroutineStarted = false;

        // PAUSED alongside the icon-overlap investigation - confirmed the cull toggle works
        // correctly at the data level but has zero visual effect, so no benefit to keep running it.
        public static bool EnableWorldScreenSpaceOverlapFix = false;

        private void EnsureWorldScreenSpaceFixCoroutine()
        {
            if (!EnableWorldScreenSpaceOverlapFix) return;
            if (_worldScreenSpaceFixCoroutineStarted) return;
            _worldScreenSpaceFixCoroutineStarted = true;
            MelonCoroutines.Start(WorldScreenSpaceFixEndOfFrameLoop());
        }

        private IEnumerator WorldScreenSpaceFixEndOfFrameLoop()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();
                FixWorldScreenSpaceElementOverlap();
            }
        }

        /// <summary>
        /// ROOT CAUSE FOUND (per prior investigation): these objects, and/or their shared parent
        /// WorldScreenSpaceElements, carry HorizontalLayoutGroup/VerticalLayoutGroup/
        /// ContentSizeFitter components - Unity's own automatic child-positioning system, which
        /// recalculates positions via a separate internal Canvas rebuild pass entirely outside
        /// normal Update/LateUpdate/coroutine timing. That's why every earlier attempt (Transform,
        /// LinkUiElement offset, throttled/unthrottled/end-of-frame anchoredPosition writes) had
        /// zero visual effect regardless of timing - none of them were ever racing a script, they
        /// were fighting a layout system that isn't part of the frame loop at all. NEW APPROACH:
        /// disable the layout components (both on the shared parent - the more likely place for
        /// something that spaces SIBLINGS apart - and on each individual icon, in case of nested
        /// layout groups) before applying our own anchoredPosition staggering, so nothing is left
        /// to fight our value afterward. These are stock UnityEngine.UI types (already imported),
        /// not custom game classes, so no reflection/IL2CPP-native-lookup workaround is needed
        /// here - a real, direct GetComponent<T>() call resolves them correctly. Re-checked every
        /// poll (not a one-shot disable) rather than assumed to stay off, matching the lesson
        /// learned twice already on this project (the tarp's MeshOutliner) that native systems can
        /// silently re-enable things every frame - cheap here regardless, since this subtree is
        /// small.
        /// </summary>
        private void DisableWorldScreenSpaceLayoutGroups()
        {
            if (_worldScreenSpaceElementsTransform == null) return;

            int disabledCount = 0;
            foreach (var lg in _worldScreenSpaceElementsTransform.GetComponentsInChildren<HorizontalLayoutGroup>(true))
            {
                if (lg != null && lg.enabled) { lg.enabled = false; disabledCount++; }
            }
            foreach (var lg in _worldScreenSpaceElementsTransform.GetComponentsInChildren<VerticalLayoutGroup>(true))
            {
                if (lg != null && lg.enabled) { lg.enabled = false; disabledCount++; }
            }
            foreach (var csf in _worldScreenSpaceElementsTransform.GetComponentsInChildren<ContentSizeFitter>(true))
            {
                if (csf != null && csf.enabled) { csf.enabled = false; disabledCount++; }
            }

            // NEW: the stock LayoutGroup disable above is confirmed working at the data level
            // (anchoredPosition now holds steady, and a 5000-unit offset produced a real ~1.6
            // meter worldPos separation) but the user still sees the icons stacked on screen -
            // ruling out both a timing race AND a magnitude problem. That means whatever actually
            // renders these icons isn't reading this live worldPos at all. The original component
            // dump also found DirectionalHorizontalLayout, InputTypeOrderProvider, and
            // UiIconElement - none of them stock Unity names, so these are custom Sons Of The
            // Forest classes untouched by anything disabled so far. DirectionalHorizontalLayout in
            // particular sounds like exactly the kind of custom replacement for a stock
            // LayoutGroup that could still be independently computing/overriding final render
            // position. These aren't stock types, so (like LinkUiElement earlier in this saga)
            // their specific managed type doesn't resolve via plain reflection - going via
            // GetComponentsInChildren<Behaviour> (a stock Unity base class every MonoBehaviour-
            // derived component inherits, so it resolves and exposes .enabled correctly) and
            // matching by native class name (il2cpp_class_get_name_) to find and disable them.
            // Deliberately NOT touching UiIconElement here - it sounds like it could BE the actual
            // icon renderer rather than a layout system, and disabling it risks breaking icon
            // visibility entirely rather than just its positioning; worth investigating separately
            // if this round still doesn't work.
            string[] customLayoutTypeNames = { "DirectionalHorizontalLayout", "InputTypeOrderProvider" };
            foreach (var beh in _worldScreenSpaceElementsTransform.GetComponentsInChildren<Behaviour>(true))
            {
                if (beh == null || !beh.enabled) continue;
                string nativeTypeName;
                try
                {
                    IntPtr classPtr = IL2CPP.il2cpp_object_get_class(beh.Pointer);
                    nativeTypeName = classPtr != IntPtr.Zero ? IL2CPP.il2cpp_class_get_name_(classPtr) : "";
                }
                catch { continue; }

                foreach (var targetName in customLayoutTypeNames)
                {
                    if (nativeTypeName == targetName)
                    {
                        beh.enabled = false;
                        disabledCount++;
                        MelonLogger.Msg($"[VRPlayer][WorldScreenSpaceFix] Disabled custom layout component {nativeTypeName} on {GetHierarchyPath(beh.transform)}");
                        break;
                    }
                }
            }

            if (disabledCount > 0)
            {
                MelonLogger.Msg($"[VRPlayer][WorldScreenSpaceFix] Disabled {disabledCount} layout component(s) under WorldScreenSpaceElements");
            }
        }

        public static float WorldScreenSpaceFlashIntervalSeconds = 1f; // user-requested: alternate visibility every 1 second while overlapping

        /// <summary>
        /// PIVOT (user's idea): confirmed conclusively across multiple rounds that repositioning
        /// these elements (Transform, LinkUiElement offset, anchoredPosition at up to 5000 units -
        /// a real ~1.6 meter worldPos separation) has ZERO visual effect no matter what, meaning
        /// whatever actually renders these to the screen doesn't read this Transform's live
        /// position at all. Rather than keep hunting for the real positioning mechanism, sidestep
        /// the problem: alternate VISIBILITY instead of position, which IS confirmed to work
        /// (that's literally how these icons appearing/disappearing has been observed throughout
        /// this whole investigation). Uses CanvasRenderer.cull (draw-only toggle) rather than
        /// GameObject.SetActive() specifically to avoid touching activeSelf, which native game
        /// logic uses to decide whether a prompt is currently relevant at all (e.g. walking away
        /// from the bed) - cull lets native code keep full control of WHETHER an icon should exist
        /// right now, while this only arbitrates WHICH of several simultaneously-relevant icons is
        /// actually drawn at a given instant. Candidates are re-scanned fresh every poll by
        /// activeSelf (never touched by this fix), so there's no tracking ambiguity about whether
        /// native or this code caused a given state.
        /// </summary>
        /// <summary>
        /// CONFIRMED via WorldScreenSpaceFixDiag: the direct typed `t.GetComponent&lt;CanvasRenderer&gt;()`
        /// call returns null on these specific objects, even though CanvasRenderer WAS confirmed
        /// present via the earlier component dump - the same "managed resolution silently fails,
        /// native lookup succeeds" pattern hit repeatedly elsewhere in this saga (LinkUiElement,
        /// DirectionalHorizontalLayout), likely specific to how these dynamically-instantiated
        /// "(Clone)" objects register their types under IL2CPP. Unlike LinkUiElement, the target
        /// type here (CanvasRenderer) is statically known at compile time, so no AppDomain scan is
        /// needed - TryCast&lt;CanvasRenderer&gt;() directly on a Component found via the proven native
        /// class-name lookup is enough.
        /// </summary>
        private CanvasRenderer FindCanvasRendererViaNativeLookup(Transform t)
        {
            // FIXED: was `t.GetComponents<Component>()` (non-recursive, object itself only) - the
            // real CanvasRenderer almost certainly lives on a CHILD of this object (e.g. the actual
            // icon/text sub-object), not on the top-level ScreenSpaceSaveElement(Clone)/
            // SleepElement(Clone) itself. This also explains the original mistaken sighting: the
            // very first component dump used a keyword-based scan across the WHOLE subtree, so it
            // likely picked up a nested child's components (name also matching the keyword filter)
            // and got misattributed to the top-level object when reading the log. The LayoutGroup
            // disable logic already searches recursively (GetComponentsInChildren) - matching that
            // pattern here too.
            foreach (var comp in t.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                string nativeTypeName;
                try
                {
                    IntPtr classPtr = IL2CPP.il2cpp_object_get_class(comp.Pointer);
                    nativeTypeName = classPtr != IntPtr.Zero ? IL2CPP.il2cpp_class_get_name_(classPtr) : "";
                }
                catch { continue; }
                if (nativeTypeName != "CanvasRenderer") continue;
                var typed = comp.TryCast<CanvasRenderer>();
                if (typed != null) return typed;
            }
            return null;
        }

        private float _lastWorldScreenSpaceFixDiagTime = 0f;

        private void FixWorldScreenSpaceElementOverlap()
        {
            if (_screenSpaceCanvas == null) return;
            if (_worldScreenSpaceElementsTransform == null)
            {
                _worldScreenSpaceElementsTransform = _screenSpaceCanvas.transform.Find("WorldScreenSpaceElements");
                if (_worldScreenSpaceElementsTransform == null) return;
            }

            DisableWorldScreenSpaceLayoutGroups();

            // Group active children by near-identical world position.
            var groups = new System.Collections.Generic.List<System.Collections.Generic.List<Transform>>();
            var groupPositions = new System.Collections.Generic.List<Vector3>();

            for (int i = 0; i < _worldScreenSpaceElementsTransform.childCount; i++)
            {
                Transform child = _worldScreenSpaceElementsTransform.GetChild(i);
                if (child == null || !child.gameObject.activeSelf) continue;
                var rt = child.GetComponent<RectTransform>();
                if (rt == null) continue;

                int matchIndex = -1;
                for (int g = 0; g < groupPositions.Count; g++)
                {
                    if ((groupPositions[g] - rt.position).sqrMagnitude < 0.0025f) { matchIndex = g; break; }
                }

                if (matchIndex == -1)
                {
                    groups.Add(new System.Collections.Generic.List<Transform> { child });
                    groupPositions.Add(rt.position);
                }
                else
                {
                    groups[matchIndex].Add(child);
                }
            }

            // NEW: not one single "Hid"/"Showed" line ever appeared in the last test despite
            // HudPromptDiag independently confirming both icons active at matching worldPos the
            // whole time - rather than guess again, log this function's OWN view of the world
            // directly (throttled, since this runs every frame via the end-of-frame coroutine) so
            // it can be compared against HudPromptDiag's independent view and pin down exactly
            // where the two diverge.
            if (Time.time - _lastWorldScreenSpaceFixDiagTime > 1f)
            {
                _lastWorldScreenSpaceFixDiagTime = Time.time;
                MelonLogger.Msg($"[VRPlayer][WorldScreenSpaceFixDiag] childCount={_worldScreenSpaceElementsTransform.childCount} activeChildren={groups.Sum(g => g.Count)} groupCount={groups.Count}");
                foreach (var group in groups)
                {
                    var names = string.Join(", ", group.Select(t =>
                    {
                        var cr2 = FindCanvasRendererViaNativeLookup(t);
                        return $"{t.name}(cull={(cr2 != null ? cr2.cull.ToString() : "no CanvasRenderer")})";
                    }));
                    MelonLogger.Msg($"[VRPlayer][WorldScreenSpaceFixDiag]   group size={group.Count}: {names}");
                }
            }

            foreach (var group in groups)
            {
                if (group.Count < 2)
                {
                    // Not overlapping - make sure it isn't stuck culled from a previous flash cycle
                    // (e.g. its sibling just went inactive, shrinking a 2-member group to 1).
                    var soloRenderer = FindCanvasRendererViaNativeLookup(group[0]);
                    if (soloRenderer != null && soloRenderer.cull) soloRenderer.cull = false;
                    continue;
                }

                int visibleIndex = Mathf.FloorToInt(Time.time / WorldScreenSpaceFlashIntervalSeconds) % group.Count;
                for (int m = 0; m < group.Count; m++)
                {
                    var cr = FindCanvasRendererViaNativeLookup(group[m]);
                    if (cr == null) continue;
                    bool shouldCull = (m != visibleIndex);
                    if (cr.cull != shouldCull)
                    {
                        cr.cull = shouldCull;
                        MelonLogger.Msg($"[VRPlayer][WorldScreenSpaceFix] {(shouldCull ? "Hid" : "Showed")} {GetHierarchyPath(group[m])} (flash group size={group.Count})");
                    }
                }
            }
        }

        public static bool DebugDumpInteractStimuliEnabled = false; // DISABLED - the broader "why don't action prompts show reliably in VR" investigation is set aside for now (see CHANGELOG.md for the full trail: PlayerInteractStimuli turned out to be part of the game's AI perception system, not the player's own interaction tracker; a separate, extensive investigation via a standalone flat-screen debug tool traced the pickup/dismantle prompts through DynamicInputIcon, UiIconElement, FollowActiveWorldLocation, UiManager and HudGui without finding the actual trigger - likely lives on the interactable object itself, not the UI side). Safe to re-enable later since it uses the proven Cast<T>() technique, never the raw pointer construction that crashed once.

        /// <summary>
        /// User's theory: flat-screen interaction detection works fine because mouse/controller
        /// look-aim is precise, but VR's head-as-pointer is naturally less precise at hitting a
        /// narrow trigger zone, causing the sporadic prompt visibility. LocalPlayer/
        /// ExtraPlayerStimuli/PlayerInteractStimuli (found via the earlier keyword scan) sounds
        /// exactly like the detection component - dumping its properties once to find whatever
        /// governs cone angle/radius/distance, rather than guessing at a fix blind.
        /// </summary>
        /// <summary>
        /// User's theory: flat-screen interaction detection works fine because mouse/controller
        /// look-aim is precise, but VR's head-as-pointer is naturally less precise at hitting a
        /// narrow trigger zone, causing the sporadic prompt visibility.
        ///
        /// REWRITTEN to use a proven-SAFE technique instead of the raw-pointer construction that
        /// crashed the game outright last time (confirmed via the crash log's own last line).
        /// Investigated separately via a standalone flat-screen debug tool (zero VR dependency,
        /// so a crash there costs nothing) - confirmed this object's real path is
        /// LocalPlayer/ExtraPlayerStimuli/PlayerInteractStimuli (re-parented there once the
        /// player is fully set up; initially lives under a different, generic "PlayerNetwork"
        /// root right after loading), and its own real type IS reachable safely via the
        /// standard, generic Il2CppObjectBase.Cast&lt;T&gt;() method (invoked via reflection,
        /// same technique already used safely elsewhere in this project for LayoutItem/
        /// QuickSelectLayoutItem) - never via a raw pointer + Activator.CreateInstance.
        ///
        /// Most promising lead from that investigation: a field called _lookAtTargetOverride,
        /// found set to a Transform named "Head". This is very plausibly the actual reference
        /// point the game's own interaction-detection logic uses for "where is the player
        /// looking from" - if that Transform isn't kept in sync with the player's real VR head
        /// orientation (the same category of bug already found and fixed once for the laptop's
        /// own "hold A to close" prompt), that would explain both symptoms reported: needing a
        /// specific angle for some prompts, and others not appearing at all. Comparing this
        /// Transform's own position/rotation against the real VR head (StereoRender.Head)
        /// directly, rather than guessing.
        /// </summary>
        private void DebugDumpInteractStimuli()
        {
            if (!DebugDumpInteractStimuliEnabled) return;
            if (LocalPlayer == null) return;

            if (!_playerInteractStimuliTypeSearched)
            {
                _playerInteractStimuliTypeSearched = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t2 in types)
                    {
                        if (t2.Name == "PlayerInteractStimuli") { _playerInteractStimuliType = t2; break; }
                    }
                    if (_playerInteractStimuliType != null) break;
                }
                MelonLogger.Msg($"[VRPlayer][InteractStimuliDiag] PlayerInteractStimuli type found: {(_playerInteractStimuliType != null)}");
            }
            if (_playerInteractStimuliType == null) return;

            Transform stimuliTransform = null;
            foreach (Transform t in LocalPlayer.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "PlayerInteractStimuli") { stimuliTransform = t; break; }
            }
            if (stimuliTransform == null) return;

            // Throttled here, before the cast attempt itself, so a persistent failure doesn't
            // spam a warning every single poll.
            if (Time.time - _lastInteractStimuliDiagTime < 1f) return;
            _lastInteractStimuliDiagTime = Time.time;

            object realComp = null;
            foreach (var candidate in stimuliTransform.GetComponents<Component>())
            {
                // FIXED (confirmed via log: InvalidCastException, "Can't cast object of type
                // Transform" - the bare "PlayerInteractStimuli" substring also matches the
                // Transform's OWN ToString(), since that includes the GameObject's name, which
                // happens to be "PlayerInteractStimuli" too - not just the real component's own
                // class name.
                //
                // FIXED AGAIN (confirmed via log: the previous fix over-corrected and matched
                // nothing at all, silently - it searched for the Il2Cpp-PREFIXED namespace, but
                // that prefix is purely a managed-side interop renaming convention; the native
                // game code's own ToString() output never includes it. Using the real, unprefixed
                // native namespace instead, which is what ToString() actually contains).
                if (candidate == null || candidate is Transform || !candidate.ToString().Contains("Sons.Ai.Vail.StimuliTypes.PlayerInteractStimuli")) continue;
                try
                {
                    var castMethod = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("Cast")?.MakeGenericMethod(_playerInteractStimuliType);
                    realComp = castMethod?.Invoke(candidate, null);
                }
                catch (Exception e) { MelonLogger.Warning($"[VRPlayer][InteractStimuliDiag] Cast failed: {(e.InnerException != null ? e.InnerException.GetType().Name + ": " + e.InnerException.Message : e.Message)}"); }
                break;
            }
            if (realComp == null) return;

            try
            {
                var overrideField = _playerInteractStimuliType.GetField("_lookAtTargetOverride", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var lookAtTarget = overrideField?.GetValue(realComp) as Transform;
                if (lookAtTarget != null && StereoRender != null && StereoRender.Head != null)
                {
                    float posDist = Vector3.Distance(lookAtTarget.position, StereoRender.Head.position);
                    float angleDiff = Quaternion.Angle(lookAtTarget.rotation, StereoRender.Head.rotation);
                    MelonLogger.Msg($"[VRPlayer][InteractStimuliDiag] _lookAtTargetOverride='{lookAtTarget.name}' pos={lookAtTarget.position} rot={lookAtTarget.rotation.eulerAngles} | realVrHead pos={StereoRender.Head.position} rot={StereoRender.Head.rotation.eulerAngles} | posDist={posDist:F3} angleDiff={angleDiff:F1}");
                }
                else
                {
                    MelonLogger.Msg($"[VRPlayer][InteractStimuliDiag] _lookAtTargetOverride={(lookAtTarget != null ? lookAtTarget.name : "null")} (StereoRender.Head unavailable for comparison: {(StereoRender == null ? "StereoRender null" : "Head null")})");
                }

                // NEW (per user's own idea): since the underlying "can I interact with this"
                // detection is confirmed working correctly even when the visual prompt fails to
                // appear (the action can still be taken), the real bug likely lives in the game's
                // own prompt-rendering UI, not here - chasing _lookAtTargetOverride further may be
                // a dead end regardless. Pivoting toward finding a reliable "interaction is
                // currently possible" signal instead, to eventually drive our own prompt UI
                // directly from it. These field names (seen in the flat-screen tool's own earlier
                // dump) sound like promising candidates for exactly that signal.
                foreach (var fieldName in new[] { "_actor", "_parentActor", "HasRegisteredActor", "RegisteredCount", "_registeredActors" })
                {
                    var field = _playerInteractStimuliType.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? (MemberInfo)_playerInteractStimuliType.GetProperty(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    object value = null;
                    try
                    {
                        if (field is FieldInfo fi) value = fi.GetValue(realComp);
                        else if (field is PropertyInfo pi && pi.CanRead) value = pi.GetValue(realComp);
                    }
                    catch (Exception e) { value = $"<read failed: {e.Message}>"; }
                    MelonLogger.Msg($"[VRPlayer][InteractStimuliDiag] {fieldName}={(value != null ? value.ToString() : "null")}");
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[VRPlayer][InteractStimuliDiag] Field read failed: {e.Message}"); }
        }

        private Type _playerInteractStimuliType;
        private bool _playerInteractStimuliTypeSearched = false;
        private float _lastInteractStimuliDiagTime = 0f;

        private float _lastSpecialActionsDumpTime = 0f;

        /// <summary>
        /// CONFIRMED: LocalPlayer/ControllerObjects/SpecialActions hosts individual "XActionIcon"
        /// objects (ClimbLadderActionIcon found via keyword scan) - same parent that already
        /// hosts GolfCartDriverAction/PassengerAction/ActionInputState. Enumerating everything
        /// under it once to find the full set of prompt icon names (sleep, save, pickup, cook,
        /// etc.) and their active state, rather than hoping the keyword scanner catches each one
        /// individually while it happens to be shown.
        /// </summary>
        private Transform _specialActionsTransform;

        private void DebugDumpSpecialActionsChildren()
        {
            if (!DebugLogCanvasChanges) return;
            if (LocalPlayer == null) return;
            if (Time.time - _lastSpecialActionsDumpTime < 1f) return;
            _lastSpecialActionsDumpTime = Time.time;

            // FIXED: was a one-shot scan, same mistake already made once before with the golf
            // cart scan - confirmed via log that SpecialActions' CHILDREN are dynamically
            // instantiated based on context (only present near a ladder, in the cart, etc.),
            // not permanent. A one-time scan only ever catches whatever happens to be there at
            // that single moment (in this case, just PlayerDeathCutsceneControllers, since it
            // fired before the player was near anything specific). The SpecialActions parent
            // itself IS always present though, so that lookup can still be cached once.
            if (_specialActionsTransform == null)
            {
                foreach (Transform t in LocalPlayer.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "SpecialActions")
                    {
                        _specialActionsTransform = t;
                        break;
                    }
                }
                if (_specialActionsTransform == null) return;
            }

            // FIXED preventively (same latent IL2CPP interop bug as PickupGui above, hasn't
            // thrown here yet but uses the identical risky pattern): non-generic
            // `foreach (Transform x in someTransform)` over Transform's IEnumerable can throw
            // InvalidCastException under IL2CPP interop. Indexed GetChild() avoids it.
            for (int i = 0; i < _specialActionsTransform.childCount; i++)
            {
                Transform child = _specialActionsTransform.GetChild(i);
                MelonLogger.Msg($"[VRPlayer][SpecialActionsDump] {child.name}: activeSelf={child.gameObject.activeSelf}");
            }
        }

        private bool IsInGolfCart()
        {
            if (LocalPlayer == null) return false;

            if (_golfCartInputStateTransform == null)
            {
                if (Time.time - _lastGolfCartSearchTime < 1f) return false;
                _lastGolfCartSearchTime = Time.time;

                foreach (Transform t in LocalPlayer.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "GolfCartActionInputState")
                    {
                        _golfCartInputStateTransform = t;
                        break;
                    }
                }
                if (_golfCartInputStateTransform == null) return false;
            }

            return _golfCartInputStateTransform.gameObject.activeSelf;
        }

        private void DebugLogVehicleCameraState()
        {
            if (!EnableVehicleCameraDiagnostic) return;
            if (Time.time - _lastVehicleScanTime < 0.15f) return;
            _lastVehicleScanTime = Time.time;

            // NEW ROUND: user reports the flicker persists, the view doesn't follow the cart's
            // steering, AND the entry-snap didn't face the right direction, plus a recurring
            // massive desync (1596.9m, matching an earlier one at 1626.1m) happened during this
            // test. High-frequency sampling (0.15s, fast enough to catch a rapid flicker) of the
            // detection signal itself (in case it's flickering True/False rather than being
            // stable, which would explain everything at once) alongside rig and body rotation,
            // to see what's actually happening rather than guessing at a third fix blind.
            bool inCart = IsInGolfCart();
            string rigRot = StereoRender != null ? StereoRender.transform.rotation.eulerAngles.ToString() : "N/A";
            string rigPos = StereoRender != null ? StereoRender.transform.position.ToString() : "N/A";
            string bodyRot = LocalPlayer != null ? LocalPlayer.transform.rotation.eulerAngles.ToString() : "N/A";
            string originPos = Origin != null ? Origin.position.ToString() : "N/A";
            MelonLogger.Msg($"[VRPlayer][GolfCartFlickerDiag] inCart={inCart} rigRot={rigRot} rigPos={rigPos} bodyRot={bodyRot} originPos={originPos}");
        }

        private void CheckForStuckViewDesync()
        {
            if (Origin == null || UsedCamera == null) return;

            // UsedCamera gets deliberately repositioned to raw head position while any
            // menu/inventory is open (tarp, campfire, book, etc.) - a completely different
            // meaning than "where your character is in the world" during normal gameplay.
            // Treating that as a desync and force-correcting was confirmed to send the view
            // flying - skip entirely while any menu is active.
            if (IsUiCursorActive) return;

            if (!_handleUpdateFirstRunRecorded)
            {
                _handleUpdateFirstRunRecorded = true;
                _handleUpdateFirstRunTime = Time.unscaledTime;
            }

            // Grace period: don't let the watchdog act during the first few seconds after
            // stereo rendering starts up. Two crashes/freezes now correlate directly with the
            // watchdog's hard, instant teleport happening right during this early window -
            // normal per-frame syncing should settle this naturally on its own during startup,
            // so the watchdog is only really needed for a gap that PERSISTS well beyond that
            // (like the genuine multiplayer 40+ second stuck scenario).
            if (Time.unscaledTime - _handleUpdateFirstRunTime < DesyncGracePeriodSeconds) return;

            if (Time.unscaledTime - _lastDesyncCheckTime < 1f) return;
            _lastDesyncCheckTime = Time.unscaledTime;

            float distance = Vector3.Distance(Origin.position, UsedCamera.transform.position);
            if (distance > DesyncCheckDistance)
            {
                MelonLogger.Warning($"[VRPlayer] View desync detected: VR rig is {distance:F1}m from the real game camera - force-correcting.");
                Origin.position = UsedCamera.transform.position;
            }
        }

        private void DebugScanLocalPlayerActivity()
        {
            if (!DebugScanCanvasesOnly) return;
            if (LocalPlayer == null) return;
            if (Time.time - _lastPlayerScanTime < 0.3f) return;
            _lastPlayerScanTime = Time.time;

            var current = new System.Collections.Generic.HashSet<string>();
            Transform[] children = LocalPlayer.transform.GetComponentsInChildren<Transform>(true);
            foreach (var t in children)
            {
                if (t == null) continue;
                string path = GetHierarchyPath(t);
                if (t.gameObject.activeSelf)
                {
                    current.Add(path);
                    if (!_lastPlayerChildActive.Contains(path))
                    {
                        MelonLogger.Msg($"[VRPlayer][PlayerScan] ACTIVATED: {path}");
                    }
                }
            }
            foreach (var path in _lastPlayerChildActive)
            {
                if (!current.Contains(path))
                {
                    MelonLogger.Msg($"[VRPlayer][PlayerScan] deactivated: {path}");
                }
            }
            _lastPlayerChildActive = current;
        }

        private System.Collections.Generic.HashSet<string> _lastHudScanActive = new System.Collections.Generic.HashSet<string>();
        private float _lastHudScanTime = 0f;

        /// <summary>
        /// Corrected understanding: the "info bubble" messages ("you are hungry", "5 ammo picked
        /// up", "can't carry more lightbulbs") are screen-anchored and gaze-following, NOT
        /// world-anchored at the item - the same general system as the originally-described
        /// notifications, not a separate world-space popup. A broad scene-wide keyword scan came
        /// up empty, meaning it likely just doesn't use any of the guessed words - rather than
        /// keep guessing names, scanning directly under the known HUD root (_screenSpaceCanvas,
        /// already confirmed to be what the main HUD lives under) for ANY newly-activated object,
        /// which will catch it regardless of its actual name the moment a message appears.
        /// </summary>
        private void DebugScanHudMessages()
        {
            if (!DebugLogCanvasChanges) return;
            if (_screenSpaceCanvas == null) return;
            if (Time.time - _lastHudScanTime < 0.3f) return;
            _lastHudScanTime = Time.time;

            var current = new System.Collections.Generic.HashSet<string>();
            Transform[] children = _screenSpaceCanvas.transform.GetComponentsInChildren<Transform>(true);
            foreach (var t in children)
            {
                if (t == null) continue;
                string path = GetHierarchyPath(t);
                if (t.gameObject.activeSelf)
                {
                    current.Add(path);
                    if (!_lastHudScanActive.Contains(path))
                    {
                        MelonLogger.Msg($"[VRPlayer][HudScan] ACTIVATED: {path}");
                    }
                }
            }
            foreach (var path in _lastHudScanActive)
            {
                if (!current.Contains(path))
                {
                    MelonLogger.Msg($"[VRPlayer][HudScan] deactivated: {path}");
                }
            }
            _lastHudScanActive = current;
        }

        private float _lastVitalsSceneWideScanTime = 0f;

        /// <summary>
        /// Both structural candidates (ToggleableHUD/PlayerVitalsUi and PlayerVitalsUiWorldSpace)
        /// have been directly, conclusively ruled out via visible extreme-offset tests - moving
        /// PlayerVitalsUiWorldSpace produced a genuine giant duplicate, but the user confirmed the
        /// NORMAL vitals ring stayed exactly where it always was at the same time, meaning that
        /// object is a separate, additional render, not the one actually controlling what's
        /// normally visible. Rather than keep guessing specific paths, a scene-wide search for
        /// anything with a plausible name. FIXED: was a one-shot scan that fired on the very first
        /// poll - confirmed via log it ran during the title menu/settings screen, before any real
        /// gameplay objects existed, so it only ever found unrelated menu UI. Now repeats
        /// (throttled) so it has a real chance to run once actual gameplay objects exist.
        /// </summary>
        private void DebugScanForVitalsSceneWide()
        {
            // GATED: was missing a flag check entirely - would run a full scene-wide Transform scan
            // (FindObjectsOfType<Transform>(true)) every 8 seconds regardless of any diagnostic flag,
            // a real, avoidable performance cost. This investigation has served its purpose and is
            // paused - gated behind DebugLogCanvasChanges like the other scanners.
            if (!DebugLogCanvasChanges) return;
            if (StereoRender == null || StereoRender.Head == null) return;
            if (Time.time - _lastVitalsSceneWideScanTime < 8f) return;
            _lastVitalsSceneWideScanTime = Time.time;

            string[] keywords = { "vitals", "health", "armour", "armor", "strength", "hunger", "fullness", "hydration", "rested" };
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
            {
                if (t == null) continue;
                string lowerName = t.name.ToLowerInvariant();
                foreach (var kw in keywords)
                {
                    if (lowerName.Contains(kw))
                    {
                        MelonLogger.Msg($"[VRPlayer][VitalsSceneWideScan] {GetHierarchyPath(t)} active={t.gameObject.activeSelf} hasRectTransform={t.GetComponent<RectTransform>() != null}");
                        break;
                    }
                }
            }
            MelonLogger.Msg("[VRPlayer][VitalsSceneWideScan] Scan complete.");
        }

        private void DebugScanGrabBagItems()
        {
            if (_grabBagHeld == null) return;
            if (Time.time - _lastGrabBagItemScanTime < 0.3f) return;
            _lastGrabBagItemScanTime = Time.time;

            var currentActive = new System.Collections.Generic.HashSet<string>();

            Transform[] children = _grabBagHeld.GetComponentsInChildren<Transform>(true);
            foreach (var t in children)
            {
                if (t == null) continue;
                string path = GetHierarchyPath(t);

                if (t.gameObject.activeSelf)
                {
                    currentActive.Add(path);
                    if (!_lastGrabBagItemActive.Contains(path))
                    {
                        MelonLogger.Msg($"[VRPlayer][GrabBagScan] ACTIVATED: {path}");
                    }
                }

                if (_lastGrabBagItemScales.TryGetValue(path, out Vector3 prevScale))
                {
                    if (Vector3.Distance(prevScale, t.localScale) > 0.01f)
                    {
                        MelonLogger.Msg($"[VRPlayer][GrabBagScan] SCALE CHANGED: {path} {prevScale} -> {t.localScale}");
                    }
                }
                _lastGrabBagItemScales[path] = t.localScale;
            }

            foreach (var path in _lastGrabBagItemActive)
            {
                if (!currentActive.Contains(path))
                {
                    MelonLogger.Msg($"[VRPlayer][GrabBagScan] deactivated: {path}");
                }
            }
            _lastGrabBagItemActive = currentActive;
        }

        /// <summary>
        /// Detects the in-game pause/save menu via Time.timeScale==0 (standard for pause
        /// menus, and confirmed this game's menu isn't Canvas/UIDocument-based so we can't
        /// detect it any other way we've found). While paused, captures the actual flat-screen
        /// frame (which correctly shows the menu, confirmed by switching to desktop) into a
        /// RenderTexture and displays it on a quad positioned in front of the player.
        /// </summary>
        private Transform _pauseMenuCanvasObject;

        /// <summary>
        /// Public so KeyboardHack's auto-menu FPS fix can use this same, already-proven-correct
        /// detection instead of its own separate one. Updated every frame from UpdateFlatMenuView.
        /// </summary>
        public static bool IsPauseMenuOpenConfirmed = false;
        // V112: strict single-player pause signal used by the temporary 2E UI pin. The broader
        // IsPauseMenuOpenConfirmed also supports multiplayer via a PauseMenu hierarchy signal, but
        // that object can outlive visible navigation state. Using timeScale here prevents a hidden/
        // stale pause root from accidentally forcing 2E throughout normal single-player gameplay.
        public static bool IsTimeScalePausedV112 => Time.timeScale <= 0.0001f;

        /// <summary>
        /// Public so KeyboardHack's auto-menu FPS fix can distinguish single-player from
        /// multiplayer directly, rather than inferring it from elapsed time. Confirmed via three
        /// separate log sessions: multiplayer consistently reports "DontDestroyOnLoad" here, while
        /// single-player consistently reports "SonsMain" - a real, reliable distinction, not a
        /// guess.
        /// </summary>
        public static bool IsLikelySinglePlayerGameplay => Instance != null && Instance.UsedSceneName == "SonsMain";
        private float _lastPauseMenuSearchTime = 0f;

        /// <summary>
        /// Confirmed via live scan: this Canvas object (same ScreenSpaceCanvas hierarchy as
        /// SoftwareCursor) becomes active the moment the pause/save menu opens in MULTIPLAYER,
        /// where Time.timeScale never actually drops to 0 (the shared world can't pause for
        /// other connected players) - unlike single-player, where timeScale==0 already covers
        /// it. This gives a real, mode-independent "menu is open" signal.
        /// </summary>
        private void FindPauseMenuCanvasObject()
        {
            if (_pauseMenuCanvasObject != null) return;
            if (Time.time - _lastPauseMenuSearchTime < 1.0f) return;
            _lastPauseMenuSearchTime = Time.time;

            GameObject found = GameObject.Find("PlayerStandin/PlayerUI/ScreenSpaceCanvas/PauseMenu");
            if (found != null) _pauseMenuCanvasObject = found.transform;
        }

        private Camera _lastKnownMainCameraFP;
        private bool _lastTitleSyncSkipState = false;
        private Camera _lastTitleSyncCamera;
        private float _gameplayReadySinceTime = -1f;
        private Quaternion? _headRotationWhenGameplayReady = null;

        /// <summary>
        /// Automatically presses the gamepad's Start/Menu button once per session, shortly
        /// after actual gameplay is confirmed active, to open the real pause menu and fix a
        /// GPU-underutilization/low-FPS issue that's only ever been confirmed fixed by a genuine
        /// menu open (see KeyboardHack.ExecuteAutoOpenAndCloseMenu for why this uses real
        /// synthetic input events rather than faking the effect in our own code).
        ///
        /// IMPORTANT: originally gated on StereoRender being ready, but that becomes true
        /// already at the TITLE SCREEN (the VR rig sets up before any save is even loaded) -
        /// confirmed via testing that this fired the one-shot press far too early, wasting it on
        /// the main menu instead of during actual gameplay. Now gated on LocalPlayer being
        /// non-null and UsedCamera genuinely being the real gameplay camera (MainCameraFP) -
        /// the same signal already used throughout this file as the standard "are we actually in
        /// gameplay, not a menu screen" check. This flag also gets reset in SetSceneAndCamera
        /// whenever a genuinely new MainCameraFP appears, so a save reload (without a full game
        /// restart) triggers this again too, not just the very first load.
        ///
        /// Opens the menu AND closes it again automatically (Start, then B ~1s later) - a full
        /// self-contained cycle, no manual interaction needed.
        /// </summary>
        /// <summary>
        /// FIXED: previously set a one-shot "already tried" flag the moment this called into
        /// KeyboardHack, regardless of whether the attempt actually worked - confirmed via user
        /// report that during the new-game intro sequence (helicopter cutscene -> a brief
        /// interactive window where pause genuinely doesn't work -> crash cutscene -> real
        /// gameplay), the game ignores pause input for most of that window even once
        /// MainCameraFP is active and a gamepad is detected, silently burning the only attempt.
        /// KeyboardHack.ExecuteAutoOpenAndCloseMenu() now verifies real success internally (via
        /// Time.timeScale actually dropping to 0) and keeps retrying every few seconds until it
        /// genuinely works, so it's now safe to just keep calling this every poll once gameplay
        /// looks active - it's a no-op while a previous attempt is still running or has already
        /// succeeded.
        /// </summary>
        /// <summary>
        /// FIXED (user's own idea, after a real, unresolved doubt about whether the previous
        /// "success" was ever genuinely from our own automated input at all, versus the user's
        /// own manual Start/B press - Time.timeScale dropping is a GLOBAL game state, so our code
        /// has no way to tell those two apart, and the log alone could never settle it). Gating on
        /// genuine head rotation since gameplay began, rather than a fixed 1.5s settle timer -
        /// a real player looks around almost immediately once they actually have control, so this
        /// should naturally land the first attempt past the exact transition window that produced
        /// the canvas signal's false positive, without needing to guess at a safe delay.
        /// </summary>
        private const float HeadMovementGateDegrees = 8f;

        private void TryAutoOpenMenuOnceForFpsFix()
        {
            if (kh == null) return;
            if (LocalPlayer == null || UsedCamera == null || !UsedCamera.name.Equals("MainCameraFP")) return;
            if (StereoRender == null || StereoRender.Head == null) return;

            if (_gameplayReadySinceTime < 0f)
            {
                _gameplayReadySinceTime = Time.time;
                _headRotationWhenGameplayReady = StereoRender.Head.rotation;
                return;
            }

            float degreesRotatedSince = Quaternion.Angle(_headRotationWhenGameplayReady.Value, StereoRender.Head.rotation);
            if (degreesRotatedSince < HeadMovementGateDegrees) return;

            kh.ExecuteAutoOpenAndCloseMenu();
        }

        /// <summary>
        /// Continuously reasserts the tarp elevation workaround every frame while the tarp/
        /// inventory screen is open - CONFIRMED via user testing that a single one-time write
        /// (in SetSceneAndCamera, right when the camera changes) gets immediately undone by the
        /// game's own gravity/grounding logic on the very next frame (visible as a brief upward
        /// jump that instantly falls back to sunken). Fighting that every frame, the same way
        /// head-freeze fights SteamVR's own per-frame tracking update elsewhere in this file.
        ///
        /// Also raises Origin (the VR rig anchor, i.e. the player's actual eye-level view) by
        /// the same amount - CONFIRMED via user report that only elevating the character's
        /// logical position (for the tarp's sake) without also raising the player's own
        /// viewpoint left gaze-based item selection misaligned, since the tarp visually moved up
        /// but the player's eyes didn't.
        ///
        /// Directly writing LocalPlayer's position can leave a CharacterController's internal
        /// grounded/velocity state out of sync with the sudden change, which can register as a
        /// violent fall on its next internal step - a known Unity gotcha, and a likely
        /// explanation for reported fall damage even with a clean instant position write.
        /// Disabling the CharacterController around the write (standard Unity teleport pattern)
        /// avoids that misdetection.
        /// </summary>
        public static float TarpRecenterDelaySeconds = 0.2f; // updated by user testing
        // zer0int v4: keep v3's useful horizontal back-off, but stop adaptive ray steering
        // because live diagnostics showed the downward ray only sees Site02Terrain Tess.
        public static float TarpRecenterForwardOffsetMeters = 0.55f;
        // Raise only the VR origin/head above the avatar during tarp mode. LocalPlayer/body
        // remains at the existing tarp-elevation workaround height, so the HMD is no longer
        // embedded in the torso while preserving the increased tarp distance.
        public static float TarpHeadExtraYOffsetMeters = 0.55f;
        public static bool TarpFarClipEnabled = true;
        public static float TarpFarClipMeters = 50f;

        /// <summary>
        /// Adjusts Origin's horizontal (X/Z) position by whatever delta is needed to make
        /// StereoRender.Head's CURRENT world position match LocalPlayer's own position, offset
        /// forward by TarpRecenterForwardOffsetMeters (adjustable - the first version, with no
        /// offset, left the camera correctly centered but noticeably behind the character by
        /// about a meter, most likely because LocalPlayer's own transform sits at a root/pivot
        /// point behind the eyes rather than at the eyes themselves). A one-time realignment, not
        /// continuous, so it doesn't fight the player's own real head movement afterward. Height
        /// (Y) is deliberately left untouched, since that's already handled separately by the tarp
        /// elevation workaround above.
        /// </summary>
        private System.Collections.IEnumerator RecenterOriginToPlayerAfterDelay()
        {
            yield return new WaitForSeconds(TarpRecenterDelaySeconds);

            if (Origin == null || StereoRender == null || StereoRender.Head == null || LocalPlayer == null) yield break;
            if (!InventoryCamIsActive) yield break; // tarp already closed again before the delay elapsed - skip, avoid recentering to a state that no longer applies

            Vector3 headPos = StereoRender.Head.position;

            Vector3 flatForward = LocalPlayer.transform.forward;
            flatForward.y = 0f;
            if (flatForward.sqrMagnitude < 1e-4f) flatForward = Vector3.forward;
            flatForward.Normalize();

            Vector3 targetHeadPos = LocalPlayer.transform.position + flatForward * TarpRecenterForwardOffsetMeters;

            Vector3 delta = targetHeadPos - headPos;
            delta.y = 0f; // height untouched - handled separately by the tarp elevation workaround

            Vector3 originPos = Origin.position;
            originPos.x += delta.x;
            originPos.z += delta.z;
            Origin.position = originPos;

            MelonLogger.Msg($"[VRPlayer][TarpRecenter] Recentered Origin by delta={delta} (head was at {headPos}, target was {targetHeadPos}, forward offset={TarpRecenterForwardOffsetMeters}m, headYAbovePlayer={(StereoRender.Head.position.y - LocalPlayer.transform.position.y):F2}m, extraHeadY={GetEffectiveTarpHeadExtraYOffsetV27():F2}m).");
        }

        private void UpdateTarpElevationWorkaround()
        {
            if (TarpElevationWorkaroundOffset == 0f || !InventoryCamIsActive || LocalPlayer == null) return;

            float targetY = _tarpBasePlayerY + TarpElevationWorkaroundOffset;
            SetPlayerYSafely(targetY);

            if (Origin != null)
            {
                Vector3 originPos = Origin.position;
                float originTargetY = _tarpBaseOriginY + TarpElevationWorkaroundOffset + GetEffectiveTarpHeadExtraYOffsetV27();
                if (Mathf.Abs(originPos.y - originTargetY) > 0.001f)
                {
                    originPos.y = originTargetY;
                    Origin.position = originPos;
                }
            }
        }

        /// <summary>
        /// Writes LocalPlayer's Y position, temporarily disabling any CharacterController first
        /// so it doesn't misread the sudden change as a real, violent fall (a known Unity
        /// gotcha with directly setting transform.position on a controller-driven object).
        /// </summary>
        private void SetPlayerYSafely(float targetY)
        {
            Vector3 pos = LocalPlayer.transform.position;
            if (Mathf.Abs(pos.y - targetY) <= 0.001f) return;

            var controller = LocalPlayer.GetComponent<CharacterController>();
            bool wasEnabled = controller != null && controller.enabled;
            if (controller != null) controller.enabled = false;

            pos.y = targetY;
            LocalPlayer.transform.position = pos;

            var rb = LocalPlayer.GetComponent<Rigidbody>();
            if (rb != null) rb.velocity = Vector3.zero;

            if (controller != null) controller.enabled = wasEnabled;
        }

        private void UpdateFlatMenuView()
        {
            // V145: native timeScale is authoritative for single-player. Merely
            // having a PauseMenu root active is NOT evidence of an open menu.
            // A scene transition may close a menu while timeScale remains zero
            // for a few loading frames: do not reopen until timeScale resumes.
            if (_v145ImplicitCloseLatch && Time.timeScale > 0.0001f)
                _v145ImplicitCloseLatch = false;
            bool isPausedSinglePlayer = IsLikelySinglePlayerGameplay &&
                UsedCamera != null && UsedCamera.name == "MainCameraFP" &&
                Time.timeScale <= 0.0001f && !_v145ImplicitCloseLatch;

            bool isMultiplayerMenuV145 = false;
            if (!IsLikelySinglePlayerGameplay && UsedSceneName == "DontDestroyOnLoad")
            {
                FindPauseMenuCanvasObject();
                isMultiplayerMenuV145 = _pauseMenuCanvasObject != null &&
                    _pauseMenuCanvasObject.gameObject.activeInHierarchy;
            }
            bool isMenuOpenAnyMode = isPausedSinglePlayer || isMultiplayerMenuV145;

            // Exposed for KeyboardHack's auto-menu FPS fix: this same detection is already
            // proven correct (confirmed via log - it correctly caught the menu opening, driving
            // the blur-suppression message, at the exact moment KeyboardHack's own separate
            // detection missed it and kept incorrectly retrying). Reusing this directly rather
            // than maintaining two independent, apparently-inconsistent detection paths.
            IsPauseMenuOpenConfirmed = isMenuOpenAnyMode;

            if (isPausedSinglePlayer != _v144PauseSeen || isMenuOpenAnyMode != _v144MenuSeen)
            {
                _v144PauseSeen = isPausedSinglePlayer;
                _v144MenuSeen = isMenuOpenAnyMode;
                ManualPauseFpsTraceV144.RecordPauseStep("FLAT_MENU_ENTER_STATE",
                    "timeScalePaused=" + isPausedSinglePlayer + " menuOpen=" + isMenuOpenAnyMode +
                    " cachedNativeCanvas=" + (_pauseMenuCanvasObject != null) +
                    " experimentalCamRestore=" + ExperimentalRestoreCameraOnPause +
                    " volumeSuppressionEnabled=" + SuppressPauseMenuBlur);
            }

            UpdateCameraSettingsForPause(isPausedSinglePlayer);
            // Blur suppression works in both modes - no reason to keep the blur in multiplayer
            // just because the world can't pause. Confirmed via direct diff against a known-
            // good build that this canvas-based detection was NOT the cause of the earlier
            // GPU-underutilization regression - that was specifically the multi-volume
            // suppression logic, now reverted to single-volume (see UpdatePauseMenuBlurSuppression).
            //
            // NOTE: an automated "startup kick" that faked this same suppress/restore toggle
            // without a real pause was tried and reverted - it didn't fix the GPU-
            // underutilization issue and left the state slightly out of sync (required TWO real
            // menu opens afterward instead of one). The real trigger appears to live in the
            // game's own native pause system (Time.timeScale changing, or something else we
            // don't have visibility into) rather than in this weight toggle alone - opening the
            // actual in-game menu once per session remains the working fix for now.
            UpdatePauseMenuBlurSuppression(isMenuOpenAnyMode); // V145 bounded, transition-driven lookup
            // Head-freeze stays single-player ONLY: freezing the tracked view while other
            // players (and enemies) keep moving around you in multiplayer would leave you
            // unable to see or react to an attack - a real safety concern the blur fix doesn't
            // have.
            UpdateHeadFreezeForPause(isPausedSinglePlayer);

            bool isPaused = isPausedSinglePlayer;

            // NEW: also show this same flat-menu-view quad at the title screen, so the Solo/
            // Multiplayer/Options/Quit menu (previously invisible/unclickable in VR - the 3D
            // background renders fine but the actual UI canvas doesn't) becomes visible attached
            // to the player's view, the same way it already works for the pause/save menu.
            bool showTitleScreenInVR = ShowTitleScreenInVR && UsedSceneName == "SonsTitleScene";
            bool showFlatMenu = (EnableFlatMenuView && isPaused) || showTitleScreenInVR;

            if (showFlatMenu)
            {
                EnsureFlatMenuQuad();

                // FOLLOW-UP (binoculars saga): the technique that failed here twice
                // (ScreenCapture without WaitForEndOfFrame timing, and a forced mid-frame
                // Camera.Render()) has since been proven safe and correct elsewhere in this
                // project - ScreenCapture.CaptureScreenshotIntoRenderTexture(), called from a
                // dedicated WaitForEndOfFrame() coroutine, confirmed via direct pixel-sampling to
                // capture genuinely correct, live, varying content with no exceptions. Wiring that
                // same proven technique in here now as a controlled comparison test: if this
                // quad ALSO stays invisible in VR despite provably-correct capture data, that
                // points to a problem general to "captured quad in VR" rather than anything
                // specific to the binoculars context - and if it works HERE, that's a real,
                // separate clue about what's different between the two contexts. Also doubles as
                // a genuine attempt at finally fixing the long-standing confirmation-dialog issue,
                // if this turns out to work.
                if (!_flatMenuCaptureCoroutineStarted)
                {
                    _flatMenuCaptureCoroutineStarted = true;
                    MelonCoroutines.Start(FlatMenuCaptureLoop());
                }
                // Only save once _wasPausedLastFrame is already true - i.e. not on the very first
                // frame the menu opens, giving the coroutine at least one full frame to actually
                // capture something first, so the saved PNG isn't blank/uninitialized.
                if (_wasPausedLastFrame) SaveDiagnosticCaptureOnce();

                if (_flatMenuQuad != null && StereoRender != null && StereoRender.Head != null)
                {
                    if (!_flatMenuQuad.activeSelf) _flatMenuQuad.SetActive(true);
                    // TEMP occlusion test - see the matching comment on BinocularsFlatQuadDistance.
                    Vector3 pos = StereoRender.Head.position + StereoRender.Head.forward * 0.05f;
                    // FIXED: a default Quad's texture-facing side points along its own local +Z.
                    // Using Head.forward directly pointed that side AWAY from the player (since
                    // Head.forward is the direction the player is looking, i.e. away from them),
                    // so they were seeing the BACK of the quad - confirmed by user report of a
                    // dark, mirrored/upside-down view. Negating it points the front face back
                    // toward the player instead, matching the same "face the camera" convention
                    // already used elsewhere in this file (e.g. the aim reticle).
                    //
                    // CONFIRMED via user testing: calling SetTextureScale/SetTextureOffset on
                    // this material - regardless of the actual values passed - breaks its
                    // rendering entirely (every version that touched those went fully invisible;
                    // only the original untouched version ever showed anything). Fixing the
                    // orientation via a pure TRANSFORM rotation instead, leaving the material
                    // completely untouched. The captured image appeared both upside-down AND
                    // mirrored simultaneously (not just flipped vertically) - exactly what a
                    // 180-degree rotation looks like - so a 180-degree roll (rotation around the
                    // quad's own forward/viewing axis, applied after it already faces the
                    // player) should correct it with nothing but a rotation.
                    Quaternion rot = Quaternion.LookRotation(-StereoRender.Head.forward, Vector3.up) * Quaternion.Euler(0f, 0f, 180f);
                    _flatMenuQuad.transform.SetPositionAndRotation(pos, rot);

                    float screenAspect = (float)Mathf.Max(Screen.width, 2) / Mathf.Max(Screen.height, 2);
                    float quadHeight = 0.9f;
                    _flatMenuQuad.transform.localScale = new Vector3(quadHeight * screenAspect, quadHeight, 1f);
                }

                if (!_wasPausedLastFrame)
                {
                    ManualPauseFpsTraceV144.RecordPauseStep("FLAT_MENU_QUAD_VISIBLE", "quadActive=" + (_flatMenuQuad != null && _flatMenuQuad.activeSelf) + " captureCoroutineStarted=" + _flatMenuCaptureCoroutineStarted);
                    MelonLogger.Msg($"[VRPlayer] Flat menu view shown ({(showTitleScreenInVR ? "title screen" : "pause/save menu")}).");
                }
            }
            else if (_flatMenuQuad != null && _flatMenuQuad.activeSelf)
            {
                _flatMenuQuad.SetActive(false);
                _diagnosticCaptureSavedThisSession = false;
            }

            if (!showFlatMenu)
            {
                _v144FlatCaptureReported = false;
                _v144FlatCaptureTimingReported = false;
            }
            _flatMenuCaptureActive = showFlatMenu;
            _wasPausedLastFrame = showFlatMenu;
        }

        private bool _flatMenuCaptureCoroutineStarted = false;
        private bool _flatMenuCaptureActive = false;
        private bool _v144PauseSeen;
        private bool _v144MenuSeen;
        private bool _v144FlatCaptureReported;
        private bool _v144FlatCaptureTimingReported;

        private IEnumerator FlatMenuCaptureLoop()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();
                if (_flatMenuCaptureActive && _flatMenuCaptureRT != null)
                {
                    try
                    {
                        if (!_v144FlatCaptureReported)
                        {
                            _v144FlatCaptureReported = true;
                            ManualPauseFpsTraceV144.RecordPauseStep("FIRST_FLAT_MENU_CAPTURE", "WaitForEndOfFrame; before ScreenCapture.CaptureScreenshotIntoRenderTexture");
                        }
                        long v144FlatCaptureStart = _v144FlatCaptureReported && !_v144FlatCaptureTimingReported ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                        ScreenCapture.CaptureScreenshotIntoRenderTexture(_flatMenuCaptureRT);
                        if (v144FlatCaptureStart != 0L)
                        {
                            _v144FlatCaptureTimingReported = true;
                            double v144FlatCaptureMs = 1000.0 * (System.Diagnostics.Stopwatch.GetTimestamp() - v144FlatCaptureStart) / System.Diagnostics.Stopwatch.Frequency;
                            ManualPauseFpsTraceV144.RecordPauseStep("FIRST_FLAT_MENU_CAPTURE_DONE", "elapsedMs=" + v144FlatCaptureMs.ToString("F3"));
                        }
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Msg($"[VRPlayer] FlatMenu capture call threw: {e.Message}");
                        yield break;
                    }
                }
            }
        }

        /// <summary>
        /// Experimental: temporarily restores more standard camera clear-flags/culling/depth
        /// while paused, since the mod's overlay-specific reconfiguration of these is a real
        /// candidate cause for the mod-only pause-menu blur. Restores the mod's own settings
        /// the instant the menu closes, so the UI-overlay trick keeps working during normal play.
        /// </summary>
        private UnityEngine.Rendering.Volume _pauseMenuBlurVolume;
        private float _pauseMenuBlurVolumeSavedWeight = 1f;
        private bool _pauseMenuBlurSuppressed = false;

        /// <summary>
        /// CONFIRMED via direct log evidence (not a guess): 'PauseMenuPostProcessingVolume' is
        /// a global, priority-10 Volume with an active Depth of Field override (manual focus,
        /// nearFocusEnd=0/farFocusStart=0 - blurs everything) that only appears while paused.
        /// This is the actual pause/save menu blur. Suppress it by zeroing its weight while
        /// paused, restoring it the instant you unpause.
        ///
        /// IMPORTANT: reverted back to touching only a SINGLE cached volume (not every matching
        /// one) - a version that suppressed ALL matching volumes was confirmed via diffing
        /// against a known-good build to be the cause of a real GPU-underutilization/low-FPS
        /// regression (touching more than one volume broke something performance-related this
        /// game's DOF/HDRP setup relies on). To still handle a save-reload leaving this cached
        /// reference stale (pointing at an old, orphaned-but-not-destroyed volume from a
        /// previous save while a new one exists for the current one), re-validate that the
        /// cached volume is still among the currently-found matches each time before using it,
        /// and re-search if not - without ever touching more than one volume per call.
        /// </summary>
        public static bool SuppressPauseMenuBlur = true;

        private Quaternion _frozenHeadRotation;
        private bool _headRotationFrozen = false;

        /// <summary>
        /// Body-freeze doesn't help the pause menu because it only stops the CHARACTER from
        /// rotating to match head direction - it has no effect on the actual headset tracking
        /// that drives what you see in VR, which is direct/physical and bypasses character
        /// logic entirely. This freezes the real tracked head rotation itself while paused,
        /// overriding SteamVR's own per-frame update (this runs in LateUpdate, confirmed to
        /// execute after SteamVR's tracking Update() call, so the override should stick).
        /// </summary>
        private void UpdateHeadFreezeForPause(bool isPaused)
        {
            if (StereoRender == null || StereoRender.Head == null) return;

            if (isPaused && !_headRotationFrozen)
            {
                _frozenHeadRotation = StereoRender.Head.localRotation;
                _headRotationFrozen = true;
            }
            else if (!isPaused && _headRotationFrozen)
            {
                _headRotationFrozen = false;
            }

            if (_headRotationFrozen)
            {
                StereoRender.Head.localRotation = _frozenHeadRotation;
            }
        }

        // V145: one bounded search campaign per REAL menu opening, not per frame.
        // Source-level V144 finding: the old code treated active PauseMenu hierarchy
        // as open while timeScale=1, searching for an absent HDRP Volume on EVERY frame.
        private bool _v145MenuOpen;
        private bool _v145ImplicitCloseLatch;
        private float _v145NextVolumeSearch;
        private float _v145VolumeSearchDeadline;
        private int _v145VolumeSearchAttempts;
        private int _v145TotalVolumeSearches;
        private double _v145TotalVolumeScanMs;
        private const int V145MaxVolumeSearchesPerOpen = 6;
        private const float V145VolumeRetryInterval = 0.30f;
        private const float V145VolumeSearchWindow = 1.75f;

        private void ClosePauseOnSceneTransitionV145(string reason)
        {
            bool hadMenuState = _v145MenuOpen || _pauseMenuBlurSuppressed;
            if (hadMenuState)
            {
                // Semantic equivalent of the MENU-CLOSED transition; NOT a
                // synthetic B input (which could cancel native loading).
                _v145ImplicitCloseLatch = Time.timeScale <= 0.0001f;
                RestorePauseVolumeV145("implicit-close:" + reason);
                ManualPauseFpsTraceV144.RecordV145Event("IMPLICIT_MENU_CLOSE",
                    "reason=" + reason + " latch=" + _v145ImplicitCloseLatch +
                    " timeScale=" + Time.timeScale.ToString("F3"));
            }
            _v145MenuOpen = false;
            _v169TitleBlurCampaignPending = reason.IndexOf("SonsTitleScene", StringComparison.Ordinal) >= 0;
            _pauseMenuBlurVolume = null;
            _pauseMenuCanvasObject = null;
            _v145VolumeSearchAttempts = 0;
            _v145NextVolumeSearch = float.PositiveInfinity;
            IsPauseMenuOpenConfirmed = false;
        }

        // V169A: this is intentionally NOT a restore. We permanently suppress only the
        // game's identified pause-menu blur Volume instance (never global post FX).
        // Restoring the weight on menu close was reintroducing blur in unrelated save/title
        // transition states. Unity owns its lifetime and destroys it on scene teardown.
        private void RestorePauseVolumeV145(string why)
        {
            if (_pauseMenuBlurSuppressed)
                ManualPauseFpsTraceV144.RecordV145Event("BLUR_LEFT_OFF",
                    "reason=" + why + " volume=" +
                    (_pauseMenuBlurVolume != null ? _pauseMenuBlurVolume.name : "destroyed"));
            _pauseMenuBlurSuppressed = false;
        }

        // V169A: one additional bounded search on entering the title, where the
        // native pause flag is false even though a transitioning blur can linger.
        private bool _v169TitleBlurCampaignPending;

        private void UpdatePauseMenuBlurSuppression(bool menuIsReallyOpen)
        {
            // V169A never restores a blur weight on menu close; this makes a
            // false flag opt-out only before the Volume has first been suppressed.
            float now = Time.unscaledTime;
            // V169A: keep existing bounded V145 lookup. On title entry a separate
            // one-shot campaign can run without treating the title as paused.
            bool titleBlurCampaign = _v169TitleBlurCampaignPending &&
                UsedSceneName == "SonsTitleScene";
            bool shouldScan = menuIsReallyOpen || titleBlurCampaign;
            if (!shouldScan)
            {
                if (_v145MenuOpen || _pauseMenuBlurSuppressed)
                {
                    RestorePauseVolumeV145("native-close");
                    ManualPauseFpsTraceV144.RecordV145Event("MENU_CLOSED",
                        "timeScale=" + Time.timeScale.ToString("F3") +
                        " searchesThisOpen=" + _v145VolumeSearchAttempts);
                }
                _v145MenuOpen = false;
                _v145NextVolumeSearch = float.PositiveInfinity;
                return;
            }

            if (!_v145MenuOpen)
            {
                _v145MenuOpen = true;
                _v145VolumeSearchAttempts = 0;
                _v145NextVolumeSearch = now;
                _v145VolumeSearchDeadline = now + V145VolumeSearchWindow;
                _pauseMenuBlurVolume = null; // re-acquire in current scene, once
                _pauseMenuBlurSuppressed = false;
                ManualPauseFpsTraceV144.RecordV145Event("MENU_OPENED",
                    "timeScale=" + Time.timeScale.ToString("F3") + " boundedScans=" + V145MaxVolumeSearchesPerOpen);
            }
            // V169A: the native system can reassign the weight on transitions.
            // Reassert on the ONE already-cached target, with no extra search.
            if (_pauseMenuBlurSuppressed)
            {
                if (_pauseMenuBlurVolume != null && _pauseMenuBlurVolume.weight > 0f)
                    _pauseMenuBlurVolume.weight = 0f;
                return;
            }
            if (!SuppressPauseMenuBlur) return;

            // Never search when the menu is closed. Never loop forever when the
            // pause-only Volume does not exist (e.g. loading an unpausable intro).
            if (_pauseMenuBlurVolume == null && now >= _v145NextVolumeSearch &&
                now <= _v145VolumeSearchDeadline &&
                _v145VolumeSearchAttempts < V145MaxVolumeSearchesPerOpen)
            {
                ++_v145VolumeSearchAttempts;
                ++_v145TotalVolumeSearches;
                _v145NextVolumeSearch = now + V145VolumeRetryInterval;
                long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    // V170: F9 proved this Volume exists in multiplayer even while
                    // FindObjectsOfType<Volume>() returned nothing six times. Resolve
                    // the observed native child directly; Transform.Find includes
                    // inactive children and avoids ~90 ms full-scene scans.
                    Transform nativePause = _pauseMenuCanvasObject;
                    if (nativePause == null && _screenSpaceCanvas != null)
                        nativePause = _screenSpaceCanvas.transform.Find("PauseMenu");
                    if (nativePause != null)
                    {
                        Transform blurChild = nativePause.Find("PauseMenuPostProcessingVolume");
                        if (blurChild != null)
                            _pauseMenuBlurVolume = blurChild.GetComponent<UnityEngine.Rendering.Volume>();
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("[V145 PauseVolume] discovery failed: " + e.Message);
                }
                double ms = 1000.0 * (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) /
                            System.Diagnostics.Stopwatch.Frequency;
                _v145TotalVolumeScanMs += ms;
                ManualPauseFpsTraceV144.RecordV145Event("VOLUME_SEARCH",
                    "attempt=" + _v145VolumeSearchAttempts + " total=" + _v145TotalVolumeSearches +
                    " costMs=" + ms.ToString("F3") + " cumulativeMs=" + _v145TotalVolumeScanMs.ToString("F3") +
                    " found=" + (_pauseMenuBlurVolume != null));
            }

            if (titleBlurCampaign && now > _v145VolumeSearchDeadline)
                _v169TitleBlurCampaignPending = false;

            if (_pauseMenuBlurVolume != null && !_pauseMenuBlurSuppressed)
            {
                try
                {
                    _pauseMenuBlurVolumeSavedWeight = _pauseMenuBlurVolume.weight;
                    _pauseMenuBlurVolume.weight = 0f;
                    _pauseMenuBlurSuppressed = true;
                    _v169TitleBlurCampaignPending = false;
                    ManualPauseFpsTraceV144.RecordV145Event("VOLUME_PERMANENTLY_ZEROED_V169A",
                        "before=" + _pauseMenuBlurVolumeSavedWeight.ToString("F3"));
                    MelonLogger.Msg("[V169A PauseVolume] Blur OFF for this Volume lifetime; never restore on menu close.");
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("[V145 PauseVolume] failed to suppress volume: " + e.Message);
                }
            }
        }

        private void UpdateCameraSettingsForPause(bool isPaused)
        {
            if (!ExperimentalRestoreCameraOnPause || UsedCamera == null) return;

            if (isPaused && !_cameraSettingsSavedForPause)
            {
                _savedClearFlags = UsedCamera.clearFlags;
                _savedCullingMask = UsedCamera.cullingMask;
                _savedDepth = UsedCamera.depth;
                _cameraSettingsSavedForPause = true;

                UsedCamera.clearFlags = CameraClearFlags.Skybox;
                UsedCamera.cullingMask = -1;
                UsedCamera.depth = 0;
                ManualPauseFpsTraceV144.RecordPauseStep("EXPERIMENTAL_CAM_RESTORE", "clearFlags=" + UsedCamera.clearFlags + " depth=" + UsedCamera.depth);
                MelonLogger.Msg("[VRPlayer] [Experimental] Restored standard camera clear-flags for pause menu (testing blur theory).");
            }
            else if (!isPaused && _cameraSettingsSavedForPause)
            {
                UsedCamera.clearFlags = _savedClearFlags;
                UsedCamera.cullingMask = _savedCullingMask;
                UsedCamera.depth = _savedDepth;
                _cameraSettingsSavedForPause = false;
            }
        }


        private bool _diagnosticCaptureSavedThisSession = false;

        /// <summary>
        /// Saves the captured frame to a plain PNG on disk once per pause session, so we can
        /// look at exactly what was captured on the PC directly - completely independent of
        /// any VR quad placement/scale issues. This isolates "is the capture correct" from
        /// "is the VR presentation correct".
        /// </summary>
        private void SaveDiagnosticCaptureOnce()
        {
            if (_diagnosticCaptureSavedThisSession) return;
            _diagnosticCaptureSavedThisSession = true;

            long v144ReadbackStart = System.Diagnostics.Stopwatch.GetTimestamp();
            ManualPauseFpsTraceV144.RecordPauseStep("PAUSE_SCREENSHOT_READBACK_BEGIN", "existing SaveDiagnosticCaptureOnce - timing only");
            try
            {
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = _flatMenuCaptureRT;

                Texture2D tex = new Texture2D(_flatMenuCaptureRT.width, _flatMenuCaptureRT.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, _flatMenuCaptureRT.width, _flatMenuCaptureRT.height), 0, 0);
                tex.Apply();

                RenderTexture.active = previous;

                byte[] png = tex.EncodeToPNG();
                string path = Path.Combine(Application.persistentDataPath, "sonsvr_menu_capture.png");
                File.WriteAllBytes(path, png);
                double v144ReadbackMs = 1000.0 * (System.Diagnostics.Stopwatch.GetTimestamp() - v144ReadbackStart) / System.Diagnostics.Stopwatch.Frequency;
                ManualPauseFpsTraceV144.RecordPauseStep("PAUSE_SCREENSHOT_READBACK_DONE", "elapsedMs=" + v144ReadbackMs.ToString("F3") + " path=" + path);
                MelonLogger.Msg($"[VRPlayer] Diagnostic menu capture saved to: {path}");

                UnityEngine.Object.Destroy(tex);
            }
            catch (Exception e)
            {
                ManualPauseFpsTraceV144.RecordPauseStep("PAUSE_SCREENSHOT_READBACK_FAILED", e.GetType().Name + ": " + e.Message);
                MelonLogger.Warning($"[VRPlayer] Failed to save diagnostic capture: {e.Message}");
            }
        }

        private GameObject _customLoadingCanvasGo;
        private UnityEngine.UI.Text _customLoadingText;
        private float _loadingLetterTimer = 0f;
        private int _loadingLetterIndex = 0;
        public static bool CustomLoadingScreenEnabled = false; // V111: superseded by the fast layer-5 three-line loader; old letter-by-letter custom loader hard-off.
        public static float CustomLoadingScreenYOffset = 0.0f; // untested starting guess, matches Options/Multiplayer default

        /// <summary>
        /// The real LoadingIndictor has no Canvas and no UIDocument anywhere in its hierarchy
        /// (confirmed via diagnostic), so there's nothing to convert to world-space the way
        /// Options/Multiplayer/Minimap work. Rather than chase another capture technique after
        /// one produced garbage and another froze the game, this builds a fully self-authored
        /// substitute instead - the same "draw our own synthetic replacement" pattern already
        /// proven for the VR menu cursor, just applied to a loading message. No screen capture,
        /// no Camera.Render(), no risk of the freeze that hit the flat-menu-view attempt.
        /// </summary>
        private GameObject FindLoadingIndictorRoot()
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t == null || t.name != "LoadingIndictor") continue;
                if (t.parent == null) return t.gameObject;
            }
            return null;
        }

        private GameObject _skipHintOverlayGo;
        private UnityEngine.UI.Text _skipHintOverlayText;
        private float _lastSkipHintCheckTime = 0f;

        /// <summary>
        /// PIVOT per user's own suggestion, after four fix attempts on the native Y-skip prompt
        /// each confirmed correct in the underlying data (active state, alpha, Animator
        /// parameters, canvas rebuild) yet had zero visible effect across many test rounds.
        /// Rather than keep fighting an opaque native system, build a fully custom overlay we
        /// control end-to-end, reusing the exact same proven technique already working for the
        /// custom loading screen (EnsureCustomLoadingScreenCreated just above). Parented directly
        /// to the real VR head with a fixed local offset, so it always sits in a consistent,
        /// comfortable spot without needing any per-frame position-chasing logic - avoiding
        /// every positioning pitfall already hit in this saga. Shown whenever the native
        /// SkipCutscene object is active - a purely READ-ONLY check, since its active-state has
        /// tracked correctly throughout even though its own rendering never did - which should
        /// cover all three skippable intro cutscenes (helicopter+laptop, helicopter crash, and
        /// waking up), not just the laptop one, since they very likely share this same object.
        /// </summary>
        private void EnsureSkipHintOverlayCreated()
        {
            if (_skipHintOverlayGo != null) return;
            if (StereoRender == null || StereoRender.Head == null) return;

            _skipHintOverlayGo = new GameObject("SonsVR_SkipHintOverlay");
            var canvas = _skipHintOverlayGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            _skipHintOverlayGo.layer = LayerMask.NameToLayer("Default");

            var rt = _skipHintOverlayGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(800f, 150f);
            _skipHintOverlayGo.transform.localScale = Vector3.one * 0.0003f; // user-tuned, smaller than the original 0.001f

            var textGo = new GameObject("SkipHintText");
            textGo.transform.SetParent(_skipHintOverlayGo.transform, false);
            _skipHintOverlayText = textGo.AddComponent<UnityEngine.UI.Text>();
            _skipHintOverlayText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _skipHintOverlayText.fontSize = 36;
            _skipHintOverlayText.alignment = TextAnchor.MiddleCenter;
            _skipHintOverlayText.color = Color.white;
            _skipHintOverlayText.text = "HOLD Y (controller) or S (keyboard) to skip"; // user-tuned wording
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.sizeDelta = Vector2.zero;

            _skipHintOverlayGo.transform.SetParent(StereoRender.Head, false);
            _skipHintOverlayGo.transform.localPosition = new Vector3(0f, -0.1f, 0.6f);
            _skipHintOverlayGo.transform.localRotation = Quaternion.identity;

            _skipHintOverlayGo.SetActive(false);
            MelonLogger.Msg("[VRPlayer] Skip-hint overlay created.");
        }

        private void UpdateSkipHintOverlay()
        {
            if (StereoRender == null || StereoRender.Head == null) return;
            if (_screenSpaceCanvas == null) return;

            EnsureSkipHintOverlayCreated();
            if (_skipHintOverlayGo == null) return;

            if (Time.time - _lastSkipHintCheckTime < 0.1f) return;
            _lastSkipHintCheckTime = Time.time;

            // Purely read-only checks against the native object - we no longer modify it at all.
            // User's request: mirror the ORIGINAL show/hide timing (flash in on a Y press, hold
            // a few seconds, fade out) rather than staying up constantly. Since we stopped
            // fighting the native CanvasGroup.alpha, it's free to naturally rise/fall exactly
            // when the game intends the hint to show - a ready-made timing signal we can just
            // read instead of reinventing. Falls back to activeInHierarchy if the CanvasGroup
            // isn't found for some reason, so the overlay never gets stuck permanently hidden.
            Transform cutscenePrompts = _screenSpaceCanvas.transform.Find("CutscenePrompts");
            Transform skipCutscene = cutscenePrompts != null ? cutscenePrompts.Find("SkipCutscene") : null;
            Transform cutscenePrompt = skipCutscene != null ? skipCutscene.Find("CutscenePrompt") : null;
            Transform animRoot = cutscenePrompt != null ? cutscenePrompt.Find("AnimationRoot") : null;
            var cg = animRoot != null ? animRoot.GetComponent<CanvasGroup>() : null;

            bool shouldShow;
            if (cg != null)
            {
                shouldShow = cg.alpha > 0.05f;
            }
            else
            {
                shouldShow = skipCutscene != null && skipCutscene.gameObject.activeInHierarchy;
            }

            if (_skipHintOverlayGo.activeSelf != shouldShow)
            {
                _skipHintOverlayGo.SetActive(shouldShow);
            }
        }

        // V102b/V103 reusable headset-visible experimental cue infrastructure.
        // V104 production leaves it dormant; future precision tests can re-enable it without log watching.
        // IMPORTANT: this overlay is NEVER shown while a truth bank is collecting. It appears
        // only after GAMEPLAY sample 6 has finished both GPU readbacks, then disappears the
        // instant the tarp's <=100 m far clip is detected; it reappears only after BOTH final
        // TARP readbacks finish. Thus the convenience cue cannot enter the measured eye images.
        private GameObject _aerV102CueOverlayGo;
        private UnityEngine.UI.Text _aerV102CueOverlayText;
        private int _aerV102CueRevisionSeen = -1;

        private void EnsureAerV102HmdCueOverlayCreated()
        {
            if (_aerV102CueOverlayGo != null) return;
            if (StereoRender == null || StereoRender.Head == null) return;

            _aerV102CueOverlayGo = new GameObject("SonsVR_AER_V102b_CueOverlay");
            var canvas = _aerV102CueOverlayGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 5000;
            _aerV102CueOverlayGo.layer = LayerMask.NameToLayer("Default");

            var rt = _aerV102CueOverlayGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(1200f, 240f);
            _aerV102CueOverlayGo.transform.localScale = Vector3.one * 0.00032f;

            var bgGo = new GameObject("Background");
            bgGo.transform.SetParent(_aerV102CueOverlayGo.transform, false);
            var bg = bgGo.AddComponent<UnityEngine.UI.Image>();
            bg.color = new Color(0f, 0f, 0f, 0.78f);
            var bgRt = bgGo.GetComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero;
            bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero;
            bgRt.offsetMax = Vector2.zero;

            var textGo = new GameObject("CueText");
            textGo.transform.SetParent(_aerV102CueOverlayGo.transform, false);
            _aerV102CueOverlayText = textGo.AddComponent<UnityEngine.UI.Text>();
            _aerV102CueOverlayText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _aerV102CueOverlayText.fontSize = 54;
            _aerV102CueOverlayText.fontStyle = FontStyle.Bold;
            _aerV102CueOverlayText.alignment = TextAnchor.MiddleCenter;
            _aerV102CueOverlayText.color = new Color(0.25f, 1.0f, 0.35f, 1f);
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(20f, 10f);
            textRt.offsetMax = new Vector2(-20f, -10f);

            _aerV102CueOverlayGo.transform.SetParent(StereoRender.Head, false);
            _aerV102CueOverlayGo.transform.localPosition = new Vector3(0f, 0.17f, 0.65f);
            _aerV102CueOverlayGo.transform.localRotation = Quaternion.identity;
            _aerV102CueOverlayGo.SetActive(false);
            MelonLogger.Msg("[VRPlayer][AER V103] shared HMD test cue overlay created.");
        }

        private void UpdateAerV102HmdCueOverlay()
        {
            if (!global::SonsVRMod.StereoRender.AerV102OcclusionForensicsEnabled &&
                !global::SonsVRMod.StereoRender.AerV103DepthGuardFillPerceptualTestEnabled &&
                !global::SonsVRMod.PerformanceForensicsV106.CueEnabled &&
                !global::SonsVRMod.AerCalibrationV139.Enabled &&
                !global::SonsVRMod.ManualPauseFpsTraceV144.Enabled) return;
            if (StereoRender == null || StereoRender.Head == null) return;

            EnsureAerV102HmdCueOverlayCreated();
            if (_aerV102CueOverlayGo == null || _aerV102CueOverlayText == null) return;

            int revision = global::SonsVRMod.StereoRender.AerV102HmdCueRevision;
            if (_aerV102CueRevisionSeen == revision) return;
            _aerV102CueRevisionSeen = revision;

            string cue = global::SonsVRMod.StereoRender.AerV102HmdCueText ?? "";
            _aerV102CueOverlayText.text = cue;
            bool show = !string.IsNullOrEmpty(cue);
            if (_aerV102CueOverlayGo.activeSelf != show)
                _aerV102CueOverlayGo.SetActive(show);
            ManualPauseFpsTraceV144.RecordCueRendered(show,
                "revision=" + revision + " overlayActive=" + _aerV102CueOverlayGo.activeInHierarchy +
                " textLength=" + cue.Length + " parent=" + (_aerV102CueOverlayGo.transform.parent != null ? _aerV102CueOverlayGo.transform.parent.name : "none") +
                " worldPos=" + _aerV102CueOverlayGo.transform.position +
                " canvas=" + (_aerV102CueOverlayGo.GetComponent<Canvas>() != null));
        }

        private void EnsureCustomLoadingScreenCreated()
        {
            if (_customLoadingCanvasGo != null) return;

            _customLoadingCanvasGo = new GameObject("SonsVR_CustomLoadingScreen");
            var canvas = _customLoadingCanvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            _customLoadingCanvasGo.layer = LayerMask.NameToLayer("Default");

            var rt = _customLoadingCanvasGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(1000f, 600f);
            // Physical size ~4m x 2.4m (doubled from the first attempt per user feedback - map
            // was still visible around the edges) - at ~1m viewing distance this covers well
            // beyond typical headset FOV, hiding the loading world underneath.
            _customLoadingCanvasGo.transform.localScale = Vector3.one * 0.004f;

            var bgGo = new GameObject("Background");
            bgGo.transform.SetParent(_customLoadingCanvasGo.transform, false);
            var bgImage = bgGo.AddComponent<UnityEngine.UI.Image>();
            bgImage.color = Color.black;
            var bgRt = bgGo.GetComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero;
            bgRt.anchorMax = Vector2.one;
            bgRt.sizeDelta = Vector2.zero;

            var textGo = new GameObject("LoadingText");
            textGo.transform.SetParent(_customLoadingCanvasGo.transform, false);
            _customLoadingText = textGo.AddComponent<UnityEngine.UI.Text>();
            _customLoadingText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _customLoadingText.fontSize = 48; // reduced from 72 per user feedback
            _customLoadingText.alignment = TextAnchor.MiddleCenter;
            _customLoadingText.color = Color.white;
            _customLoadingText.text = "";
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.sizeDelta = Vector2.zero;

            _customLoadingCanvasGo.SetActive(false);
            MelonLogger.Msg("[VRPlayer] Custom loading screen created.");
        }

        private float _lastLoadingActiveCheckTime = 0f;
        private bool _cachedLoadingActive = false;

        private void UpdateCustomLoadingScreen()
        {
            if (!CustomLoadingScreenEnabled)
            {
                if (_customLoadingCanvasGo != null && _customLoadingCanvasGo.activeSelf) _customLoadingCanvasGo.SetActive(false);
                return;
            }
            if (StereoRender == null || StereoRender.Head == null) return;
            // Same scene gate that fixed the earlier jitter bug (ConvertTitleMenuCanvasesToWorldSpace
            // running unconditionally during normal gameplay) - LoadingIndictor is confirmed to
            // only ever be active while UsedSceneName == "SonsTitleScene" (verified across
            // multiple logs), so this stays dormant during actual gameplay.
            if (UsedSceneName != "SonsTitleScene")
            {
                if (_customLoadingCanvasGo != null && _customLoadingCanvasGo.activeSelf) _customLoadingCanvasGo.SetActive(false);
                return;
            }

            // Throttling the FindObjectsOfType scan itself (not the animation/positioning below,
            // which is cheap) - a loading transition is already instantiating a huge amount of
            // scene content, so running a full-scene Transform scan every single frame here
            // risked the exact same jitter mistake fixed earlier for the canvas conversion.
            if (Time.unscaledTime - _lastLoadingActiveCheckTime > 0.5f)
            {
                _lastLoadingActiveCheckTime = Time.unscaledTime;
                _cachedLoadingActive = FindLoadingIndictorRoot() != null;
            }
            bool loadingActive = _cachedLoadingActive;

            if (loadingActive)
            {
                EnsureCustomLoadingScreenCreated();
                if (!_customLoadingCanvasGo.activeSelf) _customLoadingCanvasGo.SetActive(true);

                Vector3 flatForward = StereoRender.Head.forward;
                flatForward.y = 0f;
                if (flatForward.sqrMagnitude < 0.001f) flatForward = Vector3.forward;
                flatForward.Normalize();
                Vector3 pos = StereoRender.Head.position + flatForward * 1.0f + Vector3.up * CustomLoadingScreenYOffset;
                _customLoadingCanvasGo.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(flatForward, Vector3.up));

                _loadingLetterTimer += Time.unscaledDeltaTime;
                if (_loadingLetterTimer >= 1f)
                {
                    _loadingLetterTimer = 0f;
                    _loadingLetterIndex = (_loadingLetterIndex + 1) % 8; // 0=empty, 1-7=L through LOADING, loops back to empty
                    if (_customLoadingText != null) _customLoadingText.text = "LOADING".Substring(0, _loadingLetterIndex);
                }
            }
            else if (_customLoadingCanvasGo != null && _customLoadingCanvasGo.activeSelf)
            {
                _customLoadingCanvasGo.SetActive(false);
                _loadingLetterTimer = 0f;
                _loadingLetterIndex = 0;
            }
        }

        private void EnsureFlatMenuQuad()
        {
            if (_flatMenuQuad != null) return;

            int width = Mathf.Max(Screen.width, 2);
            int height = Mathf.Max(Screen.height, 2);
            _flatMenuCaptureRT = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            _flatMenuCaptureRT.Create();

            _flatMenuQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _flatMenuQuad.name = "SonsVR_FlatMenuQuad";

            var collider = _flatMenuQuad.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            // Scale/crop are set dynamically every frame in UpdateFlatMenuView (differs between
            // the full-screen pause-menu case and the cropped-to-bottom-strip title-screen case)
            // rather than being fixed here at creation time.

            var renderer = _flatMenuQuad.GetComponent<Renderer>();
            if (renderer != null)
            {
                // CONFIRMED (extensive testing earlier this session, aim reticle work): HDRP/
                // Unlit materials created at runtime do not render their color in this game's
                // shipped build, even though geometry/shadows still work - only HDRP/Lit was
                // confirmed to actually display. Using that here too instead of Unlit.
                Shader shader = Shader.Find("HDRP/Lit");
                if (shader == null) shader = Shader.Find("Unlit/Texture");
                if (shader != null)
                {
                    var mat = new Material(shader);
                    if (mat.HasProperty("_BaseColorMap")) mat.SetTexture("_BaseColorMap", _flatMenuCaptureRT);
                    if (mat.HasProperty("_UnlitColorMap")) mat.SetTexture("_UnlitColorMap", _flatMenuCaptureRT);
                    if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", _flatMenuCaptureRT);
                    if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", _flatMenuCaptureRT);
                    // Same occlusion test as the binoculars quad - see that block's comment.
                    mat.renderQueue = 4000;
                    renderer.material = mat;
                }
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            _flatMenuQuad.SetActive(false);
            MelonLogger.Msg("[VRPlayer] Flat menu view quad created.");
        }

        private void DebugScanKeywordObjects()
        {
            if (!DebugLogCanvasChanges) return;
            if (Time.time - _lastKeywordScanTime < 1.5f) return;
            _lastKeywordScanTime = Time.time;

            // NOTE: this walks every Transform in the scene by name - it's a temporary
            // diagnostic aid only (expensive in an open world). Set DebugLogCanvasChanges =
            // false once the campfire cook UI's object path has been identified.
            Transform[] all = UnityEngine.Object.FindObjectsOfType<Transform>();
            var current = new System.Collections.Generic.HashSet<string>();
            foreach (var t in all)
            {
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                string lname = t.name.ToLowerInvariant();
                bool matches = false;
                foreach (var kw in _uiScanKeywords)
                {
                    if (lname.Contains(kw)) { matches = true; break; }
                }
                if (!matches) continue;
                current.Add(GetHierarchyPath(t));
            }

            foreach (var name in current)
            {
                if (!_lastActiveKeywordObjects.Contains(name))
                {
                    MelonLogger.Msg($"[VRPlayer][UIScan] Matching object ACTIVE: {name}");
                }
            }
            foreach (var name in _lastActiveKeywordObjects)
            {
                if (!current.Contains(name))
                {
                    MelonLogger.Msg($"[VRPlayer][UIScan] Matching object inactive: {name}");
                }
            }
            _lastActiveKeywordObjects = current;
        }

        public static float ItemInfoPanelYOffset = 0f; // user-tuned in-game, confirmed correct - likely no longer needed at the old value now that positioning uses full head pitch (see ConvertItemInfoPanelToWorldSpace)
        public static float ItemInfoPanelWorldHeight = 0.95f; // bumped up a bit from 0.3f per feedback that it was a little small - requires a full game restart to take effect
        public static float ItemInfoPanelDistance = 0.4f; // was hardcoded at 0.6 - per user's follow-up request to bring this closer, since it reads as small and far away

        // Per user's request: tilts the panel backward (top tips away from the player) to better
        // match a downward gaze at a ground tarp, where the panel otherwise reads at close to a
        // 90-degree mismatch against an upright orientation. Positive degrees tilt it back/up;
        // 0 keeps the original, straight-upright behavior.
        public static float ItemInfoPanelTiltDegrees = 20f;

        /// <summary>
        /// CONFIRMED via diagnostic: the item info/tooltip panel (durability bars, water
        /// amount, etc.) sits under a Canvas that reports renderMode=WorldSpace, but its actual
        /// world position was (800, 540, 0) - a raw, unconverted screen-pixel coordinate - and
        /// its lossyScale was (0, 0, 0), meaning it was rendered at literally zero size. Nobody
        /// ever applied real-world scale/position math to it. This is a genuine, uninitialized
        /// Canvas needing the exact same fix already proven for Options/Multiplayer/Minimap -
        /// NOT the same category as the pause menu (confirmed separately to not be Canvas-based
        /// at all). Adding our OWN nested Canvas directly on InventoryItemInfoView itself
        /// (rather than touching the shared ScreenSpaceCanvas ancestor, which PauseMenu and
        /// other siblings depend on) keeps this fully isolated from anything else.
        /// Runs unconditionally (not gated to the title screen scene, since this panel only
        /// ever appears during actual gameplay) - cheap because GameObject.Find on this exact
        /// path returns quickly null whenever the panel isn't currently active.
        /// </summary>
        private void ConvertItemInfoPanelToWorldSpace()
        {
            // V116: the entire native ScreenSpaceCanvas already lives on the unified HUD world
            // plane. Creating a SECOND nested WorldSpace canvas here makes InventoryItemInfoView
            // head-locked at 0.4m, which is exactly why V115 showed a huge black popup in the
            // player's face instead of beside the selected tarp item. Leave its authored layout
            // untouched when the unified HUD path owns the parent canvas.
            if (UnifiedHudWorldPlaneV116.Enabled) return;
            if (StereoRender == null || StereoRender.Head == null) return;

            GameObject infoView = GameObject.Find("PlayerStandin/PlayerUI/ScreenSpaceCanvas/ToggleableHUD/Inventory/InventoryItemInfoView");
            if (infoView == null) return;

            var canvas = infoView.GetComponent<Canvas>();
            if (canvas == null)
            {
                canvas = infoView.AddComponent<Canvas>();
                canvas.overrideSorting = true;
            }

            if (canvas.renderMode != RenderMode.WorldSpace)
            {
                canvas.renderMode = RenderMode.WorldSpace;
                if (UsedCamera != null) canvas.worldCamera = UsedCamera;
                canvas.gameObject.layer = LayerMask.NameToLayer("Default");

                var canvasRectForScale = canvas.GetComponent<RectTransform>();
                float canvasLogicalHeight = (canvasRectForScale != null && canvasRectForScale.rect.height > 1f) ? canvasRectForScale.rect.height : 1080f;
                canvas.transform.localScale = Vector3.one * (ItemInfoPanelWorldHeight / canvasLogicalHeight);

                var scaler = canvas.GetComponent<CanvasScaler>();
                if (scaler != null) scaler.enabled = false;
            }
            // REVERTED: two separate attempts at reapplying localScale every poll (one
            // re-measuring rect.height each time, one using a cached height measured once)
            // BOTH made the panel fully invisible, confirming the problem isn't measurement
            // timing - it's the repeated overwrite itself. Most likely something native to this
            // panel (a pop-in animation, a layout/content-size-fitter) also drives its own
            // scale every frame, and continuously fighting it nets out to invisible. Reverted to
            // setting scale exactly once, matching the version that was confirmed working.
            // ItemInfoPanelWorldHeight can still be tuned, but requires a full game restart to
            // take effect (a fresh canvas instance), not just recompiling and reopening the tarp.

            // Reposition every time it's shown (not just once), same as the other menus, so it
            // faces wherever the player is currently looking rather than a stale direction from
            // a previous showing.
            //
            // FIXED (per user's request: "stay centered in view"). Previously used a flattened
            // (pitch-ignoring) direction for POSITION too, so looking down at a ground tarp left
            // the panel positioned for a straight-ahead look, not actually in front of the
            // player's real, downward gaze. Now uses the full head direction (including pitch)
            // for position, while keeping a flattened direction for the panel's own base
            // orientation (so it doesn't itself tip upside-down at extreme pitch).
            Vector3 lookDir = StereoRender.Head.forward;
            if (lookDir.sqrMagnitude < 0.001f) lookDir = Vector3.forward;
            lookDir.Normalize();

            Vector3 flatForward = new Vector3(lookDir.x, 0f, lookDir.z);
            if (flatForward.sqrMagnitude < 0.001f) flatForward = Vector3.forward;
            flatForward.Normalize();

            Vector3 pos = StereoRender.Head.position + lookDir * ItemInfoPanelDistance + Vector3.up * ItemInfoPanelYOffset;

            // NEW (per user's request): the panel reads at close to a 90-degree mismatch when
            // looking down at a ground tarp - an upright panel against a downward gaze. Tilting
            // it backward (top tips away from the player, like leaning a laptop screen back) by a
            // tunable amount to better match that angle. Applied around the panel's OWN local
            // right axis (derived from the base, flat-facing rotation), not a world axis, so the
            // tilt behaves the same regardless of which direction the player is facing.
            Quaternion baseRot = Quaternion.LookRotation(flatForward, Vector3.up);
            Quaternion finalRot = Quaternion.AngleAxis(ItemInfoPanelTiltDegrees, baseRot * Vector3.right) * baseRot;
            canvas.transform.SetPositionAndRotation(pos, finalRot);

            UpdateItemInfoPanelBackground(canvas);
        }

        public static bool ItemInfoPanelBackgroundEnabled = true;
        public static float ItemInfoPanelBackgroundPadding = 350f; // user-tuned in-game, confirmed correct
        public static float ItemInfoPanelBackgroundOpacity = 0.75f;
        private GameObject _itemInfoPanelBackgroundObject;

        /// <summary>
        /// Same idea as the tarp tutorial's own black background panel (UpdateTarpTutorialBackground)
        /// - the busy tarp/inventory contents behind this popup (durability bars, water amount, etc.)
        /// can make the white text hard to read. Simpler than the tutorial case, though: this
        /// canvas only ever holds this one popup (not several at different positions), so a single
        /// panel sized to the canvas's own rect - not a fixed screen-fraction size - covers it
        /// correctly regardless of how big the actual content is.
        /// </summary>
        private void UpdateItemInfoPanelBackground(Canvas canvas)
        {
            if (!ItemInfoPanelBackgroundEnabled || canvas == null) return;

            if (_itemInfoPanelBackgroundObject == null)
            {
                _itemInfoPanelBackgroundObject = new GameObject("SonsVR_ItemInfoPanelBackground");
                _itemInfoPanelBackgroundObject.transform.SetParent(canvas.transform, false);
                _itemInfoPanelBackgroundObject.transform.SetAsFirstSibling();

                var image = _itemInfoPanelBackgroundObject.AddComponent<UnityEngine.UI.Image>();
                image.color = new Color(0f, 0f, 0f, ItemInfoPanelBackgroundOpacity);
                image.raycastTarget = false; // purely visual

                var bgRt = _itemInfoPanelBackgroundObject.GetComponent<RectTransform>();
                bgRt.anchorMin = new Vector2(0.5f, 0.5f);
                bgRt.anchorMax = new Vector2(0.5f, 0.5f);
                bgRt.pivot = new Vector2(0.5f, 0.5f);
                bgRt.anchoredPosition = Vector2.zero;
            }

            // Sized every poll (not just once), since ItemInfoPanelBackgroundPadding is meant to
            // be a live-tunable value, and this is a cheap operation regardless.
            var canvasRt = canvas.GetComponent<RectTransform>();
            var rt = _itemInfoPanelBackgroundObject.GetComponent<RectTransform>();
            if (canvasRt != null)
            {
                rt.sizeDelta = new Vector2(canvasRt.rect.width + ItemInfoPanelBackgroundPadding, canvasRt.rect.height + ItemInfoPanelBackgroundPadding);
            }

            if (!_itemInfoPanelBackgroundObject.activeSelf) _itemInfoPanelBackgroundObject.SetActive(true);
        }

        public static float BinocularsWorldHeight = 0.7f; // user-tuned in-game, confirmed correct
        public static float BinocularsDistance = 0.15f; // user-tuned in-game, confirmed correct
        public static float BinocularsYOffset = -0.04f; // user-tuned in-game, confirmed correct

        /// <summary>
        /// CONFIRMED via DebugScanActiveCanvases: LocalPlayer/PlayerAnimator/LookObject/
        /// MainCameraFP/Binoculars/Canvas activates the instant binoculars are raised and stays
        /// ScreenSpaceOverlay the whole time (unlike PlayerUI/WorldSpaceCanvas and
        /// PlayerUI/ScreenSpaceCanvas right next to it in the same log, both correctly WorldSpace)
        /// - never included in the mod's conversion sweep, so it simply never renders in VR at
        /// all. Same category of bug as Options/Multiplayer/Minimap/ItemInfoPanel before their
        /// fixes, using the same proven formula (most refined version, from ItemInfoPanel: scale
        /// set once using the canvas's own measured RectTransform height rather than assuming
        /// Screen.height, reposition every frame). Unlike ItemInfoPanel (a stable menu, tracks
        /// flat yaw only so it doesn't tilt), this tracks the FULL head rotation including pitch -
        /// a binocular view should follow exactly where you're looking, the same way the main HUD
        /// canvas does, not stay artificially level.
        /// </summary>
        // NEW APPROACH: instead of positioning the real binoculars Canvas (mask + circles) in
        // WorldSpace and trying to feed a genuinely zoomed render behind it, capture the WHOLE
        // flat screen - which already shows the mask/circles/zoom all correctly composited
        // together as one 2D image, since flat rendering respects the game's FOV-based zoom -
        // and display that single captured image on a quad in front of the eyes. Avoids the
        // entire HDRP second-camera pipeline that caused the tarp regression, since this doesn't
        // render anything new - it only reads pixels already being computed for the existing flat
        // view. Default OFF - test deliberately before enabling.
        public static bool EnableBinocularsFlatCapture = true; // enabled for active testing

        private void ConvertBinocularsCanvasToWorldSpace()
        {
            if (EnableBinocularsFlatCapture)
            {
                // Mutually exclusive with the flat-capture approach below - the real Canvas stays
                // untouched/native (ScreenSpaceOverlay), rendering normally into the flat view we
                // capture, rather than also being separately positioned in WorldSpace (which would
                // show a second, redundant overlay alongside the captured quad).
                UpdateBinocularsFlatCapture();
                return;
            }

            if (StereoRender == null || StereoRender.Head == null) return;

            GameObject binocularsCanvasGo = GameObject.Find("LocalPlayer/PlayerAnimator/LookObject/MainCameraFP/Binoculars/Canvas");
            if (binocularsCanvasGo == null)
            {
                // CRITICAL FIX: the zoom camera was created once and left enabled=true forever
                // afterward, even once binoculars were put away - user confirmed this broke the
                // tarp's item-highlight system (which explicitly relies on Camera.main for its
                // native OnMouseEnter/Exit-driven detection, per an existing comment elsewhere in
                // this file), almost certainly because a second live camera confused that lookup.
                // Disabling it whenever binoculars aren't actually up stops it from interfering
                // with anything else for the rest of the session.
                if (_binocularsZoomCamera != null && _binocularsZoomCamera.enabled)
                {
                    _binocularsZoomCamera.enabled = false;
                    MelonLogger.Msg("[VRPlayer][BinocularsZoom] Disabled zoom camera (binoculars put away)");
                }
                return;
            }

            var canvas = binocularsCanvasGo.GetComponent<Canvas>();
            if (canvas == null) return;

            if (canvas.renderMode != RenderMode.WorldSpace)
            {
                canvas.renderMode = RenderMode.WorldSpace;
                if (UsedCamera != null) canvas.worldCamera = UsedCamera;
                canvas.gameObject.layer = LayerMask.NameToLayer("Default");

                var canvasRectForScale = canvas.GetComponent<RectTransform>();
                float canvasLogicalHeight = (canvasRectForScale != null && canvasRectForScale.rect.height > 1f) ? canvasRectForScale.rect.height : 1080f;
                canvas.transform.localScale = Vector3.one * (BinocularsWorldHeight / canvasLogicalHeight);

                var scaler = canvas.GetComponent<CanvasScaler>();
                if (scaler != null) scaler.enabled = false;
            }
            // Same gotcha as ItemInfoPanel: only set scale inside the renderMode guard above
            // (once), never reapply every poll - doing so made that panel invisible, likely due
            // to something native also driving scale every frame. BinocularsWorldHeight can still
            // be tuned, but (per that same lesson) requires a full game restart to take effect.

            Quaternion desiredRotation = Quaternion.LookRotation(StereoRender.Head.forward, StereoRender.Head.up);
            Vector3 pos = StereoRender.Head.position + StereoRender.Head.forward * BinocularsDistance + StereoRender.Head.up * BinocularsYOffset;
            canvas.transform.SetPositionAndRotation(pos, desiredRotation);

            EnsureBinocularsZoomCamera(binocularsCanvasGo);
        }

        private Camera _binocularsZoomCamera;
        private RenderTexture _binocularsZoomTexture;
        private RawImage _binocularsZoomRawImage;
        private Camera _mainCameraFP;
        private UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData _binocularsZoomHDData;
        private UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData _mainCameraFPHDData;

        /// <summary>
        /// CONFIRMED via DebugDumpBinocularsCanvasStructure: the binoculars canvas is just a
        /// static mask (BinocularOutline, an Image with the two-circle cutout shape) plus two
        /// solid black fill panels (LeftPad/RightPad) - no RawImage, no camera feed, nothing
        /// dynamic. The "zoom" the player sees through the cutouts is purely the normal 3D scene
        /// showing through the mask's transparent circles, magnified only via MainCameraFP's
        /// fieldOfView (95->40 while aiming) - which VR ignores entirely (confirmed by user: zoom
        /// shows on the flat monitor mirror, not in the headset). There is no existing zoom render
        /// to redirect; this builds one from scratch: a dedicated camera matching the actual head
        /// look direction and the game's own current fieldOfView, rendering to a texture that's
        /// displayed via a new RawImage inserted behind the existing mask (so the mask still
        /// creates the correct cutout shape, but what shows through it is now a genuinely
        /// magnified render instead of the unmagnified normal view).
        /// Built as a completely bare, minimal camera (new GameObject + Camera +
        /// HDAdditionalCameraData added explicitly) rather than cloning MainCameraFP - an earlier
        /// version cloned it via Instantiate() and the log showed the clone getting silently
        /// destroyed within moments every single time (recreated hundreds of times a second),
        /// almost certainly some script that came along with the clone (a singleton-camera or
        /// VR-hookup script, common on a game's main camera) killing our duplicate via its own
        /// Awake/Start before a stable reference could be kept. Building from scratch avoids
        /// inheriting any of MainCameraFP's other scripts entirely, sidestepping that risk.
        /// </summary>
        // MASTER SWITCH: temporarily false while the zoom camera's interference with the tarp
        // multi-select system (still unconfirmed exact mechanism - Camera.main is now definitively
        // ruled out via direct logging, timing suggests the tarp was tested while the zoom camera
        // was still actively rendering rather than after being disabled, so a performance/timing
        // cause hasn't been ruled out) gets investigated separately, without holding a core,
        // frequently-used feature hostage in the meantime. With this false, the binoculars canvas
        // still gets correctly positioned/scaled in VR (that part is confirmed safe and working on
        // its own) - only the second-camera zoom render is skipped.
        public static bool EnableBinocularsZoomCamera = false;

        private GameObject _binocularsFlatQuad;
        private RenderTexture _binocularsFlatCaptureRT;
        private bool _binocularsFlatCaptureCoroutineStarted = false;
        private bool _binocularsFlatCaptureActive = false; // set from the main poll, read by the coroutine

        public static float BinocularsFlatQuadHeight = 0.9f; // meters - matches the existing pause-menu flat quad's proven starting size
        public static float BinocularsFlatQuadDistance = 0.05f; // TEMP occlusion test: pushed extremely close (was 1.3f) - nothing else should plausibly render this close to the eyes, so if it still doesn't show up, occlusion is definitively ruled out too

        /// <summary>
        /// User's idea: rather than positioning the real binoculars mask in WorldSpace and trying
        /// to feed it a genuinely zoomed render (the second-camera approach that broke the tarp),
        /// capture the WHOLE flat screen - which already shows the mask/circles/zoom correctly
        /// composited as one image, since flat rendering respects the game's FOV-based zoom - and
        /// display that single image on a quad. Reuses the exact quad/material setup already
        /// proven correct for the pause-menu flat-view feature (HDRP/Lit confirmed as the only
        /// shader that actually displays color in this build; CreatePrimitive avoids the
        /// IL2CPP GameObject-constructor issue hit earlier in this project) and the same proven
        /// facing/orientation math (a plain CreatePrimitive Quad's textured side needs a 180-degree
        /// roll after facing the player, confirmed via user testing on that earlier feature).
        /// Capture itself uses ScreenCapture.CaptureScreenshotIntoRenderTexture() - the GPU-side,
        /// non-blocking variant suited to continuous per-frame use - called from a dedicated
        /// WaitForEndOfFrame() coroutine (the same established-safe technique already used
        /// elsewhere in this project for the icon-overlap fix), rather than the untimed direct
        /// calls that likely caused the earlier "garbage image" failure on the pause-menu version
        /// of this same idea.
        /// </summary>
        private void UpdateBinocularsFlatCapture()
        {
            if (StereoRender == null || StereoRender.Head == null) return;

            GameObject binocularsCanvasGo = GameObject.Find("LocalPlayer/PlayerAnimator/LookObject/MainCameraFP/Binoculars/Canvas");
            _binocularsFlatCaptureActive = binocularsCanvasGo != null;

            if (!_binocularsFlatCaptureActive)
            {
                if (_binocularsFlatQuad != null && _binocularsFlatQuad.activeSelf) _binocularsFlatQuad.SetActive(false);
                return;
            }

            EnsureBinocularsFlatQuad();
            if (!_binocularsFlatCaptureCoroutineStarted)
            {
                _binocularsFlatCaptureCoroutineStarted = true;
                MelonCoroutines.Start(BinocularsFlatCaptureLoop());
            }

            if (!_binocularsFlatQuad.activeSelf) _binocularsFlatQuad.SetActive(true);

            // Same proven facing math as the pause-menu flat quad: face the player, then roll
            // 180 degrees since a plain Quad's textured side points along its own local +Z (away
            // from the player when naively using Head.forward).
            Quaternion rot = Quaternion.LookRotation(-StereoRender.Head.forward, Vector3.up) * Quaternion.Euler(0f, 0f, 180f);
            Vector3 pos = StereoRender.Head.position + StereoRender.Head.forward * BinocularsFlatQuadDistance;
            _binocularsFlatQuad.transform.SetPositionAndRotation(pos, rot);

            float screenAspect = (float)Mathf.Max(Screen.width, 2) / Mathf.Max(Screen.height, 2);
            _binocularsFlatQuad.transform.localScale = new Vector3(BinocularsFlatQuadHeight * screenAspect, BinocularsFlatQuadHeight, 1f);

            // NEW: cheap final check before considering the deeper HDRP rendering-layer lead -
            // renderer.enabled is a separate flag from GameObject.activeSelf that was never
            // actually logged. Also grabbing Unity's own isVisible flag (directly answers "was
            // this rendered by any camera last frame", independent of why), the shader name
            // (confirms it's genuinely non-null/correct), and renderingLayerMask - the HDRP-
            // specific filtering layer, distinct from the regular Unity layer/cullingMask already
            // confirmed unrestricted - in case this cheap check doesn't resolve it and that deeper
            // lead needs to be followed next.
            if (Time.time - _lastBinocularsFlatCaptureDiagTime > 1f)
            {
                _lastBinocularsFlatCaptureDiagTime = Time.time;
                var rt = _binocularsFlatCaptureRT;
                var quadRenderer = _binocularsFlatQuad.GetComponent<Renderer>();
                MelonLogger.Msg($"[VRPlayer][BinocularsFlatCaptureDiag] active={_binocularsFlatQuad.activeSelf} pos={_binocularsFlatQuad.transform.position} scale={_binocularsFlatQuad.transform.localScale} layer={_binocularsFlatQuad.layer} rtSize={(rt != null ? $"{rt.width}x{rt.height}" : "null")} coroutineStarted={_binocularsFlatCaptureCoroutineStarted} usedCameraCullingMask={(UsedCamera != null ? UsedCamera.cullingMask.ToString() : "UsedCamera null")}");
                if (quadRenderer != null)
                {
                    MelonLogger.Msg($"[VRPlayer][BinocularsFlatCaptureDiag] renderer.enabled={quadRenderer.enabled} renderer.isVisible={quadRenderer.isVisible} shader={(quadRenderer.material != null && quadRenderer.material.shader != null ? quadRenderer.material.shader.name : "null")} renderingLayerMask={quadRenderer.renderingLayerMask}");
                }
                else
                {
                    MelonLogger.Msg("[VRPlayer][BinocularsFlatCaptureDiag] Renderer component is NULL on the quad.");
                }
            }
        }

        private float _lastBinocularsFlatCaptureDiagTime = 0f;

        private void EnsureBinocularsFlatQuad()
        {
            if (_binocularsFlatQuad != null) return;

            int width = Mathf.Max(Screen.width, 2);
            int height = Mathf.Max(Screen.height, 2);
            _binocularsFlatCaptureRT = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            _binocularsFlatCaptureRT.Create();

            _binocularsFlatQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _binocularsFlatQuad.name = "SonsVR_BinocularsFlatQuad";

            var collider = _binocularsFlatQuad.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            var renderer = _binocularsFlatQuad.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("HDRP/Lit");
                if (shader == null) shader = Shader.Find("Unlit/Texture");
                if (shader != null)
                {
                    var mat = new Material(shader);
                    if (mat.HasProperty("_BaseColorMap")) mat.SetTexture("_BaseColorMap", _binocularsFlatCaptureRT);
                    if (mat.HasProperty("_UnlitColorMap")) mat.SetTexture("_UnlitColorMap", _binocularsFlatCaptureRT);
                    if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", _binocularsFlatCaptureRT);
                    if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", _binocularsFlatCaptureRT);

                    // NEW: renderer.isVisible=True was confirmed via diagnostic (Unity's own
                    // internal rendering engine directly confirming this object WAS drawn by a
                    // camera), yet still genuinely invisible to the user - the leading remaining
                    // explanation is occlusion: something else opaque, closer to the camera than
                    // this quad (most likely the player's own hand/controller models, present in
                    // both this test and the separate pause-menu control test that failed
                    // identically), is being drawn in front of it. Pushing the render queue to
                    // 4000 (Overlay - draws dead last, after everything else) as a minimal,
                    // single-property, low-risk test - deliberately NOT combining this with any
                    // blend-state/keyword changes this time, unlike the earlier "force opaque"
                    // attempt that may have backfired by doing too much at once.
                    mat.renderQueue = 4000;

                    renderer.material = mat;
                }
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            _binocularsFlatQuad.SetActive(false);
            MelonLogger.Msg("[VRPlayer][BinocularsFlatCapture] Quad created.");
        }

        private bool _binocularsFlatCaptureLoggedFirstSuccess = false;

        private IEnumerator BinocularsFlatCaptureLoop()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();
                if (_binocularsFlatCaptureActive && _binocularsFlatCaptureRT != null)
                {
                    // NEW: wrapping in try/catch specifically because an unhandled exception
                    // inside a `while(true)` coroutine would silently kill the ENTIRE loop after
                    // just one throw (coroutines don't get MelonLoader's usual per-frame Update
                    // exception isolation) - if that happened here, the quad would exist and be
                    // positioned correctly but its texture would stay permanently blank forever
                    // after the very first failed attempt, which would fully explain "nothing
                    // shows up at all" without any other visible symptom.
                    try
                    {
                        ScreenCapture.CaptureScreenshotIntoRenderTexture(_binocularsFlatCaptureRT);
                        if (!_binocularsFlatCaptureLoggedFirstSuccess)
                        {
                            _binocularsFlatCaptureLoggedFirstSuccess = true;
                            MelonLogger.Msg("[VRPlayer][BinocularsFlatCapture] First capture call succeeded (no exception).");
                        }

                        // NEW: directly sample a pixel from the captured texture so we know for
                        // certain whether it holds real content or is genuinely blank/black -
                        // rather than guessing between "alpha transparency" and "empty capture" as
                        // the cause if forcing opacity above doesn't resolve it on its own.
                        if (Time.time - _lastBinocularsFlatPixelSampleTime > 2f)
                        {
                            _lastBinocularsFlatPixelSampleTime = Time.time;
                            RenderTexture prevActive = RenderTexture.active;
                            RenderTexture.active = _binocularsFlatCaptureRT;
                            var sampleTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                            sampleTex.ReadPixels(new Rect(_binocularsFlatCaptureRT.width / 2, _binocularsFlatCaptureRT.height / 2, 1, 1), 0, 0);
                            sampleTex.Apply();
                            Color centerPixel = sampleTex.GetPixel(0, 0);
                            RenderTexture.active = prevActive;
                            UnityEngine.Object.Destroy(sampleTex);
                            MelonLogger.Msg($"[VRPlayer][BinocularsFlatCapture] Center pixel sample: {centerPixel}");
                        }
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Msg($"[VRPlayer][BinocularsFlatCapture] Capture call threw: {e.Message}");
                        yield break; // stop the loop cleanly rather than risk a silent infinite-throw spin
                    }
                }
            }
        }

        private float _lastBinocularsFlatPixelSampleTime = 0f;

        private void EnsureBinocularsZoomCamera(GameObject binocularsCanvasGo)
        {
            if (!EnableBinocularsZoomCamera) return;
            if (StereoRender == null || StereoRender.Head == null || UsedCamera == null) return;

            // Re-enable unconditionally whenever this runs (binoculars confirmed active by the
            // caller) - covers the case where it was disabled the last time binoculars were put
            // away, per the fix in ConvertBinocularsCanvasToWorldSpace above.
            if (_binocularsZoomCamera != null) _binocularsZoomCamera.enabled = true;

            if (_binocularsZoomCamera == null)
            {
                GameObject mainCameraFPGo = GameObject.Find("LocalPlayer/PlayerAnimator/LookObject/MainCameraFP");
                if (mainCameraFPGo == null) return;
                _mainCameraFP = mainCameraFPGo.GetComponent<Camera>();
                if (_mainCameraFP == null) return;
                _mainCameraFPHDData = mainCameraFPGo.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();

                // FIXED: was cloning the entire MainCameraFP GameObject via Instantiate(), then
                // stripping components/children afterward. Log showed "Created zoom camera" firing
                // over and over, hundreds of times a second - meaning _binocularsZoomCamera kept
                // becoming null again right after a successful assignment. In Unity that specific
                // symptom (a just-assigned reference reads as null moments later) means the
                // underlying object is being destroyed, not that the reference changed - almost
                // certainly some script that came along with the clone (a singleton-camera-
                // enforcement or VR-hookup script, common on a game's main camera) destroying our
                // duplicate via its own Awake/Start before we got a stable reference. Building a
                // completely bare, minimal camera from scratch instead avoids inheriting ANY of
                // MainCameraFP's other scripts, sidestepping this whole risk entirely.
                GameObject zoomCamGo = new GameObject("SonsVR_BinocularsZoomCamera");
                zoomCamGo.tag = "Untagged"; // defensive - never let this become a Camera.main candidate
                _binocularsZoomCamera = zoomCamGo.AddComponent<Camera>();
                _binocularsZoomHDData = zoomCamGo.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
                _binocularsZoomCamera.enabled = true;
                _binocularsZoomCamera.stereoTargetEye = StereoTargetEyeMask.None;

                _binocularsZoomTexture = new RenderTexture(1024, 1024, 24);
                _binocularsZoomTexture.name = "SonsVR_BinocularsZoomTexture";
                _binocularsZoomCamera.targetTexture = _binocularsZoomTexture;

                MelonLogger.Msg("[VRPlayer][BinocularsZoom] Created zoom camera and render texture");
            }

            if (_binocularsZoomRawImage == null)
            {
                Transform group = binocularsCanvasGo.transform.Find("Group");
                if (group == null) return;

                // FIXED (compile error CS1503): `new GameObject(name, typeof(RectTransform))` uses
                // Unity's params-Type[] overload, which under IL2CPP interop expects an
                // Il2CppReferenceArray<Il2CppSystem.Type>, not a plain C# System.Type - the
                // compiler can't implicitly convert between them. Sidestepped entirely by creating
                // a bare GameObject and adding the component separately instead.
                GameObject rawImageGo = new GameObject("SonsVR_BinocularsZoomFeed");
                rawImageGo.AddComponent<RectTransform>();
                rawImageGo.transform.SetParent(group, false);
                rawImageGo.transform.SetSiblingIndex(0); // behind BinocularOutline/LeftPad/RightPad

                var rectT = rawImageGo.GetComponent<RectTransform>();
                rectT.anchorMin = Vector2.zero;
                rectT.anchorMax = Vector2.one;
                rectT.offsetMin = Vector2.zero;
                rectT.offsetMax = Vector2.zero;

                _binocularsZoomRawImage = rawImageGo.AddComponent<RawImage>();
                _binocularsZoomRawImage.texture = _binocularsZoomTexture;

                MelonLogger.Msg("[VRPlayer][BinocularsZoom] Created zoom feed RawImage");
            }

            // Every frame: match the real head look direction (not the flat MainCameraFP
            // orientation, which may lag/differ) and the game's own current zoom level - reading
            // MainCameraFP.fieldOfView live (confirmed via CameraScan log to be the actual value
            // the game changes, 95->40 while aiming) rather than UsedCamera.fieldOfView, which is
            // the VR eye camera and almost certainly never changes at all since VR ignores it -
            // reading from the wrong camera here would have silently produced no zoom regardless
            // of everything else in this function working correctly. Also not hardcoding 40, so
            // this stays correct even if a future update changes the zoom amount.
            if (_mainCameraFP == null) return;
            _binocularsZoomCamera.transform.SetPositionAndRotation(StereoRender.Head.position, StereoRender.Head.rotation);
            _binocularsZoomCamera.fieldOfView = _mainCameraFP.fieldOfView;
            _binocularsZoomCamera.nearClipPlane = _mainCameraFP.nearClipPlane;
            _binocularsZoomCamera.farClipPlane = _mainCameraFP.farClipPlane;
            _binocularsZoomCamera.cullingMask = _mainCameraFP.cullingMask;

            // NEW: user reported the zoomed view working (genuine magnification confirmed) but
            // most elements appearing black - a bare Camera + default HDAdditionalCameraData
            // likely isn't picking up the sky/atmosphere/lighting Volumes the main camera uses
            // (HDRP cameras need volumeLayerMask/clearColorMode configured to render sky and
            // volume-driven lighting correctly, not just the base Camera component settings
            // already copied above). Copying the actual HDRP-specific settings from
            // MainCameraFP's own HDAdditionalCameraData closes that gap without needing to guess
            // at manual Volume/sky setup.
            if (_binocularsZoomHDData != null && _mainCameraFPHDData != null)
            {
                _binocularsZoomHDData.clearColorMode = _mainCameraFPHDData.clearColorMode;
                _binocularsZoomHDData.backgroundColorHDR = _mainCameraFPHDData.backgroundColorHDR;
                _binocularsZoomHDData.volumeLayerMask = _mainCameraFPHDData.volumeLayerMask;
                _binocularsZoomHDData.volumeAnchorOverride = _mainCameraFPHDData.volumeAnchorOverride;
            }
            else if (Time.time - _lastBinocularsHDDataDiagTime > 2f)
            {
                // NEW: if the HDRP settings copy is silently no-op'ing (either reference null),
                // that alone would explain the black elements persisting despite the fix above -
                // logging this directly instead of assuming the copy is actually running.
                _lastBinocularsHDDataDiagTime = Time.time;
                MelonLogger.Msg($"[VRPlayer][BinocularsZoom] HDRP settings copy skipped - zoomHDData={(_binocularsZoomHDData != null ? "ok" : "NULL")} mainCameraFPHDData={(_mainCameraFPHDData != null ? "ok" : "NULL")}");
            }
        }

        private float _lastBinocularsHDDataDiagTime = 0f;

        private float _lastBinocularsDumpTime = 0f;

        /// <summary>
        /// CONFIRMED via CameraScan (fieldOfView logging): the game zooms binoculars by reducing
        /// MainCameraFP.fieldOfView from 95 to 40 - a technique VR headsets ignore entirely (each
        /// eye's actual FOV comes from the XR runtime's own per-eye projection matrices, not this
        /// Unity property), matching the user's report of zoom working on the flat monitor mirror
        /// but not through the headset. A real fix needs a genuine magnified render (a second
        /// camera at the reduced FOV, rendered to a texture, displayed in front of the eyes) rather
        /// than another canvas/position tweak. Before wiring that in blind, dumping the actual
        /// child structure of the Binoculars canvas - specifically Image/RawImage components and
        /// their texture references - to find out whether the two-circle overlay is a live camera
        /// feed already (in which case swapping/adjusting the source is enough) or just a static
        /// mask sitting over the normal environment view (in which case a new RawImage + camera
        /// needs to be added, layered correctly behind the existing mask holes).
        /// </summary>
        private void DebugDumpBinocularsCanvasStructure(GameObject binocularsCanvasGo)
        {
            if (Time.time - _lastBinocularsDumpTime < 2f) return;
            _lastBinocularsDumpTime = Time.time;

            foreach (var t in binocularsCanvasGo.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                MelonLogger.Msg($"[VRPlayer][BinocularsDiag] {GetHierarchyPath(t)} active={t.gameObject.activeSelf}");

                var img = t.GetComponent<Image>();
                if (img != null)
                {
                    MelonLogger.Msg($"[VRPlayer][BinocularsDiag]   Image sprite={(img.sprite != null ? img.sprite.name : "null")} material={(img.material != null ? img.material.name : "null")} color={img.color}");
                }
                var rawImg = t.GetComponent<RawImage>();
                if (rawImg != null)
                {
                    MelonLogger.Msg($"[VRPlayer][BinocularsDiag]   RawImage texture={(rawImg.texture != null ? rawImg.texture.name : "null")} material={(rawImg.material != null ? rawImg.material.name : "null")}");
                }
                var mask = t.GetComponent<Mask>();
                if (mask != null)
                {
                    MelonLogger.Msg($"[VRPlayer][BinocularsDiag]   Mask showMaskGraphic={mask.showMaskGraphic}");
                }
            }
        }

        private float _lastPauseMenuDumpTime = 0f;
        private System.Collections.Generic.HashSet<string> _lastActiveTopLevelRoots = new System.Collections.Generic.HashSet<string>();
        private static readonly System.Collections.Generic.HashSet<string> _knownTopLevelRootNames = new System.Collections.Generic.HashSet<string>
        {
            "TitleMenu", "BottomCanvas", "Canvas", "MultiplayerMenuCanvas", "LoadingIndictor",
            "VersionReadout", "EventSystem", "PlayerStandin", "GameManagers", "Villages",
            "DeadLocations", "Greebles_Realtime"
        };

        /// <summary>
        /// CONFIRMED via evidence: Options, MultiplayerMenuCanvas, and LoadingIndictor all
        /// turned out to be separate TOP-LEVEL root objects (parent == null), not nested where
        /// their name/context would suggest. Zero keyword or PauseMenu-subtree searches have
        /// found the quit/save confirmation dialog's name anywhere, active or inactive - so
        /// rather than guess a fourth keyword set, this scans for any newly-active top-level
        /// root NOT already in the known list, following the same pattern that worked for the
        /// other three. Scoped to root-level objects only (parent == null check happens first,
        /// which most objects fail immediately) so it's far cheaper than a full scene walk.
        /// </summary>
        private void DebugScanNewTopLevelRoots()
        {
            if (!DebugLogCanvasChanges) return;
            if (Time.time - _lastPauseMenuDumpTime < 1.5f) return;
            _lastPauseMenuDumpTime = Time.time;

            var current = new System.Collections.Generic.HashSet<string>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t == null || t.parent != null) continue;
                if (!t.gameObject.activeInHierarchy) continue;
                if (_knownTopLevelRootNames.Contains(t.name)) continue;
                current.Add(t.name);
            }
            foreach (var name in current)
            {
                if (!_lastActiveTopLevelRoots.Contains(name))
                {
                    MelonLogger.Msg($"[VRPlayer][NewRootScan] New top-level root ACTIVE: {name}");
                }
            }
            foreach (var name in _lastActiveTopLevelRoots)
            {
                if (!current.Contains(name))
                {
                    MelonLogger.Msg($"[VRPlayer][NewRootScan] Top-level root inactive: {name}");
                }
            }
            _lastActiveTopLevelRoots = current;
        }

        private void DebugDumpPauseMenuSubtree()
        {
            if (!DebugLogCanvasChanges) return;
            if (Time.time - _lastPauseMenuDumpTime < 1.5f) return;
            _lastPauseMenuDumpTime = Time.time;

            GameObject scope = GameObject.Find("PlayerStandin/PlayerUI/ScreenSpaceCanvas");
            if (scope == null) return;

            var sb = new System.Text.StringBuilder();
            foreach (var t in scope.GetComponentsInChildren<Transform>(true))
            {
                if (t.gameObject.activeInHierarchy) sb.Append($"[{t.name}] ");
            }
            MelonLogger.Msg($"[VRPlayer][ScreenSpaceCanvasDump] ACTIVE only: {sb}");
        }

        private bool _saveLoadScanDone = false;

        private float _lastConfirmDialogCameraScanTime = 0f;

        /// <summary>
        /// CONFIRMED via user testing: the base pause menu renders correctly in both VR eyes
        /// (via native rendering + the blur-suppression fix), but the quit/save confirmation
        /// dialog does not appear in VR at all while it's simultaneously visible on the flat
        /// desktop view, with the pause menu still visible behind it in VR. Since both are part
        /// of the same visual system, rendering identically otherwise, the confirmation dialog
        /// not compositing into VR points to a DIFFERENT camera being responsible for it - one
        /// that's likely mono/flat-only (stereoTargetEye != Both, or targets a display XR
        /// doesn't composite). Logging every active camera's stereo/display settings to find it,
        /// rather than guessing at another capture mechanism after the last one froze the game.
        /// </summary>
        private void DebugScanActiveCamerasForConfirmDialog()
        {
            if (!DebugLogCanvasChanges) return;
            if (Time.time - _lastConfirmDialogCameraScanTime < 1.5f) return;
            _lastConfirmDialogCameraScanTime = Time.time;

            foreach (var cam in UnityEngine.Object.FindObjectsOfType<Camera>())
            {
                if (cam == null || !cam.isActiveAndEnabled) continue;
                MelonLogger.Msg($"[VRPlayer][CameraScan] {GetHierarchyPath(cam.transform)}: stereoTargetEye={cam.stereoTargetEye} targetDisplay={cam.targetDisplay} depth={cam.depth} cullingMask={cam.cullingMask} targetTexture={(cam.targetTexture != null ? cam.targetTexture.name : "null")}");
            }
        }


        /// <summary>
        /// TARP MULTI-SELECT INVESTIGATION - read-only, changes nothing.
        /// The known workaround (physically touching the right stick, ideally UP/DOWN, while
        /// gaze drives the tarp cursor) stops the multi-select bug - but a synthetic/injected
        /// stick value confirmed does NOT replicate this. That's the key clue: this isn't about
        /// the stick's VALUE, it's about some internal state that only responds to a genuine
        /// device-level input event. Since this game uses the new Input System, UI navigation
        /// almost certainly goes through InputSystemUIInputModule, which has its own internal
        /// "move repeat" timer for stick/d-pad-driven selection changes - a plausible second,
        /// independent system that could race against gaze-driven cursor position and cause a
        /// double-selection. Logging its actual state while the tarp cursor is active, to
        /// gather real evidence before attempting any fix.
        /// </summary>
        /// <summary>
        /// ROOT CAUSE FOUND via item-side field dump: each LayoutItem has enabled=True,
        /// isActiveAndEnabled=True, and its own OnMouseEnterEvent/OnMouseExitEvent/
        /// OnMouseOverEvent backing fields - confirming this class implements Unity's BUILT-IN
        /// OnMouseEnter/OnMouseOver/OnMouseExit MonoBehaviour callbacks. Those are driven by
        /// Camera.main raycasting through the literal desktop mouse position - a completely
        /// separate system from every cursor-side component we spent multiple rounds examining
        /// and disabling (collider, EventSystem, async task) with zero effect, because none of
        /// them were ever the actual mechanism. That's why nothing on the cursor side ever
        /// mattered.
        ///
        /// Rather than chase why Unity's own mouse-hover system misbehaves in VR (whatever
        /// drives the real mouse position independent of our gaze), this sidesteps it entirely:
        /// LayoutItemCursorSnapping._currentlySnappedLayoutItem already reliably tracks the
        /// CORRECT item under gaze (confirmed across every test so far). So every poll, force
        /// that one item's IsHighlighted on and every other LayoutItem's IsHighlighted off,
        /// directly enforcing single-selection ourselves regardless of what Unity's built-in
        /// system is doing underneath.
        /// </summary>

        // Cached reflection metadata - resolved once, reused for every item/outliner instead of
        // re-resolving GetProperty() every poll for every one of potentially 256+ items, which
        // would be a real performance cost at that scale.
        private PropertyInfo _meshOutlinersProp;
        private PropertyInfo _outlinerListCountProp;
        private PropertyInfo _outlinerListIndexerProp;
        private MethodInfo _meshOutlinerEnableMethod;
        private LayoutItem _lastToggledItem;
        private PropertyInfo _cursorControllerSnappedItemProp;
        private PropertyInfo _layoutSnappedItemProp;
        // V116 tarp session cache. V115 still called FindObjectsOfType<LayoutItem>() every 0.2s.
        // The cooking profiler already proved global type scans can cost ~100 ms in this game,
        // and the user again sees ~25 -> ~12 FPS only while the tarp is active. Build once per
        // tarp session, then refresh only if gaze targets an item absent from the cache.
        private LayoutItem[] _cachedLayoutItems;
        private int _tarpLayoutCacheBuildsV116;
        private float _lastTarpLayoutCacheBuildMsV116;
        private double _tarpInteractionAccumMsV116;
        private float _tarpInteractionMaxMsV116;
        private int _tarpInteractionSamplesV116;
        private float _nextTarpInteractionPerfLogV116;
        private LayoutItem _lastSnapAnchorSearchItem;
        private PropertyInfo _snapAnchorTrackedObjectProp;
        private PropertyInfo _layoutSnapPointProp;
        private PropertyInfo _cursorControllerSnapPointProp;
        private MethodInfo _cursorControllerLastUpdateMethod;
        private GameObject _cursorStateGo;

        private void ResetTarpLayoutItemCacheV116(string reason)
        {
            _cachedLayoutItems = null;
            _tarpLayoutCacheBuildsV116 = 0;
            _lastTarpLayoutCacheBuildMsV116 = 0f;
            _lastToggledItem = null;
            _lastSnapAnchorSearchItem = null;
            _tarpInteractionAccumMsV116 = 0.0;
            _tarpInteractionMaxMsV116 = 0f;
            _tarpInteractionSamplesV116 = 0;
            _nextTarpInteractionPerfLogV116 = Time.unscaledTime + 1.0f;
            MelonLogger.Msg($"[VRPlayer][V116 TarpCache] reset reason={reason}");
        }

        private void RefreshTarpLayoutItemCacheV116(string reason)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _cachedLayoutItems = UnityEngine.Object.FindObjectsOfType<LayoutItem>();
            }
            catch (Exception e)
            {
                _cachedLayoutItems = null;
                MelonLogger.Warning($"[VRPlayer][V116 TarpCache] global LayoutItem refresh failed reason={reason}: {e.Message}");
                return;
            }

            _lastTarpLayoutCacheBuildMsV116 = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _tarpLayoutCacheBuildsV116++;
            MelonLogger.Warning(
                $"[VRPlayer][V116 TarpCache] build={_tarpLayoutCacheBuildsV116} reason={reason} " +
                $"items={(_cachedLayoutItems != null ? _cachedLayoutItems.Length : 0)} cost={_lastTarpLayoutCacheBuildMsV116:F3}ms. " +
                "No periodic full-scene LayoutItem scans are used.");
        }

        private bool TarpLayoutCacheContainsV116(LayoutItem item)
        {
            if (item == null || _cachedLayoutItems == null) return false;
            for (int i = 0; i < _cachedLayoutItems.Length; i++)
            {
                LayoutItem candidate = _cachedLayoutItems[i];
                if (candidate != null && candidate == item) return true;
            }
            return false;
        }

        private void RecordTarpInteractionCostV116(long startTick)
        {
            float ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - startTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _tarpInteractionAccumMsV116 += ms;
            _tarpInteractionSamplesV116++;
            if (ms > _tarpInteractionMaxMsV116) _tarpInteractionMaxMsV116 = ms;

            if (Time.unscaledTime < _nextTarpInteractionPerfLogV116) return;
            _nextTarpInteractionPerfLogV116 = Time.unscaledTime + 1.0f;
            float avg = _tarpInteractionSamplesV116 > 0
                ? (float)(_tarpInteractionAccumMsV116 / _tarpInteractionSamplesV116)
                : 0f;
            MelonLogger.Msg(
                $"[VRPlayer][V116 TarpPerf] highlight/snap avg={avg:F3}ms max={_tarpInteractionMaxMsV116:F3}ms " +
                $"samples={_tarpInteractionSamplesV116} cacheItems={(_cachedLayoutItems != null ? _cachedLayoutItems.Length : 0)} " +
                $"cacheBuilds={_tarpLayoutCacheBuildsV116} lastBuild={_lastTarpLayoutCacheBuildMsV116:F3}ms");
            _tarpInteractionAccumMsV116 = 0.0;
            _tarpInteractionMaxMsV116 = 0f;
            _tarpInteractionSamplesV116 = 0;
        }

        private void SetItemOutlinersEnabled(LayoutItem item, bool enabled)
        {
            try
            {
                if (_meshOutlinersProp == null)
                {
                    _meshOutlinersProp = item.GetType().GetProperty("_meshOutliners", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                var outliners = _meshOutlinersProp?.GetValue(item);
                if (outliners == null) return;

                if (_outlinerListCountProp == null) _outlinerListCountProp = outliners.GetType().GetProperty("Count");
                if (_outlinerListIndexerProp == null) _outlinerListIndexerProp = outliners.GetType().GetProperty("Item");
                int count = _outlinerListCountProp != null ? (int)_outlinerListCountProp.GetValue(outliners) : 0;

                for (int i = 0; i < count; i++)
                {
                    var outliner = _outlinerListIndexerProp?.GetValue(outliners, new object[] { i });
                    if (outliner == null) continue;

                    // FOUND via method dump: MeshOutliner has a real public Enable(Boolean)
                    // method, separate from the properties entirely. Confirmed via user
                    // screenshot + testing that directly setting _isEnabled (found=True,
                    // canWrite=True, zero exceptions) had no visual effect - meaning it's just a
                    // state flag, and Enable(bool) is almost certainly what actually performs the
                    // material swap (_originalMaterialList <-> _outlinedMaterials) that produces
                    // the visible white contour outline. Calling that instead.
                    if (_meshOutlinerEnableMethod == null)
                    {
                        _meshOutlinerEnableMethod = outliner.GetType().GetMethod("Enable", BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(bool) }, null);
                    }
                    if (_meshOutlinerEnableMethod != null)
                    {
                        _meshOutlinerEnableMethod.Invoke(outliner, new object[] { enabled });
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[VRPlayer] SetItemOutlinersEnabled failed for {item.name}: {e.Message}");
            }
        }

        private void EnforceExclusiveTarpItemHighlight()
        {
            if (_layoutCursorSnapping == null) return;

            LayoutItem currentItem = null;
            try
            {
                // FIXED: this GetProperty() metadata lookup was being re-resolved every single
                // frame instead of cached - confirmed by the user as a major FPS regression
                // (30fps -> 12fps in the tarp) introduced by the multiselect fix. Caching it now,
                // matching the pattern already used correctly elsewhere (e.g.
                // _cursorControllerSnappedItemProp).
                if (_layoutSnappedItemProp == null)
                {
                    _layoutSnappedItemProp = _layoutCursorSnapping.GetType()
                        .GetProperty("_currentlySnappedLayoutItem", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                currentItem = _layoutSnappedItemProp?.GetValue(_layoutCursorSnapping) as LayoutItem;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[VRPlayer] EnforceExclusiveTarpItemHighlight: failed to read current item: {e.Message}");
                return;
            }

            // NEW: confirmed via field dump that InventoryCursorController has its OWN SEPARATE
            // _currentlySnappedLayoutItem field (distinct from LayoutItemCursorSnapping's copy,
            // which only drives the visual highlight) - and it stays null throughout, even while
            // gaze correctly identifies an item. User confirmed the trigger does nothing even
            // when the correct single item is visually highlighted, and only holding the stick
            // (which drives InventoryCursorController's own movement-based Update logic) ever
            // makes pickup work - matching this being the field the actual pickup action reads
            // from. Syncing it directly to what we already know is correct.
            if (_cursorController != null)
            {
                try
                {
                    if (_cursorControllerSnappedItemProp == null && _cursorController != null)
                    {
                        _cursorControllerSnappedItemProp = _cursorController.GetType()
                            .GetProperty("_currentlySnappedLayoutItem", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    if (_cursorControllerSnappedItemProp != null && _cursorControllerSnappedItemProp.CanWrite)
                    {
                        var existing = _cursorControllerSnappedItemProp.GetValue(_cursorController) as LayoutItem;
                        if (existing != currentItem)
                        {
                            _cursorControllerSnappedItemProp.SetValue(_cursorController, currentItem);
                        }
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[VRPlayer] Failed to sync InventoryCursorController's snapped item: {e.Message}");
                }
            }

            // FOUND via candidate dump: _otherSnapPoints only has 3 entries unrelated to the
            // tarp (CraftCog, ColorButton, PowerButton - a different UI's controls). But
            // _snapAnchors has 1283 entries of type VirtualCursorSnapAnchor, each with a
            // TrackedObject property - almost certainly the real candidate pool, with each
            // anchor corresponding to one trackable item. Searching for the anchor whose
            // TrackedObject matches our current item, and assigning it to _currentSnapPoint on
            // both components (mirroring the _currentlySnappedLayoutItem sync above). Only
            // searching on an actual gaze-target change, not every frame - scanning 1283
            // entries via reflection every poll would be a real cost, but doing it only when
            // the target changes (a human-timescale event) is cheap.
            if (currentItem != _lastSnapAnchorSearchItem)
            {
                _lastSnapAnchorSearchItem = currentItem;
                try
                {
                    object matchedAnchor = null;
                    if (currentItem != null && _layoutCursorSnapping != null)
                    {
                        var anchorsProp = _layoutCursorSnapping.GetType()
                            .GetProperty("_snapAnchors", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var anchors = anchorsProp?.GetValue(_layoutCursorSnapping);
                        if (anchors != null)
                        {
                            var listType = anchors.GetType();
                            var countProp = listType.GetProperty("Count");
                            var indexerProp = listType.GetProperty("Item");
                            int count = countProp != null ? (int)countProp.GetValue(anchors) : 0;
                            for (int i = 0; i < count; i++)
                            {
                                var anchor = indexerProp?.GetValue(anchors, new object[] { i });
                                if (anchor == null) continue;
                                if (_snapAnchorTrackedObjectProp == null)
                                {
                                    _snapAnchorTrackedObjectProp = anchor.GetType().GetProperty("TrackedObject", BindingFlags.Public | BindingFlags.Instance);
                                }
                                var tracked = _snapAnchorTrackedObjectProp?.GetValue(anchor);
                                if (tracked != null && (ReferenceEquals(tracked, currentItem) || ReferenceEquals(tracked, currentItem.gameObject)))
                                {
                                    matchedAnchor = anchor;
                                    break;
                                }
                            }
                        }
                    }

                    if (_layoutSnapPointProp == null && _layoutCursorSnapping != null)
                    {
                        _layoutSnapPointProp = _layoutCursorSnapping.GetType().GetProperty("_currentSnapPoint", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    if (_layoutSnapPointProp != null && _layoutSnapPointProp.CanWrite)
                    {
                        _layoutSnapPointProp.SetValue(_layoutCursorSnapping, matchedAnchor);
                    }

                    if (_cursorControllerSnapPointProp == null && _cursorController != null)
                    {
                        _cursorControllerSnapPointProp = _cursorController.GetType().GetProperty("_currentSnapPoint", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    if (_cursorControllerSnapPointProp != null && _cursorControllerSnapPointProp.CanWrite)
                    {
                        _cursorControllerSnapPointProp.SetValue(_cursorController, matchedAnchor);
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[VRPlayer] Snap anchor search/assign failed: {e.Message}");
                }
            }

            // V116: V115 still paid the global FindObjectsOfType<LayoutItem>() tax at 5 Hz.
            // Build once when the tarp session starts. If the game's native layout dynamically
            // introduces a genuinely new gaze-targeted item, perform one need-driven refresh.
            // This mirrors the cooking cache strategy that removed the old 4-FPS pathology.
            if (_cachedLayoutItems == null)
            {
                RefreshTarpLayoutItemCacheV116("session-first-use");
            }
            else if (currentItem != null && !TarpLayoutCacheContainsV116(currentItem))
            {
                RefreshTarpLayoutItemCacheV116("gaze-target-cache-miss:" + (currentItem.name ?? "<unnamed>"));
            }
            var allItems = _cachedLayoutItems;
            if (allItems == null) return;
            foreach (var item in allItems)
            {
                if (item == null) continue;
                bool shouldBeHighlighted = (item == currentItem);
                if (item.IsHighlighted != shouldBeHighlighted)
                {
                    item.IsHighlighted = shouldBeHighlighted;
                }
                // Any wrong item still showing highlighted gets forced off every poll,
                // continuously suppressing whatever the game's own native OnMouseEnter/Exit
                // system keeps trying to do to it. The correct item only gets Enable(true)
                // called once per transition (below) to avoid a flash/re-animation glitch.
                if (item != currentItem && item.IsHighlighted)
                {
                    SetItemOutlinersEnabled(item, false);
                }
            }

            // FIXED: CONFIRMED via user testing - calling Enable() unconditionally on every
            // item every frame (previous version) fixed the original "stuck on multiple items"
            // bug, but introduced a NEW flashing/flickering problem on the correctly-targeted
            // item itself. Enable(bool) is very likely not idempotent - probably restarts a
            // fade/flash animation or material-swap effect each time it's called, even when
            // called repeatedly with the same value. Only calling it now on an actual
            // transition (tracked via _lastToggledItem), exactly once per change, instead of
            // every single frame regardless of whether anything changed.
            if (currentItem != _lastToggledItem)
            {
                if (_lastToggledItem != null) SetItemOutlinersEnabled(_lastToggledItem, false);
                if (currentItem != null) SetItemOutlinersEnabled(currentItem, true);
                _lastToggledItem = currentItem;
            }

            // NEW: found via method dump - LastUpdate() is a plain, parameterless public method
            // on InventoryCursorController, strongly suggesting it's meant to be called every
            // frame to actually process/commit the current snap state - but since enabled=False
            // most of the time, the game's own Update() never calls it. Confirmed via testing:
            // directly writing _currentlySnappedLayoutItem and _currentSnapPoint (both fields
            // the game itself uses) still wasn't enough for pickup to register without the
            // stick - this calls the method itself, after both fields are already set, to see
            // if that performs whatever additional commit step is missing.
            if (_cursorController != null)
            {
                try
                {
                    if (_cursorControllerLastUpdateMethod == null)
                    {
                        _cursorControllerLastUpdateMethod = _cursorController.GetType().GetMethod("LastUpdate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    }
                    if (_cursorControllerLastUpdateMethod != null)
                    {
                        _cursorControllerLastUpdateMethod.Invoke(_cursorController, null);
                    }

                    // NEW: user reported losing the visual gaze cursor in the tarp right after
                    // this LastUpdate() call was added - strong circumstantial evidence that
                    // this native method has a side effect of hiding the cursor visual when it
                    // doesn't detect genuine movement input this frame (which gaze-driven
                    // positioning never provides). Forcing the cursor's own visual GameObject
                    // (_cursorState) to stay active every poll to counteract that, without
                    // having to stop calling LastUpdate() (which is what made pickup work).
                    if (_cursorStateGo == null)
                    {
                        var cursorStateProp = _cursorController.GetType().GetProperty("_cursorState", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        _cursorStateGo = cursorStateProp?.GetValue(_cursorController) as GameObject;
                    }
                    if (_cursorStateGo != null && !_cursorStateGo.activeSelf)
                    {
                        _cursorStateGo.SetActive(true);
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[VRPlayer] Calling LastUpdate() failed: {e.Message}");
                }
            }
        }





        /// <summary>
        /// One-time scan of loaded types for anything save/load related, so we can identify
        /// the real classes/methods a custom VR menu would need to call directly instead of
        /// relying on the native (currently blurry-in-VR) menu UI at all.
        /// </summary>
        /// <summary>
        /// Y button triggers a real save via SaveMenu.CreateSave() directly - completely
        /// bypassing the (currently unreadable-in-VR) native menu. This is the actual save
        /// function the game's own UI calls internally.
        /// </summary>
        private float _lastQuickSaveTime = -999f;
        private const float QuickSaveCooldown = 8f; // give CreateSave's internal state time to settle
        private bool _pendingAutoCloseAfterSave = false;
        private float _autoCloseTimer = 0f;
        private bool _pendingButtonRelease = false;

        // Confirmed non-functional: CreateSave() runs without throwing but produces no actual
        // save file - it almost certainly depends on internal UI state set up by the real menu
        // flow that we can't replicate via reflection alone. Disabled rather than left on with
        // no benefit and a real downside (briefly locks movement).
        public static bool EnableQuickSave = false;

        private void CheckQuickSaveInput()
        {
            if (!EnableQuickSave) return;
            HandlePendingAutoClose();

            var gamepad = Gamepad.current;
            if (gamepad == null) return;
            if (!gamepad.buttonNorth.wasPressedThisFrame) return; // Y button

            if (Time.unscaledTime - _lastQuickSaveTime < QuickSaveCooldown)
            {
                MelonLogger.Msg("[VRPlayer] Quick Save ignored - still on cooldown from the last save.");
                return;
            }

            TriggerQuickSave();
        }

        private void TriggerQuickSave()
        {
            _lastQuickSaveTime = Time.unscaledTime;
            try
            {
                SaveMenu saveMenu = UnityEngine.Object.FindObjectOfType<SaveMenu>();
                if (saveMenu == null)
                {
                    SaveMenu[] all = Resources.FindObjectsOfTypeAll<SaveMenu>();
                    if (all != null && all.Length > 0) saveMenu = all[0];
                }

                if (saveMenu == null)
                {
                    MelonLogger.Warning("[VRPlayer] Quick Save: no SaveMenu instance found in the scene.");
                    return;
                }

                string name = saveMenu.GenerateDefaultSaveGameName();
                MelonLogger.Msg($"[VRPlayer] Quick Save: calling CreateSave(\"{name}\")...");
                saveMenu.CreateSave(name);
                MelonLogger.Msg("[VRPlayer] Quick Save: CreateSave call completed without throwing.");

                // CreateSave seems to leave some internal UI/interaction state open (matches
                // needing to press B to regain movement) - auto-dismiss it shortly after.
                _pendingAutoCloseAfterSave = true;
                _autoCloseTimer = 0.3f;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[VRPlayer] Quick Save failed: {e}");
                // Even on failure, try to release whatever got locked so movement isn't stuck.
                _pendingAutoCloseAfterSave = true;
                _autoCloseTimer = 0.3f;
            }
        }

        private void HandlePendingAutoClose()
        {
            var gamepad = Gamepad.current;

            if (_pendingButtonRelease)
            {
                _pendingButtonRelease = false;
                if (gamepad != null)
                {
                    StateEvent.From(gamepad, out var releaseEvent);
                    gamepad.buttonEast.WriteValueIntoEvent(0f, releaseEvent);
                    UnityEngine.InputSystem.InputSystem.QueueEvent(releaseEvent);
                }
                return;
            }

            if (!_pendingAutoCloseAfterSave) return;

            _autoCloseTimer -= Time.unscaledDeltaTime;
            if (_autoCloseTimer > 0f) return;

            _pendingAutoCloseAfterSave = false;
            if (gamepad == null) return;

            MelonLogger.Msg("[VRPlayer] Auto-simulating B button to release whatever CreateSave left locked.");
            StateEvent.From(gamepad, out var eventPtr);
            gamepad.buttonEast.WriteValueIntoEvent(1f, eventPtr); // B button
            UnityEngine.InputSystem.InputSystem.QueueEvent(eventPtr);
            _pendingButtonRelease = true;
        }


        private void DebugScanSaveLoadTypes()
        {
            if (_saveLoadScanDone) return;
            _saveLoadScanDone = true;

            MelonLogger.Msg("[VRPlayer][SaveScan] Starting save/load type scan...");
            int matchCount = 0;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var asm in assemblies)
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    string ns = type.Namespace ?? "";
                    if (!ns.StartsWith("Il2CppSons") && !ns.StartsWith("Il2CppEndnight")) continue;
                    if (type.Name.IndexOf("save", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    matchCount++;
                    MelonLogger.Msg($"[VRPlayer][SaveScan] TYPE: {type.FullName}");

                    MethodInfo[] methods;
                    try { methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); }
                    catch { methods = new MethodInfo[0]; }

                    int shown = 0;
                    foreach (var m in methods)
                    {
                        if (m.IsSpecialName) continue; // skip property get_/set_ noise
                        var ps = m.GetParameters();
                        string paramStr = "";
                        for (int i = 0; i < ps.Length; i++)
                        {
                            if (i > 0) paramStr += ", ";
                            paramStr += ps[i].ParameterType.Name;
                        }
                        MelonLogger.Msg($"[VRPlayer][SaveScan]   METHOD: {m.ReturnType.Name} {m.Name}({paramStr})");
                        shown++;
                        if (shown >= 25)
                        {
                            MelonLogger.Msg("[VRPlayer][SaveScan]   ... (more methods truncated)");
                            break;
                        }
                    }
                }
            }

            MelonLogger.Msg($"[VRPlayer][SaveScan] Scan complete. {matchCount} matching type(s) found.");
        }

        private float _lastCraftMatDiagTime = 0f;

        /// <summary>
        /// Targeted diagnostic for CraftMatWorldUI - it activates for water too (not just
        /// crafting recipes, as first assumed), so the render-mode fix that worked for other
        /// panels doesn't explain why it's invisible for water. Logs its actual position and
        /// any text content, to check whether it's positioned wrong or simply has no content
        /// populated for non-craftable items.
        /// </summary>
        private float _lastVolumeScanTime = 0f;

        /// <summary>
        /// Scans for active HDRP Volume components and their DepthOfField override settings.
        /// The pause/save menu blur is very likely a DoF effect that gets weighted in when the
        /// game pauses - this is the game's OWN post-processing system (Il2CppSons.PostProcessing
        /// and Unity.RenderPipelines.HighDefinition.Runtime are both already referenced in this
        /// project). Logs enough to identify the exact culprit so we can suppress it while paused.
        /// </summary>
        private void DebugScanPostProcessVolumes()
        {
            if (!DebugScanCanvasesOnly) return;
            if (Time.time - _lastVolumeScanTime < 1f) return;
            _lastVolumeScanTime = Time.time;

            bool paused = Time.timeScale <= 0.0001f;

            UnityEngine.Rendering.Volume[] volumes = UnityEngine.Object.FindObjectsOfType<UnityEngine.Rendering.Volume>();
            foreach (var v in volumes)
            {
                if (v == null || !v.gameObject.activeInHierarchy || !v.enabled) continue;

                string profileName = v.profile != null ? v.profile.name : "null";
                MelonLogger.Msg($"[VRPlayer][VolumeDiag] paused={paused} Volume '{v.gameObject.name}' isGlobal={v.isGlobal} weight={v.weight} priority={v.priority} profile={profileName}");

                if (v.profile == null) continue;

                if (v.profile.TryGet<UnityEngine.Rendering.HighDefinition.DepthOfField>(out var dof))
                {
                    MelonLogger.Msg($"[VRPlayer][VolumeDiag]   DepthOfField: active={dof.active} focusMode={dof.focusMode.value} nearFocusEnd={(dof.nearFocusEnd != null ? dof.nearFocusEnd.value.ToString() : "N/A")} farFocusStart={(dof.farFocusStart != null ? dof.farFocusStart.value.ToString() : "N/A")}");
                }
            }
        }

        private void DebugInspectCraftMatWorldUI()
        {
            if (!DebugScanCanvasesOnly) return;
            if (Time.time - _lastCraftMatDiagTime < 1f) return;
            _lastCraftMatDiagTime = Time.time;

            Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            foreach (var c in canvases)
            {
                if (c == null) continue;
                if (!c.name.Equals("CraftMatWorldUI")) continue;

                MelonLogger.Msg($"[VRPlayer][CraftMatDiag] CraftMatWorldUI position={c.transform.position} localPos={c.transform.localPosition} active={c.gameObject.activeInHierarchy} renderMode={c.renderMode} worldCamera={(c.worldCamera != null ? c.worldCamera.name : "null")}");

                var texts = c.GetComponentsInChildren<UnityEngine.UI.Text>(true);
                foreach (var t in texts)
                {
                    if (t == null) continue;
                    MelonLogger.Msg($"[VRPlayer][CraftMatDiag]   Text '{t.gameObject.name}' active={t.gameObject.activeInHierarchy} content='{t.text}'");
                }

                // Also check for TextMeshPro, commonly used instead of legacy UI.Text
                var tmpTexts = c.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(true);
                foreach (var t in tmpTexts)
                {
                    if (t == null) continue;
                    MelonLogger.Msg($"[VRPlayer][CraftMatDiag]   TMP_Text '{t.gameObject.name}' active={t.gameObject.activeInHierarchy} content='{t.text}'");
                }
            }
        }

        private void DebugScanActiveCanvases()
        {
            if (!DebugScanCanvasesOnly) return;
            if (Time.time - _lastCanvasScanTime < 0.5f) return;
            _lastCanvasScanTime = Time.time;

            var currentNames = new System.Collections.Generic.HashSet<string>();

            Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(); // active only
            foreach (var c in canvases)
            {
                if (c == null || c.gameObject == null) continue;
                string path = GetHierarchyPath(c.transform) + $"  [Canvas renderMode={c.renderMode}]";
                currentNames.Add(path);
            }

            // Sons Of The Forest also uses UI Toolkit (UIDocument) for some panels - a
            // legacy Canvas scan alone can completely miss those (e.g. the campfire menu).
            UnityEngine.UIElements.UIDocument[] uiDocs = UnityEngine.Object.FindObjectsOfType<UnityEngine.UIElements.UIDocument>();
            foreach (var d in uiDocs)
            {
                if (d == null || d.gameObject == null) continue;
                string rootName = d.rootVisualElement != null ? d.rootVisualElement.name : "(no root yet)";
                string path = GetHierarchyPath(d.transform) + $"  [UIDocument root={rootName}]";
                currentNames.Add(path);
            }

            foreach (var name in currentNames)
            {
                if (!_lastActiveCanvasNames.Contains(name))
                {
                    MelonLogger.Msg($"[VRPlayer][CanvasScan] ACTIVATED: {name}");
                }
            }
            foreach (var name in _lastActiveCanvasNames)
            {
                if (!currentNames.Contains(name))
                {
                    MelonLogger.Msg($"[VRPlayer][CanvasScan] deactivated: {name}");
                }
            }

            _lastActiveCanvasNames = currentNames;
        }

        private static string GetHierarchyPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
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

        /// <summary>
        /// Finds the campfire grab bag's "CursorState" object (sibling structure to the tarp
        /// inventory's InventoryCursorState, but with no InventoryCursorController wrapper).
        /// Logs every component on it once, so we can see from the log what it actually is and
        /// whether pointing it via gaze is enough to drive selection, or whether we need to call
        /// something else on one of these components.
        /// </summary>
        /// <summary>
        /// Creates (once) a small, unlit, collider-free sphere to act as a visible gaze
        /// pointer for the campfire grab bag, since its CursorState object has no renderer.
        /// </summary>
        /// <summary>
        /// Positions the gaze marker by actually raycasting from the head in the gaze
        /// direction, instead of guessing a fixed distance. A fixed distance can never
        /// reliably land on an item since real item depth varies as you look around - this is
        /// what LayoutItemCursorSnapping effectively does for the tarp cursor. Falls back to a
        /// fixed close distance only if the raycast hits nothing (e.g. items have no collider).
        /// </summary>
        private float _lastRaycastLogTime = 0f;
        private GameObject _aimReticle;

        /// <summary>
        /// Aiming reticle: a red dot showing where your gaze is pointed, visible while holding
        /// the left trigger (aim down sights). Since aligning a gun's iron sights or a bow's
        /// arrow is very difficult in VR (and the weapon likely fires based on where you're
        /// looking rather than the weapon model's actual physical orientation), this gives a
        /// direct visual reference for where a shot would actually land. Reuses the same
        /// raycast-and-position technique already proven to work for the other gaze markers.
        /// </summary>
        public static bool EnableAimReticle = true;
        public static float AimTriggerThreshold = 0.3f;

        private void UpdateAimReticle()
        {
            if (!EnableAimReticle)
            {
                if (_aimReticle != null && _aimReticle.activeSelf) _aimReticle.SetActive(false);
                return;
            }

            var gamepad = Gamepad.current;
            float leftTriggerValue = gamepad != null ? gamepad.leftTrigger.ReadValue() : -1f;
            float rightTriggerValue = gamepad != null ? gamepad.rightTrigger.ReadValue() : -1f;
            bool leftTriggerPulled = gamepad != null && leftTriggerValue > AimTriggerThreshold;
            bool rightTriggerPulled = gamepad != null && rightTriggerValue > AimTriggerThreshold;

            // Left trigger = gun/rifle/shotgun/crossbow aim-down-sights. Right trigger = bow and
            // slingshot draw (can't also use left trigger for these - pressing it cancels the
            // draw without firing). Checking each weapon type against its own correct trigger
            // only, rather than "any trigger + any weapon" - otherwise the reticle could
            // incorrectly show if the wrong trigger is pressed while holding a given weapon.
            // Laser sight check only applies to left-trigger weapons (pistol/rifle) - confirmed
            // via a diagnostic dump that LaserSightMod(Clone) appears under OnRailLocator only
            // while a laser sight is actually attached, and disappears completely once dismantled.
            bool isAiming = (leftTriggerPulled && IsLeftTriggerWeaponDrawn() && !IsLaserSightEquipped()) || (rightTriggerPulled && IsRightTriggerWeaponDrawn());

            // Never show the reticle while a menu/inventory UI is active.
            if (isAiming && !IsUiCursorActive)
            {
                EnsureAimReticle();
                if (_aimReticle != null)
                {
                    _aimReticle.SetActive(true);
                    UpdateAimReticlePosition();
                }
            }
            else if (_aimReticle != null && _aimReticle.activeSelf)
            {
                _aimReticle.SetActive(false);
            }
        }

        // Long-range raycast for aiming - the shared menu marker function uses a 1.5m range
        // tuned for close-up item hovering, which would be useless for aiming a gun or bow at
        // anything beyond point-blank distance.
        public static float AimReticleMaxDistance = 200f;
        public static float AimReticleFallbackDistance = 30f;

        // Calibration offset for the gun/bow's actual point of impact vs. dead-center gaze
        // direction, in degrees. Adjust these if the reticle doesn't line up with where shots
        // actually land - e.g. if shots land lower than the dot, try a small NEGATIVE pitch
        // value (aims the reticle itself down to match); positive pitch aims it up. Yaw works
        // the same for left/right: negative = left, positive = right.
        // Per-weapon pitch/yaw offsets replace these - see PistolPitchOffsetDegrees etc. below.

        // How large the reticle appears, as a fraction of distance (e.g. 0.01 = 1% of distance
        // away, so a target 20m away gets a 20cm sphere - keeps it a consistent, visible size
        // whether you're aiming close or far, instead of a fixed size that's invisible at range.
        // TEMPORARILY boosted for this test build to rule out "rendering but too small/dim to
        // notice" vs. "genuinely not rendering at all" - once confirmed visible, dial back down
        // to a less obtrusive size (comment left here as a reminder to revisit).
        // Now that visibility is confirmed (HDRP/Lit fixed it), back to sensible small-dot
        // sizing rather than the deliberately-oversized diagnostic test values.
        public static float AimReticleAngularScale = 0.015f;
        public static float AimReticleMinScale = 0.03f;
        public static float AimReticleMaxScale = 0.3f;

        /// <summary>
        /// Confirmed via live diagnostic logging: whichever weapon is actually drawn shows up as
        /// an extra active child under the right-hand hierarchy with a name matching the weapon
        /// itself (no "Locator" suffix) - e.g. "CompactPistolHeld" for the pistol,
        /// "CraftedBowHeld" for the bow - alongside its "...Animated(Clone)" rig. Every OTHER
        /// item (axe, book, etc.) only ever shows the permanent "...HeldLocator" anchor points,
        /// which exist regardless of what's actually held. This set can grow as more guns/bows
        /// are confirmed (e.g. Revolver, Rifle, Shotgun, TacticalBow) - add their real
        /// "...Held" object name here once confirmed the same way.
        /// </summary>
        // Confirmed via the held-object name scans (pistol/rifle/bow/slingshot under
        // rightHandHeld). Shotgun and crossbow are DELIBERATELY left out of this set: both
        // attach under a separate WeaponRoot bone as ShotgunPumpActionHeldLocator/
        // CrossbowHeldLocator, but the two are indistinguishable from each other by every
        // signal tried (active state, child structure, position, component list) - both
        // locators are always simultaneously active/identical regardless of which weapon is
        // actually drawn, so any per-weapon offset for them would silently always resolve to
        // whichever one happens to be checked first. Rather than ship a reticle that can't be
        // correctly calibrated for either, it's disabled for both until a real "currently
        // equipped weapon" signal is found elsewhere (worth revisiting later - see
        // GetOffsetsForWeapon below, which still has ShotgunPitchOffsetDegrees/
        // CrossbowPitchOffsetDegrees etc. sitting ready for whenever this gets solved).
        private static readonly System.Collections.Generic.HashSet<string> LeftTriggerWeaponHeldObjectNames =
            new System.Collections.Generic.HashSet<string> { "CompactPistolHeld", "RifleHeld" };
        private static readonly System.Collections.Generic.HashSet<string> RightTriggerWeaponHeldObjectNames =
            new System.Collections.Generic.HashSet<string> { "CraftedBowHeld", "SlingshotHeld" };

        private Transform _rightHandHeldRoot;
        private float _lastRightHandHeldSearchTime = 0f;

        private void FindRightHandHeldRoot()
        {
            if (_rightHandHeldRoot != null || LocalPlayer == null) return;
            if (Time.time - _lastRightHandHeldSearchTime < 1.0f) return;
            _lastRightHandHeldSearchTime = Time.time;

            const string path =
                "PlayerAnimator/Root/Hips/Spine/Spine1/Spine2/RightShoulder/RightArm/RightForeArm/" +
                "RightForeArmTwistNew1/RightForeArmTwistNew2/RightForeArmTwistNew3/RightHand/" +
                "RightHandWeapon/rightHandHeld";

            Transform found = null;
            try
            {
                found = LocalPlayer.transform.Find(path);
            }
            catch
            {
                found = null;
            }

            if (found != null) _rightHandHeldRoot = found;
        }

        private Transform _weaponRootTransform;
        private float _lastWeaponRootSearchTime = 0f;

        /// <summary>
        /// CONFIRMED via whole-player scan: shotgun and crossbow do NOT attach under
        /// rightHandHeld like the pistol/rifle/bow/slingshot do - they attach to a completely
        /// separate bone, WeaponRoot (a direct child of Spine2), as ShotgunPumpActionHeldLocator
        /// and CrossbowHeldLocator respectively. Likely because these are bulkier/back-mounted
        /// weapons rather than simple one-hand-grip items.
        /// </summary>
        private void FindWeaponRoot()
        {
            if (_weaponRootTransform != null || LocalPlayer == null) return;
            if (Time.time - _lastWeaponRootSearchTime < 1.0f) return;
            _lastWeaponRootSearchTime = Time.time;

            const string path = "PlayerAnimator/Root/Hips/Spine/Spine1/Spine2/WeaponRoot";

            Transform found = null;
            try
            {
                found = LocalPlayer.transform.Find(path);
            }
            catch
            {
                found = null;
            }

            if (found != null) _weaponRootTransform = found;
        }

        private bool IsHeldObjectInSet(System.Collections.Generic.HashSet<string> names)
        {
            FindRightHandHeldRoot();
            if (_rightHandHeldRoot != null)
            {
                foreach (Transform child in _rightHandHeldRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (child.gameObject.activeSelf && names.Contains(child.name)) return true;
                }
            }

            FindWeaponRoot();
            if (_weaponRootTransform != null)
            {
                foreach (Transform child in _weaponRootTransform.GetComponentsInChildren<Transform>(true))
                {
                    if (child.gameObject.activeSelf && names.Contains(child.name)) return true;
                }
            }

            return false;
        }

        /// <summary>True only while a left-trigger-aimed weapon (pistol, rifle) is drawn.</summary>
        private bool IsLeftTriggerWeaponDrawn() => IsHeldObjectInSet(LeftTriggerWeaponHeldObjectNames);

        /// <summary>True only while a right-trigger-drawn weapon (bow, slingshot) is drawn.</summary>
        private bool IsRightTriggerWeaponDrawn() => IsHeldObjectInSet(RightTriggerWeaponHeldObjectNames);

        /// <summary>
        /// CONFIRMED via DebugDumpWeaponAttachments diagnostic: the attached laser sight shows up
        /// as an active "LaserSightMod(Clone)" object under the pistol's OnRailLocator (nested
        /// several levels under _rightHandHeldRoot) - present the entire time it's equipped,
        /// completely absent from the hierarchy once dismantled. Searches the whole
        /// _rightHandHeldRoot subtree rather than a fixed depth/path, so it stays correct if the
        /// rifle (or any future left-trigger weapon) mounts a laser sight the same way.
        /// </summary>
        private bool IsLaserSightEquipped()
        {
            FindRightHandHeldRoot();
            if (_rightHandHeldRoot == null) return false;
            foreach (Transform child in _rightHandHeldRoot.GetComponentsInChildren<Transform>(true))
            {
                if (child.gameObject.activeSelf && child.name == "LaserSightMod(Clone)") return true;
            }
            return false;
        }

        public static bool DebugLogWeaponAttachments = false; // CONFIRMED laser sight fix works - diagnostic no longer needed, kept for future reference
        private float _lastWeaponAttachmentDumpTime = 0f;

        /// <summary>
        /// NEW INVESTIGATION: user wants the aim reticle to auto-hide when a laser sight is
        /// attached to the currently held gun (laser sight presumably serves the same purpose the
        /// reticle does, so showing both would be redundant/conflicting), and reappear if it's
        /// removed. No existing detection for this in the codebase - dumping every active child
        /// under the held weapon's own hierarchy (pistol/rifle only, since laser sights are a
        /// left-trigger-weapon attachment) whenever one is drawn, so the actual attachment object
        /// name can be found by diffing a "no laser" dump against a "with laser" dump.
        /// </summary>
        private void DebugDumpWeaponAttachments()
        {
            if (!DebugLogWeaponAttachments) return;
            if (Time.time - _lastWeaponAttachmentDumpTime < 1f) return;
            _lastWeaponAttachmentDumpTime = Time.time;

            if (!IsLeftTriggerWeaponDrawn()) return;
            FindRightHandHeldRoot();
            if (_rightHandHeldRoot == null) return;

            foreach (Transform child in _rightHandHeldRoot.GetComponentsInChildren<Transform>(true))
            {
                if (child == null) continue;
                MelonLogger.Msg($"[VRPlayer][WeaponAttachmentDiag] {GetHierarchyPath(child)} : active={child.gameObject.activeSelf}");
            }
        }


        // Per-weapon offsets: CONFIRMED via user testing that each weapon needs a different
        // correction (this makes sense - the reticle's raycast skip-distance and the head-gaze
        // alignment interact differently depending on how each weapon model is held/animated).
        // PistolPitch/Yaw preserves the originally-tuned values (confirmed correct for the
        // gun specifically). The rest start at 0 (untested) rather than copying the pistol's
        // values blind, since there's no reason to assume they'd need the same correction.
        public static float PistolPitchOffsetDegrees = 4.5f;
        public static float PistolYawOffsetDegrees = 0.5f;
        public static float RiflePitchOffsetDegrees = 3f; // confirmed by user
        public static float RifleYawOffsetDegrees = 0f; // confirmed by user
        public static float BowPitchOffsetDegrees = 0f;
        public static float BowYawOffsetDegrees = 0f;
        public static float SlingshotPitchOffsetDegrees = -4.5f; // confirmed by user
        public static float SlingshotYawOffsetDegrees = 0.5f; // confirmed by user
        // Not yet wired to a real weapon name - shotgun/crossbow held-object names still
        // unconfirmed (see DebugLogHeldWeaponName's shotgun/crossbow hierarchy scan).
        public static float ShotgunPitchOffsetDegrees = 0f;
        public static float ShotgunYawOffsetDegrees = 0f;
        public static float CrossbowPitchOffsetDegrees = 0f;
        public static float CrossbowYawOffsetDegrees = 0f;

        /// <summary>
        /// Returns which specific known weapon is currently held (from either trigger set), or
        /// null if none. Used to select that weapon's own pitch/yaw offset instead of one
        /// shared value for all of them.
        /// </summary>
        private string GetCurrentlyHeldWeaponName()
        {
            FindRightHandHeldRoot();
            if (_rightHandHeldRoot != null)
            {
                foreach (Transform child in _rightHandHeldRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!child.gameObject.activeSelf) continue;
                    if (LeftTriggerWeaponHeldObjectNames.Contains(child.name) || RightTriggerWeaponHeldObjectNames.Contains(child.name))
                    {
                        return child.name;
                    }
                }
            }
            return null;
        }

        private void GetOffsetsForWeapon(string weaponName, out float pitch, out float yaw)
        {
            switch (weaponName)
            {
                case "CompactPistolHeld": pitch = PistolPitchOffsetDegrees; yaw = PistolYawOffsetDegrees; return;
                case "RifleHeld": pitch = RiflePitchOffsetDegrees; yaw = RifleYawOffsetDegrees; return;
                case "CraftedBowHeld": pitch = BowPitchOffsetDegrees; yaw = BowYawOffsetDegrees; return;
                case "SlingshotHeld": pitch = SlingshotPitchOffsetDegrees; yaw = SlingshotYawOffsetDegrees; return;
                // Shotgun/crossbow cases intentionally omitted - see the comment above
                // LeftTriggerWeaponHeldObjectNames for why. ShotgunPitchOffsetDegrees/
                // CrossbowPitchOffsetDegrees etc. still exist below, unused for now, ready for
                // whenever a real per-weapon detection signal is found.
                default: pitch = 0f; yaw = 0f; return;
            }
        }

        private void UpdateAimReticlePosition()
        {
            if (_aimReticle == null || StereoRender == null || StereoRender.Head == null) return;

            Vector3 headPos = StereoRender.Head.position;
            Vector3 direction = StereoRender.Head.forward;

            // Adjustable calibration offset, same idea as CampfireCursorOffsetX/Y for the book
            // cursor: the gun's actual point of impact doesn't necessarily match dead-center
            // gaze direction exactly, so this lets that be tuned without touching raycast logic.
            // Positive pitch = aim up, negative = aim down. Positive yaw = aim right, negative =
            // aim left. Applied as a rotation of the aim direction around the head's own axes.
            // CONFIRMED via user testing: each weapon needs its own separate correction, so this
            // now looks up the specific currently-held weapon's own offset pair.
            GetOffsetsForWeapon(GetCurrentlyHeldWeaponName(), out float pitchOffset, out float yawOffset);
            if (pitchOffset != 0f || yawOffset != 0f)
            {
                Quaternion offsetRotation = Quaternion.AngleAxis(yawOffset, StereoRender.Head.up) *
                                             Quaternion.AngleAxis(pitchOffset, StereoRender.Head.right);
                direction = offsetRotation * direction;
            }

            // Skip the first bit of the ray - the held weapon model is only centimeters from
            // the camera in VR and likely has its own collider, which would get hit FIRST and
            // place the reticle uselessly close (on the gun itself) instead of on the actual
            // distant target. Start the raycast a bit further out to skip past it.
            const float skipDistance = 0.4f;
            Vector3 origin = headPos + direction * skipDistance;

            Vector3 targetPos;
            // Ignore trigger colliders - confirmed via log these were the actual problem
            // (Grabber, TemperatureModifierVolume - invisible player-attached trigger zones
            // for item-grab detection and environmental effects, always ~0.9m away regardless
            // of aim direction, getting hit before any real distant surface).
            bool didHit = Physics.Raycast(origin, direction, out RaycastHit hit, AimReticleMaxDistance - skipDistance, ~0, QueryTriggerInteraction.Ignore);
            if (didHit)
            {
                // Nudge off the surface along its normal so the sphere floats just in front of
                // it instead of sitting exactly at the hit point (half-embedded in the surface
                // it's touching, which invites z-fighting/occlusion at grazing angles).
                targetPos = hit.point + hit.normal * 0.03f;
                _aimReticle.transform.position = targetPos;
                _aimReticle.transform.rotation = Quaternion.LookRotation(-direction, Vector3.up);
            }
            else
            {
                targetPos = origin + direction * AimReticleFallbackDistance;
                _aimReticle.transform.SetPositionAndRotation(targetPos, Quaternion.LookRotation(direction, Vector3.up));
            }

            float distance = Vector3.Distance(headPos, targetPos);
            float scale = Mathf.Clamp(distance * AimReticleAngularScale, AimReticleMinScale, AimReticleMaxScale);
            // Flattened along local Z (the axis facing the player, per the LookRotation calls
            // above) so it reads as a flat disc/coin rather than a 3D ball - same proven
            // sphere+HDRP/Lit recipe, just squashed rather than uniform.
            _aimReticle.transform.localScale = new Vector3(scale, scale, scale * 0.05f);
        }

        private void EnsureAimReticle()
        {
            if (_aimReticle != null) return;

            // BREAKTHROUGH clue from live testing: with the flashlight on, the user could see a
            // shadow being cast by the (otherwise invisible) reticle onto nearby geometry - i.e.
            // the object's geometry/depth genuinely reaches HDRP's shadow pass, but its own
            // surface color never reaches the main opaque color pass. That's true of every
            // approach tried so far: a runtime HDRP/Unlit material, a cloned UI RawImage
            // hierarchy, and a completely untouched default Standard-shader material - all cast
            // shadows fine (once geometry existed) but never produced visible color. The common
            // thread: all three are relatively rare/edge-case shader families for this specific
            // game. Virtually every real object in Sons Of The Forest - terrain, props,
            // characters - uses HDRP/Lit, making it overwhelmingly the shader family most likely
            // to have a valid, compiled runtime variant in this shipped build. Switching to it.
            _aimReticle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _aimReticle.name = "SonsVR_AimReticle";

            var colliderOnClone = _aimReticle.GetComponent<Collider>();
            if (colliderOnClone != null) UnityEngine.Object.Destroy(colliderOnClone);

            var renderer = _aimReticle.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("HDRP/Lit");
                bool usedLit = shader != null;
                if (shader == null) shader = Shader.Find("HDRP/Unlit");

                Color red = new Color(1f, 0.05f, 0.05f, 1f);
                if (shader != null)
                {
                    var mat = new Material(shader);
                    // HDRP/Lit's main color property is _BaseColor (not _Color/_UnlitColor).
                    if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", red);
                    else mat.color = red;
                    if (mat.HasProperty("_EmissiveColor")) mat.SetColor("_EmissiveColor", red * 4f);
                    renderer.material = mat;
                }
                else
                {
                    MelonLogger.Warning("[VRPlayer] Neither HDRP/Lit nor HDRP/Unlit found for aim reticle.");
                }
                // Deliberately leaving shadowCastingMode at its default (On) this round - it's
                // our one confirmed, working signal that the object exists in the right place,
                // useful if we need to fall back to diagnosing again.
                renderer.receiveShadows = false;
                MelonLogger.Msg($"[VRPlayer][AimDiag] Aim reticle created with {(usedLit ? "HDRP/Lit" : "HDRP/Unlit fallback")}.");
            }

            _aimReticle.SetActive(false);
        }

        private GameObject _tarpCursorReticle;

        /// <summary>
        /// Custom gaze cursor for the tarp/inventory, built after the native cursor visual
        /// stopped showing (likely a side effect of the LastUpdate() call now needed to make
        /// item pickup work - see EnforceExclusiveTarpItemHighlight). Reuses the exact same
        /// proven red-sphere recipe as the aim reticle (HDRP/Lit, no collider) rather than
        /// fighting the native visual further. Positioning is simpler than the aim reticle:
        /// no raycast needed, since _cursorController.transform is already exactly where our
        /// own gaze code positions the cursor.
        /// </summary>
        private void EnsureTarpCursorReticle()
        {
            if (_tarpCursorReticle != null) return;

            _tarpCursorReticle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _tarpCursorReticle.name = "SonsVR_TarpCursorReticle";

            var colliderOnClone = _tarpCursorReticle.GetComponent<Collider>();
            if (colliderOnClone != null) UnityEngine.Object.Destroy(colliderOnClone);

            var renderer = _tarpCursorReticle.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("HDRP/Lit");
                if (shader == null) shader = Shader.Find("HDRP/Unlit");

                Color red = new Color(1f, 0.05f, 0.05f, 1f);
                if (shader != null)
                {
                    var mat = new Material(shader);
                    if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", red);
                    else mat.color = red;
                    if (mat.HasProperty("_EmissiveColor")) mat.SetColor("_EmissiveColor", red * 4f);
                    renderer.material = mat;
                }
                renderer.receiveShadows = false;
            }

            _tarpCursorReticle.SetActive(false);
        }

        public static float TarpCursorReticleScale = 0.03f; // small, close-range dot size - unlike the aim reticle this is always viewed up close, no distance-based scaling needed

        public static float TarpCursorMaxDistance = 5f; // short range - tarp browsing is always close-up, unlike the 200m aim reticle

        private void UpdateTarpCursorReticle()
        {
            if (_cursorController == null || StereoRender == null || StereoRender.Head == null)
            {
                if (_tarpCursorReticle != null && _tarpCursorReticle.activeSelf) _tarpCursorReticle.SetActive(false);
                return;
            }

            EnsureTarpCursorReticle();
            if (_tarpCursorReticle == null) return;

            if (!_tarpCursorReticle.activeSelf) _tarpCursorReticle.SetActive(true);

            // FIXED: UpdateCursorPositionFromGaze() (which drives _cursorController.transform)
            // uses a purely formulaic fixed-distance calculation with NO raycast at all - fine
            // for the native selection logic (just needs rough collider overlap within a small
            // snap range), but it's why the reticle floated at a distance that never actually
            // matched the tarp surface. Giving the reticle its own real raycast instead, same
            // hit-and-offset technique as the aim reticle, just much shorter range.
            Vector3 origin = StereoRender.Head.position;
            Vector3 direction = StereoRender.Head.forward;
            bool didHit = Physics.Raycast(origin, direction, out RaycastHit hit, TarpCursorMaxDistance, ~0, QueryTriggerInteraction.Ignore);
            if (didHit)
            {
                _tarpCursorReticle.transform.position = hit.point + hit.normal * 0.03f;
            }
            else
            {
                // Fall back to the same fixed-distance formula the cursor itself uses, so the
                // reticle doesn't vanish or snap somewhere odd when gaze points at empty space.
                float lookDownwardFactor = Mathf.Clamp01(-direction.y);
                float adaptiveDistance = Mathf.Lerp(3.5f, 1.4f, lookDownwardFactor);
                _tarpCursorReticle.transform.position = origin + direction * adaptiveDistance;
            }
            _tarpCursorReticle.transform.rotation = Quaternion.LookRotation(-StereoRender.Head.forward, Vector3.up);
            _tarpCursorReticle.transform.localScale = Vector3.one * TarpCursorReticleScale;
        }

        private void UpdateGazeMarkerByRaycast(Transform target = null)
        {
            target = target != null ? target : (_gazeMarker != null ? _gazeMarker.transform : null);
            if (target == null || StereoRender.Head == null) return;

            Vector3 origin = StereoRender.Head.position;
            Vector3 direction = StereoRender.Head.forward;

            bool didHit = Physics.Raycast(origin, direction, out RaycastHit hit, 1.5f);

            if (Time.time - _lastRaycastLogTime > 1.0f)
            {
                _lastRaycastLogTime = Time.time;
                if (didHit)
                {
                    MelonLogger.Msg($"[VRPlayer] Gaze raycast HIT: {hit.collider.gameObject.name} at distance {hit.distance:F2}m");
                }
                else
                {
                    MelonLogger.Msg("[VRPlayer] Gaze raycast hit NOTHING within 1.5m (items may have no collider, or gaze isn't pointed at the bag).");
                }
            }

            if (didHit)
            {
                target.position = hit.point;
                target.rotation = Quaternion.LookRotation(-direction, Vector3.up);
            }
            else
            {
                // Nothing hit (e.g. the bag's items have no collider to hit at all) - fall back
                // to a fixed close distance so the marker is at least visible near the bag.
                target.SetPositionAndRotation(
                    origin + direction * 0.45f,
                    Quaternion.LookRotation(direction, Vector3.up));
            }
        }

        /// <summary>
        /// Shows the visible marker at exactly wherever a real (but invisible) native cursor
        /// object currently is - pure observation, we never write to the source transform.
        /// </summary>
        private void MirrorMarkerTo(Transform source)
        {
            if (source == null) return;
            EnsureGazeMarker();
            _gazeMarker.SetActive(true);
            _gazeMarker.transform.SetPositionAndRotation(source.position, source.rotation);
        }

        /// <summary>
        /// Automates a workaround the user found by hand: holding the right stick in any
        /// direction while gaze is moving the cursor stops the multi-select bug. We replicate
        /// that continuously by injecting a small synthetic stick value - but ONLY when the
        /// real physical stick is at rest, so genuine navigation input from the player is never
        /// overridden or fought.
        /// </summary>
        private void SuppressGazeMultiSelectByHoldingStick()
        {
            var gamepad = Gamepad.current;
            if (gamepad == null) return;

            Vector2 realStick = gamepad.rightStick.ReadValue();
            if (realStick.sqrMagnitude > 0.01f) return; // player is actively using the stick - hands off

            StateEvent.From(gamepad, out var eventPtr);
            gamepad.rightStick.WriteValueIntoEvent(new Vector2(0f, 0.2f), eventPtr);
            UnityEngine.InputSystem.InputSystem.QueueEvent(eventPtr);
        }

        /// <summary>
        /// Integrates real right-stick input over time into a 2D local offset from the bag's
        /// own transform, starting from a known anchor point set when the bag opens. This is
        /// dead-reckoning: we never read the real cursor's actual position (we can't), we just
        /// estimate where it should be based on the same input driving it. Drift is expected -
        /// this needs calibration via the tunable constants above.
        /// </summary>
        private void UpdateCampfireCursorByDeadReckoning()
        {
            if (_softwareCursor == null || _grabBagHeld == null) return;

            var gamepad = Gamepad.current;
            if (gamepad != null)
            {
                Vector2 stick = gamepad.rightStick.ReadValue();
                _campfireCursorOffset.x += stick.x * CampfireCursorInvertX * CampfireCursorSpeed * Time.deltaTime;
                _campfireCursorOffset.y += stick.y * CampfireCursorInvertY * CampfireCursorSpeed * Time.deltaTime;
                _campfireCursorOffset.x = Mathf.Clamp(_campfireCursorOffset.x, -CampfireCursorMaxOffset, CampfireCursorMaxOffset);
                _campfireCursorOffset.y = Mathf.Clamp(_campfireCursorOffset.y, -CampfireCursorMaxOffset, CampfireCursorMaxOffset);
            }

            // Small forward offset so the cursor sits just in front of the bag's surface
            // rather than exactly on top of it (avoids z-fighting with the item layout).
            Vector3 localPos = new Vector3(_campfireCursorOffset.x, _campfireCursorOffset.y, 0.02f);
            _softwareCursor.transform.position = _grabBagHeld.TransformPoint(localPos);
            _softwareCursor.transform.rotation = _grabBagHeld.rotation;
        }

        /// <summary>
        /// Feeds the game's own real, already-working mouse-driven point-and-click selection
        /// (confirmed via the reference video - mouse+keyboard players hover and left-click to
        /// grab campfire items) instead of trying to replicate or guess at native controller
        /// selection logic. A synthetic mouse position is pinned to screen center every frame -
        /// since the camera tracks your head, looking straight ahead already corresponds to
        /// screen center from the camera's own perspective, so this should track your gaze
        /// automatically without any extra math. Button South (A) triggers a real left-click.
        /// If A conflicts with something else at this menu, tell me and we'll switch buttons.
        /// </summary>
        /// <summary>
        /// Reads the REAL shared cursor position (Mouse.current.position, being driven
        /// natively by the game's own gamepad-to-mouse translation from your stick input -
        /// confirmed by mouse/gamepad control staying on the same position when switching
        /// between them) and casts a ray through the actual game camera to find where that
        /// screen position points in the world. This is the true cursor position, not a guess.
        /// </summary>
        private Mouse _cachedVirtualMouse;
        private Camera _lastLoggedEyeCam;
        private float _lastEyeCamDivergenceLogTime = 0f;

        // ===== Flat menu view (pause/save menu blur fix) =====
        // Sons Of The Forest's in-game pause/save menus aren't built with Canvas or UIDocument
        // (confirmed - no scan activity for them at all), so the world-space-canvas trick used
        // for other UI doesn't apply. Instead: capture whatever's already correctly rendering
        // on the flat/desktop screen (which the user confirms shows the menu correctly) and
        // display it on a quad floating in front of the player in VR.
        // REVERTED to off: confirmed this mechanism freezes the game when it attempts to
        // render (forcing Render() on the VR camera hung the XR pipeline). The pause/save menu
        // and quit confirmation dialog remain unsolved - this approach needs a fundamentally
        // different, non-Render()-based capture method before it's safe to re-enable, or a
        // different strategy entirely.
        // DISABLED for the clean/stable play build: an earlier version of this capture workaround
        // caused a full game freeze. A code comment here claims that specific cause is gone and a
        // safer technique is used now, but that hasn't been independently re-confirmed in this
        // pass - erring toward stability for a build meant for actual play. Re-enable only after
        // a deliberate, isolated test.
        public static bool EnableFlatMenuView = false;
        // Shows the title screen (Solo/Multiplayer/Options/Quit) attached to the player's view
        // in VR, using the same flat-menu-view quad mechanism as the pause menu. On by default
        // since the title screen was previously invisible/unclickable in VR entirely.
        // Switched strategy to ConvertTitleMenuCanvasesToWorldSpace() instead (see call site) -
        // the flat-quad screenshot approach proved unreliable across many attempts. Leaving this
        // flag/mechanism in place but off, in case it's worth revisiting later.
        public static bool ShowTitleScreenInVR = false;
        // Fraction of screen height (measured from the bottom) that the title screen's menu
        // row (Solo/Multiplayer/Options/Quit) occupies - only that strip gets shown instead of
        // the whole title screen artwork. Estimated from a reference screenshot; adjust and
        // recompile if the crop doesn't line up exactly with the real menu row.
        public static float TitleScreenMenuCropFraction = 0.15f;
        // Quick test tunable: pushes the world-space title menu canvas upward, in case its
        // actual button content sits low within its own bounds (near floor/water level) even
        // when the canvas itself is correctly positioned in front of the head. 0 = no offset.
        public static float TitleCanvasTestYOffset = 0.2f; // confirmed by user - "perfect height" for TitleMenu - do not change without explicit testing/request
        public static float OptionsCanvasYOffset = 0.0f; // confirmed by user - "best height" for Options panel
        public static float MultiplayerCanvasYOffset = 0.0f; // confirmed by user - should match Options height (TitleMenu alone needs the higher offset since it sits in water)
        public static float LoadingCanvasYOffset = 0.0f; // untested starting guess, matching Options/Multiplayer - loading screen has no reason to differ but not yet confirmed
        private GameObject _flatMenuQuad;
        private RenderTexture _flatMenuCaptureRT;
        private bool _wasPausedLastFrame = false;

        // ===== Experimental: pause-menu blur theory =====
        // The mod sets UsedCamera.clearFlags=Depth (only clears depth, not color - meant for
        // drawing UI as an overlay on top of the VR eye render) and depth=100, cullingMask=-1.
        // This departs significantly from a normal fully-rendering camera, and this menu blur
        // ONLY happens with the mod active (confirmed) - so it's a real candidate cause. Try
        // temporarily restoring more standard settings while paused, to see if that resolves
        // whatever post-process effect isn't finishing correctly.
        public static bool ExperimentalRestoreCameraOnPause = false;
        private CameraClearFlags _savedClearFlags;
        private int _savedCullingMask;
        private float _savedDepth;
        private bool _cameraSettingsSavedForPause = false;

        /// <summary>
        /// Mouse.current reassigns itself to whichever mouse device was most recently active -
        /// so the moment your real physical mouse moves, it silently steals that reference away
        /// from the stick-driven virtual mouse device, and we lose visibility into it entirely
        /// (matches exactly what you saw: touching the mouse froze the cursor and broke stick
        /// tracking until re-entering the menu). Look specifically for a device whose name
        /// suggests it's the virtual one instead, and cache it once found.
        /// </summary>
        private Mouse FindVirtualMouseDevice()
        {
            if (_cachedVirtualMouse != null && _cachedVirtualMouse.added) return _cachedVirtualMouse;

            foreach (var device in UnityEngine.InputSystem.InputSystem.devices)
            {
                if (device is Mouse m && m.name.ToLowerInvariant().Contains("virtual"))
                {
                    _cachedVirtualMouse = m;
                    MelonLogger.Msg($"[VRPlayer] Found virtual mouse device: {m.name}");
                    return m;
                }
            }

            // Fallback: no distinctly-named virtual mouse device exists, use whatever is current.
            return Mouse.current;
        }

        // ===== Campfire cursor manual tuning - edit these and rebuild to test =====
        // Shifts where the cursor is cast FROM on screen, in pixels, before raycasting.
        // Increase CampfireCursorOffsetX to move the cursor RIGHT. Increase
        // CampfireCursorOffsetY to move the cursor UP. Negative values move the opposite way.
        // Used for LEFT-hand items: grab bag (campfire) and BlueprintBookHeld.
        public static float CampfireCursorOffsetX = 460f; // ZER0INT V9: restored to original fork value; empirical half-offset made selection unusable.
        public static float CampfireCursorOffsetY = 460f; // ZER0INT V9: restored to original fork value; empirical half-offset made selection unusable.

        // Same idea, but for RIGHT-hand items: BackpackQuickSelect and TutorialBookHeld -
        // these need a different offset since it's a different hand/held position.
        public static float RightHandCursorOffsetX = 1600f; // RESTORED, same reason as CampfireCursorOffsetX/Y above
        public static float RightHandCursorOffsetY = 460f; // RESTORED, same reason

        // Separate, independently-tunable cursor offset just for the backpack quick select -
        // deliberately not sharing RightHandCursorOffsetX/Y with the tutorial book, per user's
        // own report that the visual cursor didn't match where real (native, joystick-driven)
        // selection actually landed. Start from the same values as RightHandCursorOffsetX/Y and
        // adjust independently from there based on how it actually looks in game.
        public static float BackpackQuickSelectCursorOffsetX = 1200f; // User-confirmed in-game
        public static float BackpackQuickSelectCursorOffsetY = 380f; // User-confirmed in-game

        // V150: real 3D head-gaze native hover for backpack quick-select (can disable to
        // recover V147's original joystick cursor without changing any other menu).
        public static bool BackpackGazeV150Enabled = true;

        // How quickly the visible dot eases toward its new position each frame, instead of
        // snapping instantly. Higher = snappier/more responsive (closer to instant). Lower =
        // smoother but can lag half a step behind. Try values between 5 (very smooth) and 30
        // (nearly instant) to find what feels stable without jittering or lagging noticeably.
        // Shared by both hands.
        public static float CampfireCursorSmoothSpeed = 30f;

        // How large SoftwareCursor itself gets scaled up, since it's confirmed genuinely visible
        // and working but was described as small/easy to miss. This is the game's own native
        // cursor object's local scale (uniform on all 3 axes), not meters - its native scale is
        // presumably close to 1, so start here and adjust based on how it actually looks in game.
        public static float CampfireCursorScale = 1.5f; // V142: half-size cooking/held-item cursor (visual only).

        // How far the cursor gets pulled back off whatever surface it's resting on (toward the
        // player, along the gaze/mouse ray), in meters - per user's report, the now-larger sphere
        // was sinking about halfway into the held bag since its center sat exactly at the surface.
        // Small values go a long way here - try 0.01-0.03 first.
        public static float CampfireCursorSurfaceLift = 0.02f;

        // How close the raw mouse reading has to be to the exact screen center (in pixels)
        // before it's treated as "the stick just relaxed to neutral" rather than a real,
        // intentional position - per user's report, the cursor was snapping straight back to
        // center the instant the stick was released, since the game's own joystick-to-mouse
        // translation reads a relaxed stick as dead center, indistinguishable from having never
        // moved it at all. Below this distance, the cursor's target position update is skipped
        // entirely, holding wherever it last was instead of following the reset back to center.
        // If real, intentional cursor positions near the exact center are getting incorrectly
        // ignored, lower this; if it still snaps back sometimes, raise it.
        public static float CampfireCursorNeutralDeadzone = 15f;

        // Fixed distance (meters, along the mouse-derived ray from the camera) the cursor sits
        // at for the booklet, instead of a raycast hit point - the raycast was confirmed via
        // diagnostic log to hit literally nothing in this direction at all (hitCount=0 on every
        // poll), so there's nothing to land the cursor on regardless. Value is the user's own,
        // tested directly in-game.
        public static float BookletCursorFixedDistance = 0.19f; // user-tuned in-game, refined from 0.17. RENAMED from CampfireCursorFixedDistance - this is exclusively for the booklet (BlueprintBookHeld), not campfire-related at all; the grab bag uses the raycast-based cursor instead.

        // Same idea as BookletCursorFixedDistance, for the tutorial book instead (shares the
        // "right hand" offset branch with the backpack quick select, which uses the raycast-based
        // cursor instead - see usesRaycastCursor). Per user's request, applying the same fix -
        // started at the construction booklet's own tuned value (0.19) as a reasonable first
        // guess, since it's a similarly book-like UI likely to have the same "nothing there to
        // raycast onto" situation - adjust if it's held at a different distance than that one.
        public static float RightHandCursorFixedDistance = 0.19f;

        // Per user's request: the cursor reads a bit large for the booklet specifically at the
        // same 3x scale used everywhere else - a separate, smaller scale just for that menu.
        public static float BookletCursorScale = 1f; // V142: half-size construction/tutorial booklet cursor (visual only).

        private float _lastCampfireMouseDiagTime = 0f;
        private float _lastBookletRaycastDiagTime = 0f;
        private Vector3 _campfireCursorOwnPos;
        private Quaternion _campfireCursorOwnRot;
        private bool _campfireCursorOwnPosInitialized = false;

        /// <summary>
        /// Writes the screen center to the virtual mouse's own position - see the call site's
        /// own comment for why this correctly represents gaze direction, no raycasting needed.
        /// InputState.Change is the standard Input System API for programmatically setting a
        /// control's value, the same category of mechanism already used elsewhere in this project
        /// for synthetic button presses (StateEvent/InputSystem.QueueEvent), just for a value
        /// control instead of a button.
        /// </summary>
        private void WriteGazePositionToVirtualMouse()
        {
            var mouse = FindVirtualMouseDevice();
            if (mouse == null) return;

            Vector2 screenCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            InputState.Change(mouse.position, screenCenter);
        }

        private float _lastGrabBagGazeDiagTime = 0f;
        private bool _trueClosestAnyUsedFallback = false;
        private int _trueClosestAnyColliderCount = 0;
        private int _trueClosestAnyUsableColliderCount = 0;

        /// <summary>
        /// CONFIRMED via flat-screen diagnostic (a separate, standalone DebugTool build made
        /// specifically for this investigation): GrabBagLayoutItem carries its own IsHighlighted
        /// flag, driven by its own private OnMouseEnter/OnMouseExit/OnMouseOver methods -
        /// confirmed present via a method dump, and confirmed IsHighlighted actually flips to
        /// true during a real controller-driven selection (owner names seen: CookingPotLayoutItem,
        /// LeafLayoutItem, MeatLayoutItem, MoneyLayoutItem). Directly calling these via
        /// reflection - exactly matching what a real mouse hover does - rather than the tarp's
        /// more complex approach of syncing a separate controller's own tracked-item field, since
        /// this item-level mechanism turned out to be much simpler for this particular UI.
        /// Campfire/grab-bag only for now - the construction booklet showed no GrabBagLayoutItem
        /// instances during testing, so it likely uses a different, not-yet-identified class.
        /// </summary>
        /// <summary>
        /// Proven technique, ported directly from the standalone flat-screen diagnostic tool
        /// built for this same investigation: a generic Component reference from
        /// GetComponents&lt;Component&gt;() wraps the right underlying native object, but its own
        /// managed wrapper type is still just the generic Component - not the more specific type
        /// reflection actually needs to invoke members correctly. Casts via IL2CPP interop's own
        /// Cast&lt;T&gt;() method, called reflectively since the target type is only known at
        /// runtime here (no compile-time using statement for it).
        /// </summary>
        private static object CastToType(object instance, Type targetType)
        {
            if (instance == null || targetType == null) return null;
            try
            {
                var castMethod = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("Cast")?.MakeGenericMethod(targetType);
                return castMethod?.Invoke(instance, null);
            }
            catch (Exception e)
            {
                MelonLogger.Msg($"[VRPlayer] CastToType to {targetType.Name} failed: {e.Message}");
                return null;
            }
        }

        private Type _grabBagLayoutItemDirectType;
        private bool _grabBagLayoutItemDirectTypeSearched = false;
        private PropertyInfo _grabBagIsHighlightedProp;
        private Component _lastHighlightedGrabBagLayoutItem;

        /// <summary>
        /// PIVOTED (confirmed via log: the diagnostic walk-up from the hit MouseEventsProxy never
        /// found a GrabBagLayoutItem anywhere in its ancestor chain, across multiple attempts -
        /// they're simply unrelated objects, not parent/child at all, despite MouseEventsProxy's
        /// OnMouseEnterCollider executing without error every time. That whole approach was
        /// chasing a connection that doesn't exist). Abandoned MouseEventsProxy entirely.
        /// Instead: LayoutItem (GrabBagLayoutItem's own base class) exposes IsHighlighted as a
        /// plain read/write property directly on the item itself - found via an unfiltered
        /// member dump. Finds whichever GrabBagLayoutItem is physically closest to the gaze hit
        /// point in world space, and sets IsHighlighted on it directly - no proxy, no event
        /// forwarding, no hierarchy assumptions.
        /// </summary>
        private Type _quickSelectLayoutItemType;
        private bool _quickSelectLayoutItemTypeSearched = false;
        private LayoutItemCursorSnapping _backpackLayoutCursorSnapping;
        private bool _backpackLayoutCursorSnappingSearched = false;
        private PropertyInfo _backpackLayoutSnappedItemProp;
        private PropertyInfo _backpackSnapAnchorTrackedObjectProp;
        private PropertyInfo _backpackLayoutSnapPointProp;
        private LayoutItem _lastBackpackSnapAnchorSearchItem;
        private float _lastBackpackGazeDiagTime = 0f;
        private float _lastBackpackPersistenceCheckTime = 0f;
        private float _lastBackpackAnchorRepeatCheckTime = 0f;
        private float _lastBackpackCursorVisibilityDiagTime = 0f;
        private LayoutItem _lastBackpackWrittenItem;
        private MethodInfo _backpackTrySnapMethod;

        /// <summary>
        /// Adapts the tarp's own proven LayoutItemCursorSnapping mechanism
        /// (EnforceExclusiveTarpItemHighlight) for the backpack quick select - confirmed via
        /// diagnostic that this menu's own "CursorController" object (under BackpackHeld) is
        /// that exact same class, and its items are QuickSelectLayoutItem (a LayoutItem
        /// subclass, the same base type _currentlySnappedLayoutItem already expects) rather than
        /// GrabBagLayoutItem. Finds whichever QuickSelectLayoutItem is physically closest to the
        /// gaze hit point, and syncs both _currentlySnappedLayoutItem and _currentSnapPoint (via
        /// the same _snapAnchors/TrackedObject lookup used for the tarp) directly on this menu's
        /// own LayoutItemCursorSnapping instance.
        /// </summary>
        private void UpdateBackpackQuickSelectGazeSelection()
        {
            if (_backpackQuickSelect == null) return;

            if (!_backpackLayoutCursorSnappingSearched)
            {
                _backpackLayoutCursorSnappingSearched = true;
                Transform backpackHeld = _backpackQuickSelect.parent;
                Transform cursorControllerT = backpackHeld != null ? backpackHeld.Find("CursorController") : null;
                _backpackLayoutCursorSnapping = cursorControllerT != null ? cursorControllerT.GetComponent<LayoutItemCursorSnapping>() : null;
                MelonLogger.Msg($"[VRPlayer] Backpack LayoutItemCursorSnapping found: {(_backpackLayoutCursorSnapping != null)}");

                // DIAGNOSTIC: field writes are confirmed reliable (stable for 9+ seconds, no
                // overwriting) but produce no visible/functional effect, and the _snapAnchors
                // approach that worked for the tarp shows this list permanently empty here even
                // after the menu's been open a while - genuinely different from what worked
                // before. Rather than keep guessing which fields matter, dumping every method this
                // class actually has - there may be a proper "select/snap to this item" method
                // that also handles setup our raw field writes are missing entirely.
                if (_backpackLayoutCursorSnapping != null)
                {
                    MelonLogger.Msg("[VRPlayer][BackpackMethodDump] LayoutItemCursorSnapping methods:");
                    foreach (var m in typeof(LayoutItemCursorSnapping).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        var paramList = string.Join(", ", Array.ConvertAll<ParameterInfo, string>(m.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"));
                        MelonLogger.Msg($"[VRPlayer][BackpackMethodDump]   {m.ReturnType.Name} {m.Name}({paramList})");
                    }
                }
                if (_cursorController != null)
                {
                    MelonLogger.Msg("[VRPlayer][BackpackMethodDump] InventoryCursorController methods:");
                    foreach (var m in typeof(InventoryCursorController).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        var paramList = string.Join(", ", Array.ConvertAll<ParameterInfo, string>(m.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"));
                        MelonLogger.Msg($"[VRPlayer][BackpackMethodDump]   {m.ReturnType.Name} {m.Name}({paramList})");
                    }
                }
            }
            if (_backpackLayoutCursorSnapping == null) return;

            // FIXED (confirmed via log: this is the ONLY LayoutItemCursorSnapping instance in the
            // entire scene, active and enabled - "wrong instance" is ruled out. But 229 is a
            // specific, non-trivial number, meaning this list clearly WAS allocated to hold real
            // entries - they just aren't populated yet at the exact moment we first checked, right
            // as the menu opens. Re-checking repeatedly instead of once, to see if population
            // happens on a later frame after the menu has been open a while).
            if (Time.time - _lastBackpackAnchorRepeatCheckTime > 1f)
            {
                _lastBackpackAnchorRepeatCheckTime = Time.time;
                var snapAnchorsProp = typeof(LayoutItemCursorSnapping).GetProperty("_snapAnchors", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                int nonNullCount = 0;
                int totalCount = 0;
                try
                {
                    var anchorsNow = snapAnchorsProp?.GetValue(_backpackLayoutCursorSnapping);
                    if (anchorsNow != null)
                    {
                        var listType = anchorsNow.GetType();
                        var countProp = listType.GetProperty("Count");
                        var indexerProp = listType.GetProperty("Item");
                        totalCount = countProp != null ? (int)countProp.GetValue(anchorsNow) : 0;
                        for (int i = 0; i < totalCount; i++)
                        {
                            var anchor = indexerProp?.GetValue(anchorsNow, new object[] { i });
                            if (anchor == null) continue;
                            var trackedProp = anchor.GetType().GetProperty("TrackedObject", BindingFlags.Public | BindingFlags.Instance);
                            if (trackedProp?.GetValue(anchor) != null) nonNullCount++;
                        }
                    }
                }
                catch (Exception e) { MelonLogger.Msg($"[VRPlayer][BackpackScanDiag] repeat check threw: {e.Message}"); }
                MelonLogger.Msg($"[VRPlayer][BackpackScanDiag] repeat check: anchorCount={totalCount} nonNullTracked={nonNullCount}");
            }

            // DIAGNOSTIC (independent, throttled, read-only - no write happens here at all):
            // testing directly whether a PREVIOUS poll's write is still holding a moment later,
            // or whether something else (most likely the native system, still actively running
            // and still able to drive real selection via the joystick per the user's own report)
            // is silently overwriting it in between our own polls. If this shows something other
            // than what we last wrote, that confirms an ongoing overwrite rather than our fix
            // simply being incomplete.
            if (_backpackLayoutSnappedItemProp != null && Time.time - _lastBackpackPersistenceCheckTime > 0.5f)
            {
                _lastBackpackPersistenceCheckTime = Time.time;
                var currentValue = _backpackLayoutSnappedItemProp.GetValue(_backpackLayoutCursorSnapping) as LayoutItem;
                MelonLogger.Msg($"[VRPlayer][BackpackPersistDiag] currentValue={(currentValue != null ? currentValue.name : "null")} lastWeWrote={(_lastBackpackWrittenItem != null ? _lastBackpackWrittenItem.name : "null")} stillMatches={ReferenceEquals(currentValue, _lastBackpackWrittenItem)}");
            }

            if (!_quickSelectLayoutItemTypeSearched)
            {
                _quickSelectLayoutItemTypeSearched = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t.Name == "QuickSelectLayoutItem") { _quickSelectLayoutItemType = t; break; }
                    }
                    if (_quickSelectLayoutItemType != null) break;
                }
                MelonLogger.Msg($"[VRPlayer] QuickSelectLayoutItem type found: {(_quickSelectLayoutItemType != null)}");
            }
            if (_quickSelectLayoutItemType == null) return;
            if (StereoRender == null || StereoRender.Head == null) return;

            // Same forgiving 5-ray gaze pattern proven for the grab bag.
            Vector3 origin = StereoRender.Head.position;
            Vector3 baseDir = StereoRender.Head.forward;
            Vector3? centerRayClosestHitPoint = null;
            {
                var hitsForCenterRay = Physics.RaycastAll(origin, baseDir, 1.5f);
                float closestDist = float.MaxValue;
                foreach (var h in hitsForCenterRay)
                {
                    if (h.collider != null && h.collider.isTrigger) continue;
                    if (h.distance < closestDist) { closestDist = h.distance; centerRayClosestHitPoint = h.point; }
                }
            }
            if (centerRayClosestHitPoint == null) return;

            const float maxSelectDistance = 0.4f;
            LayoutItem closestItem = null;
            float closestItemDist = maxSelectDistance;
            var il2cppType = Il2CppInterop.Runtime.Il2CppType.From(_quickSelectLayoutItemType);
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(il2cppType))
            {
                var comp = obj.TryCast<Component>();
                if (comp == null || comp.gameObject == null || !comp.gameObject.activeInHierarchy) continue;

                // Same collider-surface-then-renderer-bounds fallback proven for the grab bag.
                float dist = float.MaxValue;
                var colliders = comp.GetComponentsInChildren<Collider>();
                if (colliders != null && colliders.Length > 0)
                {
                    foreach (var col in colliders)
                    {
                        if (col == null || !col.enabled) continue;
                        try
                        {
                            Vector3 closest = col.ClosestPoint(centerRayClosestHitPoint.Value);
                            float d = Vector3.Distance(closest, centerRayClosestHitPoint.Value);
                            if (d < dist) dist = d;
                        }
                        catch { /* non-convex mesh collider or similar - skip this one */ }
                    }
                }
                if (dist == float.MaxValue)
                {
                    Vector3 refPoint = comp.transform.position;
                    var renderers = comp.GetComponentsInChildren<Renderer>();
                    if (renderers != null && renderers.Length > 0)
                    {
                        Bounds combined = renderers[0].bounds;
                        for (int i = 1; i < renderers.Length; i++) combined.Encapsulate(renderers[i].bounds);
                        refPoint = combined.center;
                    }
                    dist = Vector3.Distance(refPoint, centerRayClosestHitPoint.Value);
                }

                if (dist < closestItemDist)
                {
                    closestItemDist = dist;
                    closestItem = comp.TryCast<LayoutItem>();
                }
            }

            // Per user's request: visual cursor should follow gaze here, since gaze - not mouse
            // position - is what actually drives selection for this menu. Positions
            // SoftwareCursor directly at the gaze hit point, same "write-only, never read this
            // object's own transform back" lesson learned earlier (it gets reset independently
            // by the game's own UI system) - just written fresh from our own local value each
            // poll instead of smoothed from a stored one, since this function doesn't keep its
            // own persistent position/rotation state the way the grab bag's cursor code does.
            FindSoftwareCursor();
            if (_softwareCursor != null)
            {
                if (!_softwareCursor.active) _softwareCursor.active = true;
                _softwareCursor.transform.position = centerRayClosestHitPoint.Value;
                _softwareCursor.transform.rotation = Quaternion.LookRotation(-baseDir, Vector3.up);
                _softwareCursor.transform.localScale = Vector3.one * CampfireCursorScale;

                // DIAGNOSTIC (per user's report: cursor visible on first open of this menu, but
                // not on subsequent opens - a specific, reproducible pattern). .active only
                // checks this object's OWN local flag, not whether it's actually visible given its
                // full parent chain - if some ancestor gets deactivated on close and isn't
                // properly reactivated on a later open, .active could still read true while the
                // cursor is genuinely invisible. Checking activeInHierarchy and the immediate
                // parent's own state too, to see if that's the real story here.
                if (Time.time - _lastBackpackCursorVisibilityDiagTime > 1f)
                {
                    _lastBackpackCursorVisibilityDiagTime = Time.time;
                    string parentInfo = _softwareCursor.transform.parent != null
                        ? $"{_softwareCursor.transform.parent.name} activeSelf={_softwareCursor.transform.parent.gameObject.activeSelf}"
                        : "no parent";
                    MelonLogger.Msg($"[VRPlayer][BackpackCursorVisDiag] active={_softwareCursor.active} activeInHierarchy={_softwareCursor.activeInHierarchy} parent=[{parentInfo}]");
                }
            }

            // Sync onto LayoutItemCursorSnapping, exact same technique as the tarp's own
            // EnforceExclusiveTarpItemHighlight.
            try
            {
                if (_backpackLayoutSnappedItemProp == null)
                {
                    _backpackLayoutSnappedItemProp = _backpackLayoutCursorSnapping.GetType()
                        .GetProperty("_currentlySnappedLayoutItem", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    // DIAGNOSTIC: no exceptions were thrown last round, but that doesn't confirm
                    // the write actually happened - if this reflection lookup silently returned
                    // null, the SetValue call below would be quietly skipped by the guard, with
                    // no error at all. Confirming the lookup itself succeeded, once.
                    MelonLogger.Msg($"[VRPlayer] Backpack _currentlySnappedLayoutItem property found: {(_backpackLayoutSnappedItemProp != null)}, canWrite: {(_backpackLayoutSnappedItemProp?.CanWrite)}");
                }
                var existing = _backpackLayoutSnappedItemProp?.GetValue(_backpackLayoutCursorSnapping) as LayoutItem;
                if (existing != closestItem && _backpackLayoutSnappedItemProp != null && _backpackLayoutSnappedItemProp.CanWrite)
                {
                    _backpackLayoutSnappedItemProp.SetValue(_backpackLayoutCursorSnapping, closestItem);
                    _lastBackpackWrittenItem = closestItem;

                    // DIAGNOSTIC: reading the value straight back, in the same poll, right after
                    // writing it - "no highlighting despite no errors" matches the same
                    // "looks successful but produces no effect" pattern hit before with
                    // MouseEventsProxy. This confirms directly whether the write is actually
                    // sticking, or being silently reverted/ignored by something else (e.g. this
                    // component's own internal Update() logic recalculating it independently).
                    var readBack = _backpackLayoutSnappedItemProp.GetValue(_backpackLayoutCursorSnapping) as LayoutItem;
                    MelonLogger.Msg($"[VRPlayer][BackpackWriteDiag] wrote={(closestItem != null ? closestItem.name : "null")} readBackImmediately={(readBack != null ? readBack.name : "null")} matches={ReferenceEquals(readBack, closestItem)}");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[VRPlayer] Backpack: failed to set _currentlySnappedLayoutItem: {e.Message}");
            }

            // NEW EXPERIMENT: TrySnapToNearestPoint(Vector3 movementDirection) - found via a
            // method dump, suggests the native system navigates by DIRECTION relative to the
            // current snap point, not by direct item selection. Our raw field writes are
            // confirmed reliable but silent - this may be the actual entry point whose internal
            // logic drives visible highlight/functional selection, which direct field writes
            // bypass entirely. Calling it with the direction from gaze toward the target item, as
            // a first, simple test of whether calling it at all produces any visible effect -
            // not replacing the field writes above yet, just adding this alongside them.
            try
            {
                if (_backpackTrySnapMethod == null)
                {
                    _backpackTrySnapMethod = _backpackLayoutCursorSnapping.GetType()
                        .GetMethod("TrySnapToNearestPoint", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    MelonLogger.Msg($"[VRPlayer] Backpack TrySnapToNearestPoint method found: {(_backpackTrySnapMethod != null)}");
                }
                if (_backpackTrySnapMethod != null && closestItem != null)
                {
                    Vector3 snapDirection = (closestItem.transform.position - origin).normalized;
                    _backpackTrySnapMethod.Invoke(_backpackLayoutCursorSnapping, new object[] { snapDirection });
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[VRPlayer] Backpack: TrySnapToNearestPoint invoke failed: {e.Message}");
            }

            // NEW: same second sync the tarp needed (EnforceExclusiveTarpItemHighlight) - a
            // SEPARATE InventoryCursorController object has its OWN copy of
            // _currentlySnappedLayoutItem, distinct from LayoutItemCursorSnapping's, and it's the
            // one that actually drives pickup - LayoutItemCursorSnapping alone only drives the
            // visual highlight. _cursorController itself is found via a player-wide search
            // (LocalPlayer.GetComponentInChildren, not scoped to the tarp), so there's a good
            // chance it's the same shared controller here too - worth syncing regardless of
            // which menu is currently open.
            if (_cursorController != null)
            {
                try
                {
                    if (_cursorControllerSnappedItemProp == null)
                    {
                        _cursorControllerSnappedItemProp = _cursorController.GetType()
                            .GetProperty("_currentlySnappedLayoutItem", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    if (_cursorControllerSnappedItemProp != null && _cursorControllerSnappedItemProp.CanWrite)
                    {
                        var existingOnController = _cursorControllerSnappedItemProp.GetValue(_cursorController) as LayoutItem;
                        if (existingOnController != closestItem)
                        {
                            _cursorControllerSnappedItemProp.SetValue(_cursorController, closestItem);
                            var readBackController = _cursorControllerSnappedItemProp.GetValue(_cursorController) as LayoutItem;
                            MelonLogger.Msg($"[VRPlayer][BackpackWriteDiag] InventoryCursorController wrote={(closestItem != null ? closestItem.name : "null")} readBackImmediately={(readBackController != null ? readBackController.name : "null")} matches={ReferenceEquals(readBackController, closestItem)}");
                        }
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[VRPlayer] Backpack: failed to sync InventoryCursorController's snapped item: {e.Message}");
                }
            }

            if (closestItem != _lastBackpackSnapAnchorSearchItem)
            {
                _lastBackpackSnapAnchorSearchItem = closestItem;
                try
                {
                    object matchedAnchor = null;
                    int anchorCount = 0;
                    if (closestItem != null)
                    {
                        var anchorsProp = _backpackLayoutCursorSnapping.GetType()
                            .GetProperty("_snapAnchors", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var anchors = anchorsProp?.GetValue(_backpackLayoutCursorSnapping);
                        if (anchors != null)
                        {
                            var listType = anchors.GetType();
                            var countProp = listType.GetProperty("Count");
                            var indexerProp = listType.GetProperty("Item");
                            int count = countProp != null ? (int)countProp.GetValue(anchors) : 0;
                            anchorCount = count;
                            // Property lookup needs to happen before either loop uses it - moved
                            // here since the diagnostic loop below runs first.
                            if (count > 0 && _backpackSnapAnchorTrackedObjectProp == null)
                            {
                                var firstAnchor = indexerProp?.GetValue(anchors, new object[] { 0 });
                                if (firstAnchor != null)
                                {
                                    _backpackSnapAnchorTrackedObjectProp = firstAnchor.GetType().GetProperty("TrackedObject", BindingFlags.Public | BindingFlags.Instance);
                                }
                            }
                            // FIXED (confirmed via log: zero non-null tracked objects found - but
                            // we'd only ever actually sampled the first 5 out of 229, since
                            // nameMatch never triggered to pull in others. The remaining 224 could
                            // easily hold valid, non-null tracked objects never looked at). Now
                            // logging every single non-null anchor across the FULL list, not a
                            // small sample - this is the direct way to see what's actually there.
                            int nonNullCount = 0;
                            for (int i = 0; i < count; i++)
                            {
                                var anchorPeek = indexerProp?.GetValue(anchors, new object[] { i });
                                if (anchorPeek == null) continue;
                                var trackedPeek = _backpackSnapAnchorTrackedObjectProp?.GetValue(anchorPeek);
                                if (trackedPeek == null) continue;
                                nonNullCount++;
                                string trackedDesc;
                                if (trackedPeek is UnityEngine.Object uo) trackedDesc = $"{trackedPeek.GetType().Name}:{uo.name}";
                                else trackedDesc = $"{trackedPeek.GetType().Name}:{trackedPeek}";
                                bool nameMatch = closestItem != null && trackedDesc.IndexOf(closestItem.name.Replace("LayoutItem", ""), StringComparison.OrdinalIgnoreCase) >= 0;
                                MelonLogger.Msg($"[VRPlayer][BackpackWriteDiag] anchor[{i}] tracked={trackedDesc} nameMatch={nameMatch}");
                            }
                            MelonLogger.Msg($"[VRPlayer][BackpackWriteDiag] total non-null tracked objects across all {count} anchors: {nonNullCount}");
                            for (int i = 0; i < count; i++)
                            {
                                var anchor = indexerProp?.GetValue(anchors, new object[] { i });
                                if (anchor == null) continue;
                                var tracked = _backpackSnapAnchorTrackedObjectProp?.GetValue(anchor);
                                // FIXED (confirmed via log: anchorCount=229 - plenty of
                                // candidates - yet matchedAnchor was null every single time).
                                // ReferenceEquals compares MANAGED wrapper identity, but two
                                // separate casts/reads of the same underlying native IL2CPP
                                // object can produce different wrapper instances - the same
                                // category of trap hit before elsewhere in this project. Comparing
                                // native pointers directly instead, via each object's own
                                // Il2CppObjectBase.Pointer, which reflects the actual underlying
                                // object regardless of which managed wrapper instance holds it.
                                IntPtr trackedPtr = (tracked as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.Pointer ?? IntPtr.Zero;
                                IntPtr itemPtr = (closestItem as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.Pointer ?? IntPtr.Zero;
                                IntPtr itemGoPtr = (closestItem.gameObject as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.Pointer ?? IntPtr.Zero;
                                if (trackedPtr != IntPtr.Zero && (trackedPtr == itemPtr || trackedPtr == itemGoPtr))
                                {
                                    matchedAnchor = anchor;
                                    break;
                                }
                            }
                        }
                        // DIAGNOSTIC: the _currentlySnappedLayoutItem write is confirmed sticking
                        // (verified via immediate readback), yet still no highlight or pickup - the
                        // _currentSnapPoint half of the sync hasn't been verified at all yet. If
                        // this system needs BOTH set consistently to register a real selection,
                        // and this anchor search is silently finding nothing (anchorCount=0, or
                        // TrackedObject never matching), that would fully explain the symptom.
                        MelonLogger.Msg($"[VRPlayer][BackpackWriteDiag] snapAnchor search: anchorCount={anchorCount} matchedAnchor={(matchedAnchor != null ? "found" : "null")}");
                    }

                    if (_backpackLayoutSnapPointProp == null)
                    {
                        _backpackLayoutSnapPointProp = _backpackLayoutCursorSnapping.GetType().GetProperty("_currentSnapPoint", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    if (_backpackLayoutSnapPointProp != null && _backpackLayoutSnapPointProp.CanWrite)
                    {
                        _backpackLayoutSnapPointProp.SetValue(_backpackLayoutCursorSnapping, matchedAnchor);
                    }

                    // Same second sync as _currentlySnappedLayoutItem above - mirroring onto
                    // InventoryCursorController's own _currentSnapPoint too.
                    if (_cursorController != null)
                    {
                        if (_cursorControllerSnapPointProp == null)
                        {
                            _cursorControllerSnapPointProp = _cursorController.GetType().GetProperty("_currentSnapPoint", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        }
                        if (_cursorControllerSnapPointProp != null && _cursorControllerSnapPointProp.CanWrite)
                        {
                            _cursorControllerSnapPointProp.SetValue(_cursorController, matchedAnchor);
                        }
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[VRPlayer] Backpack: failed to sync snap point: {e.Message}");
                }
            }

            if (Time.time - _lastBackpackGazeDiagTime > 0.5f)
            {
                _lastBackpackGazeDiagTime = Time.time;
                MelonLogger.Msg($"[VRPlayer][BackpackGazeDiag] closestItem={(closestItem != null ? closestItem.name : "none")} dist={closestItemDist:F3}");
            }
        }

        /// <summary>
        /// zer0int v11: menu-specific view/performance state for the gaze-driven campfire bag.
        ///
        /// 1) Uses the same 50m far clip that already fixed the tarp's severe menu-only GPU
        ///    cost. SetFarClipDistance only changes the eye camera far planes/projection matrices;
        ///    it does not reallocate the eye RenderTextures.
        ///
        /// 2) While the grab bag is active, keep Origin position synchronized to MainCameraFP
        ///    with the SAME positionOffset used during normal gameplay. Generic menu handling
        ///    skips this synchronization because inventory/pause cameras have different meanings,
        ///    but the grab bag never switches cameras: MainCameraFP remains the native
        ///    first-person anchor and moves with the held-bag/body animation. Freezing Origin
        ///    while that animation runs is what can leave the VR head sitting over a shoulder.
        ///
        /// Position only: never copy pitch/roll/yaw here; real HMD tracking owns orientation.
        /// </summary>
        private void UpdateGrabBagViewStateV11(bool grabBagActive)
        {
            if (grabBagActive != _grabBagViewWasActiveV11)
            {
                if (StereoRender != null)
                {
                    float targetFarClip = (grabBagActive && GrabBagFarClipEnabled)
                        ? GrabBagFarClipMeters
                        : (InventoryCamIsActive && TarpFarClipEnabled
                            ? TarpFarClipMeters
                            : global::SonsVRMod.StereoRender.GameplayFarClipMeters);
                    StereoRender.SetFarClipDistance(targetFarClip);
                }

                MelonLogger.Msg($"[VRPlayer][GrabBagViewV11] active={grabBagActive} farClip={(grabBagActive && GrabBagFarClipEnabled ? GrabBagFarClipMeters : global::SonsVRMod.StereoRender.GameplayFarClipMeters):F1}m Origin={(Origin != null ? Origin.position.ToString() : "N/A")} MainCameraFP={(UsedCamera != null ? UsedCamera.transform.position.ToString() : "N/A")}");
                _grabBagViewWasActiveV11 = grabBagActive;
            }

            if (!grabBagActive || Origin == null || UsedCamera == null) return;
            if (!UsedCamera.name.Equals("MainCameraFP")) return;

            Vector3 basePosition = UsedCamera.transform.position;
            Vector3 finalPosition = basePosition + UsedCamera.transform.TransformDirection(positionOffset);
            Origin.position = finalPosition;
        }

        // ---------------------------------------------------------------------
        // zer0int v10 CAMPFIRE / GRAB-BAG GAZE SELECTION
        // ---------------------------------------------------------------------
        // The grab bag is a real 3D menu held out beside the fire. Unlike the construction
        // booklet, translating a 2D VirtualMouse position back into 3D produces an inherently
        // misleading "floating screwdriver" because the visible marker and the game's native
        // hover selection are separate coordinate pipelines. V10 stops touching VirtualMouse for
        // this menu. Head gaze directly raycasts the real GrabBagLayoutItem colliders and invokes
        // the same OnMouseEnter/OnMouseOver/OnMouseExit callbacks the item already exposes.
        // Trigger input is deliberately untouched: the experiment is whether Sons' own hover
        // lifecycle establishes enough native state for its existing trigger/click action to work.
        private MethodInfo _grabBagOnMouseEnterV10;
        private MethodInfo _grabBagOnMouseOverV10;
        private MethodInfo _grabBagOnMouseExitV10;
        private bool _grabBagCallbacksSearchedV10 = false;
        private Component _grabBagGazeHoveredV10;
        private float _grabBagGazeV10DiagTime = 0f;

        // ZER0INT V24: CookingCog is a separate native interaction class from
        // GrabBagLayoutItem.  The ingredient gaze fix therefore excluded the actual
        // start-cooking cog entirely.  Extend the same native-hover strategy to
        // Il2CppSons.Gameplay.Cooking.CookingCog while leaving trigger input untouched.
        private Type _cookingCogTypeV24;
        private bool _cookingCogTypeSearchedV24 = false;
        private MethodInfo _cookingCogOnMouseEnterV24;
        private MethodInfo _cookingCogOnMouseOverV24;
        private MethodInfo _cookingCogOnMouseExitV24;
        private Component _cookingCogGazeHoveredV24;
        private bool _cookingCogReflectionLoggedV24 = false;

        // ZER0INT V107 PERFORMANCE FIX — the V106 profiler caught the cooking menu's 4 FPS
        // pathology red-handed: TWO full-scene FindObjectsOfType(Type) calls were executed every
        // Update (GrabBagLayoutItem + CookingCog), averaging ~116 ms EACH. The actual collider/
        // renderer traversal after those searches was only ~0.2 ms. Cache the candidate components
        // instead. We rebuild cheaply from GrabBagHeld's local hierarchy (including inactive
        // children) every 0.5 s so native activation/deactivation still works, and use at most a
        // tiny number of global ACTIVE fallbacks per menu session for a CookingCog that may live
        // outside the held-bag hierarchy. Never return to full-scene scans every frame.
        private readonly System.Collections.Generic.List<Component> _grabBagItemsCacheV107 = new System.Collections.Generic.List<Component>();
        private readonly System.Collections.Generic.List<Component> _cookingCogsCacheV107 = new System.Collections.Generic.List<Component>();
        private readonly System.Collections.Generic.List<Component> _grabBagItemsGlobalFallbackV107 = new System.Collections.Generic.List<Component>();
        private readonly System.Collections.Generic.List<Component> _cookingCogsGlobalFallbackV107 = new System.Collections.Generic.List<Component>();
        private bool _grabBagCacheWasActiveV107 = false;
        private Transform _grabBagCacheRootV107;
        private float _nextGrabBagHierarchyRefreshV107 = 0f;
        private int _cogGlobalFallbacksThisSessionV107 = 0;
        private int _lastActiveItemsForCogProbeV107 = -1;
        private float _lastGrabBagCacheBuildMsV107 = 0f;

        // ZER0INT V108 FUNCTIONALITY FIX — V107 correctly removed the two per-frame global
        // searches, but its cache only knew the currently-active ingredient page. Native page
        // switches deactivate that page and activate a different set of GrabBagLayoutItems that
        // can live outside GrabBagHeld's local subtree. Result: fast menu, but later ingredient
        // sheets became unselectable. Detect the >0 -> 0 cached-active transition and perform
        // ONE active global item refresh for that page transition. Discovered page items are
        // unioned into the persistent session cache, so revisiting an already-seen page is free.
        // This intentionally trades at most one old ~100ms-class hitch on a NEW page for correct
        // interaction, never the old ~116ms EVERY FRAME catastrophe.
        private int _lastActiveItemsForPageProbeV108 = -1;
        private int _itemPageGlobalProbesThisSessionV108 = 0;
        private const int MaxItemPageGlobalProbesPerSessionV108 = 16;

        // V142: A page is NOT guaranteed to make all cached ingredients inactive. Shelving,
        // crafting and sheet switches can replace individual items while other cached items
        // stay active, so V108's only (>0 -> 0) trigger misses newly activated objects.
        // Track active native object identities, debounce changes, and fall back to a very
        // sparse discovery if gaze remains empty. Never reintroduce per-frame scene scans.
        private int _activeItemSignatureV142 = 0;
        private bool _activeItemSignatureValidV142 = false;
        private float _itemChangeRefreshDueV142 = -1f;
        private float _nextItemGlobalRefreshV142 = 0f;
        private float _emptyGazeSinceV142 = -1f;
        private float _nextMissDiscoveryV142 = 0f;
        private int _missRefreshesWithoutAddV142 = 0;
        private int _globalItemRefreshesV142 = 0;
        private int _lastGlobalItemRefreshAddedV142 = 0;
        private const float MinGlobalRefreshIntervalV142 = 1.75f;
        private const int MaxGlobalRefreshesPerSessionV142 = 24;


        private static bool ComponentMatchesTypeV107(Component c, Type t)
        {
            if (c == null || t == null) return false;
            try
            {
                Type ct = c.GetType();
                return ct == t || (ct != null && ct.FullName == t.FullName);
            }
            catch { return false; }
        }

        private void RefreshGrabBagInteractionCacheV107(bool force, bool allowGlobalFallback)
        {
            if (_grabBagHeld == null || _grabBagLayoutItemDirectType == null) return;
            float now = Time.unscaledTime;
            if (!force && now < _nextGrabBagHierarchyRefreshV107) return;
            _nextGrabBagHierarchyRefreshV107 = now + 0.50f;

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            _grabBagItemsCacheV107.Clear();
            _cookingCogsCacheV107.Clear();

            try
            {
                // Local hierarchy census is orders of magnitude cheaper than scanning the entire
                // loaded scene. includeInactive=true is important: menu items change active state.
                Component[] local = _grabBagHeld.GetComponentsInChildren<Component>(true);
                if (local != null)
                {
                    foreach (Component c in local)
                    {
                        if (ComponentMatchesTypeV107(c, _grabBagLayoutItemDirectType))
                            _grabBagItemsCacheV107.Add(c);
                        else if (ComponentMatchesTypeV107(c, _cookingCogTypeV24))
                            _cookingCogsCacheV107.Add(c);
                    }
                }
            }
            catch { }

            // Preserve the tiny set of one-shot global fallback objects across cheap hierarchy
            // refreshes; local rebuilds must not accidentally forget an out-of-tree cog/item.
            foreach (Component c in _grabBagItemsGlobalFallbackV107)
                if (c != null && !_grabBagItemsCacheV107.Contains(c)) _grabBagItemsCacheV107.Add(c);
            foreach (Component c in _cookingCogsGlobalFallbackV107)
                if (c != null && !_cookingCogsCacheV107.Contains(c)) _cookingCogsCacheV107.Add(c);

            // Compatibility fallback: if the actual game prefab keeps one of these interaction
            // components outside GrabBagHeld, do ONE active scene search when the menu opens.
            // V106 proved this search is expensive (~116 ms), hence the strict session budget.
            if (allowGlobalFallback)
            {
                if (_grabBagItemsCacheV107.Count == 0 && _grabBagLayoutItemDirectType != null)
                {
                    try
                    {
                        var it = Il2CppInterop.Runtime.Il2CppType.From(_grabBagLayoutItemDirectType);
                        foreach (var obj in UnityEngine.Object.FindObjectsOfType(it))
                        {
                            var c = obj.TryCast<Component>();
                            if (c != null)
                            {
                                if (!_grabBagItemsGlobalFallbackV107.Contains(c)) _grabBagItemsGlobalFallbackV107.Add(c);
                                if (!_grabBagItemsCacheV107.Contains(c)) _grabBagItemsCacheV107.Add(c);
                            }
                        }
                    }
                    catch { }
                }

                if (_cookingCogsCacheV107.Count == 0 && _cookingCogTypeV24 != null && _cogGlobalFallbacksThisSessionV107 < 1)
                {
                    _cogGlobalFallbacksThisSessionV107++;
                    TryGlobalCookingCogCacheRefreshV107();
                }
            }

            _lastGrabBagCacheBuildMsV107 = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            if (force)
            {
                MelonLogger.Warning($"[VRPlayer][V107 CookingCache] built items={_grabBagItemsCacheV107.Count} cogs={_cookingCogsCacheV107.Count} in {_lastGrabBagCacheBuildMsV107:F3}ms; full-scene per-frame scans are DISABLED.");
            }
        }

        private int TryGlobalGrabBagItemCacheRefreshV108()
        {
            if (_grabBagLayoutItemDirectType == null) return 0;
            int newlyAdded = 0;
            try
            {
                var itemType = Il2CppInterop.Runtime.Il2CppType.From(_grabBagLayoutItemDirectType);
                foreach (var obj in UnityEngine.Object.FindObjectsOfType(itemType))
                {
                    var c = obj.TryCast<Component>();
                    if (c == null) continue;
                    if (!_grabBagItemsGlobalFallbackV107.Contains(c))
                    {
                        _grabBagItemsGlobalFallbackV107.Add(c);
                        newlyAdded++;
                    }
                    if (!_grabBagItemsCacheV107.Contains(c))
                        _grabBagItemsCacheV107.Add(c);
                }
            }
            catch { }
            return newlyAdded;
        }

        // V142: call after the live candidate scan. The active-set signature lets us detect
        // partial stack removals / swaps of equally sized pages, not just V108's empty page.
        private void MaybeRefreshGrabBagItemsAfterPageChangeV108(int activeItems, int activeSignature)
        {
            float now = Time.unscaledTime;
            int previous = _lastActiveItemsForPageProbeV108;
            _lastActiveItemsForPageProbeV108 = activeItems;
            if (!_activeItemSignatureValidV142)
            {
                _activeItemSignatureV142 = activeSignature;
                _activeItemSignatureValidV142 = true;
                return;
            }
            if (activeSignature != _activeItemSignatureV142 || activeItems != previous)
            {
                _activeItemSignatureV142 = activeSignature;
                _itemChangeRefreshDueV142 = now + 0.35f; // coalesce a burst of shelf operations
                MelonLogger.Msg($"[V142 CookingCache] ACTIVE_SET_CHANGED count={previous}->{activeItems} cache={_grabBagItemsCacheV107.Count} deferred=0.35s");
            }
            // V108's essential empty-page transition can be resolved immediately.
            bool emptyPage = previous > 0 && activeItems == 0;
            bool settledChange = _itemChangeRefreshDueV142 >= 0f && now >= _itemChangeRefreshDueV142;
            if (!emptyPage && !settledChange) return;
            if (TryRefreshGlobalGrabBagItemsV142(emptyPage ? "empty-page" : "membership-change"))
                _itemChangeRefreshDueV142 = -1f;
        }

        // Single gated global scan. Expensive on this game: V106 measured ~116ms per scan.
        // A one-frame hitch on structural menu changes is preferable to a permanent 4-FPS loop.
        private bool TryRefreshGlobalGrabBagItemsV142(string reason)
        {
            float now = Time.unscaledTime;
            if (now < _nextItemGlobalRefreshV142) return false;
            if (_globalItemRefreshesV142 >= MaxGlobalRefreshesPerSessionV142) return false;
            _nextItemGlobalRefreshV142 = now + MinGlobalRefreshIntervalV142;
            ++_globalItemRefreshesV142;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            int before = _grabBagItemsCacheV107.Count;
            int added = TryGlobalGrabBagItemCacheRefreshV108();
            _lastGlobalItemRefreshAddedV142 = added;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp()-start)*1000.0/System.Diagnostics.Stopwatch.Frequency;
            if (added > 0)
            {
                _missRefreshesWithoutAddV142 = 0;
                _emptyGazeSinceV142 = -1f;
            }
            MelonLogger.Warning($"[V142 CookingCache] REFRESH reason={reason} added={added} cache={before}->{_grabBagItemsCacheV107.Count} ms={ms:F2} session={_globalItemRefreshesV142}/{MaxGlobalRefreshesPerSessionV142}");
            return true;
        }

        // A missed ray can indicate a newly visible item whose GrabBagLayoutItem has never
        // entered the session cache. Discover via ONE sparse physics query first, then use a
        // budgeted global fallback if necessary. No changes to the production hit-selection ray.
        private void MaybeDiscoverGrabBagItemsOnGazeMissV142(Ray gazeRay, float maxDistance,
            bool hasTarget, int activeItems, int testedColliders)
        {
            float now = Time.unscaledTime;
            if (hasTarget)
            {
                _emptyGazeSinceV142 = -1f;
                return;
            }
            if (_emptyGazeSinceV142 < 0f) _emptyGazeSinceV142 = now;
            if (now - _emptyGazeSinceV142 < 0.45f || now < _nextMissDiscoveryV142) return;
            _nextMissDiscoveryV142 = now + 2.0f;
            int addedByRay = 0, rawHits = 0;
            string nearest = "none";
            try
            {
                RaycastHit[] hits = Physics.RaycastAll(gazeRay, maxDistance);
                if (hits != null)
                {
                    rawHits = hits.Length;
                    float nearestDist = maxDistance;
                    foreach (RaycastHit hit in hits)
                    {
                        if (hit.collider == null) continue;
                        if (hit.distance < nearestDist)
                        {
                            nearestDist = hit.distance;
                            nearest = hit.collider.name;
                        }
                        // Rare rescue for active out-of-tree items. Collider child -> owning
                        // GrabBagLayoutItem, not just a direct component on collider itself.
                        Component[] parents = hit.collider.GetComponentsInParent<Component>(true);
                        foreach (Component c in parents)
                        {
                            if (c == null || !ComponentMatchesTypeV107(c, _grabBagLayoutItemDirectType)) continue;
                            if (!_grabBagItemsCacheV107.Contains(c))
                            {
                                _grabBagItemsCacheV107.Add(c);
                                if (!_grabBagItemsGlobalFallbackV107.Contains(c)) _grabBagItemsGlobalFallbackV107.Add(c);
                                ++addedByRay;
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception e) { MelonLogger.Warning("[V142 CookingCache] gaze-ray census failed: " + e.Message); }
            if (addedByRay > 0)
            {
                _missRefreshesWithoutAddV142 = 0;
                _emptyGazeSinceV142 = -1f;
            }
            else if (now >= _nextItemGlobalRefreshV142 && _missRefreshesWithoutAddV142 < 4)
            {
                // Backoff after empty searches: don't repeatedly scan the forest because
                // the user is legitimately looking at a blank bit of the cooking tray.
                bool refreshed = TryRefreshGlobalGrabBagItemsV142("sustained-gaze-miss");
                if (refreshed && _lastGlobalItemRefreshAddedV142 == 0) ++_missRefreshesWithoutAddV142;
            }
            MelonLogger.Msg($"[V142 CookingRay] target=none cachedActive={activeItems} testedColliders={testedColliders} physicsHits={rawHits} nearest={nearest} discoveredByRay={addedByRay} missProbes={_missRefreshesWithoutAddV142}");
        }

        private void TryGlobalCookingCogCacheRefreshV107()
        {
            if (_cookingCogTypeV24 == null) return;
            try
            {
                var cogType = Il2CppInterop.Runtime.Il2CppType.From(_cookingCogTypeV24);
                foreach (var obj in UnityEngine.Object.FindObjectsOfType(cogType))
                {
                    var c = obj.TryCast<Component>();
                    if (c != null)
                    {
                        if (!_cookingCogsGlobalFallbackV107.Contains(c)) _cookingCogsGlobalFallbackV107.Add(c);
                        if (!_cookingCogsCacheV107.Contains(c)) _cookingCogsCacheV107.Add(c);
                    }
                }
            }
            catch { }
        }

        private void UpdateGrabBagInteractionCacheLifecycleV107(bool grabBagActive)
        {
            if (!grabBagActive)
            {
                if (_grabBagCacheWasActiveV107)
                {
                    MelonLogger.Msg($"[V142 CookingCache] MENU_EXIT cached={_grabBagItemsCacheV107.Count} rescans={_globalItemRefreshesV142} missRetries={_missRefreshesWithoutAddV142}");
                    _grabBagItemsCacheV107.Clear();
                    _cookingCogsCacheV107.Clear();
                    _grabBagItemsGlobalFallbackV107.Clear();
                    _cookingCogsGlobalFallbackV107.Clear();
                    _grabBagCacheRootV107 = null;
                    _cogGlobalFallbacksThisSessionV107 = 0;
                    _lastActiveItemsForCogProbeV107 = -1;
                    _lastActiveItemsForPageProbeV108 = -1;
                    _itemPageGlobalProbesThisSessionV108 = 0;
                    ResetCookingCacheDiscoveryV142();
                }
                _grabBagCacheWasActiveV107 = false;
                return;
            }

            EnsureGrabBagGazeReflectionV10();
            EnsureCookingCogReflectionV24();
            bool force = !_grabBagCacheWasActiveV107 || _grabBagCacheRootV107 != _grabBagHeld;
            if (force)
            {
                _grabBagCacheRootV107 = _grabBagHeld;
                _grabBagItemsGlobalFallbackV107.Clear();
                _cookingCogsGlobalFallbackV107.Clear();
                _cogGlobalFallbacksThisSessionV107 = 0;
                _lastActiveItemsForCogProbeV107 = -1;
                _lastActiveItemsForPageProbeV108 = -1;
                _itemPageGlobalProbesThisSessionV108 = 0;
                ResetCookingCacheDiscoveryV142();
                MelonLogger.Msg("[V142 CookingCache] MENU_ENTER rebuilding local candidates; external pages discovered on demand");
            }
            RefreshGrabBagInteractionCacheV107(force, force);
            _grabBagCacheWasActiveV107 = true;
        }

        private void ResetCookingCacheDiscoveryV142()
        {
            _activeItemSignatureV142 = 0;
            _activeItemSignatureValidV142 = false;
            _itemChangeRefreshDueV142 = -1f;
            _nextItemGlobalRefreshV142 = 0f;
            _emptyGazeSinceV142 = -1f;
            _nextMissDiscoveryV142 = 0f;
            _missRefreshesWithoutAddV142 = 0;
            _globalItemRefreshesV142 = 0;
            _lastGlobalItemRefreshAddedV142 = 0;
        }

        private void MaybeProbeCookingCogAfterItemStateChangeV107(int activeItems)
        {
            if (activeItems == _lastActiveItemsForCogProbeV107) return;
            int previous = _lastActiveItemsForCogProbeV107;
            _lastActiveItemsForCogProbeV107 = activeItems;
            if (previous < 0) return; // initial count, no reason to rescan globally yet
            if (_cookingCogsCacheV107.Count != 0 || _cogGlobalFallbacksThisSessionV107 >= 2) return;

            // A substantial menu-state change can instantiate/activate the start-cooking cog.
            // Permit ONE additional global active-cog search for the entire session, rather than
            // paying ~116 ms on every frame as V106 did.
            _cogGlobalFallbacksThisSessionV107++;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            TryGlobalCookingCogCacheRefreshV107();
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            MelonLogger.Msg($"[VRPlayer][V107 CookingCache] item-state change {previous}->{activeItems}; one-shot cog refresh found={_cookingCogsCacheV107.Count} cost={ms:F3}ms.");
        }

        private static MethodInfo FindNoArgInstanceMethodInHierarchy(Type type, string methodName)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                try
                {
                    var m = t.GetMethod(methodName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                        null, Type.EmptyTypes, null);
                    if (m != null) return m;
                }
                catch { }
            }
            return null;
        }

        private void EnsureGrabBagGazeReflectionV10()
        {
            if (!_grabBagLayoutItemDirectTypeSearched)
            {
                _grabBagLayoutItemDirectTypeSearched = true;
                const string fullName = "Il2CppSons.Gameplay.GrabBag.GrabBagLayoutItem";
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t.FullName == fullName) { _grabBagLayoutItemDirectType = t; break; }
                    }
                    if (_grabBagLayoutItemDirectType != null) break;
                }

                if (_grabBagLayoutItemDirectType != null)
                {
                    for (Type searchType = _grabBagLayoutItemDirectType; searchType != null && _grabBagIsHighlightedProp == null; searchType = searchType.BaseType)
                    {
                        _grabBagIsHighlightedProp = searchType.GetProperty("IsHighlighted",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    }
                }
            }

            if (_grabBagLayoutItemDirectType != null && !_grabBagCallbacksSearchedV10)
            {
                _grabBagCallbacksSearchedV10 = true;
                _grabBagOnMouseEnterV10 = FindNoArgInstanceMethodInHierarchy(_grabBagLayoutItemDirectType, "OnMouseEnter");
                _grabBagOnMouseOverV10 = FindNoArgInstanceMethodInHierarchy(_grabBagLayoutItemDirectType, "OnMouseOver");
                _grabBagOnMouseExitV10 = FindNoArgInstanceMethodInHierarchy(_grabBagLayoutItemDirectType, "OnMouseExit");
                MelonLogger.Msg($"[VRPlayer][GrabBagGazeV10] type={_grabBagLayoutItemDirectType.FullName} callbacks enter={(_grabBagOnMouseEnterV10 != null)} over={(_grabBagOnMouseOverV10 != null)} exit={(_grabBagOnMouseExitV10 != null)} IsHighlighted={(_grabBagIsHighlightedProp != null)} writable={(_grabBagIsHighlightedProp?.CanWrite)}");
            }
        }

        private void InvokeGrabBagMouseCallbackV10(Component item, MethodInfo method, string label)
        {
            if (item == null || method == null || _grabBagLayoutItemDirectType == null) return;
            try
            {
                object castItem = CastToType(item, _grabBagLayoutItemDirectType);
                if (castItem != null) method.Invoke(castItem, null);
            }
            catch (Exception e)
            {
                MelonLogger.Msg($"[VRPlayer][GrabBagGazeV10] {label} failed on {item.name}: {e.GetBaseException().Message}");
            }
        }

        private void EnsureCookingCogReflectionV24()
        {
            if (!_cookingCogTypeSearchedV24)
            {
                _cookingCogTypeSearchedV24 = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t.FullName == "Il2CppSons.Gameplay.Cooking.CookingCog" ||
                            (t.Name == "CookingCog" && t.FullName != null && t.FullName.Contains("Gameplay.Cooking")))
                        {
                            _cookingCogTypeV24 = t;
                            break;
                        }
                    }
                    if (_cookingCogTypeV24 != null) break;
                }

                if (_cookingCogTypeV24 != null)
                {
                    _cookingCogOnMouseEnterV24 = FindNoArgInstanceMethodInHierarchy(_cookingCogTypeV24, "OnMouseEnter");
                    _cookingCogOnMouseOverV24 = FindNoArgInstanceMethodInHierarchy(_cookingCogTypeV24, "OnMouseOver");
                    _cookingCogOnMouseExitV24 = FindNoArgInstanceMethodInHierarchy(_cookingCogTypeV24, "OnMouseExit");
                }
            }

            if (!_cookingCogReflectionLoggedV24)
            {
                _cookingCogReflectionLoggedV24 = true;
                MelonLogger.Msg($"[VRPlayer][CookingCogGazeV24] type={(_cookingCogTypeV24 != null ? _cookingCogTypeV24.FullName : "NOT FOUND")} callbacks enter={(_cookingCogOnMouseEnterV24 != null)} over={(_cookingCogOnMouseOverV24 != null)} exit={(_cookingCogOnMouseExitV24 != null)}");
            }
        }

        private void InvokeCookingCogMouseCallbackV24(Component cog, MethodInfo method, string label)
        {
            if (cog == null || method == null || _cookingCogTypeV24 == null) return;
            try
            {
                object castCog = CastToType(cog, _cookingCogTypeV24);
                if (castCog != null) method.Invoke(castCog, null);
            }
            catch (Exception e)
            {
                MelonLogger.Msg($"[VRPlayer][CookingCogGazeV24] {label} failed on {cog.name}: {e.GetBaseException().Message}");
            }
        }

        private void ClearCookingCogGazeV24()
        {
            if (_cookingCogGazeHoveredV24 == null) return;
            InvokeCookingCogMouseCallbackV24(_cookingCogGazeHoveredV24, _cookingCogOnMouseExitV24, "OnMouseExit");
            _cookingCogGazeHoveredV24 = null;
        }

        private bool TryFindCookingCogGazeTargetV24(
            Ray gazeRay, float maxDistance, out Component bestCog, out Vector3 bestPoint,
            out Vector3 bestNormal, out float bestDistance, out int activeCogs, out int testedColliders,
            out float findMsV106, out float scanMsV106, out int testedRenderersV106)
        {
            long scanStartV106 = System.Diagnostics.Stopwatch.GetTimestamp();
            bestCog = null;
            bestPoint = Vector3.zero;
            bestNormal = -gazeRay.direction;
            bestDistance = maxDistance;
            activeCogs = 0;
            testedColliders = 0;
            testedRenderersV106 = 0;
            findMsV106 = 0f;
            scanMsV106 = 0f;

            EnsureCookingCogReflectionV24();
            if (_cookingCogTypeV24 == null)
            {
                scanMsV106 = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - scanStartV106) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                return false;
            }

            // V107: candidates are cached; V106 proved the global FindObjectsOfType call itself
            // cost ~116 ms every frame. Cache refresh is handled by the menu lifecycle above.
            RefreshGrabBagInteractionCacheV107(false, false);
            findMsV106 = 0f;
            foreach (var cog in _cookingCogsCacheV107)
            {
                if (cog == null || cog.gameObject == null || !cog.gameObject.activeInHierarchy) continue;
                activeCogs++;

                bool cogHadColliderHit = false;
                var colliders = cog.GetComponentsInChildren<Collider>();
                if (colliders != null)
                {
                    foreach (var col in colliders)
                    {
                        if (col == null || !col.enabled || col.isTrigger) continue;
                        testedColliders++;
                        try
                        {
                            if (col.Raycast(gazeRay, out RaycastHit hit, maxDistance) && hit.distance < bestDistance)
                            {
                                bestDistance = hit.distance;
                                bestPoint = hit.point;
                                bestNormal = hit.normal;
                                bestCog = cog;
                                cogHadColliderHit = true;
                            }
                        }
                        catch { }
                    }
                }

                // Some interaction prefabs keep the clickable script on a transform whose visual
                // child has no useful physics collider.  In that case use the real renderer bounds
                // as a spatial fallback rather than returning to a fake fixed-depth cursor plane.
                if (!cogHadColliderHit)
                {
                    var renderers = cog.GetComponentsInChildren<Renderer>();
                    if (renderers == null) continue;
                    foreach (var renderer in renderers)
                    {
                        if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                        testedRenderersV106++;
                        try
                        {
                            if (renderer.bounds.IntersectRay(gazeRay, out float d) && d >= 0f && d <= maxDistance && d < bestDistance)
                            {
                                bestDistance = d;
                                bestPoint = gazeRay.GetPoint(d);
                                bestNormal = -gazeRay.direction;
                                bestCog = cog;
                            }
                        }
                        catch { }
                    }
                }
            }

            scanMsV106 = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - scanStartV106) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            return bestCog != null;
        }

        private void ClearGrabBagGazeHoverV10()
        {
            ClearCookingCogGazeV24();
            if (_grabBagGazeHoveredV10 == null) return;

            InvokeGrabBagMouseCallbackV10(_grabBagGazeHoveredV10, _grabBagOnMouseExitV10, "OnMouseExit");
            if (_grabBagIsHighlightedProp != null && _grabBagIsHighlightedProp.CanWrite && _grabBagLayoutItemDirectType != null)
            {
                try
                {
                    object castOld = CastToType(_grabBagGazeHoveredV10, _grabBagLayoutItemDirectType);
                    if (castOld != null) _grabBagIsHighlightedProp.SetValue(castOld, false);
                }
                catch { }
            }
            _grabBagGazeHoveredV10 = null;
        }

        private void UpdateGrabBagGazeHoverV10()
        {
            long totalStartV106 = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureGrabBagGazeReflectionV10();
            EnsureCookingCogReflectionV24();
            if (_grabBagLayoutItemDirectType == null || StereoRender == null || StereoRender.Head == null) return;

            FindSoftwareCursor();
            if (_softwareCursor != null && !_softwareCursor.active) _softwareCursor.active = true;

            const float maxDistance = 3.5f;
            Ray gazeRay = new Ray(StereoRender.Head.position, StereoRender.Head.forward);
            Component bestItem = null;
            RaycastHit bestHit = default;
            float bestDistance = maxDistance;
            int activeItems = 0;
            int activeSignatureV142 = 0;
            int testedColliders = 0;

            var activeItemList = new System.Collections.Generic.List<Component>();
            RefreshGrabBagInteractionCacheV107(false, false);
            long itemScanStartV106 = System.Diagnostics.Stopwatch.GetTimestamp();
            float itemFindMsV106 = 0f; // V107: no full-scene search in the per-frame path.
            foreach (var item in _grabBagItemsCacheV107)
            {
                if (item == null || item.gameObject == null || !item.gameObject.activeInHierarchy) continue;
                activeItems++;
                // Order-independent native object identity signature; catches partial stacks,
                // page swaps with equal counts, and dynamic item replacement.
                unchecked { activeSignatureV142 ^= (item.GetInstanceID() * 16777619); }
                activeItemList.Add(item);

                var colliders = item.GetComponentsInChildren<Collider>();
                if (colliders == null) continue;
                foreach (var col in colliders)
                {
                    if (col == null || !col.enabled || col.isTrigger) continue;
                    testedColliders++;
                    try
                    {
                        if (col.Raycast(gazeRay, out RaycastHit hit, maxDistance) && hit.distance < bestDistance)
                        {
                            bestDistance = hit.distance;
                            bestHit = hit;
                            bestItem = item;
                        }
                    }
                    catch { }
                }
            }
            float itemScanMsV106 = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - itemScanStartV106) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            MaybeRefreshGrabBagItemsAfterPageChangeV108(activeItems, activeSignatureV142);
            MaybeProbeCookingCogAfterItemStateChangeV107(activeItems);

            // V24: the start-cooking cog is NOT a GrabBagLayoutItem.  It is its own
            // Sons.Gameplay.Cooking.CookingCog component with native mouse-hover callbacks and
            // an _isMouseOver gate used by its interaction path.  Raycast it separately and let
            // the nearest real 3D target (ingredient or cog) win.
            Component bestCog;
            Vector3 cogPoint;
            Vector3 cogNormal;
            float cogDistance;
            int activeCogs;
            int cogColliders;
            float cogFindMsV106;
            float cogScanMsV106;
            int cogRenderersV106;
            bool hasCog = TryFindCookingCogGazeTargetV24(
                gazeRay, maxDistance, out bestCog, out cogPoint, out cogNormal,
                out cogDistance, out activeCogs, out cogColliders,
                out cogFindMsV106, out cogScanMsV106, out cogRenderersV106);

            bool cogWins = hasCog && (bestItem == null || cogDistance < bestDistance);
            Component targetItem = cogWins ? null : bestItem;
            Component targetCog = cogWins ? bestCog : null;

            Vector3 targetPoint = Vector3.zero;
            Vector3 targetNormal = -gazeRay.direction;
            float targetDistance = -1f;
            bool hasTarget = false;
            if (targetCog != null)
            {
                targetPoint = cogPoint;
                targetNormal = cogNormal;
                targetDistance = cogDistance;
                hasTarget = true;
            }
            else if (targetItem != null)
            {
                targetPoint = bestHit.point;
                targetNormal = bestHit.normal;
                targetDistance = bestDistance;
                hasTarget = true;
            }

            // V142: persistent empty gaze may mean uncached new inventory objects. Rare,
            // budgeted rescue/diagnostic; does not change this frame's normal raycast result.
            MaybeDiscoverGrabBagItemsOnGazeMissV142(gazeRay, maxDistance, hasTarget, activeItems, testedColliders);

            // The visible cursor follows the SAME target selection.  No-target fallback remains
            // a small free-space reticle, but the cog itself can now be reached at its true depth.
            if (_softwareCursor != null)
            {
                if (hasTarget)
                {
                    _softwareCursor.transform.position = targetPoint + targetNormal * CampfireCursorSurfaceLift;
                    _softwareCursor.transform.rotation = Quaternion.LookRotation(-gazeRay.direction, Vector3.up);
                }
                else
                {
                    _softwareCursor.transform.position = gazeRay.origin + gazeRay.direction * 0.55f;
                    _softwareCursor.transform.rotation = Quaternion.LookRotation(gazeRay.direction, Vector3.up);
                }
                _softwareCursor.transform.localScale = Vector3.one * CampfireCursorScale;
            }

            // Ingredient native hover lifecycle.
            if (targetItem != _grabBagGazeHoveredV10)
            {
                if (_grabBagGazeHoveredV10 != null)
                    InvokeGrabBagMouseCallbackV10(_grabBagGazeHoveredV10, _grabBagOnMouseExitV10, "OnMouseExit");

                _grabBagGazeHoveredV10 = targetItem;

                if (_grabBagGazeHoveredV10 != null)
                    InvokeGrabBagMouseCallbackV10(_grabBagGazeHoveredV10, _grabBagOnMouseEnterV10, "OnMouseEnter");
            }
            if (_grabBagGazeHoveredV10 != null)
                InvokeGrabBagMouseCallbackV10(_grabBagGazeHoveredV10, _grabBagOnMouseOverV10, "OnMouseOver");

            // CookingCog native hover lifecycle.  OnMouseOver is optional; the generated
            // assembly definitely exposes OnMouseEnter/OnMouseExit and the component carries
            // _isMouseOver, so entering/exiting is the important part for native trigger input.
            if (targetCog != _cookingCogGazeHoveredV24)
            {
                if (_cookingCogGazeHoveredV24 != null)
                    InvokeCookingCogMouseCallbackV24(_cookingCogGazeHoveredV24, _cookingCogOnMouseExitV24, "OnMouseExit");

                _cookingCogGazeHoveredV24 = targetCog;

                if (_cookingCogGazeHoveredV24 != null)
                    InvokeCookingCogMouseCallbackV24(_cookingCogGazeHoveredV24, _cookingCogOnMouseEnterV24, "OnMouseEnter");
            }
            if (_cookingCogGazeHoveredV24 != null && _cookingCogOnMouseOverV24 != null)
                InvokeCookingCogMouseCallbackV24(_cookingCogGazeHoveredV24, _cookingCogOnMouseOverV24, "OnMouseOver");

            // Keep ingredient highlight exclusive; looking at the cog clears ingredient highlights.
            if (_grabBagIsHighlightedProp != null && _grabBagIsHighlightedProp.CanWrite)
            {
                foreach (var item in activeItemList)
                {
                    try
                    {
                        object castItem = CastToType(item, _grabBagLayoutItemDirectType);
                        if (castItem != null) _grabBagIsHighlightedProp.SetValue(castItem, item == _grabBagGazeHoveredV10);
                    }
                    catch { }
                }
            }

            float totalMsV106 = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - totalStartV106) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            PerformanceForensicsV106.RecordCookingGaze(
                totalMsV106, itemFindMsV106, itemScanMsV106, activeItems, testedColliders,
                cogFindMsV106, cogScanMsV106, activeCogs, cogColliders, cogRenderersV106);

            if (Time.time - _grabBagGazeV10DiagTime > 0.75f)
            {
                _grabBagGazeV10DiagTime = Time.time;
                string targetName = targetCog != null ? $"COG:{targetCog.name}" : (targetItem != null ? targetItem.name : "none");
                MelonLogger.Msg($"[VRPlayer][CampfireGazeV24][V107] gaze={targetName} dist={(hasTarget ? targetDistance.ToString("F3") : "-")} activeItems={activeItems}/{_grabBagItemsCacheV107.Count} itemColliders={testedColliders} activeCogs={activeCogs}/{_cookingCogsCacheV107.Count} cogColliders={cogColliders} gazeCpu={totalMsV106:F3}ms cacheRefreshLast={_lastGrabBagCacheBuildMsV107:F3}ms");
            }
        }


        private void UpdateGrabBagGazeSelection()
        {
            if (!_grabBagLayoutItemDirectTypeSearched)
            {
                _grabBagLayoutItemDirectTypeSearched = true;
                const string fullName = "Il2CppSons.Gameplay.GrabBag.GrabBagLayoutItem";
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t.FullName == fullName) { _grabBagLayoutItemDirectType = t; break; }
                    }
                    if (_grabBagLayoutItemDirectType != null) break;
                }
                if (_grabBagLayoutItemDirectType != null)
                {
                    for (Type searchType = _grabBagLayoutItemDirectType; searchType != null && _grabBagIsHighlightedProp == null; searchType = searchType.BaseType)
                    {
                        _grabBagIsHighlightedProp = searchType.GetProperty("IsHighlighted", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    }
                }
                MelonLogger.Msg($"[VRPlayer] GrabBagLayoutItem (direct) type found={(_grabBagLayoutItemDirectType != null)}, IsHighlighted property found={(_grabBagIsHighlightedProp != null)}, writable={(_grabBagIsHighlightedProp?.CanWrite)}");
            }

            if (_grabBagLayoutItemDirectType == null || _grabBagIsHighlightedProp == null || !_grabBagIsHighlightedProp.CanWrite) return;
            if (StereoRender == null || StereoRender.Head == null) return;

            // Same forgiving-aim raycast pattern proven earlier (5 rays: center plus slight
            // up/down/left/right), skipping trigger colliders like the campfire's own heat
            // volume - kept for cursor positioning, which is unrelated to how selection itself
            // now works.
            // FIXED (per user's report: the boil-start "gear" that appears next to the fire once
            // the pot is placed sits much farther away than the bag itself, and gaze couldn't
            // reach it at all - this 1.5m cap was sized for hand-held items, nowhere near enough
            // for something positioned out by the fire). Widened generously.
            const float grabBagGazeMaxDistance = 3.5f;
            Vector3 origin = StereoRender.Head.position;
            Vector3 baseDir = StereoRender.Head.forward;
            Vector3? centerRayClosestHitPoint = null;
            {
                var hitsForCenterRay = Physics.RaycastAll(origin, baseDir, grabBagGazeMaxDistance);
                float closestDist = float.MaxValue;
                foreach (var h in hitsForCenterRay)
                {
                    if (h.collider != null && h.collider.isTrigger) continue;
                    if (h.distance < closestDist) { closestDist = h.distance; centerRayClosestHitPoint = h.point; }
                }
            }

            if (_softwareCursor != null)
            {
                Vector3 cursorPos = centerRayClosestHitPoint ?? (origin + baseDir * 0.45f);
                _softwareCursor.transform.position = cursorPos;
                _softwareCursor.transform.rotation = Quaternion.LookRotation(-baseDir, Vector3.up);
            }

            if (centerRayClosestHitPoint == null) return; // nothing to compare item distances against this poll

            // Find whichever active GrabBagLayoutItem is physically closest to the gaze hit
            // point, within a reasonable radius (items are small and close together on the tray -
            // this deliberately avoids ever picking "the closest item in the whole bag" when the
            // gaze isn't actually near any of them).
            // FIXED (confirmed via user report + screenshot: cursor visually landed on the pot,
            // but the actually-selected item was the leaves instead - transform.position isn't
            // consistently offset from each item's visual location the same way across items,
            // so "closest by transform position" doesn't reliably match "closest by what's
            // actually visible". Using each item's own renderer bounds center instead - the
            // actual visual centroid of its mesh, regardless of where that item's own pivot
            // happens to sit.
            const float maxSelectDistance = 0.4f;
            Component closestItem = null;
            float closestItemDist = maxSelectDistance;
            // DIAGNOSTIC: tracks the true closest active instance regardless of the threshold
            // above, so we can tell "no instances found at all" apart from "instances exist but
            // are all farther than the threshold allows" - two very different problems.
            Component trueClosestAny = null;
            float trueClosestAnyDist = float.MaxValue;
            int activeInstanceCount = 0;
            var il2cppType = Il2CppInterop.Runtime.Il2CppType.From(_grabBagLayoutItemDirectType);
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(il2cppType))
            {
                var comp = obj.TryCast<Component>();
                if (comp == null || comp.gameObject == null || !comp.gameObject.activeInHierarchy) continue;
                activeInstanceCount++;

                // FIXED (confirmed via log: renderer-bounds distances barely improved over raw
                // transform position - still 0.2-0.4m, nowhere near the few centimeters expected
                // if that bounds center actually tracked the visual mesh). Using each item's own
                // collider surface instead, via Collider.ClosestPoint - the actual physical
                // surface, not a bounding box or pivot point. GrabBagLayoutItem's own working
                // OnMouseEnter/OnMouseExit methods require a real collider to exist somewhere for
                // Unity's own systems to invoke them in the first place, so this should exist.
                float dist = float.MaxValue;
                var colliders = comp.GetComponentsInChildren<Collider>();
                int usableColliderCount = 0;
                if (colliders != null && colliders.Length > 0)
                {
                    foreach (var col in colliders)
                    {
                        if (col == null || !col.enabled) continue;
                        try
                        {
                            Vector3 closest = col.ClosestPoint(centerRayClosestHitPoint.Value);
                            float d = Vector3.Distance(closest, centerRayClosestHitPoint.Value);
                            if (d < dist) dist = d;
                            usableColliderCount++;
                        }
                        catch { /* non-convex mesh collider or similar - skip this one */ }
                    }
                }
                bool usedFallback = dist == float.MaxValue;
                if (usedFallback)
                {
                    // Fallback for items with no usable collider - renderer bounds, then raw
                    // transform position as a last resort.
                    Vector3 refPoint = comp.transform.position;
                    var renderers = comp.GetComponentsInChildren<Renderer>();
                    if (renderers != null && renderers.Length > 0)
                    {
                        Bounds combined = renderers[0].bounds;
                        for (int i = 1; i < renderers.Length; i++) combined.Encapsulate(renderers[i].bounds);
                        refPoint = combined.center;
                    }
                    dist = Vector3.Distance(refPoint, centerRayClosestHitPoint.Value);
                }

                if (dist < closestItemDist) { closestItemDist = dist; closestItem = comp; }
                if (dist < trueClosestAnyDist)
                {
                    trueClosestAnyDist = dist; trueClosestAny = comp;
                    _trueClosestAnyUsedFallback = usedFallback;
                    _trueClosestAnyColliderCount = colliders?.Length ?? 0;
                    _trueClosestAnyUsableColliderCount = usableColliderCount;
                }
            }

            // DIAGNOSTIC (throttled) - confirms whether a nearby item is even being found at all,
            // and at what distance, while this direct approach is first tested.
            if (Time.time - _lastGrabBagGazeDiagTime > 0.5f)
            {
                _lastGrabBagGazeDiagTime = Time.time;
                MelonLogger.Msg($"[VRPlayer][GrabBagGazeDiag] closestItem={(closestItem != null ? closestItem.name : "none")} dist={(closestItem != null ? closestItemDist.ToString("F3") : "-")} activeInstanceCount={activeInstanceCount} trueClosestAny={(trueClosestAny != null ? $"{trueClosestAny.name}@{trueClosestAnyDist:F3}m usedFallback={_trueClosestAnyUsedFallback} colliders={_trueClosestAnyUsableColliderCount}/{_trueClosestAnyColliderCount}" : "none")}");
            }

            if (closestItem == _lastHighlightedGrabBagLayoutItem) return; // no change since last poll

            if (_lastHighlightedGrabBagLayoutItem != null)
            {
                try
                {
                    var castOld = CastToType(_lastHighlightedGrabBagLayoutItem, _grabBagLayoutItemDirectType);
                    if (castOld != null) _grabBagIsHighlightedProp.SetValue(castOld, false);
                }
                catch (Exception e) { MelonLogger.Msg($"[VRPlayer] GrabBagLayoutItem IsHighlighted clear failed: {e.Message}"); }
            }
            if (closestItem != null)
            {
                try
                {
                    var castNew = CastToType(closestItem, _grabBagLayoutItemDirectType);
                    if (castNew != null) _grabBagIsHighlightedProp.SetValue(castNew, true);
                }
                catch (Exception e) { MelonLogger.Msg($"[VRPlayer] GrabBagLayoutItem IsHighlighted set failed: {e.Message}"); }
            }
            _lastHighlightedGrabBagLayoutItem = closestItem;
        }


        // ---------------------------------------------------------------------
        // zer0int v4 PASSIVE cursor calibration probe
        // ---------------------------------------------------------------------
        // Important: this code NEVER writes to the virtual mouse, SoftwareCursor, cursor
        // controller, offsets, or selection state. The original fork's cursor path remains
        // untouched. We only observe right-trigger rising edges and snapshot the raw virtual
        // mouse coordinates so two deliberate presses can measure visual-vs-selection offset.
        private enum PassiveCursorProbeTarget
        {
            None,
            CampfireGrabBag,
            ConstructionBooklet,
            BackpackQuickSelect,
            TutorialBook
        }

        private bool _cursorProbePrevRightTrigger = false;
        private PassiveCursorProbeTarget _cursorProbeLastTarget = PassiveCursorProbeTarget.None;
        private int _cursorProbeSampleIndex = 0;
        private string _cursorProbeStatus = "Right trigger sample logger armed. Press exactly twice in the target menu.";
        private string CursorProbeLogPath => Path.Combine(Environment.CurrentDirectory, "UserData", "SonsVR_CursorProbe.log");

        private PassiveCursorProbeTarget GetPassiveCursorProbeTarget()
        {
            FindGrabBagHeld();
            FindBookHeld();
            FindBackpackQuickSelect();
            FindTutorialBookHeld();

            if (_grabBagHeld != null && _grabBagHeld.gameObject.activeInHierarchy) return PassiveCursorProbeTarget.CampfireGrabBag;
            if (_bookHeld != null && _bookHeld.gameObject.activeInHierarchy) return PassiveCursorProbeTarget.ConstructionBooklet;
            if (_backpackQuickSelect != null && _backpackQuickSelect.gameObject.activeInHierarchy) return PassiveCursorProbeTarget.BackpackQuickSelect;
            if (_tutorialBookHeld != null && _tutorialBookHeld.gameObject.activeInHierarchy) return PassiveCursorProbeTarget.TutorialBook;
            return PassiveCursorProbeTarget.None;
        }

        private Vector2 GetLegacyCursorOffsetForProbe(PassiveCursorProbeTarget target)
        {
            switch (target)
            {
                case PassiveCursorProbeTarget.CampfireGrabBag:
                case PassiveCursorProbeTarget.ConstructionBooklet:
                    return new Vector2(CampfireCursorOffsetX, CampfireCursorOffsetY);
                case PassiveCursorProbeTarget.BackpackQuickSelect:
                    return new Vector2(BackpackQuickSelectCursorOffsetX, BackpackQuickSelectCursorOffsetY);
                case PassiveCursorProbeTarget.TutorialBook:
                    return new Vector2(RightHandCursorOffsetX, RightHandCursorOffsetY);
                default:
                    return Vector2.zero;
            }
        }

        private void HandlePassiveCursorTriggerProbe()
        {
            bool triggerNow = NativeVRInput.GetBoolean("TriggerRight_Press");
            bool triggerDown = triggerNow && !_cursorProbePrevRightTrigger;
            _cursorProbePrevRightTrigger = triggerNow;
            if (!triggerDown) return;

            PassiveCursorProbeTarget target = GetPassiveCursorProbeTarget();
            if (target != _cursorProbeLastTarget)
            {
                _cursorProbeLastTarget = target;
                _cursorProbeSampleIndex = 0;
            }
            _cursorProbeSampleIndex++;

            Mouse mouse = FindVirtualMouseDevice();
            Vector2 raw = mouse != null ? mouse.position.ReadValue() : new Vector2(float.NaN, float.NaN);
            Vector2 legacyOffset = GetLegacyCursorOffsetForProbe(target);
            Vector2 visualCastScreen = raw + legacyOffset;

            string softwareCursorInfo = "none";
            if (_softwareCursor != null)
            {
                softwareCursorInfo = $"active={_softwareCursor.activeInHierarchy} world={_softwareCursor.transform.position}";
            }

            string line =
                $"[VRPlayer][CursorProbe] sample={_cursorProbeSampleIndex} target={target} " +
                $"rawMouse={raw} legacyOffset={legacyOffset} visualCastScreen={visualCastScreen} " +
                $"screen={Screen.width}x{Screen.height} softwareCursor=[{softwareCursorInfo}]";

            _cursorProbeStatus = line;
            MelonLogger.Msg(line);

            try
            {
                string dir = Path.GetDirectoryName(CursorProbeLogPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(
                    CursorProbeLogPath,
                    $"{DateTime.Now:O} {line}{Environment.NewLine}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[VRPlayer][CursorProbe] Could not append {CursorProbeLogPath}: {e.Message}");
            }
        }

        public void DrawPassiveCursorProbeOverlay()
        {
            GUI.Box(new Rect(15, 15, 900, 118), "SonsVR zer0int TEST V5 - PASSIVE cursor probe (original cursor code restored)");
            GUI.Label(new Rect(30, 44, 870, 22), "Right trigger #1 = visible cursor ON target. Right trigger #2 = SAME target actually highlighted.");
            GUI.Label(new Rect(30, 67, 870, 22), "Probe only logs; it never writes mouse/cursor/selection. Log: UserData/SonsVR_CursorProbe.log");
            GUI.Label(new Rect(30, 90, 870, 22), _cursorProbeStatus);
        }

        // V150: backpack-only real 3D hover; V116 HUD, cooking and V147 tarp untouched.
        // IL2CPP contract (confirmed against the supplied generated Il2CppSons.dll):
        // QuickSelectLayoutItem : LayoutItem; overrides OnMouseEnter, inherits
        // OnMouseOver/OnMouseExit; LayoutItem exposes IsHighlighted. The owning
        // InventoryBagController exposes _isOverItem / OnPrimaryAction. DO NOT call
        // OnPrimaryAction ourselves: it takes an InputAction.CallbackContext and must
        // remain tied to the game's native input dispatch.
        private sealed class BackpackTargetV150
        {
            internal QuickSelectLayoutItem Item;
            internal readonly System.Collections.Generic.List<Collider> Colliders =
                new System.Collections.Generic.List<Collider>();
            internal readonly System.Collections.Generic.List<Renderer> Renderers =
                new System.Collections.Generic.List<Renderer>();
        }

        private readonly System.Collections.Generic.List<BackpackTargetV150> _backpackTargetsV150 =
            new System.Collections.Generic.List<BackpackTargetV150>();
        private QuickSelectLayoutItem _backpackHoveredV150;
        private InventoryBagController _backpackBagControllerV150;
        private MethodInfo _backpackMouseEnterV150;
        private MethodInfo _backpackMouseOverV150;
        private MethodInfo _backpackMouseExitV150;
        private bool _backpackOpenV150;
        private int _backpackCacheBuildsV150;
        private float _backpackNextCacheBuildV150;
        private float _backpackNextLogV150;

        private void InvokeBackpackMouseV150(QuickSelectLayoutItem item, MethodInfo callback, string label)
        {
            if (item == null || callback == null) return;
            try { callback.Invoke(item, null); }
            catch (Exception e)
            {
                MelonLogger.Warning($"[V150 BackpackGaze] {label} {item.name}: {e.GetBaseException().Message}");
            }
        }

        private void ClearBackpackGazeV150()
        {
            if (!_backpackOpenV150 && _backpackHoveredV150 == null) return;
            if (_backpackHoveredV150 != null)
                InvokeBackpackMouseV150(_backpackHoveredV150, _backpackMouseExitV150, "OnMouseExit");
            // Only clean up highlight state controlled by V150. No writes to native cursor,
            // _currentlySnappedLayoutItem, _currentSnapPoint, or input devices.
            foreach (BackpackTargetV150 target in _backpackTargetsV150)
            {
                if (target.Item == null) continue;
                try { if (target.Item.IsHighlighted) target.Item.IsHighlighted = false; }
                catch { }
            }
            _backpackHoveredV150 = null;
            _backpackTargetsV150.Clear();
            _backpackBagControllerV150 = null;
            _backpackOpenV150 = false;
            _backpackCacheBuildsV150 = 0;
            MelonLogger.Msg("[V150 BackpackGaze] EXIT (native cursor untouched)");
        }

        private void BuildBackpackTargetsV150()
        {
            // V149 native census located all nine live items *inside* this hierarchy.
            // A typed IL2CPP query avoids the V148/V149 generic Component wrapper bug.
            // Never use FindObjectsOfType or a global scan for normal gameplay.
            ++_backpackCacheBuildsV150;
            _backpackNextCacheBuildV150 = Time.time + 0.5f;
            if (_backpackQuickSelect == null) return;
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _backpackTargetsV150.Clear();
                foreach (QuickSelectLayoutItem item in _backpackQuickSelect.GetComponentsInChildren<QuickSelectLayoutItem>(true))
                {
                    if (item == null) continue;
                    BackpackTargetV150 target = new BackpackTargetV150 { Item = item };
                    foreach (Collider col in item.GetComponentsInChildren<Collider>(true))
                        if (col != null) target.Colliders.Add(col);
                    foreach (Renderer renderer in item.GetComponentsInChildren<Renderer>(true))
                        if (renderer != null) target.Renderers.Add(renderer);
                    _backpackTargetsV150.Add(target);
                }
                _backpackBagControllerV150 = _backpackQuickSelect.GetComponentInParent<InventoryBagController>();
                MelonLogger.Msg($"[V150 BackpackGaze] CACHE count={_backpackTargetsV150.Count} " +
                    $"bagController={(_backpackBagControllerV150 != null)} build={_backpackCacheBuildsV150} " +
                    $"ms={(System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F2}");
                foreach (BackpackTargetV150 target in _backpackTargetsV150)
                    MelonLogger.Msg($"[V150 BackpackGaze] ITEM name={target.Item.name} id={target.Item.GetInstanceID()} " +
                        $"itemId={target.Item.ItemId} colliders={target.Colliders.Count} renderers={target.Renderers.Count}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[V150 BackpackGaze] typed local cache failed: " + e.GetBaseException().Message);
            }
        }

        private void UpdateBackpackGazeV150()
        {
            if (StereoRender == null || StereoRender.Head == null || _backpackQuickSelect == null) return;
            if (!_backpackOpenV150)
            {
                _backpackOpenV150 = true;
                _backpackMouseEnterV150 = FindNoArgInstanceMethodInHierarchy(typeof(QuickSelectLayoutItem), "OnMouseEnter");
                _backpackMouseOverV150 = FindNoArgInstanceMethodInHierarchy(typeof(QuickSelectLayoutItem), "OnMouseOver");
                _backpackMouseExitV150 = FindNoArgInstanceMethodInHierarchy(typeof(QuickSelectLayoutItem), "OnMouseExit");
                MelonLogger.Msg($"[V150 BackpackGaze] ENTER callbacks=" +
                    $"{_backpackMouseEnterV150 != null}/{_backpackMouseOverV150 != null}/{_backpackMouseExitV150 != null}");
                BuildBackpackTargetsV150();
            }
            // A few delayed retries cover lazy initial population; otherwise ZERO searches.
            if (_backpackTargetsV150.Count == 0 && _backpackCacheBuildsV150 < 4 && Time.time >= _backpackNextCacheBuildV150)
                BuildBackpackTargetsV150();

            FindSoftwareCursor();
            if (_softwareCursor != null && !_softwareCursor.active) _softwareCursor.active = true;
            const float maxDistanceV150 = 2.0f;
            Ray gazeRay = new Ray(StereoRender.Head.position, StereoRender.Head.forward);
            QuickSelectLayoutItem bestItem = null;
            Vector3 bestPosition = gazeRay.origin + gazeRay.direction * 0.55f;
            Vector3 bestNormal = -gazeRay.direction;
            float bestDistance = maxDistanceV150;
            int activeItems = 0;
            int raycastTests = 0;
            bool rendererFallback = false;

            // Stage 1: actual item collider geometry, explicitly ignoring backpack fabric,
            // the player's hand, and all unrelated game-world geometry.
            foreach (BackpackTargetV150 target in _backpackTargetsV150)
            {
                QuickSelectLayoutItem item = target.Item;
                if (item == null || item.gameObject == null || !item.gameObject.activeInHierarchy) continue;
                ++activeItems;
                foreach (Collider col in target.Colliders)
                {
                    if (col == null || !col.enabled || !col.gameObject.activeInHierarchy || col.isTrigger) continue;
                    ++raycastTests;
                    try
                    {
                        if (col.Raycast(gazeRay, out RaycastHit hit, maxDistanceV150) && hit.distance < bestDistance)
                        {
                            bestItem = item;
                            bestDistance = hit.distance;
                            bestPosition = hit.point;
                            bestNormal = hit.normal;
                        }
                    }
                    catch { }
                }
            }
            // Stage 2: real item renderer AABBs, only if no collider was hit.
            // Handles decorative small items whose collider isn't under LayoutItem.
            if (bestItem == null)
            {
                foreach (BackpackTargetV150 target in _backpackTargetsV150)
                {
                    QuickSelectLayoutItem item = target.Item;
                    if (item == null || item.gameObject == null || !item.gameObject.activeInHierarchy) continue;
                    foreach (Renderer renderer in target.Renderers)
                    {
                        if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                        try
                        {
                            if (renderer.bounds.IntersectRay(gazeRay, out float hitDistance) &&
                                hitDistance >= 0f && hitDistance < bestDistance)
                            {
                                bestItem = item;
                                bestDistance = hitDistance;
                                bestPosition = gazeRay.GetPoint(hitDistance);
                                bestNormal = -gazeRay.direction;
                                rendererFallback = true;
                            }
                        }
                        catch { }
                    }
                }
            }

            if (_softwareCursor != null)
            {
                _softwareCursor.transform.position = bestItem != null ?
                    bestPosition + bestNormal * CampfireCursorSurfaceLift :
                    gazeRay.GetPoint(0.55f);
                _softwareCursor.transform.rotation = Quaternion.LookRotation(-gazeRay.direction, Vector3.up);
                _softwareCursor.transform.localScale = Vector3.one * CampfireCursorScale;
            }

            if (bestItem != _backpackHoveredV150)
            {
                if (_backpackHoveredV150 != null)
                    InvokeBackpackMouseV150(_backpackHoveredV150, _backpackMouseExitV150, "OnMouseExit");
                _backpackHoveredV150 = bestItem;
                if (_backpackHoveredV150 != null)
                    InvokeBackpackMouseV150(_backpackHoveredV150, _backpackMouseEnterV150, "OnMouseEnter");
            }
            if (_backpackHoveredV150 != null)
                InvokeBackpackMouseV150(_backpackHoveredV150, _backpackMouseOverV150, "OnMouseOver");

            // Mirrors V147's cooking highlight enforcement. Not a substitute for native
            // selection: native OnPrimaryAction must still make the equip decision.
            foreach (BackpackTargetV150 target in _backpackTargetsV150)
            {
                QuickSelectLayoutItem item = target.Item;
                if (item == null || item.gameObject == null || !item.gameObject.activeInHierarchy) continue;
                try
                {
                    bool isTarget = item == _backpackHoveredV150;
                    if (item.IsHighlighted != isTarget) item.IsHighlighted = isTarget;
                }
                catch { }
            }
            if (Time.time >= _backpackNextLogV150)
            {
                _backpackNextLogV150 = Time.time + 0.75f;
                string over = "unavailable";
                try { if (_backpackBagControllerV150 != null) over = _backpackBagControllerV150._isOverItem.ToString(); } catch { }
                MelonLogger.Msg($"[V150 BackpackGaze] TICK target={(_backpackHoveredV150 != null ? _backpackHoveredV150.name : "none")} " +
                    $"distance={(bestItem != null ? bestDistance.ToString("F3") : "-")} " +
                    $"active={activeItems}/{_backpackTargetsV150.Count} rays={raycastTests} " +
                    $"rendererFallback={rendererFallback} nativeBagIsOver={over}");
            }
        }

        private Vector3 _backpackCursorOwnPos;
        private Quaternion _backpackCursorOwnRot;
        private bool _backpackCursorOwnPosInitialized = false;
        private bool _backpackAlignDiagSearched = false;
        private float _lastBackpackCursorVsSelectionDiagTime = 0f;

        /// <summary>
        /// Per user's explicit request: restore the backpack quick select's own cursor to exactly
        /// v0.6.1's original, simpler behavior (confirmed via direct source comparison - no
        /// deadzone, no surface lift, no fixed-distance logic; none of that existed in v0.6.1 at
        /// all), since the deadzone added later specifically for the grab bag's own "snaps back to
        /// center" issue was also silently affecting this menu once it shared the same function,
        /// and the user reported the stick responsiveness broke as a result (barely moves with the
        /// left stick, the one that actually drives selection here). A separate, dedicated
        /// function instead of a parameter on the shared one, so the grab bag and booklet - which
        /// already work well with the current, more complex version - are never at risk from this
        /// change. Two genuine, unrelated bug fixes are kept regardless: ViewportPointToRay
        /// (correct regardless of camera resolution) and the write-only, never-read-back cursor
        /// position tracking (fixed a real "alternates between world and screen-space
        /// coordinates" bug, confirmed via log). The 3x-scale addition is also kept, per request.
        /// </summary>
        private void UpdateBackpackQuickSelectCursorV061Style(float offsetX, float offsetY)
        {
            if (_softwareCursor == null) return;
            var mouse = FindVirtualMouseDevice();
            if (mouse == null || UsedCamera == null) return;

            Vector2 screenPos = mouse.position.ReadValue();
            screenPos.x += offsetX;
            screenPos.y += offsetY;

            Vector3 viewportPos = new Vector3(screenPos.x / Screen.width, screenPos.y / Screen.height, 0f);
            Ray ray = UsedCamera.ViewportPointToRay(viewportPos);

            Vector3 targetPos;
            Quaternion targetRot;
            // The player's own right hand (which holds this backpack) sits in front of the
            // items and blocks a single-hit raycast - RaycastAll instead, skipping any hit
            // that's a descendant of the player's own body, to find whatever's actually behind
            // the hand.
            RaycastHit hit = default;
            bool didHit = false;
            var allHits = Physics.RaycastAll(ray.origin, ray.direction, 1.5f, ~0, QueryTriggerInteraction.Ignore);
            float closestNonPlayerDist = float.MaxValue;
            foreach (var h in allHits)
            {
                if (h.distance >= closestNonPlayerDist) continue;
                bool isOwnBody = false;
                Transform checkT = h.transform;
                while (checkT != null)
                {
                    if (LocalPlayer != null && checkT == LocalPlayer.transform) { isOwnBody = true; break; }
                    checkT = checkT.parent;
                }
                if (isOwnBody) continue;
                hit = h;
                didHit = true;
                closestNonPlayerDist = h.distance;
            }
            if (didHit)
            {
                targetPos = hit.point;
                targetRot = Quaternion.LookRotation(-ray.direction, Vector3.up);
            }
            else
            {
                targetPos = ray.origin + ray.direction * 0.45f;
                targetRot = Quaternion.LookRotation(ray.direction, Vector3.up);
            }

            float t = Time.deltaTime * CampfireCursorSmoothSpeed;
            if (!_backpackCursorOwnPosInitialized)
            {
                _backpackCursorOwnPos = targetPos;
                _backpackCursorOwnRot = targetRot;
                _backpackCursorOwnPosInitialized = true;
            }
            _backpackCursorOwnPos = Vector3.Lerp(_backpackCursorOwnPos, targetPos, t);
            _backpackCursorOwnRot = Quaternion.Slerp(_backpackCursorOwnRot, targetRot, t);
            _softwareCursor.transform.position = _backpackCursorOwnPos;
            _softwareCursor.transform.rotation = _backpackCursorOwnRot;
            _softwareCursor.transform.localScale = Vector3.one * CampfireCursorScale;
        }

        private void UpdateCampfireCursorFromRealMousePosition(float offsetX, float offsetY, float surfaceLift, float fixedDistance = 0f, Transform targetRoot = null, float scale = -1f)
        {
            if (_softwareCursor == null) return;
            var mouse = FindVirtualMouseDevice();
            if (mouse == null || UsedCamera == null) return;

            Vector2 screenPos = mouse.position.ReadValue();

            // FIXED (confirmed via user report: cursor jumps toward whatever direction the stick
            // is pushed, then snaps straight back the instant the stick is released - "same
            // situation we had before", now much more noticeable since the cursor itself is 3x
            // bigger and easier to see). This is the game's own joystick-to-mouse translation
            // directly mapping the mouse position from the stick's own live deflection, rather
            // than remembering a position - releasing the stick reads back as neutral/center,
            // same as never having moved it. Not something to fix in the game's own input
            // translation; working around it instead: when the raw mouse reading is at/near that
            // neutral center, skip updating the cursor's target entirely and just hold wherever
            // it last was while the stick was actively held off-center, rather than following it
            // back to center every time the stick relaxes.
            float centerDist = Vector2.Distance(screenPos, new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
            bool skippedThisPoll = centerDist < CampfireCursorNeutralDeadzone;

            if (skippedThisPoll)
            {
                return;
            }

            screenPos.x += offsetX;
            screenPos.y += offsetY;

            // FIXED (found while investigating a report that this cursor had gone missing):
            // ScreenPointToRay depends on the camera's OWN pixelWidth/pixelHeight to interpret
            // the input coordinates - and UsedCamera (the native, non-VR camera) had its
            // targetTexture redirected to a tiny 64x64 texture for the desktop-mirror
            // performance work (see RedirectNativeCameraFromScreen), which also changes what it
            // reports as its own resolution. The real mouse position is still in full actual
            // screen-resolution units, so ScreenPointToRay was misinterpreting it entirely.
            // ViewportPointToRay sidesteps this - it only needs a normalized [0,1] coordinate,
            // computed here using Screen.width/height directly (the real resolution), so it's
            // immune to whatever targetTexture size the camera happens to have.
            Vector3 viewportPos = new Vector3(screenPos.x / Screen.width, screenPos.y / Screen.height, 0f);
            Ray ray = UsedCamera.ViewportPointToRay(viewportPos);

            Vector3 targetPos;
            Quaternion targetRot;

            // Per user's own insight, and a screenshot showing the cursor landing behind the
            // booklet's own thin pages instead of on them: a raycast-based cursor assumes there's
            // real, hittable 3D geometry to land on - true for the grab bag's items sitting on a
            // tray, but not necessarily true for the booklet's own thin pages or its smaller tabs,
            // which may have thin or missing colliders that let the ray sail past to whatever's
            // behind (the player's own backpack/body). A flat-screen-native UI cursor likely never
            // needed to "land" on anything at all - just sit at a fixed, readable distance in front
            // of the camera, regardless of what's actually there. When fixedDistance > 0, skip the
            // raycast/hit-point logic entirely and always use this constant distance instead.
            if (fixedDistance > 0f && targetRoot == null)
            {
                targetPos = ray.origin + ray.direction * fixedDistance;
                targetRot = Quaternion.LookRotation(ray.direction, Vector3.up);
            }
            // TARGETED raycast support (targetRoot param) is currently unused - every caller
            // passes null, since the booklet's own targeted-raycast attempt was confirmed via
            // diagnostic to never hit anything at all (hitCount=0 every time - nothing there to
            // raycast onto). Kept as a parameter/dead branch in case a future caller can use it,
            // but never exercised in the current build.
            else if (targetRoot != null)
            {
                var hits = Physics.RaycastAll(ray.origin, ray.direction, 1.5f, ~0, QueryTriggerInteraction.Ignore);
                RaycastHit? bookHit = null;
                float closestDist = float.MaxValue;
                foreach (var h in hits)
                {
                    if (h.distance >= closestDist) continue;
                    Transform walkT = h.transform;
                    while (walkT != null)
                    {
                        if (walkT == targetRoot) { bookHit = h; closestDist = h.distance; break; }
                        walkT = walkT.parent;
                    }
                }

                if (bookHit.HasValue)
                {
                    targetPos = bookHit.Value.point - ray.direction * surfaceLift;
                    targetRot = Quaternion.LookRotation(-ray.direction, Vector3.up);
                }
                else
                {
                    targetPos = ray.origin + ray.direction * fixedDistance;
                    targetRot = Quaternion.LookRotation(ray.direction, Vector3.up);
                }
            }
            // FIXED - same root cause and fix as UpdateGrabBagGazeSelection's own raycast: this
            // was hitting the campfire's own heat/warmth trigger volume at a few centimeters,
            // which is also the real explanation for the cursor appearing to sit right at the
            // player's face. QueryTriggerInteraction.Ignore skips it.
            //
            // REVERTED back to the original v0.6.1 value of 1.5f - confirmed via user report that
            // widening this to 3.5f caused a new problem (cursor "escaping" past the bag's edge to
            // land on the actual floor whenever aim strayed even slightly) without even solving
            // the original one it was meant for (boil-start gear still didn't activate at the
            // wider range either). The gear reachability issue needs its own, more targeted
            // solution - not a blanket range increase with this kind of side effect.
            else if (Physics.Raycast(ray, out RaycastHit hit, 1.5f, ~0, QueryTriggerInteraction.Ignore))
            {
                // Per user's report: now that the cursor is scaled up 3x, its center sitting
                // exactly at the surface hit point means half the sphere sinks into whatever it's
                // resting on (the bag). Pulling back slightly toward the camera, along the same
                // ray, so it sits on top of the surface instead of straddling it.
                //
                // FIXED (per user's own insight): this was applied unconditionally for ALL four
                // hand-held menus, not just the grab bag it was meant for - the booklet and
                // backpack quick select have smaller, more tightly-packed selectable elements
                // (text, small icons) where even this small a visual shift becomes noticeable,
                // and neither of them has the "sinking into a 3D surface" problem this was
                // actually fixing in the first place. Now passed in per-caller instead of a
                // single shared constant, so it can be zero for those menus.
                targetPos = hit.point - ray.direction * surfaceLift;
                targetRot = Quaternion.LookRotation(-ray.direction, Vector3.up);
            }
            else
            {
                targetPos = ray.origin + ray.direction * 0.45f;
                targetRot = Quaternion.LookRotation(ray.direction, Vector3.up);
            }

            // FIXED (confirmed via log: cursorPos was alternating between proper world-space
            // values and completely different, screen-space/canvas-like values on different
            // polls - e.g. (-1192, 140, -249) one poll, (729, 314, 1) the next, with the raw
            // mouse position unchanged between them. SoftwareCursor lives on a Canvas, and the
            // game's own UI system is independently repositioning it using its own coordinate
            // system, specifically whenever the mouse is actively changing (i.e. while the stick
            // is held) - fighting our own write. That's why it "disappeared" while pressing
            // (landing on nonsensical world coordinates from the wrong system) and only showed
            // correctly once released and the game stopped actively touching it. Same lesson as
            // the earlier separate-marker attempt: never READ this object's own transform as a
            // smoothing source again, since it's proven unreliable - maintain our own independent
            // position/rotation instead, and only ever WRITE the final result to the object.
            float t = Time.deltaTime * CampfireCursorSmoothSpeed;
            if (!_campfireCursorOwnPosInitialized)
            {
                // Snap directly on first use, rather than smoothing in from Vector3.zero (the
                // world origin) - which could be hundreds of meters from the player given this
                // game's arbitrary world coordinates, causing a jarring, far-away starting point.
                _campfireCursorOwnPos = targetPos;
                _campfireCursorOwnRot = targetRot;
                _campfireCursorOwnPosInitialized = true;
            }
            _campfireCursorOwnPos = Vector3.Lerp(_campfireCursorOwnPos, targetPos, t);
            _campfireCursorOwnRot = Quaternion.Slerp(_campfireCursorOwnRot, targetRot, t);
            _softwareCursor.transform.position = _campfireCursorOwnPos;
            _softwareCursor.transform.rotation = _campfireCursorOwnRot;

            // PIVOTED (per user's own clarification): the separate red-sphere marker never
            // rendered at all despite everything checking out correctly in diagnostics (position,
            // active state, renderer enabled, shader properties) across several rounds - while
            // SoftwareCursor itself, the game's own real cursor object, was confirmed genuinely
            // visible and working the whole time, just small/easy to miss. Rather than keep
            // chasing why a brand-new object won't render in this specific context, working with
            // the object already confirmed to render: scaling SoftwareCursor itself up directly,
            // instead of creating a separate marker at all.
            // Set directly every poll, not just "grow if smaller" - we already know this object
            // can get its properties reset by the game independently (confirmed for position
            // earlier), so enforcing this every frame is safer than a one-time or conditional set.
            // Per-menu override (per user's request: booklet reads a bit large at the same scale
            // used everywhere else) - scale < 0 means "use the shared default".
            _softwareCursor.transform.localScale = Vector3.one * (scale >= 0f ? scale : CampfireCursorScale);
        }

        private void EnsureGazeMarker()
        {
            if (_gazeMarker != null) return;

            _gazeMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _gazeMarker.name = "SonsVR_GazeMarker";

            // Don't let this interfere with the game's own physics/raycasts.
            var collider = _gazeMarker.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.Destroy(collider);
            }

            _gazeMarker.transform.localScale = Vector3.one * 0.06f;

            var renderer = _gazeMarker.GetComponent<Renderer>();
            if (renderer != null)
            {
                // This game runs HDRP, where legacy shaders like Sprites/Default often render
                // as invalid pink. Try HDRP's own unlit shader first; fall back gracefully
                // rather than risk an invisible-for-a-different-reason marker.
                Shader shader = Shader.Find("HDRP/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                if (shader == null) shader = Shader.Find("Sprites/Default");

                if (shader != null)
                {
                    var mat = new Material(shader);
                    mat.color = new Color(1f, 0.85f, 0.1f, 1f);
                    if (mat.HasProperty("_UnlitColor")) mat.SetColor("_UnlitColor", mat.color);
                    if (mat.HasProperty("_EmissiveColor")) mat.SetColor("_EmissiveColor", new Color(1f, 0.85f, 0.1f) * 3f);
                    // Best-effort: try to make it draw on top of other geometry so it doesn't
                    // disappear behind objects it's near/behind. Not guaranteed to work on
                    // every shader - HDRP doesn't always expose a simple ZTest override, so
                    // this is attempted defensively and silently does nothing if unsupported.
                    if (mat.HasProperty("_ZTest")) mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                    if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
                    mat.renderQueue = 4000; // Overlay
                    renderer.material = mat;
                }
                else
                {
                    MelonLogger.Warning("[VRPlayer] No compatible shader found for gaze marker - it may not be visible.");
                }
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        private GameObject _campfireCursorMarker;
        private float _lastCampfireCursorMarkerDiagTime = 0f;
        private bool _triedHideSoftwareCursorRenderers = false;

        /// <summary>
        /// Per user's request: replace the game's own hard-to-see SoftwareCursor visual with a
        /// custom, clearly-visible marker, same approach as the tarp's own gaze marker (a simple
        /// primitive sphere with an always-on-top emissive material) - just red instead of gold,
        /// to keep the two visually distinct. Still positions the real SoftwareCursor object too,
        /// in case anything in the game's own logic depends on its transform - only its own
        /// visual gets hidden, not the object itself.
        /// </summary>
        private void EnsureCampfireCursorMarker()
        {
            if (_campfireCursorMarker != null) return;

            _campfireCursorMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _campfireCursorMarker.name = "SonsVR_CampfireCursorMarker";

            var collider = _campfireCursorMarker.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            // WIDENED from 0.03f - confirmed the marker was correctly positioned all along
            // (my earlier "hundreds of meters away" read was a mistake - this game's own world
            // coordinates simply aren't centered near zero, so those values were normal for
            // wherever the player actually is). At half the size of the tarp's own confirmed-
            // visible marker (0.06f), this was likely just too small to notice, especially near
            // a bright campfire where red could blend into the fire's own warm lighting.
            _campfireCursorMarker.transform.localScale = Vector3.one * 0.08f;

            var renderer = _campfireCursorMarker.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("HDRP/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                if (shader == null) shader = Shader.Find("Sprites/Default");

                if (shader != null)
                {
                    var mat = new Material(shader);
                    mat.color = new Color(1f, 0.1f, 0.1f, 1f);
                    bool hasUnlitColor = mat.HasProperty("_UnlitColor");
                    bool hasEmissive = mat.HasProperty("_EmissiveColor");
                    bool hasZTest = mat.HasProperty("_ZTest");
                    bool hasZWrite = mat.HasProperty("_ZWrite");
                    if (hasUnlitColor) mat.SetColor("_UnlitColor", mat.color);
                    if (hasEmissive) mat.SetColor("_EmissiveColor", new Color(1f, 0.1f, 0.1f) * 3f);
                    if (hasZTest) mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                    if (hasZWrite) mat.SetInt("_ZWrite", 0);
                    mat.renderQueue = 4000; // Overlay
                    renderer.material = mat;
                    // DIAGNOSTIC: the tarp's identical marker code notes this ZTest override
                    // "isn't guaranteed to work on every shader" - confirming here whether it
                    // actually took, since a silent failure would mean this marker gets occluded
                    // by nearby geometry (the held bag/hand model, very close to the marker's own
                    // position) instead of always rendering on top like intended.
                    MelonLogger.Msg($"[VRPlayer] CampfireCursorMarker shader={shader.name} hasUnlitColor={hasUnlitColor} hasEmissive={hasEmissive} hasZTest={hasZTest} hasZWrite={hasZWrite} finalRenderQueue={mat.renderQueue}");
                }
                else
                {
                    MelonLogger.Warning("[VRPlayer] No compatible shader found for campfire cursor marker - it may not be visible.");
                }
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        /// <summary>
        /// Best-effort, one-time attempt to hide whatever visual the game's own SoftwareCursor
        /// uses to render itself, without disabling the object as a whole (so its transform stays
        /// live for anything else that might read it). Tries every plausible visual-component
        /// type rather than assuming a plain MeshRenderer, since this is a UI-adjacent object and
        /// its actual rendering approach isn't something we've directly confirmed.
        /// </summary>
        private void TryHideSoftwareCursorOwnVisual()
        {
            if (_triedHideSoftwareCursorRenderers || _softwareCursor == null) return;
            _triedHideSoftwareCursorRenderers = true;

            foreach (var r in _softwareCursor.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            foreach (var g in _softwareCursor.GetComponentsInChildren<UnityEngine.UI.Graphic>(true)) g.enabled = false;
            foreach (var cr in _softwareCursor.GetComponentsInChildren<CanvasRenderer>(true)) cr.SetAlpha(0f);
        }

        private void FindGrabBagCursorState()
        {
            if (_grabBagCursorState != null || _grabBagHeld == null) return;
            var found = _grabBagHeld.Find("CursorState");
            if (found == null) return;
            _grabBagCursorState = found;

            var components = found.GetComponents<Component>();
            MelonLogger.Msg($"[VRPlayer] GrabBagHeld/CursorState found with {components.Length} component(s):");
            foreach (var c in components)
            {
                if (c == null) continue;
                MelonLogger.Msg($"[VRPlayer]   - {c.GetType().FullName}");
            }
        }
        
        private float _lastHeldMenuDiagTime = 0f;
        private float _lastHeldMenuBaselineRefreshTime = 0f;
        private System.Collections.Generic.HashSet<string> _heldMenuBaselineActiveNames;
        private bool _heldMenuDiagWasActive = false;

        /// <summary>
        /// FIXED (confirmed via TWO consecutive log rounds: zero matches, even with a widened
        /// keyword list and searching ScreenSpaceCanvas specifically - both were guesses, and
        /// both guesses were wrong). Replaced entirely with a snapshot-diff approach that makes
        /// no assumption about where the UI lives or what it's named: captures every active
        /// GameObject's full path in the scene the moment BEFORE a held menu opens (the
        /// baseline), then diffs against a fresh snapshot once it's confirmed open, logging
        /// exactly what newly activated. This finds the real UI regardless of location or naming,
        /// since it's derived directly from the moment of activation rather than a guess.
        /// </summary>
        private void DiagHeldMenuSelection(bool handHeldMenuActive)
        {
            if (!DebugLogCanvasChanges) return;

            if (!handHeldMenuActive)
            {
                // Not open - keep refreshing the baseline periodically (not every frame, a
                // full-scene Transform scan is genuinely expensive) so it's reasonably close to
                // "the moment right before" whenever the player next opens one.
                if (Time.time - _lastHeldMenuBaselineRefreshTime > 0.5f)
                {
                    _lastHeldMenuBaselineRefreshTime = Time.time;
                    _heldMenuBaselineActiveNames = SnapshotActiveObjectPaths();
                }
                _heldMenuDiagWasActive = false;
                return;
            }

            if (!_heldMenuDiagWasActive)
            {
                // Just opened this frame - the baseline captured on the previous (not-yet-open)
                // frame is exactly what we want to diff against.
                _heldMenuDiagWasActive = true;
                _lastHeldMenuDiagTime = Time.time;
                return; // give it a moment to finish activating before diffing
            }

            if (Time.time - _lastHeldMenuDiagTime < 1.5f) return; // only diff once, ~1.5s after open
            if (_heldMenuBaselineActiveNames == null) return;
            _lastHeldMenuDiagTime = float.MaxValue; // don't diff again until the menu closes and reopens

            var current = SnapshotActiveObjectPaths();
            MelonLogger.Msg("[VRPlayer][HeldMenuDiag] ===== newly active since menu opened =====");
            int count = 0;
            foreach (var path in current)
            {
                if (!_heldMenuBaselineActiveNames.Contains(path))
                {
                    MelonLogger.Msg($"[VRPlayer][HeldMenuDiag]   + {path}");
                    count++;
                }
            }
            MelonLogger.Msg($"[VRPlayer][HeldMenuDiag] ({count} newly active objects)");

            // FOLLOW-UP: the snapshot diff above already found the real structure -
            // GrabBagHeld/CursorState (a known object from earlier project work, likely the real
            // cursor controller) and a GrabBagCategoryLayouts/.../[Category]LayoutItem/SnapAnchor
            // pattern, matching the tarp's own proven "LayoutItem+SnapAnchor" structure. This
            // dumps the actual COMPONENTS (not just names) on those specific objects, to identify
            // the real script types - same next step that revealed LayoutItem/
            // LayoutItemCursorSnapping/InventoryCursorController for the tarp.
            DumpComponentsOnMatchingObjects("CursorState");
            DumpComponentsOnMatchingObjects("SnapAnchor");
            DumpComponentsOnMatchingObjects("LayoutItem");
            DumpComponentsOnMatchingObjects("GrabBagCategoryLayouts");
        }

        private System.Collections.Generic.Dictionary<string, Type> _typeNameCache = new System.Collections.Generic.Dictionary<string, Type>();

        /// <summary>
        /// Finds the real C# Type for a given class name by searching all loaded assemblies (no
        /// namespace needed), then dumps every field's current value via standard reflection.
        /// This works even though the object we have is statically typed as the generic
        /// UnityEngine.Component - FieldInfo.GetValue only needs the correct Type to look up the
        /// field definitions and the runtime instance to read from, not a static cast.
        /// </summary>
        private void DumpFieldsByTypeName(string typeName, object instance)
        {
            if (!_typeNameCache.TryGetValue(typeName, out Type type))
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t.Name == typeName)
                        {
                            type = t;
                            break;
                        }
                    }
                    if (type != null) break;
                }
                _typeNameCache[typeName] = type; // cache the miss too (null), so we don't re-scan every call
            }

            if (type == null)
            {
                MelonLogger.Msg($"[VRPlayer][HeldMenuDiag]       (no C# Type found anywhere named '{typeName}')");
                return;
            }

            MelonLogger.Msg($"[VRPlayer][HeldMenuDiag]     --- fields on {type.FullName} ---");
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string valueStr;
                try
                {
                    var value = field.GetValue(instance);
                    valueStr = value?.ToString() ?? "null";
                }
                catch (Exception e)
                {
                    valueStr = $"<read failed: {e.Message}>";
                }
                MelonLogger.Msg($"[VRPlayer][HeldMenuDiag]       {field.FieldType.Name} {field.Name} = {valueStr}");
            }

            // FIXED (real bug in this diagnostic, found via log: every field found was just
            // "isWrapped"/"pooledPtr" - internal Il2CppInterop object-pooling plumbing from the
            // base wrapper class, not the game's own data at all). Il2CppInterop-generated
            // wrapper classes expose the game's actual data as PROPERTIES with get/set
            // accessors, not raw fields - exactly matching the pattern already proven for the
            // tarp (GetProperty("_currentlySnappedLayoutItem", ...), not GetField). Missed this
            // the first time; adding it now rather than replacing the field dump, since some
            // simple value types can still be raw fields.
            MelonLogger.Msg($"[VRPlayer][HeldMenuDiag]     --- properties on {type.FullName} ---");
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!prop.CanRead) continue;
                if (prop.GetIndexParameters().Length > 0) continue; // skip indexers, GetValue(instance) alone isn't enough for them
                string valueStr;
                try
                {
                    var value = prop.GetValue(instance);
                    valueStr = value?.ToString() ?? "null";
                }
                catch (Exception e)
                {
                    valueStr = $"<read failed: {e.Message}>";
                }
                MelonLogger.Msg($"[VRPlayer][HeldMenuDiag]       {prop.PropertyType.Name} {prop.Name} = {valueStr}");
            }
        }

        private void DumpComponentsOnMatchingObjects(string nameContains)
        {
            int found = 0;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                if (!t.name.Contains(nameContains)) continue;
                if (found >= 3) break; // several LayoutItem/SnapAnchor instances exist - a few samples is enough, not all of them

                MelonLogger.Msg($"[VRPlayer][HeldMenuDiag] --- components on '{t.name}' ({GetHierarchyPath(t)}) ---");
                foreach (var comp in t.GetComponents<Component>())
                {
                    if (comp == null) continue;

                    // FIXED (confirmed via log: every single component here resolved to the
                    // generic UnityEngine.Component wrapper - the same IL2CPP interop wall hit
                    // before with PickupGui/PlayerInteractStimuli). Going straight to the native
                    // IL2CPP class system instead of managed reflection, same proven technique
                    // already used successfully elsewhere in this file.
                    string nativeTypeName;
                    try
                    {
                        IntPtr classPtr = IL2CPP.il2cpp_object_get_class(comp.Pointer);
                        nativeTypeName = classPtr != IntPtr.Zero ? IL2CPP.il2cpp_class_get_name_(classPtr) : "<null class>";
                    }
                    catch (Exception e)
                    {
                        nativeTypeName = $"<lookup failed: {e.Message}>";
                    }
                    MelonLogger.Msg($"[VRPlayer][HeldMenuDiag]     managedType={comp.GetType().FullName} nativeType={nativeTypeName}");

                    // Field-dump the three specific classes identified via the native-type
                    // lookup above (InputCursorState, VirtualCursorSnapAnchor, GrabBagLayoutItem)
                    // - looking for the equivalent of _currentlySnappedLayoutItem, which is what
                    // actually worked for the tarp.
                    if (nativeTypeName == "InputCursorState" || nativeTypeName == "VirtualCursorSnapAnchor" || nativeTypeName == "GrabBagLayoutItem")
                    {
                        DumpFieldsByTypeName(nativeTypeName, comp);
                    }
                }
                found++;
            }
        }

        private System.Collections.Generic.HashSet<string> SnapshotActiveObjectPaths()
        {
            var set = new System.Collections.Generic.HashSet<string>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                set.Add(GetHierarchyPath(t));
            }
            return set;
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

        /// <summary>
        /// Original working version - moves the real InventoryCursorController transform
        /// straight to the gaze point every frame. Confirmed this is what actually drives
        /// selection at the ground tarp.
        /// </summary>
        private void UpdateCursorPositionFromGaze()
        {
            if (_cursorController == null || StereoRender.Head == null) return;

            Vector3 origin = StereoRender.Head.position;
            Vector3 direction = StereoRender.Head.forward;

            // Keeps your working calibration completely untouched for normal ground tarp use!
            float lookDownwardFactor = Mathf.Clamp01(-direction.y);
            float adaptiveDistance = Mathf.Lerp(3.5f, 1.4f, lookDownwardFactor);

            Vector3 desiredPosition = origin + direction * adaptiveDistance;
            
            Quaternion desiredRotation = Quaternion.LookRotation(direction, Vector3.up);
            _cursorController.gameObject.transform.SetPositionAndRotation(desiredPosition, desiredRotation);
        }

        /// <summary>
        /// Same direct-snap approach as above, generalized to any target transform and distance
        /// range - used for the campfire grab bag's gaze marker (CursorState has no renderer of
        /// its own, so a synthetic marker stands in for it, calibrated to arm's-reach distance).
        /// </summary>
        private void UpdateCursorPositionFromGaze(Transform target, float farDistance, float nearDistance)
        {
            if (target == null || StereoRender.Head == null) return;

            Vector3 origin = StereoRender.Head.position;
            Vector3 direction = StereoRender.Head.forward;

            float lookDownwardFactor = Mathf.Clamp01(-direction.y);
            float adaptiveDistance = Mathf.Lerp(farDistance, nearDistance, lookDownwardFactor);

            Vector3 desiredPosition = origin + direction * adaptiveDistance;
            Quaternion desiredRotation = Quaternion.LookRotation(direction, Vector3.up);
            target.SetPositionAndRotation(desiredPosition, desiredRotation);
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

            _PlayerMiniMapTransf = playerStandin.transform.Find("PlayerUI/PlayerMiniMap");

            Transform minimapcanvasTransform =
                playerStandin.transform.Find("PlayerUI/PlayerMiniMap/GPSTrackerSystem/Canvas");
            if (minimapcanvasTransform != null)
            {
                _playerMiniMapCanvas = minimapcanvasTransform.GetComponent<Canvas>();
            }

            Transform gpsTransform = playerStandin.transform.Find("PlayerUI/PlayerMiniMap/GPSTrackerSystem");
            if (gpsTransform != null)
            {
                gpsTransform.localScale = Vector3.one;
            }
        }

        private float _lastTitleCanvasCheckTime = 0f;
        private float _lastCanvasFindDiagTime = 0f;
        private float _lastLoadingIndictorDiagTime = 0f;

        /// <summary>
        /// Title menu (TitleMenu/BottomCanvas/VersionCanvas, then TitleGui after the initial
        /// splash) renders correctly on the flat screen even with the mod active - it's just
        /// never been captured by the VR stereo cameras, the same category of problem already
        /// solved for the inventory UI. Reuses that exact proven technique here.
        /// </summary>
        private void ConvertTitleMenuCanvasesToWorldSpace()
        {
            // ROOT CAUSE FOUND: comparing timestamps in a user-provided log against the
            // "Scene or camera changed - respawning VR player rig" / "Could not find
            // LocalPlayer from camera" warnings showed those events bracket EXACTLY the
            // window the Options screen was open (confirmed twice in the same log) - and this
            // function produced ZERO log output for that entire window, meaning it was
            // silently bailing on the line below every single call, before ever reaching the
            // OptionsPanel search. UsedCamera goes transiently null while SetSceneAndCamera is
            // mid-transition (see its early "activeSceneAndCam.camera == UsedCamera" check),
            // which is exactly what happens while the game has swapped away to show Options.
            // Checked what UsedCamera is actually USED for below: only one line
            // (canvas.worldCamera = UsedCamera), which just tells Unity which camera renders/
            // raycasts the world-space canvas - NOT positioning. Positioning already uses
            // StereoRender.Head (the persistent VR rig's own head transform, unaffected by
            // this camera swap since it lives on the DontDestroyOnLoad rig body). So gating
            // the ENTIRE function on UsedCamera being non-null was blocking OptionsPanel
            // conversion for no positioning-related reason. Relaxed to only require
            // StereoRender/Head (what positioning actually needs); the worldCamera assignment
            // below is now guarded separately to only run when UsedCamera happens to be valid.
            if (StereoRender == null || StereoRender.Head == null) return;
            // PERFORMANCE FIX: this function is title-screen-only by design (it only ever
            // touches TitleMenu/BottomCanvas/VersionReadout/Options), but had no scene check at
            // all - it was relying on the old UsedCamera==null gate to accidentally skip most
            // work outside the title screen. Relaxing that gate (to fix Options never
            // converting) removed that accidental protection, so this function - including the
            // expensive FindObjectsOfType<Transform>() full-scene scan added for the OptionsPanel
            // search - started running every 0.5s unconditionally, even during normal open-world
            // gameplay. CONFIRMED by user report: constant jitter/lag appeared right after the
            // Options fix. Adding the proper, explicit gate (same UsedSceneName == "SonsTitleScene"
            // pattern already used elsewhere in this file) so the whole body - scan included -
            // only ever runs while actually at the title screen.
            if (UsedSceneName != "SonsTitleScene") return;
            // V159: if HMD pose was unavailable during title camera adoption,
            // do not convert/anchor canvases from the old gaze direction.
            if (_v159PendingTitleCamera != null) return;
            if (Time.time - _lastTitleCanvasCheckTime < 0.5f) return;
            _lastTitleCanvasCheckTime = Time.time;
            // CONFIRMED via the keyword-scan diagnostic: the multiplayer waiting room/lobby is
            // a simple top-level root canvas named "MultiplayerMenuCanvas" (containing
            // CoopLobbyDialogGui and everything else) - unlike "OptionsPanel" this name isn't
            // ambiguous, so a direct GameObject.Find works fine, no full-path search needed.
            //
            // CONFIRMED via the same diagnostic: the loading screen has the SAME ambiguity
            // problem OptionsPanel had - TWO objects share the name "LoadingIndictor" (yes,
            // that's the actual spelling in-game): a short-lived "SceneLoader/LoadingIndictor"
            // that's only active for ~1.5s, and the real long-lived root-level "LoadingIndictor"
            // (active for the whole ~46s load). Using the same full-path search technique as
            // OptionsPanel, matching only the root-level one (parent == null).
            string[] names = { "TitleMenu", "BottomCanvas", "VersionReadout/VersionCanvas", "Canvas", "MultiplayerMenuCanvas", "LoadingIndictor" };
            foreach (var name in names)
            {
                GameObject go;
                if (name == "LoadingIndictor")
                {
                    GameObject loadingIndictorRoot = null;
                    foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
                    {
                        if (t == null || t.name != "LoadingIndictor") continue;
                        if (t.parent == null) { loadingIndictorRoot = t.gameObject; break; }
                    }
                    // CONFIRMED via diagnostic: found=True but hasCanvas=False on the root -
                    // the Canvas component isn't on LoadingIndictor itself, it's on one of its
                    // children. Searching there instead.
                    Canvas childCanvas = loadingIndictorRoot != null
                        ? loadingIndictorRoot.GetComponentInChildren<Canvas>(true)
                        : null;
                    go = childCanvas != null ? childCanvas.gameObject : null;
                    if (DebugLogCanvasChanges && Time.time - _lastLoadingIndictorDiagTime > 2f)
                    {
                        _lastLoadingIndictorDiagTime = Time.time;
                        // childCanvasFound was consistently False with rootFound=True - checking
                        // for UIDocument too, since this game is confirmed to use UI Toolkit for
                        // some panels (see the UIDocument scan elsewhere in this file), and the
                        // pause/save menu is confirmed to use neither Canvas nor UIDocument at
                        // all - if this comes back false too, LoadingIndictor is in that same
                        // "neither" category and needs the flat-quad approach instead.
                        var uiDoc = loadingIndictorRoot != null ? loadingIndictorRoot.GetComponentInChildren<UnityEngine.UIElements.UIDocument>(true) : null;
                        MelonLogger.Msg($"[VRPlayer][TitleCanvasDiag] LoadingIndictor lookup: rootFound={loadingIndictorRoot != null} childCanvasFound={go != null} hasUIDocument={uiDoc != null}");
                    }
                }
                else if (name == "Canvas")
                {
                    GameObject optionsPanel = null;
                    foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
                    {
                        if (t == null || t.name != "OptionsPanel") continue;
                        if (GetHierarchyPath(t) == "Canvas/PrimaryPanel/OptionsPanel")
                        {
                            optionsPanel = t.gameObject;
                            break;
                        }
                    }
                    go = (optionsPanel != null && optionsPanel.transform.parent != null && optionsPanel.transform.parent.parent != null)
                        ? optionsPanel.transform.parent.parent.gameObject
                        : null;
                    if (DebugLogCanvasChanges && Time.time - _lastCanvasFindDiagTime > 2f)
                    {
                        _lastCanvasFindDiagTime = Time.time;
                        MelonLogger.Msg($"[VRPlayer][TitleCanvasDiag] Canvas lookup: optionsPanelFound={optionsPanel != null} resolvedGo={(go != null ? go.name : "NULL")}");
                    }
                }
                else
                {
                    go = GameObject.Find(name);
                }
                if (go == null) continue;
                var canvas = go.GetComponent<Canvas>();
                if (canvas == null) continue;
                if (canvas.renderMode == RenderMode.WorldSpace) continue; // already correctly converted, live state confirms it

                canvas.renderMode = RenderMode.WorldSpace;
                if (UsedCamera != null) canvas.worldCamera = UsedCamera; // leave as-is (falls back to Camera.main) if transiently null
                canvas.gameObject.layer = LayerMask.NameToLayer("Default");

                // CONFIRMED via diagnostic logging: the canvas's resulting scale was (0.00,
                // 0.00, 0.00) - essentially zero, explaining total invisibility regardless of
                // material/ZTest work. The bug: dividing by Screen.height (the PHYSICAL VR
                // display's pixel height, which can be several thousand pixels per eye) instead
                // of the canvas's own LOGICAL size (its RectTransform, typically fixed to a
                // reference resolution like 1920x1080 independent of actual screen resolution).
                // Using the canvas's own rect height as the divisor instead.
                var canvasRect = canvas.GetComponent<RectTransform>();
                float canvasLogicalHeight = (canvasRect != null && canvasRect.rect.height > 1f) ? canvasRect.rect.height : 1080f;
                float targetWorldHeight = 0.5f;
                canvas.transform.localScale = Vector3.one * (targetWorldHeight / canvasLogicalHeight);

                var scaler = canvas.GetComponent<CanvasScaler>();
                if (scaler != null) scaler.enabled = false;

                // TEST: pushing the canvas upward, in case the actual button content sits
                // low within its own RectTransform bounds (matching how it appears at the
                // bottom of the flat screen) - even with correct scale/position/shader, that
                // could put the real visual content down near floor/water level rather than at
                // head height, perpetually hidden behind the background scene regardless of any
                // other fix. TitleCanvasTestYOffset is a quick tunable for this specific test.
                //
                // CONFIRMED via user testing: panels come in noticeably too high, requiring an
                // upward look to read them, and this got WORSE for the TitleMenu specifically
                // right after backing out of Options - i.e. right after the user had just been
                // looking up at the (also too-high) Options panel. Root cause: StereoRender.
                // Head.forward was used raw (pitch included) for BOTH the placement direction
                // and the panel's facing rotation. Since canvases only reposition on the
                // moment they're (re)converted, converting while the head happens to be tilted
                // up bakes that tilt into the new position/rotation, pushing the panel even
                // higher and tilting it upward - the classic VR "menu chases your gaze
                // upward" spiral. Flattening the pitch component (matching the same pattern
                // already used for Origin's rotation elsewhere in this file) so placement only
                // depends on which way the player is facing (yaw), not how far up/down they're
                // currently looking.
                //
                // CONFIRMED via user testing: TitleCanvasTestYOffset=0.2f is correct for
                // TitleMenu/BottomCanvas/VersionReadout (restored correctly after backing out
                // of Options), but Options itself still sits too high at that same value - so
                // it needs its own independent offset (OptionsCanvasYOffset) rather than
                // sharing one value with the main menu canvases.
                float yOffset = (name == "Canvas") ? OptionsCanvasYOffset
                    : (name == "MultiplayerMenuCanvas") ? MultiplayerCanvasYOffset
                    : (name == "LoadingIndictor") ? LoadingCanvasYOffset
                    : TitleCanvasTestYOffset;
                Vector3 flatForward = StereoRender.Head.forward;
                flatForward.y = 0f;
                if (flatForward.sqrMagnitude < 0.001f) flatForward = Vector3.forward; // avoid NaN if looking straight up/down
                flatForward.Normalize();
                Vector3 pos = StereoRender.Head.position + flatForward * 1.0f + Vector3.up * yOffset;
                canvas.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(flatForward, Vector3.up));

                // ALWAYS-ON-TOP FIX for the known occlusion issue (menu getting hidden behind
                // the title screen's own background scene, e.g. the rising hands/water intro
                // animation): a world-space canvas respects normal 3D depth testing by default,
                // unlike the ScreenSpaceOverlay mode it started in.
                //
                // CORRECTED APPROACH: the first attempt created a brand-new Material via
                // "new Material(shader)" + CopyPropertiesFromMaterial, and testing showed it
                // made the menu fully invisible (previously it rendered fine, just occluded).
                // This matches this whole session's repeated, hard-learned lesson: creating a
                // material from scratch in this game tends to lose whatever made the ORIGINAL
                // correctly compatible with this HDRP build, even when copying its property
                // values. Modifying graphic.material IN PLACE instead - Unity's own accessor
                // already clones-on-first-write safely when the material is shared, preserving
                // the original's real compiled state, which a manual "new Material(...)" does
                // not replicate.
                // CONFIRMED via user testing: the water level in the title screen's background
                // scene sits at roughly chin height - the menu was correctly rendering with its
                // original, untouched materials the whole time, just positioned below the water
                // surface (hidden behind/under it) before TitleCanvasTestYOffset was added. Both
                // the ZTest/ZWrite override (confirmed a no-op - UI/Default doesn't expose those
                // as material properties) and the HDRP/Lit shader swap on Image components have
                // been REMOVED entirely, going back to fully untouched materials, to test
                // whether the height fix alone is sufficient without any material changes.

                // DIAGNOSTIC: comparing against the confirmed-working minimap conversion turned
                // up one clear structural difference (this method repositions the canvas
                // manually, minimap doesn't) - but rather than guess whether that's the cause,
                // logging the canvas's actual runtime state directly so we have real evidence:
                // is the canvas itself enabled, does a CanvasGroup exist with a suspicious alpha/
                // interactable state, and are the individual Graphic elements underneath it
                // actually active, enabled, and non-transparent.
                // DIAGNOSTIC: this per-graphic dump already served its purpose (finding the
                // real OptionsPanel path earlier this session) and is expensive/verbose - not
                // needed for ongoing position-offset testing. Gated behind DebugLogCanvasChanges
                // (same flag as the keyword scanner, default off) so logs stay clean unless
                // explicitly re-enabled for future diagnosis. The lightweight one-line
                // "Converted title canvas to world space" confirmation below stays unconditional.
                if (DebugLogCanvasChanges)
                {
                    var canvasGroup = canvas.GetComponent<CanvasGroup>();
                    var sb = new System.Text.StringBuilder();
                    foreach (var graphic in canvas.GetComponentsInChildren<Graphic>(true))
                    {
                        sb.Append($"[{graphic.gameObject.name}: active={graphic.gameObject.activeInHierarchy} enabled={graphic.enabled} alpha={graphic.color.a:F2}] ");
                    }
                    Vector3 finalScale = canvas.transform.localScale;
                    MelonLogger.Msg($"[VRPlayer][TitleCanvasDiag] {name}: canvas.enabled={canvas.enabled} sortingOrder={canvas.sortingOrder} overrideSorting={canvas.overrideSorting} canvasGroup={(canvasGroup != null ? $"alpha={canvasGroup.alpha:F2}" : "none")} scaleX={finalScale.x:F8} scaleY={finalScale.y:F8} scaleZ={finalScale.z:F8} computedScaleValue={(targetWorldHeight / canvasLogicalHeight):F8} canvasRectHeight={canvasLogicalHeight:F1} pos={canvas.transform.position} graphics=[{sb}]");
                }

                MelonLogger.Msg($"[VRPlayer] Converted title canvas to world space: {name}");
            }
        }

        private void ConvertCanvasToWorldSpace()
        {
            if (_screenSpaceCanvas == null || _screenSpaceCanvas.renderMode == RenderMode.WorldSpace) return;

            _screenSpaceCanvas.renderMode = RenderMode.WorldSpace;
            _screenSpaceCanvas.worldCamera = UsedCamera;
            _screenSpaceCanvas.gameObject.layer = LayerMask.NameToLayer("Default");

            // V64: V63 measured the live HUD canvas directly in the VR eye. The physical
            // output is 3840x2160, but this Canvas is logically 1920x1080. Dividing by
            // Screen.height therefore made the intended 0.35 m HUD plane exactly HALF-size
            // (0.175 m), which is the central-column regression the headset showed. Scale from
            // the Canvas's own logical RectTransform height instead; Screen.height is only a
            // fallback for an abnormal zero/uninitialized rect. No layout rebuild is involved.
            float targetWorldHeight = 0.35f;
            RectTransform hudRectV64 = _screenSpaceCanvas.GetComponent<RectTransform>();
            float canvasLogicalHeightV64 =
                hudRectV64 != null && hudRectV64.rect.height > 1f
                    ? hudRectV64.rect.height
                    : Mathf.Max(1f, Screen.height);
            float scale = targetWorldHeight / canvasLogicalHeightV64;
            _screenSpaceCanvas.transform.localScale = Vector3.one * scale;
            MelonLogger.Warning(
                $"[VRPlayer][AER V66][HudScale] Screen={Screen.width}x{Screen.height} " +
                $"canvasRect={(hudRectV64 != null ? hudRectV64.rect.size.ToString() : "null")} " +
                $"logicalHeight={canvasLogicalHeightV64:F1} targetWorldHeight={targetWorldHeight:F3} " +
                $"scale={scale:F8}. V63 proved Screen.height was a 2x-too-large denominator.");

            var scaler = _screenSpaceCanvas.GetComponent<CanvasScaler>();
            if (scaler != null)
            {
                scaler.enabled = false;
            }

            _canvasConvertedToWorldSpace = true;
        }

        private void ConvertMinimapCanvasToWorldSpace()
        {
            if (_playerMiniMapCanvas == null || _playerMiniMapCanvas.renderMode == RenderMode.WorldSpace) return;

            _playerMiniMapCanvas.renderMode = RenderMode.WorldSpace;
            _playerMiniMapCanvas.worldCamera = UsedCamera;
            // FIXED: was forcing "Default" (copy-pasted from the other canvas conversions, correct
            // for those) - confirmed via diagnostic that this minimap's own PARENT
            // (PlayerMiniMap) natively lives on the "UI" layer (something the game itself set, not
            // us), while this line put the Canvas itself on a DIFFERENT layer ("Default").
            // MainCameraFP's own cullingMask (-41) specifically excludes "UI" - a real, confirmed
            // structural mismatch worth correcting regardless of the full explanation. Matching
            // the Canvas to its actual parent's layer instead of a hardcoded guess.
            _playerMiniMapCanvas.gameObject.layer = _PlayerMiniMapTransf.gameObject.layer;

            // V14: enlarge the actual GPS/minimap canvas, but leave its parent/root scale alone.
            // V13 proved scaling PlayerMiniMap itself does not touch the separate status ring.
            // The ring is PlayerVitalsUi under the main HUD canvas, so the two systems are scaled
            // independently and placed on the same 0.50m physical plane.
            float targetWorldHeight = 0.35f * MinimapScaleMultiplier;

            // V66: V65 revealed that this Canvas root has rect=(0,0) after conversion, so its
            // attempted "logical" sizing silently fell back to physical Screen.height=2160 and
            // changed absolutely nothing. The main HUD Canvas, however, is live and measured as
            // the game's 1920x1080 logical UI space. Use that SAME logical height as the minimap
            // design basis. This is a measured relationship, not another desktop-resolution guess.
            RectTransform minimapCanvasRtV66 = _playerMiniMapCanvas.GetComponent<RectTransform>();
            RectTransform hudCanvasRtV66 = _screenSpaceCanvas != null
                ? _screenSpaceCanvas.GetComponent<RectTransform>()
                : null;
            float hudLogicalHeightV66 =
                hudCanvasRtV66 != null && Mathf.Abs(hudCanvasRtV66.rect.height) > 1f
                    ? Mathf.Abs(hudCanvasRtV66.rect.height)
                    : 0f;
            float minimapOwnHeightV66 =
                minimapCanvasRtV66 != null && Mathf.Abs(minimapCanvasRtV66.rect.height) > 1f
                    ? Mathf.Abs(minimapCanvasRtV66.rect.height)
                    : 0f;
            float canvasHeightInPixels;
            string scaleBasisV66;
            if (hudLogicalHeightV66 > 1f)
            {
                canvasHeightInPixels = hudLogicalHeightV66;
                scaleBasisV66 = "mainHudCanvasRect";
            }
            else if (minimapOwnHeightV66 > 1f)
            {
                canvasHeightInPixels = minimapOwnHeightV66;
                scaleBasisV66 = "minimapCanvasRect";
            }
            else
            {
                canvasHeightInPixels = Mathf.Max(1f, Screen.height);
                scaleBasisV66 = "physicalScreenFallback";
            }

            float scale = targetWorldHeight / canvasHeightInPixels;
            _playerMiniMapCanvas.transform.localScale = Vector3.one * scale;
            MelonLogger.Warning(
                $"[VRPlayer][AER V66][MinimapScale] Screen={Screen.width}x{Screen.height} " +
                $"minimapCanvasRect={(minimapCanvasRtV66 != null ? minimapCanvasRtV66.rect.size.ToString() : "null")} " +
                $"hudCanvasRect={(hudCanvasRtV66 != null ? hudCanvasRtV66.rect.size.ToString() : "null")} " +
                $"basis={scaleBasisV66} logicalHeight={canvasHeightInPixels:F1} " +
                $"targetWorldHeight={targetWorldHeight:F3} scale={scale:F8}. V65's zero-rect fallback is removed.");

            var miniscaler = _playerMiniMapCanvas.GetComponent<CanvasScaler>();
            if (miniscaler != null)
            {
                miniscaler.enabled = false;
            }

            _canvasMinimapConvertedToWorldSpace = true;
            // V66: no stencil refresh here. V65 gameplay telemetry proved Unity already rebuilds the native
            // ScreenMask correctly (mask writes stencil 1; Map reads Equal/1). The earlier V65 refresh
            // happened before gameplay and was both mistimed and unnecessary.
        }

        private int _alwaysOnTopLayer = -1;
        private bool _alwaysOnTopLayerResolved = false;
        private Camera _alwaysOnTopLeftCam;
        private Camera _alwaysOnTopRightCam;

        /// <summary>
        /// The REAL fix (ForceMinimapAndVitalsAlwaysOnTop below was the first attempt - confirmed
        /// via diagnostic log that this project's UI uses stock "UI/Default", which doesn't expose
        /// _ZTest at all, so that approach could never have worked here and correctly did nothing
        /// rather than risk a broken material). Since minimap/vitals are ordinary scene geometry
        /// rendered by the real VR eye cameras (LeftCam/RightCam) in their one normal depth-tested
        /// pass, the standard, robust Unity technique is a small extra camera PER EYE that renders
        /// ONLY this UI, after everything else, with its depth buffer cleared first - so nothing
        /// can occlude it no matter how close it gets. Lower risk than the binoculars zoom camera
        /// specifically because it renders a couple of small UI elements only (a dedicated layer,
        /// nothing else), not a full HDRP scene every frame. Each overlay camera targets the SAME
        /// RenderTexture as its corresponding eye camera (LeftRT/RightRT) with a higher `depth` so
        /// it draws after, `clearFlags=Depth` (wipes only the depth buffer, keeps the color already
        /// drawn), and a `cullingMask` restricted to one freshly-picked, genuinely-unused layer
        /// (found by scanning for the first layer index with no name assigned, rather than assuming
        /// a hardcoded layer is free) containing only the minimap and vitals UI. Deliberately does
        /// NOT touch LeftCam/RightCam's own cullingMask (defaultCullingMask=-1, confirmed via
        /// StereoRender.cs) to avoid any risk to the core eye rendering everything else depends on -
        /// this means the UI still also renders once, normally, in the main pass too (redundant but
        /// harmless), with the overlay pass drawing after and winning visually.
        /// </summary>
        // SAFETY SWITCH: confirmed via user report + screenshot that this feature broke the main
        // menu entirely (replaced with an isolated glowing light source on a black background, no
        // menu UI at all) - no exceptions in the log, both overlay cameras created successfully,
        // meaning the code ran exactly as written but what it does is wrong. Leading theory: HDRP
        // cameras largely ignore the legacy `Camera.clearFlags` property (Depth/Skybox/etc.) and
        // instead use `HDAdditionalCameraData.clearColorMode` - which these overlay cameras never
        // set, so HDRP likely defaults to its own clear behavior (wiping color entirely) rather
        // than the intended "preserve what's already drawn, only clear depth". Same category of
        // mistake already learned and fixed once before in this project (the binoculars zoom
        // camera work). Defaulted OFF so play isn't blocked while this gets sorted out properly.
        public static bool EnableAlwaysOnTopOverlay = false;

        private void EnsureAlwaysOnTopLayerAndCameras()
        {
            if (!EnableAlwaysOnTopOverlay) return;
            if (StereoRender == null || StereoRender.LeftCam == null || StereoRender.RightCam == null) return;
            if (StereoRender.LeftRT == null || StereoRender.RightRT == null) return;

            if (!_alwaysOnTopLayerResolved)
            {
                _alwaysOnTopLayerResolved = true;

                // FIXED (round 2): confirmed via the full 0-31 dump last round that EVERY layer in
                // this game is named/occupied - there is no genuinely free layer anywhere, so
                // "find an unnamed one" can never succeed here no matter how wide the search range.
                // New strategy: several layer NAMES strongly suggest collision/trigger-only purposes
                // with no visible geometry (BasicTrigger, ImpactDetect, Poke, Blocker) - rather than
                // trust the name alone, directly verify by checking whether any real Renderer
                // currently exists on each candidate before committing to one, and use the first
                // one that's genuinely clear of visible geometry right now.
                var occupied = new System.Text.StringBuilder();
                for (int i = 0; i <= 31; i++)
                {
                    occupied.Append($"{i}='{LayerMask.LayerToName(i)}' ");
                }
                MelonLogger.Msg($"[VRPlayer][AlwaysOnTop] All layer names: {occupied}");

                int[] candidateLayers = { 3, 14, 24, 25 }; // BasicTrigger, ImpactDetect, Poke, Blocker
                var allRenderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
                foreach (int candidate in candidateLayers)
                {
                    bool hasRenderer = false;
                    foreach (var r in allRenderers)
                    {
                        if (r != null && r.gameObject.layer == candidate) { hasRenderer = true; break; }
                    }
                    MelonLogger.Msg($"[VRPlayer][AlwaysOnTop] Candidate layer {candidate} ('{LayerMask.LayerToName(candidate)}'): hasVisibleRenderer={hasRenderer}");
                    if (!hasRenderer)
                    {
                        _alwaysOnTopLayer = candidate;
                        break;
                    }
                }
                MelonLogger.Msg($"[VRPlayer][AlwaysOnTop] Resolved dedicated layer index: {_alwaysOnTopLayer} (name at that index: '{(_alwaysOnTopLayer >= 0 ? LayerMask.LayerToName(_alwaysOnTopLayer) : "n/a")}')");
            }
            if (_alwaysOnTopLayer < 0) return; // none of the candidate layers were clear of visible geometry - bail safely, no fix applied rather than guess

            if (_alwaysOnTopLeftCam == null)
            {
                GameObject leftGo = new GameObject("SonsVR_AlwaysOnTopLeftCam");
                _alwaysOnTopLeftCam = leftGo.AddComponent<Camera>();
                var leftHdData = leftGo.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
                // FIX: HDRP largely ignores the legacy Camera.clearFlags below for its own clear
                // behavior - without this, HDRP defaults to clearing color entirely each frame,
                // wiping out whatever LeftCam already drew (confirmed as the cause of the broken
                // main menu). Explicitly telling HDRP not to clear color preserves it, matching
                // what `clearFlags = Depth` was actually meant to achieve.
                leftHdData.clearColorMode = UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData.ClearColorMode.None;
                _alwaysOnTopLeftCam.stereoTargetEye = StereoTargetEyeMask.None;
                _alwaysOnTopLeftCam.targetTexture = StereoRender.LeftRT;
                _alwaysOnTopLeftCam.clearFlags = CameraClearFlags.Depth;
                _alwaysOnTopLeftCam.cullingMask = 1 << _alwaysOnTopLayer;
                _alwaysOnTopLeftCam.depth = StereoRender.LeftCam.depth + 10;
                MelonLogger.Msg("[VRPlayer][AlwaysOnTop] Created left overlay camera");
            }
            if (_alwaysOnTopRightCam == null)
            {
                GameObject rightGo = new GameObject("SonsVR_AlwaysOnTopRightCam");
                _alwaysOnTopRightCam = rightGo.AddComponent<Camera>();
                var rightHdData = rightGo.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
                rightHdData.clearColorMode = UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData.ClearColorMode.None;
                _alwaysOnTopRightCam.stereoTargetEye = StereoTargetEyeMask.None;
                _alwaysOnTopRightCam.targetTexture = StereoRender.RightRT;
                _alwaysOnTopRightCam.clearFlags = CameraClearFlags.Depth;
                _alwaysOnTopRightCam.cullingMask = 1 << _alwaysOnTopLayer;
                _alwaysOnTopRightCam.depth = StereoRender.RightCam.depth + 10;
                MelonLogger.Msg("[VRPlayer][AlwaysOnTop] Created right overlay camera");
            }

            // Every frame: match the real eye cameras' transform/projection exactly, so this UI
            // renders with correct, consistent perspective from each eye.
            _alwaysOnTopLeftCam.transform.SetPositionAndRotation(StereoRender.LeftCam.transform.position, StereoRender.LeftCam.transform.rotation);
            _alwaysOnTopLeftCam.fieldOfView = StereoRender.LeftCam.fieldOfView;
            _alwaysOnTopLeftCam.nearClipPlane = StereoRender.LeftCam.nearClipPlane;
            _alwaysOnTopLeftCam.farClipPlane = StereoRender.LeftCam.farClipPlane;

            _alwaysOnTopRightCam.transform.SetPositionAndRotation(StereoRender.RightCam.transform.position, StereoRender.RightCam.transform.rotation);
            _alwaysOnTopRightCam.fieldOfView = StereoRender.RightCam.fieldOfView;
            _alwaysOnTopRightCam.nearClipPlane = StereoRender.RightCam.nearClipPlane;
            _alwaysOnTopRightCam.farClipPlane = StereoRender.RightCam.farClipPlane;

            if (_PlayerMiniMapTransf != null) SetLayerRecursive(_PlayerMiniMapTransf, _alwaysOnTopLayer);
            if (_playerVitalsUiTransf != null) SetLayerRecursive(_playerVitalsUiTransf, _alwaysOnTopLayer);
            if (_messageListTransf != null) SetLayerRecursive(_messageListTransf, _alwaysOnTopLayer);
        }

        private void SetLayerRecursive(Transform t, int layer)
        {
            if (t == null) return;
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
            {
                SetLayerRecursive(t.GetChild(i), layer);
            }
        }

        /// <summary>
        /// SUPERSEDED by EnsureAlwaysOnTopLayerAndCameras above - kept only because its diagnostic
        /// logging (shader name / hasZTest / renderQueue per graphic) already did its job
        /// confirming the shader is stock "UI/Default" with no _ZTest property, and the fix itself
        /// is fully gated behind a live HasProperty check so it's a genuine no-op now, not a risk.
        /// </summary>
        private void ForceMinimapAndVitalsAlwaysOnTop()
        {
            if (Time.time - _lastUiAlwaysOnTopTime < 0.5f) return;
            _lastUiAlwaysOnTopTime = Time.time;

            // FIXED: was resolving "PlayerVitalsUiWorldSpace" - confirmed via the HudScan
            // structural dump that the REAL, visible vitals ring (Health/Energy/Vitality/Rested/
            // Fullness/Hydration/Armour bars) actually lives at ToggleableHUD/PlayerVitalsUi, a
            // sibling of the message system below - PlayerVitalsUiWorldSpace is something else
            // entirely (never confirmed what), meaning this whole fix had been targeting the
            // wrong object since it was first written. Also resolving MessageList here now -
            // confirmed via the same HudScan dump as the real home of "you are hungry"/"can't
            // carry any more"/etc. (its ACTIVATED/deactivated pattern for CantCarryAnyMore(Clone)
            // matched exactly when the user tried picking up an item they were already full on).
            if (_playerVitalsUiTransf == null && _screenSpaceCanvas != null)
            {
                _playerVitalsUiTransf = _screenSpaceCanvas.transform.Find("ToggleableHUD/PlayerVitalsUi");
            }
            if (_messageListTransf == null && _screenSpaceCanvas != null)
            {
                _messageListTransf = _screenSpaceCanvas.transform.Find("ToggleableHUD/MessageList");
            }

            // NEW: safe, read-only - before attempting to reposition MessageList (a different
            // architecture than minimap: it's anchored WITHIN the shared HUD canvas via 2D
            // RectTransform anchoring, not a freely-positioned WorldSpace Transform of its own),
            // logging its actual current layout once so any repositioning fix is based on real
            // data rather than another guess. Also checking for a LayoutGroup, since one of those
            // fighting manual position changes has been the exact cause of wasted rounds
            // elsewhere in this project before (the interaction-prompt-icon saga).
            if (_messageListTransf != null && !_messageListDiagLogged)
            {
                _messageListDiagLogged = true;
                var rt = _messageListTransf.GetComponent<RectTransform>();
                if (rt != null)
                {
                    MelonLogger.Msg($"[VRPlayer][MessageListDiag] anchoredPosition={rt.anchoredPosition} anchorMin={rt.anchorMin} anchorMax={rt.anchorMax} pivot={rt.pivot} sizeDelta={rt.sizeDelta}");
                }
                var layoutGroup = _messageListTransf.GetComponent<LayoutGroup>();
                MelonLogger.Msg($"[VRPlayer][MessageListDiag] LayoutGroup on MessageList itself: {(layoutGroup != null ? layoutGroup.GetType().Name : "none")}");
                var parentLayoutGroup = _messageListTransf.parent != null ? _messageListTransf.parent.GetComponent<LayoutGroup>() : null;
                MelonLogger.Msg($"[VRPlayer][MessageListDiag] LayoutGroup on MessageList's parent: {(parentLayoutGroup != null ? parentLayoutGroup.GetType().Name : "none")}");
            }

            // NEW: user confirmed the minimap layer fix worked - separately revealed that vitals
            // (Strength/Hunger/Armor/Health) is a genuinely SEPARATE system that was never actually
            // repositioned by any of this saga's fixes (only the minimap got a dedicated
            // position/layer fix; vitals only ever got the confirmed-no-op ZTest attempt). Same
            // safe, read-only, one-shot diagnostic as MessageList before attempting anything -
            // vitals lives inside the shared HUD canvas via 2D RectTransform anchoring, not a
            // standalone WorldSpace Transform like minimap, so the exact same fix won't directly
            // transfer without knowing its real layout first.
            if (_playerVitalsUiTransf != null && !_playerVitalsUiDiagLogged)
            {
                _playerVitalsUiDiagLogged = true;
                var rt = _playerVitalsUiTransf.GetComponent<RectTransform>();
                if (rt != null)
                {
                    MelonLogger.Msg($"[VRPlayer][VitalsUiDiag] anchoredPosition={rt.anchoredPosition} anchorMin={rt.anchorMin} anchorMax={rt.anchorMax} pivot={rt.pivot} sizeDelta={rt.sizeDelta}");
                }
                var layoutGroup = _playerVitalsUiTransf.GetComponent<LayoutGroup>();
                MelonLogger.Msg($"[VRPlayer][VitalsUiDiag] LayoutGroup on PlayerVitalsUi itself: {(layoutGroup != null ? layoutGroup.GetType().Name : "none")}");
                var parentLayoutGroup = _playerVitalsUiTransf.parent != null ? _playerVitalsUiTransf.parent.GetComponent<LayoutGroup>() : null;
                MelonLogger.Msg($"[VRPlayer][VitalsUiDiag] LayoutGroup on PlayerVitalsUi's parent: {(parentLayoutGroup != null ? parentLayoutGroup.GetType().Name : "none")}");
                MelonLogger.Msg($"[VRPlayer][VitalsUiDiag] layer={_playerVitalsUiTransf.gameObject.layer} ('{LayerMask.LayerToName(_playerVitalsUiTransf.gameObject.layer)}')");
            }

            // FIX: no LayoutGroup on this object or its parent (confirmed via the diagnostic
            // above) and it's already correctly on the "UI" layer, unlike minimap's canvas which
            // needed a layer correction - so this should be a clean, direct move with nothing to
            // fight. Anchored to the bottom-right corner (anchorMin=anchorMax=pivot=(1,0)) with
            // anchoredPosition=(0,0), i.e. sitting exactly at that corner - increasing the Y
            // component moves it up, same "dodge the held-item danger zone below eye level"
            // philosophy already confirmed working for the minimap. Reasserted every poll (cheap,
            // and protects against any non-LayoutGroup native logic - e.g. a damage-taken pulse
            // animation - that might still touch this RectTransform, even though none was found).
            // Vitals positioning MOVED to a dedicated end-of-frame coroutine below - see
            // EnsureVitalsEndOfFrameCoroutine/UpdateVitalsPositionEndOfFrame for why.

            if (_PlayerMiniMapTransf != null) ForceGraphicsAlwaysOnTop(_PlayerMiniMapTransf, "Minimap");
            if (_playerVitalsUiTransf != null) ForceGraphicsAlwaysOnTop(_playerVitalsUiTransf, "Vitals");
            if (_messageListTransf != null) ForceGraphicsAlwaysOnTop(_messageListTransf, "Messages");
        }

        // FLIPPED per the minimap result: "down" turned out to be the fix there, not "up" as
        // originally assumed - the held-item-below-eye-level premise was backward. Vitals was
        // never actually tested in either direction, so applying the same corrected logic here
        // rather than keeping the untested "up" guess. Units are canvas-space, NOT the same scale
        // as minimap's meters-based offset - this magnitude is a starting guess, not a translation.
        // FIXED: the canvas these live in scales by 0.35f/Screen.height (confirmed via
        // ConvertCanvasToWorldSpace) - a "-200" canvas-unit offset only translated to roughly
        // 6.5cm of real-world movement, genuinely small enough to look like nothing happened,
        // versus minimap's own successful ~0.15-0.20 METERS. Now expressed directly in meters,
        // matching minimap's own scale, and converted to the correct canvas-unit magnitude at
        // runtime using the actual live scale factor rather than guessing a raw number again.
        // TEMP extreme test: pushed to 2.0m (over 10x the previous 0.18f guess) to settle the
        // "is this just too small to see" question definitively, same "rule it in or out cleanly"
        // approach already used successfully elsewhere in this project. If this is still
        // invisible, magnitude is conclusively ruled out too and something else entirely is going
        // on; if it's now clearly visible (even absurdly far/wrong-looking), we know it's purely a
        // magnitude tuning problem and can dial back to something reasonable from here.
        // CONFIRMED via head-relative distance diagnostic: the fix mechanism itself has been
        // working correctly all along - the 2.0m extreme test value genuinely was being applied,
        // but 2 meters straight up is roughly directly above the head, entirely outside normal
        // field of view. Dialing back to a HUD-appropriate value matching minimap's own proven
        // working range (0.15-0.20f).
        public static float VitalsUiOffsetUpMeters = 0.18f;
        public static float VitalsUiOffsetRightMeters = 0f; // anchor point is the bottom-right corner (1,0), so NEGATIVE moves it left/toward center, POSITIVE pushes it further right/off-canvas - opposite convention from minimap's "positive=right"

        private bool _messageListDiagLogged = false;
        private bool _playerVitalsUiDiagLogged = false;

        private void ForceGraphicsAlwaysOnTop(Transform root, string label)
        {
            foreach (var g in root.GetComponentsInChildren<Graphic>(true))
            {
                if (g == null || g.material == null || g.material.shader == null) continue;

                bool hasZTest = g.material.HasProperty("_ZTest");
                if (hasZTest)
                {
                    g.material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                }

                if (DebugLogCanvasChanges)
                {
                    MelonLogger.Msg($"[VRPlayer][UiAlwaysOnTopDiag] [{label}] {g.name}: shader={g.material.shader.name} hasZTest={hasZTest} renderQueue={g.material.renderQueue}");
                }
            }
        }

        /// <summary>
        /// User's own theory, worth testing directly: what if the physical Xbox A press, while
        /// OpenVR has the controller claimed for VR tracking, simply never reaches Unity's
        /// classic Input System at all - the layer IntroLaptopController.CheckInput() almost
        /// certainly reads from - completely separate from CutsceneTrigger/DynamicInputIcon/
        /// gaze, all of which are now confirmed downstream/unrelated per the flat-mode debug
        /// tool investigation (CutsceneTrigger only activates AFTER a successful close, for the
        /// arm-tattoo reveal - it never gates the close itself). User also confirmed the A icon
        /// only ever shows on the laptop's 3rd/last page and isn't gaze-driven at all, ruling out
        /// any remaining look-at theory.
        ///
        private Type _introLaptopControllerTypeForOnLastPage;
        private bool _introLaptopControllerTypeForOnLastPageSearched = false;

        /// <summary>
        /// Factored-out "are we on the laptop's last page" check, reused by the new
        /// camera-desync diagnostic below without duplicating the reflection dance a third time.
        /// </summary>
        private bool IsOnLaptopLastPage()
        {
            GameObject laptop = GameObject.Find("LaptopB");
            if (laptop == null) return false;

            Component introLaptopComp = null;
            foreach (var t in laptop.GetComponentsInChildren<Transform>(true))
            {
                foreach (var comp in t.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    try
                    {
                        IntPtr classPtr = IL2CPP.il2cpp_object_get_class(comp.Pointer);
                        string nativeType = classPtr != IntPtr.Zero ? IL2CPP.il2cpp_class_get_name_(classPtr) : "";
                        if (nativeType == "IntroLaptopController") { introLaptopComp = comp; break; }
                    }
                    catch { }
                }
                if (introLaptopComp != null) break;
            }
            if (introLaptopComp == null) return false;

            if (!_introLaptopControllerTypeForOnLastPageSearched)
            {
                _introLaptopControllerTypeForOnLastPageSearched = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { _introLaptopControllerTypeForOnLastPage = Array.Find(asm.GetTypes(), x => x.Name == "IntroLaptopController"); }
                    catch { continue; }
                    if (_introLaptopControllerTypeForOnLastPage != null) break;
                }
            }
            if (_introLaptopControllerTypeForOnLastPage == null) return false;

            try
            {
                var castMethod = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("Cast")?.MakeGenericMethod(_introLaptopControllerTypeForOnLastPage);
                var castResult = castMethod?.Invoke(introLaptopComp, null);
                if (castResult == null) return false;

                var currentScreenProp = _introLaptopControllerTypeForOnLastPage.GetProperty("_currentScreen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var laptopScreensProp = _introLaptopControllerTypeForOnLastPage.GetProperty("_laptopScreens", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (currentScreenProp == null || laptopScreensProp == null) return false;

                int currentScreen = (int)currentScreenProp.GetValue(castResult);
                var screensListObj = laptopScreensProp.GetValue(castResult);
                int screenCount = -1;
                if (screensListObj != null)
                {
                    var countProp = screensListObj.GetType().GetProperty("Count");
                    if (countProp != null) screenCount = (int)countProp.GetValue(screensListObj);
                }

                return screenCount > 0 && currentScreen >= screenCount - 1;
            }
            catch
            {
                return false;
            }
        }

        public static bool NudgeCutsceneCameraDownEnabled = true; // CONFIRMED WORKING FIX for A-close (root cause: MainCameraCustcene, the camera the game's own gaze check reads from, was never wired to track real VR head movement) - a small ONE-TIME local rotation nudge applied the moment the laptop's last page is reached, no position changes, no continuous fight against whatever legitimate animation drives this camera
        public static float NudgeCutsceneCameraDownDegrees = 20f; // tunable - confirmed working at this value
        public static float NudgeCutsceneCameraLeftDegrees = 10f; // tunable - confirmed working at this value; positive value nudges left (applied as a negative local Y rotation internally)

        private Transform _cutsceneCameraTransform;
        private bool _cutsceneCameraNudgeApplied = false;

        /// <summary>
        /// CONFIRMED WORKING FIX for the long-running A-close saga: root cause was
        /// MainCameraCustcene (the camera the game's own gaze/interact check for the close prompt
        /// reads from) never being wired to track real VR head movement - confirmed via log (its
        /// rotation stayed essentially fixed while the real tracked head swept far more broadly),
        /// and independently corroborated by the user's own observation that the flat-monitor
        /// mirror shows a static, non-head-tracking view throughout this exact scene. Every prior
        /// input-layer fix (gamepad-vs-keyboard detection, a disabled Collider, raw synthetic
        /// button presses) was addressing symptoms downstream of this gate, which is why none of
        /// them worked - the gaze check itself never passed in the first place.
        ///
        /// An earlier, more aggressive fix attempt (continuously overwriting this camera's full
        /// position+rotation every frame to match the real head) had to be reverted after it
        /// broke the intro's outro camera transition outright (user got disconnected from their
        /// body, stuck floating in the sky watching the helicopter fly away) - that fix fought a
        /// legitimate scripted animation on this same camera every single frame. This version is
        /// safer on every axis: no position changes at all, no dependency on the real head's live
        /// (constantly moving) position/rotation - just a small fixed local tilt - and applied
        /// exactly ONCE per laptop visit rather than continuously, so the camera's own normal
        /// behavior (including that same legitimate animation) continues undisturbed afterward.
        /// </summary>
        private void NudgeCutsceneCameraDown()
        {
            if (!NudgeCutsceneCameraDownEnabled) return;
            if (_cutsceneCameraNudgeApplied) return;

            if (_cutsceneCameraTransform == null)
            {
                Camera[] allCameras = UnityEngine.Object.FindObjectsOfType<Camera>();
                foreach (var cam in allCameras)
                {
                    if (cam != null && cam.name == "MainCameraCustcene")
                    {
                        _cutsceneCameraTransform = cam.transform;
                        break;
                    }
                }
                if (_cutsceneCameraTransform == null) return; // not created yet - retry every poll until it is
                MelonLogger.Msg("[VRPlayer][CutsceneCameraNudge] Found MainCameraCustcene.");
            }

            if (!IsOnLaptopLastPage()) return;

            _cutsceneCameraNudgeApplied = true; // exactly once per laptop visit - never re-applied, never reset to fight anything ongoing
            _cutsceneCameraTransform.Rotate(NudgeCutsceneCameraDownDegrees, -NudgeCutsceneCameraLeftDegrees, 0f, Space.Self);
            MelonLogger.Msg($"[VRPlayer][CutsceneCameraNudge] Applied a one-time {NudgeCutsceneCameraDownDegrees} degree local pitch-down + {NudgeCutsceneCameraLeftDegrees} degree local yaw-left nudge to MainCameraCustcene.");
        }


        public static bool PauseMenuSignalDiagEnabled = false; // DISABLED - confirmed via testing what we needed: camera name and cursor visibility/lock state never change when the pause menu opens (all ruled out), while VRPlayer's own blur-suppression detection (IsPauseMenuOpenConfirmed) correctly fires in both single-player and multiplayer. Left in place in case another candidate signal is worth checking later.
        private string _lastLoggedCameraName = "";
        private bool _lastLoggedCursorVisible = false;
        private UnityEngine.CursorLockMode _lastLoggedCursorLockState = UnityEngine.CursorLockMode.None;

        private void DiagPauseMenuSignals()
        {
            if (!PauseMenuSignalDiagEnabled) return;
            if (UsedCamera == null) return;

            string camName = UsedCamera.name;
            bool cursorVisible = UnityEngine.Cursor.visible;
            UnityEngine.CursorLockMode cursorLock = UnityEngine.Cursor.lockState;

            if (camName != _lastLoggedCameraName || cursorVisible != _lastLoggedCursorVisible || cursorLock != _lastLoggedCursorLockState)
            {
                MelonLogger.Msg($"[VRPlayer][PauseMenuSignalDiag] camera='{camName}' cursorVisible={cursorVisible} cursorLockState={cursorLock} Time.timeScale={Time.timeScale}");
                _lastLoggedCameraName = camName;
                _lastLoggedCursorVisible = cursorVisible;
                _lastLoggedCursorLockState = cursorLock;
            }
        }

        public static bool NeutralizeNativeCameraEnabled = true; // RE-ENABLED, this time as the confirmed, final choice - the DynamicResolutionHandler path is now genuinely exhausted: fixed the Il2CppSystem.Nullable<T> bug, confirmed UpdateAndUseCamera succeeds cleanly every single poll with zero errors, and it STILL produces no FPS or visual change at all (per user's own test). Same definitive "write sticks, HDRP's actual renderer never acts on it" pattern already seen once with EyeDisableCameraEffects - likely because dynamic resolution support itself is a render-pipeline-asset/build-time decision this game's own HDRP asset never had enabled, which no runtime API call can retroactively turn on. This flag remains the only confirmed-effective option for a real FPS win, at the cost of the known shadow-darkening/occlusion bug.

        public static bool RedirectNativeCameraTargetEnabled = false; // DISABLED AGAIN - confirmed via v0.6.1 source that this is the actual root cause of the original broken-selection report: it corrupts the screen-coordinate math the game's own native mouse-position selection depends on, for both the grab bag and (likely) the booklet. UpdateGrabBagGazeSelection (which didn't depend on this) has been removed from the update loop in favor of restoring that original, working mechanism - so this needs to go back off. The ~4fps this gave isn't worth broken selection. Would need a genuinely different technique (redirect output without changing reported resolution) to ever safely combine the two.
        public static int RedirectedNativeCameraTargetSize = 64;
        private RenderTexture _redirectedNativeCameraTarget;
        private Camera _redirectedCamera;
        private float _savedNativeCameraDepth;

        // NEW ATTEMPT (per user's request, after the above two were confirmed to each have their
        // own separate, unrelated bug - shadows for the culling-mask approach, broken selection
        // for the tiny-render-target approach). HDRP's DynamicResolutionHandler maintains a
        // SEPARATE handler instance per camera (confirmed via reflection dump of its own members:
        // GetOrCreateDrsInstanceHandler(Camera), s_CameraInstances) - GlobalDynamicResolutionSettings
        // has an explicit forceResolution/forcedPercentage pair, letting one specific camera's own
        // internal render resolution be forced down directly, without touching its culling mask
        // (the shadow bug's cause) or its own reported pixelWidth/pixelHeight/targetTexture (the
        // selection bug's cause) at all - dynamic resolution is specifically designed to keep the
        // camera's own external/reported resolution unchanged while shrinking only the internal
        // render buffer. Confirmed via the same diagnostic dump that the GLOBAL scale sits at 1.0
        // (no scaling happening anywhere at all right now) - this forces just the native camera's
        // own, separate handler, leaving the eye cameras and everything else untouched.
        public static bool ForceNativeCameraLowResEnabled = false; // DISABLED - confirmed dead end, not just unproven anymore. The Il2CppSystem.Nullable<T> bug was fixed, UpdateAndUseCamera succeeds cleanly every single poll with zero errors, and it still produces no FPS or visual change at all. See NeutralizeNativeCameraEnabled's own comment for the full reasoning.
        public static float ForceNativeCameraLowResPercentage = 30f;
        private bool _nativeCameraLowResWarnedOnce = false;
        private float _lastNativeCameraLowResConfirmTime = 0f;
        private Type _dynResHandlerType;
        private Type _globalDynResSettingsType;
        private MethodInfo _updateAndUseCameraMethod;
        private Type _il2CppNullableOpenType;

        /// <summary>
        /// Forces the native (non-VR, never actually seen) camera's own dynamic-resolution handler
        /// down to a low, fixed percentage.
        ///
        /// REVISED (confirmed via user report: the previous one-time ProcessSettings approach
        /// succeeded at every reflection step, across multiple different cameras, with zero
        /// warnings or errors - yet produced no visual or FPS change at all. Same "write sticks at
        /// the C# level, HDRP's actual renderer never acts on it" pattern already hit once before
        /// in this project with EyeDisableCameraEffects's FrameSettings overrides). Per-camera
        /// dynamic resolution fields like s_ActiveCameraId/s_ActiveInstance in the earlier member
        /// dump suggest this is a "which camera is active THIS FRAME" concept, not a permanent,
        /// set-once setting - UpdateAndUseCamera's own name suggests it's the method that actually
        /// activates a camera's handler for the current render, separate from just configuring it.
        /// Calling this every poll instead of once, right before this camera would render.
        /// </summary>
        private void ForceNativeCameraLowRes()
        {
            if (!ForceNativeCameraLowResEnabled) return;
            if (UsedCamera == null) return;
            if (_nativeCameraLowResWarnedOnce) return;

            try
            {
                if (_dynResHandlerType == null)
                {
                    _dynResHandlerType = FindTypeAcrossLoadedAssembliesShared("UnityEngine.Rendering.DynamicResolutionHandler")
                        ?? FindTypeAcrossLoadedAssembliesShared("UnityEngine.Rendering.HighDefinition.DynamicResolutionHandler");
                }
                if (_globalDynResSettingsType == null)
                {
                    _globalDynResSettingsType = FindTypeAcrossLoadedAssembliesShared("UnityEngine.Rendering.GlobalDynamicResolutionSettings")
                        ?? FindTypeAcrossLoadedAssembliesShared("UnityEngine.Rendering.HighDefinition.GlobalDynamicResolutionSettings");
                }
                if (_dynResHandlerType == null || _globalDynResSettingsType == null)
                {
                    _nativeCameraLowResWarnedOnce = true;
                    MelonLogger.Warning("[VRPlayer][ForceNativeCameraLowRes] DynamicResolutionHandler or GlobalDynamicResolutionSettings type not found - HDRP API may differ on this version.");
                    return;
                }

                if (_updateAndUseCameraMethod == null)
                {
                    _updateAndUseCameraMethod = _dynResHandlerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "UpdateAndUseCamera");
                }
                if (_updateAndUseCameraMethod == null)
                {
                    _nativeCameraLowResWarnedOnce = true;
                    MelonLogger.Warning("[VRPlayer][ForceNativeCameraLowRes] UpdateAndUseCamera method not found.");
                    return;
                }

                object settings = Activator.CreateInstance(_globalDynResSettingsType);
                _globalDynResSettingsType.GetField("enabled").SetValue(settings, true);
                _globalDynResSettingsType.GetField("forceResolution").SetValue(settings, true);
                _globalDynResSettingsType.GetField("forcedPercentage").SetValue(settings, ForceNativeCameraLowResPercentage);
                _globalDynResSettingsType.GetField("minPercentage").SetValue(settings, ForceNativeCameraLowResPercentage);
                _globalDynResSettingsType.GetField("maxPercentage").SetValue(settings, ForceNativeCameraLowResPercentage);

                // UpdateAndUseCamera(Camera, GlobalDynamicResolutionSettings?, Action) - the
                // second parameter is a Nullable<GlobalDynamicResolutionSettings>.
                //
                // FIXED (confirmed via log: "Object of type 'GlobalDynamicResolutionSettings'
                // cannot be converted to type 'Il2CppSystem.Nullable`1[...]'" - the method expects
                // IL2CPP's OWN Nullable<T> type, not the standard .NET System.Nullable<T> this
                // was constructing. Finding and using Il2CppSystem.Nullable<T> instead, same
                // reflection approach as the other IL2CPP-specific types found above.
                if (_il2CppNullableOpenType == null)
                {
                    _il2CppNullableOpenType = FindTypeAcrossLoadedAssembliesShared("Il2CppSystem.Nullable`1");
                }
                if (_il2CppNullableOpenType == null)
                {
                    _nativeCameraLowResWarnedOnce = true;
                    MelonLogger.Warning("[VRPlayer][ForceNativeCameraLowRes] Il2CppSystem.Nullable`1 type not found.");
                    return;
                }
                Type nullableSettingsType = _il2CppNullableOpenType.MakeGenericType(_globalDynResSettingsType);
                object nullableSettings = Activator.CreateInstance(nullableSettingsType, settings);
                _updateAndUseCameraMethod.Invoke(null, new object[] { UsedCamera, nullableSettings, null });

                if (Time.time - _lastNativeCameraLowResConfirmTime > 5f)
                {
                    _lastNativeCameraLowResConfirmTime = Time.time;
                    MelonLogger.Msg($"[VRPlayer][ForceNativeCameraLowRes] UpdateAndUseCamera succeeded for '{UsedCamera.name}' at {ForceNativeCameraLowResPercentage}%.");
                }

                // Also make sure this specific camera is actually opted in to dynamic resolution
                // at all - same allowDynamicResolution flag used for the eye cameras, applied here
                // to the native camera instead. Kept one-time-per-camera via StereoRender's own
                // cached component lookups, cheap to call repeatedly regardless.
                StereoRender.TryConfigureHdrpCameraSettingsPublic(UsedCamera);
            }
            catch (Exception e)
            {
                _nativeCameraLowResWarnedOnce = true;
                MelonLogger.Warning($"[VRPlayer][ForceNativeCameraLowRes] Failed: {e.Message}");
            }
        }

        private static Type FindTypeAcrossLoadedAssembliesShared(string fullTypeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = assembly.GetType(fullTypeName);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Only meaningful alongside StereoRender.DesktopMirrorBlitModeEnabled - with that off,
        /// the native camera's own screen output IS the mirror, so redirecting it away here would
        /// just produce a black monitor, the same failure mode neutering the culling mask caused
        /// long before the mirror-blit system existed.
        ///
        /// FIXED before this ever shipped (found via the other dev's own documented, hard-won
        /// lesson, not from a bug report): a camera pointed at a small target texture resizes
        /// render targets HDRP shares across ALL cameras that frame - if that camera renders
        /// LAST, the next frame's eye cameras can start drawing at that same small, leftover
        /// scale, before anything resets it back. Their own words: "a black eye with the sun and
        /// its flare still painted over it." Their fix, ported directly: force this camera to
        /// render FIRST (lower depth than both eyes), so the eye cameras always render after it
        /// and correctly re-establish the shared targets at the right size before the frame ends.
        /// </summary>
        private void RedirectNativeCameraFromScreen()
        {
            if (!RedirectNativeCameraTargetEnabled || !StereoRender.DesktopMirrorBlitModeEnabled) return;
            if (UsedCamera == null) return;
            if (StereoRender == null || StereoRender.LeftCam == null || StereoRender.RightCam == null) return;

            if (_redirectedCamera != UsedCamera)
            {
                // UsedCamera changed - restore whatever we previously touched before adopting the
                // new one, same reasoning as NeutralizeNativeCamera above.
                if (_redirectedCamera != null)
                {
                    _redirectedCamera.targetTexture = null;
                    _redirectedCamera.depth = _savedNativeCameraDepth;
                }
                _redirectedCamera = UsedCamera;
                _savedNativeCameraDepth = UsedCamera.depth;
            }

            if (_redirectedNativeCameraTarget == null || _redirectedNativeCameraTarget.width != RedirectedNativeCameraTargetSize)
            {
                if (_redirectedNativeCameraTarget != null) _redirectedNativeCameraTarget.Release();
                _redirectedNativeCameraTarget = new RenderTexture(RedirectedNativeCameraTargetSize, RedirectedNativeCameraTargetSize, 24)
                {
                    name = "SonsVR_RedirectedNativeCameraTarget"
                };
                _redirectedNativeCameraTarget.Create();
            }

            if (_redirectedCamera.targetTexture != _redirectedNativeCameraTarget)
            {
                _redirectedCamera.targetTexture = _redirectedNativeCameraTarget;
            }

            // Render first, so both eye cameras (depth 0) always run after this one and reset
            // HDRP's shared render targets back to the correct size before the frame ends.
            float firstDepth = Mathf.Min(StereoRender.LeftCam.depth, StereoRender.RightCam.depth) - 100f;
            if (!Mathf.Approximately(_redirectedCamera.depth, firstDepth))
            {
                _redirectedCamera.depth = firstDepth;
            }
        }

        private Camera _neutralizedNativeCamera;
        private int _savedNativeCameraCullingMask;
        private CameraType _savedNativeCameraType;
        private bool _neutralizeCameraTypeWarnedOnce = false;

        // ATTEMPT (per user's request to fix the shadow-darkening bug while keeping this
        // optimization): the working theory is that HDRP's shadow/lighting logic keys off
        // something tied to which camera is "active" - and a culling mask of exactly 0 may be
        // treated as a special "invalid/no camera" case internally, rather than just "empty set
        // of layers". A near-empty mask (one genuinely unused, high-numbered layer) still cuts
        // almost all render work while potentially avoiding whatever that special-case behavior
        // is. Layer 31 is the highest available Unity layer index, least likely to be used by the
        // game's own content.
        public static int NeutralizeNativeCameraCullingMask = 1 << 31;

        // SECOND ATTEMPT, tried alongside the above in the same build: HDRP treats CameraType.Game
        // cameras as the "real" ones contributing to main scene state (shadows, lighting) -
        // switching this camera to a non-Game type might exclude it from those decisions
        // entirely, independent of its culling mask. Preview is the least likely to have other
        // side effects (unlike SceneView/Reflection/VR, which have their own special handling).
        public static bool NeutralizeNativeCameraChangeCameraType = false; // CONFIRMED FINAL: the non-zero culling mask alone fixes shadows AND keeps the vitals ring HUD intact - this cameraType change broke the vitals ring (confirmed via isolation test) and isn't needed for the shadow fix at all, just costing a small ~2-3fps it apparently also contributed on top of the culling mask's own gain. Left off; the small extra fps isn't worth the HUD regression.
        public static CameraType NeutralizeNativeCameraTargetType = CameraType.Preview;

        // ---------------------------------------------------------------------
        // ZER0INT V114 PRE-GAME RENDER SUPPRESSION
        // ---------------------------------------------------------------------
        // V107 discovered the huge startup win: do not let both full-resolution VR eyes render
        // the partially-streamed world while SonsMainLoading/BlankScene is active. V107 copied
        // the native loading camera's layer-5 mask (0x20), which made loading much faster but still
        // intentionally rendered the game's own layer-5 sky/backing. Database archaeology in V114
        // found explicit LoadingCameraBacking geometry in the loading assets, and the user does
        // not need ANY native loading scenery in VR. We therefore reserve the same high layer 31
        // already proven by the native-camera optimization as effectively unused game content,
        // put ONLY our tiny loading label there, and force both eyes to constant black / no HDRP
        // volume influence until the real gameplay gate is ready.
        //
        // IMPORTANT: this does NOT change NeutralizeNativeCameraCullingMask. The original ~7-10fps
        // native desktop-camera optimization still uses layer 31 exactly as before. During loading,
        // that native camera may also see our one Text canvas; that cost is negligible, and once
        // gameplay starts the loading canvas is inactive.
        // V146: production loading COVER lifecycle, independent of the AER readiness flag.
        // Native scene names can move to SonsMain while the player is still under terrain;
        // and the AER flag is not guaranteed to reset on a second Load/New Game in one process.
        // This only controls the V114 eye mask and loading overlay, not AER scheduling.
        private bool _v146LoadingEpochActive = false;
        private bool _v146LoadingScenePreviously = false;
        private float _v146LoadingEpochStart = -1f;
        private float _v146NativeGameplayStableSince = -1f;
        private float _v146NextSpawnProbe = 0f;
        private bool _v146SpawnVerified = false;
        private bool _v146SpawnHasGround = false;
        private CharacterController _v146NativeCharacterController;
        private bool _v146SpawnHasCeilingWithoutGround = false;
        private bool _v146LastCutsceneSignal = false;
        private float _v146LastTransitionLog = -100f;

        // V155: NEW GAME intro reveal is camera/rig-based, never terrain/physics-based.
        // Once a verified cutscene is revealed, keep its world render enabled through
        // transient prompt/camera changes. Only the gameplay-spawn gate ends the epoch.
        private bool _v155IntroLatched = false;
        private float _v155IntroMatchedSince = -1f;
        private float _v155IntroLastDiag = -10f;
        private const float V155IntroCameraStableSeconds = 0.25f;
        private const float V155IntroMaxEyeCameraSeparation = 12.0f;
        // Ordinary Load Game often never sets CharacterController.isGrounded on the
        // cached object, despite a fully interactive player (V151 12-second fallback).
        private bool _v154FloorNearAvatar = false;
        private int _v154StableFloorProbes = 0;
        private Vector3 _v154PreviousAvatarPosition;
        private bool _v154HavePreviousAvatarPosition = false;
        private float _v154ObservedAvatarTravel = 0f;
        private bool _v154PlayerLocomotionSeen = false;
        private bool _v154PlayerControllerActive = false;
        private const float V154MinWorldStableSeconds = 1.25f;

        private void BeginPreGameplayLoadingEpochV146(string reason)
        {
            if (_v146LoadingEpochActive) return;
            _v170MultiplayerSpawnConfirmed = false;
            _v146LoadingEpochActive = true;
            _v146LoadingEpochStart = Time.realtimeSinceStartup;
            _v146NativeGameplayStableSince = -1f;
            _v146NextSpawnProbe = 0f;
            _v146SpawnVerified = false;
            _v146SpawnHasGround = false;
            _v146NativeCharacterController = null;
            _v146SpawnHasCeilingWithoutGround = false;
            _v146LastCutsceneSignal = false;
            _v155IntroLatched = false;
            _v155IntroMatchedSince = -1f;
            _v155IntroLastDiag = -10f;
            _v154FloorNearAvatar = false;
            _v154StableFloorProbes = 0;
            _v154HavePreviousAvatarPosition = false;
            _v154ObservedAvatarTravel = 0f;
            _v154PlayerLocomotionSeen = false;
            _v154PlayerControllerActive = false;
            MelonLogger.Warning("[VRPlayer][V146 LoadingCover] BEGIN " + reason +
                "; keeping layer-31 cover through Unity scene-name changes until spawn is verified.");
        }

        private void ClearPreGameplayLoadingEpochV146(string reason)
        {
            // V170: clear even if the epoch was already released; title scene
            // notifications otherwise leave a stale multiplayer-ready latch.
            _v170MultiplayerSpawnConfirmed = false;
            if (!_v146LoadingEpochActive) return;
            _v146LoadingEpochActive = false;
            _v146NativeGameplayStableSince = -1f;
            _v146SpawnVerified = false;
            _v155IntroLatched = false;
            _v155IntroMatchedSince = -1f;
            _v154HavePreviousAvatarPosition = false;
            MelonLogger.Msg("[VRPlayer][V146 LoadingCover] CLEAR " + reason);
        }

        private bool IsNativeIntroCutsceneActiveV146()
        {
            try
            {
                if (UsedCamera != null &&
                    (UsedCamera.name.IndexOf("cutscene", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     UsedCamera.name.IndexOf("custcene", StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
                if (_screenSpaceCanvas != null)
                {
                    Transform skip = _screenSpaceCanvas.transform.Find("CutscenePrompts/SkipCutscene");
                    if (skip != null && skip.gameObject.activeInHierarchy) return true;
                }
            }
            catch { }
            return false;
        }

        // V155: a skip prompt can become active several seconds before VRSystem actually
        // adopts the game's cinematic camera. Do not unmask the transitional loading view
        // based on a prompt or a timer. Wait for the real tracked stereo eyes to be near
        // the active native cutscene camera, then require a brief stable camera handoff.
        // Never use a floor/water/ceiling raycast here: those cannot distinguish a loading
        // backing quad or a scripted helicopter from an under-map spawn.
        private bool IsIntroViewReadyV155(bool rawCutscene)
        {
            if (_v155IntroLatched) return true; // One-way latch until gameplay/title epoch ends.
            if (!rawCutscene) { _v155IntroMatchedSince = -1f; return false; }

            float now = Time.realtimeSinceStartup;
            string cameraName = UsedCamera != null ? UsedCamera.name : "null";
            bool nativeCutsceneCamera = UsedCamera != null &&
                (cameraName.IndexOf("cutscene", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 cameraName.IndexOf("custcene", StringComparison.OrdinalIgnoreCase) >= 0);
            bool haveEyes = StereoRender != null && StereoRender.LeftCam != null &&
                StereoRender.RightCam != null;
            float separation = float.PositiveInfinity;
            if (nativeCutsceneCamera && haveEyes)
            {
                Vector3 eyeMid = (StereoRender.LeftCam.transform.position +
                    StereoRender.RightCam.transform.position) * 0.5f;
                separation = Vector3.Distance(eyeMid, UsedCamera.transform.position);
            }
            bool matched = nativeCutsceneCamera && haveEyes &&
                separation <= V155IntroMaxEyeCameraSeparation;
            if (!matched)
            {
                _v155IntroMatchedSince = -1f;
                if (now - _v155IntroLastDiag >= 2f)
                {
                    _v155IntroLastDiag = now;
                    MelonLogger.Msg("[VRPlayer][V155 IntroGate] WAIT camera=" + cameraName +
                        " nativeCutscene=" + nativeCutsceneCamera +
                        " eyes=" + haveEyes + " eyeToCamera=" +
                        (float.IsInfinity(separation) ? "unknown" : separation.ToString("F2")) +
                        "m; never release on SkipCutscene prompt alone");
                }
                return false;
            }
            if (_v155IntroMatchedSince < 0f) _v155IntroMatchedSince = now;
            if (now - _v155IntroMatchedSince < V155IntroCameraStableSeconds) return false;

            _v155IntroLatched = true;
            MelonLogger.Warning("[VRPlayer][V155 IntroGate] CINEMATIC_LATCHED camera=" +
                cameraName + " eyeToCamera=" + separation.ToString("F2") +
                "m nativeScene=" + UsedSceneName +
                " elapsed=" + (now - _v146LoadingEpochStart).ToString("F2") +
                "s; keep scene visible through ALL intro transitions until gameplay is ready");
            return true;
        }

        // Only inspect physics while a loading epoch is being exited; NEVER per gameplay frame.
        // A camera with terrain overhead and no support below matches the reported new-game
        // under-map/water screenshot. A native CharacterController grounded state supplies
        // independent positive spawn evidence, without modifying the character or physics.
        private void ProbeNativeSpawnV146()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _v146NextSpawnProbe) return;
            _v146NextSpawnProbe = now + 0.35f;
            _v146SpawnHasGround = false;
            _v146SpawnHasCeilingWithoutGround = false;
            _v154FloorNearAvatar = false;
            _v154PlayerControllerActive = false;
            if (UsedCamera == null || LocalPlayer == null) return;
            try
            {
                if (_v146NativeCharacterController == null)
                {
                    _v146NativeCharacterController = LocalPlayer.GetComponent<CharacterController>();
                    if (_v146NativeCharacterController == null)
                        _v146NativeCharacterController = LocalPlayer.GetComponentInChildren<CharacterController>(true);
                }
                CharacterController cc = _v146NativeCharacterController;
                _v154PlayerControllerActive = cc != null && cc.enabled && cc.gameObject.activeInHierarchy;
                if (_v154PlayerControllerActive && cc.isGrounded)
                    _v146SpawnHasGround = true;
                Vector3 pos = StereoRender != null && StereoRender.LeftCam != null &&
                    StereoRender.RightCam != null
                    ? (StereoRender.LeftCam.transform.position + StereoRender.RightCam.transform.position) * 0.5f
                    : UsedCamera.transform.position;
                // The image showed the sea/terrain ceiling ABOVE the virtual eye while
                // the player had nothing beneath them. Check both directions; rooms/caves
                // normally have a floor hit and will NOT trip this condition.
                RaycastHit hit;
                bool floor = Physics.Raycast(pos + Vector3.up * 0.1f, Vector3.down,
                    out hit, 4.0f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                bool overhead = Physics.Raycast(pos + Vector3.up * 0.1f, Vector3.up,
                    out hit, 22f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                _v146SpawnHasCeilingWithoutGround = overhead && !floor;

                // Read-only native-avatar checks, not the HMD's physical movement.
                // Horizontal native-body travel is evidence the game's locomotion loop
                // has actually started, unlike the early camera/scene handoff.
                Vector3 avatarPos = LocalPlayer.transform.position;
                if (_v154HavePreviousAvatarPosition)
                {
                    Vector3 delta = avatarPos - _v154PreviousAvatarPosition;
                    float horizontal = new Vector2(delta.x, delta.z).magnitude;
                    if (horizontal > 0.005f && horizontal < 1.5f)
                        _v154ObservedAvatarTravel += horizontal;
                    else if (horizontal >= 1.5f)
                        _v154ObservedAvatarTravel = 0f; // loading teleport is not locomotion
                }
                _v154PreviousAvatarPosition = avatarPos;
                _v154HavePreviousAvatarPosition = true;
                if (_v154ObservedAvatarTravel >= 0.18f)
                    _v154PlayerLocomotionSeen = true;

                // Floor support checked near the native avatar as well as VR eyes.
                // No full-scene scans, and don't count the avatar's own collider.
                bool avatarFloor = Physics.Raycast(avatarPos + Vector3.up * 0.15f,
                    Vector3.down, out hit, 4.5f, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);
                if (avatarFloor && hit.collider != null &&
                    hit.collider.transform.IsChildOf(LocalPlayer.transform))
                    avatarFloor = false;
                _v154FloorNearAvatar = (floor || avatarFloor) && !_v146SpawnHasCeilingWithoutGround;
                _v154StableFloorProbes = _v154FloorNearAvatar
                    ? Mathf.Min(_v154StableFloorProbes + 1, 20) : 0;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[VRPlayer][V146 LoadingCover] spawn probe: " + e.Message);
            }
            // A grounded native controller is strong evidence of a completed spawn. If the
            // controller is absent or the player spawned in water, wait longer before a
            // conservative fallback release; avoid permanently blacking out a valid game.
            _v146SpawnVerified = _v146SpawnHasGround && !_v146SpawnHasCeilingWithoutGround;
        }

        private bool _preGameplayEyeSuppressionActiveV107 = false;
        private float _preGameplayEyeSuppressionStartV107 = -1f;
        private const int PreGameplayLoadingLayerV114 = 31;
        private const int PreGameplayLoadingCullingMaskV114 = 1 << PreGameplayLoadingLayerV114;

        // V114d IL2CPP-INJECTION NOTE:
        // VRPlayer is itself registered into IL2CPP. Do NOT put managed-only helper structs,
        // especially `ref SomeManagedStruct`, in any method signature on this class. Il2CppInterop
        // scans even private methods while registering VRPlayer; V114c's
        //   ApplyPreGameplayBlackEyeV114(Camera, ref PreGameplayEyeVisualStateV114)
        // caused ClassInjector.ConvertMethodInfo() to throw before VRPlayer registration completed.
        // Keep the exact same saved state as plain per-eye fields, and make helper signatures use
        // only IL2CPP-safe primitive / UnityEngine reference parameters.
        private bool _preGameplayLeftEyeSavedV114;
        private Camera _preGameplayLeftEyeSavedCameraV114;
        private int _preGameplayLeftEyeCullingMaskV114;
        private CameraClearFlags _preGameplayLeftEyeClearFlagsV114;
        private Color _preGameplayLeftEyeBackgroundColorV114;
        private bool _preGameplayLeftEyeHadHdV114;
        private HDAdditionalCameraData.ClearColorMode _preGameplayLeftEyeHdClearColorModeV114;
        private Color _preGameplayLeftEyeHdBackgroundColorV114;
        private LayerMask _preGameplayLeftEyeHdVolumeLayerMaskV114;

        private bool _preGameplayRightEyeSavedV114;
        private Camera _preGameplayRightEyeSavedCameraV114;
        private int _preGameplayRightEyeCullingMaskV114;
        private CameraClearFlags _preGameplayRightEyeClearFlagsV114;
        private Color _preGameplayRightEyeBackgroundColorV114;
        private bool _preGameplayRightEyeHadHdV114;
        private HDAdditionalCameraData.ClearColorMode _preGameplayRightEyeHdClearColorModeV114;
        private Color _preGameplayRightEyeHdBackgroundColorV114;
        private LayerMask _preGameplayRightEyeHdVolumeLayerMaskV114;

        private void SavePreGameplayEyeStateV114(Camera cam, bool leftEye)
        {
            if (cam == null) return;

            bool alreadySaved = leftEye ? _preGameplayLeftEyeSavedV114 : _preGameplayRightEyeSavedV114;
            Camera savedCamera = leftEye ? _preGameplayLeftEyeSavedCameraV114 : _preGameplayRightEyeSavedCameraV114;
            if (alreadySaved && savedCamera == cam) return;

            bool hadHd = false;
            HDAdditionalCameraData.ClearColorMode hdClearColorMode = default;
            Color hdBackgroundColor = Color.black;
            LayerMask hdVolumeLayerMask = 0;

            try
            {
                HDAdditionalCameraData hd = cam.GetComponent<HDAdditionalCameraData>();
                if (hd != null)
                {
                    hadHd = true;
                    hdClearColorMode = hd.clearColorMode;
                    hdBackgroundColor = hd.backgroundColorHDR;
                    hdVolumeLayerMask = hd.volumeLayerMask;
                }
            }
            catch { hadHd = false; }

            if (leftEye)
            {
                _preGameplayLeftEyeSavedV114 = true;
                _preGameplayLeftEyeSavedCameraV114 = cam;
                _preGameplayLeftEyeCullingMaskV114 = cam.cullingMask;
                _preGameplayLeftEyeClearFlagsV114 = cam.clearFlags;
                _preGameplayLeftEyeBackgroundColorV114 = cam.backgroundColor;
                _preGameplayLeftEyeHadHdV114 = hadHd;
                _preGameplayLeftEyeHdClearColorModeV114 = hdClearColorMode;
                _preGameplayLeftEyeHdBackgroundColorV114 = hdBackgroundColor;
                _preGameplayLeftEyeHdVolumeLayerMaskV114 = hdVolumeLayerMask;
            }
            else
            {
                _preGameplayRightEyeSavedV114 = true;
                _preGameplayRightEyeSavedCameraV114 = cam;
                _preGameplayRightEyeCullingMaskV114 = cam.cullingMask;
                _preGameplayRightEyeClearFlagsV114 = cam.clearFlags;
                _preGameplayRightEyeBackgroundColorV114 = cam.backgroundColor;
                _preGameplayRightEyeHadHdV114 = hadHd;
                _preGameplayRightEyeHdClearColorModeV114 = hdClearColorMode;
                _preGameplayRightEyeHdBackgroundColorV114 = hdBackgroundColor;
                _preGameplayRightEyeHdVolumeLayerMaskV114 = hdVolumeLayerMask;
            }
        }

        private void ApplyPreGameplayBlackEyeV114(Camera cam, bool leftEye)
        {
            if (cam == null) return;

            SavePreGameplayEyeStateV114(cam, leftEye);

            cam.cullingMask = PreGameplayLoadingCullingMaskV114;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;

            try
            {
                HDAdditionalCameraData hd = cam.GetComponent<HDAdditionalCameraData>();
                if (hd != null)
                {
                    hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                    hd.backgroundColorHDR = Color.black;
                    hd.volumeLayerMask = 0;
                }
            }
            catch { }
        }

        private void RestorePreGameplayEyeV114(bool leftEye)
        {
            bool saved = leftEye ? _preGameplayLeftEyeSavedV114 : _preGameplayRightEyeSavedV114;
            Camera cam = leftEye ? _preGameplayLeftEyeSavedCameraV114 : _preGameplayRightEyeSavedCameraV114;
            if (!saved || cam == null)
            {
                ClearSavedPreGameplayEyeStateV114(leftEye);
                return;
            }

            try
            {
                if (leftEye)
                {
                    cam.cullingMask = _preGameplayLeftEyeCullingMaskV114;
                    cam.clearFlags = _preGameplayLeftEyeClearFlagsV114;
                    cam.backgroundColor = _preGameplayLeftEyeBackgroundColorV114;

                    if (_preGameplayLeftEyeHadHdV114)
                    {
                        HDAdditionalCameraData hd = cam.GetComponent<HDAdditionalCameraData>();
                        if (hd != null)
                        {
                            hd.clearColorMode = _preGameplayLeftEyeHdClearColorModeV114;
                            hd.backgroundColorHDR = _preGameplayLeftEyeHdBackgroundColorV114;
                            hd.volumeLayerMask = _preGameplayLeftEyeHdVolumeLayerMaskV114;
                        }
                    }
                }
                else
                {
                    cam.cullingMask = _preGameplayRightEyeCullingMaskV114;
                    cam.clearFlags = _preGameplayRightEyeClearFlagsV114;
                    cam.backgroundColor = _preGameplayRightEyeBackgroundColorV114;

                    if (_preGameplayRightEyeHadHdV114)
                    {
                        HDAdditionalCameraData hd = cam.GetComponent<HDAdditionalCameraData>();
                        if (hd != null)
                        {
                            hd.clearColorMode = _preGameplayRightEyeHdClearColorModeV114;
                            hd.backgroundColorHDR = _preGameplayRightEyeHdBackgroundColorV114;
                            hd.volumeLayerMask = _preGameplayRightEyeHdVolumeLayerMaskV114;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[VRPlayer][V114d LoadingPerf] eye-state restore failed: " + e.Message);
            }

            // V153: The V146 sticky loading epoch can survive for many seconds *after*
            // StereoRender has copied MainCameraFP's HDRP profile to the VR eyes.
            // Restoring the pre-loading (title/cutscene) HDRP volume mask here would
            // overwrite that one-shot gameplay profile and hide volume/custom-pass
            // effects, including the known construction-ghost rendering dependency.
            // Only replace the three HDRP fields altered by ApplyPreGameplayBlackEyeV114;
            // leave the eye camera's own AA/DLSS/frame settings/culling logic untouched.
            // V170: multiplayer MainCameraFP is owned by DontDestroyOnLoad.
            // It requires the same native HDRP restoration as single-player.
            if (UsedCamera != null && UsedCamera.name == "MainCameraFP" &&
                LocalPlayer != null)
            {
                try
                {
                    HDAdditionalCameraData nativeGameplayHd = UsedCamera.GetComponent<HDAdditionalCameraData>();
                    HDAdditionalCameraData restoredEyeHd = cam.GetComponent<HDAdditionalCameraData>();
                    if (nativeGameplayHd != null && restoredEyeHd != null)
                    {
                        int staleVolumeMask = restoredEyeHd.volumeLayerMask.value;
                        restoredEyeHd.clearColorMode = nativeGameplayHd.clearColorMode;
                        restoredEyeHd.backgroundColorHDR = nativeGameplayHd.backgroundColorHDR;
                        restoredEyeHd.volumeLayerMask = nativeGameplayHd.volumeLayerMask;
                        MelonLogger.Warning("[VRPlayer][V153 GhostProfile] " +
                            (leftEye ? "LEFT" : "RIGHT") +
                            " stalePreloadVolumeMask=0x" + staleVolumeMask.ToString("X8") +
                            " restoredGameplayVolumeMask=0x" +
                            nativeGameplayHd.volumeLayerMask.value.ToString("X8") +
                            " eyeCullingMask=0x" + cam.cullingMask.ToString("X8") +
                            " nativeCamera=" + UsedCamera.name);
                    }
                    else
                    {
                        MelonLogger.Warning("[VRPlayer][V153 GhostProfile] " +
                            (leftEye ? "LEFT" : "RIGHT") +
                            " missing HDRP data; old V114 restore left unchanged");
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("[VRPlayer][V153 GhostProfile] restore failed: " + e.Message);
                }
            }

            ClearSavedPreGameplayEyeStateV114(leftEye);
        }

        private void ClearSavedPreGameplayEyeStateV114(bool leftEye)
        {
            if (leftEye)
            {
                _preGameplayLeftEyeSavedV114 = false;
                _preGameplayLeftEyeSavedCameraV114 = null;
                _preGameplayLeftEyeHadHdV114 = false;
            }
            else
            {
                _preGameplayRightEyeSavedV114 = false;
                _preGameplayRightEyeSavedCameraV114 = null;
                _preGameplayRightEyeHadHdV114 = false;
            }
        }

        private void ReassertPreGameplayBlackEyesV114()
        {
            // UpdateHdrpDataCopyFromNativeCamera() runs later in this Update() and may copy the
            // gameplay camera's full HDRP state onto the eyes. During loading, reassert our black
            // utility state after that copy so one transitional frame cannot resurrect the sky.
            if (!_preGameplayEyeSuppressionActiveV107 || StereoRender == null) return;
            ApplyPreGameplayBlackEyeV114(StereoRender.LeftCam, true);
            ApplyPreGameplayBlackEyeV114(StereoRender.RightCam, false);
        }

        private void UpdatePreGameplayEyeRenderSuppressionV107()
        {
            if (StereoRender == null || StereoRender.LeftCam == null || StereoRender.RightCam == null) return;

            string scene = string.Empty;
            try { scene = SceneManager.GetActiveScene().name ?? string.Empty; } catch { }
            bool loadingScene = scene == "SonsMainLoading" || scene == "BlankScene";
            if (loadingScene && !_v146LoadingScenePreviously &&
                (scene == "SonsMainLoading" || !global::SonsVRMod.StereoRender.AerGameplayReadyForActivation))
                BeginPreGameplayLoadingEpochV146("active-scene-enter:" + scene);
            _v146LoadingScenePreviously = loadingScene;

            // V155: after a REAL cutscene-camera/VR-eye handoff, keep the intro visible.
            // The old V146/V154 raw camera and SkipCutscene prompt signals oscillate
            // during the helicopter/crash/shore script: do not reshow LOADING then.
            bool rawIntroCutscene = _v146LoadingEpochActive && IsNativeIntroCutsceneActiveV146();
            bool introCutscene = _v146LoadingEpochActive && IsIntroViewReadyV155(rawIntroCutscene);
            if (_v146LoadingEpochActive && introCutscene != _v146LastCutsceneSignal)
            {
                _v146LastCutsceneSignal = introCutscene;
                _v146NativeGameplayStableSince = -1f;
                MelonLogger.Msg("[VRPlayer][V155 LoadingCover] CINEMATIC " +
                    (introCutscene ? "REVEALED_AND_LATCHED" : "WAITING_FOR_VALID_CAMERA") +
                    "; epoch still active");
            }

            // V170: in multiplayer, VRSystem reports the camera object's owner
            // scene (DontDestroyOnLoad), while the active world scene can remain
            // BlankScene even when the local player is fully interactive.
            bool gameplayCameraOwner = UsedSceneName == "SonsMain" ||
                UsedSceneName == "DontDestroyOnLoad";
            bool nativeGameplay = gameplayCameraOwner &&
                UsedCamera != null && UsedCamera.name == "MainCameraFP" &&
                LocalPlayer != null && (!introCutscene || _v155IntroLatched);
            if (_v146LoadingEpochActive && nativeGameplay)
            {
                if (_v146NativeGameplayStableSince < 0f)
                    _v146NativeGameplayStableSince = Time.realtimeSinceStartup;
                ProbeNativeSpawnV146();
                float stableSeconds = Time.realtimeSinceStartup - _v146NativeGameplayStableSince;
                // Normally release promptly after a grounded native player has existed
                // for 0.65 s. If the controller is intentionally airborne/swimming,
                // permit a 12-second fallback provided there is no under-map evidence.
                bool verified = _v146SpawnVerified && stableSeconds >= 0.65f;
                // V154: native locomotion means the game has accepted movement.
                // Alternatively, a stable floor/camera/avatar configuration means
                // gameplay has physically spawned even if isGrounded never becomes true.
                // Preserve the old 12-second fallback for water/airborne saves.
                bool avatarNearEye = false;
                try
                {
                    Vector3 center = (StereoRender.LeftCam.transform.position +
                        StereoRender.RightCam.transform.position) * 0.5f;
                    Vector3 body = LocalPlayer.transform.position;
                    avatarNearEye = Vector2.Distance(new Vector2(center.x, center.z),
                        new Vector2(body.x, body.z)) < 3.0f &&
                        Mathf.Abs(center.y - body.y) < 4.0f;
                }
                catch { }
                bool motionReady = stableSeconds >= V154MinWorldStableSeconds &&
                    avatarNearEye && _v154PlayerLocomotionSeen &&
                    !_v146SpawnHasCeilingWithoutGround;
                bool physicallyReady = stableSeconds >= 2.25f && avatarNearEye &&
                    _v154StableFloorProbes >= 4 && !_v146SpawnHasCeilingWithoutGround;
                bool fallback = stableSeconds >= 12f && !_v146SpawnHasCeilingWithoutGround;
                if (verified || motionReady || physicallyReady || fallback)
                {
                    float elapsed = Time.realtimeSinceStartup - _v146LoadingEpochStart;
                    _v146LoadingEpochActive = false;
                    _v170MultiplayerSpawnConfirmed = UsedSceneName == "DontDestroyOnLoad";
                    MelonLogger.Warning("[VRPlayer][V170 LoadingCover] RELEASE after " +
                        elapsed.ToString("F2") + "s; trigger=" +
                        (verified ? "grounded-spawn" : motionReady ? "avatar-locomotion" :
                         physicallyReady ? "stable-avatar-floor" : "airborne/water-fallback") +
                        " floorProbes=" + _v154StableFloorProbes +
                        " avatarTravel=" + _v154ObservedAvatarTravel.ToString("F2") +
                        " avatarNearEye=" + avatarNearEye +
                        " scene=" + scene + " nativeScene=" + UsedSceneName +
                        " camera=" + UsedCamera.name);
                }
                else if (Time.realtimeSinceStartup - _v146LastTransitionLog >= 3f)
                {
                    _v146LastTransitionLog = Time.realtimeSinceStartup;
                    MelonLogger.Msg("[VRPlayer][V146 LoadingCover] WAIT_SPAWN" +
                        " stableSeconds=" + stableSeconds.ToString("F2") +
                        " grounded=" + _v146SpawnHasGround +
                        " overheadNoFloor=" + _v146SpawnHasCeilingWithoutGround +
                        " avatarFloor=" + _v154FloorNearAvatar +
                        " floorProbes=" + _v154StableFloorProbes +
                        " avatarTravel=" + _v154ObservedAvatarTravel.ToString("F2") +
                        " controllerActive=" + _v154PlayerControllerActive +
                        " scene=" + scene);
                }
            }
            else if (_v146LoadingEpochActive)
            {
                _v146NativeGameplayStableSince = -1f;
                _v154HavePreviousAvatarPosition = false;
                _v154ObservedAvatarTravel = 0f;
                _v154PlayerLocomotionSeen = false;
                _v154StableFloorProbes = 0;
                // V170: a missing camera/player gate used to fail silently forever.
                if (Time.realtimeSinceStartup - _v146LastTransitionLog >= 3f)
                {
                    _v146LastTransitionLog = Time.realtimeSinceStartup;
                    MelonLogger.Msg("[VRPlayer][V170 LoadingCover] WAIT_CONTEXT" +
                        " activeScene=" + scene + " owner=" + UsedSceneName +
                        " camera=" + (UsedCamera != null ? UsedCamera.name : "null") +
                        " localPlayer=" + (LocalPlayer != null) +
                        " intro=" + introCutscene +
                        " epochSeconds=" + (Time.realtimeSinceStartup - _v146LoadingEpochStart).ToString("F2"));
                }
            }

            // V170: SetSceneAndCamera may have adopted MainCameraFP tens of seconds
            // earlier. Arm deferred AER on the very frame multiplayer spawn is
            // verified, before the BlankScene fallback decides whether to mask.
            TryArmDeferredGameplayV170("spawn-gate-update");

            // V114 old behavior remains the safety net if an epoch wasn't observed. The
            // sticky V146 epoch survives additive scene changes and stale AER-ready flags.
            bool suppress = !introCutscene && (_v146LoadingEpochActive ||
                (loadingScene && !global::SonsVRMod.StereoRender.AerGameplayReadyForActivation));

            if (suppress)
            {
                ApplyPreGameplayBlackEyeV114(StereoRender.LeftCam, true);
                ApplyPreGameplayBlackEyeV114(StereoRender.RightCam, false);

                if (!_preGameplayEyeSuppressionActiveV107)
                {
                    _preGameplayEyeSuppressionActiveV107 = true;
                    _preGameplayEyeSuppressionStartV107 = Time.realtimeSinceStartup;
                    MelonLogger.Warning(
                        $"[VRPlayer][V114 LoadingPerf] PRE-GAME WORLD/SKY RENDER SUPPRESSED: scene={scene}; " +
                        $"both VR eyes now render ONLY mod loading layer {PreGameplayLoadingLayerV114} " +
                        $"(mask 0x{PreGameplayLoadingCullingMaskV114:X8}) on constant black with HDRP volumeLayerMask=0. " +
                        "No native layer-5 loading sky/backing is needed in headset.");
                    CameraCompositorAuditV114.NoteEvent(
                        "PRE_GAME_BLACK_LOADING_BEGIN",
                        $"scene={scene} eyeMask=0x{PreGameplayLoadingCullingMaskV114:X8} layer={PreGameplayLoadingLayerV114}");
                }
            }
            else if (_preGameplayEyeSuppressionActiveV107)
            {
                RestorePreGameplayEyeV114(true);
                RestorePreGameplayEyeV114(false);
                _preGameplayEyeSuppressionActiveV107 = false;
                float suppressedSeconds = _preGameplayEyeSuppressionStartV107 >= 0f
                    ? Time.realtimeSinceStartup - _preGameplayEyeSuppressionStartV107 : -1f;
                _preGameplayEyeSuppressionStartV107 = -1f;
                MelonLogger.Warning(
                    $"[VRPlayer][V114 LoadingPerf] gameplay gate ready / loading ended after {suppressedSeconds:F2}s: " +
                    "restored each eye's exact pre-loading culling/clear/HDRP volume state.");
                CameraCompositorAuditV114.NoteEvent(
                    "PRE_GAME_BLACK_LOADING_END",
                    $"suppressedSeconds={suppressedSeconds:F2}");
                CameraCompositorAuditV114.Snapshot("gameplay_gate_after_loading_suppression");
            }
        }

        // ZER0INT V114 LOADING UX — keep the fast synthetic loader, but isolate it on the dedicated
        // high layer used by the black pre-game eye mask. No native loading sky/world is required.
        // deliberately simple three-line synthetic label so the proportional font cannot make the
        // word LOADING walk left/right as the spinner glyph changes.
        private GameObject _preGameplayLoadingOverlayV108;
        private UnityEngine.UI.Text _preGameplayLoadingTextV108;
        private int _preGameplayLoadingSpinnerV108 = 0;
        private float _preGameplayLoadingSpinnerNextV108 = 0f;
        private static readonly string[] PreGameplayLoadingSpinnerFramesV108 = new string[] { "\\", "|", "/", "-" };

        // The old game's LoadingIndictor root remains active for its state/animation logic, but its
        // renderers are hidden while our replacement is shown. Never deactivate the root itself.
        private GameObject _nativeLoadingIndicatorRootV110;
        private Renderer[] _nativeLoadingRenderersV110;
        private bool[] _nativeLoadingRendererStatesV110;
        private Graphic[] _nativeLoadingGraphicsV110;
        private bool[] _nativeLoadingGraphicStatesV110;
        private bool _nativeLoadingVisualsSuppressedV110;
        private float _nextNativeLoadingVisualSearchV110 = 0f;

        private void SetNativeLoadingVisualsSuppressedV110(bool suppress)
        {
            if (suppress == _nativeLoadingVisualsSuppressedV110) return;
            try
            {
                if (suppress)
                {
                    // FindLoadingIndictorRoot() is a scene-wide Transform search. Do NOT repeat it
                    // every loading frame if the object has not spawned yet.
                    if (_nativeLoadingIndicatorRootV110 == null)
                    {
                        if (Time.unscaledTime < _nextNativeLoadingVisualSearchV110) return;
                        _nextNativeLoadingVisualSearchV110 = Time.unscaledTime + 0.50f;
                        _nativeLoadingIndicatorRootV110 = FindLoadingIndictorRoot();
                    }
                    if (_nativeLoadingIndicatorRootV110 == null) return;
                    _nativeLoadingRenderersV110 = _nativeLoadingIndicatorRootV110.GetComponentsInChildren<Renderer>(true);
                    _nativeLoadingRendererStatesV110 = new bool[_nativeLoadingRenderersV110.Length];
                    for (int i = 0; i < _nativeLoadingRenderersV110.Length; ++i)
                    {
                        Renderer r = _nativeLoadingRenderersV110[i];
                        if (r == null) continue;
                        _nativeLoadingRendererStatesV110[i] = r.enabled;
                        r.enabled = false;
                    }
                    _nativeLoadingGraphicsV110 = _nativeLoadingIndicatorRootV110.GetComponentsInChildren<Graphic>(true);
                    _nativeLoadingGraphicStatesV110 = new bool[_nativeLoadingGraphicsV110.Length];
                    for (int i = 0; i < _nativeLoadingGraphicsV110.Length; ++i)
                    {
                        Graphic g = _nativeLoadingGraphicsV110[i];
                        if (g == null) continue;
                        _nativeLoadingGraphicStatesV110[i] = g.enabled;
                        g.enabled = false;
                    }
                    _nativeLoadingVisualsSuppressedV110 = true;
                    MelonLogger.Msg($"[VRPlayer][V111 LoadingUI] native LoadingIndictor visuals hidden; renderers={_nativeLoadingRenderersV110.Length} graphics={_nativeLoadingGraphicsV110.Length}. Root remains active for game logic.");
                }
                else
                {
                    if (_nativeLoadingRenderersV110 != null && _nativeLoadingRendererStatesV110 != null)
                    {
                        int n = Mathf.Min(_nativeLoadingRenderersV110.Length, _nativeLoadingRendererStatesV110.Length);
                        for (int i = 0; i < n; ++i)
                            if (_nativeLoadingRenderersV110[i] != null)
                                _nativeLoadingRenderersV110[i].enabled = _nativeLoadingRendererStatesV110[i];
                    }
                    if (_nativeLoadingGraphicsV110 != null && _nativeLoadingGraphicStatesV110 != null)
                    {
                        int n = Mathf.Min(_nativeLoadingGraphicsV110.Length, _nativeLoadingGraphicStatesV110.Length);
                        for (int i = 0; i < n; ++i)
                            if (_nativeLoadingGraphicsV110[i] != null)
                                _nativeLoadingGraphicsV110[i].enabled = _nativeLoadingGraphicStatesV110[i];
                    }
                    _nativeLoadingVisualsSuppressedV110 = false;
                    _nativeLoadingIndicatorRootV110 = null;
                    _nativeLoadingRenderersV110 = null;
                    _nativeLoadingRendererStatesV110 = null;
                    _nativeLoadingGraphicsV110 = null;
                    _nativeLoadingGraphicStatesV110 = null;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[VRPlayer][V111 LoadingUI] native loading visual suppression failed: " + e.Message);
            }
        }

        private void EnsurePreGameplayLoadingOverlayV108()
        {
            if (_preGameplayLoadingOverlayV108 != null) return;
            if (StereoRender == null || StereoRender.Head == null) return;

            _preGameplayLoadingOverlayV108 = new GameObject("SonsVR_V111_LoadingOverlay");
            _preGameplayLoadingOverlayV108.layer = PreGameplayLoadingLayerV114; // MUST match V114 black-loader eye mask.
            var canvas = _preGameplayLoadingOverlayV108.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 10000;

            var rootRt = _preGameplayLoadingOverlayV108.GetComponent<RectTransform>();
            rootRt.sizeDelta = new Vector2(1200f, 520f);
            _preGameplayLoadingOverlayV108.transform.SetParent(StereoRender.Head, false);
            _preGameplayLoadingOverlayV108.transform.localPosition = new Vector3(0f, 0.02f, 0.70f);
            _preGameplayLoadingOverlayV108.transform.localRotation = Quaternion.identity;
            _preGameplayLoadingOverlayV108.transform.localScale = Vector3.one * 0.00055f;

            var mainGo = new GameObject("LoadingMain");
            mainGo.layer = PreGameplayLoadingLayerV114;
            mainGo.transform.SetParent(_preGameplayLoadingOverlayV108.transform, false);
            _preGameplayLoadingTextV108 = mainGo.AddComponent<UnityEngine.UI.Text>();
            _preGameplayLoadingTextV108.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _preGameplayLoadingTextV108.fontSize = 96;
            _preGameplayLoadingTextV108.fontStyle = FontStyle.Bold;
            _preGameplayLoadingTextV108.alignment = TextAnchor.MiddleCenter;
            _preGameplayLoadingTextV108.color = Color.white;
            _preGameplayLoadingTextV108.text = "LOADING\n\\\nSOTF VR";
            var mainOutline = mainGo.AddComponent<UnityEngine.UI.Outline>();
            mainOutline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            mainOutline.effectDistance = new Vector2(3f, -3f);
            var mainRt = mainGo.GetComponent<RectTransform>();
            mainRt.anchorMin = Vector2.zero;
            mainRt.anchorMax = Vector2.one;
            mainRt.offsetMin = Vector2.zero;
            mainRt.offsetMax = Vector2.zero;

            _preGameplayLoadingOverlayV108.SetActive(false);
            MelonLogger.Msg("[VRPlayer][V114 LoadingUI] head-locked three-line dedicated-layer loading overlay created on black background.");
        }

        private void UpdatePreGameplayLoadingOverlayV108()
        {
            bool show = _preGameplayEyeSuppressionActiveV107;
            if (!show)
            {
                SetNativeLoadingVisualsSuppressedV110(false);
                if (_preGameplayLoadingOverlayV108 != null && _preGameplayLoadingOverlayV108.activeSelf)
                    _preGameplayLoadingOverlayV108.SetActive(false);
                return;
            }

            SetNativeLoadingVisualsSuppressedV110(true);
            EnsurePreGameplayLoadingOverlayV108();
            if (_preGameplayLoadingOverlayV108 == null || _preGameplayLoadingTextV108 == null) return;
            if (!_preGameplayLoadingOverlayV108.activeSelf) _preGameplayLoadingOverlayV108.SetActive(true);

            // Keep the synthetic loader centered on the ACTUAL lifted eye viewpoint. The V111
            // +0.30m title/loading lift intentionally moves only the eye cameras, not Head/UI.
            // Without this compensation the loader itself would appear ~0.30m too low.
            if (StereoRender.LeftCam != null && StereoRender.RightCam != null && StereoRender.Head != null)
            {
                Vector3 eyeMid = (StereoRender.LeftCam.transform.position + StereoRender.RightCam.transform.position) * 0.5f;
                Vector3 overlayPos = eyeMid + StereoRender.Head.forward * 0.70f + StereoRender.Head.up * 0.02f;
                _preGameplayLoadingOverlayV108.transform.SetPositionAndRotation(overlayPos, StereoRender.Head.rotation);
            }

            float now = Time.unscaledTime;
            if (now >= _preGameplayLoadingSpinnerNextV108)
            {
                _preGameplayLoadingSpinnerNextV108 = now + 0.16f;
                string frame = PreGameplayLoadingSpinnerFramesV108[_preGameplayLoadingSpinnerV108 % PreGameplayLoadingSpinnerFramesV108.Length];
                _preGameplayLoadingSpinnerV108++;
                _preGameplayLoadingTextV108.text = "LOADING\n" + frame + "\nSOTF VR";
            }
        }

        private Camera _nativeTrashConfiguredCameraV107;
        private void ConfigureNativeCameraAsTrashV107()
        {
            if (UsedCamera == null || StereoRender == null) return;
            if (!UsedCamera.name.Equals("MainCameraFP", StringComparison.Ordinal)) return;

            // IMPORTANT ORDER: UpdateHdrpDataCopyFromNativeCamera() is called immediately before
            // this method. Never strip the source camera BEFORE the eyes have inherited the game's
            // correct HDRP profile (water/construction outlines/etc.).
            global::SonsVRMod.StereoRender.TryConfigureNativeTrashCameraV107(UsedCamera);

            if (_nativeTrashConfiguredCameraV107 != UsedCamera)
            {
                _nativeTrashConfiguredCameraV107 = UsedCamera;
                MelonLogger.Warning($"[VRPlayer][V107 NativeCamera] '{UsedCamera.name}' kept alive for Camera.main/mouse semantics, but TAA/DLSS/DRS/persistent history + optional HDRP effects are stripped after eye-profile copy. OF was never attached to this camera.");
            }
        }

        /// <summary>
        /// The camera stays enabled on purpose - disabling it would remove it from
        /// Camera.allCameras, which is exactly what VRSystem scans for its own "did the active
        /// camera change" detection, and would trigger an unwanted rig respawn.
        /// Re-applied every poll (not just once) since game code is free to reset the culling mask
        /// of its own camera at any point, matching the same "reassert, don't just set once"
        /// lesson already learned elsewhere in this project.
        /// </summary>
        private void NeutralizeNativeCamera()
        {
            if (!NeutralizeNativeCameraEnabled) return;
            if (UsedCamera == null) return;
            // Safety: never neuter the native camera until our OWN eye cameras are confirmed up
            // and rendering - doing this any earlier would leave nothing rendering at all (a
            // black screen), since the eyes wouldn't exist yet to pick up the slack.
            if (StereoRender == null || StereoRender.LeftCam == null || StereoRender.RightCam == null) return;

            if (_neutralizedNativeCamera != UsedCamera)
            {
                // UsedCamera changed (e.g. a rig respawn) - restore whatever we previously touched
                // before adopting the new one, so we never leave a stale camera stuck at
                // cullingMask=0 (or the wrong cameraType) if it's no longer the one actually in use.
                if (_neutralizedNativeCamera != null)
                {
                    _neutralizedNativeCamera.cullingMask = _savedNativeCameraCullingMask;
                    if (NeutralizeNativeCameraChangeCameraType)
                    {
                        try { _neutralizedNativeCamera.cameraType = _savedNativeCameraType; }
                        catch (Exception) { /* best-effort restore; the camera being swapped away likely means this instance is on its way out regardless */ }
                    }
                }

                _neutralizedNativeCamera = UsedCamera;
                _savedNativeCameraCullingMask = UsedCamera.cullingMask;
                _savedNativeCameraType = UsedCamera.cameraType;
                MelonLogger.Msg($"[VRPlayer][NeutralizeNativeCamera] Now neutralizing '{UsedCamera.name}' (was cullingMask={_savedNativeCameraCullingMask}, cameraType={_savedNativeCameraType}).");
            }

            if (_neutralizedNativeCamera.cullingMask != NeutralizeNativeCameraCullingMask)
            {
                _neutralizedNativeCamera.cullingMask = NeutralizeNativeCameraCullingMask;
            }
            if (NeutralizeNativeCameraChangeCameraType && _neutralizedNativeCamera.cameraType != NeutralizeNativeCameraTargetType)
            {
                try
                {
                    _neutralizedNativeCamera.cameraType = NeutralizeNativeCameraTargetType;
                }
                catch (Exception e)
                {
                    if (!_neutralizeCameraTypeWarnedOnce)
                    {
                        _neutralizeCameraTypeWarnedOnce = true;
                        MelonLogger.Warning($"[VRPlayer][NeutralizeNativeCamera] Setting cameraType failed: {e.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Copies the native camera's own HDAdditionalCameraData onto both eye cameras - the
        /// actual fix for construction ghost-preview outlines and underwater water-density
        /// murkiness being invisible in VR (this project was adding a fresh, HDRP-default
        /// HDAdditionalCameraData to the eyes instead of the game's own, already-correctly-
        /// configured one). See StereoRender.CopyHdrpDataFromNativeCamera for the full
        /// explanation. UsedCamera isn't guaranteed ready at StereoRender's own one-time Setup()
        /// call, so this runs from here instead, retrying every poll until it succeeds - the
        /// function itself is a no-op once already copied from the current UsedCamera, so this is
        /// cheap to call repeatedly.
        /// </summary>
        private void UpdateHdrpDataCopyFromNativeCamera()
        {
            if (UsedCamera == null) return;
            if (StereoRender == null || StereoRender.LeftCam == null || StereoRender.RightCam == null) return;

            StereoRender.CopyHdrpDataFromNativeCamera(UsedCamera);
        }

        public static bool UiToolkitCaptureEnabled = false; // DISABLED - confirmed via testing (twice, with both Save and Quit dialogs genuinely triggered) that this finds zero PanelSettings/UIDocuments the entire session ("0 documents on show, 0 panel settings in memory"), despite being closely modeled on another dev's proven, working implementation for the exact same game. Root cause not yet diagnosed - possibly an IL2CPP interop quirk specific to these types in this project's context, a load-order difference, or something else. Left in place but off until properly diagnosed from a clean baseline - see VRUiToolkit.cs for the full technique explanation, which remains a good lead for the Save/Quit and crafting-mat modal mystery even though this specific attempt didn't pan out.
        private VRUiToolkit _uiToolkit;

        /// <summary>
        /// Captures any UI Toolkit panel currently drawing to the screen (a completely separate
        /// system from the Canvas/GameObject UI this project's other menu-conversion fixes target)
        /// and displays it on a world-space surface in front of the player. See VRUiToolkit.cs.
        /// </summary>
        private void UpdateUiToolkitCapture()
        {
            if (!UiToolkitCaptureEnabled) return;
            if (StereoRender == null || StereoRender.Head == null) return;

            if (_uiToolkit == null)
            {
                _uiToolkit = new VRUiToolkit();
            }

            _uiToolkit.Update(StereoRender.Head.position, StereoRender.Head.forward);
        }

        private void UpdateCanvasPositionForVR()
        {
            if (StereoRender == null || StereoRender.Head == null || UsedCamera == null) return;

            Vector3 headRight = StereoRender.Head.right;
            Vector3 headUp = StereoRender.Head.up;
            Vector3 headForward = StereoRender.Head.forward;
            Vector3 desiredPosition = StereoRender.Head.position + headForward * 0.5f;

            if (_screenSpaceCanvas != null)
            {
                Quaternion desiredRotation = Quaternion.LookRotation(StereoRender.Head.forward, headUp);
                if (_screenSpaceCanvas.worldCamera != UsedCamera)
                {
                    _screenSpaceCanvas.worldCamera = UsedCamera;
                    UsedCamera.depth = 100;
                    UsedCamera.clearFlags = CameraClearFlags.Depth;
                    UsedCamera.cullingMask = -1;
                }

                _screenSpaceCanvas.transform.SetPositionAndRotation(desiredPosition, desiredRotation);
            }

            // Minimap positioning MOVED to a dedicated end-of-frame coroutine below - see
            // EnsureMinimapEndOfFrameCoroutine/UpdateMinimapPositionEndOfFrame for why.
        }

        private float _lastModalDialogCanvasUpdateTime = 0f;

        /// <summary>
        /// CONFIRMED via diagnostic log: the Save/Quit confirmation dialog (and very likely the
        /// crafting-mat "Add items to the mat..." tutorial too, same shared system) renders
        /// through its own separate Canvas component directly on "DynamicModalDialogGui" - a
        /// completely different GameObject from PlayerUI/ScreenSpaceCanvas, which is the only
        /// canvas UpdateCanvasPositionForVR above has ever touched. That's the actual, complete
        /// explanation for why this UI was never visible in VR - not a fundamentally different
        /// rendering system (the earlier UI Toolkit theory), just a second, separate
        /// ScreenSpaceOverlay canvas nobody had converted to world-space yet. Applying the exact
        /// same technique already proven for the main UI canvas.
        /// </summary>
        public static float ModalDialogTargetWidthMeters = 0.8f; // tunable - the dialog's own RectTransform sizeDelta is very likely in screen-pixel units (e.g. 1000+), which would make it enormous and effectively invisible at any close viewing distance without scaling down to a real-world size first. 0.8m is a reasonable starting guess for a dialog box held at arm's length. CONFIRMED WORKING for the Save/Quit dialog.

        // Tunable placement for the tarp tutorial specifically - adjust freely if it doesn't land
        // right. Forward/height are measured from the player's own position (not the tarp itself,
        // which isn't directly tracked); tilt angles the panel up toward eye height like a sign
        // propped up at the tarp, 0 would stand it bolt upright.
        public static float TarpTutorialForwardDistanceMeters = 1.2f; // updated by user testing
        public static float TarpTutorialHeightMeters = 0.6f; // confirmed by user testing
        public static float TarpTutorialTiltDegrees = -70f; // updated by user testing
        private bool _modalDialogScaleLogged = false;
        private Canvas _cachedModalDialogCanvas;
        private bool _modalDialogPlacedOnce = false; // unused (freezeAfterFirstPlacement=false below), kept for signature symmetry

        private void UpdateModalDialogCanvasForVR()
        {
            // V159: a modal created during a title handoff must not inherit the
            // pre-recenter gaze. The working gameplay modal path is unchanged.
            if (IsNativeTitleCameraContextV158 && _v159PendingTitleCamera != null) return;
            if (StereoRender == null || StereoRender.Head == null || UsedCamera == null) return;
            if (Time.time - _lastModalDialogCanvasUpdateTime < 0.5f) return;
            _lastModalDialogCanvasUpdateTime = Time.time;

            // Keeps following the head, unlike the tarp tutorial below - this can appear anywhere
            // the player is, not tied to a fixed world location like the tarp.
            ConvertScreenSpaceCanvasToWorldSpace("DynamicModalDialogGui", "ModalDialogCanvas", ref _modalDialogScaleLogged, ref _cachedModalDialogCanvas, freezeAfterFirstPlacement: false, ref _modalDialogPlacedOnce);
        }

        private bool _addItemsToMatScaleLogged = false;
        private Canvas _cachedAddItemsToMatCanvas;
        private float _lastAddItemsToMatCanvasUpdateTime = 0f;
        private bool _addItemsToMatPlacedOnce = false;
        private GameObject _tarpTutorialBackgroundObject;

        // Tunable - per user's own report (with flat-screen screenshot confirming it): the native
        // dark blur panel behind this tutorial's text doesn't render in VR, making the white text
        // hard to read against the busy tarp/items behind it. Rather than chase why the native
        // blur fails (a separate, likely HDRP-Custom-Pass-related rendering path, the same
        // category of thing already ruled unfixable via reflection/runtime polling elsewhere in
        // this project), just adding our own simple, solid background panel behind the text -
        // same visual goal, much simpler and more reliable to build. Sized/positioned in the same
        // pixel-space the rest of this canvas already uses (Screen.width/height, per
        // ModalDialogTargetWidthMeters's own scale calculation), so it scales consistently.
        public static bool TarpTutorialBackgroundEnabled = true;
        public static float TarpTutorialBackgroundWidthFraction = 0.42f; // fraction of Screen.width
        public static float TarpTutorialBackgroundHeightFraction = 0.22f; // fraction of Screen.height
        public static float TarpTutorialBackgroundOpacity = 0.75f;

        /// <summary>
        /// FIXED (confirmed via user testing: size/position were fine, but it never appeared or
        /// disappeared with the actual tutorial text - it was created once and left permanently
        /// visible). Also, per the user's own report, there's a SECOND tutorial popup with a
        /// different placement that should get this same treatment. Rather than hardcode a
        /// specific child name (which would only ever match the first popup), this now scans the
        /// canvas's direct children every poll for whichever ONE is currently genuinely active -
        /// showing the background only when something is, and positioning it to match THAT
        /// child's own anchoredPosition (kept at the confirmed-good fixed size, not resized per
        /// popup, since the user only reported a positioning problem, not a sizing one). No active
        /// popup found - background hidden entirely, matching "no tutorial, no rectangle".
        /// </summary>
        private void UpdateTarpTutorialBackground(Canvas canvas)
        {
            if (!TarpTutorialBackgroundEnabled || canvas == null) return;

            if (_tarpTutorialBackgroundObject == null)
            {
                _tarpTutorialBackgroundObject = new GameObject("SonsVR_TutorialBackground");
                _tarpTutorialBackgroundObject.transform.SetParent(canvas.transform, false);
                _tarpTutorialBackgroundObject.transform.SetAsFirstSibling();

                var image = _tarpTutorialBackgroundObject.AddComponent<UnityEngine.UI.Image>();
                image.color = new Color(0f, 0f, 0f, TarpTutorialBackgroundOpacity);
                image.raycastTarget = false; // purely visual - don't intercept the "E: OK" interaction

                var newRt = _tarpTutorialBackgroundObject.GetComponent<RectTransform>();
                newRt.anchorMin = new Vector2(0.5f, 0.5f);
                newRt.anchorMax = new Vector2(0.5f, 0.5f);
                newRt.pivot = new Vector2(0.5f, 0.5f);
                newRt.sizeDelta = new Vector2(Screen.width * TarpTutorialBackgroundWidthFraction, Screen.height * TarpTutorialBackgroundHeightFraction);

                MelonLogger.Msg($"[VRPlayer][TarpTutorialBackground] Created background panel, size={newRt.sizeDelta}.");
            }

            RectTransform activePopupRt = null;
            for (int i = 0; i < canvas.transform.childCount; i++)
            {
                Transform child = canvas.transform.GetChild(i);
                if (child.gameObject == _tarpTutorialBackgroundObject) continue;
                if (!child.gameObject.activeInHierarchy) continue;

                activePopupRt = child.GetComponent<RectTransform>();
                break; // first genuinely active child found - whichever popup that is
            }

            if (activePopupRt == null)
            {
                _tarpTutorialBackgroundObject.SetActive(false);
                return;
            }

            var rt = _tarpTutorialBackgroundObject.GetComponent<RectTransform>();
            rt.anchoredPosition = activePopupRt.anchoredPosition;
            _tarpTutorialBackgroundObject.SetActive(true);
        }

        /// <summary>
        /// Same confirmed-working technique and helper as the Save/Quit dialog fix above. This
        /// time targeting "CraftMatScreenSpaceUi" rather than "AddItemsToMatModal" itself - a
        /// diagnostic round found the actual Canvas sits one hop up, on the PARENT
        /// (CraftMatScreenSpaceUi), not on AddItemsToMatModal directly. Confirmed identical
        /// signature to DynamicModalDialogGui before it was fixed: renderMode=ScreenSpaceOverlay,
        /// not the known ScreenSpaceCanvas, worldCamera=null.
        ///
        /// FROZEN AFTER FIRST PLACEMENT (user's own idea, investigating the tarp
        /// black-flash-on-head-movement report): unlike Save/Quit, this is tied to a fixed world
        /// location (the tarp) - placed once, where the player was looking when it first appeared
        /// (naturally down at the tarp, since that's where they'd look to interact with it), then
        /// left alone rather than continuously repositioned every poll while the head moves.
        /// </summary>
        private void UpdateAddItemsToMatCanvasForVR()
        {
            if (StereoRender == null || StereoRender.Head == null || UsedCamera == null) return;
            if (Time.time - _lastAddItemsToMatCanvasUpdateTime < 0.5f) return;
            _lastAddItemsToMatCanvasUpdateTime = Time.time;

            ConvertScreenSpaceCanvasToWorldSpace("CraftMatScreenSpaceUi", "AddItemsToMatCanvas", ref _addItemsToMatScaleLogged, ref _cachedAddItemsToMatCanvas, freezeAfterFirstPlacement: true, ref _addItemsToMatPlacedOnce, placeRelativeToPlayer: true);
            UpdateTarpTutorialBackground(_cachedAddItemsToMatCanvas);
        }

        private float _lastLoadingScreenScanTime = 0f;
        private readonly System.Collections.Generic.List<Canvas> _convertedLoadingCanvases = new System.Collections.Generic.List<Canvas>();
        private bool _loadingScreenActive = false;
        private float _lastNestedTitleMenuCanvasDiagTime = 0f;
        private readonly System.Collections.Generic.HashSet<string> _loggedNestedTitleMenuCanvases = new System.Collections.Generic.HashSet<string>();

        // Canvases we already handle elsewhere in this project by exact name - excluded here to
        // avoid double-processing/conflicting with those existing, confirmed-working fixes.
        private static readonly System.Collections.Generic.HashSet<string> KnownHandledCanvasNames = new System.Collections.Generic.HashSet<string>
        {
            "ScreenSpaceCanvas", "DynamicModalDialogGui", "CraftMatScreenSpaceUi", "TitleMenu"
        };

        /// <summary>
        /// The generic scan below deliberately skips anything nested under another canvas (to
        /// avoid double-converting) - meaning if the real loading UI is a nested sub-canvas INSIDE
        /// the already-converted TitleMenu, with its own independent renderMode, we'd never even
        /// look at it. Checking that directly: every child Canvas under TitleMenu, logged once
        /// each, regardless of its own renderMode/state.
        /// </summary>
        private void DiagNestedTitleMenuCanvases()
        {
            if (UsedSceneName != "SonsTitleScene" && UsedSceneName != "SonsMain") return;
            if (Time.time - _lastNestedTitleMenuCanvasDiagTime < 2f) return;
            _lastNestedTitleMenuCanvasDiagTime = Time.time;

            GameObject titleMenuGo = null;
            foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
            {
                if (go != null && go.name == "TitleMenu") { titleMenuGo = go; break; }
            }
            if (titleMenuGo == null) return;

            foreach (var canvas in titleMenuGo.GetComponentsInChildren<Canvas>(true))
            {
                if (canvas == null || canvas.gameObject == titleMenuGo) continue; // skip TitleMenu's own Canvas, already known/handled
                string path = GetHierarchyPath(canvas.transform);
                if (_loggedNestedTitleMenuCanvases.Contains(path)) continue;
                _loggedNestedTitleMenuCanvases.Add(path);
                var rect = canvas.GetComponent<RectTransform>();
                MelonLogger.Msg($"[VRPlayer][NestedTitleMenuCanvasDiag] {path} activeSelf={canvas.gameObject.activeSelf} renderMode={canvas.renderMode} " +
                    $"size={(rect != null ? rect.rect.size.ToString() : "n/a")} sizeDelta={(rect != null ? rect.sizeDelta.ToString() : "n/a")}");
            }
        }

        /// <summary>
        /// PORTED from another dev's fork of this mod: rather than search for a specific named
        /// object (which we tried repeatedly without success - "LoadingIndictor",
        /// "SinglePlayerLoadPanel", neither panned out), this scans for ANY Canvas that is
        /// genuinely active, not already world-space, has no other Canvas anywhere above it in the
        /// hierarchy (root-level only, avoiding double-converting nested sub-canvases), and has a
        /// real, non-trivial size (ruling out empty/uninitialized canvases, the same all-zero
        /// RectTransform problem hit earlier with DynamicModalDialogGui before it was populated).
        /// Whatever qualifies gets converted - this sidesteps needing to know the exact object name
        /// at all. Rescanned repeatedly (not once) since the loading interface is built piecemeal
        /// as the scene tears down and rebuilds, and re-asserted every poll since the game can
        /// reset a canvas back to screen-space at any point during that same rebuild - both lessons
        /// the other dev's own code comments call out explicitly, and both consistent with what
        /// we've already independently confirmed with DynamicModalDialogGui's own reset behavior.
        /// </summary>
        private void UpdateGenericLoadingScreenCanvases()
        {
            if (StereoRender == null || StereoRender.Head == null || UsedCamera == null) return;
            if (UsedSceneName != "SonsTitleScene" && UsedSceneName != "SonsMain") return;
            if (Time.time - _lastLoadingScreenScanTime < 0.25f) return;
            _lastLoadingScreenScanTime = Time.time;

            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (canvas == null || !canvas.isActiveAndEnabled) continue;
                if (canvas.renderMode == RenderMode.WorldSpace) continue;
                if (KnownHandledCanvasNames.Contains(canvas.gameObject.name)) continue;
                if (_convertedLoadingCanvases.Contains(canvas)) continue;

                if (canvas.transform.parent != null && canvas.transform.parent.GetComponentInParent<Canvas>() != null) continue;

                var rect = canvas.GetComponent<RectTransform>();
                if (rect == null) continue;

                Vector2 size = rect.rect.size;
                if (size.y <= 1f) size = rect.sizeDelta;
                if (size.y <= 1f) continue;

                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = UsedCamera;
                canvas.transform.localScale = Vector3.one * (0.6f / size.y); // 0.6m tall, a reasonable guess for a loading panel

                var scaler = canvas.GetComponent<CanvasScaler>();
                if (scaler != null) scaler.enabled = false;

                _convertedLoadingCanvases.Add(canvas);
                _loadingScreenActive = true;
                MelonLogger.Msg($"[VRPlayer][GenericLoadingScreen] '{canvas.gameObject.name}' moved into world space ({size.x:F0}x{size.y:F0}).");
            }

            if (!_loadingScreenActive) return;

            Vector3 headForward = StereoRender.Head.forward;
            headForward.y = 0f;
            if (headForward.sqrMagnitude < 1e-4f) return;
            headForward.Normalize();

            Vector3 panelPosition = StereoRender.Head.position + headForward * 1.0f;
            Quaternion panelRotation = Quaternion.LookRotation(headForward, Vector3.up);

            for (int i = _convertedLoadingCanvases.Count - 1; i >= 0; i--)
            {
                var canvas = _convertedLoadingCanvases[i];
                if (canvas == null)
                {
                    _convertedLoadingCanvases.RemoveAt(i);
                    continue;
                }
                if (canvas.renderMode != RenderMode.WorldSpace) canvas.renderMode = RenderMode.WorldSpace;
                canvas.transform.SetPositionAndRotation(panelPosition, panelRotation);
            }
        }

        /// <summary>
        /// Finds a Canvas by GameObject name anywhere in the scene, converts it to world-space
        /// positioned in front of the head, and scales it to a sane real-world size using
        /// Screen.width rather than the canvas's own RectTransform.sizeDelta (confirmed via log to
        /// read (0,0) on root-level ScreenSpaceOverlay canvases, since their real pixel size is
        /// driven dynamically by screen resolution rather than stored there directly). CONFIRMED
        /// WORKING for the Save/Quit dialog (DynamicModalDialogGui) - kept generic in case it's
        /// reused for other similarly-structured canvases later (the crafting-mat tutorial and the
        /// native loading screen were both tried this way too, but neither was confirmed working
        /// yet, so those specific callers were removed for now).
        /// </summary>
        private void ConvertScreenSpaceCanvasToWorldSpace(string targetName, string logTag, ref bool scaleLogged, ref Canvas cachedCanvas, bool freezeAfterFirstPlacement, ref bool alreadyPlacedOnce, bool placeRelativeToPlayer = false)
        {
            // PERFORMANCE FIX: this used to do a full-scene FindObjectsOfType<GameObject>(true)
            // scan (every GameObject in the scene, including inactive ones) on EVERY call, forever
            // - twice per poll cycle across the two callers, every 0.5s, for the entire session,
            // even long after the target had already been found. Confirmed by the user as the
            // likely cause of persistent jitter. Now only does that expensive scan once, caching
            // the result - re-scanning only if the cached reference goes stale (destroyed, e.g.
            // across a scene transition), matching the same caching pattern already used
            // elsewhere in this project (NeutralizeNativeCamera, CopyHdrpDataFromNativeCamera).
            if (cachedCanvas == null)
            {
                GameObject[] allGos = UnityEngine.Object.FindObjectsOfType<GameObject>(true);
                foreach (var go in allGos)
                {
                    if (go != null && go.name == targetName)
                    {
                        cachedCanvas = go.GetComponent<Canvas>();
                        break;
                    }
                }
            }
            Canvas targetCanvas = cachedCanvas;
            if (targetCanvas == null) return;

            // FIXED (user's own idea - investigating the tarp black-flash-on-head-movement
            // report): this used to reposition the canvas relative to the head on EVERY poll,
            // continuously following wherever the player looks. For a canvas tied to a fixed
            // world location (the tarp), that's unnecessary - once placed where the player was
            // looking when it first appeared (naturally down at the tarp itself, since that's
            // where they'd be looking to interact with it), it can just stay there rather than
            // continuously repositioning. Gated per-caller (freezeAfterFirstPlacement) so
            // Save/Quit - which can appear anywhere, not tied to a location - keeps following the
            // head as before; only the tarp tutorial freezes after its first placement.
            if (freezeAfterFirstPlacement && alreadyPlacedOnce)
            {
                // Position already set once - only the renderMode/scale bookkeeping below still
                // needs to run (cheap, idempotent), position/rotation are intentionally untouched.
            }
            else if (placeRelativeToPlayer && LocalPlayer != null)
            {
                // Per user's follow-up request: rather than freezing wherever they happened to be
                // looking (unpredictable), place it at a fixed, tunable spot relative to the
                // player's own position instead - roughly where the tarp itself would be laid out
                // (in front of and below the player, since it's on the ground). All three numbers
                // below are meant to be easy to adjust directly if this doesn't land exactly right
                // - no need to understand the rest of this function to tweak them.
                Vector3 flatForward = LocalPlayer.transform.forward;
                flatForward.y = 0f;
                if (flatForward.sqrMagnitude < 1e-4f) flatForward = Vector3.forward; // player was looking straight up/down - fall back to a fixed direction rather than dividing by ~zero
                flatForward.Normalize();

                Vector3 desiredPosition = LocalPlayer.transform.position
                    + flatForward * TarpTutorialForwardDistanceMeters
                    + Vector3.up * TarpTutorialHeightMeters;

                // Tilted to face up toward the player's eye height, like a sign propped up at the
                // tarp, rather than standing bolt upright.
                Vector3 lookDirection = Quaternion.AngleAxis(-TarpTutorialTiltDegrees, LocalPlayer.transform.right) * flatForward;
                Quaternion desiredRotation = Quaternion.LookRotation(lookDirection, Vector3.up);

                targetCanvas.transform.SetPositionAndRotation(desiredPosition, desiredRotation);
                alreadyPlacedOnce = true;
            }
            else
            {
                Vector3 headUp = StereoRender.Head.up;
                Vector3 headForward = StereoRender.Head.forward;
                Vector3 desiredPosition = StereoRender.Head.position + headForward * 0.5f;
                Quaternion desiredRotation = Quaternion.LookRotation(headForward, headUp);
                targetCanvas.transform.SetPositionAndRotation(desiredPosition, desiredRotation);
                alreadyPlacedOnce = true;
            }

            // FIXED (found via comparing HDRP-copy occurrence counts against these): this
            // re-triggered every time UsedCamera changed, since worldCamera != UsedCamera became
            // true on every completely normal camera swap (e.g. the tarp opening/closing) - 5-6
            // repeats logged in a single session, despite the canvas only ever needing to be
            // converted once. These are VR menus the player looks at with the headset, not a
            // flat-screen cursor tied to UsedCamera, so worldCamera doesn't need to track its
            // every change - it just needs A valid camera reference, set once. Now only checking
            // renderMode, never re-touching worldCamera once genuinely in world-space.
            if (targetCanvas.renderMode != RenderMode.WorldSpace)
            {
                targetCanvas.renderMode = RenderMode.WorldSpace;
                targetCanvas.worldCamera = UsedCamera;
                MelonLogger.Msg($"[VRPlayer][{logTag}] Converted {targetName} to world-space.");
            }

            var rt = targetCanvas.GetComponent<RectTransform>();
            float scaleFactor = ModalDialogTargetWidthMeters / Screen.width;
            Vector3 desiredScale = Vector3.one * scaleFactor;
            if (targetCanvas.transform.localScale != desiredScale)
            {
                targetCanvas.transform.localScale = desiredScale;
            }

            if (!scaleLogged)
            {
                scaleLogged = true;
                MelonLogger.Msg($"[VRPlayer][{logTag}] sizeDelta={(rt != null ? rt.sizeDelta.ToString() : "n/a")} " +
                    $"Screen.width={Screen.width} -> scaleFactor={scaleFactor:F5} -> localScale={desiredScale}");
            }
        }

        private float _lastMinimapPositionLogTime = 0f;
        private float _lastVitalsPositionLogTime = 0f;
        private float _lastMinimapCameraCheckTime = 0f;
        private bool _minimapEndOfFrameCoroutineStarted = false;

        // ZER0INT V63: focused runtime geometry probe. It was introduced after V62 with two
        // hypotheses (VitalsBackingCircle motion, and a Canvas rebuild/layout issue). The probe
        // later falsified both narrow explanations: ScreenBounds, VitalsBackingCircle, ScreenMask
        // and PlayerArrow are all rigid in eye space, while MapRoot/Map intentionally pan/rotate;
        // and the broad HUD column was a factor-of-two physical scale error. Keep the probe because
        // its measured viewport/transform telemetry remains useful for validating later fixes.
        private float _nextHudGeometryProbeV63 = 0f;
        private bool _hudGeometryHierarchyDumpedV63 = false;
        private Vector3 _hudProbePlayerBaseV63 = Vector3.zero;
        private bool _hudProbePlayerBaseCapturedV63 = false;

        // V64: one-shot render-state census for the GPS aperture. V63 proved ScreenBounds,
        // VitalsBackingCircle, ScreenMask and PlayerArrow are all rigid in eye space while the
        // Map subtree pans underneath them. If that panning is visible outside the intended
        // aperture, the next suspect is native UGUI clipping/mask state, not anchoring.
        private bool _minimapRenderStateDumpedV64 = false;

        // V65: V64 proved the native GPS aperture has a real UnityEngine.UI.Mask, while its
        // moving Map child still exposed Default UI Material and visibly escaped the aperture.
        // Refresh the stock stencil/masking graph once after WorldSpace conversion. No manual
        // HUD drawing, no extra camera, and no per-eye Canvas rebuild.
        private bool _gpsStencilRefreshStartedV65 = false;
        private bool _gpsStencilRefreshDoneV65 = false;

        private bool _vitalsEndOfFrameCoroutineStarted = false;

        private bool _vitalsColorFlashDone = false;

        /// <summary>
        /// A genuinely different test from anything tried before: instead of moving objects and
        /// guessing whether the visible ring follows, directly force every rendering component
        /// under both remaining candidates to a dramatic, unmistakable color - a different one for
        /// each object, so the user can identify which one (if either) is the real visible ring
        /// just by looking at its color, without any ambiguity about position/distance/duplicates.
        /// Handles both Graphic (Canvas UI, for ToggleableHUD/PlayerVitalsUi) and Renderer (plain
        /// world objects, for PlayerVitalsUiWorldSpace, confirmed to have no RectTransform) since
        /// the two candidates use different rendering component types. One-shot - fires once and
        /// stays applied, doesn't need to keep re-running.
        /// </summary>
        private void UpdateVitalsColorFlashTest()
        {
            if (_vitalsColorFlashDone) return;
            if (_playerVitalsUiTransf == null) return;
            GameObject altGo = GameObject.Find("PlayerStandin/PlayerUI/PlayerVitalsUiWorldSpace");
            if (altGo == null) return;
            _vitalsColorFlashDone = true;

            int graphicsColored = 0;
            foreach (var g in _playerVitalsUiTransf.GetComponentsInChildren<Graphic>(true))
            {
                if (g == null) continue;
                g.color = Color.red;
                graphicsColored++;
            }

            int renderersColored = 0;
            foreach (var r in altGo.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.material == null) continue;
                if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", Color.blue);
                if (r.material.HasProperty("_Color")) r.material.SetColor("_Color", Color.blue);
                renderersColored++;
            }

            MelonLogger.Msg($"[VRPlayer][VitalsColorFlashTest] ToggleableHUD/PlayerVitalsUi -> RED ({graphicsColored} Graphics found). PlayerVitalsUiWorldSpace -> BLUE ({renderersColored} Renderers found).");
        }

        private void EnsureVitalsEndOfFrameCoroutine()
        {
            if (_vitalsEndOfFrameCoroutineStarted) return;
            _vitalsEndOfFrameCoroutineStarted = true;
            MelonCoroutines.Start(VitalsPositionEndOfFrameLoop());
        }


        private IEnumerator VitalsPositionEndOfFrameLoop()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();
                UpdateVitalsPositionEndOfFrame();
                UpdateVitalsWorldSpacePosition();
                UpdateVitalsWorldSpacePushUnderground();
                // UpdateVitalsColorFlashTest(); - disabled, confirmed its purpose (icons turned
                // blue), leaving it on would keep them stuck blue and make the position fix below
                // hard to judge cleanly
            }
        }

        private Transform _vitalsWorldSpaceAlt;
        private float _lastVitalsAltLogTime = 0f;

        public static float VitalsWorldSpaceOffsetRight = 0.20f; // meters
        public static float VitalsWorldSpaceOffsetUp = 0.18f; // meters

        // Position-based fix abandoned: touching this object's transform reliably produced a
        // giant, distorted duplicate rising from the horizon (confirmed twice, both times exactly
        // when this ran) with zero movement on the actual small icons. User's own observation from
        // flying near it confirmed it's genuinely a large-scale world object, likely the same one
        // as a separate pre-existing "static giant ring near a mountain" bug - not a small HUD
        // element that merely looked wrong when moved. The small icons are very likely rendered via
        // a completely separate mechanism that just happens to share the same color/material,
        // confirmed by the color-flash test turning the icons blue with no giant ring present at
        // all. Kept disabled - do not re-enable without a new reason to believe this object is
        // actually the small icons rather than the giant duplicate.
        public static bool EnableVitalsWorldSpaceFix = false;

        // REVISED per user's own idea: deactivating this object entirely broke the small status
        // icons - they froze at stale values and stopped responding to real game state changes
        // (eating didn't change hunger, etc.), colors also stuck wrong. Strong evidence this
        // REVISED per user's own analysis of the actual test history: POSITION changes to this
        // object (moving it near the player) never broke the small icons at all - they just didn't
        // move, and the giant duplicate appeared where we put it. It was specifically the
        // SCALE-down and DEACTIVATE attempts that broke icon function (froze/vanished). That's a
        // real, meaningful distinction in the data - position writes alone are confirmed safe for
        // icon function; scale/active-state writes are not. Pushing this object deep underground
        // (position only, no scale/active-state touched) should hide the giant ring from view
        // without repeating either of the failure modes already confirmed.
        public static bool PushVitalsWorldSpaceUnderground = true;
        public static float VitalsWorldSpaceUndergroundOffset = 10000f; // meters straight down from its own native position - deep enough to be out of view regardless of terrain height at that location

        private Transform _vitalsWorldSpaceUndergroundTarget;
        private Vector3 _vitalsWorldSpaceNativePosition;
        private bool _vitalsWorldSpaceNativePositionCaptured = false;

        private void UpdateVitalsWorldSpacePushUnderground()
        {
            if (!PushVitalsWorldSpaceUnderground) return;
            if (_vitalsWorldSpaceUndergroundTarget == null)
            {
                GameObject go = GameObject.Find("PlayerStandin/PlayerUI/PlayerVitalsUiWorldSpace");
                if (go == null) return;
                _vitalsWorldSpaceUndergroundTarget = go.transform;
            }
            if (!_vitalsWorldSpaceNativePositionCaptured)
            {
                // Capture its own native (X,Z) - stays wherever it naturally lives horizontally,
                // only Y gets pushed down, rather than recentering around the player like the
                // earlier (wrong) head-relative test did.
                _vitalsWorldSpaceNativePosition = _vitalsWorldSpaceUndergroundTarget.position;
                _vitalsWorldSpaceNativePositionCaptured = true;
            }
            Vector3 target = _vitalsWorldSpaceNativePosition - new Vector3(0f, VitalsWorldSpaceUndergroundOffset, 0f);
            _vitalsWorldSpaceUndergroundTarget.position = target;
        }

        private void UpdateVitalsWorldSpacePosition()
        {
            if (!EnableVitalsWorldSpaceFix) return;
            if (StereoRender == null || StereoRender.Head == null) return;
            if (_vitalsWorldSpaceAlt == null)
            {
                GameObject go = GameObject.Find("PlayerStandin/PlayerUI/PlayerVitalsUiWorldSpace");
                if (go == null) return;
                _vitalsWorldSpaceAlt = go.transform;
                MelonLogger.Msg("[VRPlayer][VitalsAltDiag] Found PlayerVitalsUiWorldSpace - confirmed as the real rendered object, applying HUD-scale fix.");
            }

            Vector3 targetPos = StereoRender.Head.position
                + StereoRender.Head.right * VitalsWorldSpaceOffsetRight
                + StereoRender.Head.up * VitalsWorldSpaceOffsetUp;
            _vitalsWorldSpaceAlt.position = targetPos;

            if (DebugLogCanvasChanges && Time.time - _lastVitalsAltLogTime > 2f)
            {
                _lastVitalsAltLogTime = Time.time;
                MelonLogger.Msg($"[VRPlayer][VitalsAltDiag] target={targetPos} actual={_vitalsWorldSpaceAlt.position} activeInHierarchy={_vitalsWorldSpaceAlt.gameObject.activeInHierarchy}");
            }
        }

        /// <summary>
        /// MOVED here from the regular per-poll update - every other theory has been directly
        /// ruled out with real evidence (write confirmed correct and correctly scaled, no
        /// LayoutGroup, no clipping Mask/RectMask2D, every descendant confirmed on the same layer
        /// as the parent), yet the user still sees zero visual movement. The one thing never
        /// actually tested for vitals specifically is timing - minimap's diagnostic had this exact
        /// same gap once (a write that reads correct immediately after being set, but something
        /// else could still be resetting it LATER in the same frame, which a same-poll read would
        /// never catch). Running this at WaitForEndOfFrame, as late as Unity allows, closes that
        /// gap either way.
        /// </summary>
        private void UpdateVitalsPositionEndOfFrame()
        {
            if (UnifiedHudWorldPlaneV116.Enabled || NativeHudCompositorV114.Enabled) return;
            // V16: V14/V15 root-level PlayerVitalsUi transforms were the wrong presentation
            // target.  Keep this legacy coroutine harmless; the actual visible
            // VitalsRenderTexture is scaled/positioned immediately after the minimap in
            // UpdateMinimapPositionEndOfFrame(), so both are guaranteed to share one rendered
            // plane and one visual center.
            if (_playerVitalsUiTransf != null && _vitalsScaleTargetV14 == _playerVitalsUiTransf)
            {
                _playerVitalsUiTransf.localScale = _vitalsBaseScaleV14;
            }
        }

        private void EnsureVitalsRenderTextureV16()
        {
            if (_vitalsRenderTextureRtV16 != null) return;
            if (_screenSpaceCanvas == null) return;

            RectTransform best = null;
            int bestScore = int.MinValue;

            // Prefer the actual RawImage whose object/texture is explicitly vitals-named.  This
            // is the display quad fed by PlayerVitalsUiWorldSpace, not the source geometry.
            foreach (var raw in _screenSpaceCanvas.GetComponentsInChildren<RawImage>(true))
            {
                if (raw == null) continue;
                var rt = raw.GetComponent<RectTransform>();
                if (rt == null) continue;

                string n = (raw.name ?? "").ToLowerInvariant();
                string tn = raw.texture != null ? (raw.texture.name ?? "").ToLowerInvariant() : "";
                string path = GetHierarchyPath(raw.transform).ToLowerInvariant();
                int score = 0;
                if (n == "vitalsrendertexture") score += 10000;
                if (n.Contains("vital")) score += 3000;
                if (tn.Contains("vital")) score += 3000;
                if (path.Contains("playervitaluirendertexturegroup")) score += 5000;
                if (path.Contains("vital")) score += 1000;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = rt;
                }
            }

            // Fallback for builds where the display uses Image rather than RawImage: scan by
            // transform name/path only.
            if (best == null || bestScore <= 0)
            {
                foreach (Transform t in _screenSpaceCanvas.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null) continue;
                    string n = (t.name ?? "").ToLowerInvariant();
                    string path = GetHierarchyPath(t).ToLowerInvariant();
                    if (!n.Contains("vital") && !path.Contains("playervitaluirendertexturegroup")) continue;
                    var rt = t.GetComponent<RectTransform>();
                    if (rt == null) continue;
                    best = rt;
                    bestScore = 1;
                    if (n == "vitalsrendertexture") break;
                }
            }

            if (best == null || bestScore <= 0) return;

            _vitalsRenderTextureRtV16 = best;
            _vitalsRenderTextureBaseScaleV16 = best.localScale;
            _vitalsRenderTextureInstanceIdV16 = best.gameObject.GetInstanceID();
            _vitalsRenderTextureLoggedV16 = false;
        }

        private bool TryGetMinimapVisualCenterV16(out Vector3 centerWorld)
        {
            centerWorld = _PlayerMiniMapTransf != null ? _PlayerMiniMapTransf.position : Vector3.zero;
            if (_playerMiniMapCanvas == null) return _PlayerMiniMapTransf != null;

            if (_minimapStableFrameRtV60 == null && _PlayerMiniMapTransf != null)
            {
                // V65 clarification: V63 telemetry disproved our V62 interpretation.
                // VitalsBackingCircle, ScreenBounds, ScreenMask and PlayerArrow are ALL rigid in
                // eye space. The thing translating/rotating is MapRoot/Map underneath them, which
                // is normal minimap behavior that should be hidden by ScreenMask's stencil.
                // ScreenBounds remains the preferred ring anchor because it is the child frame at
                // the actual ScreenMask aperture center; this is an alignment choice, not because
                // VitalsBackingCircle itself moves.
                Transform stable = _PlayerMiniMapTransf.Find(
                    "GPSTrackerSystem/Canvas/ScreenMask/ScreenBounds");
                if (stable != null)
                    _minimapStableFrameRtV60 = stable.GetComponent<RectTransform>();

                // Fallbacks only. VitalsBackingCircle is useful evidence and is logged by the
                // V63 runtime probe, but it is no longer allowed to be the preferred anchor.
                if (_minimapStableFrameRtV60 == null)
                {
                    stable = _PlayerMiniMapTransf.Find(
                        "GPSTrackerSystem/Canvas/VitalsBackingCircle");
                    if (stable != null)
                        _minimapStableFrameRtV60 = stable.GetComponent<RectTransform>();
                }
                if (_minimapStableFrameRtV60 == null)
                    _minimapStableFrameRtV60 = _playerMiniMapCanvas.GetComponent<RectTransform>();
            }

            RectTransform frame = _minimapStableFrameRtV60;
            if (frame == null) return _PlayerMiniMapTransf != null;

            Vector2 size = frame.rect.size;
            Vector2 centerFromPivot = new Vector2(
                (0.5f - frame.pivot.x) * size.x,
                (0.5f - frame.pivot.y) * size.y);
            centerWorld = frame.TransformPoint(
                new Vector3(centerFromPivot.x, centerFromPivot.y, 0f));

            if (!_minimapStableFrameLoggedV60)
            {
                _minimapStableFrameLoggedV60 = true;
                MelonLogger.Warning(
                    $"[VRPlayer][AER V66][HudLayout] RING anchor={GetHierarchyPath(frame)} " +
                    $"rect={frame.rect.size} pivot={frame.pivot} centerWorld={centerWorld}. " +
                    "ScreenBounds is the aperture-center anchor; V63 proved VitalsBackingCircle is also rigid, while MapRoot/Map alone pan/rotate underneath ScreenMask.");
            }
            return true;
        }

        private void UpdateVitalsRenderTextureV16()
        {
            EnsureVitalsRenderTextureV16();
            if (_vitalsRenderTextureRtV16 == null) return;
            if (!TryGetMinimapVisualCenterV16(out Vector3 minimapCenterWorld)) return;

            RectTransform rt = _vitalsRenderTextureRtV16;
            Vector3 targetScale = new Vector3(
                _vitalsRenderTextureBaseScaleV16.x * VitalsUiScaleMultiplier,
                _vitalsRenderTextureBaseScaleV16.y * VitalsUiScaleMultiplier,
                _vitalsRenderTextureBaseScaleV16.z);
            rt.localScale = targetScale;

            // Match the minimap plane exactly, then translate the ACTUAL rendered ring's visual
            // center onto the ACTUAL rendered map's visual center.  This avoids all assumptions
            // about the two systems' unrelated anchors/pivots/parent hierarchies.
            if (_PlayerMiniMapTransf != null) rt.rotation = _PlayerMiniMapTransf.rotation;
            Vector2 size = rt.rect.size;
            Vector2 centerFromPivot = new Vector2(
                (0.5f - rt.pivot.x) * size.x,
                (0.5f - rt.pivot.y) * size.y);
            Vector3 ringCenterWorld = rt.TransformPoint(new Vector3(centerFromPivot.x, centerFromPivot.y, 0f));
            rt.position += (minimapCenterWorld - ringCenterWorld);

            if (!_vitalsRenderTextureLoggedV16)
            {
                _vitalsRenderTextureLoggedV16 = true;
                var raw = rt.GetComponent<RawImage>();
                MelonLogger.Msg($"[VRPlayer][VitalsDisplayV16] target={GetHierarchyPath(rt)} instanceId={_vitalsRenderTextureInstanceIdV16} rawImage={(raw != null)} texture={(raw != null && raw.texture != null ? raw.texture.name : "null")} baseScale={_vitalsRenderTextureBaseScaleV16} targetScale={targetScale} rect={rt.rect.size} pivot={rt.pivot}");
            }
        }

        // ZER0INT V60: called at RenderPipelineManager.beginCameraRendering after
        // StereoRender applies the freshest HMD pose and immediately before the active eye draws.
        // This makes the captured UGUI geometry and eye camera share one pose/time.
        internal void RefreshHudLayoutForRenderV60()
        {
            if (!canUpdate || StereoRender == null || StereoRender.Head == null) return;

            // V116: update the shared native HUD plane immediately before an eye render. This is
            // the same timing hook that kept the older world-space HUD close to the latest HMD pose,
            // but now map/ring placement is solved from the native authored RectTransform pivots.
            if (UnifiedHudWorldPlaneV116.Enabled)
            {
                UnifiedHudWorldPlaneV116.Tick(
                    _screenSpaceCanvas,
                    _playerMiniMapCanvas,
                    _PlayerMiniMapTransf,
                    UsedCamera,
                    StereoRender.Head);
                return;
            }

            if (NativeHudCompositorV114.Enabled) return;
            UpdateCanvasPositionForVR();
            UpdateMinimapPositionEndOfFrame();
        }

        // V72: describe simple HUD support geometry in the CURRENT source eye.
        // meta.x = eye-space depth in meters; meta.y = shape (0=rect, 1=circle/ellipse).
        // We intentionally use coarse support surfaces for the first proof:
        //   p0 = GPS ScreenMask circle
        //   p1 = visible VitalsRenderTexture circle
        //   p2 = union of currently-active MessageList Graphics as a rectangle
        // Nothing here changes visible UI state.
        internal int GetHudDepthProxiesV72(
            Camera sourceCamera,
            out Vector4 rect0, out Vector4 meta0,
            out Vector4 rect1, out Vector4 meta1,
            out Vector4 rect2, out Vector4 meta2,
            out Vector4 rect3, out Vector4 meta3)
        {
            rect0 = Vector4.zero; meta0 = Vector4.zero;
            rect1 = Vector4.zero; meta1 = Vector4.zero;
            rect2 = Vector4.zero; meta2 = Vector4.zero;
            rect3 = Vector4.zero; meta3 = Vector4.zero;

            if (sourceCamera == null)
                return 0;

            int count = 0;
            Vector4 rect;
            float depth;

            // GPS aperture / map support disc.
            RectTransform gpsMask = null;
            if (_PlayerMiniMapTransf != null)
            {
                Transform t = _PlayerMiniMapTransf.Find("GPSTrackerSystem/Canvas/ScreenMask");
                if (t != null) gpsMask = t.GetComponent<RectTransform>();
            }

            if (TryProjectRectAndDepthV72(sourceCamera, gpsMask, 0.006f, out rect, out depth))
            {
                AssignHudDepthProxyV72(count++, rect, new Vector4(depth, 1f, 0f, 0f),
                    ref rect0, ref meta0, ref rect1, ref meta1, ref rect2, ref meta2, ref rect3, ref meta3);
            }

            // V74 status ring: the user's baked DISC radius and RING main-circle radius are both
            // exactly 0.01 in canonical HUD-local units. Therefore use the SAME ScreenMask center
            // and diameter for both. This guarantees the authored ring origin lands on the map
            // center and the little appendage bulbs extend outward exactly as modeled.
            if (TryProjectRectAndDepthV72(sourceCamera, gpsMask, 0.006f, out rect, out depth))
            {
                AssignHudDepthProxyV72(count++, rect, new Vector4(depth, 1f, 1f, 0f),
                    ref rect0, ref meta0, ref rect1, ref meta1, ref rect2, ref meta2, ref rect3, ref meta3);
            }

            // MessageList: use the union of ACTIVE child Graphics rather than the enormous authored
            // 100x2010 parent rectangle. This gives the current text/icons a compact depth bar.
            if (TryProjectActiveGraphicUnionV72(sourceCamera, _messageListTransf, 0.004f, out rect, out depth))
            {
                AssignHudDepthProxyV72(count++, rect, new Vector4(depth, 0f, 2f, 0f),
                    ref rect0, ref meta0, ref rect1, ref meta1, ref rect2, ref meta2, ref rect3, ref meta3);
            }

            return Mathf.Min(count, 4);
        }

        private static void AssignHudDepthProxyV72(
            int index, Vector4 rect, Vector4 meta,
            ref Vector4 rect0, ref Vector4 meta0,
            ref Vector4 rect1, ref Vector4 meta1,
            ref Vector4 rect2, ref Vector4 meta2,
            ref Vector4 rect3, ref Vector4 meta3)
        {
            if (index == 0) { rect0 = rect; meta0 = meta; }
            else if (index == 1) { rect1 = rect; meta1 = meta; }
            else if (index == 2) { rect2 = rect; meta2 = meta; }
            else if (index == 3) { rect3 = rect; meta3 = meta; }
        }

        private static bool TryProjectRectAndDepthV72(
            Camera cam,
            RectTransform rt,
            float margin,
            out Vector4 bounds,
            out float eyeDepth)
        {
            bounds = Vector4.zero;
            eyeDepth = 0f;
            if (!TryProjectRectToTopLeftUvV69(cam, rt, margin, out bounds) || rt == null)
                return false;

            Vector3 center = RectCenterWorldV63(rt);
            Vector3 vp = cam.WorldToViewportPoint(center);
            if (vp.z <= 0.001f)
                return false;

            eyeDepth = vp.z;
            return true;
        }

        private static bool TryProjectActiveGraphicUnionV72(
            Camera cam,
            Transform root,
            float margin,
            out Vector4 bounds,
            out float eyeDepth)
        {
            bounds = Vector4.zero;
            eyeDepth = 0f;
            if (cam == null || root == null || !root.gameObject.activeInHierarchy)
                return false;

            bool have = false;
            float xmin = 1f, ymin = 1f, xmax = 0f, ymax = 0f;
            float depthSum = 0f;
            int depthCount = 0;

            var graphics = root.GetComponentsInChildren<Graphic>(true);
            if (graphics == null)
                return false;

            for (int i = 0; i < graphics.Length; i++)
            {
                Graphic g = graphics[i];
                if (g == null || !g.enabled || !g.gameObject.activeInHierarchy)
                    continue;

                RectTransform rt = g.GetComponent<RectTransform>();
                Vector4 r;
                float d;
                if (!TryProjectRectAndDepthV72(cam, rt, 0f, out r, out d))
                    continue;

                xmin = Mathf.Min(xmin, r.x);
                ymin = Mathf.Min(ymin, r.y);
                xmax = Mathf.Max(xmax, r.z);
                ymax = Mathf.Max(ymax, r.w);
                depthSum += d;
                depthCount++;
                have = true;
            }

            if (!have || depthCount <= 0)
                return false;

            bounds = new Vector4(
                Mathf.Clamp01(xmin - margin),
                Mathf.Clamp01(ymin - margin),
                Mathf.Clamp01(xmax + margin),
                Mathf.Clamp01(ymax + margin));
            eyeDepth = depthSum / depthCount;
            return (bounds.z - bounds.x) > 0.001f && (bounds.w - bounds.y) > 0.001f;
        }

        // V76 dense planar HUD mapping. Order is fixed and matches compiled meshes:
        //   0 = GPS disc
        //   1 = status ring (same authored main-circle center/diameter)
        //   2 = active MessageList rectangle
        // Source and target are projections of the SAME live HUD objects into the two eye cameras.
        internal int GetHudDenseRemapPairsV76(
            Camera sourceCamera,
            Camera targetCamera,
            out Vector4 sourceRect0, out Vector4 targetRect0,
            out Vector4 sourceRect1, out Vector4 targetRect1,
            out Vector4 sourceRect2, out Vector4 targetRect2)
        {
            sourceRect0 = Vector4.zero; targetRect0 = Vector4.zero;
            sourceRect1 = Vector4.zero; targetRect1 = Vector4.zero;
            sourceRect2 = Vector4.zero; targetRect2 = Vector4.zero;

            if (sourceCamera == null || targetCamera == null)
                return 0;

            RectTransform gpsMask = null;
            if (_PlayerMiniMapTransf != null)
            {
                Transform t = _PlayerMiniMapTransf.Find("GPSTrackerSystem/Canvas/ScreenMask");
                if (t != null) gpsMask = t.GetComponent<RectTransform>();
            }

            Vector4 s, tRect;
            float ignoredDepth;

            // GPS disc.
            if (!TryProjectRectAndDepthV72(sourceCamera, gpsMask, 0.006f, out s, out ignoredDepth) ||
                !TryProjectRectAndDepthV72(targetCamera, gpsMask, 0.006f, out tRect, out ignoredDepth))
                return 0;
            sourceRect0 = s; targetRect0 = tRect;

            // Ring uses exactly the same base rect; its baked appendages extend beyond it.
            sourceRect1 = sourceRect0;
            targetRect1 = targetRect0;

            int count = 2;

            // MessageList active-graphic union. If unavailable, map/ring still work.
            Vector4 sMsg, tMsg;
            float d0, d1;
            if (TryProjectActiveGraphicUnionV72(sourceCamera, _messageListTransf, 0.004f, out sMsg, out d0) &&
                TryProjectActiveGraphicUnionV72(targetCamera, _messageListTransf, 0.004f, out tMsg, out d1))
            {
                sourceRect2 = sMsg;
                targetRect2 = tMsg;
                count = 3;
            }

            return count;
        }

        // V69: project only a few known, representative HUD regions into the CURRENT
        // source eye. The warp shader uses top-left UV convention, while Camera.WorldToViewportPoint
        // returns bottom-left viewport Y, so Y is explicitly flipped here.
        //
        // This is deliberately NOT an attempt at a production-quality UI mask. The diagnostic
        // flattens the background inside each rectangle too. We only need to know whether giving
        // these HUD pixels their actual ~0.5m plane depth kills the AER-only jitter.
        internal int GetHudDepthProbeRectsV69(
            Camera sourceCamera,
            out Vector4 rect0,
            out Vector4 rect1,
            out Vector4 rect2,
            out Vector4 rect3)
        {
            rect0 = Vector4.zero;
            rect1 = Vector4.zero;
            rect2 = Vector4.zero;
            rect3 = Vector4.zero;

            if (sourceCamera == null)
                return 0;

            RectTransform gpsMask = null;
            if (_PlayerMiniMapTransf != null)
            {
                Transform t = _PlayerMiniMapTransf.Find("GPSTrackerSystem/Canvas/ScreenMask");
                if (t != null) gpsMask = t as RectTransform;
            }

            RectTransform vitals = _vitalsRenderTextureRtV16;

            RectTransform message = null;
            if (_messageListTransf != null)
                message = _messageListTransf.GetComponent<RectTransform>();
            else if (_screenSpaceCanvas != null)
            {
                Transform t = _screenSpaceCanvas.transform.Find("ToggleableHUD/MessageList");
                if (t != null) message = t.GetComponent<RectTransform>();
            }

            int count = 0;
            Vector4 projected;

            // Expand GPS/ring slightly so anti-aliased edges are safely inside the probe.
            if (TryProjectRectToTopLeftUvV69(sourceCamera, gpsMask, 0.010f, out projected))
            {
                rect0 = projected;
                count++;
            }

            if (TryProjectRectToTopLeftUvV69(sourceCamera, vitals, 0.010f, out projected))
            {
                if (count == 0) rect0 = projected;
                else if (count == 1) rect1 = projected;
                else if (count == 2) rect2 = projected;
                else rect3 = projected;
                count++;
            }

            if (TryProjectRectToTopLeftUvV69(sourceCamera, message, 0.006f, out projected))
            {
                if (count == 0) rect0 = projected;
                else if (count == 1) rect1 = projected;
                else if (count == 2) rect2 = projected;
                else rect3 = projected;
                count++;
            }

            return Mathf.Min(count, 4);
        }

        private static bool TryProjectRectToTopLeftUvV69(
            Camera cam,
            RectTransform rt,
            float margin,
            out Vector4 bounds)
        {
            bounds = Vector4.zero;
            if (cam == null || rt == null || !rt.gameObject.activeInHierarchy)
                return false;

            Rect r = rt.rect;
            Vector3[] worldCorners = new Vector3[4]
            {
                rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)),
                rt.TransformPoint(new Vector3(r.xMax, r.yMin, 0f)),
                rt.TransformPoint(new Vector3(r.xMin, r.yMax, 0f)),
                rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0f))
            };

            float xmin = float.PositiveInfinity;
            float xmax = float.NegativeInfinity;
            float yminTopLeft = float.PositiveInfinity;
            float ymaxTopLeft = float.NegativeInfinity;
            int inFront = 0;

            for (int i = 0; i < 4; i++)
            {
                Vector3 v = cam.WorldToViewportPoint(worldCorners[i]);
                if (v.z > 0.001f) inFront++;
                float topLeftY = 1f - v.y;
                xmin = Mathf.Min(xmin, v.x);
                xmax = Mathf.Max(xmax, v.x);
                yminTopLeft = Mathf.Min(yminTopLeft, topLeftY);
                ymaxTopLeft = Mathf.Max(ymaxTopLeft, topLeftY);
            }

            if (inFront != 4)
                return false;

            xmin = Mathf.Clamp01(xmin - margin);
            xmax = Mathf.Clamp01(xmax + margin);
            yminTopLeft = Mathf.Clamp01(yminTopLeft - margin);
            ymaxTopLeft = Mathf.Clamp01(ymaxTopLeft + margin);

            if (xmax - xmin < 0.001f || ymaxTopLeft - yminTopLeft < 0.001f)
                return false;

            bounds = new Vector4(xmin, yminTopLeft, xmax, ymaxTopLeft);
            return true;
        }

        private static Vector3 RectCenterWorldV63(RectTransform rt)
        {
            if (rt == null) return Vector3.zero;
            Rect r = rt.rect;
            return rt.TransformPoint(new Vector3(r.center.x, r.center.y, 0f));
        }

        private static string ViewportPointV63(Camera cam, Vector3 world)
        {
            if (cam == null) return "cam=null";
            Vector3 v = cam.WorldToViewportPoint(world);
            return $"({v.x:F3},{v.y:F3},z={v.z:F3})";
        }

        private static string RectViewportSpanV63(Camera cam, RectTransform rt)
        {
            if (cam == null || rt == null) return "n/a";
            Rect r = rt.rect;
            Vector3 bl = rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0f));
            Vector3 br = rt.TransformPoint(new Vector3(r.xMax, r.yMin, 0f));
            Vector3 tl = rt.TransformPoint(new Vector3(r.xMin, r.yMax, 0f));
            Vector3 tr = rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0f));
            Vector3 vbl = cam.WorldToViewportPoint(bl);
            Vector3 vbr = cam.WorldToViewportPoint(br);
            Vector3 vtl = cam.WorldToViewportPoint(tl);
            Vector3 vtr = cam.WorldToViewportPoint(tr);
            float xmin = Mathf.Min(Mathf.Min(vbl.x, vbr.x), Mathf.Min(vtl.x, vtr.x));
            float xmax = Mathf.Max(Mathf.Max(vbl.x, vbr.x), Mathf.Max(vtl.x, vtr.x));
            float ymin = Mathf.Min(Mathf.Min(vbl.y, vbr.y), Mathf.Min(vtl.y, vtr.y));
            float ymax = Mathf.Max(Mathf.Max(vbl.y, vbr.y), Mathf.Max(vtl.y, vtr.y));
            return $"x=[{xmin:F3},{xmax:F3}] span={xmax-xmin:F3} y=[{ymin:F3},{ymax:F3}] span={ymax-ymin:F3}";
        }

        private Transform FindDescendantByNameV63(Transform root, string exactName)
        {
            if (root == null || string.IsNullOrEmpty(exactName)) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name == exactName) return t;
            }
            return null;
        }

        private void LogTrackedMinimapNodeV63(string label, Transform t, Camera cam)
        {
            if (t == null)
            {
                MelonLogger.Msg($"[VRPlayer][AER V66][MinimapMotion] {label}=NOT_FOUND");
                return;
            }

            RectTransform rt = t.GetComponent<RectTransform>();
            Vector3 center = rt != null ? RectCenterWorldV63(rt) : t.position;
            Vector3 canvasLocal = _playerMiniMapCanvas != null
                ? _playerMiniMapCanvas.transform.InverseTransformPoint(center)
                : Vector3.zero;
            string rectInfo = rt != null
                ? $" rect={rt.rect.size} anchored={rt.anchoredPosition} pivot={rt.pivot}"
                : "";
            MelonLogger.Msg(
                $"[VRPlayer][AER V66][MinimapMotion] {label} path={GetHierarchyPath(t)} " +
                $"world={center} canvasLocal={canvasLocal} localPos={t.localPosition}{rectInfo} " +
                $"localEuler={t.localEulerAngles} worldEuler={t.eulerAngles} " +
                $"leftVP={ViewportPointV63(cam, center)}");
        }

        private void EnsureGpsStencilRefreshV65()
        {
            if (_gpsStencilRefreshStartedV65) return;
            _gpsStencilRefreshStartedV65 = true;
            MelonCoroutines.Start(GpsStencilRefreshV65());
        }

        private static string MaterialNameV65(Material m)
        {
            return m != null ? m.name : "null";
        }

        private IEnumerator GpsStencilRefreshV65()
        {
            // Let render-mode conversion and native GPS setup settle first. One-shot per scene.
            yield return null;
            yield return new WaitForEndOfFrame();

            if (_gpsStencilRefreshDoneV65) yield break;
            _gpsStencilRefreshDoneV65 = true;

            if (_PlayerMiniMapTransf == null || _playerMiniMapCanvas == null)
            {
                MelonLogger.Warning("[VRPlayer][AER V66][GPSMaskRefresh] skipped: minimap root/canvas missing.");
                yield break;
            }

            Transform screenMask = _PlayerMiniMapTransf.Find("GPSTrackerSystem/Canvas/ScreenMask");
            if (screenMask == null)
            {
                MelonLogger.Warning("[VRPlayer][AER V66][GPSMaskRefresh] skipped: ScreenMask missing.");
                yield break;
            }

            Mask mask = screenMask.GetComponent<Mask>();
            MaskableGraphic[] maskables = screenMask.GetComponentsInChildren<MaskableGraphic>(true);
            Graphic[] graphics = screenMask.GetComponentsInChildren<Graphic>(true);
            Transform mapTransform = FindDescendantByNameV63(screenMask, "Map");
            MaskableGraphic mapGraphic = mapTransform != null ? mapTransform.GetComponent<MaskableGraphic>() : null;

            int stencilBefore = -1;
            string mapMatBefore = "n/a";
            bool mapMaskableBefore = false;
            if (mapGraphic != null)
            {
                try
                {
                    stencilBefore = MaskUtilities.GetStencilDepth(mapGraphic.transform, _playerMiniMapCanvas.transform);
                    mapMatBefore = MaterialNameV65(mapGraphic.materialForRendering);
                    mapMaskableBefore = mapGraphic.maskable;
                }
                catch { }
            }

            MelonLogger.Warning(
                $"[VRPlayer][AER V66][GPSMaskRefresh] BEFORE mask={(mask != null ? $"enabled={mask.enabled} show={mask.showMaskGraphic}" : "none")} " +
                $"maskables={maskables.Length} graphics={graphics.Length} mapMaskable={mapMaskableBefore} " +
                $"mapStencilDepth={stencilBefore} mapMaterialForRendering={mapMatBefore}");

            // Re-arm the native Mask after ScreenSpace -> WorldSpace conversion, then explicitly
            // ask each stock MaskableGraphic to recompute its parent stencil depth/material.
            if (mask != null)
            {
                bool wasEnabled = mask.enabled;
                if (wasEnabled) mask.enabled = false;
                mask.enabled = wasEnabled;
            }

            foreach (MaskableGraphic mg in maskables)
            {
                if (mg == null) continue;
                try
                {
                    mg.RecalculateMasking();
                    mg.SetMaterialDirty();
                    mg.SetVerticesDirty();
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[VRPlayer][AER V66][GPSMaskRefresh] refresh failed on {GetHierarchyPath(mg.transform)}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // V60's forced Canvas rebuild coincided with correctly stationary/clipped GPS output.
            // Do it ONCE here after the corrected HUD sizes are already established, not once per
            // eye as V60 did. Native CanvasRenderer remains the only renderer of the HUD.
            Canvas.ForceUpdateCanvases();

            int stencilAfter = -1;
            string mapMatAfter = "n/a";
            bool mapMaskableAfter = false;
            if (mapGraphic != null)
            {
                try
                {
                    stencilAfter = MaskUtilities.GetStencilDepth(mapGraphic.transform, _playerMiniMapCanvas.transform);
                    mapMatAfter = MaterialNameV65(mapGraphic.materialForRendering);
                    mapMaskableAfter = mapGraphic.maskable;
                }
                catch { }
            }

            MelonLogger.Warning(
                $"[VRPlayer][AER V66][GPSMaskRefresh] AFTER mapMaskable={mapMaskableAfter} " +
                $"mapStencilDepth={stencilAfter} mapMaterialForRendering={mapMatAfter}. " +
                "Expected: stencil depth > 0 and a stencil-modified material.");
        }

        private void DumpMinimapRenderStateV64(Transform t, string label)
        {
            if (t == null)
            {
                MelonLogger.Warning($"[VRPlayer][AER V66][MaskState] {label}=NOT_FOUND");
                return;
            }

            string componentTypes;
            try
            {
                componentTypes = string.Join(",", t.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().FullName));
            }
            catch
            {
                componentTypes = "<component-enumeration-failed>";
            }

            Mask mask = t.GetComponent<Mask>();
            RectMask2D rectMask = t.GetComponent<RectMask2D>();
            Graphic graphic = t.GetComponent<Graphic>();
            MaskableGraphic maskableGraphic = t.GetComponent<MaskableGraphic>();
            CanvasRenderer cr = t.GetComponent<CanvasRenderer>();

            string maskableInfo = "none";
            if (maskableGraphic != null)
            {
                int stencilDepth = -1;
                try
                {
                    stencilDepth = _playerMiniMapCanvas != null
                        ? MaskUtilities.GetStencilDepth(maskableGraphic.transform, _playerMiniMapCanvas.transform)
                        : -1;
                }
                catch { }
                maskableInfo = $"maskable={maskableGraphic.maskable} isMaskingGraphic={maskableGraphic.isMaskingGraphic} stencilDepth={stencilDepth}";
            }

            string graphicInfo = "none";
            if (graphic != null)
            {
                string mat = graphic.material != null ? graphic.material.name : "null";
                string matFor = "n/a";
                try { matFor = graphic.materialForRendering != null ? graphic.materialForRendering.name : "null"; }
                catch { matFor = "<read-failed>"; }
                string tex = graphic.mainTexture != null ? graphic.mainTexture.name : "null";
                graphicInfo = $"type={graphic.GetType().FullName} enabled={graphic.enabled} raycast={graphic.raycastTarget} material={mat} materialForRendering={matFor} mainTexture={tex}";
            }

            string rendererInfo = cr != null
                ? $"cull={cr.cull} materialCount={cr.materialCount} absoluteDepth={cr.absoluteDepth}"
                : "none";

            MelonLogger.Warning(
                $"[VRPlayer][AER V66][MaskState] {label} path={GetHierarchyPath(t)} " +
                $"components=[{componentTypes}] " +
                $"Mask={(mask != null ? $"enabled={mask.enabled} showMaskGraphic={mask.showMaskGraphic}" : "none")} " +
                $"RectMask2D={(rectMask != null ? $"enabled={rectMask.enabled}" : "none")} " +
                $"MaskableGraphic=[{maskableInfo}] Graphic=[{graphicInfo}] CanvasRenderer=[{rendererInfo}]");
        }

        private void DumpMinimapRenderStateOnceV64()
        {
            if (_minimapRenderStateDumpedV64 || _PlayerMiniMapTransf == null) return;
            _minimapRenderStateDumpedV64 = true;

            MelonLogger.Warning("[VRPlayer][AER V66][MaskState] BEGIN native GPS aperture/render-state census.");
            DumpMinimapRenderStateV64(_PlayerMiniMapTransf.Find("GPSTrackerSystem/Canvas/ScreenMask"), "ScreenMask");
            DumpMinimapRenderStateV64(_PlayerMiniMapTransf.Find("GPSTrackerSystem/Canvas/ScreenMask/ScreenBounds"), "ScreenBounds");
            DumpMinimapRenderStateV64(FindDescendantByNameV63(_PlayerMiniMapTransf, "MapRotatorGroup"), "MapRotatorGroup");
            DumpMinimapRenderStateV64(FindDescendantByNameV63(_PlayerMiniMapTransf, "MapRoot"), "MapRoot");
            DumpMinimapRenderStateV64(FindDescendantByNameV63(_PlayerMiniMapTransf, "Map"), "Map");
            DumpMinimapRenderStateV64(FindDescendantByNameV63(_PlayerMiniMapTransf, "PlayerArrow"), "PlayerArrow");
            DumpMinimapRenderStateV64(_PlayerMiniMapTransf.Find("MiniMap"), "PlayerMiniMap/MiniMap sibling");
            MelonLogger.Warning("[VRPlayer][AER V66][MaskState] END native GPS aperture/render-state census.");
        }

        /// <summary>
        /// V63 diagnostic only. We already have enough evidence that raw UGUI replay is the wrong
        /// abstraction boundary; this instead measures the native geometry that Unity is actually
        /// presenting. Two questions are answered in one low-rate probe:
        ///   A) Which GPS node translates when LocalPlayer moves/jumps?
        ///   B) How wide is ScreenSpaceCanvas in the actual VR eye viewport?
        /// No state is changed here.
        /// </summary>
        private void LogHudGeometryProbeV63()
        {
            if (Time.unscaledTime < _nextHudGeometryProbeV63) return;
            _nextHudGeometryProbeV63 = Time.unscaledTime + 0.50f;

            Camera cam = StereoRender != null ? StereoRender.LeftCam : null;
            DumpMinimapRenderStateOnceV64();

            if (!_hudGeometryHierarchyDumpedV63 && _PlayerMiniMapTransf != null)
            {
                _hudGeometryHierarchyDumpedV63 = true;
                MelonLogger.Warning("[VRPlayer][AER V66][Probe] BEGIN one-time relevant minimap RectTransform census.");
                foreach (var rt in _PlayerMiniMapTransf.GetComponentsInChildren<RectTransform>(true))
                {
                    if (rt == null) continue;
                    string n = rt.name != null ? rt.name.ToLowerInvariant() : "";
                    if (n.Contains("map") || n.Contains("screen") || n.Contains("vital") ||
                        n.Contains("arrow") || n.Contains("player") || n.Contains("root"))
                    {
                        MelonLogger.Msg(
                            $"[VRPlayer][AER V66][ProbeTree] path={GetHierarchyPath(rt)} " +
                            $"rect={rt.rect.size} anchored={rt.anchoredPosition} localPos={rt.localPosition} " +
                            $"pivot={rt.pivot} scale={rt.localScale}");
                    }
                }
                MelonLogger.Warning("[VRPlayer][AER V66][Probe] END one-time relevant minimap RectTransform census.");
            }

            if (LocalPlayer != null)
            {
                if (!_hudProbePlayerBaseCapturedV63)
                {
                    _hudProbePlayerBaseV63 = LocalPlayer.transform.position;
                    _hudProbePlayerBaseCapturedV63 = true;
                }
                Vector3 dp = LocalPlayer.transform.position - _hudProbePlayerBaseV63;
                MelonLogger.Msg(
                    $"[VRPlayer][AER V66][PlayerMotion] player={LocalPlayer.transform.position} deltaFromProbeStart={dp} " +
                    $"head={(StereoRender != null && StereoRender.Head != null ? StereoRender.Head.position.ToString() : "null")}");
            }

            Transform screenBounds = _PlayerMiniMapTransf != null
                ? _PlayerMiniMapTransf.Find("GPSTrackerSystem/Canvas/ScreenMask/ScreenBounds")
                : null;
            Transform vitalsBacking = _PlayerMiniMapTransf != null
                ? _PlayerMiniMapTransf.Find("GPSTrackerSystem/Canvas/VitalsBackingCircle")
                : null;
            Transform screenMask = _PlayerMiniMapTransf != null
                ? _PlayerMiniMapTransf.Find("GPSTrackerSystem/Canvas/ScreenMask")
                : null;

            LogTrackedMinimapNodeV63("ScreenBounds", screenBounds, cam);
            LogTrackedMinimapNodeV63("VitalsBackingCircle", vitalsBacking, cam);
            LogTrackedMinimapNodeV63("ScreenMask", screenMask, cam);
            LogTrackedMinimapNodeV63("Map", FindDescendantByNameV63(_PlayerMiniMapTransf, "Map"), cam);
            LogTrackedMinimapNodeV63("SurfaceMap", FindDescendantByNameV63(_PlayerMiniMapTransf, "SurfaceMap"), cam);
            LogTrackedMinimapNodeV63("MapRoot", FindDescendantByNameV63(_PlayerMiniMapTransf, "MapRoot"), cam);
            LogTrackedMinimapNodeV63("MapRotatorGroup", FindDescendantByNameV63(_PlayerMiniMapTransf, "MapRotatorGroup"), cam);
            LogTrackedMinimapNodeV63("PlayerArrow", FindDescendantByNameV63(_PlayerMiniMapTransf, "PlayerArrow"), cam);
            LogTrackedMinimapNodeV63("PlayerMiniMap/MiniMap sibling", _PlayerMiniMapTransf != null ? _PlayerMiniMapTransf.Find("MiniMap") : null, cam);

            // V66 decisive geometry check: compare the ACTUAL rendered vitals-ring display center
            // against the fixed GPS aperture center in BOTH eye projections. If these deltas stay
            // ~zero while the headset still shows separation, the fault is no longer transform
            // placement; it is in render timing/AER reconstruction of transparent HUD pixels.
            EnsureVitalsRenderTextureV16();
            RectTransform screenBoundsRtV66 = screenBounds != null ? screenBounds.GetComponent<RectTransform>() : null;
            if (_vitalsRenderTextureRtV16 != null && screenBoundsRtV66 != null)
            {
                Vector3 mapCenterV66 = RectCenterWorldV63(screenBoundsRtV66);
                Vector3 ringCenterV66 = RectCenterWorldV63(_vitalsRenderTextureRtV16);
                Vector3 mapLeftV66 = cam != null ? cam.WorldToViewportPoint(mapCenterV66) : Vector3.zero;
                Vector3 ringLeftV66 = cam != null ? cam.WorldToViewportPoint(ringCenterV66) : Vector3.zero;
                Camera rightCamV66 = StereoRender != null ? StereoRender.RightCam : null;
                Vector3 mapRightV66 = rightCamV66 != null ? rightCamV66.WorldToViewportPoint(mapCenterV66) : Vector3.zero;
                Vector3 ringRightV66 = rightCamV66 != null ? rightCamV66.WorldToViewportPoint(ringCenterV66) : Vector3.zero;
                Vector2 deltaLeftV66 = new Vector2(ringLeftV66.x - mapLeftV66.x, ringLeftV66.y - mapLeftV66.y);
                Vector2 deltaRightV66 = new Vector2(ringRightV66.x - mapRightV66.x, ringRightV66.y - mapRightV66.y);
                MelonLogger.Warning(
                    $"[VRPlayer][AER V66][RingVsMap] mapWorld={mapCenterV66} ringWorld={ringCenterV66} " +
                    $"worldDeltaMm={(ringCenterV66 - mapCenterV66).magnitude * 1000f:F2} " +
                    $"leftMap=({mapLeftV66.x:F3},{mapLeftV66.y:F3}) leftRing=({ringLeftV66.x:F3},{ringLeftV66.y:F3}) " +
                    $"leftDelta=({deltaLeftV66.x:F4},{deltaLeftV66.y:F4}) " +
                    $"rightMap=({mapRightV66.x:F3},{mapRightV66.y:F3}) rightRing=({ringRightV66.x:F3},{ringRightV66.y:F3}) " +
                    $"rightDelta=({deltaRightV66.x:F4},{deltaRightV66.y:F4})");
            }

            if (_screenSpaceCanvas != null)
            {
                RectTransform hud = _screenSpaceCanvas.GetComponent<RectTransform>();
                if (hud != null)
                {
                    MelonLogger.Warning(
                        $"[VRPlayer][AER V66][HudFrustum] Screen={Screen.width}x{Screen.height} " +
                        $"canvasRect={hud.rect.size} sizeDelta={hud.sizeDelta} pivot={hud.pivot} " +
                        $"localScale={hud.localScale} lossyScale={hud.lossyScale} worldPos={hud.position} " +
                        $"leftEyeViewport={RectViewportSpanV63(cam, hud)}");
                }

                Transform messageList = _screenSpaceCanvas.transform.Find("ToggleableHUD/MessageList");
                if (messageList == null)
                    messageList = FindDescendantByNameV63(_screenSpaceCanvas.transform, "MessageList");
                if (messageList != null)
                {
                    RectTransform mrt = messageList.GetComponent<RectTransform>();
                    Vector3 c = mrt != null ? RectCenterWorldV63(mrt) : messageList.position;
                    MelonLogger.Msg(
                        $"[VRPlayer][AER V66][HudNode] MessageList center={c} leftVP={ViewportPointV63(cam,c)} " +
                        $"anchored={(mrt != null ? mrt.anchoredPosition.ToString() : "n/a")}");
                }

                Transform worldHud = FindDescendantByNameV63(_screenSpaceCanvas.transform, "WorldScreenSpaceElements");
                if (worldHud != null)
                {
                    RectTransform wrt = worldHud.GetComponent<RectTransform>();
                    Vector3 c = wrt != null ? RectCenterWorldV63(wrt) : worldHud.position;
                    MelonLogger.Msg(
                        $"[VRPlayer][AER V66][HudNode] WorldScreenSpaceElements center={c} leftVP={ViewportPointV63(cam,c)} " +
                        $"anchored={(wrt != null ? wrt.anchoredPosition.ToString() : "n/a")}");
                }
            }
        }

        private void EnsureMinimapEndOfFrameCoroutine()
        {
            if (_minimapEndOfFrameCoroutineStarted) return;
            _minimapEndOfFrameCoroutineStarted = true;
            MelonCoroutines.Start(MinimapPositionEndOfFrameLoop());
        }

        private IEnumerator MinimapPositionEndOfFrameLoop()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();
                UpdateMinimapPositionEndOfFrame();
            }
        }

        /// <summary>
        /// MOVED here from the regular per-poll UpdateCanvasPositionForVR() - direct diagnostic
        /// logging proved the position code itself is completely correct (offset.y consistently
        /// positive/upward, confirmed every single sample) and nothing else in the file touches
        /// this canvas's position, yet the user saw zero visual change. That specific combination
        /// - "logged as correct every time, never visually manifests" - already happened once
        /// before in this project (the WorldScreenSpaceElements icon-overlap saga) and the cause
        /// there was a native system re-asserting its own value AFTER the mod's write, later in
        /// the same frame, invisibly undoing it before the frame ever rendered. Running this at
        /// WaitForEndOfFrame - as late as Unity allows in a frame - means nothing native can still
        /// run afterward and win that race, the same fix that worked for that earlier case.
        /// </summary>
        private bool TryGetStereoViewportCenterDeltaYV112(Vector3 worldPoint, out float headUpMeters)
        {
            headUpMeters = 0f;
            if (StereoRender == null || StereoRender.Head == null) return false;

            Camera[] cams = { StereoRender.LeftCam, StereoRender.RightCam };
            Vector3 sumDelta = Vector3.zero;
            int count = 0;

            for (int i = 0; i < cams.Length; ++i)
            {
                Camera cam = cams[i];
                if (cam == null) continue;
                try
                {
                    Vector3 vp = cam.WorldToViewportPoint(worldPoint);
                    if (vp.z <= 0.01f || float.IsNaN(vp.z) || float.IsInfinity(vp.z))
                        continue;

                    // Keep the eye's CURRENT horizontal coordinate and depth; change Y only.
                    // This is exactly "same HUD, same X/Z, but put its visual centre at 50% height"
                    // in that eye's own asymmetric projection.
                    Vector3 target = cam.ViewportToWorldPoint(new Vector3(vp.x, 0.5f, vp.z));
                    sumDelta += target - worldPoint;
                    count++;
                }
                catch { }
            }

            if (count <= 0) return false;
            Vector3 averageDelta = sumDelta / count;
            headUpMeters = Vector3.Dot(averageDelta, StereoRender.Head.up);
            if (float.IsNaN(headUpMeters) || float.IsInfinity(headUpMeters))
                return false;

            // A correct projection-centering correction should be modest. Refuse a pathological
            // geometry result rather than launching the HUD out of view.
            headUpMeters = Mathf.Clamp(headUpMeters, -0.40f, 0.40f);
            return true;
        }

        private void ApplyProjectionAwareMinimapVerticalCenterV112()
        {
            if (!CenterMinimapVerticallyV112 || _PlayerMiniMapTransf == null) return;
            Vector3 centerBefore;
            if (!TryGetMinimapVisualCenterV16(out centerBefore)) return;

            float shiftUp;
            if (!TryGetStereoViewportCenterDeltaYV112(centerBefore, out shiftUp)) return;

            _PlayerMiniMapTransf.position += StereoRender.Head.up * shiftUp;

            if (!_minimapVerticalCenterLoggedV112)
            {
                _minimapVerticalCenterLoggedV112 = true;
                Vector3 centerAfter;
                TryGetMinimapVisualCenterV16(out centerAfter);
                Vector3 lvp = StereoRender.LeftCam != null ? StereoRender.LeftCam.WorldToViewportPoint(centerAfter) : Vector3.zero;
                Vector3 rvp = StereoRender.RightCam != null ? StereoRender.RightCam.WorldToViewportPoint(centerAfter) : Vector3.zero;
                MelonLogger.Warning(
                    $"[VRPlayer][V112 HUDCenterY] applied projection-aware vertical correction {shiftUp:F3}m; " +
                    $"post leftY={lvp.y:F3} rightY={rvp.y:F3}. V108 scale/anchor/right-offset remain unchanged.");
            }
        }

        private void UpdateMinimapPositionEndOfFrame()
        {
            // V113 compositor owns the final 2D HUD sheet. Never mutate the minimap's world
            // transform or independently re-center it while that native screen-space contract is active.
            if (UnifiedHudWorldPlaneV116.Enabled || NativeHudCompositorV114.Enabled) return;
            if (StereoRender == null || StereoRender.Head == null || UsedCamera == null) return;
            if (_playerMiniMapCanvas == null || _PlayerMiniMapTransf == null)
            {
                if (DebugLogCanvasChanges && Time.time - _lastMinimapPositionLogTime > 2f)
                {
                    _lastMinimapPositionLogTime = Time.time;
                    MelonLogger.Msg($"[VRPlayer][MinimapPositionDiag] SKIPPED - _playerMiniMapCanvas null={_playerMiniMapCanvas == null} _PlayerMiniMapTransf null={_PlayerMiniMapTransf == null}");
                }
                return;
            }

            Vector3 headRight = StereoRender.Head.right;
            Vector3 headUp = StereoRender.Head.up;
            Vector3 headForward = StereoRender.Head.forward;

            // NEW: three completely different Transform-based approaches (deeply-nested Canvas,
            // end-of-frame timing, outermost container) have all produced fully correct write
            // values with zero visual effect - ruling out timing races and parent-vs-child
            // targeting entirely. Reconsidering the premise itself: many games render a minimap
            // via a DEDICATED CAMERA with a fixed screen-corner viewport rect, a mechanism
            // completely independent of any Canvas/Transform positioning - if that's what's
            // happening here, nothing we've done could ever have had any effect, since we'd have
            // been moving an object that isn't what actually controls its on-screen position at
            // all. Checking directly for any Camera component anywhere under this minimap's own
            // hierarchy, logging its viewport rect if found.
            if (DebugLogCanvasChanges && Time.time - _lastMinimapCameraCheckTime > 3f)
            {
                _lastMinimapCameraCheckTime = Time.time;
                var minimapCameras = _PlayerMiniMapTransf.GetComponentsInChildren<Camera>(true);
                if (minimapCameras.Length == 0)
                {
                    MelonLogger.Msg("[VRPlayer][MinimapCameraDiag] No Camera component found anywhere under PlayerMiniMap's own hierarchy.");
                }
                foreach (var cam in minimapCameras)
                {
                    if (cam == null) continue;
                    MelonLogger.Msg($"[VRPlayer][MinimapCameraDiag] Found: {GetHierarchyPath(cam.transform)} enabled={cam.enabled} rect={cam.rect} targetTexture={(cam.targetTexture != null ? cam.targetTexture.name : "null")}");
                }

                // NEW: also directly logging the actual layer these objects are on - testing
                // whether the real rendered content might come through a DIFFERENT camera keyed
                // to a different layer than whatever our Canvas/container currently sit on.
                MelonLogger.Msg($"[VRPlayer][MinimapCameraDiag] Layers: PlayerMiniMap={_PlayerMiniMapTransf.gameObject.layer} ('{LayerMask.LayerToName(_PlayerMiniMapTransf.gameObject.layer)}') Canvas={_playerMiniMapCanvas.gameObject.layer} ('{LayerMask.LayerToName(_playerMiniMapCanvas.gameObject.layer)}')");

                // NEW: full child hierarchy dump with per-object layer - checking for any SIBLING
                // object under PlayerMiniMap (besides GPSTrackerSystem/Canvas) that might be the
                // actual visible widget the player sees, which we may never have found/touched.
                foreach (var t in _PlayerMiniMapTransf.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null) continue;
                    MelonLogger.Msg($"[VRPlayer][MinimapCameraDiag] Child: {GetHierarchyPath(t)} active={t.gameObject.activeSelf} layer={t.gameObject.layer} ('{LayerMask.LayerToName(t.gameObject.layer)}')");
                }
            }

            // REDUCED per user's insight: the minimap was very likely moving correctly as far
            // back as gamepadfix_52 (when MinimapForwardDistance was introduced), but sitting far
            // enough to the right to fall outside the visible FOV entirely - probably explains a
            // lot of the "no visible change" reports well before the layer investigation even
            // started. Pulling it back toward center; treat as a starting guess to tune further
            // once actually visible, was 0.335f.
            // CORRECTED: user's own confirmed-working values were never actually written back
            // into the code last round - I acknowledged them in conversation but the file still
            // had the prior guess. Fixed now.
            float MinimapOffsetRight = 0.20f; // + = Right, - = Left

            // REVERTED back to down per user's insight: the "up" flip was never actually properly
            // tested, since the minimap was invisible off to the side the whole time regardless of
            // up/down - meaning we have no real evidence "down" was ever actually occluded in the
            // first place. Testing the original direction now that it'll actually be visible,
            // before assuming "up" was ever needed at all.
            float MinimapOffsetUp = -0.16f; // confirmed-working value, same correction as above

            Vector3 minimapBasePosition = StereoRender.Head.position + headForward * MinimapForwardDistance;
            Vector3 offset = headRight * MinimapOffsetRight + headUp * MinimapOffsetUp;
            Vector3 finalPosition = minimapBasePosition + offset;

            Quaternion minidesiredRotation = Quaternion.LookRotation(headForward, headUp);
            if (_playerMiniMapCanvas.worldCamera != UsedCamera)
            {
                _playerMiniMapCanvas.worldCamera = UsedCamera;
                UsedCamera.depth = 100;
                UsedCamera.clearFlags = CameraClearFlags.Depth;
                UsedCamera.cullingMask = -1;
            }

            // FIXED: was setting _playerMiniMapCanvas.transform directly - that object sits TWO
            // levels deep (PlayerMiniMap/GPSTrackerSystem/Canvas). Confirmed via diagnostic that
            // this write's own computed values were always correct, even at end-of-frame timing,
            // yet never visually took effect - the name "GPSTrackerSystem" strongly suggests that
            // intermediate parent has its own native logic continuously repositioning itself
            // (plausibly tracking a physical GPS device model), which would silently drag any
            // change made to its child right back, regardless of write timing. Moving the
            // OUTERMOST container instead (_PlayerMiniMapTransf, i.e. PlayerMiniMap itself) -
            // nothing can override a parent's position via child-relative logic, so this carries
            // GPSTrackerSystem and Canvas along with it no matter what happens between them.
            _PlayerMiniMapTransf.SetPositionAndRotation(finalPosition, minidesiredRotation);

            // V112: change ONLY the vertical screen placement. The restored V108 outer-container
            // transform, minimap canvas scaling, ScreenBounds anchor and VitalsRenderTexture
            // centering all remain intact; this simply solves for normalized viewport Y=0.5 using
            // the two real asymmetric eye projections instead of another hard-coded metre offset.
            ApplyProjectionAwareMinimapVerticalCenterV112();

            // V16: the vitals/status ring is not PlayerVitalsUi geometry; it is a separate
            // RenderTexture display.  Update it AFTER the minimap has reached its final
            // end-of-frame position so both visible quads get identical depth/orientation and
            // their visual centers can be matched directly.
            UpdateVitalsRenderTextureV16();
            LogHudGeometryProbeV63();

            if (DebugLogCanvasChanges && Time.time - _lastMinimapPositionLogTime > 2f)
            {
                _lastMinimapPositionLogTime = Time.time;
                MelonLogger.Msg($"[VRPlayer][MinimapPositionDiag] (end-of-frame, outer container) offset={offset} finalPosition={finalPosition} headUp={headUp}");
            }
        }

        

        // V105 lightweight performance logger context. Uses already-cached menu transforms only;
        // no FindObjects scans, physics queries, GPU readbacks, or allocations beyond the short string.
        internal string GetPerformanceContextV105()
        {
            try
            {
                if (InventoryCamIsActive) return "TARP_INVENTORY";
                if (_grabBagHeld != null && _grabBagHeld.gameObject.activeInHierarchy) return "GRABBAG_COOKING";
                if (_backpackQuickSelect != null && _backpackQuickSelect.gameObject.activeInHierarchy) return "BACKPACK_QUICKSELECT";
                if (_tutorialBookHeld != null && _tutorialBookHeld.gameObject.activeInHierarchy) return "TUTORIAL_BOOK";
                if (_bookHeld != null && _bookHeld.gameObject.activeInHierarchy) return "BOOKLET";
                if (IsUiCursorActive) return "UI_CURSOR_MENU";
                return "GAMEPLAY";
            }
            catch
            {
                return "UNKNOWN";
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
                
                // IMPORTANT THIS BREACK INVENTORY 
                if (_cursorController.enabled) 
                {
                    if (!_cursorDisableTimerStarted)
                    {
                        // Primo frame: avvia il timer
                        _cursorDisableTimerStarted = true;
                        _cursorDisableStartTime = Time.time;
                        MelonLogger.Msg("[VRPlayer] Timer avviato per disabilitare InventoryCursorController");
                    }
                    else if (Time.time - _cursorDisableStartTime >= CursorDisableDelay)
                    {
                        // Sono passati 3 secondi: disabilita
                        _cursorController.enabled = false;
                        MelonLogger.Msg("[VRPlayer] InventoryCursorController disabilitato dopo 3s");
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
                if (_cursorController.enabled == false)
                {
                    _softwareCursor.transform.position = _cursorController.transform.position;
                    _softwareCursor.transform.localPosition = _cursorController.transform.localPosition;
                    _softwareCursor.transform.rotation = _cursorController.transform.rotation;
                }
                
                
                /*
    

                             
                */
            }
        }
    }
}
