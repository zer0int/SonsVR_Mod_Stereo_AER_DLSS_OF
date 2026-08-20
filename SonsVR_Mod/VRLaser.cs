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
                DrawBeam(origin, origin + direction * VRConfig.LaserRange, false);
                return false;
            }

            Vector3 hit = ray.GetPoint(distance);

            // Into the panel's own space, which is the same space its controls are placed in, so no
            // conversion through a camera or a screen position is involved at all.
            Vector3 localPoint = panel.InverseTransformPoint(hit);
            Vector2 half = panel.sizeDelta * 0.5f;

            bool onPanel = Mathf.Abs(localPoint.x) <= half.x && Mathf.Abs(localPoint.y) <= half.y;

            DrawBeam(origin, hit, onPanel);

            if (!onPanel)
                return false;

            local = new Vector2(localPoint.x, localPoint.y);
            IsAiming = true;
            return true;
        }

        /// <summary>
        /// The pointing hand, in world space, mapped through the rig exactly as the head is.
        /// </summary>
        private static bool TryGetControllerRay(out Vector3 origin, out Vector3 direction)
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
