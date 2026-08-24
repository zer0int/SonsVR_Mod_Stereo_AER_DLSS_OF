using System;
using MelonLoader;
using UnityEngine;
using Valve.VR;
using Mathf = UnityEngine.Mathf;

namespace SonsVRMod
{
    /// <summary>
    /// A beam from a controller to whatever panel is in front of the player, and the point where it
    /// lands.
    /// <para>
    /// Pointing by aiming is how a hand actually works: the arm already knows how to point at a
    /// thing, so the target is reached directly rather than steered towards. A stick moves a cursor
    /// at some rate across a surface, which means every selection is a small journey.
    /// </para>
    /// <para>
    /// The stick still works. Nothing is taken away, because a controller that loses tracking, or a
    /// player resting their arms, must not leave the panel unusable.
    /// </para>
    /// </summary>
    internal class VRLaser
    {
        private LineRenderer _line;
        private GameObject _owner;
        private static readonly TrackedDevicePose_t[] Poses =
            new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

        /// <summary>True when the beam found the panel this frame.</summary>
        public bool IsAiming { get; private set; }

        private static bool _lastOtherTrigger;
        private static bool _switchConsumedClick;

        /// <summary>
        /// Moves the beam to whichever hand asks for it.
        /// <para>
        /// The trigger means two things depending on which hand holds it: on the pointing hand it
        /// clicks, on the other it takes the beam. That keeps one button doing one thing per hand,
        /// and makes swapping hands the same gesture in both directions.
        /// </para>
        /// <para>
        /// The press that swaps hands is swallowed, so picking up the beam does not also press
        /// whatever it happens to be resting on.
        /// </para>
        /// </summary>
        public static void UpdateHandSwitch()
        {
            if (!VRConfig.LaserPointer)
                return;

            bool other = VRConfig.LaserLeftHand
                ? NativeVRInput.GetBoolean("TriggerRight_Press")
                : NativeVRInput.GetBoolean("TriggerLeft_Press");

            if (other && !_lastOtherTrigger)
            {
                VRConfig.LaserLeftHand = !VRConfig.LaserLeftHand;
                _switchConsumedClick = true;

                MelonLogger.Msg($"[VRLaser] pointer moved to the " +
                                $"{(VRConfig.LaserLeftHand ? "left" : "right")} hand");
            }

            _lastOtherTrigger = other;
        }

        /// <summary>
        /// The click: the trigger of the hand currently holding the beam.
        /// <para>
        /// Held false for the rest of a press that was used to take the beam, so the hand that has
        /// just claimed it does not immediately act with it.
        /// </para>
        /// </summary>
        public static bool ClickHeld()
        {
            bool pointing = VRConfig.LaserLeftHand
                ? NativeVRInput.GetBoolean("TriggerLeft_Press")
                : NativeVRInput.GetBoolean("TriggerRight_Press");

            if (_switchConsumedClick)
            {
                if (pointing)
                    return false;

                _switchConsumedClick = false;
            }

            return pointing;
        }

        /// <summary>
        /// A round dot, built here because a game already on disk has no sprite to lend for one.
        /// </summary>
        public static Sprite CircleSprite()
        {
            if (_circle != null)
                return _circle;

            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float radius = size * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - radius + 0.5f;
                    float dy = y - radius + 0.5f;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);

                    // Faded over the last pixel of the edge, so the dot reads as round rather than
                    // as a staircase when it is drawn large on a panel a metre away.
                    float alpha = Mathf.Clamp01(radius - distance);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            _circle = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
            return _circle;
        }

        private static Sprite _circle;

        /// <summary>
        /// Works out where the beam crosses a panel.
        /// </summary>
        /// <param name="panel">The panel being pointed at.</param>
        /// <param name="local">Where the beam lands, in the panel's own coordinates.</param>
        public bool TryAim(RectTransform panel, out Vector2 local)
        {
            local = Vector2.zero;
            IsAiming = false;

            if (panel == null || !VRConfig.LaserPointer)
                return false;

            if (!TryGetControllerRay(out Vector3 origin, out Vector3 direction))
            {
                Hide();
                return false;
            }

            // The panel is a plane. Its own forward points away from the player, so the surface
            // faces back along it.
            var plane = new Plane(-panel.forward, panel.position);
            var ray = new Ray(origin, direction);

            if (!plane.Raycast(ray, out float distance) || distance > VRConfig.LaserRange)
            {
                ReportMiss($"never reached the plane of '{panel.name}'", panel, origin, direction);
                DrawBeam(origin, origin + direction * VRConfig.LaserRange, false);
                return false;
            }

            Vector3 hit = ray.GetPoint(distance);

            // Into the panel's own space, which is the same space its controls are placed in, so no
            // conversion through a camera or a screen position is involved at all.
            Vector3 localPoint = panel.InverseTransformPoint(hit);
            var local2D = new Vector2(localPoint.x, localPoint.y);

            // Measured by the resolved rect rather than by sizeDelta. A canvas anchored stretched
            // reports zero there, which made every bound collapse to a point: the beam crossed the
            // panel, was judged to have missed it, and passed straight through a menu that was right
            // in front of it. The rect also carries the pivot, so an off centre panel is bounded
            // where it actually is instead of around its transform.
            Rect bounds = panel.rect;
            bool onPanel = bounds.size.y > 1f
                ? bounds.Contains(local2D)
                : IsWithin(local2D, panel.sizeDelta * 0.5f);

            DrawBeam(origin, hit, onPanel);

            if (!onPanel)
            {
                ReportMiss($"crossed the plane of '{panel.name}' at ({local2D.x:F0}, {local2D.y:F0}) px, " +
                           $"outside x {bounds.xMin:F0}..{bounds.xMax:F0} y {bounds.yMin:F0}..{bounds.yMax:F0}, " +
                           $"{distance:F2} m along the beam",
                           panel, origin, direction);

                return false;
            }

            local = local2D;
            IsAiming = true;
            return true;
        }

