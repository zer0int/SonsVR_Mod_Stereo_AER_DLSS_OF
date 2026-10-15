using MelonLoader;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using Valve.VR;
using Mathf = UnityEngine.Mathf;

namespace SonsVRMod
{
    /// <summary>
    /// V106: low-intrusion performance forensics for the old startup/cooking 4-FPS pathology.
    ///
    /// The key V105 observation was that pathological frames keep VR pre-submit GPU time near
    /// normal (~17-22 ms) while application post-submit GPU time explodes to 100-230+ ms. V106
    /// therefore instruments the boundary we actually care about instead of adding more eye
    /// rendering probes:
    ///
    ///  - OpenVR pre/post/total GPU timings and presentation cadence
    ///  - actual CPU duration blocked in WaitGetPoses()
    ///  - every SRP camera render, tagged PRE_SUBMIT / POST_SUBMIT, with CPU begin->end duration
    ///  - exact second-eye Submit boundary and desktop mirror blit CPU duration
    ///  - Unity frame-cap knobs (targetFrameRate, vSync, OnDemandRendering interval) + HMD Hz
    ///  - sparse camera/HDRP-volume censuses at state transitions and major spikes
    ///  - cooking-gaze scene-scan timings, because this fork currently performs global
    ///    FindObjectsOfType scans while the grab-bag/cooking menu is active
    ///
    /// Camera events are kept in a small ring buffer continuously. They are dumped on a spike
    /// and written live only during short burst windows / cooking, so the logger does not itself
    /// become the performance problem we are trying to measure.
    /// </summary>
    internal static class PerformanceForensicsV106
    {
        internal static bool Enabled = false; // V107: uber-logger disabled by default after it found the cooking/global-scan culprit. Keep code dormant for future targeted forensics.
        internal static bool CueEnabled = false; // V107: no measurement protocol in normal validation play.

        private sealed class CameraStart
        {
            public long Tick;
            public string Phase;
            public int Frame;
        }

        private static readonly Dictionary<int, CameraStart> _cameraStarts = new Dictionary<int, CameraStart>();
        private static readonly Queue<string> _cameraRing = new Queue<string>();
        private const int CameraRingMax = 160;

        private static StreamWriter _writer;
        private static StreamWriter _snapshotWriter;
        private static string _timelinePath;
        private static string _snapshotPath;
        private static bool _initialized;
        private static readonly object _ioLock = new object();

        private static int _submitBeginFrame = -1;
        private static int _secondSubmitFrame = -1;
        private static long _submitBeginTick;
        private static long _secondSubmitTick;
        private static long _mirrorBeginTick;
        private static int _mirrorFrame = -1;
        private static long _guiMirrorBeginTick;
        private static int _guiMirrorFrame = -1;
        private static float _lastWaitGetPosesCpuMs;
        private static int _lastWaitGetPosesFrame = -1;

        private static string _currentContext = "STARTUP";
        private static string _lastContext = "";
        private static string _lastScene = "";
        private static float _burstUntil;
        private static float _nextBaselineRow;
        private static float _nextCameraSummary;
        private static float _nextSpikeSnapshotAllowed;
        private static bool _postGpuWasPathological;
        private static string _pendingSnapshotReason;
        private static float _pendingSnapshotAt;

        private static float _hmdHz = -1f;
        private static float _nextHmdHzRefresh;
        private static int _onDemandInterval = -1;
        private static float _nextCapRefresh;

        // Most recent compositor timing, also useful to the HMD cue state machine.
        private static float _lastClientMs;
        private static float _lastTotalGpuMs;
        private static float _lastPreGpuMs;
        private static float _lastPostGpuMs;
        private static float _lastSubmitFrameMs;
        private static float _lastWaitGetPosesCalledMs;
        private static uint _lastPresents;
        private static uint _lastMisPresented;
        private static uint _lastDropped;
        private static uint _lastReprojFlags;
        private static uint _lastCompositorFrameIndex;

        // Cooking CPU instrumentation, sampled into FRAME rows and emitted as dedicated rows at
        // most 4 Hz so it remains cheap even when the game has collapsed to ~4 FPS.
        private static float _cookTotalMs;
        private static float _cookItemFindMs;
        private static float _cookItemScanMs;
        private static float _cookCogFindMs;
        private static float _cookCogScanMs;
        private static int _cookItems;
        private static int _cookItemColliders;
        private static int _cookCogs;
        private static int _cookCogColliders;
        private static int _cookCogRenderers;
        private static float _nextCookingDetailRow;

