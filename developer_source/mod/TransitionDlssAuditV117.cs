using HarmonyLib;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace SonsVRMod
{
    /// <summary>
    /// V118 read-only DLSS control probe, retaining the V117 exit watchdog after the V116 HUD freeze:
    ///
    ///  1) keep passive pause-menu -> return-to-title watchdog coverage, and
    ///  2) determine exactly why DLSS quality selection does not change the 2E eye render domain.
    ///
    /// This class intentionally does NOT touch the V116 HUD/tarp/cooking implementation or mutate DLSS policy.
    /// Transition instrumentation is method-entry/exit only. DLSS instrumentation only reads
    /// runtime state and calls NVIDIA's GetOptimalSettings query; it does not create/destroy a
    /// DLSS feature and does not change quality, dynamic resolution, or render targets.
    /// </summary>
    internal static class TransitionDlssAuditV117
    {
        internal static bool Enabled = true;

        private static readonly object FileLock = new object();
        private static StreamWriter _writer;
        private static string _tracePath;
        private static HarmonyLib.Harmony _harmony;

        // IL2CPP SceneManager exposes UnityAction-backed callbacks rather than normal C#
        // events in this generated wrapper. Cache explicit UnityAction instances so +=/-=
        // do not rely on method-group conversion (which does not compile here), and so
        // unsubscribe uses the exact same delegate objects.
        private static UnityEngine.Events.UnityAction<Scene, LoadSceneMode> _sceneLoadedHandler;
        private static UnityEngine.Events.UnityAction<Scene> _sceneUnloadedHandler;
        private static UnityEngine.Events.UnityAction<Scene, Scene> _activeSceneChangedHandler;

        private static int _patchedMethodCount;
        private static bool _initialized;
        private static bool _shutdown;

        // Main-thread heartbeat observed by a background watchdog. The watchdog never calls Unity.
        private static long _mainHeartbeatTicks;
        private static int _titleTransitionActive;
        private static string _lastTransitionMarker = "none";
        private static Thread _watchdog;
        private static volatile bool _watchdogStop;
        private static float _nextTransitionMainSnapshot;
        private static int _delayMoveNextCount;

        // DLSS snapshot scheduling after a user settings apply.
        private static bool _dlssSnapshotPending;
        private static string _pendingDlssQuality = "<none>";
        private static bool _pendingDlssEnabled;
        private static int _dlssSnapshotStage;
        private static float _nextDlssSnapshotTime;
        private static bool _initialDlssSnapshotDone;

        private static Type _nvidiaPluginType;
        private static Type _nvidiaGraphicsDeviceType;
        private static Type _nvidiaQualityType;
        private static Type _nvidiaOptimalDataType;
        private static MethodInfo _nvidiaGetOptimalStatic;
        private static MethodInfo _nvidiaGetOptimalInstance;
        private static PropertyInfo _nvidiaDeviceProperty;
        private static object _nvidiaGraphicsDeviceInstance;
        private static Type _sonsDlssManagerType;

        // V118: camera-boundary snapshots of Unity's DynamicResolutionHandler.  The global
        // handler is camera-scoped by HDRP, so sampling only from MonoBehaviour.Update can
        // accidentally describe whichever camera rendered last.  Capture a tiny number of
        // begin/end render snapshots immediately after each settings apply instead.
        private static Il2CppSystem.Action<ScriptableRenderContext, Camera> _beginCameraRenderingDelegateV118;
        private static Il2CppSystem.Action<ScriptableRenderContext, Camera> _endCameraRenderingDelegateV118;
        private static int _renderBoundaryBudgetV118;
        private static int _renderBoundaryLeftBeginFrameV118 = -1;
        private static int _renderBoundaryRightBeginFrameV118 = -1;
        private static int _renderBoundaryLeftEndFrameV118 = -1;
        private static int _renderBoundaryRightEndFrameV118 = -1;
        private static bool _dynamicScaleProbeDoneV118;
        private static bool _surfaceInventoryDoneV118;

        private static readonly string[] PauseMenuTargetNames =
        {
            "TriggerQuitToTitle",
            "TriggerQuitToTitleInternal",
            "ExitToTitle",
            "DelayReturnToTitle",
            "ReturnToTitle",
            "ReturnToTitleWithModal",
            "QuitToTitle",
            "QuitToTitleSkipConfirm"
        };

        private static readonly string[] LoadingTargetNames =
        {
            "DoLoad",
            "DoLoadInstant",
            "LoadSync",
            "LoadExclusiveSingle",
            "LoadScenes",
            "StartLoadAsync",
            "MoveHandOffGameObjects",
            "RemoveScenesByName",
            "LoadScene",
            "UnloadScene",
            "UnloadScenes"
        };

        internal static void Initialize()
        {
            if (!Enabled || _initialized)
                return;

            _initialized = true;
            _shutdown = false;
            _mainHeartbeatTicks = Stopwatch.GetTimestamp();

            try
            {
                string gameRoot = null;
                try
                {
                    DirectoryInfo parent = Directory.GetParent(Application.dataPath);
                    gameRoot = parent != null ? parent.FullName : null;
                }
                catch { }

                string outDir = !string.IsNullOrEmpty(gameRoot)
                    ? Path.Combine(gameRoot, "Mods")
                    : Environment.CurrentDirectory;
                Directory.CreateDirectory(outDir);
                _tracePath = Path.Combine(
                    outDir,
                    "SonsVR_V128_Native_DLSS_Reroute_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");

                _writer = new StreamWriter(new FileStream(
                    _tracePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite))
                {
                    AutoFlush = true
                };

                WriteLine("INIT V128 native DLSS receiver-reroute truth probe + retained V117 exit watchdog");
                WriteLine("HUD_FREEZE UnifiedHudWorldPlaneV116 + V116 tarp/cooking path intentionally untouched");
                WriteLine("TRACE_PATH " + _tracePath);

                // V117d: Il2CppInterop UnityAction proxy constructors take IntPtr, not managed
                // method groups.  Bridge through System.Action so the generated UnityAction
                // implicit conversion / DelegateSupport path creates the IL2CPP delegate.
                _sceneLoadedHandler = (UnityEngine.Events.UnityAction<Scene, LoadSceneMode>)
                    new System.Action<Scene, LoadSceneMode>(OnSceneLoaded);
                _sceneUnloadedHandler = (UnityEngine.Events.UnityAction<Scene>)
                    new System.Action<Scene>(OnSceneUnloaded);
                _activeSceneChangedHandler = (UnityEngine.Events.UnityAction<Scene, Scene>)
                    new System.Action<Scene, Scene>(OnActiveSceneChanged);

                SceneManager.sceneLoaded += _sceneLoadedHandler;
                SceneManager.sceneUnloaded += _sceneUnloadedHandler;
                SceneManager.activeSceneChanged += _activeSceneChangedHandler;

                InstallTransitionPatches();
                DiscoverDlssSurfaces();
                InstallRenderBoundaryDrsProbeV118();
                StartWatchdog();
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[V118 Truth] Initialize failed fail-soft: " + e.GetType().Name + ": " + e.Message);
            }
        }

        internal static void Tick()
        {
            if (!Enabled || !_initialized || _shutdown)
                return;

            Interlocked.Exchange(ref _mainHeartbeatTicks, Stopwatch.GetTimestamp());

            float now = Time.unscaledTime;

            if (!_initialDlssSnapshotDone)
            {
                StereoRender stereo = VRPlayer.Instance != null ? VRPlayer.Instance.StereoRender : null;
                if (stereo != null && stereo.LeftCam != null && stereo.RightCam != null)
                {
                    _initialDlssSnapshotDone = true;
                    _renderBoundaryBudgetV118 = 8;
                    CaptureDlssSnapshot("initial-runtime");
                }
            }

            if (_dlssSnapshotPending && now >= _nextDlssSnapshotTime)
            {
                string stageLabel = _dlssSnapshotStage == 0 ? "apply+0s" :
                                    _dlssSnapshotStage == 1 ? "apply+0.75s" :
                                    _dlssSnapshotStage == 2 ? "apply+2.5s" :
                                    "apply+6s";
                CaptureDlssSnapshot(stageLabel + " requested=" + _pendingDlssEnabled + "/" + _pendingDlssQuality);

                _dlssSnapshotStage++;
                if (_dlssSnapshotStage == 1)
                    _nextDlssSnapshotTime = now + 0.75f;
                else if (_dlssSnapshotStage == 2)
                    _nextDlssSnapshotTime = now + 1.75f;
                else if (_dlssSnapshotStage == 3)
                    _nextDlssSnapshotTime = now + 3.5f;
                else
                    _dlssSnapshotPending = false;
            }

            if (Volatile.Read(ref _titleTransitionActive) != 0 && now >= _nextTransitionMainSnapshot)
            {
                _nextTransitionMainSnapshot = now + 0.5f;
                string scene = SafeActiveSceneName();
                bool player = VRPlayer.Instance != null;
                bool stereo = player && VRPlayer.Instance.StereoRender != null;
                WriteLine(
                    "MAIN_HEARTBEAT scene=" + scene +
                    " player=" + player +
                    " stereo=" + stereo +
                    " aer=" + StereoRender.AlternateEyeRenderingEnabled +
                    " gameplayReady=" + StereoRender.AerGameplayReadyForActivation +
                    " last=" + _lastTransitionMarker);
            }
        }

        internal static void NotifySettingsApplied(bool dlssEnabled, string quality)
        {
            if (!Enabled || !_initialized || _shutdown)
                return;

            _pendingDlssEnabled = dlssEnabled;
            _pendingDlssQuality = quality ?? "<null>";
            _dlssSnapshotStage = 0;
            _nextDlssSnapshotTime = Time.unscaledTime;
            _dlssSnapshotPending = true;
            _renderBoundaryBudgetV118 = 16;
            _renderBoundaryLeftBeginFrameV118 = -1;
            _renderBoundaryRightBeginFrameV118 = -1;
            _renderBoundaryLeftEndFrameV118 = -1;
            _renderBoundaryRightEndFrameV118 = -1;
            WriteLine("DLSS_SETTINGS_APPLIED enabled=" + dlssEnabled + " quality=" + _pendingDlssQuality);
        }

        internal static void Shutdown()
        {
            if (!_initialized || _shutdown)
                return;

            _shutdown = true;
            _watchdogStop = true;

            try
            {
                if (_beginCameraRenderingDelegateV118 != null)
                    RenderPipelineManager.remove_beginCameraRendering(_beginCameraRenderingDelegateV118);
                if (_endCameraRenderingDelegateV118 != null)
                    RenderPipelineManager.remove_endCameraRendering(_endCameraRenderingDelegateV118);
                _beginCameraRenderingDelegateV118 = null;
                _endCameraRenderingDelegateV118 = null;
            }
            catch { }

            try
            {
                if (_sceneLoadedHandler != null)
                    SceneManager.sceneLoaded -= _sceneLoadedHandler;
                if (_sceneUnloadedHandler != null)
                    SceneManager.sceneUnloaded -= _sceneUnloadedHandler;
                if (_activeSceneChangedHandler != null)
                    SceneManager.activeSceneChanged -= _activeSceneChangedHandler;

                _sceneLoadedHandler = null;
                _sceneUnloadedHandler = null;
                _activeSceneChangedHandler = null;
            }
            catch { }

            try
            {
                WriteLine("SHUTDOWN");
                lock (FileLock)
                {
                    if (_writer != null)
                    {
                        _writer.Flush();
                        _writer.Dispose();
                        _writer = null;
                    }
                }
            }
            catch { }
        }

        private static void InstallTransitionPatches()
        {
            try
            {
                _harmony = new HarmonyLib.Harmony("zer0int.sonsvr.v117.exit-dlss-truth");
                HarmonyMethod prefix = new HarmonyMethod(typeof(TransitionDlssAuditV117).GetMethod(
                    nameof(TracePrefix), BindingFlags.NonPublic | BindingFlags.Static));
                HarmonyMethod postfix = new HarmonyMethod(typeof(TransitionDlssAuditV117).GetMethod(
                    nameof(TracePostfix), BindingFlags.NonPublic | BindingFlags.Static));
                HarmonyMethod movePrefix = new HarmonyMethod(typeof(TransitionDlssAuditV117).GetMethod(
                    nameof(DelayMoveNextPrefix), BindingFlags.NonPublic | BindingFlags.Static));
                HarmonyMethod movePostfix = new HarmonyMethod(typeof(TransitionDlssAuditV117).GetMethod(
                    nameof(DelayMoveNextPostfix), BindingFlags.NonPublic | BindingFlags.Static));

                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string asmName = SafeAssemblyName(asm);
                    bool guiAsm = asmName.IndexOf("Sons.Gui", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool loadingAsm = asmName.IndexOf("Sons.Loading", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!guiAsm && !loadingAsm)
                        continue;

                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch { continue; }

                    foreach (Type t in types)
                    {
                        if (t == null) continue;
                        string full = t.FullName ?? t.Name ?? "";

                        if (guiAsm && full.IndexOf("PauseMenu", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            PatchNamedMethods(t, PauseMenuTargetNames, prefix, postfix);

                            if (full.IndexOf("DelayReturnToTitle", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                                {
                                    if (m.Name != "MoveNext" || m.ReturnType != typeof(bool) || m.ContainsGenericParameters || m.IsAbstract)
                                        continue;
                                    TryPatch(m, movePrefix, movePostfix);
                                }
                            }
                        }

                        if (loadingAsm &&
                            (full.IndexOf("SonsSceneManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             full.IndexOf("LoadScene", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             full.IndexOf("SceneLoading", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            PatchNamedMethods(t, LoadingTargetNames, prefix, postfix);
                        }
                    }
                }

                WriteLine("PATCH_SUMMARY patchedMethods=" + _patchedMethodCount);
                MelonLogger.Msg("[V117 Truth] Installed passive exit-to-title patches: " + _patchedMethodCount + " methods.");
            }
            catch (Exception e)
            {
                WriteLine("PATCH_FATAL " + e.GetType().Name + ": " + e.Message);
                MelonLogger.Warning("[V117 Truth] transition patch install failed fail-soft: " + e.Message);
            }
        }

        private static void PatchNamedMethods(Type t, string[] names, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            MethodInfo[] methods;
            try
            {
                methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            }
            catch { return; }

            foreach (MethodInfo m in methods)
            {
                if (m == null || m.ContainsGenericParameters || m.IsAbstract)
                    continue;
                if (!names.Contains(m.Name, StringComparer.Ordinal))
                    continue;
                TryPatch(m, prefix, postfix);
            }
        }

        private static void TryPatch(MethodInfo m, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            try
            {
                _harmony.Patch(m, prefix: prefix, postfix: postfix);
                _patchedMethodCount++;
                WriteLine("PATCHED " + MethodLabel(m));
            }
            catch (Exception e)
            {
                WriteLine("PATCH_FAIL " + MethodLabel(m) + " :: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void TracePrefix(MethodBase __originalMethod)
        {
            string label = MethodLabel(__originalMethod);
            if (IsTitleTransitionMethod(__originalMethod))
            {
                if (Interlocked.Exchange(ref _titleTransitionActive, 1) == 0)
                {
                    _nextTransitionMainSnapshot = 0f;
                    _delayMoveNextCount = 0;
                    WriteLine("TITLE_TRANSITION_ARM scene=" + SafeActiveSceneName());
                }
            }

            if (Volatile.Read(ref _titleTransitionActive) != 0)
            {
                _lastTransitionMarker = "ENTER " + label;
                WriteLine(_lastTransitionMarker);
            }
        }

        private static void TracePostfix(MethodBase __originalMethod)
        {
            if (Volatile.Read(ref _titleTransitionActive) == 0)
                return;

            string label = MethodLabel(__originalMethod);
            _lastTransitionMarker = "EXIT " + label;
            WriteLine(_lastTransitionMarker);
        }

        private static void DelayMoveNextPrefix(MethodBase __originalMethod)
        {
            if (Volatile.Read(ref _titleTransitionActive) == 0)
                return;

            int n = Interlocked.Increment(ref _delayMoveNextCount);
            if (n <= 20 || (n % 60) == 0)
            {
                _lastTransitionMarker = "ENTER DelayReturnToTitle.MoveNext#" + n;
                WriteLine(_lastTransitionMarker);
            }
        }

        private static void DelayMoveNextPostfix(bool __result, MethodBase __originalMethod)
        {
            if (Volatile.Read(ref _titleTransitionActive) == 0)
                return;

            int n = Volatile.Read(ref _delayMoveNextCount);
            if (n <= 20 || (n % 60) == 0 || !__result)
            {
                _lastTransitionMarker = "EXIT DelayReturnToTitle.MoveNext#" + n + " result=" + __result;
                WriteLine(_lastTransitionMarker);
            }
        }

        private static bool IsTitleTransitionMethod(MethodBase m)
        {
            if (m == null) return false;
            string n = m.Name ?? "";
            return n.IndexOf("QuitToTitle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("ReturnToTitle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.Equals("ExitToTitle", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("DelayReturnToTitle", StringComparison.OrdinalIgnoreCase);
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (Volatile.Read(ref _titleTransitionActive) == 0)
                return;
            WriteLine("SCENE_LOADED name=" + SafeSceneName(scene) + " mode=" + mode);
            if (SafeSceneName(scene).IndexOf("Title", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                WriteLine("TITLE_SCENE_REACHED");
                Interlocked.Exchange(ref _titleTransitionActive, 0);
            }
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            if (Volatile.Read(ref _titleTransitionActive) != 0)
                WriteLine("SCENE_UNLOADED name=" + SafeSceneName(scene));
        }

        private static void OnActiveSceneChanged(Scene oldScene, Scene newScene)
        {
            if (Volatile.Read(ref _titleTransitionActive) != 0)
                WriteLine("ACTIVE_SCENE_CHANGED old=" + SafeSceneName(oldScene) + " new=" + SafeSceneName(newScene));
        }

        private static void StartWatchdog()
        {
            try
            {
                _watchdogStop = false;
                _watchdog = new Thread(WatchdogLoop)
                {
                    IsBackground = true,
                    Name = "SonsVR_V117_TransitionWatchdog"
                };
                _watchdog.Start();
                WriteLine("WATCHDOG_STARTED");
            }
            catch (Exception e)
            {
                WriteLine("WATCHDOG_START_FAIL " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void WatchdogLoop()
        {
            while (!_watchdogStop)
            {
                try
                {
                    Thread.Sleep(1000);
                    if (Volatile.Read(ref _titleTransitionActive) == 0)
                        continue;

                    long now = Stopwatch.GetTimestamp();
                    long last = Interlocked.Read(ref _mainHeartbeatTicks);
                    double ageMs = (now - last) * 1000.0 / Stopwatch.Frequency;
                    WriteLineFromAnyThread(
                        "WATCHDOG mainHeartbeatAgeMs=" + ageMs.ToString("F1") +
                        " last=" + (_lastTransitionMarker ?? "<null>") +
                        (ageMs > 5000.0 ? " MAIN_THREAD_STALL_GT5S" : ""));
                }
                catch { }
            }
        }

        private static void DiscoverDlssSurfaces()
        {
            try
            {
                _nvidiaPluginType = FindType("UnityEngine.NVIDIA.NVUnityPlugin");
                _nvidiaGraphicsDeviceType = FindType("UnityEngine.NVIDIA.GraphicsDevice");
                _nvidiaQualityType = FindType("UnityEngine.NVIDIA.DLSSQuality");
                _nvidiaOptimalDataType = FindType("UnityEngine.NVIDIA.OptimalDLSSSettingsData");

                // V117 looked for GetOptimalSettings on NVUnityPlugin.  The shipped NVIDIAModule
                // actually exposes the PUBLIC query on GraphicsDevice.device; NVUnityPlugin owns
                // only the private native bridge NVUP_GetOptimalSettings.  Discover both so the
                // log proves which surface this exact game build exposes.
                if (_nvidiaPluginType != null)
                {
                    _nvidiaGetOptimalStatic = _nvidiaPluginType.GetMethods(
                            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "NVUP_GetOptimalSettings" && m.GetParameters().Length == 4);
                }

                if (_nvidiaGraphicsDeviceType != null)
                {
                    _nvidiaGetOptimalInstance = _nvidiaGraphicsDeviceType.GetMethods(
                            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "GetOptimalSettings" && m.GetParameters().Length == 4);
                    _nvidiaDeviceProperty = _nvidiaGraphicsDeviceType.GetProperty(
                        "device", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (_nvidiaDeviceProperty != null)
                    {
                        try { _nvidiaGraphicsDeviceInstance = _nvidiaDeviceProperty.GetValue(null, null); }
                        catch (Exception e) { WriteLine("NVIDIA_DEVICE_GET_FAIL " + e.GetType().Name + ": " + e.Message); }
                    }
                }

                WriteLine(
                    "NVIDIA_SURFACE plugin=" + (_nvidiaPluginType != null ? _nvidiaPluginType.FullName : "NOT_FOUND") +
                    " graphicsDevice=" + (_nvidiaGraphicsDeviceType != null ? _nvidiaGraphicsDeviceType.FullName : "NOT_FOUND") +
                    " device=" + SafeTypeName(_nvidiaGraphicsDeviceInstance) +
                    " quality=" + (_nvidiaQualityType != null ? _nvidiaQualityType.FullName : "NOT_FOUND") +
                    " optimalData=" + (_nvidiaOptimalDataType != null ? _nvidiaOptimalDataType.FullName : "NOT_FOUND") +
                    " nativeStatic=" + MethodLabel(_nvidiaGetOptimalStatic) +
                    " publicInstance=" + MethodLabel(_nvidiaGetOptimalInstance));

                if (_nvidiaQualityType != null && _nvidiaQualityType.IsEnum)
                {
                    try
                    {
                        foreach (string n in Enum.GetNames(_nvidiaQualityType))
                        {
                            object v = Enum.Parse(_nvidiaQualityType, n);
                            WriteLine("NVIDIA_ENUM DLSSQuality " + n + "=" + Convert.ToUInt32(v));
                        }
                    }
                    catch (Exception e)
                    {
                        WriteLine("NVIDIA_ENUM_FAIL " + e.GetType().Name + ": " + e.Message);
                    }
                }

                _sonsDlssManagerType = FindSonsDlssManagerType();
                WriteLine("SONS_DLSS_SURFACE manager=" + (_sonsDlssManagerType != null ? _sonsDlssManagerType.FullName : "NOT_FOUND"));

                DumpRelevantMethodSurfaceV118(_nvidiaGraphicsDeviceType, "NVIDIA_GRAPHICS_DEVICE_SURFACE",
                    new[] { "Optimal", "Feature", "DLSS", "device" });
                DumpRelevantMethodSurfaceV118(FindType("UnityEngine.Rendering.DynamicResolutionHandler"),
                    "DRS_HANDLER_SURFACE", new[] { "Scale", "Dynamic", "Resolution", "Request", "Viewport", "Update" });
            }
            catch (Exception e)
            {
                WriteLine("DLSS_DISCOVERY_FAIL " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void CaptureDlssSnapshot(string reason)
        {
            try
            {
                StereoRender stereo = VRPlayer.Instance != null ? VRPlayer.Instance.StereoRender : null;
                string actualMode = stereo != null ? stereo.GetActualRenderModeV112() : "NO_STEREO";
                WriteLine(
                    "DLSS_SNAPSHOT_BEGIN reason=" + reason +
                    " config=" + SonsVRMod.EnableV91RealDlss + "/" + SonsVRMod.V91DlssQuality +
                    " actualMode=" + actualMode +
                    " altEye=" + StereoRender.AlternateEyeRenderingEnabled +
                    " scalable=" + ScalableBufferManager.widthScaleFactor.ToString("F6") + "x" + ScalableBufferManager.heightScaleFactor.ToString("F6"));

                DumpSonsDlssManagerState();
                DumpPipelineDynamicResolutionV118(reason);
                DumpDynamicResolutionHandlerStateV118("update:" + reason, detailed: reason.Contains("initial") || reason.Contains("+0s"));
                DumpSonsDynamicResolutionReceiversV118(reason);
                RunTinyDynamicScaleProbeV118();

                int outW = 0;
                int outH = 0;
                if (stereo != null)
                {
                    DumpEye("L", stereo.LeftCam);
                    DumpEye("R", stereo.RightCam);
                    if (stereo.LeftCam != null && stereo.LeftCam.targetTexture != null)
                    {
                        outW = stereo.LeftCam.targetTexture.width;
                        outH = stereo.LeftCam.targetTexture.height;
                    }
                    else if (stereo.RightCam != null && stereo.RightCam.targetTexture != null)
                    {
                        outW = stereo.RightCam.targetTexture.width;
                        outH = stereo.RightCam.targetTexture.height;
                    }
                }

                if (outW > 0 && outH > 0)
                    DumpNvidiaOptimalSettings(outW, outH);
                else
                    WriteLine("NVIDIA_OPTIMAL skipped=no-eye-output-size");

                WriteLine("DLSS_SNAPSHOT_END reason=" + reason);
            }
            catch (Exception e)
            {
                WriteLine("DLSS_SNAPSHOT_FAIL reason=" + reason + " :: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void DumpEye(string eye, Camera cam)
        {
            if (cam == null)
            {
                WriteLine("DLSS_EYE eye=" + eye + " camera=null");
                return;
            }

            try
            {
                RenderTexture rt = cam.targetTexture;
                HDAdditionalCameraData data = null;
                try { data = cam.GetComponent<HDAdditionalCameraData>(); } catch { }

                int observedW, observedH, observedFrame;
                bool observed = EyeDepthCaptureCustomPassV81.TryGetObservedViewportV116(
                    eye == "L" ? 0 : 1,
                    out observedW,
                    out observedH,
                    out observedFrame);

                HDCamera hd = null;
                try { hd = HDCamera.GetOrCreate(cam, 0); } catch { }

                string hdData = data == null ? "hdAdditional=null" :
                    "hdAdditional=" +
                    "allowDLSS:" + ReadMember(data, "allowDeepLearningSuperSampling") + "," +
                    "allowDyn:" + ReadMember(data, "allowDynamicResolution") + "," +
                    "customQuality:" + ReadMember(data, "deepLearningSuperSamplingUseCustomQualitySettings") + "," +
                    "quality:" + ReadMember(data, "deepLearningSuperSamplingQuality") + "," +
                    "customAttrs:" + ReadMember(data, "deepLearningSuperSamplingUseCustomAttributes") + "," +
                    "optimal:" + ReadMember(data, "deepLearningSuperSamplingUseOptimalSettings") + "," +
                    "sharp:" + ReadMember(data, "deepLearningSuperSamplingSharpening") + "," +
                    "persistent:" + ReadMember(data, "hasPersistentHistory");

                string target = rt == null ? "target=null" :
                    "target=" + rt.width + "x" + rt.height +
                    " useDynamicScale=" + rt.useDynamicScale +
                    " descDyn=" + SafeRenderTextureDescriptorDynamicScaleV118(rt) +
                    " dynExplicit=" + ReadMember(rt, "useDynamicScaleExplicit") +
                    " created=" + rt.IsCreated();

                string actual = hd != null
                    ? "hdActual=" + hd.actualWidth + "x" + hd.actualHeight
                    : "hdActual=null";

                string observedText = observed
                    ? "observed=" + observedW + "x" + observedH + "@frame" + observedFrame
                    : "observed=n/a";

                WriteLine(
                    "DLSS_EYE eye=" + eye +
                    " camDyn=" + cam.allowDynamicResolution +
                    " scaledPixel=" + cam.scaledPixelWidth + "x" + cam.scaledPixelHeight +
                    " " + target +
                    " " + actual +
                    " " + observedText +
                    " " + hdData);
            }
            catch (Exception e)
            {
                WriteLine("DLSS_EYE_FAIL eye=" + eye + " :: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void DumpSonsDlssManagerState()
        {
            Type t = _sonsDlssManagerType ?? FindSonsDlssManagerType();
            if (t == null)
            {
                WriteLine("SONS_DLSS_STATE manager=NOT_FOUND");
                return;
            }

            StringBuilder b = new StringBuilder();
            b.Append("SONS_DLSS_STATE type=").Append(t.FullName);
            foreach (string field in new[]
            {
                "_activeDlssQualitySetting",
                "_activeDlssSetting",
                "_dynamicResolutionOption",
                "DlssSettingDefault",
                "DynamicResUpscaleSettingDefault",
                "DlssQualityString",
                "DlssBalancedString",
                "DlssPerformanceString",
                "DlssUltraPerformanceString"
            })
            {
                b.Append(' ').Append(field).Append('=').Append(ReadStaticMember(t, field));
            }

            b.Append(" GetDynamicResolutionOption=").Append(InvokeStaticZeroArg(t, "GetDynamicResolutionOption"));
            b.Append(" IsDlssSupported=").Append(InvokeStaticZeroArg(t, "IsDlssSupported"));
            WriteLine(b.ToString());
        }

        // V129: run the same read-only NVIDIA query AFTER the selected native
        // menu setting has settled in gameplay. Advisory values are not evidence
        // of which actual input lattice HDRP used; see V129 EYE_BEGIN/EYE_END TSV.
        internal static void DumpLiveOptimalSettingsV129(int outputWidth, int outputHeight, string reason)
        {
            try
            {
                if (!Enabled || outputWidth <= 0 || outputHeight <= 0) return;
                WriteLine("V129_LIVE_NVIDIA_QUERY reason=" + reason +
                    " output=" + outputWidth + "x" + outputHeight);
                DumpNvidiaOptimalSettings(outputWidth, outputHeight);
            }
            catch (Exception e)
            {
                WriteLine("V129_LIVE_NVIDIA_QUERY_FAIL " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void DumpNvidiaOptimalSettings(int outputWidth, int outputHeight)
        {
            if (_nvidiaQualityType == null || _nvidiaOptimalDataType == null)
            {
                WriteLine("NVIDIA_OPTIMAL unavailable=surface-not-found");
                return;
            }

            if (_nvidiaGraphicsDeviceInstance == null && _nvidiaDeviceProperty != null)
            {
                try { _nvidiaGraphicsDeviceInstance = _nvidiaDeviceProperty.GetValue(null, null); }
                catch (Exception e) { WriteLine("NVIDIA_DEVICE_GET_RETRY_FAIL " + e.GetType().Name + ": " + e.Message); }
            }

            foreach (string qualityName in new[]
            {
                "MaximumQuality",
                "Balanced",
                "MaximumPerformance",
                "UltraPerformance"
            })
            {
                try
                {
                    object quality = Enum.Parse(_nvidiaQualityType, qualityName, true);
                    object data = Activator.CreateInstance(_nvidiaOptimalDataType);
                    object result = null;
                    bool invoked = false;
                    string path = "none";

                    // Preferred path in this Unity 2022.2 NVIDIAModule: public instance query on
                    // GraphicsDevice.device.  V117 incorrectly looked for this method on
                    // NVUnityPlugin, which is why every quality returned unavailable=no-method.
                    if (_nvidiaGetOptimalInstance != null && _nvidiaGraphicsDeviceInstance != null)
                    {
                        object[] args = { (uint)outputWidth, (uint)outputHeight, quality, data };
                        result = _nvidiaGetOptimalInstance.Invoke(_nvidiaGraphicsDeviceInstance, args);
                        data = args[3];
                        invoked = true;
                        path = "GraphicsDevice.device.GetOptimalSettings";
                    }
                    else if (_nvidiaGetOptimalStatic != null)
                    {
                        object[] args = { (uint)outputWidth, (uint)outputHeight, quality, data };
                        result = _nvidiaGetOptimalStatic.Invoke(null, args);
                        data = args[3];
                        invoked = true;
                        path = "NVUnityPlugin.NVUP_GetOptimalSettings";
                    }

                    if (!invoked)
                    {
                        WriteLine("NVIDIA_OPTIMAL quality=" + qualityName + " unavailable=no-callable-surface");
                        continue;
                    }

                    string outRenderW = ReadFirstMember(data, "outRenderWidth", "m_OutRenderWidth");
                    string outRenderH = ReadFirstMember(data, "outRenderHeight", "m_OutRenderHeight");
                    string minW = ReadFirstMember(data, "minWidth", "m_MinWidth");
                    string minH = ReadFirstMember(data, "minHeight", "m_MinHeight");
                    string maxW = ReadFirstMember(data, "maxWidth", "m_MaxWidth");
                    string maxH = ReadFirstMember(data, "maxHeight", "m_MaxHeight");
                    string sharp = ReadFirstMember(data, "sharpness", "m_Sharpness");
                    string qualityField = ReadFirstMember(data, "quality", "m_Quality");

                    WriteLine(
                        "NVIDIA_OPTIMAL quality=" + qualityName +
                        " qualityCode=" + Convert.ToUInt32(quality) +
                        " output=" + outputWidth + "x" + outputHeight +
                        " path=" + path +
                        " ok=" + FormatSimple(result) +
                        " outRender=" + outRenderW + "x" + outRenderH +
                        " min=" + minW + "x" + minH +
                        " max=" + maxW + "x" + maxH +
                        " sharp=" + sharp +
                        " qualityField=" + qualityField);
                }
                catch (Exception e)
                {
                    WriteLine("NVIDIA_OPTIMAL_FAIL quality=" + qualityName + " :: " +
                        e.GetType().Name + ": " + e.Message +
                        (e.InnerException != null ? " inner=" + e.InnerException.GetType().Name + ":" + e.InnerException.Message : ""));
                }
            }
        }

        private static void InstallRenderBoundaryDrsProbeV118()
        {
            try
            {
                if (_beginCameraRenderingDelegateV118 == null)
                {
                    System.Action<ScriptableRenderContext, Camera> cb = OnBeginCameraRenderingV118;
                    _beginCameraRenderingDelegateV118 = cb;
                }
                if (_endCameraRenderingDelegateV118 == null)
                {
                    System.Action<ScriptableRenderContext, Camera> cb = OnEndCameraRenderingV118;
                    _endCameraRenderingDelegateV118 = cb;
                }

                try { RenderPipelineManager.remove_beginCameraRendering(_beginCameraRenderingDelegateV118); } catch { }
                try { RenderPipelineManager.remove_endCameraRendering(_endCameraRenderingDelegateV118); } catch { }
                RenderPipelineManager.add_beginCameraRendering(_beginCameraRenderingDelegateV118);
                RenderPipelineManager.add_endCameraRendering(_endCameraRenderingDelegateV118);
                WriteLine("DRS_BOUNDARY_HOOKS installed");
            }
            catch (Exception e)
            {
                WriteLine("DRS_BOUNDARY_HOOKS_FAIL " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void OnBeginCameraRenderingV118(ScriptableRenderContext context, Camera cam)
        {
            NativeDlssLiveResolutionTraceV129.OnEyeRenderBoundary("begin", cam); // V129 read-only live per-eye state
            SampleRenderBoundaryDrsV118("begin", cam);
        }

        private static void OnEndCameraRenderingV118(ScriptableRenderContext context, Camera cam)
        {
            NativeDlssLiveResolutionTraceV129.OnEyeRenderBoundary("end", cam); // V129 read-only live per-eye state
            SampleRenderBoundaryDrsV118("end", cam);
        }

        private static void SampleRenderBoundaryDrsV118(string phase, Camera cam)
        {
            if (_renderBoundaryBudgetV118 <= 0 || cam == null)
                return;

            StereoRender stereo = VRPlayer.Instance != null ? VRPlayer.Instance.StereoRender : null;
            if (stereo == null)
                return;

            string eye = null;
            if (stereo.LeftCam != null && cam == stereo.LeftCam) eye = "L";
            else if (stereo.RightCam != null && cam == stereo.RightCam) eye = "R";
            if (eye == null)
                return;

            int frame = Time.frameCount;
            if (phase == "begin")
            {
                if (eye == "L")
                {
                    if (_renderBoundaryLeftBeginFrameV118 == frame) return;
                    _renderBoundaryLeftBeginFrameV118 = frame;
                }
                else
                {
                    if (_renderBoundaryRightBeginFrameV118 == frame) return;
                    _renderBoundaryRightBeginFrameV118 = frame;
                }
            }
            else
            {
                if (eye == "L")
                {
                    if (_renderBoundaryLeftEndFrameV118 == frame) return;
                    _renderBoundaryLeftEndFrameV118 = frame;
                }
                else
                {
                    if (_renderBoundaryRightEndFrameV118 == frame) return;
                    _renderBoundaryRightEndFrameV118 = frame;
                }
            }

            _renderBoundaryBudgetV118--;
            DumpDynamicResolutionHandlerStateV118(
                "renderBoundary:" + phase + ":eye=" + eye + ":frame=" + frame,
                detailed: false);
        }

        private static void DumpDynamicResolutionHandlerStateV118(string reason, bool detailed)
        {
            try
            {
                Type t = FindType("UnityEngine.Rendering.DynamicResolutionHandler");
                if (t == null)
                {
                    WriteLine("DRS_HANDLER reason=" + reason + " type=NOT_FOUND");
                    return;
                }

                object inst = GetStaticRawMember(t, "instance");
                if (inst == null)
                {
                    WriteLine("DRS_HANDLER reason=" + reason + " instance=null");
                    return;
                }

                WriteLine(
                    "DRS_HANDLER reason=" + reason +
                    " type=" + inst.GetType().FullName +
                    " resolvedScale=" + InvokeZeroArgRawFormatted(inst, "GetResolvedScale") +
                    " finalViewport=" + ReadMember(inst, "finalViewport") +
                    " filter=" + ReadMember(inst, "filter") +
                    " requestsHW=" + InvokeZeroArgRawFormatted(inst, "RequestsHardwareDynamicResolution") +
                    " hwEnabled=" + InvokeZeroArgRawFormatted(inst, "HardwareDynamicResIsEnabled") +
                    " swEnabled=" + InvokeZeroArgRawFormatted(inst, "SoftwareDynamicResIsEnabled") +
                    " currentRequest=" + ReadFirstMember(inst, "m_CurrentCameraRequest", "m_CurrentCameraRequestEnabled", "m_CameraRequest") +
                    " currentFraction=" + ReadFirstMember(inst, "m_CurrentFraction", "m_CurrentScale", "m_ResolvedFraction") +
                    " forceSoftware=" + ReadFirstMember(inst, "m_ForceSoftwareFallback", "m_ForceSoftwareDynamicRes"));

                if (detailed)
                {
                    DumpMatchingFieldsV118(inst, "DRS_HANDLER_FIELD", new[]
                    {
                        "scale", "fraction", "request", "enabled", "viewport", "filter", "dynamic", "camera", "force"
                    }, 48);
                }
            }
            catch (Exception e)
            {
                WriteLine("DRS_HANDLER_FAIL reason=" + reason + " :: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void DumpPipelineDynamicResolutionV118(string reason)
        {
            try
            {
                Type gsType = typeof(GraphicsSettings);
                object asset = GetStaticRawMember(gsType, "currentRenderPipeline") ??
                               GetStaticRawMember(gsType, "renderPipelineAsset") ??
                               GetStaticRawMember(gsType, "defaultRenderPipeline");
                if (asset == null)
                {
                    WriteLine("PIPELINE_DRS reason=" + reason + " asset=null");
                    return;
                }

                object platform = GetRawMember(asset, "currentPlatformRenderPipelineSettings") ??
                                  GetRawMember(asset, "renderPipelineSettings");
                object drs = platform != null ?
                    (GetRawMember(platform, "dynamicResolutionSettings") ?? GetRawMember(platform, "m_DynamicResolutionSettings")) : null;

                WriteLine(
                    "PIPELINE_DRS reason=" + reason +
                    " asset=" + asset.GetType().FullName +
                    " settings=" + SafeTypeName(platform) +
                    " drs=" + SafeTypeName(drs) +
                    " enabled=" + ReadFirstMember(drs, "enabled") +
                    " minPct=" + ReadFirstMember(drs, "minPercentage", "minScreenPercentage") +
                    " maxPct=" + ReadFirstMember(drs, "maxPercentage", "maxScreenPercentage") +
                    " forceResolution=" + ReadFirstMember(drs, "forceResolution") +
                    " forcedPct=" + ReadFirstMember(drs, "forcedPercentage") +
                    " dynResType=" + ReadFirstMember(drs, "dynResType") +
                    " filter=" + ReadFirstMember(drs, "upscaleFilter") +
                    " enableDLSS=" + ReadFirstMember(drs, "enableDLSS") +
                    " dlssQuality=" + ReadFirstMember(drs, "DLSSPerfQualitySetting") +
                    " dlssOptimal=" + ReadFirstMember(drs, "DLSSUseOptimalSettings") +
                    " dlssSharp=" + ReadFirstMember(drs, "DLSSSharpness") +
                    " mipBias=" + ReadFirstMember(drs, "useMipBias") +
                    " injection=" + ReadFirstMember(drs, "DLSSInjectionPoint"));

                if (!_surfaceInventoryDoneV118)
                {
                    _surfaceInventoryDoneV118 = true;
                    DumpMatchingFieldsV118(drs, "PIPELINE_DRS_FIELD", new[]
                    {
                        "dynamic", "resolution", "percentage", "dlss", "scale", "filter", "upscale", "force"
                    }, 64);
                }
            }
            catch (Exception e)
            {
                WriteLine("PIPELINE_DRS_FAIL reason=" + reason + " :: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void DumpSonsDynamicResolutionReceiversV118(string reason)
        {
            Type t = _sonsDlssManagerType ?? FindSonsDlssManagerType();
            if (t == null)
                return;

            try
            {
                object collection = GetStaticRawMember(t, "_dynamicResolutionSettingReceivers");
                if (collection == null)
                {
                    WriteLine("SONS_DRS_RECEIVERS reason=" + reason + " collection=null");
                    return;
                }

                List<object> items = EnumerateCollectionV118(collection, 32);
                WriteLine("SONS_DRS_RECEIVERS reason=" + reason + " count=" + items.Count +
                    " rawCount=" + ReadMember(collection, "Count") +
                    " collectionType=" + collection.GetType().FullName);

                StereoRender stereo = VRPlayer.Instance != null ? VRPlayer.Instance.StereoRender : null;
                for (int i = 0; i < items.Count; ++i)
                {
                    object receiver = items[i];
                    object targetObj = GetRawMember(receiver, "_targetCamera");
                    Camera targetCam = targetObj as Camera;
                    object hd = GetRawMember(receiver, "_hdAdditionalCameraData");
                    string role = "other";
                    if (stereo != null && targetCam != null)
                    {
                        if (stereo.LeftCam != null && targetCam == stereo.LeftCam) role = "VRLeftEye";
                        else if (stereo.RightCam != null && targetCam == stereo.RightCam) role = "VRRightEye";
                    }
                    if (targetCam != null && targetCam.name == "MainCameraFP") role = "MainCameraFP";

                    WriteLine(
                        "SONS_DRS_RECEIVER i=" + i +
                        " type=" + SafeTypeName(receiver) +
                        " role=" + role +
                        " target=" + SafeUnityObjectLabelV118(targetObj) +
                        " camDyn=" + (targetCam != null ? targetCam.allowDynamicResolution.ToString() : "n/a") +
                        " hdAllowDyn=" + ReadFirstMember(hd, "allowDynamicResolution") +
                        " hdAllowDLSS=" + ReadFirstMember(hd, "allowDeepLearningSuperSampling") +
                        " hdCustomQuality=" + ReadFirstMember(hd, "deepLearningSuperSamplingUseCustomQualitySettings") +
                        " hdQuality=" + ReadFirstMember(hd, "deepLearningSuperSamplingQuality") +
                        " hdOptimal=" + ReadFirstMember(hd, "deepLearningSuperSamplingUseOptimalSettings") +
                        " QualitySettingValue=" + ReadFirstMember(receiver, "QualitySettingValue") +
                        " BalancedSettingValue=" + ReadFirstMember(receiver, "BalancedSettingValue") +
                        " PerformanceSettingValue=" + ReadFirstMember(receiver, "PerformanceSettingValue") +
                        " UltraPerformanceSettingValue=" + ReadFirstMember(receiver, "UltraPerformanceSettingValue"));
                }
            }
            catch (Exception e)
            {
                WriteLine("SONS_DRS_RECEIVERS_FAIL reason=" + reason + " :: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void RunTinyDynamicScaleProbeV118()
        {
            if (_dynamicScaleProbeDoneV118)
                return;
            _dynamicScaleProbeDoneV118 = true;

            RenderTexture probe = null;
            try
            {
                RenderTextureDescriptor desc = new RenderTextureDescriptor(16, 16, RenderTextureFormat.ARGB32, 0);
                desc.msaaSamples = 1;
                desc.useDynamicScale = true;
                probe = new RenderTexture(desc);
                bool beforeCreate = probe.useDynamicScale;
                string descBefore = SafeRenderTextureDescriptorDynamicScaleV118(probe);
                bool created = probe.Create();
                bool afterCreate = probe.useDynamicScale;
                string descAfter = SafeRenderTextureDescriptorDynamicScaleV118(probe);
                WriteLine(
                    "DYNAMIC_RT_PROBE descriptorRequested=True beforeCreate=" + beforeCreate +
                    " descriptorBefore=" + descBefore +
                    " createOk=" + created +
                    " afterCreate=" + afterCreate +
                    " descriptorAfter=" + descAfter);
            }
            catch (Exception e)
            {
                WriteLine("DYNAMIC_RT_PROBE_FAIL " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                try
                {
                    if (probe != null)
                    {
                        if (probe.IsCreated()) probe.Release();
                        UnityEngine.Object.Destroy(probe);
                    }
                }
                catch { }
            }
        }

        private static string SafeRenderTextureDescriptorDynamicScaleV118(RenderTexture rt)
        {
            if (rt == null) return "n/a";
            try
            {
                RenderTextureDescriptor d = rt.descriptor;
                return d.useDynamicScale.ToString();
            }
            catch (Exception e)
            {
                return "<error:" + e.GetType().Name + ">";
            }
        }

        private static void DumpRelevantMethodSurfaceV118(Type t, string prefix, string[] needles)
        {
            if (t == null)
            {
                WriteLine(prefix + " type=NOT_FOUND");
                return;
            }

            try
            {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    string n = m.Name ?? "";
                    bool keep = needles == null || needles.Length == 0 || needles.Any(x => n.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (keep)
                        WriteLine(prefix + " " + MethodLabel(m) + " static=" + m.IsStatic + " return=" + SafeTypeName(m.ReturnType));
                }
            }
            catch (Exception e)
            {
                WriteLine(prefix + " FAIL " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void DumpMatchingFieldsV118(object obj, string prefix, string[] needles, int max)
        {
            if (obj == null) return;
            try
            {
                int count = 0;
                Type t = obj.GetType();
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (count >= max) break;
                    string n = f.Name ?? "";
                    if (needles != null && needles.Length > 0 && !needles.Any(x => n.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0))
                        continue;
                    object value = null;
                    string formatted;
                    try
                    {
                        value = f.GetValue(f.IsStatic ? null : obj);
                        formatted = FormatSimple(value);
                    }
                    catch (Exception e) { formatted = "<error:" + e.GetType().Name + ">"; }
                    WriteLine(prefix + " owner=" + t.FullName + " name=" + n + " value=" + formatted);
                    count++;
                }
            }
            catch (Exception e)
            {
                WriteLine(prefix + " INVENTORY_FAIL " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static List<object> EnumerateCollectionV118(object collection, int max)
        {
            List<object> result = new List<object>();
            if (collection == null || max <= 0) return result;

            try
            {
                System.Collections.IEnumerable enumerable = collection as System.Collections.IEnumerable;
                if (enumerable != null)
                {
                    foreach (object x in enumerable)
                    {
                        result.Add(x);
                        if (result.Count >= max) break;
                    }
                    return result;
                }
            }
            catch { }

            try
            {
                int count = 0;
                object countRaw = GetRawMember(collection, "Count");
                if (countRaw != null) count = Convert.ToInt32(countRaw);
                MethodInfo item = collection.GetType().GetMethod("get_Item",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(int) }, null);
                if (item != null)
                {
                    for (int i = 0; i < Math.Min(count, max); ++i)
                        result.Add(item.Invoke(collection, new object[] { i }));
                }
            }
            catch { }
            return result;
        }

        private static object GetRawMember(object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name)) return null;
            Type t = obj is Type ? (Type)obj : obj.GetType();
            bool staticOnly = obj is Type;
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                 (staticOnly ? BindingFlags.Static : (BindingFlags.Instance | BindingFlags.Static));
            try
            {
                FieldInfo f = t.GetField(name, flags);
                if (f != null) return f.GetValue(f.IsStatic ? null : obj);
            }
            catch { }
            try
            {
                PropertyInfo p = t.GetProperty(name, flags);
                if (p != null && p.GetIndexParameters().Length == 0)
                {
                    MethodInfo getter = p.GetGetMethod(true);
                    return p.GetValue(getter != null && getter.IsStatic ? null : obj, null);
                }
            }
            catch { }
            return null;
        }

        private static object GetStaticRawMember(Type t, string name)
        {
            return t == null ? null : GetRawMember(t, name);
        }

        private static string ReadFirstMember(object obj, params string[] names)
        {
            if (obj == null) return "<null>";
            foreach (string n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                string value = ReadMember(obj, n);
                if (value != "<missing>") return value;
            }
            return "<missing>";
        }

        private static string InvokeZeroArgRawFormatted(object obj, string name)
        {
            if (obj == null) return "<null>";
            try
            {
                MethodInfo m = obj.GetType().GetMethod(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                return m == null ? "<missing>" : FormatSimple(m.Invoke(obj, null));
            }
            catch (Exception e)
            {
                return "<error:" + e.GetType().Name + ">";
            }
        }

        private static string SafeTypeName(object obj)
        {
            if (obj == null) return "<null>";
            try
            {
                Type t = obj as Type;
                if (t != null) return t.FullName ?? t.Name;
                return obj.GetType().FullName ?? obj.GetType().Name;
            }
            catch { return "<type-error>"; }
        }

        private static string SafeUnityObjectLabelV118(object obj)
        {
            if (obj == null) return "<null>";
            try
            {
                UnityEngine.Object uo = obj as UnityEngine.Object;
                if (uo != null)
                    return uo.name + "#" + uo.GetInstanceID() + "(" + SafeTypeName(obj) + ")";
            }
            catch { }
            return FormatSimple(obj);
        }

        private static Type FindSonsDlssManagerType()
        {
            if (_sonsDlssManagerType != null)
                return _sonsDlssManagerType;

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = SafeAssemblyName(asm);
                if (n.IndexOf("Sons.Dlss", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (Type t in types)
                {
                    if (t == null) continue;
                    try
                    {
                        MethodInfo[] ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (ms.Any(m => m.Name == "ApplyDlssSetting") &&
                            ms.Any(m => m.Name == "IsDlssSupported"))
                        {
                            _sonsDlssManagerType = t;
                            return t;
                        }
                    }
                    catch { }
                }
            }
            return null;
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

        private static string ReadMember(object obj, string name)
        {
            if (obj == null) return "<null>";
            Type t = obj.GetType();
            try
            {
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (f != null) return FormatSimple(f.GetValue(f.IsStatic ? null : obj));
            }
            catch (Exception e) { return "<field-error:" + e.GetType().Name + ">"; }

            try
            {
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (p != null && p.GetIndexParameters().Length == 0)
                {
                    MethodInfo getter = p.GetGetMethod(true);
                    return FormatSimple(p.GetValue(getter != null && getter.IsStatic ? null : obj, null));
                }
            }
            catch (Exception e) { return "<prop-error:" + e.GetType().Name + ">"; }
            return "<missing>";
        }

        private static string ReadStaticMember(Type t, string name)
        {
            if (t == null) return "<type-null>";
            try
            {
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null) return FormatSimple(f.GetValue(null));
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (p != null && p.GetIndexParameters().Length == 0) return FormatSimple(p.GetValue(null, null));
            }
            catch (Exception e) { return "<error:" + e.GetType().Name + ">"; }
            return "<missing>";
        }

        private static string InvokeStaticZeroArg(Type t, string name)
        {
            try
            {
                MethodInfo m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                return m == null ? "<missing>" : FormatSimple(m.Invoke(null, null));
            }
            catch (Exception e) { return "<error:" + e.GetType().Name + ">"; }
        }

        private static string FormatSimple(object value)
        {
            if (value == null) return "<null>";
            try
            {
                if (value is string s) return s.Replace("\r", "\\r").Replace("\n", "\\n");
                if (value is float f) return f.ToString("F6");
                if (value is double d) return d.ToString("F6");
                if (value is IFormattable formattable) return formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
                return value.ToString();
            }
            catch { return "<format-error>"; }
        }

        private static string MethodLabel(MethodBase m)
        {
            if (m == null) return "<null>";
            try
            {
                string owner = m.DeclaringType != null ? (m.DeclaringType.FullName ?? m.DeclaringType.Name) : "<no-type>";
                string args = string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name));
                return owner + "." + m.Name + "(" + args + ")";
            }
            catch { return m.Name ?? "<method>"; }
        }

        private static string SafeAssemblyName(Assembly asm)
        {
            try { return asm.GetName().Name ?? ""; }
            catch { return ""; }
        }

        private static string SafeActiveSceneName()
        {
            try { return SceneManager.GetActiveScene().name ?? "<unnamed>"; }
            catch { return "<scene-error>"; }
        }

        private static string SafeSceneName(Scene scene)
        {
            try { return scene.name ?? "<unnamed>"; }
            catch { return "<scene-error>"; }
        }

        private static void WriteLine(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [main] " + message;
            WriteLineFromAnyThreadRaw(line);
            try
            {
                if (message.StartsWith("TITLE_TRANSITION_ARM", StringComparison.Ordinal) ||
                    message.StartsWith("ENTER ", StringComparison.Ordinal) ||
                    message.StartsWith("EXIT ", StringComparison.Ordinal) ||
                    message.StartsWith("DLSS_SNAPSHOT_BEGIN", StringComparison.Ordinal))
                {
                    MelonLogger.Msg("[V117 Truth] " + message);
                }
            }
            catch { }
        }

        private static void WriteLineFromAnyThread(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [watchdog] " + message;
            WriteLineFromAnyThreadRaw(line);
        }

        private static void WriteLineFromAnyThreadRaw(string line)
        {
            try
            {
                lock (FileLock)
                {
                    _writer?.WriteLine(line);
                    _writer?.Flush();
                }
            }
            catch { }
        }
    }
}
