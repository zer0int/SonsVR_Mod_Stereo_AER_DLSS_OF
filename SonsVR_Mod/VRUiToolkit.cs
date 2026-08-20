using System;
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

        /// <summary>True once a UI Toolkit screen is being shown on the panel in the world.</summary>
        public bool HasContent { get; private set; }

        /// <summary>Size of the cursor in panel coordinates, kept for the pointer mapping.</summary>
        public Vector2 PanelSize => _panelTexture != null
            ? new Vector2(_panelTexture.width, _panelTexture.height)
            : Vector2.zero;

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
        /// </summary>
        private void Capture()
        {
            var documents = UnityEngine.Object.FindObjectsOfType<UIDocument>();

            foreach (var document in documents)
            {
                if (document == null || !document.isActiveAndEnabled)
                    continue;

                PanelSettings settings = document.panelSettings;
                if (settings == null || _captured.Contains(settings))
                    continue;

                EnsureTexture();

                settings.targetTexture = _panelTexture;
                _captured.Add(settings);
                HasContent = true;

                MelonLogger.Msg($"[VRUiToolkit] '{document.name}' now renders into a texture " +
                                $"({_panelTexture.width}x{_panelTexture.height}) instead of the screen");
            }

            EnsureSurface();
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
                if (settings != null)
                    settings.targetTexture = null;
            }

            _captured.Clear();
            HasContent = false;

            if (_surface != null)
                _surface.gameObject.SetActive(false);
        }
    }
}
