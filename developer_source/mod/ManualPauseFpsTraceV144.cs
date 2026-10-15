using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using Valve.VR;

namespace SonsVRMod
{
    /// <summary>
    /// V145: passive startup/performance trace, inherited from V144. NEVER injects
    /// input or modifies render settings. All timers use unscaled time so pause is visible.
    /// On-the-critical-path work is only a compact row and the existing OpenVR timing query;
    /// camera/DRS snapshots are performed at a few phase boundaries, never continuously.
    /// </summary>
    internal static class ManualPauseFpsTraceV144
    {
        internal static bool Enabled = true;
        private enum Phase { PassiveV145, Waiting, Baseline, Countdown, AwaitOpen, InMenu, AfterClose, Complete }
        // V145: passive launch trace, NO scheduled intervention or HMD countdown.
        private static Phase _phase = Phase.PassiveV145;
        private const bool PassiveStartupV145 = true;
        private static float _phaseStart;
        private static float _readySince = -1f;
        private static float _nextHeartbeat;
        private static float _lastFlush;
        private static float _lastSample;
        private static float _cueClearTime;
        private static float _postOpenSnapshotDue = -1f;
        private static float _postCloseSnapshotDue = -1f;
        private static float _lastNativeEventTime = -1f;
        private static readonly System.Collections.Generic.HashSet<string> NativeOnce = new System.Collections.Generic.HashSet<string>();
        private static int _countdownLast = -1;
        private static bool _lastPause;
        private static bool _lastFocus;
        private static bool _initialized;
        private static StreamWriter _writer;
        private static string _path;
        private static long _samples;
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        private static readonly StringBuilder Buffer = new StringBuilder(65536);
        private static string _lastScene = "";
        private const float BaselineSeconds = 8f;
        private const float CountdownSeconds = 5f;
        private const float ManualActionTimeout = 30f;
        private const float RecoverySeconds = 15f;

        internal static string TracePath { get { return _path; } }

