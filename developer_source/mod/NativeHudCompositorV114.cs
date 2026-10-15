using MelonLoader;
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;
using Valve.VR;
using Mathf = UnityEngine.Mathf;

namespace SonsVRMod
{
    /// <summary>
    /// V114: preserve SOTF's native 2D HUD contract, but fix V113's capture-camera mistakes.
    ///
    /// V113 proved the architecture but put the ScreenSpaceCamera capture at (100000,100000,100000),
    /// which gives float32 coordinate steps of ~0.0078125 world units. With orthographicSize=1 at
    /// 1080p that is roughly four UI pixels per representable step -- catastrophic for text glyph
    /// vertices. V114 keeps the capture camera isolated, but at a sane coordinate and with a tight
    /// clip range.
    ///
    /// V113 also used cullingMask=-1. V114 computes the union of layers actually used by Canvas/
    /// CanvasRenderer objects under the two native HUD roots. The camera additionally has HDRP
    /// volume influence disabled and an explicit transparent constant clear.
    ///
    /// Before the OpenVR overlay is ever shown, a hidden two-stage preflight samples the actual
    /// capture RenderTexture:
    ///   A) cullingMask=0, volumeLayerMask=0  -> detects sky/fullscreen/background contamination
    ///   B) HUD-only culling mask            -> measures the real HUD sheet including alpha
    /// Sampling is downscaled to 64x36 first, so the one-shot synchronous readback is tiny and
    /// cannot become a recurring performance tax.
    ///
    /// The physically-held GPS remains a separate, genuine WorldSpace object and is never touched.
    /// </summary>
    internal static class NativeHudCompositorV114
    {
        internal static bool Enabled = false; // V115: retired; HDRP capture forced alpha=1 and broke interactive root semantics.

        internal const float OverlayDistanceMeters = 1.20f;
        internal const float OverlayHorizontalFovDegrees = 60.0f;
        internal const int TextureWidth = 1920;
        internal const int TextureHeight = 1080;

        internal const float NativeHudReferenceRowPixelsY = 153.0f;
        internal const float DesiredHudReferenceViewportY = 0.50f;

        private const string OverlayKey = "sons_vr.native_hud.v114";
        private const string OverlayName = "SonsVR Native HUD V114";

        // Sane isolation coordinate: still nowhere near the gameplay world, but small enough that
        // float precision is orders of magnitude better than V113's 100000-unit exile.
        private static readonly Vector3 HudCameraPosition = new Vector3(1000f, 1000f, 1000f);
        private const float HudPlaneDistance = 1.0f;
        private const float HudNearClip = 0.50f;
        private const float HudFarClip = 1.50f;

        private static RenderTexture _hudRt;
        private static GameObject _hudCameraGo;
        private static Camera _hudCamera;
        private static HDAdditionalCameraData _hudHd;
        private static ulong _overlayHandle = OpenVR.k_ulOverlayHandleInvalid;
        private static bool _overlayVisible;
        private static bool _overlayTextureStateConfigured;
        private static bool _overlayPoseConfigured;
        private static bool _setupLogged;
        private static float _nextRetry;
        private static bool _staleOverlayCleanupDone;

        private static Canvas _screenCanvas;
        private static Canvas _minimapCanvas;
        private static int _screenCanvasId;
        private static int _minimapCanvasId;

        private static int _hudLayerMask;
        private static float _nextHudMaskRefresh;

        private static bool _preflightRunning;
        private static bool _preflightComplete;
        private static int _preflightGeneration;

        internal static Camera UiCamera => _hudCamera;
        internal static RenderTexture HudTexture => _hudRt;
        internal static int HudLayerMask => _hudLayerMask;
        internal static bool PreflightComplete => _preflightComplete;
        internal static bool OwnsNativeHud => Enabled && _screenCanvas != null && _minimapCanvas != null;
        internal static string OpenVrOverlayKey => OverlayKey;

        internal static bool IsTargetCanvas(Canvas c)
        {
            if (c == null) return false;
            return c == _screenCanvas || c == _minimapCanvas;
        }

