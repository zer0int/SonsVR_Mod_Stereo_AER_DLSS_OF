using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Image = UnityEngine.UI.Image;

namespace SonsVRMod
{
    /// <summary>
    /// Makes the flat menus usable from inside a headset.
    /// <para>
    /// A screen space canvas is drawn straight onto the output image and never passes through a
    /// camera, so it cannot appear in an eye texture at all: in the headset the menu simply is not
    /// there. Moving those canvases into world space puts them on a panel the eye cameras can see.
    /// </para>
    /// <para>
    /// Once the menu is a panel in the world, the pointer has to come back too. The interface still
    /// wants a mouse position in screen coordinates, so the cursor is moved across the panel in its
    /// own space and then projected through the same camera the canvas raycasts with. Going out and
    /// back through one camera keeps the two consistent whatever the panel is doing.
    /// </para>
    /// </summary>
    internal class VRMenu
    {
        /// <summary>True while the menu owns the mouse, so nothing else fights it for the cursor.</summary>
        public static bool Active { get; private set; }

        /// <summary>True while the left stick is driving the cursor rather than anything else.</summary>
        public bool IsSteering => Active;

        private readonly System.Collections.Generic.List<Canvas> _converted =
            new System.Collections.Generic.List<Canvas>();

        private readonly System.Collections.Generic.HashSet<string> _reportedNested =
            new System.Collections.Generic.HashSet<string>();

        private readonly VRUiToolkit _uiToolkit = new VRUiToolkit();
        private readonly VRLaser _laser = new VRLaser();
        private Canvas _primary;
        private RectTransform _primaryRect;
        private RectTransform _cursorRect;
        private Vector2 _cursorPosition;
        private bool _lastClick;
        private int _scanCountdown;
        private bool _panelPlaced;
        private Camera _eyeCamera;
        private Vector3 _panelPosition;
        private Quaternion _panelRotation = Quaternion.identity;

        /// <summary>
        /// Drives the menu for this frame. Called while the game, rather than the player, owns the
        /// camera, which is exactly the situation the menus appear in.
        /// </summary>
        public void Update(Transform head, Camera eyeCamera)
        {
            if (head == null || eyeCamera == null)
            {
                Release();
                return;
            }

            // Whether a menu is open is decided by the mouse cursor, not by the contents of a canvas.
            //
            // Counting controls looked like a good test and is not: the entries of this game's main
            // menu are its own components, not Unity selectables, so the canvas holding the menu
            // counted zero and was skipped while the one holding the cursor was kept. What every menu
            // does have in common is that the pointer is released for it, and gameplay and cutscenes
            // both keep it locked away.
            if (!IsMenuContext())
            {
                Release();
                return;
            }

            if (--_scanCountdown <= 0)
            {
                _scanCountdown = 30;
                ScanForMenus(eyeCamera);
            }

            // A menu session continues across menu changes.
            //
            // Swapping one screen for another destroys the old canvases before the new ones are
            // found, and for those few frames there is no primary. Treating that gap as "the menus
            // are gone" threw the anchor away, so each screen re-anchored in front of wherever the
            // player happened to be looking: the menus appeared to orbit the head instead of
            // standing still. The anchor only goes when gameplay actually resumes.
            if (_primary == null && !_panelPlaced)
            {
                Deactivate();
                return;
            }

            Active = true;
            _eyeCamera = eyeCamera;
            VRLaser.UpdateHandSwitch();

            PlacePanels(head);

            // The options screen and its like are built with UI Toolkit rather than canvases, so they
            // need their own treatment; they share the panel position so everything lines up.
            if (VRConfig.MenuUiToolkit)
                _uiToolkit.Update(_panelPosition, _panelRotation);

            MoveCursor();
            SendPointer(eyeCamera);
        }

        /// <summary>Gives the mouse back and forgets the panels, when there is no menu on screen.</summary>
        public void Release()
        {
            Deactivate();

            // Next time a menu comes up it gets anchored afresh, wherever the player is looking then.
            _panelPlaced = false;
        }

        /// <summary>
        /// Called when the game swaps camera, which is what a scene change looks like from here.
        /// <para>
        /// The anchor is a position in the world, so it only means anything within the scene it was
        /// measured in. Carrying it across a scene change put the title menu wherever the intro had
        /// left it, which from the new viewpoint was somewhere behind the player. Menu screens come
        /// and go inside a scene and must keep the anchor; a scene change must not.
        /// </para>
        /// </summary>
        public void OnSceneChanged(string sceneName)
        {
            // Keyed on the scene, not on the camera. Opening the options screen swaps the camera
            // without leaving the scene, and treating that as a scene change threw the anchor away
            // and put the options panel back in front of the player every time.
            if (sceneName == _sceneName)
                return;

            _sceneName = sceneName;
            _converted.Clear();
            _primary = null;
            _primaryRect = null;
            _scanCountdown = 0;
            Release();
        }

