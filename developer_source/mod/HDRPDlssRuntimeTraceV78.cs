using MelonLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace SonsVRMod
{
    /// <summary>
    /// V78 PASSIVE runtime forensics for HDRP RTHandles / DynamicResolution / Sons.Dlss.
    /// Read-only: no render state or game setting is written.
    /// </summary>
    internal static class HDRPDlssRuntimeTraceV78
    {
        internal static bool Enabled = false; // V84 CLEAN PERFORMANCE ANCHOR

        private static readonly Dictionary<string, string> _last =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly Regex InterestingMember = new Regex(
            "dlss|dynamic|resolution|upscal|fsr|taau|mip|viewport|scale|filter|screen|render|quality|active|final",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] GlobalTextureNames = new[]
        {
            "_CameraDepthTexture",
            "_CameraColorTexture",
            "_CameraMotionVectorsTexture",
            "_DepthPyramidTexture",
            "_ColorPyramidTexture",
            "_AfterPostProcessTexture",
            "_InputColorTexture",
            "_OutputUpscaledTexture"
        };

        private static readonly string[] GlobalVectorNames = new[]
        {
            "_RTHandleScale",
            "_RTHandleScaleHistory",
            "_ScreenSize",
            "_PostProcessScreenSize",
            "_DynamicResolutionScreenSize",
            "_ZBufferParams",
            "_ScreenCoordScaleBias",
            "_ScreenSizeOverride",
            "_GlobalMipBias"
        };

        private static string _tracePath;
        private static bool _initialized;
        private static float _nextTick;
        private static float _nextHeartbeat;
        private static int _eventCount;
        private static readonly object _fileLock = new object();
        private static readonly Dictionary<string, Type> _typeCache =
            new Dictionary<string, Type>(StringComparer.Ordinal);
        private static Type[] _sonsDlssTypes = new Type[0];
        private static Type[] _settingsWrapperTypes = new Type[0];
        private static Type[] _sonsQualityTypes = new Type[0];
        private static readonly Dictionary<string, float> _nextBoundarySample =
            new Dictionary<string, float>(StringComparer.Ordinal);

        internal static string TracePath => _tracePath ?? "";

        internal static void Initialize()
        {
            if (!Enabled || _initialized)
                return;

            _initialized = true;

            try
            {
                string gameRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string logDir = Path.Combine(gameRoot, "MelonLoader", "Logs");
                Directory.CreateDirectory(logDir);
                _tracePath = Path.Combine(
                    logDir,
                    "SonsVR_HDRP_DLSS_TRACE_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".tsv");

                File.WriteAllText(
                    _tracePath,
                    "time\tframe\tscope\tcamera\tkey\told\tnew\n",
                    Encoding.UTF8);

                MelonLogger.Warning(
                    "[StereoRender][AER V78][TRACE] PASSIVE HDRP/DLSS FORENSICS ACTIVE. " +
                    "No render state will be changed. Dedicated trace: " + _tracePath);

                WriteRaw("INIT", "", "trace.path", "", _tracePath);
                WriteRaw("INIT", "", "unity.version", "", Application.unityVersion);
                WriteRaw("INIT", "", "game.version", "", Application.version);
                WriteRaw("INIT", "", "scene", "", SceneManager.GetActiveScene().name);

                InitializeTypeCaches();
                DumpRelevantTypeInventory();
                Capture("INIT", null, true);
            }
            catch (Exception e)
            {
                MelonLogger.Error(
                    "[StereoRender][AER V78][TRACE] initialization failed: " +
                    e.GetType().Name + ": " + e.Message);
            }
        }

        internal static void Shutdown()
        {
            if (!_initialized)
                return;
            try
            {
                Capture("QUIT", null, true);
                MelonLogger.Warning(
                    "[StereoRender][AER V78][TRACE] finished. events=" + _eventCount +
                    " file=" + _tracePath);
            }
            catch { }
        }

        internal static void Tick()
        {
            if (!Enabled)
                return;

            EyeDepthCaptureCustomPassV81.PollReadbacks();

            if (!Enabled)
                return;
            if (!_initialized)
                Initialize();
            if (!_initialized)
                return;

            float now = Time.unscaledTime;
            if (now >= _nextTick)
            {
                _nextTick = now + 0.50f; // 2 Hz; settings are held for seconds in the test protocol.
                Capture("TICK", null, false);
            }

            if (now >= _nextHeartbeat)
            {
                _nextHeartbeat = now + 2.0f;
                WriteRaw(
                    "HEARTBEAT", "", "alive", "",
                    "scene=" + SceneManager.GetActiveScene().name +
                    " screen=" + Screen.width + "x" + Screen.height);
            }
        }

        internal static void CameraBoundary(string boundary, Camera camera)
        {
            if (!Enabled || camera == null)
                return;
            if (!_initialized)
                Initialize();

            string n = camera.name ?? "";
            if (n != "VRLeftEye" &&
                n != "VRRightEye" &&
                n != "MainCameraFP" &&
                !n.Contains("MainCamera"))
                return;

            // Boundary sampling is intentionally throttled. We need the exact SRP phase, not
            // every single frame. 4-5 samples/sec per phase is plenty for settings held ~3 sec.
            string gate = boundary + "|" + n;
            float now = Time.unscaledTime;
            float next;
            if (_nextBoundarySample.TryGetValue(gate, out next) && now < next)
                return;
            _nextBoundarySample[gate] = now + 0.25f;

            CaptureBoundary(boundary, camera, false);
        }

        private static void CaptureBoundary(string scope, Camera camera, bool force)
        {
            string camName = camera != null ? camera.name : "";

            Record(scope, camName, "screen.size",
                Screen.width + "x" + Screen.height, force);
            SafeRecord(scope, camName, "scalable.widthScaleFactor",
                () => ScalableBufferManager.widthScaleFactor.ToString("R", CultureInfo.InvariantCulture), force);
            SafeRecord(scope, camName, "scalable.heightScaleFactor",
                () => ScalableBufferManager.heightScaleFactor.ToString("R", CultureInfo.InvariantCulture), force);

            DumpStaticTypeByName(
                "UnityEngine.XR.XRSettings",
                scope, camName, "xr.", force,
                new Regex("eyeTexture|renderViewport|stereoRendering|loadedDevice|enabled",
                          RegexOptions.IgnoreCase));

            if (camera != null)
            {
                Record(scope, camName, "camera.pixel",
                    camera.pixelWidth + "x" + camera.pixelHeight, force);
                SafeRecord(scope, camName, "camera.scaledPixel",
                    () => camera.scaledPixelWidth + "x" + camera.scaledPixelHeight, force);
                Record(scope, camName, "camera.pixelRect", camera.pixelRect.ToString("F3"), force);
                Record(scope, camName, "camera.rect", camera.rect.ToString("F5"), force);
                SafeRecord(scope, camName, "camera.allowDynamicResolution",
                    () => camera.allowDynamicResolution.ToString(), force);
                DumpTexture(scope, camName, "camera.targetTexture", camera.targetTexture, force);
                DumpHDAdditionalCameraData(camera, scope, camName, force);
                DumpHDCameraHistoryV79(camera, scope, camName, force);
            }

            // Exact runtime state relevant to the depth-atlas mapping hypothesis.
            DumpRTHandleProperties(scope, camName, force);
            DumpDynamicResolutionHandler(scope, camName, force);

            foreach (string texName in GlobalTextureNames)
            {
                try
                {
                    Texture t = Shader.GetGlobalTexture(texName);
                    DumpTexture(scope, camName, "shader.tex." + texName, t, force);
                }
                catch (Exception e)
                {
                    Record(scope, camName, "shader.tex." + texName + ".ERROR",
                        e.GetType().Name + ":" + e.Message, force);
                }
            }

            foreach (string vecName in GlobalVectorNames)
            {
                try
                {
                    Vector4 v = Shader.GetGlobalVector(vecName);
                    Record(scope, camName, "shader.vec." + vecName, v.ToString("F6"), force);
                }
                catch (Exception e)
                {
                    Record(scope, camName, "shader.vec." + vecName + ".ERROR",
                        e.GetType().Name + ":" + e.Message, force);
                }
            }
        }

        private static void Capture(string scope, Camera camera, bool force)
        {
            if (!_initialized)
                return;

            string camName = camera != null ? camera.name : "";

            Record(scope, camName, "screen.size",
                Screen.width + "x" + Screen.height, force);

            SafeRecord(scope, camName, "scalable.widthScaleFactor",
                () => ScalableBufferManager.widthScaleFactor.ToString("R", CultureInfo.InvariantCulture), force);
            SafeRecord(scope, camName, "scalable.heightScaleFactor",
                () => ScalableBufferManager.heightScaleFactor.ToString("R", CultureInfo.InvariantCulture), force);

            DumpStaticTypeByName(
                "UnityEngine.XR.XRSettings",
                scope, camName, "xr.", force,
                new Regex("eyeTexture|renderViewport|stereoRendering|loadedDevice|enabled",
                          RegexOptions.IgnoreCase));

            if (camera != null)
            {
                Record(scope, camName, "camera.pixel",
                    camera.pixelWidth + "x" + camera.pixelHeight, force);
                SafeRecord(scope, camName, "camera.scaledPixel",
                    () => camera.scaledPixelWidth + "x" + camera.scaledPixelHeight, force);
                Record(scope, camName, "camera.pixelRect", camera.pixelRect.ToString("F3"), force);
                Record(scope, camName, "camera.rect", camera.rect.ToString("F5"), force);
                SafeRecord(scope, camName, "camera.allowDynamicResolution",
                    () => camera.allowDynamicResolution.ToString(), force);

                if (camera.targetTexture != null)
                    DumpTexture(scope, camName, "camera.targetTexture", camera.targetTexture, force);
                else
                    Record(scope, camName, "camera.targetTexture", "null", force);

                DumpHDAdditionalCameraData(camera, scope, camName, force);
            }

            DumpRTHandleProperties(scope, camName, force);
            DumpDynamicResolutionHandler(scope, camName, force);
            DumpSonsDlss(scope, camName, force);
            DumpSonsSettingsWrappers(scope, camName, force);
            DumpSonsQuality(scope, camName, force);
            DumpRenderPipelineAssets(scope, camName, force);
            DumpQualitySettings(scope, camName, force);

            foreach (string texName in GlobalTextureNames)
            {
                try
                {
                    Texture t = Shader.GetGlobalTexture(texName);
                    DumpTexture(scope, camName, "shader.tex." + texName, t, force);
                }
                catch (Exception e)
                {
                    Record(scope, camName, "shader.tex." + texName + ".ERROR",
                        e.GetType().Name + ":" + e.Message, force);
                }
            }

            foreach (string vecName in GlobalVectorNames)
            {
                try
                {
                    Vector4 v = Shader.GetGlobalVector(vecName);
                    Record(scope, camName, "shader.vec." + vecName, v.ToString("F6"), force);
                }
                catch (Exception e)
                {
                    Record(scope, camName, "shader.vec." + vecName + ".ERROR",
                        e.GetType().Name + ":" + e.Message, force);
                }
            }
        }

        private static void DumpRTHandleProperties(
            string scope, string camName, bool force)
        {
            Type t = FindType("UnityEngine.Rendering.RTHandles");
            if (t == null)
            {
                Record(scope, camName, "rthandles.type", "NOT_FOUND", force);
                return;
            }

            object props = GetStaticMember(t, "rtHandleProperties");
            if (props == null)
            {
                Record(scope, camName, "rthandles.rtHandleProperties", "null/unavailable", force);
                return;
            }

            DumpObjectMembers(
                props, props.GetType(), scope, camName, "rthandles.", force,
                new Regex("viewport|renderTarget|rtHandle|scale|size|previous|current",
                          RegexOptions.IgnoreCase), 0, 1);
        }

        private static void DumpDynamicResolutionHandler(
            string scope, string camName, bool force)
        {
            Type t = FindType("UnityEngine.Rendering.DynamicResolutionHandler");
            if (t == null)
            {
                Record(scope, camName, "drs.type", "NOT_FOUND", force);
                return;
            }

            object inst = GetStaticMember(t, "instance");
            if (inst == null)
            {
                Record(scope, camName, "drs.instance", "null/unavailable", force);
                return;
            }

            DumpObjectMembers(
                inst, inst.GetType(), scope, camName, "drs.", force,
                new Regex("dynamic|scale|filter|viewport|upsampl|hardware|software|final|resolution|mip|request|run",
                          RegexOptions.IgnoreCase), 0, 1);

            InvokeZeroArgMethods(
                inst, inst.GetType(), scope, camName, "drs.call.", force,
                new Regex("DynamicResolutionEnabled|HardwareDynamicResIsEnabled|SoftwareDynamicResIsEnabled|RequestsHardwareDynamicResolution",
                          RegexOptions.IgnoreCase));
        }

        private static void DumpSonsDlss(
            string scope, string camName, bool force)
        {
            foreach (Type t in _sonsDlssTypes)
            {
                string p = "sons.dlss." + t.FullName + ".";
                DumpStaticMembers(
                    t, scope, camName, p, force,
                    new Regex("dlss|dynamic|resolution|upscal|fsr|taau|active|quality|option|setting|filter",
                              RegexOptions.IgnoreCase));

                InvokeStaticZeroArgMethods(
                    t, scope, camName, p + "call.", force,
                    new Regex("^GetDynamicResolutionOption$|^IsDlssSupported$|^IsFsr3Supported$|^get_",
                              RegexOptions.IgnoreCase));
            }
        }

        private static void DumpSonsSettingsWrappers(
            string scope, string camName, bool force)
        {
            foreach (Type t in _settingsWrapperTypes)
            {
                string p = "sons.settingswrappers." + t.FullName + ".";
                DumpStaticMembers(
                    t, scope, camName, p, force,
                    new Regex("dlss|dynamic|upscal|mip|fsr|resolution|budget|active",
                              RegexOptions.IgnoreCase));
            }
        }

        private static void DumpSonsQuality(
            string scope, string camName, bool force)
        {
            foreach (Type t in _sonsQualityTypes)
            {
                string p = "sons.quality." + t.FullName + ".";
                DumpStaticMembers(
                    t, scope, camName, p, force,
                    new Regex("screen|percentage|dynamic|resolution|scale",
                              RegexOptions.IgnoreCase));
            }
        }

        private static void DumpRenderPipelineAssets(
            string scope, string camName, bool force)
        {
            Type graphicsSettings = FindType("UnityEngine.Rendering.GraphicsSettings");
            if (graphicsSettings != null)
            {
                object current = GetStaticMember(graphicsSettings, "currentRenderPipeline");
                DumpPipelineObject(current, scope, camName, "pipeline.current.", force);

                object defaultRp = GetStaticMember(graphicsSettings, "defaultRenderPipeline");
                DumpPipelineObject(defaultRp, scope, camName, "pipeline.default.", force);
            }

            Type qualitySettings = FindType("UnityEngine.QualitySettings");
            if (qualitySettings != null)
            {
                object qrp = GetStaticMember(qualitySettings, "renderPipeline");
                DumpPipelineObject(qrp, scope, camName, "pipeline.quality.", force);
            }
        }

        private static void DumpPipelineObject(
            object obj, string scope, string camName, string prefix, bool force)
        {
            if (obj == null)
            {
                Record(scope, camName, prefix + "object", "null", force);
                return;
            }

            Record(scope, camName, prefix + "type", obj.GetType().FullName, force);
            Record(scope, camName, prefix + "value", FormatValue(obj), force);

            DumpObjectMembers(
                obj, obj.GetType(), scope, camName, prefix, force,
                new Regex("dynamic|resolution|dlss|upscal|fsr|mip|screen|percentage|render|quality|platform|setting",
                          RegexOptions.IgnoreCase), 0, 2);
        }

        private static void DumpQualitySettings(
            string scope, string camName, bool force)
        {
            Type t = FindType("UnityEngine.QualitySettings");
            if (t == null)
                return;

            DumpStaticMembers(
                t, scope, camName, "quality.", force,
                new Regex("mip|texture|lod|resolution|render|quality|stream",
                          RegexOptions.IgnoreCase));
        }

        private static void DumpHDAdditionalCameraData(
            Camera camera, string scope, string camName, bool force)
        {
            try
            {
                Component[] comps = camera.gameObject.GetComponents<Component>();
                foreach (Component c in comps)
                {
                    if (c == null)
                        continue;
                    Type t = c.GetType();
                    if (t.Name != "HDAdditionalCameraData" &&
                        (t.FullName == null ||
                         t.FullName.IndexOf("HDAdditionalCameraData",
                             StringComparison.OrdinalIgnoreCase) < 0))
                        continue;

                    DumpObjectMembers(
                        c, t, scope, camName, "hdcamera.", force,
                        new Regex("dlss|deep|dynamic|resolution|custom|frame|render|quality|anti|scale",
                                  RegexOptions.IgnoreCase), 0, 1);
                    break;
                }
            }
            catch (Exception e)
            {
                Record(scope, camName, "hdcamera.ERROR",
                    e.GetType().Name + ":" + e.Message, force);
            }
        }

        // V79: HDCamera owns a SEPARATE history RTHandle system. Global RTHandles can
        // legitimately still describe MainCameraFP at SRP_BEGIN; what matters for temporal
        // contamination is whether this per-eye history system follows MainCamera's dimensions.
        private static void DumpHDCameraHistoryV79(
            Camera camera, string scope, string camName, bool force)
        {
            try
            {
                HDCamera hd = HDCamera.GetOrCreate(camera, 0);
                if (hd == null)
                {
                    Record(scope, camName, "hdhistory.HDCamera", "null", force);
                    return;
                }

                Type t = hd.GetType();

                string[] simpleNames = new[]
                {
                    "actualWidth",
                    "actualHeight",
                    "viewCount",
                    "cameraFrameCount",
                    "historyValidity",
                    "antialiasing",
                    "isFirstFrame",
                    "resetPostProcessingHistory"
                };

                foreach (string n in simpleNames)
                {
                    try
                    {
                        object v = GetMember(hd, n);
                        if (v != null)
                            Record(scope, camName, "hdhistory." + n, FormatValue(v), force);
                    }
                    catch (Exception e)
                    {
                        Record(scope, camName, "hdhistory." + n + ".ERROR",
                            e.GetType().Name + ":" + e.Message, force);
                    }
                }

                object props = null;
                try { props = GetMember(hd, "historyRTHandleProperties"); }
                catch { }

                if (props != null)
                {
                    DumpObjectMembers(
                        props, props.GetType(),
                        scope, camName, "hdhistory.props.", force,
                        new Regex("viewport|renderTarget|rtHandle|scale|size|previous|current",
                                  RegexOptions.IgnoreCase),
                        0, 1);
                }
                else
                {
                    Record(scope, camName, "hdhistory.props", "null/unavailable", force);
                }

                // Internal GetHistoryRTHandleSystem() exists in this HDRP build. Reflection is
                // intentional so the tracer remains diagnostic even though the method is internal
                // to Unity's assembly.
                object historySystem = InvokeNamedZeroArgV79(hd, t, "GetHistoryRTHandleSystem");
                if (historySystem != null)
                {
                    Record(scope, camName, "hdhistory.system.type",
                        historySystem.GetType().FullName, force);

                    object sysProps = null;
                    try { sysProps = GetMember(historySystem, "rtHandleProperties"); }
                    catch { }

                    if (sysProps != null)
                    {
                        DumpObjectMembers(
                            sysProps, sysProps.GetType(),
                            scope, camName, "hdhistory.system.props.", force,
                            new Regex("viewport|renderTarget|rtHandle|scale|size|previous|current",
                                      RegexOptions.IgnoreCase),
                            0, 1);
                    }

                    DumpObjectMembers(
                        historySystem, historySystem.GetType(),
                        scope, camName, "hdhistory.system.", force,
                        new Regex("width|height|reference|frame|history|rtHandle|properties",
                                  RegexOptions.IgnoreCase),
                        0, 1);
                }
                else
                {
                    Record(scope, camName, "hdhistory.system", "null/unavailable", force);
                }
            }
            catch (Exception e)
            {
                Record(scope, camName, "hdhistory.ERROR",
                    e.GetType().Name + ":" + e.Message, force);
            }
        }

        private static object InvokeNamedZeroArgV79(object obj, Type t, string name)
        {
            try
            {
                MethodInfo[] methods = SafeGetMethods(
                    t, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (MethodInfo m in methods)
                {
                    if (m.Name == name && m.GetParameters().Length == 0)
                        return m.Invoke(obj, null);
                }
            }
            catch { }
            return null;
        }

        // External passive measurements generated by StereoRender itself.
        internal static void RecordCoverageV79(
            string sourceEye,
            string targetEye,
            int sourceFrame,
            int width,
            int height,
            float holeFraction,
            float coveredFraction,
            float highConfidenceFraction,
            float meanConfidence,
            float largestHoleFraction,
            Vector4 largestHoleBBoxBottomLeft,
            Vector2 largestHoleCentroidBottomLeft)
        {
            if (!_initialized) Initialize();

            string value =
                "source=" + sourceEye +
                "|target=" + targetEye +
                "|sourceFrame=" + sourceFrame +
                "|debug=" + width + "x" + height +
                "|holeFrac=" + holeFraction.ToString("F6", CultureInfo.InvariantCulture) +
                "|coveredFrac=" + coveredFraction.ToString("F6", CultureInfo.InvariantCulture) +
                "|highConfFrac=" + highConfidenceFraction.ToString("F6", CultureInfo.InvariantCulture) +
                "|meanConf=" + meanConfidence.ToString("F6", CultureInfo.InvariantCulture) +
                "|largestHoleFrac=" + largestHoleFraction.ToString("F6", CultureInfo.InvariantCulture) +
                "|bboxBL=" + largestHoleBBoxBottomLeft.ToString("F6") +
                "|centroidBL=" + largestHoleCentroidBottomLeft.ToString("F6");

            // Coverage is deliberately NOT de-duplicated. We want a time series across the
            // setting transitions, not merely one row per unique geometry.
            WriteRaw("COVERAGE_V79", targetEye, "v51.coverage", "", value);
        }

        internal static void RecordDepthInputV80(
            string eye,
            int sourceFrame,
            int sourceWidth,
            int sourceHeight,
            int atlasWidth,
            int atlasHeight,
            int globalDepthInstanceId,
            string globalDepthName,
            float rawZeroFraction,
            float rawNonzeroFraction,
            float validFraction,
            float tooFarFraction,
            float discontinuityFraction,
            float approxMedianDepthMeters,
            float validTop,
            float validMiddle,
            float validBottom,
            float validLeft,
            float validCenter,
            float validRight,
            float largestInvalidFraction,
            Vector4 largestInvalidBBoxBottomLeft,
            Vector2 largestInvalidCentroidBottomLeft)
        {
            if (!_initialized) Initialize();

            string value =
                "eye=" + eye +
                "|frame=" + sourceFrame +
                "|source=" + sourceWidth + "x" + sourceHeight +
                "|atlas=" + atlasWidth + "x" + atlasHeight +
                "|depthId=" + globalDepthInstanceId +
                "|depthName=" + Sanitize(globalDepthName ?? "") +
                "|zeroFrac=" + rawZeroFraction.ToString("F6", CultureInfo.InvariantCulture) +
                "|nonzeroFrac=" + rawNonzeroFraction.ToString("F6", CultureInfo.InvariantCulture) +
                "|validFrac=" + validFraction.ToString("F6", CultureInfo.InvariantCulture) +
                "|tooFarFrac=" + tooFarFraction.ToString("F6", CultureInfo.InvariantCulture) +
                "|discFrac=" + discontinuityFraction.ToString("F6", CultureInfo.InvariantCulture) +
                "|medianDepthM=" + approxMedianDepthMeters.ToString("F4", CultureInfo.InvariantCulture) +
                "|validRows=" +
                    validTop.ToString("F5", CultureInfo.InvariantCulture) + "," +
                    validMiddle.ToString("F5", CultureInfo.InvariantCulture) + "," +
                    validBottom.ToString("F5", CultureInfo.InvariantCulture) +
                "|validCols=" +
                    validLeft.ToString("F5", CultureInfo.InvariantCulture) + "," +
                    validCenter.ToString("F5", CultureInfo.InvariantCulture) + "," +
                    validRight.ToString("F5", CultureInfo.InvariantCulture) +
                "|largestInvalidFrac=" + largestInvalidFraction.ToString("F6", CultureInfo.InvariantCulture) +
                "|invalidBBoxBL=" + largestInvalidBBoxBottomLeft.ToString("F6") +
                "|invalidCentroidBL=" + largestInvalidCentroidBottomLeft.ToString("F6");

            WriteRaw("DEPTH_V80", eye, "v50.depthInput", "", value);
        }

        internal static void RecordLocalDepthV81(
            string eye,
            int frame,
            int slice,
            int sourceWidth,
            int sourceHeight,
            int sourceVolumeDepth,
            int sourceInstanceId,
            string sourceName,
            float zeroFraction,
            float nonzeroFraction,
            float validFraction,
            float tooFarFraction,
            float discontinuityFraction,
            float approxMedianDepthMeters,
            float validTop,
            float validMiddle,
            float validBottom,
            float validLeft,
            float validCenter,
            float validRight,
            float largestInvalidFraction,
            Vector4 largestInvalidBBoxBottomLeft,
            Vector2 largestInvalidCentroidBottomLeft,
            Vector4 zParams)
        {
            if (!_initialized) Initialize();

            string value =
                "eye=" + eye +
                "|frame=" + frame +
                "|slice=" + slice +
                "|cameraLocalSource=" + sourceWidth + "x" + sourceHeight +
                "|volumeDepth=" + sourceVolumeDepth +
                "|sourceId=" + sourceInstanceId +
                "|sourceName=" + Sanitize(sourceName ?? "") +
                "|zeroFrac=" + zeroFraction.ToString("F6", CultureInfo.InvariantCulture) +
                "|nonzeroFrac=" + nonzeroFraction.ToString("F6", CultureInfo.InvariantCulture) +
                "|validFrac=" + validFraction.ToString("F6", CultureInfo.InvariantCulture) +
                "|tooFarFrac=" + tooFarFraction.ToString("F6", CultureInfo.InvariantCulture) +
                "|discFrac=" + discontinuityFraction.ToString("F6", CultureInfo.InvariantCulture) +
                "|medianDepthM=" + approxMedianDepthMeters.ToString("F4", CultureInfo.InvariantCulture) +
                "|validRows=" +
                    validTop.ToString("F5", CultureInfo.InvariantCulture) + "," +
                    validMiddle.ToString("F5", CultureInfo.InvariantCulture) + "," +
                    validBottom.ToString("F5", CultureInfo.InvariantCulture) +
                "|validCols=" +
                    validLeft.ToString("F5", CultureInfo.InvariantCulture) + "," +
                    validCenter.ToString("F5", CultureInfo.InvariantCulture) + "," +
                    validRight.ToString("F5", CultureInfo.InvariantCulture) +
                "|largestInvalidFrac=" + largestInvalidFraction.ToString("F6", CultureInfo.InvariantCulture) +
                "|invalidBBoxBL=" + largestInvalidBBoxBottomLeft.ToString("F6") +
                "|invalidCentroidBL=" + largestInvalidCentroidBottomLeft.ToString("F6") +
                "|zParams=" + zParams.ToString("F7");

            WriteRaw(
                "LOCALDEPTH_V81",
                eye,
                "custompass.cameraDepthBuffer",
                "",
                value);
        }

        internal static void RecordProjectionV80(
            string eye,
            Matrix4x4 sourceProjection,
            Matrix4x4 sourceGpuProjection,
            Matrix4x4 targetProjection,
            Matrix4x4 targetGpuProjection,
            Vector4 zParams)
        {
            if (!_initialized) Initialize();

            Record("PROJECTION_V80", eye, "source.proj",
                FormatMatrixV80(sourceProjection), false);
            Record("PROJECTION_V80", eye, "source.gpuProj",
                FormatMatrixV80(sourceGpuProjection), false);
            Record("PROJECTION_V80", eye, "target.proj",
                FormatMatrixV80(targetProjection), false);
            Record("PROJECTION_V80", eye, "target.gpuProj",
                FormatMatrixV80(targetGpuProjection), false);
            Record("PROJECTION_V80", eye, "zParams",
                zParams.ToString("F7"), false);
        }

        private static string FormatMatrixV80(Matrix4x4 m)
        {
            return String.Format(
                CultureInfo.InvariantCulture,
                "[{0:F7},{1:F7},{2:F7},{3:F7};{4:F7},{5:F7},{6:F7},{7:F7};" +
                "{8:F7},{9:F7},{10:F7},{11:F7};{12:F7},{13:F7},{14:F7},{15:F7}]",
                m.m00,m.m01,m.m02,m.m03,
                m.m10,m.m11,m.m12,m.m13,
                m.m20,m.m21,m.m22,m.m23,
                m.m30,m.m31,m.m32,m.m33);
        }

        private static float _nextSubmitTraceV79;

        internal static void RecordSubmissionV79(
            RenderTexture left,
            RenderTexture right,
            float uMin,
            float vMin,
            float uMax,
            float vMax,
            int steamSceneWidth,
            int steamSceneHeight,
            string textureType)
        {
            if (!_initialized) Initialize();

            float now = Time.unscaledTime;
            if (now < _nextSubmitTraceV79)
                return;
            _nextSubmitTraceV79 = now + 0.50f;

            string leftDesc = DescribeSubmitTextureV79(left);
            string rightDesc = DescribeSubmitTextureV79(right);

            Record(
                "SUBMIT_V79", "L", "openvr.texture",
                leftDesc, false);
            Record(
                "SUBMIT_V79", "R", "openvr.texture",
                rightDesc, false);

            Record(
                "SUBMIT_V79", "", "openvr.bounds",
                String.Format(CultureInfo.InvariantCulture,
                    "u=[{0:F6},{1:F6}]|v=[{2:F6},{3:F6}]",
                    uMin, uMax, vMin, vMax),
                false);

            Record(
                "SUBMIT_V79", "", "openvr.sceneRecommended",
                steamSceneWidth + "x" + steamSceneHeight, false);
            Record(
                "SUBMIT_V79", "", "openvr.textureType",
                textureType ?? "", false);
        }

        private static string DescribeSubmitTextureV79(RenderTexture rt)
        {
            if (rt == null)
                return "null";

            string handle = "0x0";
            try
            {
                IntPtr p = rt.GetNativeTexturePtr();
                handle = "0x" + p.ToInt64().ToString("X");
            }
            catch { }

            return
                (rt.name ?? "<unnamed>") +
                "|" + rt.width + "x" + rt.height +
                "|fmt=" + rt.graphicsFormat +
                "|dyn=" + rt.useDynamicScale +
                "|handle=" + handle;
        }

        private static void DumpTexture(
            string scope, string camName, string prefix, Texture t, bool force)
        {
            if (t == null)
            {
                Record(scope, camName, prefix, "null", force);
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(t.name ?? "<unnamed>");
            sb.Append("|").Append(t.GetType().Name);
            sb.Append("|").Append(t.width).Append("x").Append(t.height);

            SafeAppendProperty(sb, t, "dimension");
            SafeAppendProperty(sb, t, "graphicsFormat");
            SafeAppendProperty(sb, t, "mipmapCount");
            SafeAppendProperty(sb, t, "volumeDepth");
            SafeAppendProperty(sb, t, "useDynamicScale");
            SafeAppendProperty(sb, t, "vrUsage");
            SafeAppendProperty(sb, t, "antiAliasing");

            Record(scope, camName, prefix, sb.ToString(), force);
        }

        private static void SafeAppendProperty(
            StringBuilder sb, object obj, string member)
        {
            try
            {
                object v = GetMember(obj, member);
                if (v != null)
                    sb.Append("|").Append(member).Append("=").Append(FormatValue(v));
            }
            catch { }
        }

        private static void DumpStaticMembers(
            Type t, string scope, string camName, string prefix, bool force, Regex include)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            foreach (PropertyInfo p in SafeGetProperties(t, flags))
            {
                if (!include.IsMatch(p.Name) || p.GetIndexParameters().Length != 0)
                    continue;
                try
                {
                    MethodInfo getter = p.GetGetMethod(true);
                    if (getter == null) continue;
                    object v = p.GetValue(null, null);
                    Record(scope, camName, prefix + p.Name, FormatValue(v), force);
                }
                catch (Exception e)
                {
                    Record(scope, camName, prefix + p.Name + ".ERROR",
                        e.GetType().Name + ":" + e.Message, force);
                }
            }

            foreach (FieldInfo f in SafeGetFields(t, flags))
            {
                if (f.Name.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal))
                    continue;
                if (!include.IsMatch(f.Name))
                    continue;
                try
                {
                    object v = f.GetValue(null);
                    Record(scope, camName, prefix + f.Name, FormatValue(v), force);
                }
                catch (Exception e)
                {
                    Record(scope, camName, prefix + f.Name + ".ERROR",
                        e.GetType().Name + ":" + e.Message, force);
                }
            }
        }

        private static void DumpObjectMembers(
            object obj, Type t,
            string scope, string camName, string prefix, bool force,
            Regex include, int depth, int maxDepth)
        {
            if (obj == null || t == null)
                return;

            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (PropertyInfo p in SafeGetProperties(t, flags))
            {
                if (p.GetIndexParameters().Length != 0)
                    continue;

                bool interesting = include.IsMatch(p.Name);
                bool carrier = IsCarrierName(p.Name);
                if (!interesting && !(carrier && depth < maxDepth))
                    continue;

                try
                {
                    MethodInfo getter = p.GetGetMethod(true);
                    if (getter == null) continue;

                    object v = p.GetValue(obj, null);
                    string key = prefix + p.Name;

                    if (IsSimple(v))
                        Record(scope, camName, key, FormatValue(v), force);
                    else if (v != null && depth < maxDepth && (interesting || carrier))
                    {
                        Record(scope, camName, key + ".type", v.GetType().FullName, force);
                        DumpObjectMembers(
                            v, v.GetType(), scope, camName, key + ".",
                            force, include, depth + 1, maxDepth);
                    }
                }
                catch (Exception e)
                {
                    Record(scope, camName, prefix + p.Name + ".ERROR",
                        e.GetType().Name + ":" + e.Message, force);
                }
            }

            foreach (FieldInfo f in SafeGetFields(t, flags))
            {
                if (f.Name.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal))
                    continue;

                bool interesting = include.IsMatch(f.Name);
                bool carrier = IsCarrierName(f.Name);
                if (!interesting && !(carrier && depth < maxDepth))
                    continue;

                try
                {
                    object v = f.GetValue(obj);
                    string key = prefix + f.Name;
                    if (IsSimple(v))
                        Record(scope, camName, key, FormatValue(v), force);
                    else if (v != null && depth < maxDepth && (interesting || carrier))
                    {
                        Record(scope, camName, key + ".type", v.GetType().FullName, force);
                        DumpObjectMembers(
                            v, v.GetType(), scope, camName, key + ".",
                            force, include, depth + 1, maxDepth);
                    }
                }
                catch (Exception e)
                {
                    Record(scope, camName, prefix + f.Name + ".ERROR",
                        e.GetType().Name + ":" + e.Message, force);
                }
            }
        }

        private static void InvokeZeroArgMethods(
            object obj, Type t,
            string scope, string camName, string prefix, bool force, Regex include)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (MethodInfo m in SafeGetMethods(t, flags))
            {
                if (!include.IsMatch(m.Name) || m.IsGenericMethod ||
                    m.GetParameters().Length != 0 || m.ReturnType == typeof(void) || m.IsSpecialName)
                    continue;
                try
                {
                    object v = m.Invoke(obj, null);
                    Record(scope, camName, prefix + m.Name, FormatValue(v), force);
                }
                catch (Exception e)
                {
                    Record(scope, camName, prefix + m.Name + ".ERROR",
                        e.GetType().Name + ":" + e.Message, force);
                }
            }
        }

        private static void InvokeStaticZeroArgMethods(
            Type t, string scope, string camName, string prefix, bool force, Regex include)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (MethodInfo m in SafeGetMethods(t, flags))
            {
                if (!include.IsMatch(m.Name) || m.IsGenericMethod ||
                    m.GetParameters().Length != 0 || m.ReturnType == typeof(void) || m.IsSpecialName)
                    continue;
                try
                {
                    object v = m.Invoke(null, null);
                    Record(scope, camName, prefix + m.Name, FormatValue(v), force);
                }
                catch (Exception e)
                {
                    Record(scope, camName, prefix + m.Name + ".ERROR",
                        e.GetType().Name + ":" + e.Message, force);
                }
            }
        }

        private static bool HasInterestingMember(Type t, Regex rx)
        {
            try
            {
                foreach (PropertyInfo p in t.GetProperties(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.Instance))
                    if (rx.IsMatch(p.Name)) return true;

                foreach (FieldInfo f in t.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.Instance))
                    if (rx.IsMatch(f.Name)) return true;
            }
            catch { }
            return false;
        }

        private static bool IsAssemblyLike(Type t, string token)
        {
            try
            {
                string an = t.Assembly.GetName().Name ?? "";
                string fn = t.FullName ?? "";
                return an.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       fn.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static bool IsCarrierName(string n)
        {
            if (String.IsNullOrEmpty(n)) return false;
            return Regex.IsMatch(
                n,
                "settings|platform|dynamic|resolution|render|quality|pipeline|frame|mip|dlss|upscal",
                RegexOptions.IgnoreCase);
        }

        private static bool IsSimple(object v)
        {
            if (v == null) return true;
            Type t = v.GetType();
            return t.IsPrimitive || t.IsEnum || v is string || v is decimal ||
                   v is Vector2 || v is Vector3 || v is Vector4 ||
                   v is Vector2Int || v is Vector3Int ||
                   v is Rect || v is Color || v is Bounds ||
                   v is UnityEngine.Object ||
                   t.FullName == "System.String" ||
                   t.FullName == "Il2CppSystem.String";
        }

        private static string FormatValue(object v)
        {
            if (v == null) return "null";
            try
            {
                if (v is UnityEngine.Object uo)
                    return uo == null ? "UnityObject:null" :
                        uo.name + "<" + uo.GetType().FullName + ">";

                if (v is string s) return Sanitize(s);

                Type t = v.GetType();
                if (t.IsEnum || t.IsPrimitive || v is decimal)
                    return Convert.ToString(v, CultureInfo.InvariantCulture);

                if (v is IEnumerable enumerable && !(v is string))
                {
                    StringBuilder sb = new StringBuilder("[");
                    int n = 0;
                    foreach (object item in enumerable)
                    {
                        if (n > 0) sb.Append(",");
                        if (n >= 16) { sb.Append("..."); break; }
                        sb.Append(FormatValue(item));
                        n++;
                    }
                    sb.Append("]");
                    return Sanitize(sb.ToString());
                }

                return Sanitize(v.ToString());
            }
            catch (Exception e)
            {
                return "<format-error:" + e.GetType().Name + ">";
            }
        }

        private static object GetStaticMember(Type t, string name)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            try
            {
                PropertyInfo p = t.GetProperty(name, flags);
                if (p != null && p.GetIndexParameters().Length == 0 &&
                    p.GetGetMethod(true) != null)
                    return p.GetValue(null, null);
            }
            catch { }

            try
            {
                FieldInfo f = t.GetField(name, flags);
                if (f != null && !f.Name.StartsWith("NativeFieldInfoPtr_"))
                    return f.GetValue(null);
            }
            catch { }

            return null;
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            Type t = obj.GetType();
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            PropertyInfo p = t.GetProperty(name, flags);
            if (p != null && p.GetIndexParameters().Length == 0 && p.GetGetMethod(true) != null)
                return p.GetValue(obj, null);

            FieldInfo f = t.GetField(name, flags);
            if (f != null && !f.Name.StartsWith("NativeFieldInfoPtr_"))
                return f.GetValue(obj);

            return null;
        }

        private static void DumpStaticTypeByName(
            string fullName,
            string scope, string camName, string prefix, bool force, Regex include)
        {
            Type t = FindType(fullName);
            if (t == null)
            {
                Record(scope, camName, prefix + "type", "NOT_FOUND:" + fullName, force);
                return;
            }
            DumpStaticMembers(t, scope, camName, prefix, force, include);
        }

        private static Type FindType(string fullName)
        {
            Type cached;
            if (_typeCache.TryGetValue(fullName, out cached))
                return cached;

            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType(fullName, false);
                    if (t != null)
                    {
                        _typeCache[fullName] = t;
                        return t;
                    }
                }
                catch { }
            }

            foreach (Type t in FindTypes(x =>
                String.Equals(x.FullName, fullName, StringComparison.Ordinal)))
            {
                _typeCache[fullName] = t;
                return t;
            }

            _typeCache[fullName] = null;
            return null;
        }

        private static IEnumerable<Type> FindTypes(Func<Type, bool> predicate)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types = null;
                try { types = a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }

                if (types == null) continue;

                foreach (Type t in types)
                {
                    if (t == null) continue;
                    bool ok = false;
                    try { ok = predicate(t); } catch { }
                    if (ok) yield return t;
                }
            }
        }

        private static PropertyInfo[] SafeGetProperties(Type t, BindingFlags f)
        {
            try { return t.GetProperties(f); }
            catch { return new PropertyInfo[0]; }
        }

        private static FieldInfo[] SafeGetFields(Type t, BindingFlags f)
        {
            try { return t.GetFields(f); }
            catch { return new FieldInfo[0]; }
        }

        private static MethodInfo[] SafeGetMethods(Type t, BindingFlags f)
        {
            try { return t.GetMethods(f); }
            catch { return new MethodInfo[0]; }
        }

        private static void InitializeTypeCaches()
        {
            try
            {
                List<Type> dlss = new List<Type>();
                List<Type> wrappers = new List<Type>();
                List<Type> quality = new List<Type>();

                foreach (Type t in FindTypes(type =>
                {
                    string n = type.FullName ?? "";
                    string a = type.Assembly.GetName().Name ?? "";
                    return n.IndexOf("DynamicResUpscaleManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           n.IndexOf("DynamicResolutionSettingReceiver", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           a.IndexOf("Sons.SettingsWrappers", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           a.IndexOf("Sons.Quality", StringComparison.OrdinalIgnoreCase) >= 0;
                }))
                {
                    string n = t.FullName ?? "";
                    string a = t.Assembly.GetName().Name ?? "";

                    if (n.IndexOf("DynamicResUpscaleManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("DynamicResolutionSettingReceiver", StringComparison.OrdinalIgnoreCase) >= 0)
                        dlss.Add(t);

                    if (a.IndexOf("Sons.SettingsWrappers", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        HasInterestingMember(t,
                            new Regex("dlss|dynamic|upscal|mip|fsr|resolution",
                                      RegexOptions.IgnoreCase)))
                        wrappers.Add(t);

                    if (a.IndexOf("Sons.Quality", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        HasInterestingMember(t,
                            new Regex("screen|percentage|dynamic|resolution|scale",
                                      RegexOptions.IgnoreCase)))
                        quality.Add(t);
                }

                _sonsDlssTypes = dlss.ToArray();
                _settingsWrapperTypes = wrappers.ToArray();
                _sonsQualityTypes = quality.ToArray();

                MelonLogger.Msg(
                    $"[StereoRender][AER V78][TRACE] cached reflection types: " +
                    $"dlss={_sonsDlssTypes.Length} wrappers={_settingsWrapperTypes.Length} " +
                    $"quality={_sonsQualityTypes.Length}.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning(
                    "[StereoRender][AER V78][TRACE] type-cache init failed: " +
                    e.GetType().Name + ": " + e.Message);
            }
        }

        private static void DumpRelevantTypeInventory()
        {
            try
            {
                foreach (Type t in FindTypes(type =>
                {
                    string n = (type.FullName ?? "") + " " + (type.Assembly.GetName().Name ?? "");
                    return Regex.IsMatch(
                        n,
                        "DynamicResUpscaleManager|DynamicResolutionSettingReceiver|DynamicResolutionHandler|RTHandles|HDRenderPipelineAsset|SettingsWrappers",
                        RegexOptions.IgnoreCase);
                }))
                {
                    WriteRaw(
                        "TYPE", "", t.FullName ?? t.Name, "",
                        t.Assembly.GetName().Name ?? "");
                }
            }
            catch { }
        }

        private static void SafeRecord(
            string scope, string cam, string key, Func<string> getter, bool force)
        {
            try { Record(scope, cam, key, getter(), force); }
            catch (Exception e)
            {
                Record(scope, cam, key + ".ERROR",
                    e.GetType().Name + ":" + e.Message, force);
            }
        }

        private static void Record(
            string scope, string cam, string key, string value, bool force)
        {
            value = Sanitize(value ?? "null");
            string stateKey = scope + "|" + cam + "|" + key;

            string old;
            bool had = _last.TryGetValue(stateKey, out old);
            if (!force && had && old == value) return;

            _last[stateKey] = value;
            WriteRaw(scope, cam, key, had ? old : "", value);

            if (IsMajorKey(key) && had && old != value)
            {
                MelonLogger.Warning(
                    "[StereoRender][AER V78][TRACE-CHANGE] " +
                    scope + " " + (String.IsNullOrEmpty(cam) ? "" : cam + " ") +
                    key + ": '" + old + "' -> '" + value + "'");
            }
        }

        private static bool IsMajorKey(string key)
        {
            return Regex.IsMatch(
                key,
                "sons\\.dlss|dynamicResUpscaleActive|MipMapLimit|rthandles|hdhistory|drs\\.|scalable\\.|shader\\.tex\\._CameraDepthTexture|shader\\.vec\\._RTHandleScale|xr\\.|renderViewport",
                RegexOptions.IgnoreCase);
        }

        private static void WriteRaw(
            string scope, string cam, string key, string oldValue, string newValue)
        {
            if (String.IsNullOrEmpty(_tracePath)) return;

            string line =
                DateTime.Now.ToString("HH:mm:ss.fff") + "\t" +
                Time.frameCount + "\t" +
                Sanitize(scope) + "\t" +
                Sanitize(cam) + "\t" +
                Sanitize(key) + "\t" +
                Sanitize(oldValue) + "\t" +
                Sanitize(newValue) + "\n";

            lock (_fileLock)
            {
                File.AppendAllText(_tracePath, line, Encoding.UTF8);
            }
            _eventCount++;
        }

        private static string Sanitize(string s)
        {
            if (s == null) return "";
            return s.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