        internal static void Tick(VRPlayer player, Canvas screenCanvas, Canvas minimapCanvas)
        {
            if (!Enabled)
            {
                HideOverlay();
                return;
            }

            if (player == null || player.LocalPlayer == null || player.StereoRender == null ||
                !StereoRender.AerGameplayReadyForActivation)
            {
                HideOverlay();
                return;
            }

            if (screenCanvas == null || minimapCanvas == null)
                return;

            int sId = screenCanvas.gameObject.GetInstanceID();
            int mId = minimapCanvas.gameObject.GetInstanceID();
            bool changed = _screenCanvas != screenCanvas || _minimapCanvas != minimapCanvas ||
                           _screenCanvasId != sId || _minimapCanvasId != mId;

            if (changed)
            {
                _screenCanvas = screenCanvas;
                _minimapCanvas = minimapCanvas;
                _screenCanvasId = sId;
                _minimapCanvasId = mId;
                _setupLogged = false;
                _preflightComplete = false;
                _preflightGeneration++;
                _nextHudMaskRefresh = 0f;
                HideOverlay();
            }

            if (Time.unscaledTime < _nextRetry) return;

            try
            {
                if (!EnsureOverlayAndCamera())
                {
                    _nextRetry = Time.unscaledTime + 1.0f;
                    return;
                }

                ConfigureNativeCanvas(_screenCanvas);
                ConfigureNativeCanvas(_minimapCanvas);
                RefreshHudCameraMaskIfNeeded();

                if (!_preflightComplete)
                {
                    EnsurePreflightStarted();
                    return;
                }

                // Reassert the final production mask after the diagnostic coroutine has finished.
                if (_hudCamera.cullingMask != _hudLayerMask)
                    _hudCamera.cullingMask = _hudLayerMask;

                if (!ConfigureOverlayTextureStateOnce() || !ConfigureOverlayPoseOnce())
                {
                    _nextRetry = Time.unscaledTime + 1.0f;
                    return;
                }

                if (!SubmitOverlayTextureThisFrame())
                {
                    _nextRetry = Time.unscaledTime + 0.25f;
                    return;
                }

                if (!_overlayVisible)
                {
                    EVROverlayError showErr = OpenVR.Overlay.ShowOverlay(_overlayHandle);
                    if (showErr == EVROverlayError.None)
                        _overlayVisible = true;
                    else
                    {
                        MelonLogger.Warning("[V114 NativeHUD] ShowOverlay failed: " + showErr);
                        _nextRetry = Time.unscaledTime + 1.0f;
                        return;
                    }
                }

                if (!_setupLogged)
                {
                    _setupLogged = true;
                    LogNativeContractRuntime();
                }
            }
            catch (Exception e)
            {
                _nextRetry = Time.unscaledTime + 1.0f;
                MelonLogger.Warning("[V114 NativeHUD] setup/tick failed; retrying without touching held GPS: " + e.Message);
            }
        }