        private string _sceneName;

        /// <summary>
        /// Stops pointing and hands the mouse back, but keeps the anchor. Used for the gaps between
        /// one menu screen and the next, which are not the end of the menu session.
        /// </summary>
        private void Deactivate()
        {
            if (Active)
            {
                _uiToolkit.Release();
                DestroyCursor();
                _laser.Hide();
            }

            Active = false;
        }

        /// <summary>
        /// Finds canvases drawn straight to the screen and turns them into panels in the world.
        /// </summary>
        private void ScanForMenus(Camera eyeCamera)
        {
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();

            foreach (var canvas in canvases)
            {
                if (canvas == null || !canvas.isActiveAndEnabled)
                    continue;

                if (canvas.renderMode == RenderMode.WorldSpace)
                    continue;

                // Only top level canvases: a nested one takes its mode from its root, so converting
                // the root is enough. Reported, because a canvas that never gets converted and never
                // says why is exactly how the options screen stayed on the monitor unnoticed.
                if (canvas.transform.parent != null && canvas.transform.parent.GetComponentInParent<Canvas>() != null)
                {
                    if (_reportedNested.Add(canvas.name))
                        MelonLogger.Msg($"[VRMenu] '{canvas.name}' left alone: nested inside another canvas");

                    continue;
                }

                Convert(canvas, eyeCamera);
            }

            ChoosePrimary();
        }

        private void Convert(Canvas canvas, Camera eyeCamera)
        {
            var rect = canvas.GetComponent<RectTransform>();
            if (rect == null)
            {
                MelonLogger.Msg($"[VRMenu] '{canvas.name}' skipped: no rect transform");
                return;
            }

            // The resolved size, not sizeDelta. A full screen canvas is normally anchored stretched,
            // and then sizeDelta is zero while the real size lives in the resolved rect. Measuring the
            // wrong one made this bail out silently on exactly the canvases that matter most.
            Vector2 size = rect.rect.size;
            if (size.y <= 1f)
                size = rect.sizeDelta;

            if (size.y <= 1f)
            {
                MelonLogger.Msg($"[VRMenu] '{canvas.name}' skipped: no usable size " +
                                $"(rect {rect.rect.size}, sizeDelta {rect.sizeDelta})");
                return;
            }

            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = eyeCamera;

            // A canvas measures itself in pixels. Scaling by the height it should occupy in metres
            // turns those pixels into a panel of a sensible physical size.
            float scale = VRConfig.MenuHeight / size.y;
            canvas.transform.localScale = Vector3.one * scale;

            var scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler != null)
                scaler.enabled = false;

            // A canvas that appears after the panel was anchored joins it where it already stands.
            if (_panelPlaced)
                canvas.transform.SetPositionAndRotation(_panelPosition, _panelRotation);

            _converted.Add(canvas);
            MelonLogger.Msg($"[VRMenu] '{canvas.name}' moved into world space " +
                            $"({size.x:F0}x{size.y:F0} px, scale {scale:F4})");
        }

        /// <summary>
        /// Picks the canvas the cursor lives on: the one with the most things to click.
        /// <para>
        /// Sorting order was the wrong measure. It picked whichever canvas happened to be drawn last,
        /// which turned out to be the one holding the version label: nothing to point at, and the
        /// cursor lived on a panel the player had no reason to look at. What identifies the menu is
        /// that it is the one carrying the buttons.
        /// </para>
        /// </summary>
        private void ChoosePrimary()
        {
            Canvas best = null;
            int bestCount = -1;

            foreach (var canvas in _converted)
            {
                if (canvas == null || !canvas.isActiveAndEnabled)
                    continue;

                // Most controls wins, and with none anywhere the largest panel does: on this game
                // the entries are custom components, so a canvas counting zero can still be the menu.
                int count = CountClickable(canvas);
                if (count == 0 && bestCount <= 0)
                {
                    var rect = canvas.GetComponent<RectTransform>();
                    if (best == null || (rect != null && rect.rect.size.sqrMagnitude >
                                         best.GetComponent<RectTransform>().rect.size.sqrMagnitude))
                        best = canvas;

                    continue;
                }

                if (count > bestCount)
                {
                    bestCount = count;
                    best = canvas;
                }
            }

            bestCount = Mathf.Max(bestCount, 0);

            if (best == _primary)
                return;

            _primary = best;
            _primaryRect = best != null ? best.GetComponent<RectTransform>() : null;

            // The cursor belongs to the canvas it sits on, so a new menu needs a new one. Dropping
            // the reference is not enough: the old object stays parented to the old canvas and keeps
            // being drawn, which left a dead cursor behind on every click.
            DestroyCursor();

            if (_primary != null)
            {
                _cursorPosition = Vector2.zero;
                MelonLogger.Msg($"[VRMenu] Pointer attached to '{_primary.name}' ({bestCount} clickable elements)");
            }
        }

