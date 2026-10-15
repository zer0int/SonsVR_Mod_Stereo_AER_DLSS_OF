using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace SonsVRMod
{
    /// <summary>
    /// V138: extend V128's proven authored native DLSS receiver route to AER.
    /// One existing native MainCameraFP receiver is retargeted to the left eye; exactly
    /// one new (normally activated) native receiver is attached to a live host for right.
    /// Game menu owns all quality/option changes. NO manager quality/filter setters.
    /// On invalidation / scene teardown, restore the authored receiver and deactivate our host.
    /// This is strictly distinct from the V116 AER reconstruction/viewport policy.
    /// </summary>
    internal static class NativeDlssReceiverRouterV128
    {
        internal static bool Enabled => true;
        internal static bool Routed => _routed;
        private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                                     BindingFlags.Instance | BindingFlags.Static;
        private static Type _managerType;
        private static Type _receiverType;
        private static object _original;
        private static Camera _originalCamera;
        private static object _originalHd;
        private static bool _mainCamDynBefore;
        private static bool _mainHdDynBefore;
        private static bool _mainDlssBefore;
        private static GameObject _rightHost;
        private static object _rightReceiver;
        private static int _leftId;
        private static int _rightId;
        private static bool _routed;
        private static float _nextAttempt;
        private static float _nextHealth;
        private static float _nextWaitingLog;
        private static int _failures;
        private static string _lastReason = "not initialized";

        internal static void Tick(StereoRender stereo)
        {
            // V138: the same authored Endnight receiver pair now drives BOTH 2E and AER.
            // This is a camera/quality routing change only. We never mutate AER depth
            // reconstruction, shader parameters, scaler slots or render targets here.
            if (stereo == null || stereo.LeftCam == null || stereo.RightCam == null) return;
            float now = Time.unscaledTime;
            if (_routed)
            {
                if (now < _nextHealth) return;
                _nextHealth = now + 2.0f; // small registered-list census, not scene search
                try
                {
                    List<object> list = RegisteredReceivers();
                    if (stereo.LeftCam.GetInstanceID() != _leftId ||
                        stereo.RightCam.GetInstanceID() != _rightId ||
                        !ContainsReceiver(list, _original, stereo.LeftCam) ||
                        !ContainsReceiver(list, _rightReceiver, stereo.RightCam) ||
                        _rightHost == null || !_rightHost.activeInHierarchy)
                    {
                        Restore("native receiver or eye-camera identity changed");
                        _nextAttempt = now + 5.0f;
                        return;
                    }
                    if (now >= _nextWaitingLog)
                    {
                        _nextWaitingLog = now + 8.0f;
                        HDAdditionalCameraData observedLeftHd = Read(_original, "_hdAdditionalCameraData") as HDAdditionalCameraData;
                        HDAdditionalCameraData observedRightHd = Read(_rightReceiver, "_hdAdditionalCameraData") as HDAdditionalCameraData;
                        HDAdditionalCameraData expectedLeftHd = stereo.LeftCam.GetComponent<HDAdditionalCameraData>();
                        HDAdditionalCameraData expectedRightHd = stereo.RightCam.GetComponent<HDAdditionalCameraData>();
                        bool leftCacheMatches = observedLeftHd != null && expectedLeftHd != null &&
                            observedLeftHd.GetInstanceID() == expectedLeftHd.GetInstanceID();
                        bool rightCacheMatches = observedRightHd != null && expectedRightHd != null &&
                            observedRightHd.GetInstanceID() == expectedRightHd.GetInstanceID();
                        MelonLogger.Msg("[V128 NativeDLSS] ROUTE HEALTHY: registered=" + list.Count +
                            " leftHdCacheMatches=" + leftCacheMatches +
                            " rightHdCacheMatches=" + rightCacheMatches +
                            " leftReceiverQuality=" + Read(_original, "_activeDlssSetting") +
                            " rightReceiverQuality=" + Read(_rightReceiver, "_activeDlssSetting"));
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("[V128 NativeDLSS] health exception: " + e.GetType().Name + ": " + e.Message);
                    Restore("health exception");
                    _nextAttempt = now + 5.0f;
                }
                return;
            }
            if (now < _nextAttempt) return;
            _nextAttempt = now + 2.0f;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                if (Discover() && TryRoute(stereo.LeftCam, stereo.RightCam))
                {
                    _failures = 0;
                    _routed = true;
                    _leftId = stereo.LeftCam.GetInstanceID();
                    _rightId = stereo.RightCam.GetInstanceID();
                    _nextHealth = now + 2.0f;
                    _nextWaitingLog = now + 8.0f;
                    MelonLogger.Warning("[V138 AER-DLSS] ROUTER ACTIVE mode=" +
                        (StereoRender.UserGameplayAERV112 ? "AER" : "2E") +
                        " authored MainCameraFP receiver -> VRLeftEye; live native right receiver -> VRRightEye; " +
                        "native menu owns quality; no manager setters.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[V128 NativeDLSS] ROUTE ERROR: " + e.GetType().Name + ": " + e.Message);
                Restore("route exception");
                ++_failures;
                _nextAttempt = now + Math.Min(30.0f, 5.0f * _failures);
            }
            finally
            {
                sw.Stop();
                if (sw.Elapsed.TotalMilliseconds > 4.0)
                    MelonLogger.Warning("[V128 NativeDLSS] route pass ms=" + sw.Elapsed.TotalMilliseconds.ToString("F2"));
            }
        }

        private static bool Discover()
        {
            if (_managerType != null && _receiverType != null) return true;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = "";
                try { name = asm.GetName().Name ?? ""; } catch { }
                if (name.IndexOf("Sons.Dlss", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    _managerType = _managerType ?? asm.GetType("Il2CppSons.Dlss.DynamicResUpscaleManager", false);
                    _receiverType = _receiverType ?? asm.GetType("Il2CppAssemblies.Sons.Dlss.DynamicResolutionSettingReceiver", false);
                }
                catch { }
            }
            if (_managerType == null || _receiverType == null) return false;
            MelonLogger.Msg("[V128 NativeDLSS] wrappers: " + _managerType.FullName + " and " + _receiverType.FullName);
            return true;
        }

        private static bool TryRoute(Camera left, Camera right)
        {
            HDAdditionalCameraData leftHd = left.GetComponent<HDAdditionalCameraData>();
            HDAdditionalCameraData rightHd = right.GetComponent<HDAdditionalCameraData>();
            if (leftHd == null || rightHd == null) return Wait("waiting for eye HDRP camera components");

            List<object> list = RegisteredReceivers();
            if (list.Count == 0) return Wait("no native receivers registered yet");

            object authored = null;
            Camera originalCamera = null;
            foreach (object receiver in list)
            {
                Camera target = Read(receiver, "_targetCamera") as Camera;
                if (target != null && target.name == "MainCameraFP")
                {
                    authored = receiver;
                    originalCamera = target;
                    break;
                }
            }
            if (authored == null || originalCamera == null)
                return Wait("waiting for the registered, authored MainCameraFP receiver");
            // Important: do not mutate the real receiver before the new live right receiver
            // has registered successfully. This avoids the V126 inactive-host problem.
            _original = authored;
            _originalCamera = originalCamera;
            _originalHd = Read(authored, "_hdAdditionalCameraData");
            _mainCamDynBefore = originalCamera.allowDynamicResolution;
            HDAdditionalCameraData originalCameraHd = originalCamera.GetComponent<HDAdditionalCameraData>();
            _mainHdDynBefore = originalCameraHd != null && originalCameraHd.allowDynamicResolution;
            _mainDlssBefore = originalCameraHd != null && originalCameraHd.allowDeepLearningSuperSampling;

            try
            {
                _rightHost = new GameObject("__SonsVR_V128_Active_NativeDLSS_RightEye");
                _rightHost.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_rightHost);
                Component raw = _rightHost.AddComponent(Il2CppType.From(_receiverType));
                _rightReceiver = CastReceiver(raw);
                if (_rightReceiver == null) throw new InvalidOperationException("IL2CPP Cast of native right receiver failed");
                if (!Write(_rightReceiver, "_targetCamera", right) ||
                    !Write(_rightReceiver, "_hdAdditionalCameraData", rightHd))
                    throw new InvalidOperationException("right receiver camera/cache field write failed");
                foreach (string field in new[] {
                    "QualitySettingValue", "BalancedSettingValue", "PerformanceSettingValue", "UltraPerformanceSettingValue" })
                {
                    object value = Read(authored, field);
                    if (value == null || !Write(_rightReceiver, field, value))
                        throw new InvalidOperationException("could not copy Endnight mapping: " + field);
                }
                // Keep native debug readout unowned by the clone: don't route UI references
                // or reproduce the debug canvas on both eyes. The original keeps its wiring.
                _rightHost.SetActive(true); // native OnEnable now executes with VALID target + mapping
                list = RegisteredReceivers();
                if (!ContainsReceiver(list, _rightReceiver, right))
                    throw new InvalidOperationException("right receiver OnEnable did not register with the native manager");

                if (!Write(authored, "_targetCamera", left) ||
                    !Write(authored, "_hdAdditionalCameraData", leftHd))
                    throw new InvalidOperationException("authored left receiver camera/cache retarget failed");
                // Receiver-local initialization only; NEVER ApplyDlssSetting on the global manager.
                // Normal active OnEnable/LateUpdate handles subsequent game-menu updates.
                RefreshNativeReceiver(authored);
                RefreshNativeReceiver(_rightReceiver);
                list = RegisteredReceivers();
                if (!ContainsReceiver(list, authored, left) || !ContainsReceiver(list, _rightReceiver, right))
                    throw new InvalidOperationException("two-eye post-retarget registration validation failed");

                // The native camera stays enabled for Camera.main and interaction logic.
                // It must not pay for temporal DLSS rendering after the receiver moves.
                HDAdditionalCameraData nativeHd = originalCamera.GetComponent<HDAdditionalCameraData>();
                if (nativeHd != null)
                {
                    nativeHd.allowDynamicResolution = false;
                    nativeHd.allowDeepLearningSuperSampling = false;
                }
                originalCamera.allowDynamicResolution = false;

                MelonLogger.Warning("[V128 NativeDLSS] native mapping copied Q=" + Read(authored, "QualitySettingValue") +
                    " B=" + Read(authored, "BalancedSettingValue") +
                    " P=" + Read(authored, "PerformanceSettingValue") +
                    " UP=" + Read(authored, "UltraPerformanceSettingValue") +
                    "; right OnEnable auto-registered, original retargeted; current managerQ=" +
                    ReadStatic(_managerType, "_activeDlssQualitySetting") + ".");
                return true;
            }
            catch
            {
                Restore("transaction rollback");
                throw;
            }
        }

        private static void RefreshNativeReceiver(object receiver)
        {
            foreach (string name in new[] { "RefreshActiveSetting", "RefreshDlssActiveSetting" })
            {
                try
                {
                    MethodInfo method = _receiverType.GetMethod(name, Flags, null, Type.EmptyTypes, null);
                    if (method != null) method.Invoke(receiver, null);
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("[V128 NativeDLSS] " + name + " warning: " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        internal static void Restore(string reason)
        {
            // Never search the scene; only revert our own known receiver/host.
            _routed = false;
            try
            {
                if (_original != null && _originalCamera != null)
                {
                    Write(_original, "_targetCamera", _originalCamera);
                    // Restore even a null cache: a previously uncached native receiver must
                    // not retain the VR-left HD component after returning to pancake/AER.
                    Write(_original, "_hdAdditionalCameraData", _originalHd);
                    RefreshNativeReceiver(_original);
                    // Restore the precise prior eligibility state. In particular, the V116
                    // desktop-dummy optimization must not silently change after 2E -> AER.
                    _originalCamera.allowDynamicResolution = _mainCamDynBefore;
                    HDAdditionalCameraData originalCameraHd = _originalCamera.GetComponent<HDAdditionalCameraData>();
                    if (originalCameraHd != null)
                    {
                        originalCameraHd.allowDynamicResolution = _mainHdDynBefore;
                        originalCameraHd.allowDeepLearningSuperSampling = _mainDlssBefore;
                    }
                }
            }
            catch (Exception e) { MelonLogger.Warning("[V128 NativeDLSS] authored receiver restore: " + e.Message); }
            try
            {
                if (_rightHost != null)
                {
                    _rightHost.SetActive(false); // real OnDisable -> native Unregister
                    UnityEngine.Object.Destroy(_rightHost);
                }
            }
            catch (Exception e) { MelonLogger.Warning("[V128 NativeDLSS] right host shutdown: " + e.Message); }
            _rightHost = null;
            _rightReceiver = null;
            _original = null;
            _originalCamera = null;
            _originalHd = null;
            _leftId = 0;
            _rightId = 0;
            MelonLogger.Warning("[V128 NativeDLSS] ROLLBACK / native restore: " + reason);
        }

        internal static void OnSceneLoaded()
        {
            // Sons streams additive scenes during play. Never tear down a working route
            // merely because some unrelated scene loaded; revalidate on the next Tick.
            if (_routed)
            {
                _nextHealth = 0f;
                return;
            }
            _nextAttempt = Time.unscaledTime + 3.0f;
        }

        private static bool Wait(string message)
        {
            if (Time.unscaledTime >= _nextWaitingLog)
            {
                _nextWaitingLog = Time.unscaledTime + 6.0f;
                MelonLogger.Msg("[V128 NativeDLSS] " + message + " (no scene scans or manager writes)");
            }
            return false;
        }

        private static object CastReceiver(Component raw)
        {
            if (raw == null) return null;
            if (_receiverType.IsInstanceOfType(raw)) return raw;
            MethodInfo cast = typeof(Il2CppObjectBase).GetMethod("Cast")?.MakeGenericMethod(_receiverType);
            return cast?.Invoke(raw, null);
        }

        private static object ReadStatic(Type type, string name)
        {
            if (type == null) return null;
            return Read(type, name);
        }

        private static object Read(object obj, string name)
        {
            if (obj == null) return null;
            Type type = obj as Type ?? obj.GetType();
            bool isType = obj is Type;
            try
            {
                PropertyInfo p = type.GetProperty(name, Flags);
                if (p != null && p.GetIndexParameters().Length == 0)
                    return p.GetValue(isType ? null : obj, null);
            }
            catch { }
            try
            {
                FieldInfo field = type.GetField(name, Flags);
                if (field != null) return field.GetValue(isType || field.IsStatic ? null : obj);
            }
            catch { }
            return null;
        }

        private static bool Write(object obj, string name, object value)
        {
            if (obj == null) return false;
            Type type = obj.GetType();
            try
            {
                PropertyInfo p = type.GetProperty(name, Flags);
                if (p != null && p.CanWrite && p.GetIndexParameters().Length == 0)
                {
                    p.SetValue(obj, value, null);
                    return true;
                }
            }
            catch { }
            try
            {
                FieldInfo field = type.GetField(name, Flags);
                if (field != null && !field.IsStatic)
                {
                    field.SetValue(obj, value);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static List<object> RegisteredReceivers()
        {
            List<object> result = new List<object>();
            object list = ReadStatic(_managerType, "_dynamicResolutionSettingReceivers");
            if (list == null) return result;
            // V118/V127-proven IL2CPP collection paths: IEnumerable or get_Item(int).
            try
            {
                IEnumerable enumerable = list as IEnumerable;
                if (enumerable != null)
                {
                    foreach (object item in enumerable)
                    {
                        if (item != null) result.Add(item);
                        if (result.Count >= 32) break;
                    }
                    return result;
                }
            }
            catch { result.Clear(); }
            try
            {
                object countRaw = Read(list, "Count");
                if (countRaw == null) return result;
                int count = Math.Min(Convert.ToInt32(countRaw), 32);
                MethodInfo indexer = list.GetType().GetMethod("get_Item", Flags, null, new[] { typeof(int) }, null);
                if (indexer == null) return result;
                for (int i = 0; i < count; i++)
                {
                    object item = indexer.Invoke(list, new object[] { i });
                    if (item != null) result.Add(item);
                }
            }
            catch { result.Clear(); }
            return result;
        }

        private static bool ContainsReceiver(List<object> items, object receiver, Camera target)
        {
            if (items == null || receiver == null || target == null) return false;
            // Comparing IL2CPP native Object pointer identity via GetInstanceID rather than
            // managed-wrapper reference equality: enumeration may create new CLR wrappers.
            int id = ComponentId(receiver);
            if (id == 0) return false;
            foreach (object item in items)
            {
                if (ComponentId(item) != id) continue;
                Camera c = Read(item, "_targetCamera") as Camera;
                if (c != null && c.GetInstanceID() == target.GetInstanceID()) return true;
            }
            return false;
        }

        private static int ComponentId(object receiver)
        {
            try
            {
                Component nativeComponent = receiver as Component;
                if (nativeComponent != null) return nativeComponent.GetInstanceID();
                MethodInfo getId = receiver.GetType().GetMethod("GetInstanceID", Flags,
                    null, Type.EmptyTypes, null);
                return getId == null ? 0 : Convert.ToInt32(getId.Invoke(receiver, null));
            }
            catch { return 0; }
        }
    }
}
