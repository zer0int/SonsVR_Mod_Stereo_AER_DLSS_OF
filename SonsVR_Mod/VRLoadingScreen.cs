using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace SonsVRMod
{
    /// <summary>
    /// Shows the loading screen in the headset while a scene is being built.
    /// <para>
    /// Until now the rig simply stood down for the duration: no rendering, no submitting. That was
    /// the right answer to a stall, but it leaves the player looking at a frozen frame of a world
    /// that no longer exists, and sometimes at the outside of one, since whatever the camera was
    /// pointing at when the scene was torn down stays on screen.
    /// </para>
    /// <para>
    /// Instead the eyes keep rendering, but with nothing to see: an empty culling mask and a black
    /// clear, so the player is in darkness rather than in the wreckage of the last scene. The
    /// loading interface, which is drawn to the screen and so invisible through a lens, is moved onto
    /// a panel in front of them.
    /// </para>
    /// </summary>
    internal class VRLoadingScreen
    {
        private readonly System.Collections.Generic.List<Canvas> _converted =
            new System.Collections.Generic.List<Canvas>();

        private bool _active;
        private int _scanCountdown;
        private Vector3 _panelPosition;
        private Quaternion _panelRotation = Quaternion.identity;
        private bool _placed;

        public bool IsActive => _active;

        /// <summary>Called while the rig has no camera to follow, which is what loading looks like.</summary>
        public void Begin(Transform head, Camera eyeCamera)
        {
            if (!VRConfig.LoadingScreenInVR)
                return;

            if (!_active)
            {
                _active = true;
                _placed = false;
                _scanCountdown = 0;
                MelonLogger.Msg("[VRLoadingScreen] showing the loading screen in the headset");
            }

            Place(head);

            if (--_scanCountdown <= 0)
            {
                _scanCountdown = 15;
                Capture(eyeCamera);
            }

            Reapply();
        }

        /// <summary>Called once a camera is available again: the world is back, the panel is not needed.</summary>
        public void End()
        {
            if (!_active)
                return;

            _active = false;
            _converted.Clear();
            _placed = false;

            MelonLogger.Msg("[VRLoadingScreen] loading finished, releasing the panel");
        }

        /// <summary>
        /// Anchored once, in front of wherever the player was looking when loading began, and then
        /// left alone. A panel that chases the head cannot be looked away from.
        /// </summary>
        private void Place(Transform head)
        {
            if (_placed || head == null)
                return;

            Vector3 forward = head.forward;
            forward.y = 0f;

            if (forward.sqrMagnitude < 1e-4f)
                return;

            forward.Normalize();

            _panelPosition = head.position + forward * VRConfig.MenuDistance;
            _panelRotation = Quaternion.LookRotation(forward, Vector3.up);
            _placed = true;
        }

        /// <summary>
        /// Moves whatever the loading screen is drawing into world space.
        /// <para>
        /// Scanned repeatedly rather than once: the loading interface is built while the scene is
        /// being torn down and put back together, so it appears partway through and its pieces do not
        /// all arrive together.
        /// </para>
        /// </summary>
        private void Capture(Camera eyeCamera)
        {
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();

            foreach (var canvas in canvases)
            {
                if (canvas == null || !canvas.isActiveAndEnabled)
                    continue;

                if (canvas.renderMode == RenderMode.WorldSpace)
                    continue;

                if (canvas.transform.parent != null &&
                    canvas.transform.parent.GetComponentInParent<Canvas>() != null)
                    continue;

                var rect = canvas.GetComponent<RectTransform>();
                if (rect == null)
                    continue;

                Vector2 size = rect.rect.size;
                if (size.y <= 1f)
                    size = rect.sizeDelta;

                if (size.y <= 1f)
                    continue;

                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = eyeCamera;
                canvas.transform.localScale = Vector3.one * (VRConfig.MenuHeight / size.y);

                var scaler = canvas.GetComponent<CanvasScaler>();
                if (scaler != null)
                    scaler.enabled = false;

                _converted.Add(canvas);
                MelonLogger.Msg($"[VRLoadingScreen] '{canvas.name}' moved into world space " +
                                $"({size.x:F0}x{size.y:F0})");
            }
        }

        /// <summary>
        /// Holds the panels in place and in world space, every frame.
        /// <para>
        /// Re-asserted because these canvases belong to a scene that is actively being rebuilt around
        /// them: their parents move, and the game may put them back to screen space at any point.
        /// </para>
        /// </summary>
        private void Reapply()
        {
            if (!_placed)
                return;

            foreach (var canvas in _converted)
            {
                if (canvas == null)
                    continue;

                if (canvas.renderMode != RenderMode.WorldSpace)
                    canvas.renderMode = RenderMode.WorldSpace;

                canvas.transform.SetPositionAndRotation(_panelPosition, _panelRotation);
            }
        }
    }
}
