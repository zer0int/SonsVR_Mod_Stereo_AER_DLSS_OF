using System;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace SonsVRMod
{
    /// <summary>
    /// Reports everything needed to drive the player's arms from the controllers, before any attempt
    /// is made to do so.
    /// <para>
    /// The game ships Final IK, so the solver is already available and the question is what to point
    /// it at: which skeleton the visible arms belong to, whether that skeleton is described as
    /// humanoid, which animator writes to it, and whether the game already runs inverse kinematics on
    /// the same bones. Guessing at any of those and building on the guess is how the rest of this mod
    /// has cost the most time.
    /// </para>
    /// </summary>
    internal static class VRArmRecon
    {
        private static bool _done;

        public static void Run(GameObject localPlayer)
        {
            if (_done || localPlayer == null || !VRConfig.LogArmSkeleton)
                return;

            _done = true;

            MelonLogger.Msg("[VRArmRecon] ==== player rig survey ====");

            try
            {
                ReportAnimators(localPlayer);
                ReportExistingIK(localPlayer);
                ReportBipedIK(localPlayer);
                ReportSkinnedMeshes(localPlayer);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[VRArmRecon] Survey stopped: {e}");
            }

            MelonLogger.Msg("[VRArmRecon] ==== end of survey ====");
        }

        /// <summary>
        /// Animators, and for any described as humanoid the arm chain by name.
        /// <para>
        /// A humanoid avatar is the good case: the bones can be asked for by role rather than found by
        /// name, which no longer depends on how this particular skeleton was labelled.
        /// </para>
        /// </summary>
        private static void ReportAnimators(GameObject localPlayer)
        {
            var animators = localPlayer.GetComponentsInChildren<Animator>(true);
            MelonLogger.Msg($"[VRArmRecon] {animators.Length} animators");

            foreach (var animator in animators)
            {
                if (animator == null)
                    continue;

                bool human = animator.avatar != null && animator.isHuman;

                MelonLogger.Msg($"[VRArmRecon]   '{PathOf(animator.transform, localPlayer.transform)}' " +
                                $"avatar {(animator.avatar != null ? animator.avatar.name : "none")}, " +
                                $"humanoid {human}, enabled {animator.enabled}, " +
                                $"cullingMode {animator.cullingMode}, updateMode {animator.updateMode}");

                if (!human)
                    continue;

                ReportBone(animator, HumanBodyBones.Chest, localPlayer);
                ReportBone(animator, HumanBodyBones.LeftShoulder, localPlayer);
                ReportBone(animator, HumanBodyBones.LeftUpperArm, localPlayer);
                ReportBone(animator, HumanBodyBones.LeftLowerArm, localPlayer);
                ReportBone(animator, HumanBodyBones.LeftHand, localPlayer);
                ReportBone(animator, HumanBodyBones.RightShoulder, localPlayer);
                ReportBone(animator, HumanBodyBones.RightUpperArm, localPlayer);
                ReportBone(animator, HumanBodyBones.RightLowerArm, localPlayer);
                ReportBone(animator, HumanBodyBones.RightHand, localPlayer);

                ReportHandContents(animator.GetBoneTransform(HumanBodyBones.LeftHand), "left", localPlayer);
                ReportHandContents(animator.GetBoneTransform(HumanBodyBones.RightHand), "right", localPlayer);
            }
        }

        private static void ReportBone(Animator animator, HumanBodyBones bone, GameObject localPlayer)
        {
            Transform t = animator.GetBoneTransform(bone);

            MelonLogger.Msg($"[VRArmRecon]     {bone}: " +
                            (t != null ? $"'{t.name}' at {PathOf(t, localPlayer.transform)}" : "not mapped"));
        }

        /// <summary>
        /// What hangs off each hand. Tools and weapons are parented there, so moving a hand moves them,
        /// and anything the game measures from those objects moves with it.
        /// </summary>
        private static void ReportHandContents(Transform hand, string side, GameObject localPlayer)
        {
            if (hand == null)
                return;

            var names = new StringBuilder();
            for (int i = 0; i < hand.childCount && i < 12; i++)
            {
                if (names.Length > 0)
                    names.Append(", ");

                names.Append(hand.GetChild(i).name);
            }

            MelonLogger.Msg($"[VRArmRecon]     {side} hand carries {hand.childCount}: {names}");
        }

        /// <summary>
        /// Inverse kinematics the game already runs. A second solver on the same bones would fight the
        /// first, so an existing one has to be extended rather than joined.
        /// </summary>
        private static void ReportExistingIK(GameObject localPlayer)
        {
            var components = localPlayer.GetComponentsInChildren<Component>(true);
            int found = 0;

            foreach (var component in components)
            {
                if (component == null)
                    continue;

                string typeName;
                try
                {
                    typeName = component.GetIl2CppType().FullName;
                }
                catch
                {
                    continue;
                }

                if (typeName == null || typeName.IndexOf("IK", StringComparison.Ordinal) < 0)
                    continue;

                if (++found > 20)
                    break;

                MelonLogger.Msg($"[VRArmRecon]   IK component: {typeName} " +
                                $"on '{PathOf(component.transform, localPlayer.transform)}'");
            }

            MelonLogger.Msg($"[VRArmRecon] {found} inverse kinematics components " +
                            $"among {components.Length} components in total");
        }

        /// <summary>
        /// The solver the game already runs on the player, read through its own type.
        /// <para>
        /// This is the one that matters. Its arm solvers are exactly what has to be pointed at the
        /// controllers, and it already carries the game's own bone references, which settles the arm
        /// chain without having to recognise bones by name on a skeleton that is not described as
        /// humanoid.
        /// </para>
        /// <para>
        /// The weights are the other half of the answer: whatever the game leaves them at is what the
        /// arms are doing now, and taking them to one is what will hand the arms over.
        /// </para>
        /// </summary>
        private static void ReportBipedIK(GameObject localPlayer)
        {
            var biped = localPlayer.GetComponentInChildren<Il2CppRootMotion.FinalIK.BipedIK>(true);
            if (biped == null)
            {
                MelonLogger.Msg("[VRArmRecon] no BipedIK found");
                return;
            }

            MelonLogger.Msg($"[VRArmRecon] BipedIK on '{PathOf(biped.transform, localPlayer.transform)}', " +
                            $"enabled {biped.enabled}");

            var references = biped.references;
            if (references != null)
            {
                MelonLogger.Msg($"[VRArmRecon]   references: " +
                                $"root '{Name(references.root)}', pelvis '{Name(references.pelvis)}', " +
                                $"leftUpperArm '{Name(references.leftUpperArm)}', " +
                                $"leftForearm '{Name(references.leftForearm)}', " +
                                $"leftHand '{Name(references.leftHand)}'");

                MelonLogger.Msg($"[VRArmRecon]   references: " +
                                $"rightUpperArm '{Name(references.rightUpperArm)}', " +
                                $"rightForearm '{Name(references.rightForearm)}', " +
                                $"rightHand '{Name(references.rightHand)}', " +
                                $"spine bones {(references.spine != null ? references.spine.Length : 0)}");

                if (references.leftHand != null)
                    MelonLogger.Msg($"[VRArmRecon]   left hand path: {PathOf(references.leftHand, localPlayer.transform)}");

                if (references.rightHand != null)
                    MelonLogger.Msg($"[VRArmRecon]   right hand path: {PathOf(references.rightHand, localPlayer.transform)}");
            }

            var solvers = biped.solvers;
            if (solvers != null)
            {
                ReportSolver("leftHand", solvers.leftHand);
                ReportSolver("rightHand", solvers.rightHand);
            }
        }

        private static void ReportSolver(string label, Il2CppRootMotion.FinalIK.IKSolverLimb solver)
        {
            if (solver == null)
            {
                MelonLogger.Msg($"[VRArmRecon]   solver {label}: none");
                return;
            }

            MelonLogger.Msg($"[VRArmRecon]   solver {label}: " +
                            $"position weight {solver.IKPositionWeight:F2}, " +
                            $"rotation weight {solver.IKRotationWeight:F2}, " +
                            $"target '{Name(solver.target)}', " +
                            $"bend '{Name(solver.bendGoal)}'");
        }

        private static string Name(Transform t)
        {
            return t != null ? t.name : "none";
        }

        /// <summary>
        /// The visible meshes. First person arms are often a separate skinned mesh from the body, on a
        /// skeleton of their own, and the arms that need to follow the controllers are the ones the
        /// player can actually see.
        /// </summary>
        private static void ReportSkinnedMeshes(GameObject localPlayer)
        {
            var meshes = localPlayer.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            MelonLogger.Msg($"[VRArmRecon] {meshes.Length} skinned meshes");

            int reported = 0;
            foreach (var mesh in meshes)
            {
                if (mesh == null || ++reported > 12)
                    continue;

                MelonLogger.Msg($"[VRArmRecon]   '{mesh.name}' at {PathOf(mesh.transform, localPlayer.transform)}, " +
                                $"enabled {mesh.enabled}, bones {(mesh.bones != null ? mesh.bones.Length : 0)}, " +
                                $"root '{(mesh.rootBone != null ? mesh.rootBone.name : "none")}'");
            }
        }

        /// <summary>Path of a transform relative to the player root, for locating it again later.</summary>
        private static string PathOf(Transform transform, Transform root)
        {
            var path = new StringBuilder(transform.name);

            Transform current = transform.parent;
            int guard = 0;

            while (current != null && current != root && ++guard < 32)
            {
                path.Insert(0, current.name + "/");
                current = current.parent;
            }

            return path.ToString();
        }
    }
}
