using System;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;
using Image = UnityEngine.UI.Image;

namespace SonsVRMod
{
    /// <summary>
    /// PORTED from another developer's independent fork of this same mod, which found something
    /// we spent an enormous amount of effort chasing without success: the Save/Quit confirmation
    /// dialog (and, almost certainly, the crafting-mat "Add items to the mat..." tutorial window)
    /// are NOT built from Unity's traditional Canvas/RectTransform/GameObject UI system at all -
    /// they're built with Unity's newer UI Toolkit, a completely separate retained-mode UI
    /// framework that paints directly to the screen via a PanelSettings/UIDocument mechanism,
    /// bypassing the GameObject hierarchy entirely. That fully explains why our extensive
    /// reflection-based polling (activeSelf, activeInHierarchy, RectTransform anchors) never
    /// found these dialogs genuinely active no matter how precisely we targeted them - we were
    /// checking properties on a system this UI doesn't use to render at all.
    ///
    /// The technique: Unity's UI Toolkit natively supports redirecting a whole panel's rendered
    /// output to an offscreen RenderTexture instead of the screen (PanelSettings.targetTexture).
    /// This finds any panel currently drawing to the screen and redirects it, then displays that
    /// texture on a world-space surface positioned in front of the player - with a screen-to-panel
    /// coordinate mapping function so pointer/click input still works correctly against the
    /// captured texture. This is a GENERIC technique: it doesn't need to know anything about the
    /// specific dialog's name or structure, so it should catch the Save/Quit dialog, the
    /// crafting-mat tutorial, and potentially other UI Toolkit-based screens we haven't
    /// specifically investigated, all at once.
    /// </summary>
    internal class VRUiToolkit
    {
        private readonly System.Collections.Generic.List<PanelSettings> _captured =
            new System.Collections.Generic.List<PanelSettings>();

        private RenderTexture _panelTexture;
        private Canvas _surface;
        private RawImage _surfaceImage;
        private int _scanCountdown;

        public static float PanelDistance = 1.0f; // meters in front of the head
        public static float PanelHeight = 0.6f; // meters tall, matching the general scale used elsewhere for world-space menu conversions in this project

        /// <summary>The point the pointer is on, in panel coordinates. NaN means nowhere.</summary>
        private Vector2 _panelPoint = Invalid;

        private static readonly Vector2 Invalid = new Vector2(float.NaN, float.NaN);

        private Func<Vector2, Vector2> _mappingManaged;
        private Il2CppSystem.Func<Vector2, Vector2> _mapping;

        /// <summary>True once a UI Toolkit screen is being shown on the panel in the world.</summary>
        public bool HasContent { get; private set; }

        public Vector2 PanelSize => _panelTexture != null
            ? new Vector2(_panelTexture.width, _panelTexture.height)
            : Vector2.zero;

        /// <summary>
        /// The surface the captured screens are shown on, so a VR pointer/cursor system can be
        /// aimed at it like at any other panel, if this project's own cursor logic is later
        /// extended to interact with it.
        /// </summary>
        public RectTransform SurfaceRect => _surface != null
            ? _surface.transform as RectTransform
            : null;

        /// <summary>
        /// Converts a world-space hit point into panel coordinates for pointer input. Not yet
        /// wired to any of this project's own cursor/input systems - added for completeness,
        /// matching the original implementation, so it's available when needed.
        /// </summary>
        public void SetPointer(Vector3 world)
        {
            RectTransform rect = SurfaceRect;

            if (rect == null || _panelTexture == null)
            {
                _panelPoint = Invalid;
                return;
            }

            Vector2 size = rect.rect.size;
            if (size.x <= 0f || size.y <= 0f)
            {
                _panelPoint = Invalid;
                return;
            }

            Vector3 local = rect.InverseTransformPoint(world);
            float u = local.x / size.x + 0.5f;
            float v = local.y / size.y + 0.5f;

            if (u < 0f || u > 1f || v < 0f || v > 1f)
            {
                _panelPoint = Invalid;
                return;
            }

            _panelPoint = new Vector2(u * _panelTexture.width, (1f - v) * _panelTexture.height);
        }

        private Vector2 ScreenToPanel(Vector2 screenPosition)
        {
            return _panelPoint;
        }

        /// <summary>
        /// Built once and kept - a delegate handed across to the IL2CPP runtime and then dropped
        /// here would be garbage-collected while still in use on the other side.
        /// </summary>
        private Il2CppSystem.Func<Vector2, Vector2> Mapping()
        {
            if (_mapping != null)
                return _mapping;

            _mappingManaged = ScreenToPanel;
            _mapping = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<Vector2, Vector2>>(_mappingManaged);

            return _mapping;
        }

