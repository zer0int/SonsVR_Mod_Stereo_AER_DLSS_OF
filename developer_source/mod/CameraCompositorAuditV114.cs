using MelonLoader;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using Valve.VR;

namespace SonsVRMod
{
    /// <summary>
    /// V114 camera/compositor census.
    ///
    /// Purpose: distinguish "loaded in memory" from "actually rendering" and catch stale cameras,
    /// duplicate mod cameras, huge hidden render targets, or old loading/background renderers that
    /// survive into gameplay. This is intentionally sparse: camera render callbacks only increment
    /// counters/timestamps; full Resources.FindObjectsOfTypeAll scans happen on scene/gate/signature
    /// changes or a slow periodic cadence, never every frame.
    /// </summary>
    internal static class CameraCompositorAuditV114
    {
        internal static bool Enabled = false; // V116: retired after V115 exit crash in stale CustomPass wrapper census.

        private sealed class RenderStat
        {
            internal string Name;
            internal long BeginTick;
            internal int BeginFrame = -1;
            internal long RenderCount;
            internal long LastSnapshotRenderCount;
            internal int LastRenderFrame = -1;
            internal float LastRenderTime = -1f;
            internal double LastCpuMs;
            internal double MaxCpuMs;
            internal double TotalCpuMs;
        }

        private static readonly Dictionary<int, RenderStat> _renderStats = new Dictionary<int, RenderStat>();
        private static StreamWriter _writer;
        private static string _path;
        private static bool _initialized;
        private static float _nextScan;
        private static string _lastScene = "";
        private static bool _lastGameplayReady;
        private static string _lastSignature = "";
        private static int _snapshotIndex;
        private static float _startTime;
        private static float _nextHeartbeat;
        private static float _lastRtPipelineScan = -999f;
        private static float _lastCustomPassScan = -999f;

        internal static string LogPath => _path;

        internal static void Initialize()
        {
            if (_initialized || !Enabled) return;
            _initialized = true;
            _startTime = Time.realtimeSinceStartup;
            try
            {
                string dir = Path.Combine(Environment.CurrentDirectory, "UserData");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "SonsVR_V114_CameraCompositorAudit.log");
                _writer = new StreamWriter(_path, false, Encoding.UTF8) { AutoFlush = true };
                WriteLine("# SonsVR V114 CAMERA / COMPOSITOR AUDIT");
                WriteLine("# Loaded cameras are enumerated with Resources.FindObjectsOfTypeAll<Camera>(); render counts come from SRP begin/end callbacks.");
                WriteLine("# A camera can therefore be present-but-idle, or active-and-actually-rendering; the log records both.");
                WriteLine("# lastCpuMs/maxCpuMs are wall time between SRP begin/end callbacks (CPU-side render interval), NOT GPU timing.");
                NoteEvent("AUDIT_INIT", "path=" + _path);
                Snapshot("startup");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[V114 CameraAudit] initialize failed: " + e.Message);
            }
        }

        internal static void Tick()
        {
            if (!Enabled) return;
            if (!_initialized) Initialize();
            if (!_initialized) return;

            string scene = SafeActiveSceneName();
            bool gameplay = StereoRender.AerGameplayReadyForActivation;
            bool stateChanged = scene != _lastScene || gameplay != _lastGameplayReady;

            float now = Time.unscaledTime;
            float elapsed = Time.realtimeSinceStartup - _startTime;
            float cadence = elapsed < 90f ? 2.0f : 10.0f;
            if (!stateChanged && now < _nextScan) return;
            _nextScan = now + cadence;

            string signature = BuildCameraSignature();
            bool cameraSetChanged = signature != _lastSignature;

            if (stateChanged || cameraSetChanged)
            {
                string reason = stateChanged
                    ? $"state_change scene={_lastScene}->{scene} gameplay={_lastGameplayReady}->{gameplay}"
                    : "camera_signature_change";
                _lastScene = scene;
                _lastGameplayReady = gameplay;
                _lastSignature = signature;
                Snapshot(reason);
            }
            else if (elapsed < 90f && now >= _nextHeartbeat)
            {
                // Sparse heartbeat during the loading/startup window even if the camera set is
                // stable, so render-count deltas reveal a camera that is silently burning frames.
                _nextHeartbeat = now + 10.0f;
                Snapshot("startup_heartbeat");
            }
        }