        /// <summary>
        /// Puts the panels where the player was looking when the menu appeared, and leaves them there.
        /// <para>
        /// Placed once rather than every frame on purpose. A panel that follows the head can never be
        /// looked away from or looked around, and the cursor becomes impossible to aim, because the
        /// surface it lives on moves with the same motion the player is using to point at it. Anchored
        /// to the world it behaves like a screen standing in the room.
        /// </para>
        /// </summary>
        private void PlacePanels(Transform head)
        {
            if (!_panelPlaced)
            {
                Vector3 forward = head.forward;
                forward.y = 0f;

                if (forward.sqrMagnitude < 1e-4f)
                    return;

                forward.Normalize();

                _panelPosition = head.position + forward * VRConfig.MenuDistance;

                // The panel does not inherit the raise applied to the viewpoint. Raising the camera
                // to clear the scenery would otherwise carry the panel up with it, leaving it at the
                // same height relative to the eyes and so changing nothing at all. Cancelling the
                // raise here makes the two independent: the viewpoint moves, the panel stays.
                _panelPosition.y += VRConfig.MenuPanelVerticalOffset - VRConfig.MenuHeightOffset;

                _panelRotation = Quaternion.LookRotation(forward, Vector3.up);
                _panelPlaced = true;

                MelonLogger.Msg($"[VRMenu] Panel anchored at {_panelPosition}, " +
                                $"facing {_panelRotation.eulerAngles.y:F0} degrees");
            }

            // Re-applied every frame, not written once. These canvases hang off objects that move
            // with the camera, so a world pose set once is dragged away by the parent on the very
            // next frame and the panel goes back to following the head. Rewriting it each frame
            // holds it in the room regardless of what its parent is doing.
            foreach (var canvas in _converted)
            {
                if (canvas == null)
                    continue;

                // A canvas that has gone back to screen space ignores its transform completely and
                // is painted straight onto the output image, which looks exactly like a panel that
                // insists on staying in front of the player. Writing a pose to it would do nothing,
                // so the mode is put back first.
                if (canvas.renderMode != RenderMode.WorldSpace)
                {
                    canvas.renderMode = RenderMode.WorldSpace;
                    canvas.worldCamera = _eyeCamera;
                    _revertCount++;
                }

                canvas.transform.SetPositionAndRotation(_panelPosition, _panelRotation);
            }

            ReportAnchorHealth();
        }

        private int _revertCount;
        private int _healthCountdown;

        /// <summary>
        /// Reports whether the anchor is actually holding: how often the canvases had to be put back
        /// into world space, and where the panel really ended up against where it was told to be.
        /// </summary>
        private void ReportAnchorHealth()
        {
            if (--_healthCountdown > 0 || _primary == null)
                return;

            _healthCountdown = 120;

            Vector3 actual = _primary.transform.position;
            float drift = Vector3.Distance(actual, _panelPosition);
            float turn = Quaternion.Angle(_primary.transform.rotation, _panelRotation);

            MelonLogger.Msg($"[VRMenu] '{_primary.name}' mode {_primary.renderMode}, " +
                            $"reverted {_revertCount} times, " +
                            $"off anchor by {drift:F2} m and {turn:F0} degrees");

            _revertCount = 0;
        }

        /// <summary>
        /// Moves the cursor across the panel with the left stick, kept inside its edges.
        /// </summary>
        private void MoveCursor()
        {
            if (_primaryRect == null)
                return;

            // Aiming a controller at the panel wins over steering a cursor across it.
            if (_laser.TryAim(_primaryRect, out Vector2 aimed))
            {
                _cursorPosition = aimed;
                EnsureCursorVisual();

                if (_cursorRect != null)
                    _cursorRect.anchoredPosition = _cursorPosition;

                return;
            }

            Vector2 stick = ReadPointerStick();

            _cursorPosition += stick * VRConfig.MenuCursorSpeed * Time.unscaledDeltaTime;

            Vector2 half = _primaryRect.sizeDelta * 0.5f;
            _cursorPosition.x = Mathf.Clamp(_cursorPosition.x, -half.x, half.x);
            _cursorPosition.y = Mathf.Clamp(_cursorPosition.y, -half.y, half.y);

            EnsureCursorVisual();

            if (_cursorRect != null)
                _cursorRect.anchoredPosition = _cursorPosition;
        }

        private bool _lastMenuContext;