        internal static void Initialize()
        {
            if (_initialized || !Enabled) return;
            _initialized = true;
            try
            {
                // Resolve next to SonsVR_Mod.dll, exactly as the existing V106 tracer does.
                string dir = Path.Combine(Environment.CurrentDirectory, "Mods");
                try
                {
                    string assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(assemblyPath)) dir = Path.GetDirectoryName(assemblyPath);
                }
                catch { }
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "SonsVR_V145_StartupFPS_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CI) + ".csv");
                _writer = new StreamWriter(new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false), 131072);
                _writer.WriteLine("kind,wall_time,real_s,phase,frame,scene,mode,time_scale,paused,focus,unity_dt_ms,gamepad_start,gamepad_b,keyboard_esc,compositor_frame,client_ms,total_gpu_ms,pre_gpu_ms,post_gpu_ms,submit_ms,wait_poses_ms,presents,mis_presented,dropped,reprojection_flags,active_slot,drs_fraction,target_fps,vsync,details");
                _writer.Flush();
                _lastPause = IsPaused();
                _lastFocus = Application.isFocused;
                LogEvent("BOOT", "V145 passive startup FPS + pause lifecycle; NO Start/B injection, NO scheduled cue");
                MelonLoader.MelonLogger.Warning("[V145 FPS-PROBE] file=" + _path + " ; NO SYNTHETIC MENU INPUT");
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Error("[V145 FPS-PROBE] failed to create CSV: " + ex);
                _writer = null;
            }
        }

        private static string Quote(string text)
        {
            text = text ?? "";
            return "\"" + text.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        }

        private static bool IsPaused()
        {
            // Time.timeScale is the authoritative single-player signal, with the cached
            // native pause-canvas signal as an additional observation for multiplayer.
            return Time.timeScale <= 0.0001f ||
                   (!VRPlayer.IsLikelySinglePlayerGameplay && VRPlayer.IsPauseMenuOpenConfirmed);
        }

        private static void Cue(string message)
        {
            StereoRender.SetAerV102HmdCue(message);
        }

        private static void Transition(Phase next, string detail)
        {
            _phase = next;
            _phaseStart = Time.unscaledTime;
            if (next == Phase.Baseline) NativeOnce.Clear();
            LogEvent("PHASE_" + next.ToString().ToUpperInvariant(), detail);
            CaptureLightState("PHASE_" + next.ToString().ToUpperInvariant());
            Flush();
        }

        // Called only at real pause-path state changes / one-shot capture milestones;
        // never modifies rendering, pauses the game or performs a scene scan.
        internal static void RecordPauseStep(string stage, string detail)
        {
            if (!Enabled || !_initialized || _writer == null) return;
            string key = stage + ":" + (_phase == Phase.InMenu || IsPaused() ? "paused" : "live");
            if (!NativeOnce.Add(key)) return;
            _lastNativeEventTime = Time.unscaledTime;
            LogEvent("NATIVE_" + stage, detail);
        }

        // V145: lightweight transitions only; unlike the old once-per-phase
        // markers, repeated real scene and menu events must all be observable.
        internal static void RecordV145Event(string stage, string detail)
        {
            if (!Enabled || !_initialized || _writer == null) return;
            LogEvent("V145_" + stage, detail);
        }

        // The legacy V102 world-space cue only produces a UI request; this callback
        // records the actual overlay activation in VRPlayer.LateUpdate.
        internal static void RecordCueRendered(bool active, string detail)
        {
            if (!Enabled || !_initialized || _writer == null) return;
            LogEvent(active ? "HMD_CUE_OVERLAY_ENABLED" : "HMD_CUE_OVERLAY_DISABLED", detail);
        }

        private static void CaptureVolumeSnapshot(string tag)
        {
            // One managed FindObjectsOfType<Volume> only after a phase has settled;
            // NEVER on Start or the first paused frame. Time the scan itself.
            long begin = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var volumes = UnityEngine.Object.FindObjectsOfType<UnityEngine.Rendering.Volume>();
                int total = volumes == null ? 0 : volumes.Length;
                StringBuilder b = new StringBuilder("tag=" + tag + " count=" + total);
                if (volumes != null)
                {
                    foreach (var v in volumes)
                    {
                        if (v == null) continue;
                        // Pause-related entries and high-priority global volumes, capped
                        // to prevent one diagnostic line from becoming an enormous scan.
                        if (v.name.IndexOf("pause", StringComparison.OrdinalIgnoreCase) < 0 &&
                            v.name.IndexOf("menu", StringComparison.OrdinalIgnoreCase) < 0 &&
                            (!v.isGlobal || v.priority < 8f)) continue;
                        b.Append(" | ").Append(v.name).Append('#').Append(v.GetInstanceID())
                            .Append(" active=").Append(v.gameObject.activeInHierarchy)
                            .Append(" enabled=").Append(v.enabled)
                            .Append(" global=").Append(v.isGlobal)
                            .Append(" weight=").Append(v.weight.ToString("F3", CI))
                            .Append(" priority=").Append(v.priority.ToString("F2", CI));
                        if (b.Length > 3500) { b.Append(" [truncated]"); break; }
                    }
                }
                double ms = 1000.0 * (System.Diagnostics.Stopwatch.GetTimestamp() - begin) /
                            System.Diagnostics.Stopwatch.Frequency;
                LogEvent("VOLUME_SNAPSHOT", b.ToString() + " scanMs=" + ms.ToString("F3", CI));
            }
            catch (Exception e)
            {
                LogEvent("VOLUME_SNAPSHOT_ERROR", tag + " " + e.GetType().Name + ": " + e.Message);
            }
        }

        internal static void Tick()
        {
            if (!Enabled) return;
            if (!_initialized) Initialize();
            if (_writer == null) return;
            float now = Time.unscaledTime;

            bool startPress = false, bPress = false, escapePress = false;
            try
            {
                var gamepad = Gamepad.current;
                if (gamepad != null)
                {
                    startPress = gamepad.startButton.wasPressedThisFrame;
                    bPress = gamepad.bButton.wasPressedThisFrame;
                }
                var keyboard = Keyboard.current;
                if (keyboard != null) escapePress = keyboard.escapeKey.wasPressedThisFrame;
            }
            catch (Exception ex) { LogEvent("INPUT_QUERY_FAILED", ex.Message); }

            bool pauseNow = IsPaused();
            if (startPress)
            {
                LogEvent("START_PRESS", "physical/observed input-system Start; NO injection");
                CaptureLightState("START_PRESS");
            }
            if (bPress) LogEvent("B_PRESS", "physical/observed input-system B; NO injection");
            if (escapePress) LogEvent("ESC_PRESS", "observed keyboard escape");
            if (pauseNow != _lastPause)
            {
                LogEvent(pauseNow ? "PAUSE_ENTER" : "PAUSE_EXIT", "timeScale=" + Time.timeScale.ToString("F3", CI) + " canvas=" + VRPlayer.IsPauseMenuOpenConfirmed);
                CaptureLightState(pauseNow ? "PAUSE_ENTER" : "PAUSE_EXIT");
                if (!PassiveStartupV145)
                {
                    if (pauseNow) _postOpenSnapshotDue = now + 0.90f;
                    else _postCloseSnapshotDue = now + 1.0f;
                }
                Flush();
                _lastPause = pauseNow;
            }
            if (_postOpenSnapshotDue > 0f && now >= _postOpenSnapshotDue)
            {
                _postOpenSnapshotDue = -1f;
                CaptureVolumeSnapshot("IN_MENU_SETTLED");
            }
            if (_postCloseSnapshotDue > 0f && now >= _postCloseSnapshotDue)
            {
                _postCloseSnapshotDue = -1f;
                CaptureVolumeSnapshot("AFTER_CLOSE_SETTLED");
            }
            if (Application.isFocused != _lastFocus)
            {
                _lastFocus = Application.isFocused;
                LogEvent("APP_FOCUS", "focused=" + _lastFocus);
                Flush();
            }
            string scene = SceneManager.GetActiveScene().name;
            if (scene != _lastScene)
            {
                LogEvent("SCENE_CHANGE", _lastScene + " -> " + scene);
                _lastScene = scene;
            }

            // Every rendered Unity frame during the critical transition, sparse baseline
            // elsewhere. Even at 4 FPS, we preserve every available frame in the event window.
            if (_phase == Phase.Countdown || _phase == Phase.AwaitOpen ||
                _phase == Phase.InMenu || _phase == Phase.AfterClose ||
                now - _lastSample >= (PassiveStartupV145 && now < 120f ? 0.25f : 0.5f))
            {
                _lastSample = now;
                WriteRow("FRAME", startPress, bPress, escapePress, "");
            }

            // V145: keep logging until exit; do not cue Start/B, do not scan
            // every HDRP volume in the baseline, and do not perform an A/B.
            if (PassiveStartupV145)
            {
                if (now - _lastFlush >= 1.0f) { _lastFlush = now; Flush(); }
                return;
            }

            if (_phase == Phase.Waiting)
            {
                var p = VRPlayer.Instance;
                var s = p != null ? p.StereoRender : null;
                bool ready = VRPlayer.IsLikelySinglePlayerGameplay && p != null && p.IsNativeGameplayCameraReadyV143 &&
                             s != null && s.LeftCam != null && s.RightCam != null &&
                             !pauseNow;
                if (!ready)
                {
                    _readySince = -1f;
                    if (now >= _nextHeartbeat)
                    {
                        _nextHeartbeat = now + 5f;
                        LogEvent("WAITING_FOR_GAMEPLAY", "activeScene=" + scene + " nativeGameplayContext=" + VRPlayer.IsLikelySinglePlayerGameplay + " nativeGameplayCam=" + (p != null && p.IsNativeGameplayCameraReadyV143) + " eyes=" + (s != null && s.LeftCam != null && s.RightCam != null));
                    }
                }
                else
                {
                    if (_readySince < 0) _readySince = now;
                    if (now - _readySince >= 2.0f)
                        Transition(Phase.Baseline, "native SonsMain gameplay context + MainCameraFP + LocalPlayer + VR eyes stable for 2s; activeScene can be BlankScene");
                }
            }
            else if (_phase == Phase.Baseline && now - _phaseStart >= BaselineSeconds)
            {
                CaptureVolumeSnapshot("BASELINE_COMPLETE");
                Transition(Phase.Countdown, "8s unmodified slow-FPS baseline captured");
                _countdownLast = -1;
            }
            else if (_phase == Phase.Countdown)
            {
                int left = UnityEngine.Mathf.Max(1, UnityEngine.Mathf.CeilToInt(CountdownSeconds - (now - _phaseStart)));
                if (left != _countdownLast)
                {
                    _countdownLast = left;
                    Cue("V144 FPS INVESTIGATION: " + left + "s\nWHEN THIS DISAPPEARS:\nPRESS START, THEN B ONCE");
                    LogEvent("COUNTDOWN", "remaining=" + left);
                }
                if (now - _phaseStart >= CountdownSeconds)
                {
                    // Hide before the user presses Start. No synthetic input is ever queued.
                    Cue("");
                    _cueClearTime = now;
                    Transition(Phase.AwaitOpen, "CUE_HIDDEN: MANUAL START THEN B NOW (30s allowed)");
                }
            }
            else if (_phase == Phase.AwaitOpen)
            {
                if (pauseNow)
                {
                    Transition(Phase.InMenu, "native pause detected; await user close");
                }
                else if (now - _phaseStart >= ManualActionTimeout)
                {
                    LogEvent("PAUSE_NOT_DETECTED", "manual press may have been ignored; still observing for recovery");
                    Transition(Phase.AfterClose, "TIMEOUT: no definitive pause signal; post window begins");
                }
            }
            else if (_phase == Phase.InMenu && !pauseNow)
            {
                Transition(Phase.AfterClose, "native pause closed; observe 15 seconds of recovery");
            }
            else if (_phase == Phase.AfterClose && now - _phaseStart >= RecoverySeconds)
            {
                Transition(Phase.Complete, "observation finished, safe to exit; no automatic workaround was run");
                Cue("V144 PAUSE TEST COMPLETE\nYOU MAY EXIT AND PACK THE TRACE");
            }
            if (now - _lastFlush >= 1.0f) { _lastFlush = now; Flush(); }
        }

        private static void CaptureLightState(string tag)
        {
            try
            {
                var handler = DynamicResolutionHandler.instance;
                string drs = handler == null ? "NA" : handler.m_CurrentFraction.ToString("F5", CI);
                string slots = DynamicResolutionHandler.s_ActiveScalerSlot.ToString();
                var cams = Camera.allCameras;
                StringBuilder info = new StringBuilder("tag=" + tag + " drs=" + drs + " slot=" + slots + " activeCameraCount=" + (cams == null ? 0 : cams.Length));
                if (cams != null)
                {
                    foreach (Camera c in cams)
                    {
                        if (c == null) continue;
                        info.Append(" | ").Append(c.name).Append('#').Append(c.GetInstanceID())
                            .Append(" enabled=").Append(c.enabled).Append(" active=").Append(c.gameObject.activeInHierarchy)
                            .Append(" depth=").Append(c.depth.ToString("F2", CI))
                            .Append(" mask=0x").Append(unchecked((uint)c.cullingMask).ToString("X8"))
                            .Append(" dyn=").Append(c.allowDynamicResolution);
                        if (c.targetTexture != null) info.Append(" rt=").Append(c.targetTexture.width).Append('x').Append(c.targetTexture.height);
                    }
                }
                LogEvent("LIGHT_CAMERA_STATE", info.ToString());
            }
            catch (Exception ex) { LogEvent("CAMERA_STATE_ERROR", ex.GetType().Name + ": " + ex.Message); }
        }

        private static void WriteRow(string kind, bool startPress, bool bPress, bool escapePress, string detail)
        {
            try
            {
                int f = -1; float client = -1, total = -1, pre = -1, post = -1, submit = -1, poses = -1;
                uint presented = 0, mis = 0, dropped = 0, flags = 0;
                try
                {
                    var compositor = OpenVR.Compositor;
                    if (compositor != null)
                    {
                        Compositor_FrameTiming t = new Compositor_FrameTiming();
                        t.m_nSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Compositor_FrameTiming));
                        if (compositor.GetFrameTiming(ref t, 0))
                        {
                            f = (int)t.m_nFrameIndex;
                            client = t.m_flClientFrameIntervalMs;
                            total = t.m_flTotalRenderGpuMs;
                            pre = t.m_flPreSubmitGpuMs;
                            post = t.m_flPostSubmitGpuMs;
                            submit = t.m_flSubmitFrameMs;
                            poses = t.m_flWaitGetPosesCalledMs;
                            presented = t.m_nNumFramePresents;
                            mis = t.m_nNumMisPresented;
                            dropped = t.m_nNumDroppedFrames;
                            flags = t.m_nReprojectionFlags;
                        }
                    }
                }
                catch { }
                string drs = "NA", slot = "NA";
                try
                {
                    slot = DynamicResolutionHandler.s_ActiveScalerSlot.ToString();
                    var handler = DynamicResolutionHandler.instance;
                    if (handler != null) drs = handler.m_CurrentFraction.ToString("F5", CI);
                }
                catch { }
                string line = string.Join(",", new string[] {
                    kind,
                    Quote(DateTime.Now.ToString("HH:mm:ss.fff", CI)),
                    Time.unscaledTime.ToString("F4", CI),
                    _phase.ToString(),
                    Time.frameCount.ToString(CI),
                    Quote(SceneManager.GetActiveScene().name),
                    StereoRender.UserGameplayAERV112 ? "AER" : "2E",
                    Time.timeScale.ToString("F4", CI),
                    IsPaused() ? "1" : "0",
                    Application.isFocused ? "1" : "0",
                    (Time.unscaledDeltaTime * 1000f).ToString("F3", CI),
                    startPress ? "1" : "0", bPress ? "1" : "0", escapePress ? "1" : "0",
                    f.ToString(CI), client.ToString("F3", CI), total.ToString("F3", CI),
                    pre.ToString("F3", CI), post.ToString("F3", CI), submit.ToString("F3", CI), poses.ToString("F3", CI),
                    presented.ToString(CI), mis.ToString(CI), dropped.ToString(CI), "0x" + flags.ToString("X"),
                    Quote(slot), Quote(drs), Application.targetFrameRate.ToString(CI), QualitySettings.vSyncCount.ToString(CI), Quote(detail)
                });
                Buffer.AppendLine(line);
                _samples++;
                if (Buffer.Length > 100000) Flush();
            }
            catch (Exception e)
            {
                if (_samples < 3) MelonLoader.MelonLogger.Warning("[V145 FPS-PROBE] sample error: " + e.Message);
            }
        }

        private static void LogEvent(string kind, string detail)
        {
            WriteRow(kind, false, false, false, detail);
            MelonLoader.MelonLogger.Warning("[V145 FPS-PROBE] " + kind + " " + detail);
        }

        private static void Flush()
        {
            try
            {
                if (_writer == null || Buffer.Length == 0) return;
                _writer.Write(Buffer.ToString());
                _writer.Flush();
                Buffer.Clear();
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Warning("[V145 FPS-PROBE] flush error: " + ex.Message);
            }
        }

        internal static void Shutdown()
        {
            try { Cue(""); Flush(); _writer?.Dispose(); }
            catch { }
            _writer = null;
        }
    }
}