        internal static void OnCameraBegin(Camera camera)
        {
            if (!Enabled || camera == null) return;
            int id = camera.GetInstanceID();
            RenderStat s;
            if (!_renderStats.TryGetValue(id, out s))
            {
                s = new RenderStat { Name = camera.name };
                _renderStats[id] = s;
            }
            s.Name = camera.name;
            s.BeginTick = Stopwatch.GetTimestamp();
            s.BeginFrame = Time.frameCount;
        }

        internal static void OnCameraEnd(Camera camera)
        {
            if (!Enabled || camera == null) return;
            int id = camera.GetInstanceID();
            RenderStat s;
            if (!_renderStats.TryGetValue(id, out s))
            {
                s = new RenderStat { Name = camera.name };
                _renderStats[id] = s;
            }

            double ms = 0.0;
            if (s.BeginTick != 0 && s.BeginFrame == Time.frameCount)
                ms = (Stopwatch.GetTimestamp() - s.BeginTick) * 1000.0 / Stopwatch.Frequency;

            s.Name = camera.name;
            s.RenderCount++;
            s.LastRenderFrame = Time.frameCount;
            s.LastRenderTime = Time.unscaledTime;
            s.LastCpuMs = ms;
            s.TotalCpuMs += ms;
            if (ms > s.MaxCpuMs) s.MaxCpuMs = ms;
            s.BeginTick = 0;
            s.BeginFrame = -1;
        }

        internal static void NoteEvent(string kind, string detail)
        {
            if (!Enabled) return;
            if (!_initialized) Initialize();
            WriteLine($"EVENT t={Time.unscaledTime:F3} frame={Time.frameCount} scene={SafeActiveSceneName()} kind={kind} {detail}");
        }