        private static bool EnsureOverlayAndCamera()
        {
            if (OpenVR.Overlay == null || OpenVR.System == null)
                return false;

            CleanupKnownStaleOverlayOnce();

            if (_hudRt == null)
            {
                _hudRt = new RenderTexture(TextureWidth, TextureHeight, 0, RenderTextureFormat.ARGB32)
                {
                    name = "SonsVR_V114_NativeHudRT",
                    useMipMap = false,
                    autoGenerateMips = false,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                _hudRt.Create();
                _overlayTextureStateConfigured = false;
            }

            if (_hudCamera == null)
            {
                _hudCameraGo = new GameObject("SonsVR_V114_NativeHudCamera");
                _hudCameraGo.tag = "Untagged";
                _hudCamera = _hudCameraGo.AddComponent<Camera>();
                _hudCamera.stereoTargetEye = StereoTargetEyeMask.None;
                _hudCamera.targetTexture = _hudRt;
                _hudCamera.clearFlags = CameraClearFlags.SolidColor;
                _hudCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                _hudCamera.allowHDR = false;
                _hudCamera.allowMSAA = false;
                _hudCamera.allowDynamicResolution = false;
                _hudCamera.nearClipPlane = HudNearClip;
                _hudCamera.farClipPlane = HudFarClip;
                _hudCamera.depth = 5000f;
                _hudCamera.orthographic = true;
                _hudCamera.orthographicSize = 1.0f;
                _hudCamera.cullingMask = 0; // preflight starts from a known-empty object mask

                _hudCameraGo.transform.position = HudCameraPosition;
                _hudCameraGo.transform.rotation = Quaternion.identity;

                _hudHd = _hudCameraGo.AddComponent<HDAdditionalCameraData>();
                _hudHd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                _hudHd.backgroundColorHDR = new Color(0f, 0f, 0f, 0f);
                _hudHd.hasPersistentHistory = false;
                _hudHd.volumeLayerMask = 0;

                // Keep the utility camera cheap. V107's helper disables AA/DLSS/DRS/history and
                // best-effort FrameSettings; V114 additionally owns clear mode + volume mask above.
                StereoRender.TryConfigureNativeTrashCameraV107(_hudCamera);

                CameraCompositorAuditV114.NoteEvent(
                    "HUD_CAMERA_CREATED",
                    $"pos={HudCameraPosition} near={HudNearClip:F2} far={HudFarClip:F2} RT={TextureWidth}x{TextureHeight} ARGB32 volumeMask=0");
            }

            if (_overlayHandle == OpenVR.k_ulOverlayHandleInvalid)
            {
                ulong handle = OpenVR.k_ulOverlayHandleInvalid;
                EVROverlayError err = OpenVR.Overlay.FindOverlay(OverlayKey, ref handle);
                if (err != EVROverlayError.None)
                    err = OpenVR.Overlay.CreateOverlay(OverlayKey, OverlayName, ref handle);
                if (err != EVROverlayError.None)
                {
                    MelonLogger.Warning("[V114 NativeHUD] Create/FindOverlay failed: " + err);
                    return false;
                }
                _overlayHandle = handle;
                _overlayTextureStateConfigured = false;
                _overlayPoseConfigured = false;
                _overlayVisible = false;
                CameraCompositorAuditV114.NoteEvent("OPENVR_OVERLAY_CREATED", $"key={OverlayKey} handle={_overlayHandle}");
            }

            return true;
        }

        private static void CleanupKnownStaleOverlayOnce()
        {
            if (_staleOverlayCleanupDone || OpenVR.Overlay == null) return;
            _staleOverlayCleanupDone = true;

            // V113 used a different key. A clean shutdown destroys it, but after a crash or forced
            // termination we do not want a previous experimental HUD surface hanging around and
            // confusing the next diagnosis. Only our own known historical key is touched.
            const string staleKey = "sons_vr.native_hud.v113";
            ulong staleHandle = OpenVR.k_ulOverlayHandleInvalid;
            try
            {
                EVROverlayError find = OpenVR.Overlay.FindOverlay(staleKey, ref staleHandle);
                if (find != EVROverlayError.None || staleHandle == OpenVR.k_ulOverlayHandleInvalid)
                    return;

                OpenVR.Overlay.HideOverlay(staleHandle);
                OpenVR.Overlay.DestroyOverlay(staleHandle);
                MelonLogger.Warning(
                    $"[V114 NativeHUD] stale V113 overlay cleanup requested: handle={staleHandle}.");
                CameraCompositorAuditV114.NoteEvent(
                    "STALE_OPENVR_OVERLAY_CLEANUP",
                    $"key={staleKey} handle={staleHandle} hide+destroy requested");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[V114 NativeHUD] stale V113 overlay cleanup failed harmlessly: " + e.Message);
            }
        }

        private static void ConfigureNativeCanvas(Canvas canvas)
        {
            if (canvas == null || _hudCamera == null) return;

            if (canvas.renderMode != RenderMode.ScreenSpaceCamera)
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
            if (canvas.worldCamera != _hudCamera)
                canvas.worldCamera = _hudCamera;
            canvas.planeDistance = HudPlaneDistance;

            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler != null && !scaler.enabled)
                scaler.enabled = true;
        }

