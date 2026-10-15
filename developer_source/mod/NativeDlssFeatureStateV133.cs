using System;
using System.Collections;
using System.Reflection;
using Il2CppInterop.Runtime; // V133B: cast IL2CPP base wrapper to the actual HDRP runtime type.
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SonsVRMod
{
    /// <summary>
    /// V133: PASSIVE, read-only inspection of HDRP's real DLSS per-camera/per-view state.
    /// Does not call ScaleFn, DLSSContext initialization/evaluation, camera setters, or
    /// DynamicResolutionHandler scaler/slot/minimum setters. V128 native route unchanged.
    /// </summary>
    internal static class NativeDlssFeatureStateV133
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                           BindingFlags.Static | BindingFlags.Instance;
        private static float _next;
        private static int _errors;
        private static string _lastSummary;
        private static int _sample;
        private static bool _reportedSurface;
        private static bool _reportedCameraKeys;

        internal static void Tick()
        {
            if (!NativeDlssReceiverRouterV128.Routed)
                return;
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 2.0f;
            try
            {
                StereoRender stereo = VRPlayer.Instance != null ? VRPlayer.Instance.StereoRender : null;
                if (stereo == null || stereo.LeftCam == null || stereo.RightCam == null) return;
                // V133B: currentPipeline is *wrapped* as the base RenderPipeline even when
                // the native instance is HDRP. CLR reflection on that base wrapper never
                // exposes m_DLSSPass, so V133 wrongly reported NO_DLSS_PASS.
                var basePipeline = RenderPipelineManager.currentPipeline;
                if (basePipeline == null) { LogOnce("NO_CURRENT_PIPELINE"); return; }
                HDRenderPipeline pipeline;
                try { pipeline = basePipeline.TryCast<HDRenderPipeline>(); }
                catch (Exception e)
                {
                    LogOnce("HDRP_CAST_EXCEPTION " + e.GetType().Name + " " + e.Message);
                    return;
                }
                if (pipeline == null)
                {
                    LogOnce("NOT_HDRP_PIPELINE baseWrapper=" + basePipeline.GetType().FullName);
                    return;
                }
                // These are genuine generated public IL2CPP property getters verified
                // in the user's Unity.RenderPipelines.HighDefinition.Runtime.dll.
                object dlss = pipeline.m_DLSSPass;
                object enabled = pipeline.m_DLSSPassEnabled;
                if (dlss == null)
                {
                    LogOnce("NO_DLSS_PASS_ON_TYPED_HDRP enabled=" + Val(enabled));
                    return;
                }
                object states = Read(dlss, "m_CameraStates");
                if (!_reportedSurface)
                {
                    _reportedSurface = true;
                    Log("SURFACE pipeline=" + pipeline.GetType().FullName +
                        " dlssPass=" + dlss.GetType().FullName +
                        " cameraStates=" + TypeName(states) +
                        " dictionaryCount=" + Val(Read(states, "Count")));
                }
                if (!_reportedCameraKeys && Int(Read(states, "Count"), 0) > 0)
                {
                    // V133B: enumerate at most 16 dictionary keys once. HDRP may use an
                    // internal camera identifier rather than Camera.GetInstanceID().
                    _reportedCameraKeys = true;
                    Log("CAMERA_STATE_KEYS " + ReadDictionaryKeys(states) +
                        " leftInstanceID=" + stereo.LeftCam.GetInstanceID() +
                        " rightInstanceID=" + stereo.RightCam.GetInstanceID());
                }
                string left = CameraSummary("L", stereo.LeftCam, states);
                string right = CameraSummary("R", stereo.RightCam, states);
                string summary = "enabled=" + Val(enabled) + " slots=" + Val(Read(states, "Count")) +
                                 " " + left + " " + right;
                _sample++;
                // Log on changes, and a periodic heartbeat so stable presets are represented.
                if (summary != _lastSummary || (_sample % 4) == 0)
                {
                    Log("SAMPLE #" + _sample + " " + summary);
                    _lastSummary = summary;
                }
            }
            catch (Exception ex)
            {
                if (_errors++ < 6) Log("READ_EXCEPTION " + ex.GetType().Name + " " + ex.Message);
                _next = now + 5f;
            }
        }

        private static string CameraSummary(string label, Camera cam, object dictionary)
        {
            if (cam == null) return label + "=NO_CAMERA";
            int id = cam.GetInstanceID();
            string info = label + "(cameraID=" + id;
            HDAdditionalCameraData hd = cam.GetComponent<HDAdditionalCameraData>();
            if (hd != null)
                info += ",Q=" + hd.deepLearningSuperSamplingQuality +
                        ",allowed=" + hd.allowDeepLearningSuperSampling +
                        ",customAttrs=" + hd.deepLearningSuperSamplingUseCustomAttributes +
                        ",optimal=" + hd.deepLearningSuperSamplingUseOptimalSettings;
            if (cam.targetTexture != null)
                info += ",target=" + cam.targetTexture.width + "x" + cam.targetTexture.height;

            object cameraState = Lookup(dictionary, id);
            if (cameraState == null) return info + ",state=NOT_FOUND)";
            object scale = Read(cameraState, "ScaleDelegate") ?? Read(cameraState, "m_ScaleDelegate");
            info += ",scaleDelegate=" + TypeName(scale);
            object views = Read(cameraState, "ViewStates") ?? Read(cameraState, "m_Views");
            int count = Int(Read(views, "Count"), 0);
            info += ",views=" + count;
            for (int i = 0; i < Math.Min(count, 2); i++)
            {
                object view = Lookup(views, i);
                if (view == null) { info += ",v" + i + "=NOT_FOUND"; continue; }
                object request = Read(view, "m_OptimalSettingsRequest") ??
                                 Read(view, "OptimalSettingsRequestData");
                object data = Read(view, "m_Data");
                object context = Read(view, "DLSSContext") ?? Read(view, "m_DlssContext");
                object init = Read(context, "initData");
                object requestSettings = Read(request, "optimalSettings");
                info += ",v" + i + "{auto=" + Val(Read(view, "useAutomaticSettings") ?? Read(view, "m_UseAutomaticSettings")) +
                        ",usingOpt=" + Val(Read(view, "m_UsingOptimalSettings")) +
                        ",reqQ=" + Val(Read(request, "quality")) +
                        ",reqViewport=" + Val(Read(request, "viewport")) +
                        ",reqOpt=" + Dimensions(requestSettings) +
                        ",viewQ=" + Val(Read(data, "perfQuality")) +
                        ",viewIn=" + Dimensions(Read(data, "inputRes")) +
                        ",viewOut=" + Dimensions(Read(data, "outputRes")) +
                        ",ctx=" + (context != null ? "yes" : "no") +
                        ",ctxQ=" + Val(Read(init, "quality")) +
                        ",ctxOut=" + Val(Read(init, "outputRTWidth")) + "x" + Val(Read(init, "outputRTHeight")) + "}";
            }
            return info + ")";
        }

        private static string ReadDictionaryKeys(object dictionary)
        {
            if (dictionary == null) return "NULL";
            try
            {
                var getEnumerator = dictionary.GetType().GetMethod("GetEnumerator", Flags);
                if (getEnumerator == null) return "NO_GET_ENUMERATOR";
                object iterator = getEnumerator.Invoke(dictionary, null);
                if (iterator == null) return "NULL_ENUMERATOR";
                var moveNext = iterator.GetType().GetMethod("MoveNext", Flags);
                if (moveNext == null) return "NO_MOVE_NEXT";
                string keys = "[";
                for (int i = 0; i < 16; i++)
                {
                    object moved = moveNext.Invoke(iterator, null);
                    if (!(moved is bool) || !(bool)moved) break;
                    object pair = Read(iterator, "Current");
                    if (i != 0) keys += ",";
                    keys += Val(Read(pair, "Key"));
                }
                return keys + "]";
            }
            catch (Exception ex) { return "ENUMERATION_ERROR:" + ex.GetType().Name; }
        }

        private static object Lookup(object collection, int index)
        {
            if (collection == null) return null;
            try
            {
                MethodInfo method = collection.GetType().GetMethod("get_Item", Flags);
                if (method != null) return method.Invoke(collection, new object[] { index });
            }
            catch { }
            try
            {
                PropertyInfo p = collection.GetType().GetProperty("Item", Flags);
                if (p != null) return p.GetValue(collection, new object[] { index });
            }
            catch { }
            return null;
        }

        private static object Read(object instance, string member)
        {
            if (instance == null) return null;
            try
            {
                Type t = instance.GetType();
                PropertyInfo property = t.GetProperty(member, Flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(instance, null);
                MethodInfo method = t.GetMethod("get_" + member, Flags);
                if (method != null && method.GetParameters().Length == 0)
                    return method.Invoke(instance, null);
                FieldInfo field = t.GetField(member, Flags);
                if (field != null) return field.GetValue(instance);
            }
            catch { }
            return null;
        }

        private static string Dimensions(object obj)
        {
            if (obj == null) return "NA";
            object w = Read(obj, "width") ?? Read(obj, "outRenderWidth") ?? Read(obj, "m_Width");
            object h = Read(obj, "height") ?? Read(obj, "outRenderHeight") ?? Read(obj, "m_Height");
            return w != null && h != null ? Val(w) + "x" + Val(h) : Val(obj);
        }
        private static int Int(object obj, int fallback)
        {
            try { return obj == null ? fallback : Convert.ToInt32(obj); } catch { return fallback; }
        }
        private static string TypeName(object obj) { return obj == null ? "NULL" : obj.GetType().Name; }
        private static string Val(object obj)
        {
            if (obj == null) return "NA";
            try { return obj.ToString().Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '); }
            catch { return "BAD_VALUE"; }
        }
        private static string _lastOnce;
        private static void LogOnce(string s)
        {
            if (s == _lastOnce) return;
            _lastOnce = s;
            Log(s);
        }
        private static void Log(string s) { MelonLogger.Warning("[V133 DLSS-FEATURE] " + s); }
    }
}
