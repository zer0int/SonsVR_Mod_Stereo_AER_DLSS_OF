using MelonLoader;

namespace SonsVRMod
{
    /// <summary>
    /// Runtime knobs, backed by MelonPreferences so they can be changed without rebuilding the mod.
    /// <para>
    /// The file is written to <c>&lt;game&gt;\UserData\MelonPreferences.cfg</c> under the
    /// <c>[SonsVR]</c> section on the first run. Edit it between launches.
    /// </para>
    /// <para>
    /// The three Enable* switches exist to isolate a hang: turn one off at a time and see which one
    /// lets the game keep running. That points at the stage responsible without touching the code.
    /// </para>
    /// </summary>
    internal static class VRConfig
    {
        /// <summary>Multiplier on the eye resolution recommended by the runtime.</summary>
        public static float RenderScale = 0.7f;

        /// <summary>
        /// Whether the eye textures are handed to the OpenVR compositor.
        /// Turn off to find out whether a hang comes from the compositor: with this off the headset
        /// stays black by design, but the game should keep running.
        /// </summary>
        public static bool SubmitToCompositor = true;

        /// <summary>
        /// Whether the eye cameras render at all.
        /// Turn off to find out whether a hang comes from rendering the scene twice through HDRP.
        /// </summary>
        public static bool EyeCamerasEnabled = true;

        /// <summary>
        /// Whether the blocking WaitGetPoses call runs each frame.
        /// Turn off to find out whether a hang comes from the compositor throttling the frame loop.
        /// Head tracking freezes when this is off.
        /// </summary>
        public static bool WaitForPoses = true;

        /// <summary>
        /// Whether the game camera is stripped down and the desktop shows the left eye.
        /// <para>
        /// Off by default. It saves a third of the frame, but it rewrites the culling mask and the
        /// target texture of the camera the game owns, and it switches on at the exact moment
        /// gameplay starts. That is the least proven code in the pipeline, so stability comes first
        /// and this gets turned on once everything else is known to hold.
        /// </para>
        /// </summary>
        public static bool DesktopMirror = false;

        /// <summary>
        /// Eye height in metres to use instead of the measured one. Zero measures it from the headset.
        /// </summary>
        public static float EyeHeightOverride = 0f;

        /// <summary>
        /// How far, in metres, the view may move away from the head of the character through real
        /// head movement. Keeps leaning and peeking while stopping the camera from wandering into the
        /// body of the character. Zero pins the view to the head and drops positional tracking.
        /// </summary>
        public static float PositionalTrackingRange = 0.30f;

        /// <summary>
        /// Let the character turn with the head even while standing still. Off keeps scripted
        /// animations intact and stops the character swivelling every time the player glances around.
        /// </summary>
        public static bool BodyFollowsHeadWhileStill = false;

        /// <summary>
        /// Above this tilt of the head bone, in degrees, the character is treated as being posed by
        /// the game rather than standing, and the body is left alone.
        /// </summary>
        public static float MaxBodyAlignTilt = 45f;

        /// <summary>
        /// Below this angle between head and shoulders, in degrees, the player is taken to have
        /// turned their whole body in the room, and the character turns with them.
        /// </summary>
        public static float ShoulderAlignAngle = 35f;

        /// <summary>
        /// The angle the head has to open up to, relative to the shoulders, before the character
        /// stops following again. Wider than the align angle so the decision cannot flicker.
        /// </summary>
        public static float ShoulderReleaseAngle = 55f;

        /// <summary>
        /// Minimum horizontal distance in metres between the two controllers for the line between
        /// them to say anything useful about which way the shoulders face.
        /// </summary>
        public static float MinHandSeparation = 0.20f;

        /// <summary>
        /// Move the inventory camera onto the head while the inventory is open. The game picks items
        /// by projecting through that camera, so the head-following pointer depends on it. Turn off
        /// if the inventory layout, or the player, starts drifting away from where it was opened.
        /// </summary>
        public static bool DriveInventoryCamera = true;

