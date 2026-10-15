using MelonLoader;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace SonsVRMod
{
    /// <summary>
    /// V91: reuse Sons Of The Forest's OWN HDRP/DLSS runtime instead of integrating NGX twice.
    ///
    /// The game already ships Il2CppSons.Dlss.dll + UnityEngine.NVIDIAModule.dll and exposes a
    /// DynamicResUpscaleManager with public static controls for the active dynamic-resolution
    /// upscaler and DLSS quality. We deliberately find that manager by callable surface rather
    /// than hard-coding its generated IL2CPP type name; this keeps the mod resilient to namespace
    /// wrapper changes while still calling the game's real implementation.
    ///
    /// This class owns only GLOBAL selection/verification. Per-eye eligibility remains in
    /// StereoRender.TryConfigureHdrpCameraSettings(), where the persistent LEFT/RIGHT cameras are
    /// explicitly allowed to use dynamic resolution + DLSS.
    /// </summary>
    internal static class DlssRuntimeV91
    {
        private static Type _managerType;
        private static bool _configured;
        private static bool _successLogged;
        private static bool _unsupportedLogged;
        private static float _nextAttemptTime;
        private static float _nextStatusTime;
        private static int _attempts;

        internal static bool Configured => _configured;

        internal static void RequestRuntimeReconfigureV105()
        {
            _configured = false;
            _successLogged = false;
            _unsupportedLogged = false;
            _attempts = 0;
            _nextAttemptTime = 0f;
            _nextStatusTime = 0f;
        }

        internal static void Tick(StereoRender stereo)
        {
            // V138: one settings authority for both 2E and AER: the actual Sons menu.
            // V91's legacy one-shot ApplyDlssSetting(Quality) would overwrite the menu
            // when entering AER. The native two-eye receiver route already handles both.
            if (NativeDlssReceiverRouterV128.Enabled)
            {
                NativeDlssReceiverRouterV128.Tick(stereo);
                return;
            }
            if (!SonsVRMod.EnableV91RealDlss)
                return;

            float now = Time.unscaledTime;

            if (!_configured && now >= _nextAttemptTime)
            {
                _nextAttemptTime = now + (_attempts < 15 ? 1.0f : 5.0f);
                _attempts++;
                TryConfigureGlobalDlss();
            }

            if (now >= _nextStatusTime)
            {
                _nextStatusTime = now + 5.0f;
                LogEyeStatus(stereo);
            }
        }

        private static bool TryConfigureGlobalDlss()
        {
            try
            {
                Type t = FindManagerType();
                if (t == null)
                {
                    if (_attempts <= 3 || (_attempts % 10) == 0)
                        MelonLogger.Warning("[StereoRender][AER V91][DLSS] Sons DLSS manager type not loaded yet; retrying fail-soft.");
                    return false;
                }

                bool supported = InvokeStaticBool(t, "IsDlssSupported", fallback: true);
                if (!supported)
                {
                    if (!_unsupportedLogged)
                    {
                        _unsupportedLogged = true;
                        MelonLogger.Warning("[StereoRender][AER V91][DLSS] Sons.Dlss reports DLSS unsupported. Keeping normal eye rendering/AER; no NGX calls are attempted by this mod.");
                    }
                    return false;
                }

                string dlssOption =
                    GetStaticString(t, "DynamicResUpscaleDlssOption") ??
                    GetStaticString(t, "DynamicResUpscaleSettingDefault") ??
                    "DLSS";

                string quality = ResolveQualityString(t, SonsVRMod.V91DlssQuality);

                // Use Sons' own public runtime controls. Some game builds route the high-level
                // option and the actual HDRP filter through separate methods, so invoke BOTH when
                // available. Each is isolated so one wrapper mismatch cannot prevent the other.
                bool optionSet =
                    TryInvokeStaticString(t, "SetDynamicResolutionOption", dlssOption) |
                    TryInvokeStaticString(t, "SetDynamicResUpscaleFilter", dlssOption);
                bool qualitySet = TryInvokeStaticString(t, "ApplyDlssSetting", quality);

                string activeOption = TryInvokeStaticStringResult(t, "GetDynamicResolutionOption");

                _configured = optionSet && qualitySet;
                if (!_configured)
                {
                    MelonLogger.Warning(
                        $"[StereoRender][AER V91][DLSS] Sons manager discovered but one or more runtime setters failed " +
                        $"(optionSet={optionSet} qualitySet={qualitySet}); retrying fail-soft.");
                    return false;
                }

                if (!_successLogged)
                {
                    _successLogged = true;
                    MelonLogger.Warning(
                        "[StereoRender][AER V91][DLSS] GLOBAL REAL-DLSS REQUEST APPLIED via Sons' own DynamicResUpscaleManager. " +
                        $"supported={supported} requestedUpscaler='{dlssOption}' activeOption='{activeOption ?? "<unknown>"}' " +
                        $"quality='{quality}'. Per-eye DRS/DLSS eligibility is configured separately on VRLeftEye/VRRightEye.");
                }
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning(
                    "[StereoRender][AER V91][DLSS] global configuration failed fail-soft: " +
                    e.GetType().Name + ": " + e.Message);
                return false;
            }
        }


        internal static void ConfigureEyeCamera(HDAdditionalCameraData data, Camera cam)
        {
            if (!SonsVRMod.EnableV91RealDlss || data == null || cam == null)
                return;

            // V138: never stamp mod-owned Quality/Optimal settings onto EITHER mode.
            // TryConfigureHdrpCameraSettings still enables DLSS/DRS eligibility; Endnight's
            // live receiver owns the camera quality and option. This avoids dual writers.
            if (NativeDlssReceiverRouterV128.Enabled)
                return;
            try
            {
                data.allowDynamicResolution = true;
                data.allowDeepLearningSuperSampling = true;
                data.hasPersistentHistory = true;
                cam.allowDynamicResolution = true;

                // Make this experiment independent of the user's last pancake-menu choice.
                // HDRP 14 stores the NVIDIA quality as an opaque uint; resolve the official
                // UnityEngine.NVIDIA.DLSSQuality enum at runtime so we don't hard-code its value.
                uint quality = ResolveDlssQualityCode(SonsVRMod.V91DlssQuality);
                data.deepLearningSuperSamplingUseCustomQualitySettings = true;
                data.deepLearningSuperSamplingQuality = quality;
                data.deepLearningSuperSamplingUseCustomAttributes = true;
                data.deepLearningSuperSamplingUseOptimalSettings = true;
                data.deepLearningSuperSamplingSharpening = 0.0f;

                MelonLogger.Msg(
                    $"[StereoRender][AER V91][DLSS] eye={cam.name} customQuality='{SonsVRMod.V91DlssQuality}' code={quality} " +
                    $"optimal=True sharpening=0 persistentHistory={data.hasPersistentHistory} " +
                    $"postWrite(camDyn={cam.allowDynamicResolution}, hdDyn={data.allowDynamicResolution}, dlssAllow={data.allowDeepLearningSuperSampling}).");
            }
            catch (Exception e)
            {
                MelonLogger.Warning(
                    $"[StereoRender][AER V91][DLSS] per-eye custom quality setup failed on {cam.name}: " +
                    e.GetType().Name + ": " + e.Message + ". Global Quality + camera allow flags remain active.");
            }
        }

        private static uint ResolveDlssQualityCode(string requested)
        {
            string r = (requested ?? "Quality").Trim();
            string[] names =
                r.Equals("Balanced", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "Balanced" }
                : r.Equals("Performance", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "MaximumPerformance", "MaxPerf" }
                : r.Equals("UltraPerformance", StringComparison.OrdinalIgnoreCase) ||
                  r.Equals("Ultra Performance", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "UltraPerformance" }
                : new[] { "MaximumQuality", "MaxQuality" };

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type qt = asm.GetType("UnityEngine.NVIDIA.DLSSQuality");
                    if (qt == null || !qt.IsEnum) continue;
                    foreach (string enumName in names)
                    {
                        try
                        {
                            object parsed = Enum.Parse(qt, enumName, ignoreCase: true);
                            return Convert.ToUInt32(parsed);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // Fail-soft fallback only. NGX/Unity historically maps MaximumQuality to 2.
            // Normal operation should resolve the shipped NVIDIAModule enum above.
            MelonLogger.Warning(
                "[StereoRender][AER V91][DLSS] UnityEngine.NVIDIA.DLSSQuality enum not found/parsed; " +
                "falling back to opaque quality code 2 for MaximumQuality.");
            return 2u;
        }

        private static Type FindManagerType()
        {
            if (_managerType != null)
                return _managerType;

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName = "";
                try { asmName = asm.GetName().Name ?? ""; } catch { }
                if (asmName.IndexOf("Sons.Dlss", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (Type type in types)
                {
                    if (type == null) continue;
                    MethodInfo apply = type.GetMethod(
                        "ApplyDlssSetting",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        binder: null,
                        types: new[] { typeof(string) },
                        modifiers: null);
                    MethodInfo support = type.GetMethod(
                        "IsDlssSupported",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        binder: null,
                        types: Type.EmptyTypes,
                        modifiers: null);

                    if (apply != null && support != null)
                    {
                        _managerType = type;
                        MelonLogger.Msg(
                            "[StereoRender][AER V91][DLSS] discovered Sons DLSS manager type '" +
                            type.FullName + "' in assembly '" + asmName + "'.");
                        return _managerType;
                    }
                }
            }

            return null;
        }

        private static string ResolveQualityString(Type t, string requested)
        {
            string r = (requested ?? "Quality").Trim();
            string prop = r.Equals("Balanced", StringComparison.OrdinalIgnoreCase) ? "DlssBalancedString" :
                          r.Equals("Performance", StringComparison.OrdinalIgnoreCase) ? "DlssPerformanceString" :
                          r.Equals("UltraPerformance", StringComparison.OrdinalIgnoreCase) ||
                          r.Equals("Ultra Performance", StringComparison.OrdinalIgnoreCase) ? "DlssUltraPerformanceString" :
                          "DlssQualityString";

            return GetStaticString(t, prop) ?? r;
        }

        private static string GetStaticString(Type t, string name)
        {
            try
            {
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (p != null && p.PropertyType == typeof(string))
                    return p.GetValue(null) as string;

                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null && f.FieldType == typeof(string))
                    return f.GetValue(null) as string;
            }
            catch { }
            return null;
        }

        private static bool InvokeStaticBool(Type t, string method, bool fallback)
        {
            try
            {
                MethodInfo m = t.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (m == null) return fallback;
                object v = m.Invoke(null, null);
                return v is bool b ? b : fallback;
            }
            catch { return fallback; }
        }

        private static bool TryInvokeStaticString(Type t, string method, string arg)
        {
            try
            {
                MethodInfo m = t.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (m == null)
                {
                    MelonLogger.Warning($"[StereoRender][AER V91][DLSS] Sons DLSS manager method {method}(string) not found; continuing.");
                    return false;
                }
                m.Invoke(null, new object[] { arg });
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[StereoRender][AER V91][DLSS] {method}('{arg}') failed: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        private static string TryInvokeStaticStringResult(Type t, string method)
        {
            try
            {
                MethodInfo m = t.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                return m?.Invoke(null, null) as string;
            }
            catch { return null; }
        }

        private static void LogEyeStatus(StereoRender stereo)
        {
            if (stereo == null || stereo.LeftCam == null || stereo.RightCam == null)
                return;

            LogOneEye(stereo.LeftCam, "L");
            LogOneEye(stereo.RightCam, "R");
        }

        private static void LogOneEye(Camera cam, string eye)
        {
            try
            {
                HDAdditionalCameraData data = cam.GetComponent<HDAdditionalCameraData>();
                string hdrp = data == null
                    ? "HDData=null"
                    : $"AA={data.antialiasing} DLSSallow={data.allowDeepLearningSuperSampling} dynAllow={data.allowDynamicResolution} persistent={data.hasPersistentHistory}";

                RenderTexture rt = cam.targetTexture;
                string target = rt == null
                    ? "target=null"
                    : $"target={rt.width}x{rt.height} dynScale={rt.useDynamicScale} aa={rt.antiAliasing}";

                int observedW, observedH, observedFrame;
                int eyeIndex = eye == "L" ? 0 : 1;
                bool haveObserved = EyeDepthCaptureCustomPassV81.TryGetObservedViewportV116(
                    eyeIndex, out observedW, out observedH, out observedFrame);
                string observed = haveObserved
                    ? $"hdActual={observedW}x{observedH} observedFrame={observedFrame}"
                    : "hdActual=n/a";
                string ratio = (haveObserved && rt != null && rt.width > 0 && rt.height > 0)
                    ? $"activeToTarget={(float)observedW / rt.width:F6}x{(float)observedH / rt.height:F6}"
                    : "activeToTarget=n/a";

                MelonLogger.Msg(
                    $"[StereoRender][AER V91][DLSS][EyeStatus] eye={eye} camera.allowDynamicResolution={cam.allowDynamicResolution} " +
                    $"{hdrp} {target} {observed} {ratio} globalConfigured={_configured}.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[StereoRender][AER V91][DLSS][EyeStatus] eye={eye} failed: {e.Message}");
            }
        }
    }
}
