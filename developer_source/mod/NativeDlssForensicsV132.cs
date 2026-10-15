using System;
using System.Reflection;
using System.Threading;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SonsVRMod
{
    /// <summary>
    /// V132: staged 2E-only experiment, separate from the V128 native quality router.
    /// Stage 0  : 30 seconds passive NVIDIA/HDRP measurements.
    /// Stage A  : warning, then CustomAttrs=true / Optimal=false and (when safely
    ///            reversible) a native dynamic-resolution percentage callback.
    /// Stage B  : warning, then the public Unity 2022.3 bug-report workaround:
    ///            DynamicResolutionHandler.m_MinScreenFraction = 0.25f.
    /// Shutdown : restore each value actually changed and the original callback
    ///            when its previous delegate and policy were recoverable.
    /// NO AER interventions, no HUD architecture or shader changes.
    /// </summary>
    internal static class NativeDlssForensicsV132
    {
        internal static bool CueActive => _state >= 1;
        private const float BaselineSeconds = 30f;
        private const float WarningSeconds = 5f;
        private const float CompareSeconds = 20f;
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.Static |
                                       BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo CopyFlag = typeof(StereoRender).GetField("_hdrpDataCopiedOnce", F);
        private static int _state; // 0 waiting, 1 baseline, 2 warning A, 3 cue clear A, 4 compare A, 5 warning B, 6 cue clear B, 7 compare B, 9 done
        private static float _phaseAt;
        private static float _next;
        private static float _nextSnapshot;
        private static int _lastWarning;
        private static int _clearFrame;
        private static Camera _left, _right;
        private static bool _leftAttrs, _rightAttrs, _leftOptimal, _rightOptimal;
        private static bool _cameraChanged;
        private static Type _handlerType;
        private static object _handler;
        private static FieldInfo _minField;
        private static float _originalMin;
        private static bool _minChanged;
        private static MethodInfo _setScaler;
        private static object _oldScaler;
        private static object _oldPolicy;
        private static bool _scalerChanged;
        private static Delegate _managedScaler;
        private static object _nativeScaler;
        private static long _scalerCalls;
        private static int _attempts;
        private static Type _managerType;
        private static object _lastQuality;
        private static FieldInfo _qualityField;
        private static volatile float _cachedPercentage = 66.7f;

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 0.12f;
            try
            {
                LateCueTick();
                if (_state == 9) return;
                _cachedPercentage = ComputeRequestedPercent();
                if (StereoRender.UserGameplayAERV112 || !NativeDlssReceiverRouterV128.Routed)
                {
                    if (_state > 0) Abort("AER / native receiver route inactive");
                    return;
                }
                StereoRender stereo = VRPlayer.Instance != null ? VRPlayer.Instance.StereoRender : null;
                if (stereo == null || stereo.LeftCam == null || stereo.RightCam == null ||
                    CopyFlag == null || !(CopyFlag.GetValue(stereo) is bool copied) || !copied) return;
                if (_state > 0 && (_left != stereo.LeftCam || _right != stereo.RightCam))
                { Abort("eye-camera identity changed"); return; }
                if (_state == 0)
                {
                    _left = stereo.LeftCam;
                    _right = stereo.RightCam;
                    RememberCameraPolicy();
                    DiscoverDrs();
                    NvidiaFeatureTraceV132.Initialize();
                    _phaseAt = now;
                    _state = 1;
                    Log("BASELINE_START: native 2E route confirmed; 30 seconds passive. The V129 TSV is the actual render-size authority.");
                    Snapshot("BASELINE_START");
                    return;
                }
                if (now >= _nextSnapshot)
                {
                    _nextSnapshot = now + 2f;
                    Snapshot("PERIODIC");
                }
                if (_state == 1 && now - _phaseAt >= BaselineSeconds)
                    BeginWarning(2, "A: MANUAL DLSS SCALE", "OPTIMAL OFF / SCALER TEST");
                if (_state == 2 || _state == 5)
                {
                    float leftSeconds = WarningSeconds - (now - _phaseAt);
                    if (leftSeconds > 0)
                    {
                        int remaining = Mathf.CeilToInt(leftSeconds);
                        if (_lastWarning != remaining)
                        {
                            _lastWarning = remaining;
                            SetCue("V132 DLSS EXPERIMENT\n" + (_state == 2 ? "A: DISABLE OPTIMAL + TEST SCALER" : "B: UNITY 2022.3 MIN-FRACTION FIX") +
                                "\nIN " + remaining + " SECONDS\nPOSSIBLE VISUAL CHANGE / CRASH");
                        }
                        return;
                    }
                    SetCue("");
                    _clearFrame = Time.frameCount;
                    _state = _state == 2 ? 3 : 6;
                    Log("WARNING_CLEAR: waiting >=2 frames before intervention");
                    return;
                }
                if (_state == 3 && Time.frameCount >= _clearFrame + 2)
                {
                    Flush("PRE_MANUAL_OVERRIDE");
                    ApplyManualMode();
                    _phaseAt = now + 3f; // measurement starts only AFTER phase message disappears
                    _state = 4;
                    SetCue("V132 A STARTED\nSWITCH QUALITY <-> ULTRA PERFORMANCE\nCAPTURING FOR ~20 SECONDS");
                    _cueClearAt = now + 3f;
                    return;
                }
                if (_state == 4 && now - _phaseAt >= CompareSeconds)
                    BeginWarning(5, "B: MIN-FRACTION PATCH", "UNITY 2022.3 WORKAROUND");
                if (_state == 6 && Time.frameCount >= _clearFrame + 2)
                {
                    Flush("PRE_MIN_FRACTION_PATCH");
                    ApplyMinFractionFix();
                    _phaseAt = now + 3f; // measurement starts only AFTER phase message disappears
                    _state = 7;
                    SetCue("V132 B STARTED\nSWITCH QUALITY <-> ULTRA PERFORMANCE\nCAPTURING FOR ~20 SECONDS");
                    _cueClearAt = now + 3f;
                    return;
                }
                if (_state == 7 && now - _phaseAt >= CompareSeconds)
                {
                    Snapshot("B_COMPLETE");
                    Flush("B_COMPLETE_BEFORE_RESTORE");
                    Restore("experiment complete");
                    _state = 9;
                    SetCue("V132 COMPLETE\nRESTORED - YOU CAN EXIT / PACK TRACE");
                    _completionCueAt = now + 5f;
                    Log("COMPLETE: both intervention windows finished; restoring policy");
                }
                if (_cueClearAt > 0f && now >= _cueClearAt)
                {
                    SetCue("");
                    _cueClearAt = 0f;
                    Flush("PHASE_" + _state + "_MEASUREMENT_START_CUE_HIDDEN");
                }
            }
            catch (Exception e)
            {
                Log("EXCEPTION " + e.GetType().Name + ": " + e.Message);
                Abort("exception");
            }
        }
        internal static void Shutdown()
        {
            if (_state != 9) { try { Restore("application quit"); } catch { } }
            SetCue("");
            _state = 9;
        }
        private static float _cueClearAt, _completionCueAt;
        internal static void LateCueTick()
        {
            // After completion the overlay updater must still consume the empty revision.
            if (_completionCueAt > 0 && Time.unscaledTime >= _completionCueAt)
            {
                _completionCueAt = 0f;
                SetCue("");
            }
        }
        private static void BeginWarning(int state, string label, string description)
        {
            Snapshot("BEFORE_" + label);
            Flush("BASELINE_END_OR_A_COMPLETE_" + state);
            _state = state;
            _phaseAt = Time.unscaledTime;
            _lastWarning = -1;
            Log("WARNING_START " + label + " " + description + " for 5s");
        }
        private static void RememberCameraPolicy()
        {
            HDAdditionalCameraData l = _left.GetComponent<HDAdditionalCameraData>();
            HDAdditionalCameraData r = _right.GetComponent<HDAdditionalCameraData>();
            if (l == null || r == null) throw new InvalidOperationException("HDRP eye data missing");
            _leftAttrs = l.deepLearningSuperSamplingUseCustomAttributes;
            _rightAttrs = r.deepLearningSuperSamplingUseCustomAttributes;
            _leftOptimal = l.deepLearningSuperSamplingUseOptimalSettings;
            _rightOptimal = r.deepLearningSuperSamplingUseOptimalSettings;
            Log("CAMERA_INITIAL L=" + _leftAttrs + "/" + _leftOptimal + " R=" + _rightAttrs + "/" + _rightOptimal);
        }
        private static void DiscoverDrs()
        {
            _handlerType = typeof(DynamicResolutionHandler);
            try { _handler = _handlerType.GetProperty("instance", F)?.GetValue(null, null); }
            catch (Exception e) { Log("HANDLER_GET_FAIL " + e.Message); }
            _minField = _handlerType.GetField("m_MinScreenFraction", F);
            Log("DRS_HANDLER " + (_handler != null ? "FOUND" : "NULL") +
                " minField=" + (_minField != null ? _minField.FieldType.FullName : "MISSING"));
            if (_handler != null && _minField != null && _minField.FieldType == typeof(float))
                _originalMin = (float)_minField.GetValue(_handler);
            Log("MIN_INITIAL=" + (_minField != null && _handler != null ? _originalMin.ToString("F6") : "unavailable"));
            foreach (MethodInfo m in _handlerType.GetMethods(F))
                if (m.Name == "SetDynamicResScaler" && m.GetParameters().Length == 2)
                { _setScaler = m; break; }
            if (_setScaler == null) Log("SCALER_API_MISSING - no callback installation");
            else Log("SCALER_API " + _setScaler);
            // The IL2CPP delegate used by this API is potentially stored inside a private
            // scaler-slot struct. Log all candidate fields before modifying anything.
            foreach (FieldInfo f in _handlerType.GetFields(F))
            {
                string n = f.Name.ToLowerInvariant();
                if (n.Contains("scaler") || n.Contains("policy") || n.Contains("fraction"))
                {
                    try { object value = f.GetValue(f.IsStatic ? null : _handler);
                        Log("DRS_MEMBER " + f.Name + " type=" + f.FieldType.FullName +
                            " value=" + Safe(value)); }
                    catch (Exception e) { Log("DRS_MEMBER_ERR " + f.Name + " " + e.GetType().Name); }
                }
            }
        }
        private static void ApplyManualMode()
        {
            HDAdditionalCameraData l = _left.GetComponent<HDAdditionalCameraData>();
            HDAdditionalCameraData r = _right.GetComponent<HDAdditionalCameraData>();
            // Change the local policy only; Endnight's native receiver continues owning
            // enable/disable and quality. Nothing touches SteamVR or AER render targets.
            l.deepLearningSuperSamplingUseCustomAttributes = true;
            r.deepLearningSuperSamplingUseCustomAttributes = true;
            l.deepLearningSuperSamplingUseOptimalSettings = false;
            r.deepLearningSuperSamplingUseOptimalSettings = false;
            _cameraChanged = true;
            Log("PHASE_A CAMERA_WRITE L=" + l.deepLearningSuperSamplingUseCustomAttributes +
                "/" + l.deepLearningSuperSamplingUseOptimalSettings + " R=" +
                r.deepLearningSuperSamplingUseCustomAttributes + "/" +
                r.deepLearningSuperSamplingUseOptimalSettings);
            TryInstallReversibleScaler();
            Snapshot("PHASE_A_AFTER");
        }
        private static float RequestedPercent()
        {
            Interlocked.Increment(ref _scalerCalls);
            if (StereoRender.UserGameplayAERV112 || !NativeDlssReceiverRouterV128.Routed) return 50f;
            return _cachedPercentage;
        }
        private static float ComputeRequestedPercent()
        {
            string s = Safe(GetQuality()).ToLowerInvariant();
            // Native menu quality enum: 2=Quality,1=Balanced,0=Performance,3=UltraPerformance.
            if (s.Contains("ultra") || s == "3") return 33.3f;
            if (s.Contains("balanced") || s == "1") return 58f;
            if (s.Contains("maximumperformance") || s.Contains("performance") || s == "0") return 50f;
            return 66.7f;
        }
        private static object GetQuality()
        {
            if (_managerType == null)
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name.IndexOf("Sons.Dlss", StringComparison.OrdinalIgnoreCase) >= 0)
                    { _managerType = a.GetType("Il2CppSons.Dlss.DynamicResUpscaleManager", false); if (_managerType != null) break; }
            }
            if (_managerType == null) return "unknown";
            try
            {
                if (_qualityField == null) _qualityField = _managerType.GetField("_activeDlssQualitySetting", F);
                return _lastQuality = _qualityField == null ? "unknown" : _qualityField.GetValue(null);
            }
            catch { return _lastQuality ?? "unknown"; }
        }
        private static void TryInstallReversibleScaler()
        {
            if (_setScaler == null) { Log("SCALER_UNSUPPORTED: API missing"); return; }
            try
            {
                ParameterInfo[] p = _setScaler.GetParameters();
                if (p.Length != 2) { Log("SCALER_UNSUPPORTED: signature " + _setScaler); return; }
                Type delegateType = p[0].ParameterType;
                // A reversible patch requires recoverable original delegate and policy;
                // unlike the old scaler-slot hacks we refuse to overwrite unknown state.
                FieldInfo originalField = null;
                foreach (FieldInfo f in _handlerType.GetFields(F))
                {
                    if (f.FieldType == delegateType && f.IsStatic)
                    { originalField = f; break; }
                }
                if (originalField == null)
                {
                    Log("SCALER_NOT_INSTALLED: existing delegate not retrievable as a static " + delegateType.Name +
                        "; baseline preserved. MIN-FRACTION patch will still be tested independently.");
                    return;
                }
                _oldScaler = originalField.GetValue(null);
                if (_oldScaler == null) { Log("SCALER_NOT_INSTALLED: original callback null"); return; }
                // Find a policy field with exact enum type; do not invent an enum value.
                FieldInfo policyField = null;
                foreach (FieldInfo f in _handlerType.GetFields(F))
                    if (f.FieldType == p[1].ParameterType && f.IsStatic) { policyField = f; break; }
                if (policyField == null) { Log("SCALER_NOT_INSTALLED: original policy unavailable"); return; }
                _oldPolicy = policyField.GetValue(null);
                _managedScaler = new Func<float>(RequestedPercent);
                object cb = null;
                if (delegateType.IsInstanceOfType(_managedScaler)) cb = _managedScaler;
                if (cb == null)
                {
                    foreach (MethodInfo m in delegateType.GetMethods(F))
                    {
                        if (m.Name != "op_Implicit" || m.ReturnType != delegateType) continue;
                        var args = m.GetParameters();
                        if (args.Length != 1 || !args[0].ParameterType.IsInstanceOfType(_managedScaler)) continue;
                        cb = m.Invoke(null, new object[] { _managedScaler });
                        break;
                    }
                }
                if (cb == null)
                { Log("SCALER_NOT_INSTALLED: no valid managed->IL2CPP delegate conversion"); return; }
                _nativeScaler = cb; // keep alive for native callback
                object percentage = Enum.Parse(p[1].ParameterType, "ReturnsPercentage");
                _setScaler.Invoke(null, new[] { cb, percentage });
                _scalerChanged = true;
                Log("SCALER_INSTALLED: ReturnsPercentage, original=" + originalField.Name +
                    " oldPolicy=" + Safe(_oldPolicy));
            }
            catch (Exception e) { Log("SCALER_INSTALL_FAIL " + e.GetType().Name + ": " + e.Message); }
        }
        private static void ApplyMinFractionFix()
        {
            if (_handler == null || _minField == null || _minField.FieldType != typeof(float))
            { Log("MIN_FIX_UNAVAILABLE: cannot locate private float m_MinScreenFraction; no write"); return; }
            try
            {
                float before = (float)_minField.GetValue(_handler);
                _minField.SetValue(_handler, 0.25f);
                float after = (float)_minField.GetValue(_handler);
                _minChanged = Math.Abs(after - _originalMin) > 0.00001f;
                Log("MIN_FIX_WRITE original=" + _originalMin.ToString("F6") +
                    " immediatelyBefore=" + before.ToString("F6") +
                    " readBack=" + after.ToString("F6") +
                    " accepted=" + (Math.Abs(after - 0.25f) < 0.0001f));
                Snapshot("PHASE_B_AFTER");
            }
            catch (Exception e) { Log("MIN_FIX_ERROR " + e.GetType().Name + ": " + e.Message); }
        }
        private static void Snapshot(string label)
        {
            object min = "NA";
            try { if (_handler != null && _minField != null) min = _minField.GetValue(_handler); } catch { }
            object scale = "NA";
            try { scale = _handlerType?.GetMethod("GetResolvedScale", F)?.Invoke(_handler, null); } catch { }
            HDAdditionalCameraData l = _left != null ? _left.GetComponent<HDAdditionalCameraData>() : null;
            HDAdditionalCameraData r = _right != null ? _right.GetComponent<HDAdditionalCameraData>() : null;
            var lh = _left != null ? HDCamera.GetOrCreate(_left, 0) : null;
            var rh = _right != null ? HDCamera.GetOrCreate(_right, 0) : null;
            Log("SNAPSHOT " + label + " state=" + _state + " min=" + Safe(min) +
                " quality=" + Safe(GetQuality()) + " requested%=" + ComputeRequestedPercent() +
                " callbackCalls=" + Interlocked.Read(ref _scalerCalls) +
                " resolved=" + Safe(scale) +
                " L=" + (lh != null ? lh.actualWidth + "x" + lh.actualHeight : "NA") +
                " R=" + (rh != null ? rh.actualWidth + "x" + rh.actualHeight : "NA") +
                " cameraOptimal=" + (l != null ? l.deepLearningSuperSamplingUseOptimalSettings.ToString() : "NA") +
                "/" + (r != null ? r.deepLearningSuperSamplingUseOptimalSettings.ToString() : "NA") +
                " scalerInstalled=" + _scalerChanged);
            NvidiaFeatureTraceV132.Report(label);
        }
        private static void Restore(string reason)
        {
            if (_scalerChanged && _setScaler != null && _oldScaler != null && _oldPolicy != null)
            {
                try { _setScaler.Invoke(null, new[] { _oldScaler, _oldPolicy });
                    Log("SCALER_RESTORED original callback+policy " + reason); }
                catch (Exception e) { Log("SCALER_RESTORE_FAILED " + e.Message); }
            }
            _scalerChanged = false;
            if (_minChanged && _handler != null && _minField != null)
            {
                try { _minField.SetValue(_handler, _originalMin);
                    Log("MIN_RESTORED " + Safe(_minField.GetValue(_handler))); }
                catch (Exception e) { Log("MIN_RESTORE_FAILED " + e.Message); }
            }
            _minChanged = false;
            if (_cameraChanged)
            {
                try { var l = _left != null ? _left.GetComponent<HDAdditionalCameraData>() : null;
                      var r = _right != null ? _right.GetComponent<HDAdditionalCameraData>() : null;
                      if (l != null) { l.deepLearningSuperSamplingUseCustomAttributes = _leftAttrs;
                          l.deepLearningSuperSamplingUseOptimalSettings = _leftOptimal; }
                      if (r != null) { r.deepLearningSuperSamplingUseCustomAttributes = _rightAttrs;
                          r.deepLearningSuperSamplingUseOptimalSettings = _rightOptimal; }
                      Log("CAMERA_ATTRIBUTES_RESTORED " + reason); }
                catch (Exception e) { Log("CAMERA_RESTORE_FAILED " + e.Message); }
            }
            _cameraChanged = false;
            Flush("RESTORED_" + reason.Replace(' ','_'));
        }
        private static void Abort(string why)
        {
            Log("ABORT " + why);
            SetCue("");
            Restore(why);
            _state = 9;
        }
        private static void Flush(string marker)
        {
            try { NativeDlssLiveResolutionTraceV129.V131FlushMarker("V132_" + marker); } catch { }
            Log("TSV_FLUSH " + marker);
        }
        private static void SetCue(string value) { StereoRender.SetAerV102HmdCue(value); }
        private static string Safe(object v) { try { return v == null ? "null" : v.ToString().Replace('\n',' ').Replace('\t',' '); } catch { return "ERROR"; } }
        private static void Log(string message) { MelonLogger.Warning("[V132 DLSS-DRS] " + message); }
    }
}