        /// <summary>
        /// Master switch for everything this mod does to the inventory: driving its camera, moving
        /// its layout, hiding its ground, disabling its cursor controller and sending the popup key.
        /// <para>
        /// Turn off to find out in a single run whether the character falling through the world comes
        /// from any of that, or from somewhere else entirely. The VR inventory will be unusable with
        /// it off, which is the point: it isolates the question.
        /// </para>
        /// </summary>
        public static bool InventoryInterventions = true;

        /// <summary>
        /// Disable the cursor controller of the game a few seconds after the inventory opens, so the
        /// pointer can be driven by gaze instead. The original code did this unconditionally and
        /// flagged it as breaking the inventory; it is a prime suspect for the character falling
        /// through the world, which starts around the same time.
        /// </summary>
        public static bool DisableInventoryCursorController = true;

        /// <summary>
        /// Send an interact key press shortly after the inventory opens, to dismiss a popup.
        /// </summary>
        public static bool SendInventoryPopupKey = true;

        /// <summary>Hide the renderers of the plane the inventory lays its contents out on.</summary>
        public static bool HideInventoryFakeGround = true;

        /// <summary>
        /// Clearance in metres kept between the inventory layout and the ground under it.
        /// <para>
        /// With <see cref="InventoryAdaptiveLift"/> on, this is the margin above the surface found by
        /// probing downwards. With it off, it is simply added to where the game placed the layout.
        /// </para>
        /// <para>
        /// A margin is still needed either way, because probing finds collision and the things that
        /// swallow the items, tall grass above all, are usually drawn without any.
        /// </para>
        /// </summary>
        public static float InventoryLift = 0.15f;

        /// <summary>
        /// Find the ground under the inventory by probing downwards, instead of trusting the height
        /// the game placed the layout at. Handles slopes, steps and standing on top of something.
        /// </summary>
        public static bool InventoryAdaptiveLift = true;

        /// <summary>
        /// Radius in metres over which the ground is sampled. The layout covers an area, so a single
        /// probe down its centre would miss a rise under one of its corners.
        /// </summary>
        public static float InventoryProbeRadius = 0.6f;

        /// <summary>
        /// Size multiplier for the inventory pointer. A cursor drawn for a monitor is only a few
        /// pixels across on a panel viewed from a metre away.
        /// </summary>
        public static float InventoryCursorScale = 3.0f;

        /// <summary>
        /// Distance in metres of the inventory interface panel from the head. A world space canvas is
        /// depth tested against the scene, so anything closer than this hides it.
        /// </summary>
        public static float InventoryCanvasDistance = 0.3f;

        /// <summary>Whether the eye cameras keep their HDRP temporal history between frames.</summary>
        public static bool KeepTemporalHistory = false;

        /// <summary>
        /// Feed the desktop mirror from the right eye instead of the left. A diagnostic: it tells
        /// apart a mirror that damages the eye it copies from a fault that always lands on the left
        /// because that camera happens to be rendered first.
        /// </summary>
        public static bool MirrorRightEye = false;

        /// <summary>
        /// Point the stripped down game camera at a tiny target, so its full screen passes stop
        /// running at desktop resolution. Measured as the larger part of what the mirror saves.
        /// Turn off if the eye images break again.
        /// </summary>
        public static bool MirrorTinyTarget = true;

        /// <summary>
        /// Fraction of the horizontal field of view to render, as a tangent multiplier. 1 renders
        /// everything the runtime asks for; 0.90 drops the outer tenth of the view.
        /// <para>
        /// The frustum is narrowed and drawn into the matching part of the eye texture, leaving the
        /// rest black, so the compositor still maps the whole texture onto the whole field of view.
        /// Geometry and lens distortion are untouched: what goes away is the extreme periphery, where
        /// the lens is barely transmitting anything anyway. The same idea as the field of view
        /// tangent sliders in Virtual Desktop.
        /// </para>
        /// </summary>
        public static float FovTangentHorizontal = 1.0f;

        /// <summary>Fraction of the vertical field of view to render. See <see cref="FovTangentHorizontal"/>.</summary>
        public static float FovTangentVertical = 1.0f;

