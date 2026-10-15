using System;
using System.IO;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SonsVRMod
{
    /// <summary>
    /// V129: passive, render-boundary-proven input-resolution measurements.
    /// V128 retains exclusive ownership of the native receiver routing.
    /// This class does NOT write camera data, dynamic-resolution policy, receiver
    /// registration, DLSS settings, scaler slots, or render textures.
    /// Event-based 2E capture with a 0.25s post-setting-change window and sparse
    /// 1.5s settled samples; AER gets NO sampling or changes.
    /// </summary>
    internal static class NativeDlssLiveResolutionTraceV129
    {
        private const float TransitionWindowSeconds = 4.0f;
        private const float FastIntervalSeconds = 0.25f;
        private const float IdleIntervalSeconds = 1.5f;
        private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                                     BindingFlags.Instance | BindingFlags.Static;
        private static StreamWriter _writer;
        private static Type _managerType;
        private static Type _handlerType;
        private static MethodInfo _optionMethod;
        private static string _lastManager = null;
        private static string _currentOption = "unknown";
        private static string _currentQuality = "unknown";
        private static float _nextManagerPoll;
        private static float _nextLeft;
        private static float _nextRight;
        private static float _transitionUntil;
        private static float _nextOptimalQuery;
        private static int _transition;
        private static int _errors;
        private static bool _initialized;
        private static bool _everRouted;
        private static bool _warnedMissingCallbacks;
        private static float _lastRenderCallback;

        internal static void Tick()
        {
            if (!NativeDlssReceiverRouterV128.Routed)
            {
                if (_everRouted && Time.unscaledTime >= _nextManagerPoll)
                {
                    _nextManagerPoll = Time.unscaledTime + 4.0f;
                    WriteMarker("ROUTE_INACTIVE");
                    _everRouted = false;
                    _lastManager = null;
                }
                return;
            }
            _everRouted = true;
            float now = Time.unscaledTime;
            if (now < _nextManagerPoll) return;
            _nextManagerPoll = now + 0.20f;
            try
            {
                if (!_initialized && !Initialize()) return;
                _currentOption = Display(_optionMethod?.Invoke(null, null));
                _currentQuality = Display(Read(_managerType, null, "_activeDlssQualitySetting"));
                string manager = _currentOption + "/" + _currentQuality;
                if (_lastManager != manager)
                {
                    _transition++;
                    _lastManager = manager;
                    _transitionUntil = now + TransitionWindowSeconds;
                    _nextLeft = 0f;
                    _nextRight = 0f;
                    _nextOptimalQuery = now + 1.5f;
                    if (_lastRenderCallback <= 0f) _lastRenderCallback = now;
                    WriteMarker("MENU_TRANSITION");
                    MelonLogger.Warning("[V129 DLSS-LIVE] menu transition=" + _transition +
                        " option=" + _currentOption + " quality=" + _currentQuality +
                        " (render-boundary samples in V129 TSV)");
                }
                if (_nextOptimalQuery > 0 && now >= _nextOptimalQuery)
                {
                    _nextOptimalQuery = 0;
                    // Existing V118 read-only API; outputs four *advisory* NVIDIA
                    // recommendations into the transition trace after GAMEPLAY initialized.
                    StereoRender stereo = VRPlayer.Instance != null ? VRPlayer.Instance.StereoRender : null;
                    if (stereo != null && stereo.LeftCam != null && stereo.LeftCam.targetTexture != null)
                        TransitionDlssAuditV117.DumpLiveOptimalSettingsV129(
                            stereo.LeftCam.targetTexture.width, stereo.LeftCam.targetTexture.height,
                            "menu=" + _currentOption + "/" + _currentQuality);
                }
                if (!_warnedMissingCallbacks && now > 30f && _lastRenderCallback < now - 10f)
                {
                    _warnedMissingCallbacks = true;
                    MelonLogger.Warning("[V129 DLSS-LIVE] No recent VR-eye render callback; " +
                        "inspect TSV for missing EYE_BEGIN / EYE_END rows. Camera-state polling " +
                        "alone cannot prove the actual frame's resolution.");
                }
            }
            catch (Exception e)
            {
                if (_errors++ < 4) MelonLogger.Warning("[V129 DLSS-LIVE] Tick read failed: " +
                    e.GetType().Name + ": " + e.Message);
                _nextManagerPoll = now + 3f;
            }
        }

        private static bool Initialize()
        {
            try
            {
                _managerType = FindType("Il2CppSons.Dlss.DynamicResUpscaleManager");
                _handlerType = FindType("UnityEngine.Rendering.DynamicResolutionHandler");
                if (_managerType == null || _handlerType == null)
                {
                    _nextManagerPoll = Time.unscaledTime + 5f;
                    return false;
                }
                _optionMethod = _managerType.GetMethod("GetDynamicResolutionOption", Flags,
                    null, Type.EmptyTypes, null);
                string root = Directory.GetParent(Application.dataPath)?.FullName ?? Environment.CurrentDirectory;
                string mods = Path.Combine(root, "Mods");
                Directory.CreateDirectory(mods);
                string path = Path.Combine(mods, "SonsVR_V129_DLSS_Live_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".tsv");
                _writer = new StreamWriter(new FileStream(path, FileMode.Create,
                    FileAccess.Write, FileShare.ReadWrite));
                _writer.WriteLine("uptime\tframe\ttransition\tevent\teye\tmode\tmenuOption\tmanagerQuality" +
                    "\tcam\tcamDyn\thdDyn\tdlssAllowed\tcustomQ\tqCode\tcustomAttrs\toptimal" +
                    "\ttarget\tuseDynamicScale\thdActual\tactiveFractionX\tactiveFractionY" +
                    "\tdrsResolvedScale\tdrsCurrentScale\tdrsFinalViewport\tdrsFilter\tdrsCameraRequest" +
                    "\tdrsDynamicEnabled\tdrsSoftware\tdrsHardware\tdrsLastScaledSize\tbufferScale" +
                    "\tassetPolicy");
                _initialized = true;
                MelonLogger.Warning("[V129 DLSS-LIVE] passive render-resolution probe initialized: " + path);
                return true;
            }
            catch (Exception e)
            {
                if (_errors++ < 3) MelonLogger.Warning("[V129 DLSS-LIVE] initialization failed: " + e.Message);
                _nextManagerPoll = Time.unscaledTime + 5f;
                return false;
            }
        }

        internal static void OnEyeRenderBoundary(string phase, Camera cam)
        {
            if (!_initialized || !_everRouted ||
                !NativeDlssReceiverRouterV128.Routed || cam == null) return;
            try
            {
                StereoRender stereo = VRPlayer.Instance != null ? VRPlayer.Instance.StereoRender : null;
                if (stereo == null) return;
                bool isLeft = stereo.LeftCam != null && cam == stereo.LeftCam;
                bool isRight = stereo.RightCam != null && cam == stereo.RightCam;
                if (!isLeft && !isRight) return;
                _lastRenderCallback = Time.unscaledTime;
                float now = _lastRenderCallback;
                float next = isLeft ? _nextLeft : _nextRight;
                // Emit both BEGIN and END for the same sampled frame, so handler
                // state is not wrongly attributed to another camera's render.
                if (phase == "begin")
                {
                    if (now < next) return;
                    if (isLeft) _nextLeft = now + (now < _transitionUntil ? FastIntervalSeconds : IdleIntervalSeconds);
                    else _nextRight = now + (now < _transitionUntil ? FastIntervalSeconds : IdleIntervalSeconds);
                }
                else if (phase != "end") return;
                // END record belongs to the BEGIN-selected frame even if the
                // callback arrives a few milliseconds after the timer advances.
                if (phase == "end" && (isLeft ? _lastLeftBeginFrame : _lastRightBeginFrame) != Time.frameCount)
                    return;
                if (phase == "begin")
                {
                    if (isLeft) _lastLeftBeginFrame = Time.frameCount;
                    else _lastRightBeginFrame = Time.frameCount;
                }
                Capture(phase == "begin" ? "EYE_BEGIN" : "EYE_END", isLeft ? "L" : "R", cam);
            }
            catch (Exception e)
            {
                if (_errors++ < 5) MelonLogger.Warning("[V129 DLSS-LIVE] render-boundary probe: " +
                    e.GetType().Name + ": " + e.Message);
            }
        }

        private static int _lastLeftBeginFrame = -1;
        private static int _lastRightBeginFrame = -1;

        private static void Capture(string ev, string eye, Camera cam)
        {
            HDAdditionalCameraData data = cam.GetComponent<HDAdditionalCameraData>();
            if (data == null) return;
            RenderTexture rt = cam.targetTexture;
            HDCamera hd = HDCamera.GetOrCreate(cam, 0);
            // HDCamera.actualWidth/Height are current-frame HDRP render dimensions,
            // not the allocated output texture size. Treat as HDRP-reported truth.
            string actual = hd == null ? "NA" : hd.actualWidth + "x" + hd.actualHeight;
            string target = rt == null ? "NA" : rt.width + "x" + rt.height;
            string fx = hd != null && rt != null && rt.width > 0
                ? ((double)hd.actualWidth / rt.width).ToString("F6") : "NA";
            string fy = hd != null && rt != null && rt.height > 0
                ? ((double)hd.actualHeight / rt.height).ToString("F6") : "NA";

            object handler = Read(_handlerType, null, "instance");
            string drsResolved = Invoke(handler, "GetResolvedScale");
            string drsScale = Invoke(handler, "GetCurrentScale");
            string drsViewport = Display(Read(_handlerType, handler, "finalViewport"));
            string drsFilter = Display(Read(_handlerType, handler, "filter"));
            string request = Display(Read(_handlerType, handler, "m_CurrentCameraRequest"));
            string enabled = Invoke(handler, "DynamicResolutionEnabled");
            string software = Invoke(handler, "SoftwareDynamicResIsEnabled");
            string hardware = Invoke(handler, "HardwareDynamicResIsEnabled");
            string lastSize = Invoke(handler, "GetLastScaledSize");
            WriteRecord(ev, eye, cam.name + "#" + cam.GetInstanceID(),
                Display(cam.allowDynamicResolution), Display(data.allowDynamicResolution),
                Display(data.allowDeepLearningSuperSampling),
                Display(data.deepLearningSuperSamplingUseCustomQualitySettings),
                Display(data.deepLearningSuperSamplingQuality),
                Display(data.deepLearningSuperSamplingUseCustomAttributes),
                Display(data.deepLearningSuperSamplingUseOptimalSettings),
                target, rt == null ? "NA" : Display(rt.useDynamicScale),
                actual, fx, fy, drsResolved, drsScale, drsViewport, drsFilter,
                request, enabled, software, hardware, lastSize,
                ScalableBufferManager.widthScaleFactor.ToString("F6") + "x" +
                ScalableBufferManager.heightScaleFactor.ToString("F6"),
                CaptureAssetPolicy());
        }

        private static float _nextAssetPolicy;
        private static string _assetPolicy = "unread";
        private static string CaptureAssetPolicy()
        {
            if (Time.unscaledTime < _nextAssetPolicy) return _assetPolicy;
            _nextAssetPolicy = Time.unscaledTime + 2f;
            try
            {
                Type settings = typeof(GraphicsSettings);
                object asset = Read(settings, null, "currentRenderPipeline") ??
                    Read(settings, null, "renderPipelineAsset") ??
                    Read(settings, null, "defaultRenderPipeline");
                object platform = ReadAny(asset, "currentPlatformRenderPipelineSettings", "renderPipelineSettings");
                object drs = ReadAny(platform, "dynamicResolutionSettings", "m_DynamicResolutionSettings");
                _assetPolicy = drs == null ? "NOT_FOUND" :
                    "enabled:" + ReadAny(drs, "enabled") +
                    ",min:" + ReadAny(drs, "minPercentage", "minScreenPercentage") +
                    ",max:" + ReadAny(drs, "maxPercentage", "maxScreenPercentage") +
                    ",force:" + ReadAny(drs, "forceResolution") +
                    ",forcedPct:" + ReadAny(drs, "forcedPercentage") +
                    ",type:" + ReadAny(drs, "dynResType") +
                    ",filter:" + ReadAny(drs, "upscaleFilter") +
                    ",dlssQuality:" + ReadAny(drs, "DLSSPerfQualitySetting") +
                    ",dlssOptimal:" + ReadAny(drs, "DLSSUseOptimalSettings");
            }
            catch (Exception e) { _assetPolicy = "ERROR:" + e.GetType().Name; }
            return _assetPolicy;
        }

        private static void WriteMarker(string ev)
        {
            WriteRecord(ev, "-", "-", "-", "-", "-", "-", "-", "-", "-",
                "-", "-", "-", "-", "-", "-", "-", "-", "-", "-",
                "-", "-", "-", "-");
        }

        private static void WriteRecord(string ev, string eye, string cam, string camDyn,
            string hdDyn, string dlss, string customQ, string quality, string attrs,
            string optimal, string target, string useDynamic, string actual, string fx,
            string fy, string resolved, string current, string viewport, string filter,
            string request, string enabled, string sw, string hw, string last,
            string buffer = "-", string asset = "-")
        {
            if (_writer == null) return;
            // Manager-only events have no frame-scoped DRS state; use explicit '-' fields.
            string[] cols = {
                Time.unscaledTime.ToString("F3"), Time.frameCount.ToString(), _transition.ToString(),
                ev, eye, StereoRender.UserGameplayAERV112 ? "AER" : "2E",
                _currentOption, _currentQuality, cam, camDyn, hdDyn, dlss, customQ,
                quality, attrs, optimal, target, useDynamic, actual, fx, fy,
                resolved, current, viewport, filter, request, enabled, sw, hw, last,
                buffer, asset
            };
            _writer.WriteLine(string.Join("\t", cols));
            // Reduce synchronous I/O during camera rendering; flush only occasionally.
            if (Time.unscaledTime >= _nextFlush)
            {
                _writer.Flush();
                _nextFlush = Time.unscaledTime + 3.0f;
            }
        }
        private static float _nextFlush;

        private static string Display(object value)
        {
            try { return value == null ? "NA" : value.ToString().Replace('\t',' ').Replace('\n',' '); }
            catch { return "ERR"; }
        }
        private static object ReadAny(object obj, params string[] names)
        {
            foreach (string name in names)
            {
                object value = Read(obj == null ? null : obj.GetType(), obj, name);
                if (value != null) return value;
            }
            return null;
        }
        private static object Read(Type type, object obj, string member)
        {
            if (type == null) return null;
            try
            {
                PropertyInfo prop = type.GetProperty(member, Flags);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                    return prop.GetValue(obj, null);
                FieldInfo field = type.GetField(member, Flags);
                return field == null ? null : field.GetValue(obj);
            }
            catch { return null; }
        }
        private static string Invoke(object obj, string method)
        {
            if (obj == null) return "NA";
            try
            {
                MethodInfo info = obj.GetType().GetMethod(method, Flags, null, Type.EmptyTypes, null);
                return info == null ? "NA" : Display(info.Invoke(obj, null));
            }
            catch (Exception e) { return "ERR:" + e.GetType().Name; }
        }
        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = asm.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
        // V131: durability boundary before the intentional risk of a HDRP asset rebuild.
        internal static void V131FlushMarker(string label)
        {
            try
            {
                if (_writer == null) return;
                WriteMarker("V131_" + label);
                _writer.Flush();
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[V131 DLSS-ACT] marker flush failed: " + e.Message);
            }
        }

        internal static void Shutdown()
        {
            try { _writer?.Flush(); _writer?.Dispose(); }
            catch { }
            _writer = null;
        }
    }
}