        /// <summary>
        /// True when the game has handed the pointer to the player, which is what it does for menus
        /// and only for menus: gameplay and cutscenes keep the cursor locked and hidden.
        /// </summary>
        private bool IsMenuContext()
        {
            bool menu = Cursor.visible || Cursor.lockState == CursorLockMode.None;

            if (menu != _lastMenuContext)
            {
                _lastMenuContext = menu;
                MelonLogger.Msg($"[VRMenu] Menu context: {menu} " +
                                $"(cursor visible {Cursor.visible}, lock {Cursor.lockState})");
            }

            return menu;
        }

        /// <summary>
        /// How many controls this canvas carries, used only to decide which panel the pointer lives
        /// on when more than one is up.
        /// <para>
        /// Deliberately not asking whether each control is interactable at this instant. That test
        /// also fails while a menu is fading in, or whenever a parent group has been switched off for
        /// a moment, so the main menu was rejected on the very frame it appeared and only the canvas
        /// holding the cursor stayed visible.
        /// </para>
        /// </summary>
        private static int CountClickable(Canvas canvas)
        {
            return canvas.GetComponentsInChildren<Selectable>(false).Length;
        }

        private GameObject _cursorObject;

        /// <summary>Removes the cursor marker, so it cannot outlive the menu it was drawn on.</summary>
        private void DestroyCursor()
        {
            if (_cursorObject != null)
                UnityEngine.Object.Destroy(_cursorObject);

            _cursorObject = null;
            _cursorRect = null;
        }

        /// <summary>
        /// The stick driving the pointer: either one, whichever is being pushed further.
        /// <para>
        /// Both hands work in a menu because there is no reason to make the player remember which one
        /// does. Neither stick has anything else to do here: snap turning is suspended while a menu is
        /// up, and gameplay input is not injected at all.
        /// </para>
        /// </summary>
        private static Vector2 ReadPointerStick()
        {
            Vector2 left = NativeVRInput.GetVector2("ThumbstickLeft");
            Vector2 right = NativeVRInput.GetVector2("ThumbstickRight");

            Vector2 stick = right.sqrMagnitude > left.sqrMagnitude ? right : left;

            return stick.magnitude < 0.15f ? Vector2.zero : stick;
        }

        /// <summary>
        /// The click. While the beam is in use it belongs to the pointing hand alone, since the other
        /// trigger is what takes the beam across. Without a beam either trigger will do.
        /// </summary>
        private static bool ReadPointerClick()
        {
            if (VRConfig.LaserPointer)
                return VRLaser.ClickHeld();

            return NativeVRInput.GetBoolean("TriggerLeft_Press") ||
                   NativeVRInput.GetBoolean("TriggerRight_Press");
        }

        /// <summary>A small marker on the panel, so the player can see where they are pointing.</summary>
        private void EnsureCursorVisual()
        {
            if (_cursorRect != null || _primary == null)
                return;

            var go = new GameObject("VRMenuCursor");
            go.transform.SetParent(_primary.transform, false);

            var image = go.AddComponent<Image>();
            image.sprite = VRLaser.CircleSprite();
            image.color = new Color(1f, 1f, 1f, 0.9f);
            image.raycastTarget = false;

            _cursorObject = go;
            _cursorRect = go.GetComponent<RectTransform>();
            _cursorRect.sizeDelta = new Vector2(VRConfig.MenuCursorSize, VRConfig.MenuCursorSize);
            _cursorRect.anchorMin = new Vector2(0.5f, 0.5f);
            _cursorRect.anchorMax = new Vector2(0.5f, 0.5f);
            _cursorRect.pivot = new Vector2(0.5f, 0.5f);
        }

        /// <summary>
        /// Reports the cursor to the interface as a mouse.
        /// <para>
        /// The panel position is projected through the very camera the canvas raycasts with, so the
        /// position handed over lands back on the same point when the interface projects it again.
        /// The left trigger is the button.
        /// </para>
        /// </summary>
        private void SendPointer(Camera eyeCamera)
        {
            var mouse = Mouse.current;
            if (mouse == null || _primaryRect == null)
                return;

            Vector3 world = _primaryRect.TransformPoint(_cursorPosition);
            Vector3 screen = eyeCamera.WorldToScreenPoint(world);

            if (screen.z <= 0f)
                return;

            bool click = ReadPointerClick();

            StateEvent.From(mouse, out var eventPtr);
            mouse.position.WriteValueIntoEvent(new Vector2(screen.x, screen.y), eventPtr);
            mouse.leftButton.WriteValueIntoEvent(click ? 1f : 0f, eventPtr);
            InputSystem.QueueEvent(eventPtr);

            if (click != _lastClick)
            {
                _lastClick = click;
                MelonLogger.Msg($"[VRMenu] Trigger {(click ? "pressed" : "released")} at {_cursorPosition}");
            }
        }
    }
}