        /// <summary>Move the flat menus onto a panel in the world so they can be seen in a headset.</summary>
        public static bool MenuInWorldSpace = true;

        /// <summary>
        /// Also capture the screens built with UI Toolkit, the options screen among them, which have
        /// no canvas to convert and otherwise appear only on the monitor.
        /// </summary>
        public static bool MenuUiToolkit = true;

        /// <summary>Distance in metres the menu panel sits in front of the player.</summary>
        public static float MenuDistance = 1.32f;

        /// <summary>
        /// Metres to raise the viewpoint, and the menu panel with it, while the game owns the camera.
        /// The cameras the menus and cutscenes use sit low enough to put the viewpoint inside the
        /// scenery, which is both uncomfortable and in the way of the panel.
        /// </summary>
        public static float MenuHeightOffset = 0.5f;

        /// <summary>Height in metres of the menu panel.</summary>
        public static float MenuHeight = 1.0f;

        /// <summary>
        /// Metres to raise or lower the menu panel, independently of the viewpoint. Negative brings
        /// it down. Use this to place the panel; use MenuHeightOffset to place the camera.
        /// </summary>
        public static float MenuPanelVerticalOffset = 0f;

        /// <summary>Cursor speed across the panel, in canvas units per second.</summary>
        public static float MenuCursorSpeed = 900f;

        /// <summary>Size of the cursor marker, in canvas units.</summary>
        public static float MenuCursorSize = 24f;

        /// <summary>How many frames get detailed enter/exit tracing at startup.</summary>
        public static int TraceFrames = 5;

        /// <summary>Measure and report how much of the frame this mod costs.</summary>
        public static bool Profile = true;

        /// <summary>
        /// Tell the compositor explicitly that the frame has been handed over, right after submitting.
        /// <para>
        /// Without it the compositor decides for itself when our frame is finished and holds the next
        /// blocking pose call until then. The watchdog caught the main thread parked in that call
        /// while the headset showed "waiting": each side waiting on the other.
        /// </para>
        /// </summary>
        public static bool PostPresentHandoff = true;

        /// <summary>
        /// Switch off the camera effects that make no sense in a headset: motion blur, depth of
        /// field and distortion. All three simulate a camera rather than an eye, and in VR they fight
        /// what the eye is already doing. Turning them off is a gain in comfort as well as speed.
        /// </summary>
        public static bool EyeDisableCameraEffects = true;

        /// <summary>
        /// Switch off the screen space effects on the eye cameras: reflections, ambient occlusion and
        /// contact shadows. They are among the more expensive passes and read poorly in stereo, since
        /// each eye computes them from its own view and the two do not agree.
        /// </summary>
        public static bool EyeDisableScreenSpaceEffects = true;

        /// <summary>
        /// Switch off volumetric lighting and clouds on the eye cameras. A real visual loss, so it is
        /// off by default: measure whether it buys enough to be worth the fog going flat.
        /// </summary>
        public static bool EyeDisableVolumetrics = false;

        private static bool _loaded;

