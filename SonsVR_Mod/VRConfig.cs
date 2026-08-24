using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

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
        /// Milliseconds a single Submit may take before it is written to the log as a stall.
        /// <para>
        /// The compositor counted forty nine timeout episodes over one session that ended with the
        /// main thread stopped inside Submit, so the question is whether that call stalls all the
        /// time and once failed to come back, or whether it ran clean until it stopped dead. The two
        /// have different causes and the log cannot tell them apart without measuring.
        /// </para>
        /// <para>
        /// Well above a frame at forty per second, so ordinary waiting on the compositor is not
        /// reported: only a submit that has plainly lost its place.
        /// </para>
        /// </summary>
        public static float SubmitStallMs = 50f;


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
        public static bool DesktopMirror = true;

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
        public static float InventoryCursorScale = 1.0f;

        /// <summary>
        /// Distance in metres of the inventory interface panel from the head. A world space canvas is
        /// depth tested against the scene, so anything closer than this hides it.
        /// </summary>
        public static float InventoryCanvasDistance = 0.57f;

        /// <summary>
        /// Height in metres of the heads up display panel.
        /// <para>
        /// Together with the distance this decides how wide an angle the display covers. Too large or
        /// too near and the things anchored to its corners, the compass and the map among them, fall
        /// outside what the lenses show.
        /// </para>
        /// </summary>
        public static float HudSize = 0.47f;

        /// <summary>
        /// The display size the interface positions were tuned at.
        /// <para>
        /// A canvas grows about its own origin, and the map and its ring hang at different offsets
        /// from theirs, so changing the size slid them apart. The placement code keeps each piece
        /// still by cancelling the movement its own size change would cause, and that cancellation
        /// has to be measured from somewhere: this is that reference. At this size nothing is
        /// cancelled and the hand tuned offsets apply exactly as they were set.
        /// </para>
        /// </summary>
        public const float HudReferenceSize = 0.35f;

        /// <summary>
        /// Where the whole display sits relative to straight ahead, in metres. Positive is right.
        /// <para>
        /// Applied to the point everything on the display is placed from, so the map, the ring and
        /// the messages all move together and keep the arrangement they were given.
        /// </para>
        /// </summary>
        public static float HudOffsetX = 0.078f;

        /// <summary>Where the whole display sits vertically, in metres. Positive is up.</summary>
        public static float HudOffsetY = -0.054f;

        /// <summary>
        /// Where the map sits relative to the middle of the display, in metres. Positive is right.
        /// <para>
        /// The map and the ring drawn around it are on separate canvases, and only the map is
        /// placed by this mod: the ring rides wherever the main display lands. So the two are lined
        /// up by these two numbers and nothing else.
        /// </para>
        /// <para>
        /// Left in the file rather than put on the panel. This is the alignment of one piece
        /// against another, set once and then correct; moving the display about is a different job,
        /// done by HudOffsetX and HudOffsetY.
        /// </para>
        /// </summary>
        public static float MiniMapOffsetX = 0.335f;

        /// <summary>Where the map sits vertically, in metres. Positive is up. See the sideways one.</summary>
        public static float MiniMapOffsetY = -0.21f;

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
        public static float FovTangentHorizontal = 0.90f;

        /// <summary>Fraction of the vertical field of view to render. See <see cref="FovTangentHorizontal"/>.</summary>
        public static float FovTangentVertical = 0.86f;

        /// <summary>Move the flat menus onto a panel in the world so they can be seen in a headset.</summary>
        public static bool MenuInWorldSpace = true;

        /// <summary>
        /// Also capture the screens built with UI Toolkit, the options screen among them, which have
        /// no canvas to convert and otherwise appear only on the monitor.
        /// </summary>
        public static bool MenuUiToolkit = true;

        /// <summary>Distance in metres the menu panel sits in front of the player.</summary>
        public static float MenuDistance = 1.2f;

        /// <summary>
        /// Metres to raise the viewpoint, and the menu panel with it, while the game owns the camera.
        /// The cameras the menus and cutscenes use sit low enough to put the viewpoint inside the
        /// scenery, which is both uncomfortable and in the way of the panel.
        /// </summary>
        public static float MenuHeightOffset = 0.65f;

        /// <summary>Height in metres of the menu panel.</summary>
        public static float MenuHeight = 1.0f;

        /// <summary>
        /// Metres to raise or lower the menu panel, independently of the viewpoint. Negative brings
        /// it down. Use this to place the panel; use MenuHeightOffset to place the camera.
        /// </summary>
        public static float MenuPanelVerticalOffset = 1.2f;

        /// <summary>Cursor speed across the panel, in canvas units per second.</summary>
        public static float MenuCursorSpeed = 900f;

        /// <summary>Size of the cursor marker, in canvas units.</summary>
        public static float MenuCursorSize = 24f;

        /// <summary>Height in metres of the mod's settings panel.</summary>
        public static float SettingsPanelHeight = 1.4f;

        /// <summary>Distance in metres the settings panel sits in front of the player.</summary>
        public static float SettingsPanelDistance = 1.6f;

        /// <summary>
        /// Whether the buttons the game leaves spare are bound to the actions it has no room for.
        /// <para>
        /// Building needs more commands than a controller has buttons, so the spare ones each carry
        /// two: a tap and a hold. Turn off to leave the stick clicks and the left X alone.
        /// </para>
        /// </summary>
        public static bool ExtraBindings = true;

        /// <summary>How long a button is held, in seconds, before it counts as a hold and not a tap.</summary>
        public static float LongPressSeconds = 0.6f;

        /// <summary>
        /// Whether holding the left grip turns the right stick into the rotate controls for building.
        /// <para>
        /// The stick already turns the player, and a piece being placed also has to be turned, so the
        /// grip says which of the two is meant. Turning the player is suspended while it is held,
        /// rather than both happening at once.
        /// </para>
        /// </summary>
        public static bool BuildRotateWithGrip = true;

        /// <summary>
        /// Hide the character while the settings panel is open, arms and held items included. The
        /// beam otherwise emerges from the back of a hand that is in the way of the panel.
        /// </summary>
        public static bool HidePlayerInSettings = true;

        /// <summary>
        /// Whether the inventory is pointed at with the controller rather than with the head.
        /// <para>
        /// The game finds what is under the pointer by projecting through its own inventory camera,
        /// so aiming that camera along the controller is what moves the selection onto the beam.
        /// That camera draws nothing the player sees: the eye cameras do that, from the head.
        /// </para>
        /// </summary>
        public static bool InventoryLaserPointing = true;

        /// <summary>How far the beam is drawn in the inventory, in metres, having nothing to land on.</summary>
        public static float InventoryLaserLength = 2.5f;

        /// <summary>
        /// Whether the game's own arrow is kept on screen in the inventory.
        /// <para>
        /// Off while the beam is doing the pointing. The arrow sits in the middle of the display
        /// wherever the head happens to face, which is no longer where anything is being selected:
        /// two pointers, one of which is lying. It went to some trouble to stay visible, so it is
        /// switched off deliberately rather than left to be noticed.
        /// </para>
        /// </summary>
        public static bool InventorySoftwareCursor = false;

        /// <summary>
        /// Whether the beam can be moved to the other hand from inside the inventory.
        /// <para>
        /// On, as it is on the menus, so a left handed player can take the beam wherever they are
        /// rather than having to leave the inventory to change hands.
        /// </para>
        /// <para>
        /// Both triggers belong to the game in here, the right one being the mouse button that
        /// takes an item and the left one the secondary action, and they keep working either way:
        /// swapping only flips which hand the beam comes from. What it costs is that the beam moves
        /// on a press aimed at the game, which is why it can be turned off.
        /// </para>
        /// </summary>
        public static bool InventoryLaserHandSwitch = true;

        /// <summary>
        /// Whether the character is taken out of sight while the inventory is open.
        /// <para>
        /// The character kneels to open the pack while the viewpoint keeps standing height and its
        /// own heading. Turning to look at the contents therefore brings a kneeling body into view
        /// from above, with no head on it, the head being where the player is looking from.
        /// </para>
        /// </summary>
        public static bool HidePlayerInInventory = true;

        /// <summary>
        /// Show the loading screen on a panel in the world.
        /// <para>
        /// Without it the view simply stops while a scene loads: the last frame stays frozen in the
        /// headset, and whatever the camera was pointing at when the world was torn down stays there
        /// too, which can be the inside of nothing at all.
        /// </para>
        /// </summary>
        public static bool LoadingScreenInVR = true;

        /// <summary>How many frames get detailed enter/exit tracing at startup.</summary>
        public static int TraceFrames = 5;

        /// <summary>Measure and report how much of the frame this mod costs.</summary>
        public static bool Profile = true;

        /// <summary>
        /// Survey the player's skeleton, animators and existing inverse kinematics once, as
        /// groundwork for driving the arms from the controllers.
        /// </summary>
        /// <summary>Write the interface layout to the log once, to work out where its pieces hang.</summary>
        public static bool LogInterfaceLayout = false;

        public static bool LogArmSkeleton = true;

        /// <summary>
        /// How far the right stick must be pushed forward or back before running or crouching begin.
        /// <para>
        /// High on purpose. That stick also turns the player, and a turn is a push sideways that
        /// rarely stays perfectly sideways: a thumb sweeping left picks up a little forward or back
        /// on the way, which at a low threshold is enough to break into a run or drop into a crouch
        /// in the middle of looking around. Asking for something close to the end of travel makes
        /// the two gestures tell each other apart.
        /// </para>
        /// </summary>
        public static float RunCrouchThreshold = 0.85f;

        /// <summary>
        /// How far the left stick must be pushed sideways before the player steps sideways.
        /// <para>
        /// Half travel, for the same reason at a gentler setting: walking forward is a push that is
        /// meant to be straight and never quite is, and a low threshold turns every walk into a
        /// drift across the diagonal. Forward and back keep their own light threshold, since nothing
        /// else on this stick competes with them.
        /// </para>
        /// </summary>
        public static float StrafeThreshold = 0.5f;

        /// <summary>Degrees turned by one flick of the stick.</summary>
        public static float SnapTurnAngle = 30f;

        /// <summary>
        /// Turn continuously with the stick instead of in fixed steps.
        /// <para>
        /// Which of the two is comfortable is a matter of the person, not of the game: continuous
        /// turning moves the world while the head is still, which some people read as motion and
        /// others do not notice at all. Hence a switch rather than a decision.
        /// </para>
        /// </summary>
        public static bool SmoothTurn = false;

        /// <summary>Degrees per second while turning continuously.</summary>
        public static float SmoothTurnSpeed = 90f;

        /// <summary>Metres the viewpoint sits to the right of where the game hangs its camera.</summary>
        public static float PlayerOffsetX = 0f;

        /// <summary>Metres the viewpoint sits forward of where the game hangs its camera.</summary>
        public static float PlayerOffsetZ = 0f;

        /// <summary>Point at panels by aiming a controller, rather than steering a cursor with a stick.</summary>
        public static bool LaserPointer = true;

        /// <summary>Aim with the left controller instead of the right.</summary>
        public static bool LaserLeftHand = false;

        /// <summary>
        /// Degrees the beam is tilted down from the controller's own axis. A controller held
        /// naturally points somewhat below the direction its axis runs in.
        /// </summary>
        public static float LaserAngle = 30f;

        /// <summary>How far the beam reaches, in metres.</summary>
        public static float LaserRange = 6f;

        /// <summary>Thickness of the beam at the controller, in metres.</summary>
        public static float LaserWidth = 0.004f;

        /// <summary>Make the arms of the character follow the motion controllers.</summary>
        public static bool HandTracking = true;

        /// <summary>
        /// Metres to raise the viewpoint during gameplay, above the point the camera hangs from.
        /// <para>
        /// That point sits where the game attaches its camera, which is around the neck rather than
        /// between the eyes. On a flat screen nothing shows it; with the body and arms visible in a
        /// headset the difference reads as looking out from shoulder height.
        /// </para>
        /// </summary>
        public static float PlayerEyeHeightOffset = 0.19f;

        /// <summary>
        /// How strongly the controllers override the animation, from 0 to 1. Below one the arms blend
        /// between where the animation puts them and where the controller is, which is a way to keep
        /// some of the game's own posing while still following the hands.
        /// </summary>
        public static float HandIKWeight = 1.0f;

        /// <summary>
        /// Rotation in degrees that turns a controller pose into a hand pose, for the left hand.
        /// <para>
        /// A controller reports the pose of the device, not of a hand: which way the palm and fingers
        /// face relative to it is a decision this game's rig made, and every skeleton makes it
        /// differently. It was found by looking at the result, so it is fixed here rather than
        /// offered as a setting: it belongs to the game, not to the player.
        /// </para>
        /// <para>
        /// The 180 is the mirror between the two hands. The rest is the tilt measured in the headset,
        /// with the arms held out and the palms turned inward.
        /// </para>
        /// </summary>
        public static readonly Vector3 HandBaseRotationLeft = new Vector3(140f, 0f, 0f);

        /// <summary>The same correction for the right hand, without the mirror.</summary>
        public static readonly Vector3 HandBaseRotationRight = new Vector3(-40f, 0f, 0f);

        /// <summary>
        /// Adjustment of the left hand in degrees, on top of the fixed correction above.
        /// <para>
        /// Kept apart from that correction so zero means correct. What is left to adjust here is how
        /// the player holds the controller, which differs between people and between grips, and that
        /// is the part worth a slider. Pitch tips the palm, yaw swings it sideways, roll twists it.
        /// </para>
        /// </summary>
        public static Vector3 HandRotationOffsetLeft = Vector3.zero;

        /// <summary>Adjustment of the right hand in degrees. See the left one.</summary>
        public static Vector3 HandRotationOffsetRight = Vector3.zero;

        /// <summary>
        /// How strongly the elbows are held in a natural position, from 0 to 1.
        /// <para>
        /// The hand pose alone does not fix the arm: the elbow can sit anywhere on a circle around
        /// the line from shoulder to hand. At zero the solver chooses, which tends to fold the elbows
        /// into the body; at one they are kept where a real elbow would be.
        /// </para>
        /// </summary>
        public static float ElbowBendWeight = 1.0f;

        /// <summary>
        /// Metres the elbow goal sits out to the side of the shoulder. This is the value that makes
        /// the arms open outwards rather than fold across the chest.
        /// </summary>
        public static float ElbowOutward = 0.45f;

        /// <summary>Metres the elbow goal sits behind the shoulder.</summary>
        public static float ElbowBack = 0.15f;

        /// <summary>Metres the elbow goal sits below the shoulder.</summary>
        public static float ElbowDown = 0.25f;

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

        /// <summary>
        /// One setting the player can move from inside the headset.
        /// <para>
        /// Carries the reading and writing of the value together with the means to persist it, so a
        /// panel can offer a setting without knowing anything about how it is stored.
        /// </para>
        /// </summary>
        internal sealed class Adjustable
        {
            public string Category;

            /// <summary>Which column of the settings panel this setting is shown in.</summary>
            public int Column;

            public string Label;
            public float Min;
            public float Max;
            public bool IsToggle;
            public Func<float> Read;
            public Action<float> Write;
            public Action Persist;
        }

        /// <summary>The settings worth adjusting while wearing the headset, in the order shown.</summary>
        internal static readonly List<Adjustable> Adjustables = new List<Adjustable>();

        /// <summary>The group the next registered settings belong to.</summary>
        private static string _group = "";

        /// <summary>The panel column the next registered settings belong to.</summary>
        private static int _column;

        private static void Register(string label, float min, float max, Func<float> read, Action<float> write,
            MelonPreferences_Entry<float> entry)
        {
            Adjustables.Add(new Adjustable
            {
                Category = _group,
                Column = _column,
                Label = label,
                Min = min,
                Max = max,
                IsToggle = false,
                Read = read,
                Write = write,
                Persist = () => entry.Value = read()
            });
        }

        private static void RegisterToggle(string label, Func<bool> read, Action<bool> write,
            MelonPreferences_Entry<bool> entry)
        {
            Adjustables.Add(new Adjustable
            {
                Category = _group,
                Column = _column,
                Label = label,
                Min = 0f,
                Max = 1f,
                IsToggle = true,
                Read = () => read() ? 1f : 0f,
                Write = v => write(v >= 0.5f),
                Persist = () => entry.Value = read()
            });
        }

        /// <summary>
        /// The settings offered in the headset: the ones whose effect can be judged by looking at it
        /// while it changes. Everything else stays in the file, where it belongs.
        /// </summary>
        private static void BuildAdjustables(MelonPreferences_Category category)
        {
            Adjustables.Clear();

            // Which column of the panel each group lands in, decided here rather than worked out by
            // the panel. Balancing the two halves by height is easy and wrong: it puts groups where
            // they fit rather than where they are looked for, and the placement then shifts every
            // time a setting is added. Arms is long enough to hold a column on its own.
            _column = 0;

            _group = "View";
            Register("Render scale", 0.4f, 1.2f, () => RenderScale, v => RenderScale = v,
                category.GetEntry<float>("RenderScale"));
            Register("Field of view, width", 0.6f, 1.0f, () => FovTangentHorizontal,
                v => FovTangentHorizontal = v, category.GetEntry<float>("FovTangentHorizontal"));
            Register("Field of view, height", 0.6f, 1.0f, () => FovTangentVertical,
                v => FovTangentVertical = v, category.GetEntry<float>("FovTangentVertical"));

            _group = "Viewpoint";
            Register("Height", -0.2f, 0.5f, () => PlayerEyeHeightOffset,
                v => PlayerEyeHeightOffset = v, category.GetEntry<float>("PlayerEyeHeightOffset"));
            Register("Sideways", -0.5f, 0.5f, () => PlayerOffsetX, v => PlayerOffsetX = v,
                category.GetEntry<float>("PlayerOffsetX"));
            Register("Forward", -0.5f, 0.5f, () => PlayerOffsetZ, v => PlayerOffsetZ = v,
                category.GetEntry<float>("PlayerOffsetZ"));
            Register("Head movement range", 0f, 0.8f, () => PositionalTrackingRange,
                v => PositionalTrackingRange = v, category.GetEntry<float>("PositionalTrackingRange"));

            _group = "Movement";
            Register("Run and crouch need", 0.5f, 0.98f, () => RunCrouchThreshold,
                v => RunCrouchThreshold = v, category.GetEntry<float>("RunCrouchThreshold"));
            Register("Step sideways needs", 0.2f, 0.9f, () => StrafeThreshold,
                v => StrafeThreshold = v, category.GetEntry<float>("StrafeThreshold"));

            _group = "Turning";
            RegisterToggle("Smooth turning", () => SmoothTurn, v => SmoothTurn = v,
                category.GetEntry<bool>("SmoothTurn"));
            Register("Snap angle, degrees", 15f, 90f, () => SnapTurnAngle,
                v => SnapTurnAngle = v, category.GetEntry<float>("SnapTurnAngle"));
            Register("Smooth speed, degrees a second", 30f, 240f, () => SmoothTurnSpeed,
                v => SmoothTurnSpeed = v, category.GetEntry<float>("SmoothTurnSpeed"));
            RegisterToggle("Turn body while standing still", () => BodyFollowsHeadWhileStill,
                v => BodyFollowsHeadWhileStill = v, category.GetEntry<bool>("BodyFollowsHeadWhileStill"));

            _column = 1;

            _group = "Arms";
            RegisterToggle("Follow the controllers", () => HandTracking, v => HandTracking = v,
                category.GetEntry<bool>("HandTracking"));
            Register("Strength", 0f, 1f, () => HandIKWeight, v => HandIKWeight = v,
                category.GetEntry<float>("HandIKWeight"));
            Register("Left hand pitch, degrees", -45f, 45f, () => HandRotationOffsetLeft.x,
                v => HandRotationOffsetLeft.x = v, category.GetEntry<float>("HandAdjustLeftPitch"));
            Register("Left hand yaw, degrees", -45f, 45f, () => HandRotationOffsetLeft.y,
                v => HandRotationOffsetLeft.y = v, category.GetEntry<float>("HandAdjustLeftYaw"));
            Register("Left hand roll, degrees", -45f, 45f, () => HandRotationOffsetLeft.z,
                v => HandRotationOffsetLeft.z = v, category.GetEntry<float>("HandAdjustLeftRoll"));
            Register("Right hand pitch, degrees", -45f, 45f, () => HandRotationOffsetRight.x,
                v => HandRotationOffsetRight.x = v, category.GetEntry<float>("HandAdjustRightPitch"));
            Register("Right hand yaw, degrees", -45f, 45f, () => HandRotationOffsetRight.y,
                v => HandRotationOffsetRight.y = v, category.GetEntry<float>("HandAdjustRightYaw"));
            Register("Right hand roll, degrees", -45f, 45f, () => HandRotationOffsetRight.z,
                v => HandRotationOffsetRight.z = v, category.GetEntry<float>("HandAdjustRightRoll"));
            Register("Elbows outward", 0.1f, 0.8f, () => ElbowOutward, v => ElbowOutward = v,
                category.GetEntry<float>("ElbowOutward"));
            Register("Elbows down", 0f, 0.6f, () => ElbowDown, v => ElbowDown = v,
                category.GetEntry<float>("ElbowDown"));

            _group = "Interface";
            Register("Display distance", 0.3f, 1.2f, () => InventoryCanvasDistance,
                v => InventoryCanvasDistance = v, category.GetEntry<float>("InventoryCanvasDistance"));
            Register("Display size", 0.15f, 0.8f, () => HudSize, v => HudSize = v,
                category.GetEntry<float>("HudSize"));
            Register("Menu distance", 0.6f, 2.5f, () => MenuDistance, v => MenuDistance = v,
                category.GetEntry<float>("MenuDistance"));
            Register("Inventory pointer size", 1f, 8f, () => InventoryCursorScale,
                v => InventoryCursorScale = v, category.GetEntry<float>("InventoryCursorScale"));
            RegisterToggle("Point at the inventory", () => InventoryLaserPointing,
                v => InventoryLaserPointing = v, category.GetEntry<bool>("InventoryLaserPointing"));
            RegisterToggle("Hide the body in the inventory", () => HidePlayerInInventory,
                v => HidePlayerInInventory = v, category.GetEntry<bool>("HidePlayerInInventory"));
            Register("Display sideways", -0.6f, 0.6f, () => HudOffsetX,
                v => HudOffsetX = v, category.GetEntry<float>("HudOffsetX"));
            Register("Display height", -0.6f, 0.6f, () => HudOffsetY,
                v => HudOffsetY = v, category.GetEntry<float>("HudOffsetY"));

            _column = 0;

            _group = "Performance";
            RegisterToggle("Desktop shows an eye", () => DesktopMirror, v => DesktopMirror = v,
                category.GetEntry<bool>("DesktopMirror"));
            RegisterToggle("Camera effects off", () => EyeDisableCameraEffects,
                v => EyeDisableCameraEffects = v, category.GetEntry<bool>("EyeDisableCameraEffects"));
            RegisterToggle("Screen space effects off", () => EyeDisableScreenSpaceEffects,
                v => EyeDisableScreenSpaceEffects = v, category.GetEntry<bool>("EyeDisableScreenSpaceEffects"));
        }

        /// <summary>Writes every adjusted value back to the preferences file.</summary>
        public static void Save()
        {
            foreach (var adjustable in Adjustables)
                adjustable.Persist?.Invoke();

            MelonPreferences.Save();
            MelonLogger.Msg($"[VRConfig] {Adjustables.Count} settings saved");
        }

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

                SubmitStallMs = category.CreateEntry("SubmitStallMs", SubmitStallMs,
                    description: "Milliseconds one Submit may take before the log records it as a stall. Zero switches the measurement off.").Value;

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
                    description: "Distance in metres of the interface panel from the head.").Value;

                HudOffsetX = category.CreateEntry("HudOffsetX", HudOffsetX,
                    description: "Metres the whole display sits to the right of straight ahead.").Value;

                HudOffsetY = category.CreateEntry("HudOffsetY", HudOffsetY,
                    description: "Metres the whole display sits above straight ahead. Negative is below.").Value;

                MiniMapOffsetX = category.CreateEntry("MiniMapOffsetX", MiniMapOffsetX,
                    description: "Metres the map sits to the right of the middle of the display.").Value;

                MiniMapOffsetY = category.CreateEntry("MiniMapOffsetY", MiniMapOffsetY,
                    description: "Metres the map sits above the middle of the display. Negative is below.").Value;

                HudSize = category.CreateEntry("HudSize", HudSize,
                    description: "Height in metres of the heads up display panel.").Value;

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

                SettingsPanelHeight = category.CreateEntry("SettingsPanelHeight", SettingsPanelHeight,
                    description: "Height in metres of the mod's settings panel.").Value;

                SettingsPanelDistance = category.CreateEntry("SettingsPanelDistance", SettingsPanelDistance,
                    description: "Distance in metres the settings panel sits in front of the player.").Value;

                InventoryLaserPointing = category.CreateEntry("InventoryLaserPointing", InventoryLaserPointing,
                    description: "Aim the inventory with the controller instead of the head.").Value;

                InventoryLaserLength = category.CreateEntry("InventoryLaserLength", InventoryLaserLength,
                    description: "Metres the beam is drawn in the inventory, where it has nothing to land on.").Value;

                InventorySoftwareCursor = category.CreateEntry("InventorySoftwareCursor", InventorySoftwareCursor,
                    description: "Keep the game's own arrow on screen in the inventory. Off while the beam points, so there are not two pointers.").Value;

                InventoryLaserHandSwitch = category.CreateEntry("InventoryLaserHandSwitch", InventoryLaserHandSwitch,
                    description: "Let the other trigger take the beam while the inventory is open, so a left handed player can swap hands without leaving it.").Value;

                HidePlayerInInventory = category.CreateEntry("HidePlayerInInventory", HidePlayerInInventory,
                    description: "Hide the character while the inventory is open: it kneels below the viewpoint and reads as headless.").Value;

                BuildRotateWithGrip = category.CreateEntry("BuildRotateWithGrip", BuildRotateWithGrip,
                    description: "Hold the left grip to rotate what is being placed with the right stick, instead of turning.").Value;

                ExtraBindings = category.CreateEntry("ExtraBindings", ExtraBindings,
                    description: "Bind the spare buttons to the building and menu commands. Each carries a tap and a hold.").Value;

                LongPressSeconds = category.CreateEntry("LongPressSeconds", LongPressSeconds,
                    description: "Seconds a spare button is held before it counts as a hold rather than a tap.").Value;

                HidePlayerInSettings = category.CreateEntry("HidePlayerInSettings", HidePlayerInSettings,
                    description: "Hide the character while the settings panel is open.").Value;

                LoadingScreenInVR = category.CreateEntry("LoadingScreenInVR", LoadingScreenInVR,
                    description: "Show the loading screen on a panel in the world instead of freezing the view.").Value;

                EyeDisableCameraEffects = category.CreateEntry("EyeDisableCameraEffects", EyeDisableCameraEffects,
                    description: "Turn off motion blur, depth of field and distortion on the eye cameras.").Value;

                EyeDisableScreenSpaceEffects = category.CreateEntry("EyeDisableScreenSpaceEffects", EyeDisableScreenSpaceEffects,
                    description: "Turn off screen space reflections, ambient occlusion and contact shadows on the eye cameras.").Value;

                EyeDisableVolumetrics = category.CreateEntry("EyeDisableVolumetrics", EyeDisableVolumetrics,
                    description: "Turn off volumetric lighting and clouds on the eye cameras. Visible quality loss.").Value;

                PostPresentHandoff = category.CreateEntry("PostPresentHandoff", PostPresentHandoff,
                    description: "Hand the frame over to the compositor explicitly after submitting, to avoid both sides waiting.").Value;

                RunCrouchThreshold = category.CreateEntry("RunCrouchThreshold", RunCrouchThreshold,
                    description: "How far the right stick goes forward or back before running or crouching start. High, so turning cannot trigger either.").Value;

                StrafeThreshold = category.CreateEntry("StrafeThreshold", StrafeThreshold,
                    description: "How far the left stick goes sideways before the player steps sideways. Keeps walking forward from drifting into a diagonal.").Value;

                SnapTurnAngle = category.CreateEntry("SnapTurnAngle", SnapTurnAngle,
                    description: "Degrees turned by one flick of the stick.").Value;

                SmoothTurn = category.CreateEntry("SmoothTurn", SmoothTurn,
                    description: "Turn continuously with the stick instead of in fixed steps.").Value;

                SmoothTurnSpeed = category.CreateEntry("SmoothTurnSpeed", SmoothTurnSpeed,
                    description: "Degrees per second while turning continuously.").Value;

                PlayerOffsetX = category.CreateEntry("PlayerOffsetX", PlayerOffsetX,
                    description: "Metres the viewpoint sits to the right of the game's camera point.").Value;

                PlayerOffsetZ = category.CreateEntry("PlayerOffsetZ", PlayerOffsetZ,
                    description: "Metres the viewpoint sits forward of the game's camera point.").Value;

                LaserPointer = category.CreateEntry("LaserPointer", LaserPointer,
                    description: "Point at panels by aiming a controller.").Value;

                LaserLeftHand = category.CreateEntry("LaserLeftHand", LaserLeftHand,
                    description: "Aim with the left controller instead of the right.").Value;

                LaserAngle = category.CreateEntry("LaserAngle", LaserAngle,
                    description: "Degrees the beam is tilted down from the controller axis.").Value;

                LaserRange = category.CreateEntry("LaserRange", LaserRange,
                    description: "How far the beam reaches, in metres.").Value;

                LaserWidth = category.CreateEntry("LaserWidth", LaserWidth,
                    description: "Thickness of the beam at the controller, in metres.").Value;

                HandTracking = category.CreateEntry("HandTracking", HandTracking,
                    description: "Make the arms of the character follow the motion controllers.").Value;

                ElbowBendWeight = category.CreateEntry("ElbowBendWeight", ElbowBendWeight,
                    description: "How strongly the elbows are held in a natural position, 0 to 1.").Value;

                ElbowOutward = category.CreateEntry("ElbowOutward", ElbowOutward,
                    description: "Metres the elbow sits out to the side of the shoulder. Raise if the arms fold inwards.").Value;

                ElbowBack = category.CreateEntry("ElbowBack", ElbowBack,
                    description: "Metres the elbow sits behind the shoulder.").Value;

                ElbowDown = category.CreateEntry("ElbowDown", ElbowDown,
                    description: "Metres the elbow sits below the shoulder.").Value;

                PlayerEyeHeightOffset = category.CreateEntry("PlayerEyeHeightOffset", PlayerEyeHeightOffset,
                    description: "Metres to raise the viewpoint during gameplay: the game hangs its camera nearer the neck than the eyes.").Value;

                HandIKWeight = category.CreateEntry("HandIKWeight", HandIKWeight,
                    description: "How strongly the controllers override the animation, 0 to 1.").Value;

                // Read as three numbers each: MelonPreferences handles vectors poorly, and three
                // named angles are easier to adjust by hand than a serialised structure. These are
                // adjustments on top of HandBaseRotation*, so zero is correct and the numbers stay
                // small. The keys were renamed when the fixed part moved into code, so that a file
                // holding the old absolute angles is ignored rather than added twice.
                HandRotationOffsetLeft = new Vector3(
                    category.CreateEntry("HandAdjustLeftPitch", 0f, description: "Left hand pitch adjustment, degrees. Tips the palm up or down.").Value,
                    category.CreateEntry("HandAdjustLeftYaw", 0f, description: "Left hand yaw adjustment, degrees. Swings the palm sideways.").Value,
                    category.CreateEntry("HandAdjustLeftRoll", 0f, description: "Left hand roll adjustment, degrees. Twists the palm.").Value);

                HandRotationOffsetRight = new Vector3(
                    category.CreateEntry("HandAdjustRightPitch", 0f, description: "Right hand pitch adjustment, degrees. Tips the palm up or down.").Value,
                    category.CreateEntry("HandAdjustRightYaw", 0f, description: "Right hand yaw adjustment, degrees. Swings the palm sideways.").Value,
                    category.CreateEntry("HandAdjustRightRoll", 0f, description: "Right hand roll adjustment, degrees. Twists the palm.").Value);

                LogInterfaceLayout = category.CreateEntry("LogInterfaceLayout", LogInterfaceLayout,
                    description: "Write the heads up display layout to the log once. Large, and only useful when its pieces move apart.").Value;

                LogArmSkeleton = category.CreateEntry("LogArmSkeleton", LogArmSkeleton,
                    description: "Survey the player skeleton, animators and existing inverse kinematics once.").Value;

                Profile = category.CreateEntry("Profile", Profile,
                    description: "Measure and report how much main thread time this mod costs per frame.").Value;

                TraceFrames = category.CreateEntry("TraceFrames", TraceFrames,
                    description: "How many startup frames get detailed enter/exit tracing in the log.").Value;

                BuildAdjustables(category);

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
