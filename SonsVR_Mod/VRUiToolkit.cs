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
    /// Brings the parts of the interface built with UI Toolkit into the headset.
    /// <para>
    /// Some screens, the options screen among them, are not built from canvases at all. UI Toolkit
    /// draws them through a panel that paints straight onto the output image, so there is no
    /// transform to move and no camera involved: converting canvases cannot reach them, which is why
    /// the options screen appeared on the monitor and nowhere else.
    /// </para>
    /// <para>
    /// What it does offer is a target texture. Pointed at one, the panel renders into that texture
    /// instead of onto the screen, and the texture can then be shown on a surface in the world like
    /// any other picture.
    /// </para>
    /// </summary>
    internal class VRUiToolkit
    {
        private readonly System.Collections.Generic.List<PanelSettings> _captured =
            new System.Collections.Generic.List<PanelSettings>();

        private RenderTexture _panelTexture;
        private Canvas _surface;
        private RawImage _surfaceImage;
        private int _scanCountdown;

        /// <summary>The point the pointer is on, in panel coordinates. Not a number means nowhere.</summary>
        private Vector2 _panelPoint = Invalid;

        private static readonly Vector2 Invalid = new Vector2(float.NaN, float.NaN);

        private Func<Vector2, Vector2> _mappingManaged;
        private Il2CppSystem.Func<Vector2, Vector2> _mapping;

        /// <summary>True once a UI Toolkit screen is being shown on the panel in the world.</summary>
        public bool HasContent { get; private set; }

        /// <summary>Size of the cursor in panel coordinates, kept for the pointer mapping.</summary>
        public Vector2 PanelSize => _panelTexture != null
            ? new Vector2(_panelTexture.width, _panelTexture.height)
            : Vector2.zero;

        /// <summary>
        /// The surface the captured screens are shown on, so the pointer can be aimed at it like at
        /// any other panel. Null until a screen has actually been captured.
        /// </summary>
        public RectTransform SurfaceRect => _surface != null
            ? _surface.transform as RectTransform
            : null;

        /// <summary>
        /// Puts the pointer on the panel, given where it lands in the world.
        /// <para>
        /// A UI Toolkit screen aimed at a texture no longer has any relationship to the screen the
        /// mouse is reported in, so the position a mouse event carries is meaningless to it. The
        /// runtime knows this and offers the way out: a panel can be given a function that converts
        /// a screen position into a panel one, and it uses that function for every pointer event it
        /// handles. This is that conversion. It ignores the position it is handed and answers with
        /// the point the beam is on, which is the only one that means anything here.
        /// </para>
        /// <para>
        /// Panel coordinates count pixels of the texture from its top left corner, so the vertical
        /// axis runs opposite to the one the surface is measured in. Not a number is the value the
        /// runtime reads as "the pointer is not on this panel", which is what keeps a button from
        /// staying lit after the beam has moved off it.
        /// </para>
        /// </summary>
        public void SetPointer(Vector3 world)
        {
            RectTransform rect = SurfaceRect;

            if (rect == null || _panelTexture == null)
            {
                _panelPoint = Invalid;
                return;
            }

            Vector2 size = rect.ResolvedSize();
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
        /// The conversion above, as something the runtime can call. Built once and kept, because a
        /// delegate handed across to the runtime and then dropped here is collected while still in
        /// use on the other side.
        /// </summary>
        private Il2CppSystem.Func<Vector2, Vector2> Mapping()
        {
            if (_mapping != null)
                return _mapping;

            _mappingManaged = ScreenToPanel;
            _mapping = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<Vector2, Vector2>>(_mappingManaged);

            return _mapping;
        }

        public void Update(Vector3 position, Quaternion rotation)
        {
            if (--_scanCountdown <= 0)
            {
                _scanCountdown = 30;
                Capture();
            }

            if (_surface != null)
            {
                _surface.transform.SetPositionAndRotation(position, rotation);
                _surface.gameObject.SetActive(HasContent);
            }
        }

        /// <summary>
        /// Finds panels still drawing to the screen and points them at a texture instead.
        /// <para>
        /// Two ways of finding them, because one was not enough. A UIDocument is the component that
        /// puts a screen on show, and looking for those found the options screen but not the pause
        /// or save screens: whatever draws them is not reached that way. The panel settings, on the
        /// other hand, are the thing that actually decides where a UI Toolkit screen is painted, and
        /// they can be found whether or not a component points at them.
        /// </para>
        /// <para>
        /// A panel already aimed at a texture is left alone: it is either one of ours or something
        /// the game is deliberately drawing elsewhere.
        /// </para>
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

            // Said once per scan while nothing has been found, so an empty log means the screens are
            // not UI Toolkit at all rather than the search never having run.
            if (!HasContent && --_emptyReportCountdown <= 0)
            {
                _emptyReportCountdown = 4;

                MelonLogger.Msg($"[VRUiToolkit] nothing captured: {documents} documents on show, " +
                                $"{panels} panel settings in memory");
            }

            EnsureSurface();
        }

        private int _emptyReportCountdown;

        /// <summary>Points one panel at our texture, if it is still painting onto the screen.</summary>
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

            MelonLogger.Msg($"[VRUiToolkit] '{owner}' now renders into a texture " +
                            $"({_panelTexture.width}x{_panelTexture.height}) instead of the screen");
        }

        private void EnsureTexture()
        {
            if (_panelTexture != null)
                return;

            int width = Mathf.Clamp(Screen.width, 1280, 1920);
            int height = Mathf.Max(720, Mathf.RoundToInt(width * 9f / 16f));

            _panelTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear)
            {
                name = "VRUiToolkitRT",
                filterMode = FilterMode.Bilinear
            };
            _panelTexture.Create();
        }

        /// <summary>
        /// Builds the surface in the world that the captured panel is shown on: a world space canvas
        /// carrying the texture, which reuses the machinery already in place for the other menus.
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
            rect.localScale = Vector3.one * (VRConfig.MenuHeight / _panelTexture.height);

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
        /// Hands the panels back to the screen. Without this the options screen would stay invisible
        /// on the monitor after the headset is done with it.
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