        public static void Load()
        {
            if (_loaded)
                return;

            _loaded = true;

            try
            {
                var category = MelonPreferences.CreateCategory("SonsVR", "Sons of the Forest VR");

                RenderScale = category.CreateEntry("RenderScale", RenderScale,
                    description: "Multiplier on the eye resolution recommended by SteamVR.").Value;

                SubmitToCompositor = category.CreateEntry("SubmitToCompositor", SubmitToCompositor,
                    description: "Send the eye textures to SteamVR. Off = black headset, for isolating hangs.").Value;

                EyeCamerasEnabled = category.CreateEntry("EyeCamerasEnabled", EyeCamerasEnabled,
                    description: "Let the eye cameras render. Off = nothing is drawn, for isolating hangs.").Value;

                WaitForPoses = category.CreateEntry("WaitForPoses", WaitForPoses,
                    description: "Run the blocking WaitGetPoses call. Off = no head tracking, for isolating hangs.").Value;

                DesktopMirror = category.CreateEntry("DesktopMirror", DesktopMirror,
                    description: "Strip the game camera and show the left eye on the desktop instead.").Value;

                EyeHeightOverride = category.CreateEntry("EyeHeightOverride", EyeHeightOverride,
                    description: "Eye height in metres. 0 measures it from the headset at scene start.").Value;

                PositionalTrackingRange = category.CreateEntry("PositionalTrackingRange", PositionalTrackingRange,
                    description: "How far in metres the view may move off the head of the character. 0 pins it.").Value;

                BodyFollowsHeadWhileStill = category.CreateEntry("BodyFollowsHeadWhileStill", BodyFollowsHeadWhileStill,
                    description: "Turn the character with the head even when standing still.").Value;

                MaxBodyAlignTilt = category.CreateEntry("MaxBodyAlignTilt", MaxBodyAlignTilt,
                    description: "Head bone tilt in degrees above which the character is treated as posed by the game.").Value;

                ShoulderAlignAngle = category.CreateEntry("ShoulderAlignAngle", ShoulderAlignAngle,
                    description: "Head to shoulder angle below which a real body turn is assumed.").Value;

                ShoulderReleaseAngle = category.CreateEntry("ShoulderReleaseAngle", ShoulderReleaseAngle,
                    description: "Head to shoulder angle above which the character stops following again.").Value;

                MinHandSeparation = category.CreateEntry("MinHandSeparation", MinHandSeparation,
                    description: "Minimum distance in metres between controllers for the shoulder reading to count.").Value;

                DriveInventoryCamera = category.CreateEntry("DriveInventoryCamera", DriveInventoryCamera,
                    description: "Put the inventory camera on the head so the pointer follows where the player looks.").Value;

                InventoryInterventions = category.CreateEntry("InventoryInterventions", InventoryInterventions,
                    description: "Master switch for everything this mod does to the inventory. Off = vanilla inventory behaviour.").Value;

                DisableInventoryCursorController = category.CreateEntry("DisableInventoryCursorController", DisableInventoryCursorController,
                    description: "Switch off the cursor controller of the game a few seconds after the inventory opens.").Value;

                SendInventoryPopupKey = category.CreateEntry("SendInventoryPopupKey", SendInventoryPopupKey,
                    description: "Send an interact key press shortly after the inventory opens.").Value;

                HideInventoryFakeGround = category.CreateEntry("HideInventoryFakeGround", HideInventoryFakeGround,
                    description: "Hide the renderers of the plane the inventory lays its contents out on.").Value;

                InventoryLift = category.CreateEntry("InventoryLift", InventoryLift,
                    description: "Clearance in metres kept above the ground, so items do not sink into snow or tall grass.").Value;

                InventoryAdaptiveLift = category.CreateEntry("InventoryAdaptiveLift", InventoryAdaptiveLift,
                    description: "Probe downwards for the real ground instead of trusting where the game placed the layout.").Value;

                InventoryCursorScale = category.CreateEntry("InventoryCursorScale", InventoryCursorScale,
                    description: "Size multiplier for the inventory pointer.").Value;

                InventoryCanvasDistance = category.CreateEntry("InventoryCanvasDistance", InventoryCanvasDistance,
                    description: "Distance in metres of the inventory interface from the head. Closer clears nearby objects.").Value;

                InventoryProbeRadius = category.CreateEntry("InventoryProbeRadius", InventoryProbeRadius,
                    description: "Radius in metres over which the ground under the inventory is sampled.").Value;

                KeepTemporalHistory = category.CreateEntry("KeepTemporalHistory", KeepTemporalHistory,
                    description: "Keep HDRP temporal history per eye camera. Costs a lot of video memory.").Value;

                MirrorRightEye = category.CreateEntry("MirrorRightEye", MirrorRightEye,
                    description: "Feed the desktop mirror from the right eye instead of the left. Diagnostic.").Value;

                MirrorTinyTarget = category.CreateEntry("MirrorTinyTarget", MirrorTinyTarget,
                    description: "Render the stripped down game camera into a tiny target instead of at desktop resolution.").Value;

                FovTangentHorizontal = category.CreateEntry("FovTangentHorizontal", FovTangentHorizontal,
                    description: "Fraction of the horizontal field of view to render. 1 = full, 0.90 = drop the outer tenth.").Value;

                FovTangentVertical = category.CreateEntry("FovTangentVertical", FovTangentVertical,
                    description: "Fraction of the vertical field of view to render. 1 = full, 0.86 = drop the outer seventh.").Value;

                MenuInWorldSpace = category.CreateEntry("MenuInWorldSpace", MenuInWorldSpace,
                    description: "Move the flat menus onto a panel in the world so they can be seen in a headset.").Value;

                MenuUiToolkit = category.CreateEntry("MenuUiToolkit", MenuUiToolkit,
                    description: "Also capture screens built with UI Toolkit, such as the options screen.").Value;

                MenuDistance = category.CreateEntry("MenuDistance", MenuDistance,
                    description: "Distance in metres the menu panel sits in front of the player.").Value;

                MenuHeightOffset = category.CreateEntry("MenuHeightOffset", MenuHeightOffset,
                    description: "Metres to raise the viewpoint while the game owns the camera, so it clears the scenery.").Value;

                MenuHeight = category.CreateEntry("MenuHeight", MenuHeight,
                    description: "Height in metres of the menu panel.").Value;

                MenuPanelVerticalOffset = category.CreateEntry("MenuPanelVerticalOffset", MenuPanelVerticalOffset,
                    description: "Metres to raise or lower the menu panel, independently of the viewpoint.").Value;

                MenuCursorSpeed = category.CreateEntry("MenuCursorSpeed", MenuCursorSpeed,
                    description: "Cursor speed across the menu panel, in canvas units per second.").Value;

                MenuCursorSize = category.CreateEntry("MenuCursorSize", MenuCursorSize,
                    description: "Size of the menu cursor marker, in canvas units.").Value;

                EyeDisableCameraEffects = category.CreateEntry("EyeDisableCameraEffects", EyeDisableCameraEffects,
                    description: "Turn off motion blur, depth of field and distortion on the eye cameras.").Value;

                EyeDisableScreenSpaceEffects = category.CreateEntry("EyeDisableScreenSpaceEffects", EyeDisableScreenSpaceEffects,
                    description: "Turn off screen space reflections, ambient occlusion and contact shadows on the eye cameras.").Value;

                EyeDisableVolumetrics = category.CreateEntry("EyeDisableVolumetrics", EyeDisableVolumetrics,
                    description: "Turn off volumetric lighting and clouds on the eye cameras. Visible quality loss.").Value;

                PostPresentHandoff = category.CreateEntry("PostPresentHandoff", PostPresentHandoff,
                    description: "Hand the frame over to the compositor explicitly after submitting, to avoid both sides waiting.").Value;

                Profile = category.CreateEntry("Profile", Profile,
                    description: "Measure and report how much main thread time this mod costs per frame.").Value;

                TraceFrames = category.CreateEntry("TraceFrames", TraceFrames,
                    description: "How many startup frames get detailed enter/exit tracing in the log.").Value;

                // Applied after every entry has been read, so the master switch wins over the
                // individual ones rather than being overwritten by them.
                if (!InventoryInterventions)
                {
                    DriveInventoryCamera = false;
                    DisableInventoryCursorController = false;
                    SendInventoryPopupKey = false;
                    HideInventoryFakeGround = false;
                    InventoryLift = 0f;

                    MelonLogger.Warning("[VRConfig] Inventory interventions are off: the VR inventory will not " +
                                        "work, this setting exists to isolate the falling bug");
                }

                MelonLogger.Msg($"[VRConfig] RenderScale={RenderScale:F2} Submit={SubmitToCompositor} " +
                                $"EyeCameras={EyeCamerasEnabled} WaitPoses={WaitForPoses} " +
                                $"Mirror={DesktopMirror} TemporalHistory={KeepTemporalHistory} Trace={TraceFrames}");
            }
            catch (System.Exception e)
            {
                MelonLogger.Warning($"[VRConfig] Preferences unavailable, using defaults: {e.Message}");
            }
        }
    }
}
