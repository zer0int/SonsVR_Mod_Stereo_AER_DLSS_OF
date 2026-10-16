using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace SonsVRMod
{
    /// <summary>
    /// V169L: stabilize native tooltip SCREEN PIXEL input before V169D projects it
    /// onto the V116 HUD. Unlike V169E's first-frame indefinite canvas-local latch,
    /// initial acquisition waits for native positioning to settle and sustained
    /// large changes relock to the real source pixel position. Native UGUI subtree
    /// and hierarchy (including backdrop) remain unchanged.
    /// V169E required-item billboarding and V169D cooking depth remain unchanged.
    /// No camera, canvas, raycaster, visibility, input or scene hierarchy changes.
    /// See V169E_TEST_PLAN.md for evidence and the two independent A/B switches.
    /// </summary>
    internal static class UiPlacementCorrectionsV169E
    {
        // Config-at-top A/B switches: change these individually when diagnosing regressions.
        internal static bool RepairInvalidTooltipWorldPositions = true;
        internal static bool BringCookingRecipeListCloser = true;
        internal static float CookingRecipeDepthMeters = 0.75f; // V116 plane = 1.20 m.

        // V169L A/B: F7 disables/enables native screen-pixel signal stabilization;
        // the baseline pixel->HUD projection remains identical in both modes.
        internal static bool LatchTooltipWhileHovering = true;
        internal static bool FlattenRequiredItemMessages = true;

        // V169L: reuse the same F7 key, no new diagnostic key bindings.
        // F7 toggles ONLY tooltip latching.  Tooltip pixel->world repair,
        // blueprint message unfolding and cooking depth remain untouched.
        // Starting mode is the headset-confirmed stable-tooltip V169E path.
        internal static void TickLatchAB()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.f7Key.wasPressedThisFrame) return;
            if (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed ||
                keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed) return;
            LatchTooltipWhileHovering = !LatchTooltipWhileHovering;
            // Drop the previous source-pixel sample immediately. The regular V169D
            // correction will run on the next native tooltip position write.
            InventoryInfo.ClearLatch();
            WorldTooltip.ClearLatch();
            MelonLogger.Msg("[V169L TOOLTIP AB] F7: source-pixel stabilizer=" +
                (LatchTooltipWhileHovering ? "ON (settling + deadzone)" : "OFF (V169D live pixels)") +
                "; blueprint unfolding, cooking and other HUD code unchanged.");
        }
        // At ~90 degrees the native required-item text is edge-on. Preserve native
        // mild rotations/entrance animations; intervene only on obvious folding.
        internal static float FoldedMessageAngleDegrees = 55f;

        // Input-space pixel thresholds (desktop native coordinates, NOT HUD world metres).
        // Native tooltip spawn coordinates can begin near screen center, then jump
        // hundreds of pixels to their actual hover position: never latch that first
        // spawn coordinate indefinitely. Physiological cursor drift is much smaller.
        internal static float TooltipSourceDeadzonePixels = 24f;
        internal static float TooltipSourceAcquisitionTolerancePixels = 36f;
        internal static int TooltipSourceSettleSamples = 3;

        private sealed class TooltipTarget
        {
            internal readonly string Path;
            internal readonly string Label;
            internal RectTransform Rect;
            internal int Corrections;
            internal Vector2 LastRawPixels;
            internal Vector3 LastCorrectedWorld;
            internal bool Logged;
            internal bool Latched;
            internal string LastTitle;
            internal Vector2 StableRawPixels;
            internal Vector2 CandidateRawPixels;
            internal int CandidateFrames;
            internal int LatchStarts;
            internal int LatchReuses;
            internal int SourceRelocks;
            internal int ContentChanges;
            internal TooltipTarget(string label, string path) { Label = label; Path = path; }
            internal void ClearLatch()
            {
                Latched = false;
                CandidateFrames = 0;
                LastTitle = null;
            }
        }

        private static readonly TooltipTarget InventoryInfo = new TooltipTarget(
            "InventoryItemInfoView", "ToggleableHUD/Inventory/InventoryItemInfoView");
        private static readonly TooltipTarget WorldTooltip = new TooltipTarget(
            "ToolTipInfoView", "ToggleableHUD/ToolTip/ToolTipInfoView");

        private static Canvas _canvas;
        private static RectTransform _cookingRoot;
        private static RectTransform _messageList;
        private static int _messageTurns;
        private static int _messageRootCount;
        private static bool _messageTurnLogged;
        private static bool _recipeApplied;
        private static bool _recipeHasBase;
        private static Vector3 _recipeBaseLocalPosition;
        private static Vector3 _recipeBaseLocalScale;
        private static Vector3 _recipeLastAppliedLocalPosition;
        private static Vector3 _recipeLastAppliedLocalScale;
        private static float _nextResolveTime;
        private static float _lastErrorTime = -100f;
        private static int _recipeApplications;

        internal static void Tick(Canvas canvas, Transform head)
        {
            if (canvas == null || head == null || canvas.renderMode != RenderMode.WorldSpace ||
                !canvas.gameObject.activeInHierarchy)
            {
                InventoryInfo.ClearLatch();
                WorldTooltip.ClearLatch();
                return;
            }

            try
            {
                if (_canvas != canvas)
                {
                    // New scene/UI canvas: never let previously cached native transforms
                    // or positions bleed into the next instance.
                    _canvas = canvas;
                    InventoryInfo.Rect = null;
                    WorldTooltip.Rect = null;
                    InventoryInfo.Logged = false;
                    WorldTooltip.Logged = false;
                    InventoryInfo.ClearLatch();
                    WorldTooltip.ClearLatch();
                    _messageList = null;
                    _messageTurnLogged = false;
                    _cookingRoot = null;
                    _recipeHasBase = false;
                    _recipeApplied = false;
                    _nextResolveTime = 0f;
                }

                if (Time.unscaledTime >= _nextResolveTime &&
                    (InventoryInfo.Rect == null || WorldTooltip.Rect == null ||
                     _cookingRoot == null || _messageList == null))
                {
                    _nextResolveTime = Time.unscaledTime + 1f;
                    if (InventoryInfo.Rect == null) InventoryInfo.Rect = Resolve(canvas, InventoryInfo.Path);
                    if (WorldTooltip.Rect == null) WorldTooltip.Rect = Resolve(canvas, WorldTooltip.Path);
                    if (_cookingRoot == null) _cookingRoot = Resolve(canvas, "ToggleableHUD/Inventory/CookingRecipesView");
                    if (_messageList == null) _messageList = Resolve(canvas, "ToggleableHUD/MessageList");
                }

                if (RepairInvalidTooltipWorldPositions)
                {
                    CorrectInvalidScreenPixelPosition(InventoryInfo, canvas, head);
                    CorrectInvalidScreenPixelPosition(WorldTooltip, canvas, head);
                }
                else
                {
                    InventoryInfo.ClearLatch();
                    WorldTooltip.ClearLatch();
                }
                if (FlattenRequiredItemMessages)
                    CorrectFoldedRequiredItemMessages(canvas, head);
                if (BringCookingRecipeListCloser)
                    AdjustCookingRecipeDepth(canvas, head);
                else
                    RestoreCookingRecipeIfOwned();
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime - _lastErrorTime > 5f)
                {
                    _lastErrorTime = Time.unscaledTime;
                    MelonLogger.Warning("[V169E UI] Placement exception: " + ex.GetBaseException().Message);
                }
            }
        }

        private static RectTransform Resolve(Canvas canvas, string path)
        {
            Transform child = canvas.transform.Find(path);
            return child != null ? child.GetComponent<RectTransform>() : null;
        }

        private static void CorrectInvalidScreenPixelPosition(TooltipTarget target, Canvas canvas, Transform head)
        {
            RectTransform rt = target.Rect;
            // Native owns visibility; a hidden popup ends the hover lifetime. This
            // also prevents a previous object's anchor surviving a later open.
            if (rt == null || !rt.gameObject.activeInHierarchy)
            {
                target.ClearLatch();
                return;
            }
            if (!LatchTooltipWhileHovering && target.Latched)
                target.ClearLatch(); // Exact V169D correction path when the latch is disabled.
            Vector3 raw = rt.position;
            if (!Finite(raw.x) || !Finite(raw.y) || !Finite(raw.z)) return;

            // F9 16:00:38: native root world=(1921,1019,2.57) despite HMD=(-566,96,588).
            // This is consistent with desktop *pixel* coordinates written as world metres.
            // Repair only that signature; genuinely world-positioned tips are left alone.
            if (Vector3.Distance(raw, head.position) < 20f ||
                Screen.width < 100 || Screen.height < 100 ||
                raw.x < -32f || raw.y < -32f ||
                raw.x > Screen.width + 32f || raw.y > Screen.height + 32f)
                return;

            RectTransform canvasRt = canvas.GetComponent<RectTransform>();
            if (canvasRt == null) return;
            Rect logical = canvasRt.rect;
            if (logical.width < 100f || logical.height < 100f) return;

            // Title is an identity proxy only; no gaze/raycast/native show/hide
            // handlers are modified. A new item or inactive view clears the anchor.
            string title = ReadTooltipTitle(target);
            bool changed = target.Latched && title != null &&
                target.LastTitle != null &&
                !string.Equals(title, target.LastTitle, StringComparison.Ordinal);
            if (changed)
            {
                target.ContentChanges++;
                target.ClearLatch();
            }
            if (target.Latched && target.LastTitle == null && title != null)
                target.LastTitle = title;

            // V169L: stabilize the *native source pixel coordinate*, before
            // doing ANY pixel-to-world projection. This does NOT latch a world
            // transform or change the native tooltip's children (backdrop, text,
            // icons and their layout remain managed by the game).
            Vector2 nativePixels = new Vector2(raw.x, raw.y);
            Vector2 projectedPixels = LatchTooltipWhileHovering
                ? StabilizeNativeSourcePixels(target, nativePixels, title)
                : nativePixels;

            // V169D projection retained exactly; now it receives a stabilized
            // source coordinate instead of an indefinitely frozen world anchor.
            Vector3 local = new Vector3(
                logical.xMin + projectedPixels.x / Screen.width * logical.width,
                logical.yMin + projectedPixels.y / Screen.height * logical.height,
                -2f);
            Vector3 corrected = canvasRt.TransformPoint(local);
            if (Vector3.Distance(corrected, head.position) > 4f) return;
            rt.position = corrected;
            target.LastRawPixels = new Vector2(raw.x, raw.y);
            target.LastCorrectedWorld = corrected;
            target.Corrections++;
            if (!target.Logged)
            {
                target.Logged = true;
                MelonLogger.Msg($"[V169L UI][{target.Label}] corrected pixel-as-world origin " +
                    $"raw={raw} screen={Screen.width}x{Screen.height} canvas={logical} " +
                    $"mapped={corrected} headDistance={Vector3.Distance(corrected, head.position):F3}m");
            }
        }

        private static Vector2 StabilizeNativeSourcePixels(TooltipTarget target,
            Vector2 nativePixels, string title)
        {
            // Initial tooltip placement is often a transient CENTER-OF-SCREEN
            // value, not the final mouse/gaze position. Keep live behavior during
            // the first few frames instead of anchoring that transient forever.
            if (!target.Latched)
            {
                if (target.CandidateFrames == 0 ||
                    Vector2.Distance(nativePixels, target.CandidateRawPixels) >
                        TooltipSourceAcquisitionTolerancePixels)
                {
                    target.CandidateRawPixels = nativePixels;
                    target.CandidateFrames = 1;
                }
                else
                {
                    target.CandidateFrames++;
                }
                if (target.CandidateFrames >= TooltipSourceSettleSamples)
                {
                    target.StableRawPixels = nativePixels;
                    target.Latched = true;
                    target.LastTitle = title;
                    target.CandidateFrames = 0;
                    target.LatchStarts++;
                }
                return nativePixels;
            }

            float deviation = Vector2.Distance(nativePixels, target.StableRawPixels);
            if (deviation <= TooltipSourceDeadzonePixels)
            {
                // Tiny involuntary cursor/head motion is ignored while the
                // hovered item remains active; no time-based interpolation.
                target.CandidateFrames = 0;
                target.LatchReuses++;
                return target.StableRawPixels;
            }

            // A substantial *sustained* move is a real native reposition, not
            // heartbeat jitter. Two/three samples protect against transient
            // 1920x1080-style spawn positions and a one-frame noisy cursor.
            if (target.CandidateFrames == 0 ||
                Vector2.Distance(nativePixels, target.CandidateRawPixels) >
                    TooltipSourceAcquisitionTolerancePixels)
            {
                target.CandidateRawPixels = nativePixels;
                target.CandidateFrames = 1;
            }
            else target.CandidateFrames++;

            if (target.CandidateFrames >= TooltipSourceSettleSamples)
            {
                target.StableRawPixels = nativePixels;
                target.CandidateFrames = 0;
                target.SourceRelocks++;
                return nativePixels;
            }
            target.LatchReuses++;
            return target.StableRawPixels;
        }

        private static string ReadTooltipTitle(TooltipTarget target)
        {
            RectTransform rt = target.Rect;
            if (rt == null) return null;
            // Inventory's "BackgroundImage/TitleText" is confirmed by the F9
            // capture. Other native views may use different names; an unrecognized
            // title still gets an active-lifetime latch without fabricated identity.
            Transform title = rt.Find("BackgroundImage/TitleText");
            if (title == null) title = rt.Find("TitleText");
            if (title == null) return null;
            var text = title.GetComponent<Il2CppTMPro.TMP_Text>();
            if (text == null || string.IsNullOrEmpty(text.text)) return null;
            return text.text;
        }

        private static void CorrectFoldedRequiredItemMessages(Canvas canvas, Transform head)
        {
            RectTransform list = _messageList;
            if (list == null || !list.gameObject.activeInHierarchy) return;
            Quaternion targetRotation = canvas.transform.rotation;
            int activeRoots = 0;
            // Only DIRECT children matching the captured blueprint requirement
            // family. Never rotate the whole MessageList, a world building, or
            // cooking/interaction HUD. No scene-wide component searches.
            for (int i = 0; i < list.childCount; i++)
            {
                Transform child = list.GetChild(i);
                if (child == null || !child.gameObject.activeInHierarchy ||
                    !child.name.StartsWith("RequiredItem(Clone)", StringComparison.Ordinal)) continue;
                Transform textRoot = child.Find("AnimationRoot/Description");
                if (textRoot == null || !textRoot.gameObject.activeInHierarchy) continue;
                var description = textRoot.GetComponent<Il2CppTMPro.TMP_Text>();
                if (description == null || !description.enabled ||
                    string.IsNullOrEmpty(description.text)) continue;
                activeRoots++;
                // Only fix roots actually on the unified player HUD, and only
                // when native rotation has folded them substantially edge-on.
                Vector3 h = head.InverseTransformPoint(child.position);
                if (h.z < 0.35f || h.z > 2.50f || Mathf.Abs(h.x) > 2.5f ||
                    Mathf.Abs(h.y) > 2.5f) continue;
                float delta = Quaternion.Angle(child.rotation, targetRotation);
                if (delta < FoldedMessageAngleDegrees) continue;
                // World rotation keeps the pivot/world position fixed and avoids
                // shifting the layout group's sibling positions. Native owns
                // text, opacity, timing, and removal of message entries.
                child.rotation = targetRotation;
                _messageTurns++;
                if (!_messageTurnLogged)
                {
                    _messageTurnLogged = true;
                    MelonLogger.Msg($"[V169E UI][RequiredItem] unfolded {child.name} " +
                        $"delta={delta:F1}deg text='{description.text}' " +
                        $"headLocal={h}; only active RequiredItem clones affected");
                }
            }
            _messageRootCount = activeRoots;
        }

        private static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);

        private static void AdjustCookingRecipeDepth(Canvas canvas, Transform head)
        {
            RectTransform rt = _cookingRoot;
            if (rt == null) { _recipeApplied = false; _recipeHasBase = false; return; }
            if (!rt.gameObject.activeInHierarchy)
            {
                RestoreCookingRecipeIfOwned();
                _recipeHasBase = false;
                return;
            }

            float originalDepth = UnifiedHudWorldPlaneV116.DistanceMeters;
            float targetDepth = Mathf.Clamp(CookingRecipeDepthMeters, 0.50f, originalDepth);
            float ratio = targetDepth / originalDepth;
            if (ratio > 0.999f) { RestoreCookingRecipeIfOwned(); return; }
            Transform parent = rt.parent;
            if (parent == null) return;

            // After our own write, compare against exactly the last applied local
            // transform. This distinguishes a native layout/animation write from our
            // own prior offset while the head/canvas continues moving each frame.
            if (!_recipeHasBase)
            {
                _recipeBaseLocalPosition = rt.localPosition;
                _recipeBaseLocalScale = rt.localScale;
                _recipeHasBase = true;
            }
            else if (_recipeApplied &&
                (rt.localPosition - _recipeLastAppliedLocalPosition).sqrMagnitude > 0.01f)
            {
                // Native may rewrite only x/y while leaving our synthetic z
                // offset in place. Never adopt that offset as the new baseline.
                _recipeBaseLocalPosition = new Vector3(
                    rt.localPosition.x, rt.localPosition.y, _recipeBaseLocalPosition.z);
                if (_recipeApplications < 4)
                    MelonLogger.Msg("[V169E UI][Cooking] native changed recipe pivot; baseline refreshed");
            }
            else if (!_recipeApplied)
            {
                _recipeBaseLocalPosition = rt.localPosition;
                _recipeBaseLocalScale = rt.localScale;
            }

            // Do not override a native hidden/scale-zero animation. No animator is
            // disabled and no scrolling/layout component is moved or reparented.
            if (_recipeBaseLocalScale.sqrMagnitude < 0.1f) return;
            if (_recipeApplied &&
                (rt.localScale - _recipeLastAppliedLocalScale).sqrMagnitude > 0.01f &&
                rt.localScale.sqrMagnitude < 0.3f)
                return;

            Vector3 authoredPivotWorld = parent.TransformPoint(_recipeBaseLocalPosition);
            Vector3 originalView = authoredPivotWorld - head.position;
            // Only modify a root already sitting on the V116 HUD plane.
            if (Vector3.Dot(originalView, head.forward) < 0.8f ||
                Vector3.Dot(originalView, head.forward) > 1.7f) return;

            // Scaling about the HMD (rather than the tooltip pivot) preserves the
            // panel's *angular* dimensions and screen position while moving it closer.
            rt.position = head.position + originalView * ratio;
            rt.localScale = _recipeBaseLocalScale * ratio;
            _recipeLastAppliedLocalPosition = rt.localPosition;
            _recipeLastAppliedLocalScale = rt.localScale;
            _recipeApplied = true;
            if (++_recipeApplications == 1)
                MelonLogger.Msg($"[V169E UI][Cooking] moved CookingRecipesView " +
                    $"{originalDepth:F2}m -> {targetDepth:F2}m (scale x{ratio:F3}); " +
                    "native cogwheel, parent canvas and cursor untouched");
        }

        private static void RestoreCookingRecipeIfOwned()
        {
            if (!_recipeApplied || !_recipeHasBase || _cookingRoot == null) return;
            // Only restore values if the root still holds our own transform. Do
            // not overwrite an authored native layout or closing animation.
            if ((_cookingRoot.localPosition - _recipeLastAppliedLocalPosition).sqrMagnitude < 0.01f)
                _cookingRoot.localPosition = _recipeBaseLocalPosition;
            if ((_cookingRoot.localScale - _recipeLastAppliedLocalScale).sqrMagnitude < 0.01f)
                _cookingRoot.localScale = _recipeBaseLocalScale;
            _recipeApplied = false;
        }

        internal static string SnapshotState()
        {
            return $"V169L tooltipRepair={RepairInvalidTooltipWorldPositions} " +
                $"inventoryFixes={InventoryInfo.Corrections} raw={InventoryInfo.LastRawPixels} " +
                $"tooltipFixes={WorldTooltip.Corrections} raw={WorldTooltip.LastRawPixels} " +
                $"latchOn={LatchTooltipWhileHovering} " +
                $"inventoryPixelAnchor={InventoryInfo.Latched}/{InventoryInfo.LatchStarts}/{InventoryInfo.LatchReuses} relocks={InventoryInfo.SourceRelocks} stable={InventoryInfo.StableRawPixels} pending={InventoryInfo.CandidateFrames} title='{InventoryInfo.LastTitle}' changes={InventoryInfo.ContentChanges} " +
                $"worldTipPixelAnchor={WorldTooltip.Latched}/{WorldTooltip.LatchStarts}/{WorldTooltip.LatchReuses} relocks={WorldTooltip.SourceRelocks} stable={WorldTooltip.StableRawPixels} pending={WorldTooltip.CandidateFrames} title='{WorldTooltip.LastTitle}' changes={WorldTooltip.ContentChanges} " +
                $"unfold={FlattenRequiredItemMessages} activeRequirements={_messageRootCount} turned={_messageTurns} " +
                $"cookingNear={BringCookingRecipeListCloser} target={CookingRecipeDepthMeters:F2}m " +
                $"cookingActive={(_cookingRoot != null && _cookingRoot.gameObject.activeInHierarchy)} " +
                $"cookingApplied={_recipeApplied} applications={_recipeApplications}";
        }
    }
}
