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

        private readonly System.Collections.Generic.HashSet<string> _reportedOverlay =
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
        private bool _hudAdopted;
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

            Active = true;
            _eyeCamera = eyeCamera;
            VRLaser.UpdateHandSwitch();

            PlacePanels(head);

            // The options screen and its like are built with UI Toolkit rather than canvases, so they
            // need their own treatment; they share the panel position so everything lines up.
            //
            // Run before the check below, not after it. The pause screen is UI Toolkit and brings no
            // canvas of its own, so a session that gives up on finding a canvas first never reaches
            // the capture that would have found the screen, and the screen it was looking for stays
            // out of the headset for good.
            if (VRConfig.MenuUiToolkit)
                _uiToolkit.Update(_panelPosition, _panelRotation);

            // A menu session continues across menu changes.
            //
            // Swapping one screen for another destroys the old canvases before the new ones are
            // found, and for those few frames there is no primary. Treating that gap as "the menus
            // are gone" threw the anchor away, so each screen re-anchored in front of wherever the
            // player happened to be looking: the menus appeared to orbit the head instead of
            // standing still. The anchor only goes when gameplay actually resumes.
            if (_primary == null && !_panelPlaced && !_uiToolkit.HasContent)
            {
                Deactivate();
                return;
            }

            MoveCursor();
            SendPointer(eyeCamera);
        }

        /// <summary>Gives the mouse back and forgets the panels, when there is no menu on screen.</summary>
        public void Release()
        {
            Deactivate();

            // Next time a menu comes up it gets anchored afresh, wherever the player is looking then.
            _panelPlaced = false;

            // The display canvas goes back to the display, so the next menu has to take it again.
            _hudAdopted = false;
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

                // The performance overlay is not part of the game's interface and is never something
                // to point at, but it is a full screen canvas and so competes with the menus for the
                // pointer, and wins on size. Left on the screen it stays out of the headset, which is
                // where a debug overlay belongs.
                if (IsOverlay(canvas))
                {
                    if (_reportedOverlay.Add(canvas.name))
                        MelonLogger.Msg($"[VRMenu] '{canvas.name}' left alone: a debug overlay, not a menu");

                    continue;
                }

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

            AdoptHudCanvas(eyeCamera);
            ChoosePrimary();
        }

        /// <summary>
        /// Takes the canvas the game draws its display on into the menu panel.
        /// <para>
        /// The pause and save screens are children of it, and the mod had already moved it into world
        /// space itself, for the display. That is exactly why the scan above never picked it up: it
        /// only looks at canvases still being drawn to the screen, and this one had already been
        /// taken. The screens were visible and unreachable, riding on a surface locked to the head,
        /// so the beam was tested against a panel somewhere else entirely and passed through the menu
        /// the player was aiming at.
        /// </para>
        /// <para>
        /// Nothing is saved to be restored afterwards. The display code re-applies its own scale and
        /// position every frame, so it takes the canvas back on its own the moment the menu closes.
        /// </para>
        /// </summary>
        private void AdoptHudCanvas(Camera eyeCamera)
        {
            VRPlayer player = VRPlayer.Instance;
            if (player == null)
                return;

            Canvas hud = player.HudCanvas;
            if (hud == null || !hud.isActiveAndEnabled)
                return;

            // Everything below is re-applied on every scan rather than once when the canvas is first
            // seen. The display takes the canvas back the moment a menu closes: its own scale, its
            // own place and its own event camera. A second menu then inherits a canvas that is still
            // in the list, so it is still anchored where the panel is and the pointer still moves
            // correctly across it, while its raycasts go through the game's camera instead of the
            // eye. Every click lands somewhere else and nothing about the picture looks wrong.
            if (hud.worldCamera != eyeCamera)
                hud.worldCamera = eyeCamera;

            var rect = hud.GetComponent<RectTransform>();
            Vector2 size = rect != null ? rect.ResolvedSize() : Vector2.zero;

            if (size.y > 1f)
                hud.transform.localScale = Vector3.one * (VRConfig.MenuHeight / size.y);

            if (_panelPlaced)
                hud.transform.SetPositionAndRotation(_panelPosition, _panelRotation);

            if (!_converted.Contains(hud))
                _converted.Add(hud);

            if (_hudAdopted)
                return;

            _hudAdopted = true;

            MelonLogger.Msg($"[VRMenu] '{hud.name}' taken over from the display for the menu " +
                            $"({size.x:F0}x{size.y:F0} px, scale {hud.transform.localScale.x:F4}, " +
                            $"event camera '{eyeCamera.name}')");
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
            Vector2 size = rect.ResolvedSize();

            if (size.y <= 1f)
            {
                MelonLogger.Msg($"[VRMenu] '{canvas.name}' skipped: no usable size " +
                                $"(rect {rect.rect.size}, sizeDelta {rect.sizeDelta})");
                return;
            }

            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = eyeCamera;
            EnsureVisibleLayer(canvas, eyeCamera);

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
        /// Moves a panel onto a layer the eye cameras actually draw.
        /// <para>
        /// The eye cameras take their culling mask from the game's camera, and in play that camera
        /// leaves out the interface layer: it has no reason to draw a screen space canvas, since one
        /// is painted straight onto the output image without passing through any camera at all. Move
        /// that same canvas into world space and it is suddenly something a camera has to draw, on a
        /// layer that camera was told to ignore.
        /// </para>
        /// <para>
        /// The result is a panel that is present, laid out, lit and fully answering the pointer, and
        /// invisible. That is the worst shape this fault can take: the beam lands on it, the log
        /// reports controls under the cursor, and the player is aiming at a screen they cannot see.
        /// </para>
        /// <para>
        /// Read from the mask rather than written as a fixed layer, so a canvas already on a layer
        /// being drawn is left exactly as the game had it. At the title screen the camera draws
        /// everything and this does nothing at all, which is why the menus there always worked.
        /// </para>
        /// </summary>
        private void EnsureVisibleLayer(Canvas canvas, Camera eyeCamera)
        {
            if (canvas == null || eyeCamera == null)
                return;

            GameObject go = canvas.gameObject;
            int mask = eyeCamera.cullingMask;

            if ((mask & (1 << go.layer)) != 0)
                return;

            int was = go.layer;
            go.layer = 0;

            if (_movedLayers.Add(canvas.name))
            {
                MelonLogger.Msg($"[VRMenu] '{canvas.name}' moved from layer {was} to layer 0: " +
                                $"the eye cameras do not draw layer {was} " +
                                $"(mask 0x{mask:X8}), so the panel was invisible");
            }
        }

        private readonly System.Collections.Generic.HashSet<string> _movedLayers =
            new System.Collections.Generic.HashSet<string>();

        /// <summary>
        /// Picks the canvas the cursor lives on: the one with the most things to click.
        /// <para>
        /// Sorting order was the wrong measure. It picked whichever canvas happened to be drawn last,
        /// which turned out to be the one holding the version label: nothing to point at, and the
        /// cursor lived on a panel the player had no reason to look at. What identifies the menu is
        /// that it is the one carrying the buttons.
        /// </para>
        /// <para>
        /// Size was the wrong tie break for the case where nothing carries a Unity control, which is
        /// what the pause screen looks like from here: every canvas counted zero and the biggest one
        /// won, so the pointer went to a full screen overlay while the menu beside it stayed dead.
        /// Counting what a pointer could actually land on separates them, since a menu is built out
        /// of things meant to be hit and an overlay is not. Size decides only when even that ties.
        /// </para>
        /// </summary>
        private void ChoosePrimary()
        {
            Canvas best = null;
            int bestCount = -1;
            int bestTargets = -1;
            float bestArea = -1f;

            foreach (var canvas in _converted)
            {
                if (canvas == null || !canvas.isActiveAndEnabled)
                    continue;

                int count = CountClickable(canvas);
                int targets = CountRaycastTargets(canvas);

                var rect = canvas.GetComponent<RectTransform>();
                float area = rect != null ? rect.rect.size.sqrMagnitude : 0f;

                bool better = count > bestCount
                              || (count == bestCount && targets > bestTargets)
                              || (count == bestCount && targets == bestTargets && area > bestArea);

                if (!better)
                    continue;

                best = canvas;
                bestCount = count;
                bestTargets = targets;
                bestArea = area;
            }

            bestCount = Mathf.Max(bestCount, 0);
            bestTargets = Mathf.Max(bestTargets, 0);

            if (best == _primary)
                return;

            _primary = best;
            _primaryRect = best != null ? best.GetComponent<RectTransform>() : null;

            // The cursor belongs to the canvas it sits on, so a new menu needs a new one. Dropping
            // the reference is not enough: the old object stays parented to the old canvas and keeps
            // being drawn, which left a dead cursor behind on every click.
            DestroyCursor();

            // Every candidate and what it scored, because the choice between them is a judgement made
            // from counts and the counts are the only way to see why it went the way it did.
            foreach (var canvas in _converted)
            {
                if (canvas == null || !canvas.isActiveAndEnabled)
                    continue;

                var candidate = canvas.GetComponent<RectTransform>();
                Vector2 size = candidate != null ? candidate.ResolvedSize() : Vector2.zero;

                MelonLogger.Msg($"[VRMenu]   candidate '{canvas.name}': " +
                                $"{CountClickable(canvas)} controls, " +
                                $"{CountRaycastTargets(canvas)} hittable, " +
                                $"{size.x:F0}x{size.y:F0} px, holds {Children(canvas.transform)}" +
                                $"{(canvas == _primary ? "  <- chosen" : string.Empty)}");
            }

            ReportControls();

            if (_primary != null)
            {
                _cursorPosition = Vector2.zero;
                MelonLogger.Msg($"[VRMenu] Pointer attached to '{_primary.name}' " +
                                $"({bestCount} controls, {bestTargets} things a pointer can hit)");
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

                // Reported against the eyes rather than against the world, because where the panel
                // is in the world says nothing about whether it can be pointed at, and where it is
                // relative to the head says everything.
                float above = _panelPosition.y - head.position.y;

                MelonLogger.Msg($"[VRMenu] Panel anchored at {_panelPosition}, " +
                                $"facing {_panelRotation.eulerAngles.y:F0} degrees, " +
                                $"centre {above:+0.00;-0.00} m from the eyes, " +
                                $"{VRConfig.MenuHeight:F2} m tall at {VRConfig.MenuDistance:F2} m, " +
                                $"so it spans {above - VRConfig.MenuHeight * 0.5f:+0.00;-0.00} to " +
                                $"{above + VRConfig.MenuHeight * 0.5f:+0.00;-0.00} m");
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
                    _revertCount++;
                }

                // Held every frame alongside the pose, because it is just as easy to lose and far
                // harder to notice. A canvas raycasts through the camera named here, so one that has
                // been pointed back at the game's camera stays exactly where it should be, draws
                // exactly as it should, and quietly answers every click with a miss.
                if (canvas.worldCamera != _eyeCamera)
                {
                    canvas.worldCamera = _eyeCamera;
                    _cameraResetCount++;
                }

                EnsureVisibleLayer(canvas, _eyeCamera);

                canvas.transform.SetPositionAndRotation(_panelPosition, _panelRotation);
            }

            ReportAnchorHealth();
        }

        private int _revertCount;
        private int _cameraResetCount;
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
                            $"camera '{(_primary.worldCamera != null ? _primary.worldCamera.name : "none")}', " +
                            $"reverted {_revertCount} times, camera put back {_cameraResetCount} times, " +
                            $"off anchor by {drift:F2} m and {turn:F0} degrees");

            _revertCount = 0;
            _cameraResetCount = 0;
        }

        /// <summary>
        /// The surface the pointer lives on.
        /// <para>
        /// The menu canvas when there is one, and otherwise the surface the UI Toolkit screens are
        /// shown on. The pause screen is UI Toolkit and brings no canvas at all, so with only the
        /// first choice there was nothing for the beam to land on: it passed straight through the
        /// screen it was aimed at, no cursor was drawn, and no pointer was reported. The screen was
        /// visible and dead.
        /// </para>
        /// </summary>
        private RectTransform PointerRect => _primaryRect != null
            ? _primaryRect
            : _uiToolkit.SurfaceRect;

        /// <summary>
        /// Moves the cursor across the panel with the left stick, kept inside its edges.
        /// </summary>
        private void MoveCursor()
        {
            RectTransform host = PointerRect;
            if (host == null)
                return;

            // Aiming a controller at the panel wins over steering a cursor across it.
            if (_laser.TryAim(host, out Vector2 aimed))
            {
                _cursorPosition = aimed;
                EnsureCursorVisual(host);

                if (_cursorRect != null)
                    _cursorRect.anchoredPosition = _cursorPosition;

                return;
            }

            Vector2 stick = ReadPointerStick();

            _cursorPosition += stick * VRConfig.MenuCursorSpeed * Time.unscaledDeltaTime;

            Vector2 half = host.ResolvedSize() * 0.5f;
            _cursorPosition.x = Mathf.Clamp(_cursorPosition.x, -half.x, half.x);
            _cursorPosition.y = Mathf.Clamp(_cursorPosition.y, -half.y, half.y);

            EnsureCursorVisual(host);

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

        /// <summary>
        /// Where the controls of the chosen panel are, in the very coordinates the cursor is reported
        /// in.
        /// <para>
        /// "The button was not clickable" and "the pointer was not on the button" look identical from
        /// inside a headset, and the log could not tell them apart either: it said what was under the
        /// pointer, which is nothing useful when the answer is a background. Written in the panel's
        /// own space, a control's position sits next to the cursor position in the same units, and
        /// the two can simply be compared.
        /// </para>
        /// </summary>
        private void ReportControls()
        {
            if (_primaryRect == null || _primary == null)
                return;

            var controls = _primary.GetComponentsInChildren<Selectable>(false);
            if (controls.Length == 0)
            {
                MelonLogger.Msg($"[VRMenu]   '{_primary.name}' carries no controls at all");
                return;
            }

            int listed = 0;

            foreach (var control in controls)
            {
                if (control == null || listed++ >= 12)
                    continue;

                Vector3 local = _primaryRect.InverseTransformPoint(control.transform.position);

                MelonLogger.Msg($"[VRMenu]   control '{control.name}' at " +
                                $"({local.x:F0}, {local.y:F0}) px, " +
                                $"{(control.interactable ? "live" : "not interactable")}, " +
                                $"{(control.isActiveAndEnabled ? "on" : "off")}");
            }

            if (controls.Length > listed)
                MelonLogger.Msg($"[VRMenu]   ... and {controls.Length - listed} more controls");
        }

        /// <summary>
        /// The names of what a canvas carries directly. A canvas counting nothing hittable is either
        /// empty or full of things that refuse the pointer, and only its contents say which; without
        /// them a menu that never gets picked cannot even be identified in the log.
        /// </summary>
        private static string Children(Transform parent)
        {
            int count = parent.childCount;
            if (count == 0)
                return "nothing";

            string names = string.Empty;

            for (int i = 0; i < count && i < 4; i++)
                names += $"{(i > 0 ? ", " : string.Empty)}'{parent.GetChild(i).name}'";

            return count > 4 ? $"{count}: {names}, ..." : $"{count}: {names}";
        }

        /// <summary>
        /// How many drawn things on this canvas a pointer is allowed to land on. A menu is built out
        /// of them; an overlay that only reports numbers is not.
        /// </summary>
        private static int CountRaycastTargets(Canvas canvas)
        {
            int count = 0;

            foreach (var graphic in canvas.GetComponentsInChildren<Graphic>(false))
            {
                if (graphic != null && graphic.raycastTarget)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Whether this canvas is a debug overlay rather than part of the game's interface. Matched
        /// by name because that is what it is: a third party component dropped into the build, with
        /// nothing about it to recognise it by other than what it calls itself.
        /// </summary>
        private static bool IsOverlay(Canvas canvas)
        {
            return canvas.name.StartsWith("[Graphy]");
        }

        private GameObject _cursorObject;
        private Transform _cursorHost;

        /// <summary>Removes the cursor marker, so it cannot outlive the menu it was drawn on.</summary>
        private void DestroyCursor()
        {
            if (_cursorObject != null)
                UnityEngine.Object.Destroy(_cursorObject);

            _cursorObject = null;
            _cursorHost = null;
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

        /// <summary>
        /// Writes down what the interface itself finds under the pointer when a click is made.
        /// <para>
        /// A click that does nothing has two explanations that look identical from outside: the
        /// pointer is not where we believe it is, or it is exactly right and something invisible is
        /// standing in front of the menu and taking the press. Asking the interface what it hits, at
        /// the very position the click was sent at, tells the two apart.
        /// </para>
        /// </summary>
        private static void ReportRaycast(Vector2 screen)
        {
            var system = EventSystem.current;
            if (system == null)
            {
                MelonLogger.Msg("[VRMenu] no event system to ask what is under the pointer");
                return;
            }

            var data = new PointerEventData(system) { position = screen };
            var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();

            system.RaycastAll(data, results);

            if (results.Count == 0)
            {
                MelonLogger.Msg($"[VRMenu] nothing under the pointer at {screen}");
                return;
            }

            string found = string.Empty;

            for (int i = 0; i < results.Count && i < 3; i++)
            {
                RaycastResult result = results[i];
                string target = result.gameObject != null ? result.gameObject.name : "nothing";
                string raycaster = result.module != null ? result.module.name : "unknown";

                found += $"{(i > 0 ? ", " : string.Empty)}'{target}' through '{raycaster}'";
            }

            MelonLogger.Msg($"[VRMenu] under the pointer at {screen}: {found} ({results.Count} in all)");
        }

        /// <summary>
        /// A small marker on the panel, so the player can see where they are pointing. Rebuilt when
        /// the pointer moves to a different surface, since it is drawn as a child of that surface.
        /// </summary>
        private void EnsureCursorVisual(RectTransform host)
        {
            if (host == null)
                return;

            if (_cursorRect != null && _cursorHost == host.transform)
                return;

            DestroyCursor();

            var go = new GameObject("VRMenuCursor");
            go.transform.SetParent(host.transform, false);

            var image = go.AddComponent<Image>();
            image.sprite = VRLaser.CircleSprite();
            image.color = new Color(1f, 1f, 1f, 0.9f);
            image.raycastTarget = false;

            _cursorObject = go;
            _cursorHost = host.transform;
            _cursorRect = go.GetComponent<RectTransform>();

            MelonLogger.Msg($"[VRMenu] Cursor drawn on '{host.name}' " +
                            $"({host.sizeDelta.x:F0}x{host.sizeDelta.y:F0} px), " +
                            $"UI Toolkit content {_uiToolkit.HasContent}");
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
            RectTransform host = PointerRect;

            var mouse = Mouse.current;
            if (mouse == null || host == null)
                return;

            Vector3 world = host.TransformPoint(_cursorPosition);

            // A UI Toolkit screen is told where the pointer is in its own terms. The mouse position
            // below still has to be sent, because that is what makes the runtime handle a pointer
            // event at all, but the position it carries means nothing to a screen drawn into a
            // texture: the panel takes the point from here instead.
            _uiToolkit.SetPointer(world);

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

                if (click)
                    ReportRaycast(new Vector2(screen.x, screen.y));
            }
        }
    }
}
