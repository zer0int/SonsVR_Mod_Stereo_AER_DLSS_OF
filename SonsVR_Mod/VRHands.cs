using System;
using MelonLoader;
using UnityEngine;
using Valve.VR;
using Il2CppRootMotion.FinalIK;
using Mathf = UnityEngine.Mathf;

namespace SonsVRMod
{
    /// <summary>
    /// Drives the arms of the character from the motion controllers.
    /// <para>
    /// The game already carries Final IK's <c>BipedIK</c> on the player, with its bone references
    /// filled in and its arm solvers ready, but disabled and pointed at nothing. So no solver is
    /// added and no bone is looked up by name: the existing one is switched on and told where the
    /// hands should be.
    /// </para>
    /// <para>
    /// Controller poses are read in the same tracking space as the head and mapped into the world
    /// through the same rig transform. Hands and head therefore agree by construction, rather than by
    /// two conversions that have to be kept in step.
    /// </para>
    /// </summary>
    internal class VRHands
    {
        private BipedIK _biped;
        private GameObject _owner;
        private bool _announced;
        private int _reportCountdown;
        private Transform _leftGoal;
        private Transform _rightGoal;

        private static readonly TrackedDevicePose_t[] Poses =
            new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

        /// <summary>Forgets the character, so a new one is picked up cleanly.</summary>
        public void Reset()
        {
            _biped = null;
            _owner = null;
            _announced = false;
        }

        /// <summary>
        /// Places both hands for this frame. Called from the rig update, after the head pose has been
        /// applied, so the same origin is used for both.
        /// </summary>
        public void Update(GameObject localPlayer, Transform origin, Vector3 headNeutral)
        {
            if (!VRConfig.HandTracking || localPlayer == null || origin == null)
                return;

            if (!Acquire(localPlayer))
                return;

            // Checked every frame, not only when acquired.
            //
            // These are the game's own objects, reached through the interop layer, and this code
            // writes into them on every frame of play. If one is torn down while we still hold it,
            // the call goes through a pointer that is no longer there, which is not an exception the
            // runtime can catch: it takes the process with it. The event log recorded exactly that,
            // an access violation at address zero, so the reference is verified rather than assumed.
            if (!IsUsable())
            {
                _biped = null;
                _owner = null;
                return;
            }

            // The game leaves it switched off. Re-asserted rather than set once: anything that turns
            // it off again would otherwise silently stop the arms following.
            if (!_biped.enabled)
                _biped.enabled = true;

            PlaceElbowGoals(localPlayer.transform);

            ApplyHand(ETrackedControllerRole.LeftHand, _biped.solvers.leftHand, origin, headNeutral,
                VRConfig.HandBaseRotationLeft, VRConfig.HandRotationOffsetLeft);

            ApplyHand(ETrackedControllerRole.RightHand, _biped.solvers.rightHand, origin, headNeutral,
                VRConfig.HandBaseRotationRight, VRConfig.HandRotationOffsetRight);

            Report();
        }

        /// <summary>
        /// True when the solver and the bones it drives are all still there to be written to.
        /// </summary>
        private bool IsUsable()
        {
            try
            {
                if (_biped == null || _biped.solvers == null || _biped.references == null)
                    return false;

                if (_biped.references.leftHand == null || _biped.references.rightHand == null)
                    return false;

                return _biped.solvers.leftHand != null && _biped.solvers.rightHand != null;
            }
            catch
            {
                return false;
            }
        }

        private bool Acquire(GameObject localPlayer)
        {
            if (_biped != null && _owner == localPlayer)
                return true;

            _owner = localPlayer;
            _biped = localPlayer.GetComponentInChildren<BipedIK>(true);

            if (_biped == null)
            {
                if (!_announced)
                {
                    _announced = true;
                    MelonLogger.Warning("[VRHands] No BipedIK on the player: arms cannot follow the controllers");
                }

                return false;
            }

            MelonLogger.Msg($"[VRHands] Driving the arms through BipedIK on '{_biped.name}' " +
                            $"(left hand '{Name(_biped.references.leftHand)}', " +
                            $"right hand '{Name(_biped.references.rightHand)}')");

            ConfigureElbows(_biped.solvers.leftHand, "left", ref _leftGoal);
            ConfigureElbows(_biped.solvers.rightHand, "right", ref _rightGoal);

            return true;
        }

