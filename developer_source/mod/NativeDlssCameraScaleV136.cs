using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SonsVRMod
{
    /// <summary>
    /// V137: retain the proven V136 per-camera hook, but select the return from Sons' native menu.
    /// Unity HDRP 2022.2/staging DLSSPass.cs: CameraState.ScaleFn computes recommended
    /// percentage, and InternalNVIDIASetupDRSScaling registers that *same delegate*
    /// into DynamicResScalerSlot.System. Never replace scaler slots or handler fields.
    ///
    /// This is experimental: the generated IL2CPP wrapper exposes ScaleFn, but a
    /// Harmony patch may fail to intercept delegates invoking the native target.
    /// No callback hit means NOT TESTED, not that 67% was rejected.
    /// </summary>
    internal static class NativeDlssCameraScaleV136
    {
        // V137: menu is authoritative; unknown options never silently default to Quality.
        private static volatile float _targetPercentage = 66.7f;
        private static volatile bool _menuDlssActive;
        private static Type _menuManagerType;
        private static MethodInfo _menuOptionGetter;
        private static PropertyInfo _menuQualityProperty;
        private static FieldInfo _menuQualityField;
        private static MethodInfo _menuQualityGetter;
        private static string _lastMenuState = "";
        private static int _menuErrors;
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static |
                                           BindingFlags.Public | BindingFlags.NonPublic;
        private static HarmonyLib.Harmony _harmony;
        private static bool _patchInstalled;
        private static volatile bool _armed;
        private static long _leftStatePointer;
        private static long _rightStatePointer;
        private static long _leftHits, _rightHits, _otherCalls, _sampleFailures;
        private static float _nextPoll, _nextHeartbeat;
        private static int _leftCamId, _rightCamId;
        private static int _lastLeftCamId, _lastRightCamId;
        private static int _errors;
        private static bool _reportedMissingStates;

        private static void Log(string message)
        {
            MelonLogger.Warning("[V137 DLSS-SCALEFN] " + message);
        }

        internal static void Initialize()
        {
            try
            {
                Type type = typeof(HDRenderPipeline).Assembly.GetType(
                    "UnityEngine.Rendering.HighDefinition.DLSSPass+CameraState", false);
                MethodInfo method = type != null ? type.GetMethod("ScaleFn", Flags) : null;
                if (method == null || method.ReturnType != typeof(float) || method.GetParameters().Length != 0)
                {
                    Log("PATCH_UNAVAILABLE CameraState.ScaleFn() : float not found; NO SCALER CHANGES");
                    return;
                }
                MethodInfo prefix = typeof(NativeDlssCameraScaleV136).GetMethod(nameof(ScaleFnPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                _harmony = new HarmonyLib.Harmony("zer0int.sonsvr.v137.native-menu-scalefn");
                _harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                _patchInstalled = true;
                Log("PATCH_INSTALLED " + type.FullName + ".ScaleFn; requires HOOK_HITS to prove execution");
            }
            catch (Exception ex)
            {
                Log("PATCH_FAILED " + ex.GetType().Name + ": " + ex.Message + " (unmodified renderer)");
            }
        }

        // This is the *single* rendering-policy intervention in V136.
        // No SetDynamicResScaler, SetSystemDynamicResScaler, or handler writes.
        private static bool ScaleFnPrefix(object __instance, ref float __result)
        {
            if (!_armed || !_menuDlssActive ||
                !NativeDlssReceiverRouterV128.Routed)
                return true;
            IntPtr pointer = (__instance as Il2CppObjectBase)?.Pointer ?? IntPtr.Zero;
            long p = pointer.ToInt64();
            if (p == 0) return true;
            if (p == Interlocked.Read(ref _leftStatePointer))
            {
                __result = _targetPercentage;
                Interlocked.Increment(ref _leftHits);
                return false;
            }
            if (p == Interlocked.Read(ref _rightStatePointer))
            {
                __result = _targetPercentage;
                Interlocked.Increment(ref _rightHits);
                return false;
            }
            Interlocked.Increment(ref _otherCalls);
            return true;
        }

        internal static void Tick()
        {
            if (!_patchInstalled) return;
            float now = Time.unscaledTime;
            if (now < _nextPoll) return;
            _nextPoll = now + 0.5f;
            try
            {
                if (!NativeDlssReceiverRouterV128.Routed)
                {
                    Disarm();
                    return;
                }
                StereoRender stereo = VRPlayer.Instance != null ? VRPlayer.Instance.StereoRender : null;
                if (stereo == null || stereo.LeftCam == null || stereo.RightCam == null)
                {
                    Disarm();
                    return;
                }
                // V137: read Endnight's actual native manager, not our legacy VR settings.
                // A failed/unknown quality read disables this override (no guessed scaling).
                if (!UpdateNativeMenuTarget())
                {
                    Disarm();
                    return;
                }
                RenderPipeline basePipeline = RenderPipelineManager.currentPipeline;
                HDRenderPipeline pipeline = basePipeline != null ? basePipeline.TryCast<HDRenderPipeline>() : null;
                if (pipeline == null || pipeline.m_DLSSPass == null)
                {
                    Disarm();
                    return;
                }
                object states = Read(pipeline.m_DLSSPass, "m_CameraStates");
                _leftCamId = stereo.LeftCam.GetInstanceID();
                _rightCamId = stereo.RightCam.GetInstanceID();
                object leftState = Lookup(states, _leftCamId);
                object rightState = Lookup(states, _rightCamId);
                long left = Pointer(leftState);
                long right = Pointer(rightState);
                if (left == 0 || right == 0 || left == right)
                {
                    Disarm();
                    if (!_reportedMissingStates)
                    {
                        _reportedMissingStates = true;
                        Log("WAIT_FOR_BOTH_EYE_STATES left=" + left + " right=" + right +
                            " leftCamId=" + _leftCamId + " rightCamId=" + _rightCamId);
                    }
                    return;
                }
                _reportedMissingStates = false;
                bool changed = !_armed || left != Interlocked.Read(ref _leftStatePointer) ||
                               right != Interlocked.Read(ref _rightStatePointer) ||
                               _leftCamId != _lastLeftCamId || _rightCamId != _lastRightCamId;
                // Keep pointer assignments atomic and arm only after both are known.
                _armed = false;
                Interlocked.Exchange(ref _leftStatePointer, left);
                Interlocked.Exchange(ref _rightStatePointer, right);
                _lastLeftCamId = _leftCamId;
                _lastRightCamId = _rightCamId;
                _armed = true;
                if (changed)
                {
                    Log("ARMED leftState=" + left + " rightState=" + right +
                        " cameraIDs=" + _leftCamId + "/" + _rightCamId +
                        " mode=" + (StereoRender.UserGameplayAERV112 ? "AER" : "2E") +
                        " menu=" + _lastMenuState + " target=" + _targetPercentage + "% ; NO global slot mutations");
                }
                if (now >= _nextHeartbeat)
                {
                    _nextHeartbeat = now + 2f;
                    var handler = DynamicResolutionHandler.instance;
                    Log("HOOK_HITS L=" + Interlocked.Read(ref _leftHits) +
                        " R=" + Interlocked.Read(ref _rightHits) +
                        " others=" + Interlocked.Read(ref _otherCalls) +
                        " mode=" + (StereoRender.UserGameplayAERV112 ? "AER" : "2E") +
                        " menu=" + _lastMenuState + " target=" + _targetPercentage +
                        " activeSlot=" + DynamicResolutionHandler.s_ActiveScalerSlot +
                        " fraction=" + (handler != null ? handler.m_CurrentFraction.ToString("F5") : "NA") +
                        " note=only live actual eye viewport in V129 TSV proves resolution");
                }
            }
            catch (Exception ex)
            {
                Disarm();
                if (_errors++ < 6) Log("PROBE_EXCEPTION " + ex.GetType().Name + " " + ex.Message);
            }
        }


        // V137: poll only on the existing 0.5s camera-state interval, NEVER inside ScaleFn.
        // Confirmed menu mapping from V127/V129 and V128 authored receivers:
        // Quality->66.7, Balanced->58, Performance->50, UltraPerformance->33.3.
        // No global DRS slot or fraction modifications.
        private static bool UpdateNativeMenuTarget()
        {
            try
            {
                if (_menuManagerType == null)
                {
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        _menuManagerType = asm.GetType("Il2CppSons.Dlss.DynamicResUpscaleManager", false);
                        if (_menuManagerType != null) break;
                    }
                    if (_menuManagerType == null) return false;
                    _menuOptionGetter = _menuManagerType.GetMethod("GetDynamicResolutionOption",
                        Flags, null, Type.EmptyTypes, null);
                    _menuQualityProperty = _menuManagerType.GetProperty("_activeDlssQualitySetting", Flags);
                    _menuQualityField = _menuManagerType.GetField("_activeDlssQualitySetting", Flags);
                    _menuQualityGetter = _menuManagerType.GetMethod("get__activeDlssQualitySetting",
                        Flags, null, Type.EmptyTypes, null);
                }
                object optionValue = _menuOptionGetter != null ? _menuOptionGetter.Invoke(null, null) : null;
                object qualityValue = _menuQualityProperty != null ? _menuQualityProperty.GetValue(null, null) :
                    _menuQualityField != null ? _menuQualityField.GetValue(null) :
                    _menuQualityGetter != null ? _menuQualityGetter.Invoke(null, null) : null;
                string option = optionValue != null ? optionValue.ToString() : "";
                string quality = qualityValue != null ? qualityValue.ToString() : "";
                string state = option + "/" + quality;
                bool valid = string.Equals(option, "Dlss", StringComparison.OrdinalIgnoreCase);
                string key = quality.Trim().Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
                float percentage = 0f;
                if (valid)
                {
                    switch (key)
                    {
                        case "quality":
                        case "maximumquality": percentage = 66.7f; break;
                        case "balanced": percentage = 58.0f; break;
                        case "performance":
                        case "maximumperformance": percentage = 50.0f; break;
                        case "ultraperformance": percentage = 33.3f; break;
                        default: valid = false; break;
                    }
                }
                if (state != _lastMenuState)
                {
                    _lastMenuState = state;
                    Log("MENU_SCALE option=" + option + " quality=" + quality +
                        (valid ? " requested=" + percentage.ToString("F1") + "%" : " native/no override"));
                }
                // Publish percentage before activating its use in native callback.
                if (valid) _targetPercentage = percentage;
                _menuDlssActive = valid;
                return valid;
            }
            catch (Exception e)
            {
                _menuDlssActive = false;
                if (_menuErrors++ < 5)
                    Log("MENU_READ_EXCEPTION " + e.GetType().Name + " " + e.Message);
                return false;
            }
        }

        private static long Pointer(object o) => (o as Il2CppObjectBase)?.Pointer.ToInt64() ?? 0;
        private static object Read(object o, string name)
        {
            if (o == null) return null;
            var p = o.GetType().GetProperty(name, Flags);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
            var m = o.GetType().GetMethod("get_" + name, Flags);
            return m != null ? m.Invoke(o, null) : null;
        }
        private static object Lookup(object o, int key)
        {
            if (o == null) return null;
            try
            {
                var method = o.GetType().GetMethod("get_Item", Flags);
                return method != null ? method.Invoke(o, new object[] { key }) : null;
            }
            catch { return null; }
        }
        private static void Disarm()
        {
            _armed = false;
            Interlocked.Exchange(ref _leftStatePointer, 0);
            Interlocked.Exchange(ref _rightStatePointer, 0);
        }
        internal static void Shutdown()
        {
            Disarm();
            Log("SHUTDOWN hookHits=" + Interlocked.Read(ref _leftHits) + "/" +
                Interlocked.Read(ref _rightHits));
            try { _harmony?.UnpatchSelf(); }
            catch (Exception ex) { Log("UNPATCH_FAILED " + ex.Message); }
        }
    }
}