        // Reusable grayon test protocol. It never gates logging; it just tells the HMD-wearing
        // human when the useful state transition has occurred.
        private static int _cueState;
        private static float _gameplayStableSince = -1f;
        private static float _cookingEnteredAt = -1f;
        private static float _cookingExitedAt = -1f;

        internal static string TimelinePath => _timelinePath;
        internal static string SnapshotPath => _snapshotPath;

        internal static void Initialize()
        {
            if (_initialized || !Enabled) return;
            _initialized = true;
            try
            {
                string dir = ResolveModDirectory();
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
                _timelinePath = MakeUniquePath(dir, "SonsVR_V106_Timeline_" + stamp, ".csv");
                _snapshotPath = MakeUniquePath(dir, "SonsVR_V106_Snapshots_" + stamp, ".txt");

                _writer = new StreamWriter(new FileStream(_timelinePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false), 128 * 1024);
                _snapshotWriter = new StreamWriter(new FileStream(_snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false), 64 * 1024);

                _writer.WriteLine(
                    "kind,wall_time,unity_time,frame,scene,context,unity_dt_ms," +
                    "steamvr_frame,client_ms,total_gpu_ms,pre_submit_gpu_ms,post_submit_gpu_ms," +
                    "waitgetposes_called_ms,submit_frame_ms,presents,mis_presented,dropped,reproj_flags," +
                    "waitgetposes_cpu_ms,target_fps,vsync,on_demand_interval,hmd_hz," +
                    "phase,camera,camera_id,camera_type,enabled,active,culling_mask,target,target_wh,camera_cpu_ms," +
                    "cook_total_ms,cook_item_find_ms,cook_item_scan_ms,cook_items,cook_item_colliders," +
                    "cook_cog_find_ms,cook_cog_scan_ms,cook_cogs,cook_cog_colliders,cook_cog_renderers,note");
                _writer.Flush();
                _snapshotWriter.WriteLine("SonsVR V106 PERFORMANCE FORENSICS");
                _snapshotWriter.WriteLine("Started: " + DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
                _snapshotWriter.WriteLine();
                _snapshotWriter.Flush();
                MelonLogger.Warning($"[V106 PerfForensics] timeline='{_timelinePath}' snapshots='{_snapshotPath}'.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[V106 PerfForensics] initialize failed: " + e.Message);
                _writer = null;
                _snapshotWriter = null;
            }
        }

        internal static void Shutdown()
        {
            try
            {
                lock (_ioLock)
                {
                    _writer?.Flush();
                    _snapshotWriter?.Flush();
                    _writer?.Dispose();
                    _snapshotWriter?.Dispose();
                }
            }
            catch { }
            _writer = null;
            _snapshotWriter = null;
        }

        internal static void Tick(StereoRender stereo, VRPlayer player)
        {
            if (!Enabled) return;
            if (!_initialized) Initialize();

            string scene = "";
            try { scene = SceneManager.GetActiveScene().name ?? ""; } catch { }
            string context = player != null ? player.GetPerformanceContextV105() : "NO_VRPLAYER";
            _currentContext = context ?? "UNKNOWN";

            RefreshCapState();
            bool haveTiming = ReadCompositorTiming();

            float now = Time.unscaledTime;
            float dtMs = Time.unscaledDeltaTime * 1000f;
            bool cooking = string.Equals(_currentContext, "GRABBAG_COOKING", StringComparison.Ordinal);
            bool pathologicalPost = haveTiming && _lastPostGpuMs >= 40f;
            bool severeFrame = dtMs >= 80f || (haveTiming && (_lastTotalGpuMs >= 80f || _lastPostGpuMs >= 50f));

            if (scene != _lastScene)
            {
                string old = _lastScene;
                _lastScene = scene;
                WriteEvent("SCENE_CHANGE", $"'{old}' -> '{scene}'");
                ScheduleSnapshot("SCENE_CHANGE " + old + " -> " + scene, 0.20f);
                ActivateBurst(2.5f, "scene_change");
            }

            if (_currentContext != _lastContext)
            {
                string old = _lastContext;
                _lastContext = _currentContext;
                WriteEvent("CONTEXT_CHANGE", $"'{old}' -> '{_currentContext}'");
                ScheduleSnapshot("CONTEXT_CHANGE " + old + " -> " + _currentContext, 0.15f);
                ActivateBurst(cooking ? 12f : 4f, "context_change");
            }

            if (pathologicalPost && !_postGpuWasPathological)
            {
                WriteEvent("POST_GPU_PATHOLOGY_BEGIN", $"post={_lastPostGpuMs:F2}ms pre={_lastPreGpuMs:F2}ms total={_lastTotalGpuMs:F2}ms");
                DumpCameraRing("POST_GPU_PATHOLOGY_BEGIN");
                ActivateBurst(5f, "post_gpu_begin");
                ScheduleSnapshot("POST_GPU_PATHOLOGY_BEGIN", 0.10f);
            }
            else if (!pathologicalPost && _postGpuWasPathological && haveTiming && _lastPostGpuMs < 15f)
            {
                WriteEvent("POST_GPU_RECOVERED", $"post={_lastPostGpuMs:F2}ms pre={_lastPreGpuMs:F2}ms total={_lastTotalGpuMs:F2}ms");
                ActivateBurst(3f, "post_gpu_recovered");
                ScheduleSnapshot("POST_GPU_RECOVERED", 0.15f);
            }
            _postGpuWasPathological = pathologicalPost;

            if (severeFrame)
            {
                ActivateBurst(2.5f, "severe_frame");
                if (now >= _nextSpikeSnapshotAllowed)
                {
                    _nextSpikeSnapshotAllowed = now + 5f;
                    ScheduleSnapshot($"SEVERE_FRAME dt={dtMs:F1} totalGpu={_lastTotalGpuMs:F1} pre={_lastPreGpuMs:F1} post={_lastPostGpuMs:F1}", 0.10f);
                }
            }

            bool burst = now < _burstUntil;
            if (burst || cooking || severeFrame || now >= _nextBaselineRow)
            {
                if (!burst && !cooking) _nextBaselineRow = now + 0.50f;
                WriteFrameRow(severeFrame ? "FRAME_SPIKE" : cooking ? "FRAME_COOKING" : burst ? "FRAME_BURST" : "FRAME");
            }

            if (cooking && now >= _nextCookingDetailRow)
            {
                _nextCookingDetailRow = now + 0.25f;
                WriteCookingRow();
            }

            if (now >= _nextCameraSummary)
            {
                _nextCameraSummary = now + (burst || cooking ? 0.50f : 2.0f);
                WriteCompactCameraSummary(stereo);
            }

            if (!string.IsNullOrEmpty(_pendingSnapshotReason) && now >= _pendingSnapshotAt)
            {
                string reason = _pendingSnapshotReason;
                _pendingSnapshotReason = null;
                DumpSnapshot(reason, stereo);
            }

            UpdateCueProtocol(_currentContext, now);

            // Keep crash-relevant forensic data on disk much more aggressively than ordinary
            // lightweight perf logging, but still not every healthy frame.
            if (severeFrame || cooking || pathologicalPost)
                FlushBestEffort();
        }

        internal static void NotifyExternalEvent(string label)
        {
            if (!Enabled) return;
            WriteEvent("EXTERNAL_EVENT", label ?? "");
            ActivateBurst(4f, "external:" + (label ?? ""));
            ScheduleSnapshot("EXTERNAL_EVENT " + (label ?? ""), 0.15f);
            FlushBestEffort();
        }

        internal static void RecordWaitGetPoses(float cpuMs)
        {
            _lastWaitGetPosesCpuMs = cpuMs;
            _lastWaitGetPosesFrame = Time.frameCount;
        }

        internal static void MarkSubmitBegin()
        {
            if (!Enabled) return;
            _submitBeginFrame = Time.frameCount;
            _submitBeginTick = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        internal static void MarkSecondSubmitCompleted(EVRCompositorError left, EVRCompositorError right)
        {
            if (!Enabled) return;
            _secondSubmitFrame = Time.frameCount;
            _secondSubmitTick = System.Diagnostics.Stopwatch.GetTimestamp();
            double cpuMs = ElapsedMs(_submitBeginTick, _secondSubmitTick);
            WriteEvent("SUBMIT_PAIR_END", $"cpu={cpuMs:F3}ms L={left} R={right}");
        }

        internal static void MarkMirrorBegin()
        {
            if (!Enabled) return;
            _mirrorBeginTick = System.Diagnostics.Stopwatch.GetTimestamp();
            _mirrorFrame = Time.frameCount;
        }

        internal static void MarkMirrorEnd()
        {
            if (!Enabled || _mirrorFrame != Time.frameCount || _mirrorBeginTick == 0) return;
            double ms = ElapsedMs(_mirrorBeginTick, System.Diagnostics.Stopwatch.GetTimestamp());
            if (ms >= 0.35 || Time.unscaledTime < _burstUntil || _currentContext == "GRABBAG_COOKING")
                WriteEvent("MIRROR_BLIT_CPU", $"cpu={ms:F3}ms");
            _mirrorBeginTick = 0;
        }

        internal static void MarkGuiMirrorBegin()
        {
            if (!Enabled) return;
            _guiMirrorBeginTick = System.Diagnostics.Stopwatch.GetTimestamp();
            _guiMirrorFrame = Time.frameCount;
        }

        internal static void MarkGuiMirrorEnd()
        {
            if (!Enabled || _guiMirrorFrame != Time.frameCount || _guiMirrorBeginTick == 0) return;
            double ms = ElapsedMs(_guiMirrorBeginTick, System.Diagnostics.Stopwatch.GetTimestamp());
            if (ms >= 0.35 || Time.unscaledTime < _burstUntil || _currentContext == "GRABBAG_COOKING")
                WriteEvent("IMGUI_MIRROR_CPU", $"cpu={ms:F3}ms");
            _guiMirrorBeginTick = 0;
        }

        internal static void OnCameraBegin(Camera camera)
        {
            if (!Enabled || camera == null) return;
            int id = camera.GetInstanceID();
            _cameraStarts[id] = new CameraStart
            {
                Tick = System.Diagnostics.Stopwatch.GetTimestamp(),
                Phase = PhaseNow(),
                Frame = Time.frameCount
            };
        }

        internal static void OnCameraEnd(Camera camera)
        {
            if (!Enabled || camera == null) return;
            int id = camera.GetInstanceID();
            CameraStart start;
            long nowTick = System.Diagnostics.Stopwatch.GetTimestamp();
            double cpuMs = 0.0;
            string phase = PhaseNow();
            if (_cameraStarts.TryGetValue(id, out start))
            {
                cpuMs = ElapsedMs(start.Tick, nowTick);
                phase = start.Phase;
                _cameraStarts.Remove(id);
            }

            bool isEye = StereoRender.Instance != null &&
                         (camera == StereoRender.Instance.LeftCam || camera == StereoRender.Instance.RightCam);
            string row = BuildCameraRow(camera, phase, (float)cpuMs, isEye ? "eye" : "non_eye");
            EnqueueCameraRing(row);

            // Non-eye cameras are exactly what V106 is hunting, so log them continuously. Eye
            // cameras are verbose and already well-characterized; only emit them during bursts.
            if (!isEye || Time.unscaledTime < _burstUntil || _currentContext == "GRABBAG_COOKING")
                WriteRawRow(row);
        }

        internal static void RecordCookingGaze(
            float totalMs,
            float itemFindMs,
            float itemScanMs,
            int items,
            int itemColliders,
            float cogFindMs,
            float cogScanMs,
            int cogs,
            int cogColliders,
            int cogRenderers)
        {
            if (!Enabled) return;
            _cookTotalMs = totalMs;
            _cookItemFindMs = itemFindMs;
            _cookItemScanMs = itemScanMs;
            _cookItems = items;
            _cookItemColliders = itemColliders;
            _cookCogFindMs = cogFindMs;
            _cookCogScanMs = cogScanMs;
            _cookCogs = cogs;
            _cookCogColliders = cogColliders;
            _cookCogRenderers = cogRenderers;
        }

        private static bool ReadCompositorTiming()
        {
            try
            {
                if (OpenVR.Compositor == null) return false;
                Compositor_FrameTiming t = new Compositor_FrameTiming();
                t.m_nSize = (uint)Marshal.SizeOf(typeof(Compositor_FrameTiming));
                if (!OpenVR.Compositor.GetFrameTiming(ref t, 0)) return false;
                _lastCompositorFrameIndex = t.m_nFrameIndex;
                _lastPresents = t.m_nNumFramePresents;
                _lastMisPresented = t.m_nNumMisPresented;
                _lastDropped = t.m_nNumDroppedFrames;
                _lastReprojFlags = t.m_nReprojectionFlags;
                _lastClientMs = t.m_flClientFrameIntervalMs;
                _lastTotalGpuMs = t.m_flTotalRenderGpuMs;
                _lastPreGpuMs = t.m_flPreSubmitGpuMs;
                _lastPostGpuMs = t.m_flPostSubmitGpuMs;
                _lastWaitGetPosesCalledMs = t.m_flWaitGetPosesCalledMs;
                _lastSubmitFrameMs = t.m_flSubmitFrameMs;
                return true;
            }
            catch { return false; }
        }

        private static void RefreshCapState()
        {
            float now = Time.unscaledTime;
            if (now < _nextCapRefresh) return;
            _nextCapRefresh = now + 1f;

            _onDemandInterval = -1;
            try
            {
                Type t = Type.GetType("UnityEngine.Rendering.OnDemandRendering, UnityEngine.CoreModule");
                PropertyInfo p = t?.GetProperty("renderFrameInterval", BindingFlags.Public | BindingFlags.Static);
                if (p != null) _onDemandInterval = Convert.ToInt32(p.GetValue(null));
            }
            catch { }

            if (now >= _nextHmdHzRefresh)
            {
                _nextHmdHzRefresh = now + 5f;
                _hmdHz = -1f;
                try
                {
                    object steamVr = SteamVR.instance;
                    PropertyInfo p = steamVr?.GetType().GetProperty("hmd_DisplayFrequency", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null) _hmdHz = Convert.ToSingle(p.GetValue(steamVr), CultureInfo.InvariantCulture);
                }
                catch { }
            }
        }

        private static void WriteFrameRow(string kind)
        {
            string row = BaseColumns(kind) +
                         ",,,,,,,,,,," +
                         Float(_cookTotalMs) + "," + Float(_cookItemFindMs) + "," + Float(_cookItemScanMs) + "," + _cookItems + "," + _cookItemColliders + "," +
                         Float(_cookCogFindMs) + "," + Float(_cookCogScanMs) + "," + _cookCogs + "," + _cookCogColliders + "," + _cookCogRenderers + ",";
            WriteRawRow(row);
        }

        private static void WriteCookingRow()
        {
            string note = $"global scans + collider/renderer traversal; total={_cookTotalMs:F3}ms";
            string row = BaseColumns("COOK_CPU") +
                         ",,,,,,,,,,," +
                         Float(_cookTotalMs) + "," + Float(_cookItemFindMs) + "," + Float(_cookItemScanMs) + "," + _cookItems + "," + _cookItemColliders + "," +
                         Float(_cookCogFindMs) + "," + Float(_cookCogScanMs) + "," + _cookCogs + "," + _cookCogColliders + "," + _cookCogRenderers + "," + Csv(note);
            WriteRawRow(row);
        }

        private static string BuildCameraRow(Camera c, string phase, float cpuMs, string note)
        {
            string target = "SCREEN";
            string wh = "";
            try
            {
                if (c.targetTexture != null)
                {
                    target = c.targetTexture.name ?? "RT";
                    wh = c.targetTexture.width + "x" + c.targetTexture.height;
                }
                else
                {
                    wh = c.pixelWidth + "x" + c.pixelHeight;
                }
            }
            catch { }

            string culling = "0x" + unchecked((uint)c.cullingMask).ToString("X8", CultureInfo.InvariantCulture);
            return BaseColumns("CAMERA") + "," +
                   Csv(phase) + "," + Csv(c.name) + "," + c.GetInstanceID().ToString(CultureInfo.InvariantCulture) + "," + Csv(c.cameraType.ToString()) + "," +
                   (c.enabled ? "1" : "0") + "," + (c.gameObject != null && c.gameObject.activeInHierarchy ? "1" : "0") + "," + culling + "," +
                   Csv(target) + "," + Csv(wh) + "," + Float(cpuMs) +
                   ",,,,,,,,,,," + Csv(note);
        }

        private static string BaseColumns(string kind)
        {
            string scene = "";
            try { scene = SceneManager.GetActiveScene().name ?? ""; } catch { }
            float waitCpu = _lastWaitGetPosesFrame == Time.frameCount ? _lastWaitGetPosesCpuMs : _lastWaitGetPosesCpuMs;
            return string.Join(",", new string[]
            {
                Csv(kind),
                Csv(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                Float(Time.unscaledTime),
                Time.frameCount.ToString(CultureInfo.InvariantCulture),
                Csv(scene),
                Csv(_currentContext),
                Float(Time.unscaledDeltaTime * 1000f),
                _lastCompositorFrameIndex.ToString(CultureInfo.InvariantCulture),
                Float(_lastClientMs), Float(_lastTotalGpuMs), Float(_lastPreGpuMs), Float(_lastPostGpuMs),
                Float(_lastWaitGetPosesCalledMs), Float(_lastSubmitFrameMs),
                _lastPresents.ToString(CultureInfo.InvariantCulture), _lastMisPresented.ToString(CultureInfo.InvariantCulture),
                _lastDropped.ToString(CultureInfo.InvariantCulture), "0x" + _lastReprojFlags.ToString("X", CultureInfo.InvariantCulture),
                Float(waitCpu),
                Application.targetFrameRate.ToString(CultureInfo.InvariantCulture),
                QualitySettings.vSyncCount.ToString(CultureInfo.InvariantCulture),
                _onDemandInterval.ToString(CultureInfo.InvariantCulture),
                Float(_hmdHz)
            });
        }

        private static void WriteCompactCameraSummary(StereoRender stereo)
        {
            try
            {
                Camera[] cameras = Camera.allCameras;
                StringBuilder b = new StringBuilder();
                b.Append("activeCameras=").Append(cameras != null ? cameras.Length : 0);
                if (cameras != null)
                {
                    foreach (Camera c in cameras)
                    {
                        if (c == null) continue;
                        b.Append(" | ").Append(c.name)
                         .Append("#").Append(c.GetInstanceID())
                         .Append(" type=").Append(c.cameraType)
                         .Append(" en=").Append(c.enabled ? 1 : 0)
                         .Append(" mask=0x").Append(unchecked((uint)c.cullingMask).ToString("X8"));
                        if (c.targetTexture != null)
                            b.Append(" rt=").Append(c.targetTexture.name).Append("@").Append(c.targetTexture.width).Append("x").Append(c.targetTexture.height);
                        else
                            b.Append(" rt=SCREEN");
                    }
                }
                WriteEvent("CAMERA_SUMMARY", b.ToString());
            }
            catch (Exception e) { WriteEvent("CAMERA_SUMMARY_FAIL", e.Message); }
        }

        private static void DumpSnapshot(string reason, StereoRender stereo)
        {
            if (_snapshotWriter == null) return;
            try
            {
                StringBuilder b = new StringBuilder(8192);
                b.AppendLine("================================================================================");
                b.Append("SNAPSHOT ").Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
                 .Append(" frame=").Append(Time.frameCount).Append(" reason=").AppendLine(reason);
                b.Append("scene=").Append(SceneManager.GetActiveScene().name)
                 .Append(" context=").Append(_currentContext)
                 .Append(" dt=").Append((Time.unscaledDeltaTime * 1000f).ToString("F2", CultureInfo.InvariantCulture)).Append("ms")
                 .Append(" compositor(total/pre/post)=")
                 .Append(_lastTotalGpuMs.ToString("F2", CultureInfo.InvariantCulture)).Append("/")
                 .Append(_lastPreGpuMs.ToString("F2", CultureInfo.InvariantCulture)).Append("/")
                 .Append(_lastPostGpuMs.ToString("F2", CultureInfo.InvariantCulture)).AppendLine(" ms");
                b.Append("caps targetFrameRate=").Append(Application.targetFrameRate)
                 .Append(" vSyncCount=").Append(QualitySettings.vSyncCount)
                 .Append(" onDemandInterval=").Append(_onDemandInterval)
                 .Append(" hmdHz=").Append(_hmdHz.ToString("F2", CultureInfo.InvariantCulture)).AppendLine();
                b.AppendLine();

                Camera[] cams = Camera.allCameras;
                b.Append("CAMERAS count=").AppendLine((cams != null ? cams.Length : 0).ToString(CultureInfo.InvariantCulture));
                if (cams != null)
                {
                    foreach (Camera c in cams)
                    {
                        if (c == null) continue;
                        b.Append("  ").Append(c.name).Append("#").Append(c.GetInstanceID())
                         .Append(" type=").Append(c.cameraType)
                         .Append(" enabled=").Append(c.enabled)
                         .Append(" active=").Append(c.gameObject != null && c.gameObject.activeInHierarchy)
                         .Append(" depth=").Append(c.depth.ToString("F2", CultureInfo.InvariantCulture))
                         .Append(" culling=0x").Append(unchecked((uint)c.cullingMask).ToString("X8"))
                         .Append(" clear=").Append(c.clearFlags)
                         .Append(" allowDyn=").Append(c.allowDynamicResolution)
                         .Append(" targetDisplay=").Append(c.targetDisplay);
                        if (c.targetTexture != null)
                            b.Append(" target=").Append(c.targetTexture.name).Append("@").Append(c.targetTexture.width).Append("x").Append(c.targetTexture.height);
                        else
                            b.Append(" target=SCREEN pixel=").Append(c.pixelWidth).Append("x").Append(c.pixelHeight);

                        try
                        {
                            HDAdditionalCameraData hd = c.GetComponent<HDAdditionalCameraData>();
                            if (hd != null)
                            {
                                b.Append(" HDRP[AA=").Append(hd.antialiasing)
                                 .Append(" DLSS=").Append(hd.allowDeepLearningSuperSampling)
                                 .Append(" dyn=").Append(hd.allowDynamicResolution)
                                 .Append(" persistent=").Append(hd.hasPersistentHistory).Append("]");
                            }
                            else b.Append(" HDRP[none]");
                        }
                        catch (Exception e) { b.Append(" HDRP[ERR:").Append(e.Message).Append("]"); }
                        b.AppendLine();
                    }
                }

                b.AppendLine();
                try
                {
                    Volume[] volumes = UnityEngine.Object.FindObjectsOfType<Volume>();
                    b.Append("VOLUMES count=").AppendLine((volumes != null ? volumes.Length : 0).ToString(CultureInfo.InvariantCulture));
                    if (volumes != null)
                    {
                        foreach (Volume v in volumes)
                        {
                            if (v == null) continue;
                            b.Append("  ").Append(v.name).Append("#").Append(v.GetInstanceID())
                             .Append(" enabled=").Append(v.enabled)
                             .Append(" active=").Append(v.gameObject != null && v.gameObject.activeInHierarchy)
                             .Append(" global=").Append(v.isGlobal)
                             .Append(" priority=").Append(v.priority.ToString("F2", CultureInfo.InvariantCulture))
                             .Append(" weight=").Append(v.weight.ToString("F3", CultureInfo.InvariantCulture))
                             .Append(" layer=").Append(v.gameObject != null ? v.gameObject.layer : -1);
                            VolumeProfile profile = v.sharedProfile;
                            if (profile == null) profile = v.profile;
                            if (profile != null && profile.components != null)
                            {
                                b.Append(" profile=").Append(profile.name).Append(" components=[");
                                bool first = true;
                                foreach (VolumeComponent comp in profile.components)
                                {
                                    if (comp == null) continue;
                                    if (!first) b.Append(";");
                                    first = false;
                                    b.Append(comp.GetType().Name).Append(":active=").Append(comp.active);
                                }
                                b.Append("]");
                            }
                            else b.Append(" profile=<none>");
                            b.AppendLine();
                        }
                    }
                }
                catch (Exception e) { b.AppendLine("VOLUME_CENSUS_ERROR " + e); }

                b.AppendLine();
                b.Append("COOKING_CPU total=").Append(_cookTotalMs.ToString("F3", CultureInfo.InvariantCulture))
                 .Append("ms itemFind=").Append(_cookItemFindMs.ToString("F3", CultureInfo.InvariantCulture))
                 .Append(" itemScan=").Append(_cookItemScanMs.ToString("F3", CultureInfo.InvariantCulture))
                 .Append(" items=").Append(_cookItems).Append(" itemCols=").Append(_cookItemColliders)
                 .Append(" cogFind=").Append(_cookCogFindMs.ToString("F3", CultureInfo.InvariantCulture))
                 .Append(" cogScan=").Append(_cookCogScanMs.ToString("F3", CultureInfo.InvariantCulture))
                 .Append(" cogs=").Append(_cookCogs).Append(" cogCols=").Append(_cookCogColliders)
                 .Append(" cogRenderers=").Append(_cookCogRenderers).AppendLine();
                b.AppendLine();

                lock (_ioLock)
                {
                    _snapshotWriter.Write(b.ToString());
                    _snapshotWriter.Flush();
                }
                WriteEvent("SNAPSHOT_WRITTEN", reason);
            }
            catch (Exception e)
            {
                WriteEvent("SNAPSHOT_FAIL", e.GetType().Name + ": " + e.Message);
            }
        }

        private static void UpdateCueProtocol(string context, float now)
        {
            if (!CueEnabled) return;
            bool gameplay = string.Equals(context, "GAMEPLAY", StringComparison.Ordinal);
            bool cooking = string.Equals(context, "GRABBAG_COOKING", StringComparison.Ordinal);

            if (_cueState == 0)
            {
                if (gameplay)
                {
                    if (_gameplayStableSince < 0f) _gameplayStableSince = now;
                    if (now - _gameplayStableSince >= 3f)
                    {
                        StereoRender.SetAerV102HmdCue("V106 PERFORMANCE LOGGER READY\nOPEN COOKING MENU WHEN CONVENIENT");
                        _cueState = 1;
                    }
                }
                else _gameplayStableSince = -1f;
            }
            else if (_cueState == 1)
            {
                if (cooking)
                {
                    _cookingEnteredAt = now;
                    StereoRender.SetAerV102HmdCue("");
                    WriteEvent("CUE_COOKING_CAPTURE_BEGIN", "cooking detected");
                    ActivateBurst(15f, "cue_cooking");
                    _cueState = 2;
                }
            }
            else if (_cueState == 2)
            {
                if (cooking && now - _cookingEnteredAt >= 8f)
                {
                    StereoRender.SetAerV102HmdCue("V106 COOKING CAPTURED\nCLOSE COOKING MENU NOW");
                    _cueState = 3;
                }
                else if (!cooking)
                {
                    // User closed quickly; still capture recovery and finish gracefully.
                    _cookingExitedAt = now;
                    StereoRender.SetAerV102HmdCue("");
                    ActivateBurst(5f, "cooking_quick_exit");
                    _cueState = 4;
                }
            }
            else if (_cueState == 3)
            {
                if (!cooking)
                {
                    _cookingExitedAt = now;
                    StereoRender.SetAerV102HmdCue("");
                    WriteEvent("CUE_COOKING_CAPTURE_END", "cooking closed; recovery capture");
                    ActivateBurst(5f, "cue_recovery");
                    _cueState = 4;
                }
            }
            else if (_cueState == 4 && now - _cookingExitedAt >= 4f)
            {
                StereoRender.SetAerV102HmdCue("V106 CAPTURE COMPLETE\nYOU CAN KEEP PLAYING OR PACK THE LOG");
                _cueState = 5;
            }
            else if (_cueState == 5 && now - _cookingExitedAt >= 8f)
            {
                StereoRender.SetAerV102HmdCue("");
                _cueState = 6;
            }
        }

        private static void ScheduleSnapshot(string reason, float delay)
        {
            if (string.IsNullOrEmpty(_pendingSnapshotReason))
            {
                _pendingSnapshotReason = reason;
                _pendingSnapshotAt = Time.unscaledTime + Mathf.Max(0f, delay);
            }
        }

        private static void ActivateBurst(float seconds, string reason)
        {
            float until = Time.unscaledTime + Mathf.Max(0.5f, seconds);
            if (until > _burstUntil) _burstUntil = until;
            WriteEvent("BURST_ARM", reason + " until=" + _burstUntil.ToString("F2", CultureInfo.InvariantCulture));
        }

        private static void EnqueueCameraRing(string row)
        {
            _cameraRing.Enqueue(row);
            while (_cameraRing.Count > CameraRingMax) _cameraRing.Dequeue();
        }

        private static void DumpCameraRing(string reason)
        {
            WriteEvent("CAMERA_RING_BEGIN", reason + " rows=" + _cameraRing.Count);
            foreach (string row in _cameraRing)
                WriteRawRow(row.Replace("CAMERA,", "CAMERA_RING,"));
            WriteEvent("CAMERA_RING_END", reason);
        }

        private static string PhaseNow()
        {
            int frame = Time.frameCount;
            if (_secondSubmitFrame == frame) return "POST_SUBMIT";
            if (_submitBeginFrame == frame) return "SUBMIT_WINDOW";
            return "PRE_SUBMIT";
        }

        private static void WriteEvent(string kind, string note)
        {
            string row = BaseColumns(kind) + ",,,,,,,,,,,,,,,,,,,,," + Csv(note);
            WriteRawRow(row);
        }

        private static void WriteRawRow(string row)
        {
            if (_writer == null) return;
            try
            {
                lock (_ioLock) _writer.WriteLine(row);
            }
            catch { }
        }

        private static void FlushBestEffort()
        {
            try
            {
                lock (_ioLock)
                {
                    _writer?.Flush();
                    _snapshotWriter?.Flush();
                }
            }
            catch { }
        }

        private static double ElapsedMs(long start, long end)
        {
            if (start <= 0 || end < start) return 0.0;
            return (end - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private static string Float(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

        private static string Csv(string s)
        {
            s = s ?? "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static string ResolveModDirectory()
        {
            try
            {
                string asm = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(asm)) return Path.GetDirectoryName(asm);
            }
            catch { }
            return Path.Combine(Directory.GetCurrentDirectory(), "Mods");
        }

        private static string MakeUniquePath(string dir, string stem, string ext)
        {
            string path = Path.Combine(dir, stem + ext);
            if (!File.Exists(path)) return path;
            for (int i = 1; i < 1000; i++)
            {
                path = Path.Combine(dir, stem + "_" + i.ToString("D3", CultureInfo.InvariantCulture) + ext);
                if (!File.Exists(path)) return path;
            }
            return Path.Combine(dir, stem + "_" + Guid.NewGuid().ToString("N") + ext);
        }
    }
}