        private static void RefreshHudCameraMaskIfNeeded()
        {
            if (_hudCamera == null || _screenCanvas == null || _minimapCanvas == null) return;
            if (Time.unscaledTime < _nextHudMaskRefresh && _hudLayerMask != 0) return;
            _nextHudMaskRefresh = Time.unscaledTime + 5.0f;

            int newMask = ComputeCanvasUiLayerMask(_screenCanvas) | ComputeCanvasUiLayerMask(_minimapCanvas);
            if (newMask == 0)
            {
                // Defensive fallback: a Canvas root must at least have a layer even if no active
                // CanvasRenderer has been constructed yet during this exact frame.
                newMask = (1 << _screenCanvas.gameObject.layer) | (1 << _minimapCanvas.gameObject.layer);
            }

            if (newMask != _hudLayerMask)
            {
                int old = _hudLayerMask;
                _hudLayerMask = newMask;
                MelonLogger.Warning($"[V114 NativeHUD] HUD-only culling mask 0x{unchecked((uint)old):X8} -> 0x{unchecked((uint)_hudLayerMask):X8}; layers={DescribeMask(_hudLayerMask)}");
                CameraCompositorAuditV114.NoteEvent("HUD_LAYER_MASK", $"mask=0x{unchecked((uint)_hudLayerMask):X8} layers={DescribeMask(_hudLayerMask)}");
            }

            if (!_preflightRunning && _preflightComplete)
                _hudCamera.cullingMask = _hudLayerMask;
        }

        private static int ComputeCanvasUiLayerMask(Canvas root)
        {
            if (root == null) return 0;
            int mask = 1 << root.gameObject.layer;

            Canvas[] canvases = root.GetComponentsInChildren<Canvas>(true);
            for (int i = 0; i < canvases.Length; ++i)
            {
                Canvas c = canvases[i];
                if (c != null && c.gameObject != null)
                    mask |= 1 << c.gameObject.layer;
            }

            CanvasRenderer[] renderers = root.GetComponentsInChildren<CanvasRenderer>(true);
            for (int i = 0; i < renderers.Length; ++i)
            {
                CanvasRenderer r = renderers[i];
                if (r != null && r.gameObject != null)
                    mask |= 1 << r.gameObject.layer;
            }

            return mask;
        }

        private static string DescribeMask(int mask)
        {
            string s = "";
            for (int i = 0; i < 32; ++i)
            {
                if ((mask & (1 << i)) == 0) continue;
                string name = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(name)) name = "<unnamed>";
                if (s.Length > 0) s += ",";
                s += i + ":" + name;
            }
            return s;
        }

        private static void EnsurePreflightStarted()
        {
            if (_preflightRunning || _preflightComplete || _hudCamera == null || _hudRt == null || _hudLayerMask == 0)
                return;

            _preflightRunning = true;
            int generation = _preflightGeneration;
            HideOverlay();
            MelonLogger.Warning("[V114 NativeHUD][Preflight] hidden two-stage capture test starting; overlay will remain invisible until both samples complete.");
            MelonLoader.MelonCoroutines.Start(RunPreflight(generation));
        }

        private static IEnumerator RunPreflight(int generation)
        {
            // Stage A: absolutely no scene-object layers. If this is not clean transparent black,
            // the contamination is outside ordinary Camera.cullingMask object rendering.
            if (!PreflightStillValid(generation)) yield break;
            _hudCamera.cullingMask = 0;
            if (_hudHd != null)
            {
                _hudHd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                _hudHd.backgroundColorHDR = new Color(0f, 0f, 0f, 0f);
                _hudHd.volumeLayerMask = 0;
            }
            _hudCamera.clearFlags = CameraClearFlags.SolidColor;
            _hudCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);

            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            if (!PreflightStillValid(generation)) yield break;
            SampleHudRt("EMPTY_MASK0_VOLUME0");