        /// <summary>
        /// Decides where the elbows go.
        /// <para>
        /// Position and rotation of the hand do not determine the arm: the elbow can swing anywhere
        /// on a circle around the line from shoulder to hand, and with nothing said about it the
        /// solver picked a plane that put both elbows into the body. Final IK carries a modifier
        /// built for arms, which keeps the elbow where a real one would sit for a given hand pose.
        /// </para>
        /// </summary>
        private void ConfigureElbows(IKSolverLimb solver, string side, ref Transform goal)
        {
            if (solver == null)
                return;

            try
            {
                if (goal == null)
                {
                    goal = new GameObject($"VRElbowGoal_{side}").transform;
                    UnityEngine.Object.DontDestroyOnLoad(goal.gameObject);
                }

                // A point in space for the elbow to reach towards, rather than a rule for guessing
                // where it should go. The built in estimate put both elbows on the wrong side, giving
                // arms that closed inwards when the hands were pulled back instead of opening out.
                solver.bendGoal = goal;
                solver.bendModifier = IKSolverLimb.BendModifier.Goal;
                solver.bendModifierWeight = Mathf.Clamp01(VRConfig.ElbowBendWeight);

                MelonLogger.Msg($"[VRHands] {side} elbow aimed at a goal, weight " +
                                $"{solver.bendModifierWeight:F2}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[VRHands] Could not set the {side} elbow behaviour: {e.Message}");
            }
        }

        /// <summary>
        /// Places the elbow goals, out to the side of each shoulder and a little back and down, which
        /// is where an elbow rests on a body.
        /// <para>
        /// Positioned in the character's own frame rather than the world, so they stay to the outside
        /// as the character turns. The sign of the sideways offset is what makes the arms open out
        /// instead of folding across the chest.
        /// </para>
        /// </summary>
        private void PlaceElbowGoals(Transform body)
        {
            if (body == null)
                return;

            PlaceGoal(_leftGoal, _biped.references.leftUpperArm, body, -1f);
            PlaceGoal(_rightGoal, _biped.references.rightUpperArm, body, 1f);
        }

        private static void PlaceGoal(Transform goal, Transform upperArm, Transform body, float side)
        {
            if (goal == null || upperArm == null)
                return;

            goal.position = upperArm.position
                            + body.right * (VRConfig.ElbowOutward * side)
                            - body.forward * VRConfig.ElbowBack
                            - body.up * VRConfig.ElbowDown;
        }

        /// <summary>
        /// Points one arm solver at its controller.
        /// </summary>
        private void ApplyHand(ETrackedControllerRole role, IKSolverLimb solver, Transform origin,
            Vector3 headNeutral, Vector3 baseRotation, Vector3 adjustment)
        {
            if (solver == null)
                return;

            if (!TryGetControllerPose(role, out Vector3 trackedPosition, out Quaternion trackedRotation))
            {
                // Nothing tracked: hand the arm back to the animation rather than leave it pinned
                // wherever the controller was last seen.
                solver.IKPositionWeight = 0f;
                solver.IKRotationWeight = 0f;
                return;
            }

            // Same mapping the head goes through: measured from the neutral pose, then placed by the
            // rig. Using the identical chain is what keeps the hands attached to the head.
            Vector3 local = trackedPosition - headNeutral;

            solver.IKPosition = origin.TransformPoint(local);
            // Two corrections, in order. The first turns the controller pose into a hand pose for
            // this skeleton and is fixed. The second is the player's own adjustment, applied in the
            // frame the first one leaves behind, so it reads as moving the hand rather than the
            // controller: at zero the hands are already right.
            solver.IKRotation = origin.rotation * trackedRotation
                                * Quaternion.Euler(baseRotation)
                                * Quaternion.Euler(adjustment);

            solver.IKPositionWeight = VRConfig.HandIKWeight;
            solver.IKRotationWeight = VRConfig.HandIKWeight;
        }

        /// <summary>
        /// Controller pose in the tracking space the head is reported in.
        /// <para>
        /// Read here rather than through the action poses, which are given in the raw, uncalibrated
        /// space. Mixing the two spaces is a mistake this mod has already made once, on the shoulder
        /// reading, and it produces an error that varies per player and per room.
        /// </para>
        /// </summary>
        private static bool TryGetControllerPose(ETrackedControllerRole role, out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            if (OpenVR.System == null)
                return false;

            uint index = OpenVR.System.GetTrackedDeviceIndexForControllerRole(role);
            if (index == OpenVR.k_unTrackedDeviceIndexInvalid)
                return false;

            OpenVR.System.GetDeviceToAbsoluteTrackingPose(
                ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, Poses);

            TrackedDevicePose_t pose = Poses[(int)index];
            if (!pose.bPoseIsValid || !pose.bDeviceIsConnected)
                return false;

            position = pose.mDeviceToAbsoluteTracking.ToUnityPosition();
            rotation = pose.mDeviceToAbsoluteTracking.ToUnityRotation();
            return true;
        }

        private void Report()
        {
            if (--_reportCountdown > 0)
                return;

            _reportCountdown = 300;

            MelonLogger.Msg($"[VRHands] weights left {_biped.solvers.leftHand.IKPositionWeight:F2}, " +
                            $"right {_biped.solvers.rightHand.IKPositionWeight:F2}, " +
                            $"solver enabled {_biped.enabled}");
        }

        private static string Name(Transform t)
        {
            return t != null ? t.name : "none";
        }
    }
}