        private static bool IsWithin(Vector2 point, Vector2 half)
        {
            return Mathf.Abs(point.x) <= half.x && Mathf.Abs(point.y) <= half.y;
        }

        private int _missReportCountdown;

        /// <summary>
        /// Says where the beam went when it did not land on the panel.
        /// <para>
        /// A beam that is never cast, one that misses the plane entirely and one that crosses it just
        /// outside the panel all look identical from inside the headset: the pointer does not move.
        /// They are three different faults. Throttled, because a miss is the normal state whenever
        /// the player is not pointing at anything.
        /// </para>
        /// </summary>
        private void ReportMiss(string what, RectTransform panel, Vector3 origin, Vector3 direction)
        {
            if (--_missReportCountdown > 0)
                return;

            _missReportCountdown = 120;

            // The one number that separates the two possible faults. Wide means the beam genuinely
            // points somewhere else, and the fault is in the pose or in the rig it is mapped through.
            // Narrow means the beam is aimed at the panel and it is the conversion into the panel's
            // own coordinates that is wrong. Nothing else told these apart.
            Vector3 toPanel = panel.position - origin;
            float offBy = Vector3.Angle(direction, toPanel);

            MelonLogger.Msg($"[VRLaser] {what}; beam is {offBy:F0} degrees off the centre of the panel " +
                            $"({toPanel.magnitude:F2} m away), " +
                            $"beam from {origin} towards {direction}, panel at {panel.position} " +
                            $"facing {panel.forward}");
        }

        /// <summary>
        /// The pointing hand, in world space, mapped through the rig exactly as the head is.
        /// </summary>
        public static bool TryGetControllerRay(out Vector3 origin, out Vector3 direction)
        {
            origin = Vector3.zero;
            direction = Vector3.forward;

            Transform rig = VRPlayer.RigOrigin;
            if (rig == null || OpenVR.System == null)
                return false;

            var role = VRConfig.LaserLeftHand
                ? ETrackedControllerRole.LeftHand
                : ETrackedControllerRole.RightHand;

            uint index = OpenVR.System.GetTrackedDeviceIndexForControllerRole(role);
            if (index == OpenVR.k_unTrackedDeviceIndexInvalid)
                return false;

            OpenVR.System.GetDeviceToAbsoluteTrackingPose(
                ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, Poses);

            TrackedDevicePose_t pose = Poses[(int)index];
            if (!pose.bPoseIsValid || !pose.bDeviceIsConnected)
                return false;

            Vector3 tracked = pose.mDeviceToAbsoluteTracking.ToUnityPosition();
            Quaternion rotation = pose.mDeviceToAbsoluteTracking.ToUnityRotation();

            origin = rig.TransformPoint(tracked - VRPlayer.HeadNeutral);

            // Tilted down from the controller's own forward: a controller held naturally points
            // somewhat below where its axis does, and aiming along the raw axis makes the beam sit
            // high of where the player believes they are pointing.
            direction = rig.rotation * rotation * Quaternion.Euler(VRConfig.LaserAngle, 0f, 0f)
                        * Vector3.forward;

            return true;
        }

        private void DrawBeam(Vector3 from, Vector3 to, bool onTarget)
        {
            EnsureLine();

            if (_line == null)
                return;

            _line.gameObject.SetActive(true);
            _line.SetPosition(0, from);
            _line.SetPosition(1, to);

            Color colour = onTarget
                ? new Color(0.4f, 0.8f, 1f, 0.9f)
                : new Color(1f, 1f, 1f, 0.25f);

            _line.startColor = colour;
            _line.endColor = colour;
        }

        /// <summary>
        /// Shows the beam with nothing to land on, for pointing at the world rather than a panel.
        /// <para>
        /// The inventory has no panel to hit: the game finds what is under the pointer by projecting
        /// through its own camera, so what the beam is for here is telling the player where that
        /// camera is aimed. Drawn to a fixed length for the same reason.
        /// </para>
        /// </summary>
        public bool ShowRay(out Vector3 origin, out Vector3 direction)
        {
            origin = Vector3.zero;
            direction = Vector3.forward;

            if (!VRConfig.LaserPointer)
            {
                Hide();
                return false;
            }

            if (!TryGetControllerRay(out origin, out direction))
            {
                Hide();
                return false;
            }

            DrawBeam(origin, origin + direction * VRConfig.InventoryLaserLength, true);
            IsAiming = true;
            return true;
        }

        public void Hide()
        {
            IsAiming = false;

            if (_line != null)
                _line.gameObject.SetActive(false);
        }

        private void EnsureLine()
        {
            if (_line != null)
                return;

            try
            {
                _owner = new GameObject("VRLaser");
                UnityEngine.Object.DontDestroyOnLoad(_owner);

                _line = _owner.AddComponent<LineRenderer>();
                _line.useWorldSpace = true;
                _line.positionCount = 2;
                _line.startWidth = VRConfig.LaserWidth;
                _line.endWidth = VRConfig.LaserWidth * 0.5f;
                _line.numCapVertices = 4;

                // A shader that the game already carries: nothing can be added to a built game, so
                // the beam borrows one that the interface itself relies on being present.
                Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                if (shader != null)
                    _line.material = new Material(shader);

                MelonLogger.Msg($"[VRLaser] beam created using '{(shader != null ? shader.name : "no shader")}'");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[VRLaser] Could not create the beam: {e.Message}");
            }
        }
    }
}
