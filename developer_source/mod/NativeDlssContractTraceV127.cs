using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace SonsVRMod
{
    /// <summary>
    /// V127 read-only DLSS/DRS contract audit, based on the V116 known-good source.
    /// Never modifies a camera, the scaler, quality, registration, or native receiver.
    /// Never performs a scene-wide FindObjectsOfType or creates a GameObject.
    /// Samples light-weight state at 20Hz; reads manager receiver list only every 4 seconds.
    /// </summary>
    internal static class NativeDlssContractTraceV127
    {
        private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                                    BindingFlags.Static | BindingFlags.Instance;
        private static Type _managerType;
        private static MethodInfo _getOption;
        private static Type _scalerType;
        private static MethodInfo _getScale;
        private static float _nextProbe;
        private static float _nextCensus;
        private static float _nextHeartbeat;
        private static string _lastManagerState;
        private static string _lastEyeState;
        private static string _lastScaleState;
        private static string _lastCensus;
        private static int _probeFailureCount;
        private static bool _initialized;
        private static int _transitionNumber;

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < _nextProbe) return;
            _nextProbe = now + 0.05f;

            try
            {
                if (!_initialized)
                {
                    // Type resolution is constant, not a scene-object search. If these wrappers
                    // are absent, retry only every 5 seconds.
                    _managerType = FindType("Il2CppSons.Dlss.DynamicResUpscaleManager");
                    _scalerType = FindType("Il2CppSons.Quality.DynamicResolutionScaler");
                    if (_managerType == null || _scalerType == null)
                    {
                        _nextProbe = now + 5.0f;
                        return;
                    }
                    _getOption = _managerType.GetMethod("GetDynamicResolutionOption", Flags,
                                                        null, Type.EmptyTypes, null);
                    _getScale = _scalerType.GetMethod("GetCurrentScale", Flags,
                                                     null, Type.EmptyTypes, null);
                    _initialized = true;
                    _nextCensus = now;
                    MelonLogger.Warning("[V127 DLSS-CONTRACT] PASSIVE trace ready; " +
                        "no scene-wide searches, no quality writes, no receiver modifications.");
                }

                string option = Stringify(_getOption?.Invoke(null, null));
                string quality = Stringify(ReadMember(_managerType, null, "_activeDlssQualitySetting"));
                string managerState = "option=" + option + " managerQuality=" + quality;

                float scale = float.NaN;
                try
                {
                    object value = _getScale?.Invoke(null, null);
                    if (value != null) scale = Convert.ToSingle(value);
                }
                catch { }
                string scaleState = "scaler.CurrentScale=" + (float.IsNaN(scale) ? "NA" : scale.ToString("F5")) +
                                    " scaler.Min=" + Stringify(ReadMember(_scalerType, null, "_minScreenPercentage")) +
                                    " scaler.Max=" + Stringify(ReadMember(_scalerType, null, "_maxScreenPercentage"));

                StereoRender stereo = null;
                try { if (VRPlayer.Instance != null) stereo = VRPlayer.Instance.StereoRender; }
                catch { }
                string eyeState = "mode=" + (StereoRender.UserGameplayAERV112 ? "AER" : "2E") +
                                  " L{" + CameraState(stereo != null ? stereo.LeftCam : null) + "}" +
                                  " R{" + CameraState(stereo != null ? stereo.RightCam : null) + "}";

                // Record every changed quality/camera configuration; scale alone is rate limited
                // because an FPS-responsive scaler can legitimately change every frame.
                bool managerChanged = _lastManagerState != managerState;
                bool eyeChanged = _lastEyeState != eyeState;
                bool scaleChanged = _lastScaleState != scaleState;
                bool heartbeat = now >= _nextHeartbeat;
                if (managerChanged || eyeChanged || heartbeat ||
                    (scaleChanged && now >= _nextHeartbeat - 0.80f))
                {
                    if (managerChanged || eyeChanged) ++_transitionNumber;
                    MelonLogger.Warning("[V127 DLSS-CONTRACT] t=" + now.ToString("F3") +
                        " transition=" + _transitionNumber + " changed=" +
                        (managerChanged ? "MANAGER," : "") + (eyeChanged ? "EYES," : "") +
                        (scaleChanged ? "SCALE," : "") + " " + managerState +
                        " " + scaleState + " " + eyeState);
                    _nextHeartbeat = now + 1.0f;
                }
                _lastManagerState = managerState;
                _lastEyeState = eyeState;
                _lastScaleState = scaleState;

                // The manager list is small, but avoid per-frame enumeration/reflection.
                if (now >= _nextCensus)
                {
                    _nextCensus = now + 4.0f;
                    object list = ReadMember(_managerType, null, "_dynamicResolutionSettingReceivers");
                    string census = ReceiverCensus(list);
                    if (_lastCensus != census)
                    {
                        MelonLogger.Warning("[V127 DLSS-RECEIVERS] t=" + now.ToString("F3") +
                                            " " + census);
                        _lastCensus = census;
                    }
                }
            }
            catch (Exception e)
            {
                // Fail-soft; never permit telemetry to interrupt the renderer.
                if (_probeFailureCount++ < 5)
                    MelonLogger.Warning("[V127 DLSS-CONTRACT] passive read failed " +
                        e.GetType().Name + ": " + e.Message);
                _nextProbe = now + 2.0f;
            }
        }

        private static string CameraState(Camera cam)
        {
            if (cam == null) return "null";
            try
            {
                HDAdditionalCameraData hd = cam.GetComponent<HDAdditionalCameraData>();
                if (hd == null) return cam.name + ":no-HDAdditionalCameraData";
                RenderTexture rt = cam.targetTexture;
                return cam.name + "#" + cam.GetInstanceID() +
                    ",CamDyn=" + cam.allowDynamicResolution +
                    ",HdDyn=" + hd.allowDynamicResolution +
                    ",DlssAllowed=" + hd.allowDeepLearningSuperSampling +
                    ",CustomQ=" + hd.deepLearningSuperSamplingUseCustomQualitySettings +
                    ",QCode=" + hd.deepLearningSuperSamplingQuality +
                    ",CustomAttrs=" + hd.deepLearningSuperSamplingUseCustomAttributes +
                    ",Optimal=" + hd.deepLearningSuperSamplingUseOptimalSettings +
                    ",History=" + hd.hasPersistentHistory +
                    ",RT=" + (rt == null ? "null" : rt.width + "x" + rt.height +
                                            ",dyn=" + rt.useDynamicScale);
            }
            catch (Exception e) { return "READ_ERROR:" + e.GetType().Name; }
        }

        private static string ReceiverCensus(object list)
        {
            if (list == null) return "managerList=null";
            List<object> entries = new List<object>();
            // Same fallback order as the successfully deployed V118/V126 census:
            // IEnumerable, then get_Item(int). Neither path searches the scene.
            bool done = false;
            try
            {
                if (list is IEnumerable enumerable)
                {
                    foreach (object item in enumerable)
                    {
                        if (entries.Count >= 32) break;
                        if (item != null) entries.Add(item);
                    }
                    done = true;
                }
            }
            catch { entries.Clear(); }
            if (!done)
            {
                try
                {
                    Type type = list.GetType();
                    object countValue = ReadMember(type, list, "Count");
                    int count = Math.Min(32, Convert.ToInt32(countValue));
                    MethodInfo getItem = type.GetMethod("get_Item", Flags, null,
                                                       new[] { typeof(int) }, null);
                    if (getItem == null) return "managerList: no get_Item(int)";
                    for (int i = 0; i < count; ++i)
                    {
                        object item = getItem.Invoke(list, new object[] { i });
                        if (item != null) entries.Add(item);
                    }
                }
                catch (Exception e) { return "enumeration-error:" + e.GetType().Name; }
            }

            List<string> items = new List<string>();
            foreach (object item in entries)
            {
                Type type = item.GetType();
                Camera target = ReadMember(type, item, "_targetCamera") as Camera;
                string actualQuality = Stringify(ReadMember(type, item, "_activeDlssSetting"));
                string actualOption = Stringify(ReadMember(type, item, "_dynamicResolutionOption"));
                object hd = ReadMember(type, item, "_hdAdditionalCameraData");
                object enabled = ReadMember(type, item, "enabled");
                items.Add((target == null ? "no-camera" : target.name + "#" + target.GetInstanceID()) +
                    "{activeQ=" + actualQuality + ",option=" + actualOption +
                    ",HDcache=" + (hd == null ? "null" : "non-null") +
                    ",enabled=" + Stringify(enabled) + "}");
            }
            return "count=" + entries.Count + " " + string.Join(" ; ", items.ToArray());
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try { type = assembly.GetType(fullName, false); }
                catch { }
                if (type != null) return type;
            }
            return null;
        }

        private static object ReadMember(Type type, object instance, string name)
        {
            if (type == null) return null;
            try
            {
                PropertyInfo property = type.GetProperty(name, Flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(instance, null);
                FieldInfo field = type.GetField(name, Flags);
                if (field != null) return field.GetValue(instance);
            }
            catch { }
            return null;
        }

        private static string Stringify(object value)
        {
            try { return value == null ? "null" : value.ToString(); }
            catch { return "invalid"; }
        }
    }
}
