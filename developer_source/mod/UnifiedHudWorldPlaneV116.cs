using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace SonsVRMod
{
    /// <summary>
    /// V116 unified native HUD world-plane path.
    ///
    /// V115 proved the interaction-preserving WorldSpace boundary works well. The remaining
    /// minimap error came from aligning RECTANGLE CENTERS (ScreenBounds -> VitalsRenderTexture),
    /// even though Endnight authored the relationship at the parent RectTransform anchors/pivots:
    ///   PlayerVitalUiRenderTextureGroup ~= (-162, 152)
    ///   ScreenMask                      ~= (-162, 153)
    /// Rect centers are not equivalent when sizes/pivots differ. V116 therefore preserves the
    /// native authored pivot delta directly in the shared pixel-to-metre contract.
    ///
    /// - Main native HUD remains 60 degrees wide at 1.20 m.
    /// - Native row y=153 stays at eye height.
    /// - Main HUD and minimap use the same world-units-per-native-pixel.
    /// - The minimap OUTER root is translated only enough to make the ScreenMask pivot sit at the
    ///   authored delta from PlayerVitalUiRenderTextureGroup's pivot. Children remain untouched.
    /// - No HUD camera, RenderTexture, compositor overlay, or extra stereo camera is created.
    /// - The held physical GPS is separate 3D geometry and is never touched here.
    /// </summary>
    internal static class UnifiedHudWorldPlaneV116
    {
        internal const bool Enabled = true;
        internal const float DistanceMeters = 1.20f;
        internal const float HorizontalFovDegrees = 60.0f;
        // V163H: modest screen-UP translation of the existing shared HUD plane.
        // 0.00f restores exact V163 positioning; no depth/material/camera changes.
        internal const float HudVerticalLiftMeters = 0.07f;
        internal const float NativeReferenceRowY = 153.0f;
        internal const float FallbackLogicalWidth = 1920.0f;
        internal const float FallbackLogicalHeight = 1080.0f;

        private static int _screenCanvasId;
        private static int _minimapCanvasId;
        private static RectTransform _ringAnchorRt;
        private static RectTransform _mapAnchorRt;
        private static RectTransform _ringVisualRt;
        private static RectTransform _mapVisualRt;
        private static bool _loggedSetup;
        private static bool _loggedMissingRing;
        private static bool _loggedMissingMap;
        private static float _lastDiagTime;
        private static float _nextAnchorRetryTime;

        internal static void Tick(
            Canvas screenCanvas,
            Canvas minimapCanvas,
            Transform minimapRoot,
            Camera eventCamera,
            Transform head)
        {
            if (!Enabled || screenCanvas == null || minimapCanvas == null || minimapRoot == null ||
                eventCamera == null || head == null)
                return;

            RectTransform screenRt = screenCanvas.GetComponent<RectTransform>();
            if (screenRt == null)
                return;

            ConfigureWorldCanvas(screenCanvas, eventCamera, forceDefaultLayer: true, minimapRoot: null);
            ConfigureWorldCanvas(minimapCanvas, eventCamera, forceDefaultLayer: false, minimapRoot: minimapRoot);

            Rect screenRect = screenRt.rect;
            float logicalWidth = Mathf.Abs(screenRect.width) > 1f ? Mathf.Abs(screenRect.width) : FallbackLogicalWidth;
            float logicalHeight = Mathf.Abs(screenRect.height) > 1f ? Mathf.Abs(screenRect.height) : FallbackLogicalHeight;

            float planeWidth = 2.0f * DistanceMeters * Mathf.Tan(0.5f * HorizontalFovDegrees * Mathf.Deg2Rad);
            float planeHeight = planeWidth * (logicalHeight / logicalWidth);
            float worldPerPixel = planeHeight / logicalHeight;

            Quaternion rotation = Quaternion.LookRotation(head.forward, head.up);
            // V163H: both ScreenSpaceCanvas and the native minimap derive from this
            // same reference, so the ring/map/pivots move together in the view plane.
            // Use head.up (view-up), NOT world-space Vector3.up: roll/head tilt should
            // not make an otherwise screen-relative overlay drift sideways.
            Vector3 referenceWorld = head.position + head.forward * DistanceMeters
                + head.up * HudVerticalLiftMeters;

            // Native logical X center and authored y=153 become the one world-space reference.
            Vector3 referenceLocal = new Vector3(
                screenRect.xMin + logicalWidth * 0.5f,
                screenRect.yMin + Mathf.Clamp(NativeReferenceRowY, 0f, logicalHeight),
                0f);

            SetUniformWorldScale(screenCanvas.transform, worldPerPixel);
            screenCanvas.transform.SetPositionAndRotation(referenceWorld, rotation);
            Vector3 currentReferenceWorld = screenCanvas.transform.TransformPoint(referenceLocal);
            screenCanvas.transform.position += referenceWorld - currentReferenceWorld;

            ResolveAnchors(screenCanvas, minimapCanvas, minimapRoot);

            // Give the minimap the same plane basis/scale, then solve ONLY the outer-root
            // translation from the authored anchor/pivot relationship.
            minimapRoot.SetPositionAndRotation(referenceWorld, rotation);
            SetUniformWorldScale(minimapCanvas.transform, worldPerPixel);

            if (_ringAnchorRt != null && _mapAnchorRt != null)
            {
                Vector2 nativeDeltaPx = _mapAnchorRt.anchoredPosition - _ringAnchorRt.anchoredPosition;
                Vector3 desiredDeltaWorld = rotation * new Vector3(
                    nativeDeltaPx.x * worldPerPixel,
                    nativeDeltaPx.y * worldPerPixel,
                    0f);

                Vector3 ringPivotWorld = GetPivotWorld(_ringAnchorRt);
                Vector3 mapPivotWorld = GetPivotWorld(_mapAnchorRt);
                Vector3 desiredMapPivotWorld = ringPivotWorld + desiredDeltaWorld;
                minimapRoot.position += desiredMapPivotWorld - mapPivotWorld;
            }

            if (!_loggedSetup)
            {
                _loggedSetup = true;
                MelonLogger.Warning(
                    $"[VRPlayer][V116 HUD] unified world plane active: logical={logicalWidth:F0}x{logicalHeight:F0} " +
                    $"distance={DistanceMeters:F2}m hfov={HorizontalFovDegrees:F0}deg plane={planeWidth:F3}x{planeHeight:F3}m " +
                    $"worldPerPixel={worldPerPixel:F8}. Minimap alignment uses authored RectTransform PIVOTS, not rect centers.");
            }

            if (Time.unscaledTime - _lastDiagTime > 5f)
            {
                _lastDiagTime = Time.unscaledTime;
                string pivotDiag = "anchors=n/a";
                if (_ringAnchorRt != null && _mapAnchorRt != null)
                {
                    Vector2 nativeDeltaPx = _mapAnchorRt.anchoredPosition - _ringAnchorRt.anchoredPosition;
                    Vector3 targetDeltaWorld = rotation * new Vector3(nativeDeltaPx.x * worldPerPixel, nativeDeltaPx.y * worldPerPixel, 0f);
                    Vector3 actualDeltaWorld = GetPivotWorld(_mapAnchorRt) - GetPivotWorld(_ringAnchorRt);
                    float residualMm = (actualDeltaWorld - targetDeltaWorld).magnitude * 1000f;
                    pivotDiag =
                        $"ringAnchor={_ringAnchorRt.anchoredPosition} mapAnchor={_mapAnchorRt.anchoredPosition} " +
                        $"nativeDeltaPx={nativeDeltaPx} pivotResidual={residualMm:F3}mm";
                }

                string visualDiag = "visualCenters=n/a";
                if (_ringVisualRt != null && _mapVisualRt != null)
                {
                    float visualSepMm = Vector3.Distance(GetRectCenterWorld(_ringVisualRt), GetRectCenterWorld(_mapVisualRt)) * 1000f;
                    visualDiag = $"visualCenterSeparation={visualSepMm:F3}mm";
                }

                MelonLogger.Msg(
                    $"[VRPlayer][V116 HUD] screenMode={screenCanvas.renderMode} miniMode={minimapCanvas.renderMode} " +
                    $"{pivotDiag} {visualDiag} screenPos={screenCanvas.transform.position} miniRootPos={minimapRoot.position}");
            }
        }

        private static void ConfigureWorldCanvas(
            Canvas canvas,
            Camera eventCamera,
            bool forceDefaultLayer,
            Transform minimapRoot)
        {
            if (canvas == null) return;

            if (canvas.renderMode != RenderMode.WorldSpace)
                canvas.renderMode = RenderMode.WorldSpace;

            // This assignment is for UGUI/raycast semantics only. Never mutate the event camera's
            // culling/depth/clear state; MainCameraFP's layer-31 optimization is independent.
            if (canvas.worldCamera != eventCamera)
                canvas.worldCamera = eventCamera;

            if (forceDefaultLayer)
            {
                int defaultLayer = LayerMask.NameToLayer("Default");
                if (defaultLayer >= 0) canvas.gameObject.layer = defaultLayer;
            }
            else if (minimapRoot != null)
            {
                canvas.gameObject.layer = minimapRoot.gameObject.layer;
            }

            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler != null && scaler.enabled)
                scaler.enabled = false;
        }

        private static void SetUniformWorldScale(Transform t, float targetWorldScale)
        {
            if (t == null) return;

            Vector3 parentScale = t.parent != null ? t.parent.lossyScale : Vector3.one;
            float px = Mathf.Abs(parentScale.x) > 1e-6f ? Mathf.Abs(parentScale.x) : 1f;
            float py = Mathf.Abs(parentScale.y) > 1e-6f ? Mathf.Abs(parentScale.y) : 1f;
            float pz = Mathf.Abs(parentScale.z) > 1e-6f ? Mathf.Abs(parentScale.z) : 1f;
            t.localScale = new Vector3(targetWorldScale / px, targetWorldScale / py, targetWorldScale / pz);
        }

        private static void ResolveAnchors(Canvas screenCanvas, Canvas minimapCanvas, Transform minimapRoot)
        {
            int sId = screenCanvas != null ? screenCanvas.gameObject.GetInstanceID() : 0;
            int mId = minimapCanvas != null ? minimapCanvas.gameObject.GetInstanceID() : 0;
            bool sameInstances = sId == _screenCanvasId && mId == _minimapCanvasId;
            if (sameInstances && _ringAnchorRt != null && _mapAnchorRt != null)
                return;
            if (sameInstances && Time.unscaledTime < _nextAnchorRetryTime)
                return;

            _nextAnchorRetryTime = Time.unscaledTime + 2.0f;
            _screenCanvasId = sId;
            _minimapCanvasId = mId;
            _ringAnchorRt = null;
            _mapAnchorRt = null;
            _ringVisualRt = null;
            _mapVisualRt = null;
            _loggedMissingRing = false;
            _loggedMissingMap = false;

            if (screenCanvas != null)
            {
                Transform ringAnchor = screenCanvas.transform.Find("ToggleableHUD/PlayerVitalUiRenderTextureGroup");
                if (ringAnchor == null)
                    ringAnchor = FindDescendantByName(screenCanvas.transform, "PlayerVitalUiRenderTextureGroup");
                if (ringAnchor != null)
                    _ringAnchorRt = ringAnchor.GetComponent<RectTransform>();

                Transform ringVisual = FindDescendantByName(screenCanvas.transform, "VitalsRenderTexture");
                if (ringVisual != null)
                    _ringVisualRt = ringVisual.GetComponent<RectTransform>();

                // Fail-soft fallback: if this game build lacks the authored group name, use the
                // visible RawImage pivot rather than refusing to align at all.
                if (_ringAnchorRt == null)
                    _ringAnchorRt = _ringVisualRt;
            }

            if (minimapRoot != null)
            {
                Transform mapAnchor = minimapRoot.Find("GPSTrackerSystem/Canvas/ScreenMask");
                if (mapAnchor != null)
                    _mapAnchorRt = mapAnchor.GetComponent<RectTransform>();

                Transform mapVisual = minimapRoot.Find("GPSTrackerSystem/Canvas/ScreenMask/ScreenBounds");
                if (mapVisual != null)
                    _mapVisualRt = mapVisual.GetComponent<RectTransform>();

                if (_mapAnchorRt == null)
                    _mapAnchorRt = _mapVisualRt;
            }

            if (_ringAnchorRt == null && !_loggedMissingRing)
            {
                _loggedMissingRing = true;
                MelonLogger.Warning("[VRPlayer][V116 HUD] could not resolve PlayerVitalUiRenderTextureGroup/VitalsRenderTexture pivot; minimap alignment deferred.");
            }
            if (_mapAnchorRt == null && !_loggedMissingMap)
            {
                _loggedMissingMap = true;
                MelonLogger.Warning("[VRPlayer][V116 HUD] could not resolve ScreenMask/ScreenBounds pivot; minimap alignment deferred.");
            }
        }

        private static Transform FindDescendantByName(Transform root, string exactName)
        {
            if (root == null) return null;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t != null && t.name == exactName)
                    return t;
            }
            return null;
        }

        private static Vector3 GetPivotWorld(RectTransform rt)
        {
            return rt != null ? rt.TransformPoint(Vector3.zero) : Vector3.zero;
        }

        private static Vector3 GetRectCenterWorld(RectTransform rt)
        {
            if (rt == null) return Vector3.zero;
            Rect r = rt.rect;
            return rt.TransformPoint(new Vector3(r.center.x, r.center.y, 0f));
        }
    }
}
