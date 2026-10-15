using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace SonsVRMod
{
    // V139: a single-launch, offscreen real-2E reference campaign.  No live V103
    // thresholds, matrices, HDRP callbacks, GPU contexts, or AER poses are mutated.
    internal static class AerCalibrationV139
    {
        internal static bool Enabled = false; // V140: preserve source, disable GPU truth campaign during live A/B
        internal static bool ActiveCapture => _phase == Phase.Capture;
        private enum Phase { WaitingForGameplay, Setup, Settle, Capture, Complete, Aborted }
        private static Phase _phase = Phase.WaitingForGameplay;
        private static readonly string[] Qualities = { "Performance", "Balanced", "Quality" };
        private const int SamplesPerBank = 4;
        private const float SetupSeconds = 180f;
        private const float WarmupSeconds = 9f;
        private const float BankTimeoutSeconds = 90f;
        private static int _bank = -1;
        private static float _phaseStart;
        private static float _nextAnnouncement;
        private static string _detail = "Waiting for gameplay; AER will be selected automatically";
        private static bool _bootLogged;
        private static float _nextWaitStatus;
        private static bool _modeSaved;
        private static bool _savedUserAer;
        private static bool _savedActualAer;
        private static bool _temporaryAer;
        private static bool _actualAerApplied;
        private static string _originalQuality = "";
        private static string _originalOption = "";
        private static bool _qualityTouched;
        private static int _lastReadbackId;
        private static bool _bankTransitionPending;
        private static readonly Dictionary<int,int> ReadbackMasks = new Dictionary<int,int>();
        private static StreamWriter _csv;
        private static string _path;
        // V139C: independent durable GPU-stage journal survives a frozen Player.log.
        private static StreamWriter _stageJournal;
        private static string _stagePath;
        private static float _lastControllerHeartbeat;
        private static Type _manager;
        private static MethodInfo _apply;
        private static MethodInfo _getOption;
        private static MethodInfo _getQuality;
        private static int _errors;
        private static float _poseStillStart;
        private static Vector3 _lastHeadPosition;
        private static Quaternion _lastHeadRotation;
        private static bool _haveHeadPose;
        private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance;

        private static void Log(string text)
        {
            MelonLogger.Warning("[V139 CAL] " + text);
        }

        // V139B: capture the user-selected mode before touching any runtime switch.
        // Do not modify SonsVR_Settings.ini; this is a one-session calibration mode.
        internal static void Initialize()
        {
            if (_modeSaved) return;
            _modeSaved = true;
            _savedUserAer = StereoRender.UserGameplayAERV112;
            _savedActualAer = StereoRender.AlternateEyeRenderingEnabled;
            Log("BOOT V139C savedGameplayMode=" + (_savedUserAer ? "AER" : "2E") +
                " capture=WAITING startupAutoAER=true; desktop status is enabled even while waiting");
        }

        private static void EnsureCalibrationAer()
        {
            if (!_modeSaved) Initialize();
            VRPlayer player = VRPlayer.Instance;
            StereoRender stereo = player != null ? player.StereoRender : null;
            if (stereo == null || stereo.LeftCam == null || stereo.RightCam == null) return;
            if (!StereoRender.UserGameplayAERV112)
            {
                StereoRender.UserGameplayAERV112 = true;
                _temporaryAer = true;
                Log("AUTO_SELECT_AER desiredMode=AER; original=" + (_savedUserAer ? "AER" : "2E") +
                    "; never writes settings INI");
            }
            // Title/loading remains conventional 2E. V112's normal gameplay activation
            // should select AER from the desired flag. If already in 2E gameplay, the
            // existing enabled-state detector safely switches AER on the next frame.
            if (StereoRender.AerGameplayReadyForActivation &&
                !StereoRender.AlternateEyeRenderingEnabled)
            {
                StereoRender.AlternateEyeRenderingEnabled = true;
                _temporaryAer = true;
                if (!_actualAerApplied)
                {
                    _actualAerApplied = true;
                    Log("AUTO_ACTIVATE_AER after gameplay gate; normal V112 transition will reset histories");
                }
            }
        }

        private static void RestoreCalibrationMode()
        {
            if (!_temporaryAer || !_modeSaved) return;
            StereoRender.UserGameplayAERV112 = _savedUserAer;
            if (StereoRender.AerGameplayReadyForActivation)
                StereoRender.AlternateEyeRenderingEnabled = _savedUserAer;
            Log("RESTORE_GAMEPLAY_MODE " + (_savedUserAer ? "AER" : "2E") +
                " (runtime only; persisted settings untouched)");
            _temporaryAer = false;
        }

        private static bool GameReady()
        {
            var player = VRPlayer.Instance;
            var stereo = player == null ? null : player.StereoRender;
            return StereoRender.AerGameplayReadyForActivation &&
                StereoRender.AlternateEyeRenderingEnabled &&
                NativeDlssReceiverRouterV128.Routed && stereo != null &&
                stereo.LeftCam != null && stereo.RightCam != null;
        }

        private static void Cue(string text)
        {
            StereoRender.SetAerV102HmdCue(text);
        }

        private static bool ResolveManager()
        {
            if (_manager != null && _apply != null && _getQuality != null && _getOption != null)
                return true;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    _manager = asm.GetType("Il2CppSons.Dlss.DynamicResUpscaleManager", false);
                    if (_manager != null) break;
                }
                catch { }
            }
            if (_manager == null) return false;
            _getOption = _manager.GetMethod("GetDynamicResolutionOption", Flags, null,
                Type.EmptyTypes, null);
            _getQuality = _manager.GetMethod("get__activeDlssQualitySetting", Flags, null,
                Type.EmptyTypes, null);
            if (_getQuality == null)
            {
                PropertyInfo q = _manager.GetProperty("_activeDlssQualitySetting", Flags);
                if (q != null) _getQuality = q.GetGetMethod(true);
            }
            foreach (MethodInfo method in _manager.GetMethods(Flags))
            {
                if (method.Name != "ApplyDlssSetting" || !method.IsStatic) continue;
                ParameterInfo[] args = method.GetParameters();
                if (args.Length == 1 && args[0].ParameterType == typeof(string))
                {
                    _apply = method;
                    break;
                }
            }
            if (_apply == null || _getQuality == null || _getOption == null)
            {
                Log("NATIVE_API_MISSING apply=" + (_apply != null) +
                    " qualityGetter=" + (_getQuality != null) +
                    " optionGetter=" + (_getOption != null) + " (no quality changes)");
                return false;
            }
            return true;
        }

        private static string Quality() => _getQuality?.Invoke(null, null)?.ToString() ?? "";
        private static string Option() => _getOption?.Invoke(null, null)?.ToString() ?? "";
        private static string Normalized(string s) => (s ?? "").Replace(" ", "")
            .Replace("_", "").Replace("-", "").Trim().ToLowerInvariant();

        private static bool MatchesQuality(string actual, string expected)
        {
            string a = Normalized(actual), e = Normalized(expected);
            return a == e || (e == "quality" && a == "maximumquality") ||
                (e == "performance" && a == "maximumperformance");
        }

        private static void OpenCsv()
        {
            string gameRoot = Directory.GetParent(Application.dataPath)?.FullName
                ?? Environment.CurrentDirectory;
            string dir = Path.Combine(gameRoot, "Mods");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "SonsVR_V139_CalibrationTiles_" +
                DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
            _csv = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write,
                FileShare.ReadWrite));
            _csv.WriteLine("bank,requestedQuality,sample,eye,candidate,tileX,tileY,tilesX,tilesY," +
                "yawDeg,pitchDeg,matchConfidence,edgeMAE,coverage,historyAgeMs,truthDepthMean,sceneGradient,depthEdgeFraction,matchAtSearchBoundary");
            _csv.Flush();
            _stagePath = Path.Combine(dir, "SonsVR_V139C_GpuStages_" +
                DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
            _stageJournal = new StreamWriter(new FileStream(_stagePath, FileMode.Create,
                FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough))
            { AutoFlush = true };
            TraceGpuV139C("JOURNAL_OPEN csv=" + _path + " stageLog=" + _stagePath);
            Log("CSV_OPEN " + _path);
        }

        private static float _nextEofHeartbeat;
        internal static void RenderHeartbeatV139C()
        {
            if (!Enabled || Time.unscaledTime < _nextEofHeartbeat) return;
            _nextEofHeartbeat = Time.unscaledTime + 5f;
            // This is AFTER stereoRenderPass.Execute; does not assert the GPU finished.
            Log("EOF_SUBMIT_HEARTBEAT phase=" + _phase + " frame=" + Time.frameCount +
                " mode=" + (StereoRender.AlternateEyeRenderingEnabled ? "AER" : "2E") +
                " gpuStageLog=" + (_stageJournal == null ? "not_open" : "open"));
        }

        internal static void TraceGpuV139C(string eventText)
        {
            string message = "[V139C GPU] t=" + Time.unscaledTime.ToString("F3",
                System.Globalization.CultureInfo.InvariantCulture) +
                " frame=" + Time.frameCount + " " + eventText;
            // The journal writes through to disk after each submission/completion.
            // Melon log is secondary; a stalled GPU may prevent normal final log flush.
            try { _stageJournal?.WriteLine(message); }
            catch (Exception e) { MelonLogger.Warning("[V139C GPU] JOURNAL_ERROR " + e.Message); }
            MelonLogger.Warning(message);
        }

        internal static void SetWorkerStatusV139C(string status)
        {
            _detail = "GPU staged work: " + status + " (one candidate per fence)";
        }

        internal static void FailGpuV139C(string reason)
        {
            TraceGpuV139C("FATAL " + reason);
            Abort(reason);
        }

        private static void Abort(string reason)
        {
            Log("ABORT " + reason);
            _detail = "ABORT: " + reason;
            _phase = Phase.Aborted;
            StereoRender.AerV99ForensicsEnabled = false;
            Cue("V139 CALIBRATION STOPPED\nCHECK DESKTOP / LOG");
            RestoreOriginalQuality();
            RestoreCalibrationMode();
            _csv?.Flush();
            try { _stageJournal?.Flush(); } catch { }
        }

        private static void RestoreOriginalQuality()
        {
            if (!_qualityTouched || _apply == null || string.IsNullOrEmpty(_originalQuality)) return;
            try
            {
                string name = _originalQuality;
                if (Normalized(name) == "maximumquality") name = "Quality";
                else if (Normalized(name) == "maximumperformance") name = "Performance";
                _apply.Invoke(null, new object[] { name });
                Log("RESTORE_QUALITY requested=" + name + " observed=" + Quality());
            }
            catch (Exception ex) { Log("RESTORE_FAILED " + ex.Message); }
            _qualityTouched = false;
        }

        private static bool IsStablePose()
        {
            Transform head = VRPlayer.Instance != null && VRPlayer.Instance.StereoRender != null
                ? VRPlayer.Instance.StereoRender.Head : null;
            if (head == null) return false;
            float now = Time.unscaledTime;
            Vector3 pos = head.position;
            Quaternion rot = head.rotation;
            if (!_haveHeadPose)
            {
                _haveHeadPose = true; _lastHeadPosition = pos;
                _lastHeadRotation = rot; _poseStillStart = now;
                return false;
            }
            float d = Vector3.Distance(pos, _lastHeadPosition);
            float a = Quaternion.Angle(rot, _lastHeadRotation);
            _lastHeadPosition = pos;
            _lastHeadRotation = rot;
            if (d > 0.002f || a > 0.12f) _poseStillStart = now;
            return now - _poseStillStart > 2f;
        }

        internal static void Tick()
        {
            if (!Enabled || _phase == Phase.Complete || _phase == Phase.Aborted) return;
            try
            {
                float now = Time.unscaledTime;
                if (!_bootLogged)
                {
                    _bootLogged = true;
                    Initialize();
                }
                if (_phase == Phase.WaitingForGameplay)
                {
                    EnsureCalibrationAer();
                    if (!GameReady())
                    {
                        _detail = "WAITING: AER=" + StereoRender.AlternateEyeRenderingEnabled +
                            " gameplayGate=" + StereoRender.AerGameplayReadyForActivation +
                            " nativeReceivers=" + NativeDlssReceiverRouterV128.Routed;
                        if (now >= _nextWaitStatus)
                        {
                            _nextWaitStatus = now + 10f;
                            Log("WAIT_FOR_AER " + _detail + "; no timer yet");
                        }
                        return;
                    }
                    if (!ResolveManager()) { Abort("native DLSS quality setter/getter unavailable"); return; }
                    _originalOption = Option();
                    _originalQuality = Quality();
                    if (!string.Equals(_originalOption, "Dlss", StringComparison.OrdinalIgnoreCase))
                    { Abort("please select native DLSS in the game menu first; option=" + _originalOption); return; }
                    OpenCsv();
                    _phaseStart = now;
                    _phase = Phase.Setup;
                    Log("SETUP_180_SECONDS originalQuality=" + _originalQuality +
                        " (enter console, reach rooftop, mount HMD; clock uses unscaled time)");
                    _nextAnnouncement = -1;
                }

                if (!GameReady())
                {
                    if (_phase == Phase.Capture || _phase == Phase.Settle)
                        Abort("left AER gameplay / receiver route invalidated");
                    return;
                }

                if (_phase == Phase.Setup)
                {
                    int remaining = Mathf.Max(0, Mathf.CeilToInt(SetupSeconds - (now - _phaseStart)));
                    _detail = "SETUP " + remaining + "s: console / rooftop / mount headset";
                    if (now >= _nextAnnouncement)
                    {
                        _nextAnnouncement = now + (remaining > 15 ? 10f : 1f);
                        Cue("V139 CALIBRATION STARTS IN " + remaining + "s\n" +
                            "ENTER CONSOLE COMMANDS / POSITION HMD");
                        Log("SETUP_REMAINING " + remaining + "s");
                    }
                    if (remaining <= 0)
                    {
                        Cue("");
                        _phaseStart = now;
                        _phase = Phase.Settle;
                        _bank = -1;
                        Log("SETUP_ENDED; waiting for stationary tracking");
                    }
                    return;
                }

                if (_phase == Phase.Settle)
                {
                    // V139: never change native DLSS quality inside an AsyncGPUReadback
                    // completion callback; schedule it from the next main-thread Update.
                    if (_bankTransitionPending)
                    {
                        _bankTransitionPending = false;
                        BeginNextBank(now);
                        return;
                    }
                    if (_bank == -1)
                    {
                        _detail = "Waiting for headset pose to remain stable for 2s";
                        if (!IsStablePose()) return;
                        BeginNextBank(now);
                        return;
                    }
                    if (now - _phaseStart < WarmupSeconds)
                    {
                        _detail = "WARMUP " + Qualities[_bank] + " (" +
                            Mathf.Max(0,Mathf.CeilToInt(WarmupSeconds-(now-_phaseStart))) + "s)";
                        return;
                    }
                    if (!MatchesQuality(Quality(), Qualities[_bank]))
                    { Abort("native menu quality did not settle to " + Qualities[_bank] +
                            "; actual=" + Quality()); return; }
                    // Guarantee no in-HMD prompt is present on the diagnostic truth frames.
                    Cue("");
                    if (!StereoRender.TryBeginCalibrationBankV139(SamplesPerBank))
                    {
                        if (now - _phaseStart > WarmupSeconds + 12f)
                            Abort("V99 truth readbacks did not drain before bank switch");
                        return;
                    }
                    _phase = Phase.Capture;
                    _phaseStart = now;
                    _detail = "CAPTURE " + Qualities[_bank] + " 0/" + SamplesPerBank;
                    Log("BANK_BEGIN bank=" + _bank + " quality=" + Qualities[_bank] +
                        " samples=" + SamplesPerBank + " no on-screen cue during captures");
                    return;
                }

                if (_phase == Phase.Capture)
                {
                    if (now - _lastControllerHeartbeat >= 3f)
                    {
                        _lastControllerHeartbeat = now;
                        TraceGpuV139C("CONTROLLER_HEARTBEAT bank=" + _bank +
                            " quality=" + Qualities[_bank] + " detail=" + _detail +
                            " managedMemoryMB=" + (GC.GetTotalMemory(false)/(1024*1024)));
                    }
                    if (!MatchesQuality(Quality(), Qualities[_bank]))
                    { Abort("unexpected native quality change during bank"); return; }
                    if (now - _phaseStart > BankTimeoutSeconds)
                        Abort("bank timeout after " + BankTimeoutSeconds + "s");
                }
            }
            catch (Exception e)
            {
                if (_errors++ < 4) Log("EXCEPTION " + e.GetType().Name + ": " + e.Message);
                Abort("exception in controller");
            }
        }

        private static void BeginNextBank(float now)
        {
            _bank++;
            if (_bank >= Qualities.Length)
            {
                Finish();
                return;
            }
            _haveHeadPose = false;
            ReadbackMasks.Clear();
            string desired = Qualities[_bank];
            _apply.Invoke(null, new object[] { desired });
            _qualityTouched = true;
            _phaseStart = now;
            _phase = Phase.Settle;
            _detail = "Switching native DLSS to " + desired;
            Log("NATIVE_QUALITY_REQUEST bank=" + _bank + " value=" + desired +
                " readback=" + Quality() + " previous=" + _originalQuality);
        }

        internal static void RecordTile(int sample, int eye, string candidate,
            int x, int y, int columns, int rows, float yaw, float pitch,
            float confidence, float edgeMae, float coverage, float historyAge,
            float truthDepthMean, float sceneGradient, float depthEdgeFraction,
            bool matchAtSearchBoundary = false)
        {
            if (_phase != Phase.Capture || _csv == null) return;
            // CSV uses invariant culture so postprocessing works on any Windows locale.
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            _csv.WriteLine(string.Join(",", new string[] {
                _bank.ToString(ci), Qualities[_bank], sample.ToString(ci), eye.ToString(ci),
                candidate, x.ToString(ci), y.ToString(ci), columns.ToString(ci), rows.ToString(ci),
                yaw.ToString("G9",ci), pitch.ToString("G9",ci),
                confidence.ToString("G9",ci), edgeMae.ToString("G9",ci),
                coverage.ToString("G9",ci), historyAge.ToString("G9",ci),
                truthDepthMean.ToString("G9",ci),sceneGradient.ToString("G9",ci),
                depthEdgeFraction.ToString("G9",ci),
                matchAtSearchBoundary ? "1" : "0"
            }));
        }

        internal static void OnReadbackError(int sample,int eye)
        {
            if (_phase == Phase.Capture) Abort("GPU readback failed sample=" + sample + " eye=" + eye);
        }

        internal static void OnReadbackComplete(int sample,int eye)
        {
            if (_phase != Phase.Capture) return;
            int mask;
            ReadbackMasks.TryGetValue(sample, out mask);
            mask |= 1 << eye;
            ReadbackMasks[sample] = mask;
            _lastReadbackId = sample;
            int pairs=0;
            foreach (var pair in ReadbackMasks) if (pair.Value == 3) pairs++;
            _detail = "CAPTURE " + Qualities[_bank] + " " + pairs + "/" + SamplesPerBank +
                " stereo truth pairs";
            if (mask == 3) _csv?.Flush(); // durable after each accepted stereo pair
            TraceGpuV139C("TRUTH_READBACK_COMPLETE bank=" + _bank + " sample=" + sample +
                " eye=" + eye + " pairs=" + pairs);
            Log("TRUTH_READBACK bank=" + _bank + " quality=" + Qualities[_bank] +
                " sample=" + sample + " eye=" + (eye == 0 ? "L" : "R") +
                " completedPairs=" + pairs);
            if (pairs >= SamplesPerBank)
            {
                StereoRender.AerV99ForensicsEnabled = false;
                _csv?.Flush();
                Log("BANK_COMPLETE bank=" + _bank + " quality=" + Qualities[_bank] +
                    " pairs=" + pairs + " csv=" + _path);
                _phase = Phase.Settle;
                _bankTransitionPending = true;
            }
        }

        private static void Finish()
        {
            StereoRender.AerV99ForensicsEnabled = false;
            _csv?.Flush();
            _phase = Phase.Complete;
            _detail = "COMPLETE: 50% / 58% / 67%. Pack trace and run analysis.";
            RestoreOriginalQuality();
            RestoreCalibrationMode();
            Cue("V139 CALIBRATION COMPLETE\nYOU CAN MOVE THE HEADSET");
            TraceGpuV139C("CAMPAIGN_COMPLETE samplesPerBank=" + SamplesPerBank);
            try { _stageJournal?.Dispose(); } catch { }
            _stageJournal = null;
            Log("COMPLETE profile banks=3 samplesPerBank=" + SamplesPerBank +
                " report via ANALYZE_V139.bat; no live guard profile was changed");
        }

        internal static void DrawDesktopStatus()
        {
            if (!Enabled) return;
            // Show preconditions too: V139 hid the panel until AER became ready, which
            // made a missing mode prerequisite look like ten minutes of silence.
            // Desktop IMGUI only; never drawn into the offscreen candidate textures.
            UnityEngine.GUI.Box(new Rect(12, 12, 695, 114),
                "V139C TIME-SLICED AUTO-CALIBRATION  |  " + _phase + "\n" +
                _detail + "\n" +
                (_phase == Phase.WaitingForGameplay
                    ? "Waiting for gameplay and native DLSS. AER will be selected automatically."
                    : "3-minute setup then automatic truth capture; watch progress here.") + "\n" +
                "Do not move headset once CAPTURE begins. Logs: [V139 CAL]");
        }

        internal static void Shutdown()
        {
            StereoRender.AerV99ForensicsEnabled = false;
            RestoreOriginalQuality();
            RestoreCalibrationMode();
            try { _csv?.Flush(); _csv?.Dispose(); }
            catch { }
            _csv = null;
            try { _stageJournal?.Flush(); _stageJournal?.Dispose(); }
            catch { }
            _stageJournal = null;
        }
    }
}