        internal static void Snapshot(string reason)
        {
            if (!Enabled) return;
            if (!_initialized && reason != "startup") Initialize();
            if (_writer == null) return;

            try
            {
                _snapshotIndex++;
                Camera[] loaded = Resources.FindObjectsOfTypeAll<Camera>();
                List<Camera> cameras = new List<Camera>();
                HashSet<int> seen = new HashSet<int>();
                if (loaded != null)
                {
                    for (int i = 0; i < loaded.Length; ++i)
                    {
                        Camera c = loaded[i];
                        if (c == null) continue;
                        int id = c.GetInstanceID();
                        if (seen.Add(id)) cameras.Add(c);
                    }
                }
                cameras.Sort(CompareCameras);

                int activeEnabled = 0;
                int actuallyRenderedSinceLast = 0;
                int activeNonEye = 0;
                int renderedNonEyeSinceLast = 0;
                for (int i = 0; i < cameras.Count; ++i)
                {
                    Camera c = cameras[i];
                    if (c.enabled && c.gameObject != null && c.gameObject.activeInHierarchy) activeEnabled++;
                    RenderStat s;
                    if (_renderStats.TryGetValue(c.GetInstanceID(), out s) && s.RenderCount > s.LastSnapshotRenderCount)
                        actuallyRenderedSinceLast++;
                    bool eye = StereoRender.Instance != null && (c == StereoRender.Instance.LeftCam || c == StereoRender.Instance.RightCam);
                    bool active = c.enabled && c.gameObject != null && c.gameObject.activeInHierarchy;
                    if (!eye && active) activeNonEye++;
                    RenderStat renderStat;
                    if (!eye && _renderStats.TryGetValue(c.GetInstanceID(), out renderStat) &&
                        renderStat.RenderCount > renderStat.LastSnapshotRenderCount)
                        renderedNonEyeSinceLast++;
                }

                WriteLine("");
                WriteLine($"===== SNAPSHOT {_snapshotIndex} t={Time.unscaledTime:F3} frame={Time.frameCount} scene={SafeActiveSceneName()} gameplayReady={StereoRender.AerGameplayReadyForActivation} reason={reason} =====");
                WriteLine($"SUMMARY loadedCameras={cameras.Count} activeEnabled={activeEnabled} renderedSinceLast={actuallyRenderedSinceLast} activeNonEye={activeNonEye} renderedNonEyeSinceLast={renderedNonEyeSinceLast} Camera.main={DescribeCameraShort(Camera.main)}");

                for (int i = 0; i < cameras.Count; ++i)
                    WriteCamera(cameras[i]);

                WriteKnownOpenVrOverlays();

                // The camera census above is cheap enough for the sparse heartbeat. The global
                // Renderer scan is not: a live level can contain many thousands of renderers, so
                // do that ONLY at semantic transitions (startup/scene/gate/shutdown), never on a
                // periodic heartbeat. RT/pipeline and CustomPass inventories are lighter but still
                // rate-limited. This audit must not become the performance problem it is hunting.
                bool important = reason.IndexOf("state_change", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 reason.IndexOf("startup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 reason.IndexOf("gameplay_gate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 reason.IndexOf("shutdown", StringComparison.OrdinalIgnoreCase) >= 0;

                if (important)
                    WriteLoadingBackingRendererCensus();
                else
                    WriteLine("LOADING_BACKING_CENSUS skipped (transition-only; avoids live-level renderer scan cost)");

                if (important || Time.unscaledTime - _lastRtPipelineScan >= 30.0f)
                {
                    _lastRtPipelineScan = Time.unscaledTime;
                    WriteRenderTextureCensus();
                    WritePipelineAssetSummary();
                }
                else
                {
                    WriteLine("RT_PIPELINE_CENSUS skipped (rate-limited)");
                }

                if (important || Time.unscaledTime - _lastCustomPassScan >= 20.0f)
                {
                    _lastCustomPassScan = Time.unscaledTime;
                    WriteCustomPassVolumeCensus();
                }
                else
                {
                    WriteLine("CUSTOM_PASS_CENSUS skipped (rate-limited)");
                }

                // Mark render counts as consumed by this snapshot after writing every camera row.
                foreach (KeyValuePair<int, RenderStat> kv in _renderStats)
                    kv.Value.LastSnapshotRenderCount = kv.Value.RenderCount;
            }
            catch (Exception e)
            {
                WriteLine("SNAPSHOT_ERROR " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void WriteCamera(Camera c)
        {
            int id = c.GetInstanceID();
            RenderStat s;
            _renderStats.TryGetValue(id, out s);
            long delta = s != null ? s.RenderCount - s.LastSnapshotRenderCount : 0;
            bool active = c.enabled && c.gameObject != null && c.gameObject.activeInHierarchy;

            string scene = "<none>";
            string path = "<no-go>";
            string tag = "";
            int layer = -1;
            Vector3 pos = Vector3.zero;
            if (c.gameObject != null)
            {
                try { scene = c.gameObject.scene.IsValid() ? c.gameObject.scene.name : "<invalid>"; } catch { }
                path = HierarchyPath(c.transform);
                try { tag = c.gameObject.tag; } catch { tag = "<tag-error>"; }
                layer = c.gameObject.layer;
                pos = c.transform.position;
            }

            string target = "SCREEN";
            if (c.targetTexture != null)
            {
                RenderTexture rt = c.targetTexture;
                target = $"{rt.name}#{rt.GetInstanceID()} {rt.width}x{rt.height} fmt={rt.format} depth={rt.depth} aa={rt.antiAliasing} created={rt.IsCreated()}";
            }
            else
            {
                target = $"SCREEN {c.pixelWidth}x{c.pixelHeight}";
            }

            string hd = DescribeHd(c);
            string components = DescribeCameraComponents(c);
            string flags = ClassifyCamera(c, active, delta);
            WriteLine(
                $"CAM id={id} name=\"{c.name}\" path=\"{path}\" scene=\"{scene}\" tag={tag} layer={layer}:{LayerMask.LayerToName(layer)} " +
                $"enabled={c.enabled} activeHierarchy={(c.gameObject != null && c.gameObject.activeInHierarchy)} isActiveEnabled={c.isActiveAndEnabled} " +
                $"type={c.cameraType} depth={c.depth:F2} stereo={c.stereoTargetEye} display={c.targetDisplay} " +
                $"culling=0x{unchecked((uint)c.cullingMask):X8} clear={c.clearFlags} hdr={c.allowHDR} msaa={c.allowMSAA} dyn={c.allowDynamicResolution} " +
                $"near={c.nearClipPlane:F3} far={c.farClipPlane:F1} ortho={c.orthographic} orthoSize={c.orthographicSize:F3} fov={c.fieldOfView:F2} " +
                $"pos=({pos.x:F3},{pos.y:F3},{pos.z:F3}) target=[{target}] " +
                $"rendersTotal={(s != null ? s.RenderCount : 0)} rendersDelta={delta} lastRenderFrame={(s != null ? s.LastRenderFrame : -1)} " +
                $"lastCpuMs={(s != null ? s.LastCpuMs : 0):F3} maxCpuMs={(s != null ? s.MaxCpuMs : 0):F3} totalCpuMs={(s != null ? s.TotalCpuMs : 0):F3} " +
                $"{hd} components=[{components}] flags=[{flags}]");
        }

        private static string DescribeCameraComponents(Camera c)
        {
            try
            {
                if (c == null || c.gameObject == null) return "";
                Component[] components = c.gameObject.GetComponents<Component>();
                if (components == null || components.Length == 0) return "";
                List<string> names = new List<string>();
                for (int i = 0; i < components.Length; ++i)
                {
                    Component component = components[i];
                    if (component == null)
                    {
                        names.Add("<missing-script>");
                        continue;
                    }
                    try { names.Add(component.GetType().FullName ?? component.GetType().Name); }
                    catch { names.Add("<type-error>"); }
                }
                return string.Join(";", names.ToArray());
            }
            catch (Exception e)
            {
                return "ERR:" + e.GetType().Name;
            }
        }

        private static string DescribeHd(Camera c)
        {
            try
            {
                HDAdditionalCameraData hd = c.GetComponent<HDAdditionalCameraData>();
                if (hd == null) return "HDRP[none]";
                return "HDRP[clear=" + SafeReadMember(hd, "clearColorMode") +
                       " bg=" + SafeReadMember(hd, "backgroundColorHDR") +
                       " volMask=" + MaskMemberHex(hd, "volumeLayerMask") +
                       " probeMask=" + MaskMemberHex(hd, "probeLayerMask") +
                       " AA=" + SafeReadMember(hd, "antialiasing") +
                       " DLSS=" + SafeReadMember(hd, "allowDeepLearningSuperSampling") +
                       " dyn=" + SafeReadMember(hd, "allowDynamicResolution") +
                       " history=" + SafeReadMember(hd, "hasPersistentHistory") +
                       " custom=" + SafeReadMember(hd, "customRenderingSettings") + "]";
            }
            catch (Exception e)
            {
                return "HDRP[ERR:" + e.Message + "]";
            }
        }

        private static string SafeReadMember(object o, string name)
        {
            if (o == null) return "null";
            try
            {
                Type t = o.GetType();
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.GetIndexParameters().Length == 0) return FormatValue(p.GetValue(o));
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return FormatValue(f.GetValue(o));
            }
            catch (Exception e) { return "ERR:" + e.GetType().Name; }
            return "<missing>";
        }

        private static string MaskMemberHex(object o, string name)
        {
            string v = SafeReadMember(o, name);
            try
            {
                Type t = o.GetType();
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object raw = p != null ? p.GetValue(o) : t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);
                if (raw is LayerMask) return "0x" + unchecked((uint)((LayerMask)raw).value).ToString("X8", CultureInfo.InvariantCulture);
                if (raw is int) return "0x" + unchecked((uint)(int)raw).ToString("X8", CultureInfo.InvariantCulture);
            }
            catch { }
            return v;
        }

        private static string ClassifyCamera(Camera c, bool active, long delta)
        {
            List<string> f = new List<string>();
            bool eye = StereoRender.Instance != null && (c == StereoRender.Instance.LeftCam || c == StereoRender.Instance.RightCam);
            if (eye) f.Add("VR_EYE");
            if (c == Camera.main) f.Add("CAMERA_MAIN");
            if (c.name != null && c.name.StartsWith("SonsVR_", StringComparison.Ordinal)) f.Add("MOD_CREATED");
            if (c.name == "MainCameraFP" || c.name == "InventoryCamera" || c.name == "MainCameraCutscene" || c.name == "MainCameraCustcene")
                f.Add("KNOWN_GAME_CAMERA");
            string cameraName = c.name ?? "";
            string cameraPath = "";
            try { cameraPath = HierarchyPath(c.transform) ?? ""; } catch { }
            if (cameraName.IndexOf("Binocular", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cameraPath.IndexOf("Binocular", StringComparison.OrdinalIgnoreCase) >= 0)
                f.Add("BINOCULAR_FEATURE_CAMERA");
            if (cameraName.IndexOf("Loading", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cameraPath.IndexOf("Loading", StringComparison.OrdinalIgnoreCase) >= 0)
                f.Add("LOADING_CAMERA_CANDIDATE");
            if (active) f.Add("ACTIVE"); else f.Add("IDLE_OR_DISABLED");
            if (delta > 0) f.Add("RENDERED_THIS_WINDOW"); else f.Add("NO_RENDER_THIS_WINDOW");
            if (!eye && active && delta > 0) f.Add("NON_EYE_RENDER_COST");
            if (!eye && active && c.cullingMask == -1) f.Add("FULL_MASK_NON_EYE");
            if (c.targetTexture != null && (long)c.targetTexture.width * c.targetTexture.height >= 1000000L) f.Add("LARGE_RT");
            return string.Join(";", f.ToArray());
        }

        private static void WriteKnownOpenVrOverlays()
        {
            if (OpenVR.Overlay == null)
            {
                WriteLine("OPENVR overlays=<OpenVR.Overlay null>");
                return;
            }
            string[] keys = new string[]
            {
                "sons_vr.native_hud.v114",
                "sons_vr.native_hud.v113"
            };
            for (int i = 0; i < keys.Length; ++i)
            {
                ulong h = OpenVR.k_ulOverlayHandleInvalid;
                EVROverlayError err;
                try { err = OpenVR.Overlay.FindOverlay(keys[i], ref h); }
                catch (Exception e)
                {
                    WriteLine($"OPENVR key={keys[i]} find=EXCEPTION:{e.Message}");
                    continue;
                }
                string visible = "?";
                if (err == EVROverlayError.None)
                {
                    try { visible = OpenVR.Overlay.IsOverlayVisible(h).ToString(); } catch { }
                }
                WriteLine($"OPENVR key={keys[i]} find={err} handle={h} visible={visible}");
            }
        }

        private static void WriteRenderTextureCensus()
        {
            try
            {
                RenderTexture[] all = Resources.FindObjectsOfTypeAll<RenderTexture>();
                List<RenderTexture> list = new List<RenderTexture>();
                HashSet<int> seen = new HashSet<int>();
                if (all != null)
                {
                    for (int i = 0; i < all.Length; ++i)
                    {
                        RenderTexture rt = all[i];
                        if (rt == null) continue;
                        if (seen.Add(rt.GetInstanceID())) list.Add(rt);
                    }
                }
                list.Sort((a, b) => ((long)b.width * b.height).CompareTo((long)a.width * a.height));
                WriteLine($"RENDERTEXTURES loaded={list.Count} topByArea={Math.Min(30, list.Count)}");
                int n = Math.Min(30, list.Count);
                for (int i = 0; i < n; ++i)
                {
                    RenderTexture rt = list[i];
                    WriteLine($"  RT id={rt.GetInstanceID()} name=\"{rt.name}\" {rt.width}x{rt.height} fmt={rt.format} depth={rt.depth} aa={rt.antiAliasing} created={rt.IsCreated()} hideFlags={rt.hideFlags}");
                }
            }
            catch (Exception e)
            {
                WriteLine("RENDERTEXTURE_CENSUS_ERROR " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void WriteLoadingBackingRendererCensus()
        {
            try
            {
                Renderer[] all = Resources.FindObjectsOfTypeAll<Renderer>();
                int hits = 0;
                if (all != null)
                {
                    for (int i = 0; i < all.Length; ++i)
                    {
                        Renderer r = all[i];
                        if (r == null || r.gameObject == null) continue;
                        bool match = false;
                        string mats = "";
                        try
                        {
                            Material[] sm = r.sharedMaterials;
                            if (sm != null)
                            {
                                for (int j = 0; j < sm.Length; ++j)
                                {
                                    Material m = sm[j];
                                    if (m == null) continue;
                                    if (mats.Length > 0) mats += ";";
                                    mats += m.name;
                                    if (string.Equals(m.name, "LoadingCameraBacking", StringComparison.OrdinalIgnoreCase) ||
                                        m.name.IndexOf("LoadingCameraBacking", StringComparison.OrdinalIgnoreCase) >= 0)
                                        match = true;
                                }
                            }
                        }
                        catch { }
                        if (!match) continue;
                        hits++;
                        string scene = "<invalid>";
                        try { scene = r.gameObject.scene.IsValid() ? r.gameObject.scene.name : "<invalid>"; } catch { }
                        Bounds b = r.bounds;
                        WriteLine($"LOADING_BACKING renderer={r.GetType().Name}#{r.GetInstanceID()} path=\"{HierarchyPath(r.transform)}\" scene={scene} layer={r.gameObject.layer}:{LayerMask.LayerToName(r.gameObject.layer)} enabled={r.enabled} active={r.gameObject.activeInHierarchy} mats=[{mats}] boundsCenter={b.center} boundsSize={b.size}");
                    }
                }
                WriteLine($"LOADING_BACKING_SUMMARY hits={hits}");
            }
            catch (Exception e)
            {
                WriteLine("LOADING_BACKING_CENSUS_ERROR " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void WriteCustomPassVolumeCensus()
        {
            // V116: permanently retired. V115 demonstrated that CustomPass wrappers may become
            // invalid during scene teardown and even a harmless-looking pass.name getter can
            // cross into Il2CppStringToManaged with freed native memory, causing fatal CLR
            // corruption that no managed try/catch can recover from. Never dereference this
            // object graph from a long-lived audit.
            WriteLine("CUSTOM_PASS_CENSUS disabled in V116 (unsafe across IL2CPP scene teardown)");
        }

        private static void WritePipelineAssetSummary()
        {
            try
            {
                Type gs = FindType("UnityEngine.Rendering.GraphicsSettings");
                object current = ReadStatic(gs, "currentRenderPipeline");
                object def = ReadStatic(gs, "defaultRenderPipeline");
                Type qs = FindType("UnityEngine.QualitySettings");
                object quality = ReadStatic(qs, "renderPipeline");
                WritePipelineObject("PIPELINE_CURRENT", current);
                WritePipelineObject("PIPELINE_DEFAULT", def);
                WritePipelineObject("PIPELINE_QUALITY", quality);
            }
            catch (Exception e)
            {
                WriteLine("PIPELINE_CENSUS_ERROR " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void WritePipelineObject(string label, object o)
        {
            if (o == null)
            {
                WriteLine(label + " null");
                return;
            }
            Type t = o.GetType();
            WriteLine($"{label} type={t.FullName} value={FormatValue(o)}");
            string[] names = new string[]
            {
                "colorBufferFormat", "supportedLitShaderMode", "currentPlatformRenderPipelineSettings",
                "renderPipelineSettings", "supportRayTracing", "supportVolumetrics", "supportSSR",
                "supportTransparentDepthPrepass", "supportTransparentDepthPostpass"
            };
            for (int i = 0; i < names.Length; ++i)
            {
                string v = SafeReadMember(o, names[i]);
                if (v != "<missing>") WriteLine($"  {names[i]}={v}");
            }

            // The useful color-buffer field is often nested in platform/render-pipeline settings.
            // One reflective level keeps this diagnostic targeted rather than dumping an enormous
            // HDRP object graph.
            object settings = ReadMemberRaw(o, "currentPlatformRenderPipelineSettings") ?? ReadMemberRaw(o, "renderPipelineSettings");
            if (settings != null)
            {
                WriteLine("  nestedSettingsType=" + settings.GetType().FullName);
                string[] nested = new string[] { "colorBufferFormat", "supportVolumetrics", "supportMotionVectors", "supportSSR", "supportSSAO" };
                for (int i = 0; i < nested.Length; ++i)
                {
                    string v = SafeReadMember(settings, nested[i]);
                    if (v != "<missing>") WriteLine($"    {nested[i]}={v}");
                }
            }
        }

        private static object ReadStatic(Type t, string name)
        {
            if (t == null) return null;
            try
            {
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(null);
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null) return f.GetValue(null);
            }
            catch { }
            return null;
        }

        private static object ReadMemberRaw(object o, string name)
        {
            if (o == null) return null;
            try
            {
                Type t = o.GetType();
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o);
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f.GetValue(o);
            }
            catch { }
            return null;
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType(fullName);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static string BuildCameraSignature()
        {
            try
            {
                Camera[] all = Resources.FindObjectsOfTypeAll<Camera>();
                List<string> rows = new List<string>();
                if (all != null)
                {
                    HashSet<int> seen = new HashSet<int>();
                    for (int i = 0; i < all.Length; ++i)
                    {
                        Camera c = all[i];
                        if (c == null) continue;
                        int id = c.GetInstanceID();
                        if (!seen.Add(id)) continue;
                        int targetId = c.targetTexture != null ? c.targetTexture.GetInstanceID() : 0;
                        bool active = c.gameObject != null && c.gameObject.activeInHierarchy;
                        rows.Add($"{id}:{c.name}:{c.enabled}:{active}:{c.cullingMask}:{c.depth:F2}:{targetId}:{c.cameraType}");
                    }
                }
                rows.Sort(StringComparer.Ordinal);
                return string.Join("|", rows.ToArray()) + "|main=" + (Camera.main != null ? Camera.main.GetInstanceID().ToString() : "0");
            }
            catch (Exception e)
            {
                return "ERR:" + e.Message;
            }
        }

        private static int CompareCameras(Camera a, Camera b)
        {
            bool aa = a != null && a.enabled && a.gameObject != null && a.gameObject.activeInHierarchy;
            bool ba = b != null && b.enabled && b.gameObject != null && b.gameObject.activeInHierarchy;
            if (aa != ba) return aa ? -1 : 1;
            int n = string.Compare(a != null ? a.name : "", b != null ? b.name : "", StringComparison.Ordinal);
            if (n != 0) return n;
            return (a != null ? a.GetInstanceID() : 0).CompareTo(b != null ? b.GetInstanceID() : 0);
        }

        private static string DescribeCameraShort(Camera c)
        {
            if (c == null) return "null";
            return $"{c.name}#{c.GetInstanceID()} path={HierarchyPath(c.transform)}";
        }

        private static string HierarchyPath(Transform t)
        {
            if (t == null) return "<null>";
            string p = t.name;
            Transform cur = t.parent;
            int guard = 0;
            while (cur != null && guard++ < 64)
            {
                p = cur.name + "/" + p;
                cur = cur.parent;
            }
            return p;
        }

        private static string SafeActiveSceneName()
        {
            try { return SceneManager.GetActiveScene().name ?? ""; }
            catch { return "<scene-error>"; }
        }

        private static string FormatValue(object o)
        {
            if (o == null) return "null";
            try { return o.ToString(); } catch { return "<ToString-error>"; }
        }

        private static void WriteLine(string line)
        {
            try
            {
                if (_writer != null) _writer.WriteLine(line);
            }
            catch { }
        }

        internal static void Shutdown()
        {
            if (!_initialized) return;
            try { Snapshot("shutdown"); } catch { }
            try { _writer?.Flush(); } catch { }
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _initialized = false;
        }
    }
}
