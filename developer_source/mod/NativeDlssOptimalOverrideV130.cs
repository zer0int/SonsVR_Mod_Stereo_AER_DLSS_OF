using System;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace SonsVRMod
{
    /// <summary>
    /// V130: opt-in-in-source, 2E-only HDRP DLSS optimal-settings A/B experiment.
    /// V129 established that native quality routing works, but every enabled quality
    /// mode uses an identical 50% render lattice. Only when the gameplay native
    /// camera's HDAdditionalCameraData has already been copied do we set the two
    /// eye-local attributes needed to make HDRP's optimal policy eligible:
    ///   deepLearningSuperSamplingUseCustomAttributes = true
    ///   deepLearningSuperSamplingUseOptimalSettings = true
    /// No native manager calls, no scaler-slot changes, no resolution writes,
    /// no render-target changes, and no AER policy changes. V129 remains active.
    /// </summary>
    internal static class NativeDlssOptimalOverrideV130
    {
        // Experiment gate: change only this one source constant to compare V129 behavior.
        internal const bool ExperimentEnabled = true;
        private static readonly FieldInfo HdrpCopyField = typeof(StereoRender).GetField(
            "_hdrpDataCopiedOnce", BindingFlags.NonPublic | BindingFlags.Instance);
        private static Camera _left;
        private static Camera _right;
        private static bool _leftAttrs;
        private static bool _leftOptimal;
        private static bool _rightAttrs;
        private static bool _rightOptimal;
        private static bool _applied;
        private static bool _reportedDrift;
        private static float _nextCheck;
        private static int _errors;

        internal static void Tick()
        {
            if (!ExperimentEnabled) return;
            float now = Time.unscaledTime;
            if (now < _nextCheck) return;
            _nextCheck = now + 0.5f;
            try
            {
                if (StereoRender.UserGameplayAERV112 || !NativeDlssReceiverRouterV128.Routed)
                {
                    Restore("AER or native route inactive");
                    return;
                }
                StereoRender stereo = VRPlayer.Instance != null ? VRPlayer.Instance.StereoRender : null;
                if (stereo == null || stereo.LeftCam == null || stereo.RightCam == null)
                {
                    Restore("VR cameras unavailable");
                    return;
                }
                if (_applied && (_left != stereo.LeftCam || _right != stereo.RightCam))
                    Restore("VR eye cameras changed");

                // Explicitly wait for the one-time native HDRP CopyTo(eye) at the
                // gameplay gate; setting these attributes during loading would
                // be overwritten by that copy (verified in V129).
                if (HdrpCopyField == null ||
                    !(HdrpCopyField.GetValue(stereo) is bool copied) || !copied)
                    return;

                HDAdditionalCameraData l = stereo.LeftCam.GetComponent<HDAdditionalCameraData>();
                HDAdditionalCameraData r = stereo.RightCam.GetComponent<HDAdditionalCameraData>();
                if (l == null || r == null) return;

                if (!_applied)
                {
                    _left = stereo.LeftCam;
                    _right = stereo.RightCam;
                    _leftAttrs = l.deepLearningSuperSamplingUseCustomAttributes;
                    _leftOptimal = l.deepLearningSuperSamplingUseOptimalSettings;
                    _rightAttrs = r.deepLearningSuperSamplingUseCustomAttributes;
                    _rightOptimal = r.deepLearningSuperSamplingUseOptimalSettings;
                    try
                    {
                        l.deepLearningSuperSamplingUseCustomAttributes = true;
                        l.deepLearningSuperSamplingUseOptimalSettings = true;
                        r.deepLearningSuperSamplingUseCustomAttributes = true;
                        r.deepLearningSuperSamplingUseOptimalSettings = true;
                        _applied = true;
                        _reportedDrift = false;
                        MelonLogger.Warning("[V130 DLSS-OPT] APPLIED after gameplay native HDRP CopyTo: " +
                            "L[" + _leftAttrs + "," + _leftOptimal + "] => [" +
                            l.deepLearningSuperSamplingUseCustomAttributes + "," +
                            l.deepLearningSuperSamplingUseOptimalSettings + "]; R[" +
                            _rightAttrs + "," + _rightOptimal + "] => [" +
                            r.deepLearningSuperSamplingUseCustomAttributes + "," +
                            r.deepLearningSuperSamplingUseOptimalSettings + "]. " +
                            "Game menu still owns DLSS on/off and quality; V129 samples the live lattice.");
                    }
                    catch
                    {
                        Restore("partial policy write failed");
                        throw;
                    }
                }
                else if (!_reportedDrift &&
                    (!l.deepLearningSuperSamplingUseCustomAttributes ||
                     !l.deepLearningSuperSamplingUseOptimalSettings ||
                     !r.deepLearningSuperSamplingUseCustomAttributes ||
                     !r.deepLearningSuperSamplingUseOptimalSettings))
                {
                    _reportedDrift = true;
                    MelonLogger.Warning("[V130 DLSS-OPT] OVERRIDE LOST: " +
                        "another policy writer changed the camera attributes. " +
                        "Not fighting/reapplying; V129 trace records the resulting state. " +
                        "L=" + l.deepLearningSuperSamplingUseCustomAttributes + "/" +
                        l.deepLearningSuperSamplingUseOptimalSettings + " R=" +
                        r.deepLearningSuperSamplingUseCustomAttributes + "/" +
                        r.deepLearningSuperSamplingUseOptimalSettings);
                }
            }
            catch (Exception e)
            {
                if (_errors++ < 5)
                    MelonLogger.Warning("[V130 DLSS-OPT] " + e.GetType().Name + ": " + e.Message);
            }
        }

        internal static void Restore(string reason)
        {
            if (!_applied && _left == null && _right == null) return;
            // Restore only attributes this experiment owns, not native menu state,
            // receiver mappings, frame settings or anything in the AER renderer.
            try
            {
                HDAdditionalCameraData l = _left != null ? _left.GetComponent<HDAdditionalCameraData>() : null;
                if (l != null)
                {
                    l.deepLearningSuperSamplingUseCustomAttributes = _leftAttrs;
                    l.deepLearningSuperSamplingUseOptimalSettings = _leftOptimal;
                }
            }
            catch (Exception e) { MelonLogger.Warning("[V130 DLSS-OPT] left restore: " + e.Message); }
            try
            {
                HDAdditionalCameraData r = _right != null ? _right.GetComponent<HDAdditionalCameraData>() : null;
                if (r != null)
                {
                    r.deepLearningSuperSamplingUseCustomAttributes = _rightAttrs;
                    r.deepLearningSuperSamplingUseOptimalSettings = _rightOptimal;
                }
            }
            catch (Exception e) { MelonLogger.Warning("[V130 DLSS-OPT] right restore: " + e.Message); }
            _left = null;
            _right = null;
            _applied = false;
            _reportedDrift = false;
            MelonLogger.Warning("[V130 DLSS-OPT] RESTORED on " + reason);
        }
    }
}