        /// <summary>
        /// Called every poll from VRPlayer's own update loop, passing the real tracked VR head's
        /// position/forward - positions the panel in front of the player and keeps it there,
        /// re-scanning periodically (not once) since UI Toolkit panels can appear/disappear as the
        /// player navigates menus.
        /// </summary>
        public void Update(Vector3 headPosition, Vector3 headForward)
        {
            if (--_scanCountdown <= 0)
            {
                _scanCountdown = 30;
                Capture();
            }

            if (_surface == null) return;

            Vector3 flatForward = headForward;
            flatForward.y = 0f;
            if (flatForward.sqrMagnitude < 1e-4f) return;
            flatForward.Normalize();

            Vector3 position = headPosition + flatForward * PanelDistance;
            Quaternion rotation = Quaternion.LookRotation(flatForward, Vector3.up);

            _surface.transform.SetPositionAndRotation(position, rotation);
            _surface.gameObject.SetActive(HasContent);
        }

        /// <summary>
        /// Finds panels still drawing to the screen and points them at a texture instead.
        /// <para>
        /// Two ways of finding them: a UIDocument is the component that puts a screen on show, but
        /// per the original implementation this alone misses some screens (the pause/save screens
        /// specifically) - whatever draws them isn't reached that way. PanelSettings, the asset
        /// that actually decides where a UI Toolkit screen is painted, can be found whether or not
        /// a component currently points at it, so both are checked.
        /// </para>
        /// <para>A panel already redirected to a texture is left alone.</para>
        /// </summary>
        private void Capture()
        {
            int documents = 0;
            int panels = 0;

            foreach (var document in UnityEngine.Object.FindObjectsOfType<UIDocument>())
            {
                if (document == null || !document.isActiveAndEnabled)
                    continue;

                documents++;
                Take(document.panelSettings, document.name);
            }

            foreach (var settings in Resources.FindObjectsOfTypeAll<PanelSettings>())
            {
                if (settings == null)
                    continue;

                panels++;
                Take(settings, settings.name);
            }

            if (!HasContent && --_emptyReportCountdown <= 0)
            {
                _emptyReportCountdown = 4;
                MelonLogger.Msg($"[VRUiToolkit] nothing captured this scan: {documents} documents on show, {panels} panel settings in memory");
            }

            EnsureSurface();
        }

        private int _emptyReportCountdown;

        private void Take(PanelSettings settings, string owner)
        {
            if (settings == null || _captured.Contains(settings))
                return;

            if (settings.targetTexture != null)
                return;

            EnsureTexture();

            settings.targetTexture = _panelTexture;
            settings.SetScreenToPanelSpaceFunction(Mapping());
            _captured.Add(settings);
            HasContent = true;

            MelonLogger.Msg($"[VRUiToolkit] '{owner}' now renders into a texture ({_panelTexture.width}x{_panelTexture.height}) instead of the screen");
        }

        private void EnsureTexture()
        {
            if (_panelTexture != null)
                return;

            int width = Mathf.Clamp(Screen.width, 1280, 1920);
            int height = Mathf.Max(720, Mathf.RoundToInt(width * 9f / 16f));

            _panelTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                name = "VRUiToolkitRT",
                filterMode = FilterMode.Bilinear
            };
            _panelTexture.Create();
        }

        /// <summary>
        /// Builds the world-space surface the captured panel is shown on.
        /// </summary>
        private void EnsureSurface()
        {
            if (_surface != null || _panelTexture == null)
                return;

            var go = new GameObject("VRUiToolkitPanel");
            UnityEngine.Object.DontDestroyOnLoad(go);

            _surface = go.AddComponent<Canvas>();
            _surface.renderMode = RenderMode.WorldSpace;

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(_panelTexture.width, _panelTexture.height);
            rect.localScale = Vector3.one * (PanelHeight / _panelTexture.height);

            var imageObject = new GameObject("Surface");
            imageObject.transform.SetParent(go.transform, false);

            _surfaceImage = imageObject.AddComponent<RawImage>();
            _surfaceImage.texture = _panelTexture;
            _surfaceImage.raycastTarget = false;

            var imageRect = imageObject.GetComponent<RectTransform>();
            imageRect.anchorMin = Vector2.zero;
            imageRect.anchorMax = Vector2.one;
            imageRect.offsetMin = Vector2.zero;
            imageRect.offsetMax = Vector2.zero;

            MelonLogger.Msg("[VRUiToolkit] Panel surface created in the world");
        }

        /// <summary>
        /// Hands the panels back to the screen. Without this, a UI Toolkit screen would stay
        /// invisible on the flat monitor mirror after VR is done with it.
        /// </summary>
        public void Release()
        {
            foreach (var settings in _captured)
            {
                if (settings == null)
                    continue;

                settings.targetTexture = null;
                settings.SetScreenToPanelSpaceFunction(null);
            }

            _captured.Clear();
            HasContent = false;
            _panelPoint = Invalid;

            if (_surface != null)
                _surface.gameObject.SetActive(false);
        }
    }
}