            // Stage B: only layers actually used by the two target HUD canvas trees.
            _hudCamera.cullingMask = _hudLayerMask;
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            if (!PreflightStillValid(generation)) yield break;
            SampleHudRt("HUD_ONLY_MASK");

            _hudCamera.cullingMask = _hudLayerMask;
            _preflightRunning = false;
            _preflightComplete = true;
            MelonLogger.Warning("[V114 NativeHUD][Preflight] COMPLETE; production HUD compositor may now become visible.");
            CameraCompositorAuditV114.NoteEvent("HUD_PREFLIGHT_COMPLETE", $"mask=0x{unchecked((uint)_hudLayerMask):X8}");
        }

        private static bool PreflightStillValid(int generation)
        {
            if (generation == _preflightGeneration && _hudCamera != null && _hudRt != null)
                return true;
            _preflightRunning = false;
            return false;
        }

        private static void SampleHudRt(string label)
        {
            const int w = 64;
            const int h = 36;
            RenderTexture small = null;
            Texture2D cpu = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                small = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                small.filterMode = FilterMode.Bilinear;
                Graphics.Blit(_hudRt, small);

                RenderTexture.active = small;
                cpu = new Texture2D(w, h, TextureFormat.RGBA32, false);
                cpu.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                cpu.Apply(false, false);

                var px = cpu.GetPixels32();
                int n = px.Length;
                if (n <= 0)
                {
                    MelonLogger.Warning($"[V114 NativeHUD][Preflight] {label}: no pixels returned.");
                    return;
                }

                double sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                double sumLum = 0, sumLum2 = 0;
                int nonBlack = 0, nonTransparent = 0, opaque = 0;
                byte minA = 255, maxA = 0;
                double topLum = 0, bottomLum = 0, topA = 0, bottomA = 0;
                int topN = 0, bottomN = 0;

                for (int i = 0; i < n; ++i)
                {
                    Color32 p = px[i];
                    sumR += p.r; sumG += p.g; sumB += p.b; sumA += p.a;
                    double lum = (0.2126 * p.r + 0.7152 * p.g + 0.0722 * p.b) / 255.0;
                    sumLum += lum;
                    sumLum2 += lum * lum;
                    if (p.r > 3 || p.g > 3 || p.b > 3) nonBlack++;
                    if (p.a > 3) nonTransparent++;
                    if (p.a >= 252) opaque++;
                    if (p.a < minA) minA = p.a;
                    if (p.a > maxA) maxA = p.a;

                    int y = i / w;
                    if (y >= h * 2 / 3)
                    {
                        topLum += lum; topA += p.a / 255.0; topN++;
                    }
                    else if (y < h / 3)
                    {
                        bottomLum += lum; bottomA += p.a / 255.0; bottomN++;
                    }
                }

                double inv = 1.0 / n;
                double meanLum = sumLum * inv;
                double varLum = Math.Max(0.0, sumLum2 * inv - meanLum * meanLum);
                string msg =
                    $"{label} mask=0x{unchecked((uint)_hudCamera.cullingMask):X8} " +
                    $"meanRGBA=({sumR*inv/255.0:F4},{sumG*inv/255.0:F4},{sumB*inv/255.0:F4},{sumA*inv/255.0:F4}) " +
                    $"alpha[min={minA/255.0:F4} max={maxA/255.0:F4}] " +
                    $"nonBlack={100.0*nonBlack/n:F2}% nonTransparent={100.0*nonTransparent/n:F2}% opaque={100.0*opaque/n:F2}% " +
                    $"lumVar={varLum:F6} topLum={(topN>0?topLum/topN:0):F4} bottomLum={(bottomN>0?bottomLum/bottomN:0):F4} " +
                    $"topA={(topN>0?topA/topN:0):F4} bottomA={(bottomN>0?bottomA/bottomN:0):F4}";

                MelonLogger.Warning("[V114 NativeHUD][Preflight] " + msg);
                CameraCompositorAuditV114.NoteEvent("HUD_RT_SAMPLE", msg);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[V114 NativeHUD][Preflight] {label} sample failed: {e.GetType().Name}: {e.Message}");
                CameraCompositorAuditV114.NoteEvent("HUD_RT_SAMPLE_FAIL", label + " " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                RenderTexture.active = previous;
                if (cpu != null) UnityEngine.Object.Destroy(cpu);
                if (small != null) RenderTexture.ReleaseTemporary(small);
            }
        }

        private static bool ConfigureOverlayTextureStateOnce()
        {
            if (_overlayTextureStateConfigured) return true;
            if (_overlayHandle == OpenVR.k_ulOverlayHandleInvalid || OpenVR.Overlay == null)
                return false;

            var bounds = new VRTextureBounds_t { uMin = 0f, vMin = 1f, uMax = 1f, vMax = 0f };
            OpenVR.Overlay.SetOverlayTextureBounds(_overlayHandle, ref bounds);
            OpenVR.Overlay.SetOverlayAlpha(_overlayHandle, 1.0f);
            OpenVR.Overlay.SetOverlayColor(_overlayHandle, 1.0f, 1.0f, 1.0f);
            _overlayTextureStateConfigured = true;
            return true;
        }

        private static bool SubmitOverlayTextureThisFrame()
        {
            if (_overlayHandle == OpenVR.k_ulOverlayHandleInvalid || OpenVR.Overlay == null || _hudRt == null)
                return false;

            var texture = new Texture_t
            {
                handle = _hudRt.GetNativeTexturePtr(),
                eType = SteamVR.instance.textureType,
                eColorSpace = EColorSpace.Auto
            };
            EVROverlayError texErr = OpenVR.Overlay.SetOverlayTexture(_overlayHandle, ref texture);
            if (texErr != EVROverlayError.None)
            {
                MelonLogger.Warning("[V114 NativeHUD] SetOverlayTexture failed: " + texErr);
                return false;
            }
            return true;
        }

        private static bool ConfigureOverlayPoseOnce()
        {
            if (_overlayPoseConfigured) return true;
            if (_overlayHandle == OpenVR.k_ulOverlayHandleInvalid || OpenVR.Overlay == null)
                return false;

            float widthMeters = 2f * OverlayDistanceMeters *
                                Mathf.Tan(OverlayHorizontalFovDegrees * Mathf.Deg2Rad * 0.5f);
            float heightMeters = widthMeters * ((float)TextureHeight / TextureWidth);
            float nativeRowV = NativeHudReferenceRowPixelsY / TextureHeight;
            float centerYOffsetMeters = (DesiredHudReferenceViewportY - nativeRowV) * heightMeters;

            EVROverlayError widthErr = OpenVR.Overlay.SetOverlayWidthInMeters(_overlayHandle, widthMeters);
            if (widthErr != EVROverlayError.None)
            {
                MelonLogger.Warning("[V114 NativeHUD] SetOverlayWidthInMeters failed: " + widthErr);
                return false;
            }

            GameObject scratch = null;
            HmdMatrix34_t rel = new HmdMatrix34_t();
            try
            {
                scratch = new GameObject("SonsVR_V114_OverlayPoseScratch");
                scratch.transform.position = new Vector3(0f, centerYOffsetMeters, OverlayDistanceMeters);
                scratch.transform.rotation = Quaternion.identity;
                rel = new SteamVR_Utils.RigidTransform(scratch.transform).ToHmdMatrix34();
            }
            finally
            {
                if (scratch != null) UnityEngine.Object.Destroy(scratch);
            }

            EVROverlayError xformErr = OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(
                _overlayHandle, OpenVR.k_unTrackedDeviceIndex_Hmd, ref rel);
            if (xformErr != EVROverlayError.None)
            {
                MelonLogger.Warning("[V114 NativeHUD] HMD-relative transform failed: " + xformErr);
                return false;
            }

            _overlayPoseConfigured = true;
            MelonLogger.Warning(
                $"[V114 NativeHUD] finite compositor plane configured: distance={OverlayDistanceMeters:F2}m " +
                $"width={widthMeters:F2}m height={heightMeters:F2}m angularWidth={OverlayHorizontalFovDegrees:F1}deg " +
                $"globalY={centerYOffsetMeters:F3}m; native row y={NativeHudReferenceRowPixelsY:F0}/{TextureHeight} -> view y={DesiredHudReferenceViewportY:F2}.");
            return true;
        }

        private static void LogNativeContractRuntime()
        {
            try
            {
                RectTransform screenRt = _screenCanvas != null ? _screenCanvas.GetComponent<RectTransform>() : null;
                RectTransform miniRt = _minimapCanvas != null ? _minimapCanvas.GetComponent<RectTransform>() : null;
                string srect = screenRt != null ? screenRt.rect.size.ToString() : "null";
                string mrect = miniRt != null ? miniRt.rect.size.ToString() : "null";

                MelonLogger.Warning(
                    $"[V114 NativeHUD] ACTIVE: native 2D HUD -> {TextureWidth}x{TextureHeight} RT -> OpenVR compositor. " +
                    $"capturePos={HudCameraPosition}, mask=0x{unchecked((uint)_hudLayerMask):X8} ({DescribeMask(_hudLayerMask)}), " +
                    $"ScreenSpaceCanvas rect={srect} scaleFactor={(_screenCanvas != null ? _screenCanvas.scaleFactor : 0f):F3}; " +
                    $"MiniMapCanvas rect={mrect} scaleFactor={(_minimapCanvas != null ? _minimapCanvas.scaleFactor : 0f):F3}. " +
                    "Held/physical GPS remains untouched.");

                Transform mask = _minimapCanvas != null ? _minimapCanvas.transform.Find("ScreenMask") : null;
                Transform vitals = _screenCanvas != null ? _screenCanvas.transform.Find("ToggleableHUD/PlayerVitalUiRenderTextureGroup") : null;
                RectTransform maskRt = mask != null ? mask.GetComponent<RectTransform>() : null;
                RectTransform vitalsRt = vitals != null ? vitals.GetComponent<RectTransform>() : null;
                if (maskRt != null && vitalsRt != null)
                {
                    Vector2 d = vitalsRt.anchoredPosition - maskRt.anchoredPosition;
                    MelonLogger.Warning(
                        $"[V114 NativeHUD] runtime native anchor check: GPS={maskRt.anchoredPosition} " +
                        $"vitalsGroup={vitalsRt.anchoredPosition} delta={d}; expected about (0,-1px). No child recentering applied.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[V114 NativeHUD] runtime contract log failed: " + e.Message);
            }
        }

        private static void HideOverlay()
        {
            if (_overlayHandle == OpenVR.k_ulOverlayHandleInvalid || OpenVR.Overlay == null) return;
            if (!_overlayVisible) return;
            try { OpenVR.Overlay.HideOverlay(_overlayHandle); } catch { }
            _overlayVisible = false;
        }

        internal static void Shutdown()
        {
            _preflightGeneration++;
            _preflightRunning = false;
            _preflightComplete = false;
            HideOverlay();
            if (_overlayHandle != OpenVR.k_ulOverlayHandleInvalid && OpenVR.Overlay != null)
            {
                try { OpenVR.Overlay.DestroyOverlay(_overlayHandle); } catch { }
            }
            _overlayHandle = OpenVR.k_ulOverlayHandleInvalid;
            _overlayTextureStateConfigured = false;
            _overlayPoseConfigured = false;

            if (_hudCameraGo != null)
            {
                try { UnityEngine.Object.Destroy(_hudCameraGo); } catch { }
            }
            _hudCameraGo = null;
            _hudCamera = null;
            _hudHd = null;

            if (_hudRt != null)
            {
                try { _hudRt.Release(); } catch { }
                try { UnityEngine.Object.Destroy(_hudRt); } catch { }
            }
            _hudRt = null;
            _screenCanvas = null;
            _minimapCanvas = null;
            _screenCanvasId = 0;
            _minimapCanvasId = 0;
            _hudLayerMask = 0;
            _overlayVisible = false;
            _setupLogged = false;
            _staleOverlayCleanupDone = false;
        }
    }
}
