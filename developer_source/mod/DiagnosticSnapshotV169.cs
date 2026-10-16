using System;
using System.IO;
using System.Text;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Rendering.HighDefinition;

namespace SonsVRMod
{
    /// <summary>
    /// V169C: passive until a keyboard F9 rising edge. No Harmony patches, no
    /// background FindObjectsOfType scans, no object/property mutation, no GPU
    /// readbacks. Captures the UI/camera/post-FX state when a rare issue is seen.
    /// This is a bounded diagnostic snapshot, NOT a serialization of the whole game.
    /// </summary>
    internal static class DiagnosticSnapshotV169
    {
        private const int MaxComponentsPerKind = 4000;
        private const int MaxUiNodes = 3000;
        private static bool _capturing;

        internal static void Tick()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.f9Key.wasPressedThisFrame || _capturing) return;
            // Avoid clashing with the configurable Ctrl+Alt+F9 settings shortcut.
            if (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed ||
                keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed) return;
            Capture();
        }

        private static void Capture()
        {
            _capturing = true;
            try
            {
                string root = Path.Combine(Application.persistentDataPath, "SonsVR_Diagnostics");
                Directory.CreateDirectory(root);
                string name = "V169L_UI_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") +
                              "_frame" + Time.frameCount + ".txt";
                string path = Path.Combine(root, name);
                using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
                {
                    writer.WriteLine("SONSVR V169L ON-DEMAND UI STATE SNAPSHOT");
                    writer.WriteLine("UTC=" + DateTime.UtcNow.ToString("O") +
                        " local=" + DateTime.Now.ToString("O") +
                        " scene=" + SceneManager.GetActiveScene().name +
                        " frame=" + Time.frameCount +
                        " timeScale=" + Time.timeScale +
                        " unscaledTime=" + Time.unscaledTime +
                        " screen=" + Screen.width + "x" + Screen.height);
                    writer.WriteLine("NOTE: UI/gameplay diagnostic ONLY; it does not serialize all world/game state.");
                    writer.WriteLine("V169L F7 tooltip source-pixel stabilizer: " +
                        (UiPlacementCorrectionsV169E.LatchTooltipWhileHovering ? "ON / settles + deadzone" : "OFF / V169D live") +
                        "; pixel-space coordinate repair and blueprint unfolding remain enabled.");
                    writer.WriteLine("Original tarp tutorial backdrop enabled=" + VRPlayer.TarpTutorialBackgroundEnabled);
                    Safe(writer, "PLAYER", () => DumpPlayer(writer));
                    Safe(writer, "CAMERAS", () => DumpCameras(writer));
                    Safe(writer, "VOLUMES", () => DumpVolumes(writer));
                    Safe(writer, "CANVASES", () => DumpCanvases(writer));
                    Safe(writer, "UI_TOOLKIT", () => DumpUiToolkit(writer));
                    Safe(writer, "TMP_TEXT", () => DumpTmpText(writer));
                    Safe(writer, "UGUI_TEXT", () => DumpUguiText(writer));
                    Safe(writer, "UI_HIERARCHY", () => DumpInterestingUiNodes(writer));
                    // V169C: focused head-relative geometry / parent state for HUD roots.
                    // Bounded, on F9 only; no changes to UI/cameras/colliders.
                    Safe(writer, "FOCUSED_HUD_OVERLAYS", () => DumpFocusedHudOverlaysV169C(writer));
                    // V169G: identify the actual black renderer before any further disable attempt.
                    // This potentially expensive scan runs ONLY during an F9 snapshot.
                    Safe(writer, "BLACK_SURFACE_PROVENANCE_V169G", () => BlackBackdropProbeV169G.Capture(writer));
                    writer.WriteLine(UiPlacementCorrectionsV169E.SnapshotState());
                    writer.WriteLine("V169H restoredSyntheticTarpTutorialBackdropEnabled=" + VRPlayer.TarpTutorialBackgroundEnabled);
                }
                MelonLogger.Warning("[V169L SNAPSHOT] Saved: " + path +
                    " (attach with Latest.log; F9 again for another timestamped capture)");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[V169L SNAPSHOT] Failed: " + e);
            }
            finally { _capturing = false; }
        }

        private static void Safe(StreamWriter w, string label, Action action)
        {
            w.WriteLine();
            w.WriteLine("============== " + label + " ==============");
            try { action(); } catch (Exception e) { w.WriteLine("SECTION_ERROR: " + e); }
        }

        private static void DumpPlayer(StreamWriter w)
        {
            VRPlayer player = VRPlayer.Instance;
            w.WriteLine("VRPlayer=" + (player != null) +
                " pauseConfirmed=" + VRPlayer.IsPauseMenuOpenConfirmed +
                " suppressPauseBlur=" + VRPlayer.SuppressPauseMenuBlur);
            if (player == null || player.StereoRender == null) return;
            var render = player.StereoRender;
            if (render.Head != null)
                w.WriteLine("HMD " + Pose(render.Head));
            if (render.LeftCam != null)
                w.WriteLine("LEFT_EYE " + CameraState(render.LeftCam));
            if (render.RightCam != null)
                w.WriteLine("RIGHT_EYE " + CameraState(render.RightCam));
        }

        private static void DumpCameras(StreamWriter w)
        {
            var list = UnityEngine.Object.FindObjectsOfType<Camera>(true);
            w.WriteLine("count=" + list.Length);
            for (int i = 0; i < list.Length && i < MaxComponentsPerKind; ++i)
            {
                var c = list[i];
                try { if (c != null) w.WriteLine(CameraState(c)); }
                catch (Exception e) { w.WriteLine("camera[" + i + "] " + e.Message); }
            }
        }

        private static string CameraState(Camera c)
        {
            var hd = c.GetComponent<HDAdditionalCameraData>();
            return PathOf(c.transform) + " active=" + c.gameObject.activeInHierarchy +
                " enabled=" + c.enabled + " depth=" + c.depth +
                " mask=0x" + c.cullingMask.ToString("X8") +
                " pos=" + c.transform.position + " rot=" + c.transform.eulerAngles +
                " near=" + c.nearClipPlane + " far=" + c.farClipPlane +
                " FOV=" + c.fieldOfView +
                " HDRP=" + (hd != null ? ("volumeMask=0x" + hd.volumeLayerMask.value.ToString("X8")) : "none");
        }

        private static void DumpVolumes(StreamWriter w)
        {
            var list = UnityEngine.Object.FindObjectsOfType<Volume>(true);
            w.WriteLine("count=" + list.Length);
            for (int i = 0; i < list.Length && i < MaxComponentsPerKind; ++i)
            {
                try
                {
                    Volume v = list[i];
                    if (v == null) continue;
                    w.WriteLine(PathOf(v.transform) + " enabled=" + v.enabled +
                        " active=" + v.gameObject.activeInHierarchy +
                        " weight=" + v.weight + " priority=" + v.priority +
                        " global=" + v.isGlobal + " layer=" + v.gameObject.layer +
                        " profile=" + (v.sharedProfile != null ? v.sharedProfile.name : "null"));
                }
                catch (Exception e) { w.WriteLine("volume[" + i + "] " + e.Message); }
            }
        }

        private static void DumpCanvases(StreamWriter w)
        {
            var list = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
            w.WriteLine("count=" + list.Length);
            for (int i = 0; i < list.Length && i < MaxComponentsPerKind; ++i)
            {
                try
                {
                    Canvas c = list[i];
                    if (c == null) continue;
                    CanvasGroup group = c.GetComponent<CanvasGroup>();
                    w.WriteLine(PathOf(c.transform) + " enabled=" + c.enabled +
                        " active=" + c.gameObject.activeInHierarchy +
                        " mode=" + c.renderMode + " layer=" + c.gameObject.layer +
                        " sort=" + c.sortingOrder + " overrideSorting=" + c.overrideSorting +
                        " eventCam=" + (c.worldCamera != null ? c.worldCamera.name : "null") +
                        " alpha=" + (group != null ? group.alpha.ToString("F4") : "noCanvasGroup") +
                        " " + Pose(c.transform));
                }
                catch (Exception e) { w.WriteLine("canvas[" + i + "] " + e.Message); }
            }
        }

        private static void DumpUiToolkit(StreamWriter w)
        {
            var list = UnityEngine.Object.FindObjectsOfType<UnityEngine.UIElements.UIDocument>(true);
            w.WriteLine("count=" + list.Length);
            for (int i = 0; i < list.Length && i < MaxComponentsPerKind; ++i)
            {
                try
                {
                    var document = list[i];
                    if (document == null) continue;
                    var visualRoot = document.rootVisualElement;
                    w.WriteLine(PathOf(document.transform) +
                        " enabled=" + document.enabled +
                        " active=" + document.gameObject.activeInHierarchy +
                        " visualRoot=" + (visualRoot == null ? "none" :
                            visualRoot.name + " children=" + visualRoot.childCount +
                            " panel=" + (visualRoot.panel != null)));
                }
                catch (Exception e) { w.WriteLine("uitk[" + i + "] " + e.Message); }
            }
        }

        private static void DumpTmpText(StreamWriter w)
        {
            var list = UnityEngine.Object.FindObjectsOfType<Il2CppTMPro.TMP_Text>(true);
            w.WriteLine("count=" + list.Length);
            for (int i = 0; i < list.Length && i < MaxComponentsPerKind; ++i)
            {
                try
                {
                    var t = list[i];
                    if (t == null) continue;
                    CanvasGroup group = t.GetComponentInParent<CanvasGroup>();
                    w.WriteLine(PathOf(t.transform) + " active=" + t.gameObject.activeInHierarchy +
                        " self=" + t.gameObject.activeSelf + " enabled=" + t.enabled +
                        " alpha=" + t.color.a.ToString("F3") +
                        " groupAlpha=" + (group != null ? group.alpha.ToString("F3") : "none") +
                        " layer=" + t.gameObject.layer + " " + Pose(t.transform) +
                        " text=\"" + Abbrev(t.text, 600) + "\"");
                }
                catch (Exception e) { w.WriteLine("tmp[" + i + "] " + e.Message); }
            }
        }

        private static void DumpUguiText(StreamWriter w)
        {
            var list = UnityEngine.Object.FindObjectsOfType<Text>(true);
            w.WriteLine("count=" + list.Length);
            for (int i = 0; i < list.Length && i < MaxComponentsPerKind; ++i)
            {
                try
                {
                    var t = list[i];
                    if (t == null) continue;
                    w.WriteLine(PathOf(t.transform) + " active=" + t.gameObject.activeInHierarchy +
                        " enabled=" + t.enabled + " alpha=" + t.color.a +
                        " " + Pose(t.transform) + " text=\"" + Abbrev(t.text, 600) + "\"");
                }
                catch (Exception e) { w.WriteLine("text[" + i + "] " + e.Message); }
            }
        }

        private static void DumpInterestingUiNodes(StreamWriter w)
        {
            // Only children of a few known UI roots, avoiding a global scene Transform
            // traversal through foliage/terrain when a snapshot is requested.
            string[] roots =
            {
                "PlayerStandin/PlayerUI/ScreenSpaceCanvas",
                "PlayerStandin/PlayerUI/PlayerMiniMap",
                "CraftMatScreenSpaceUi",
                "DynamicModalDialogGui",
                "TitleGui"
            };
            foreach (string rootName in roots)
            {
                GameObject root = GameObject.Find(rootName);
                w.WriteLine("ROOT " + rootName + " found=" + (root != null));
                if (root == null) continue;
                var nodes = root.GetComponentsInChildren<RectTransform>(true);
                w.WriteLine("  RectTransformCount=" + nodes.Length);
                for (int i = 0; i < nodes.Length && i < MaxUiNodes; ++i)
                {
                    try
                    {
                        RectTransform rt = nodes[i];
                        if (rt == null) continue;
                        string n = rt.name.ToLowerInvariant();
                        if (!Interesting(n)) continue;
                        Graphic graphic = rt.GetComponent<Graphic>();
                        CanvasGroup group = rt.GetComponent<CanvasGroup>();
                        w.WriteLine("  " + PathOf(rt) +
                            " active=" + rt.gameObject.activeInHierarchy +
                            " self=" + rt.gameObject.activeSelf +
                            " layer=" + rt.gameObject.layer +
                            " rect=" + rt.rect + " anchored=" + rt.anchoredPosition +
                            " localScale=" + rt.localScale +
                            " graphic=" + (graphic == null ? "none" : graphic.GetType().Name +
                                " enabled=" + graphic.enabled + " alpha=" + graphic.color.a) +
                            " canvasGroup=" + (group == null ? "none" : group.alpha.ToString("F3")) +
                            " " + Pose(rt));
                    }
                    catch (Exception e) { w.WriteLine("  ui[" + i + "] " + e.Message); }
                }
            }
        }

        // V169C: the 16:00:38 tooltip and 16:02:28/16:03:20 cooking reports
        // require evidence about *which* view exists and where it projects in the HMD.
        // All work happens only on a deliberate F9 snapshot; never from Tick without F9.
        private static void DumpFocusedHudOverlaysV169C(StreamWriter w)
        {
            var player = VRPlayer.Instance;
            if (player == null)
            {
                w.WriteLine("VRPlayer unavailable");
                return;
            }
            WriteFocusedOverlayV169C(w, "HudGui._cookingRecipeInfoView",
                player.DiagnosticCookingRecipeViewV169C);
            WriteFocusedOverlayV169C(w, "HudGui._toolTipInfoView",
                player.DiagnosticTooltipViewV169C);
            WriteFocusedOverlayV169C(w, "HudGui._availableRecipesView",
                player.DiagnosticAvailableRecipesViewV169C);
            WriteFocusedOverlayV169C(w, "HudGui._quickSelectItemInfoView",
                player.DiagnosticQuickSelectInfoViewV169C);

            // If reflective HudGui member resolution failed, report likely native
            // UI roots directly. This scan is permitted on F9; NOT in Update().
            var all = UnityEngine.Object.FindObjectsOfType<RectTransform>(true);
            w.WriteLine("RectTransformsInScene=" + all.Length);
            int matched = 0;
            for (int i = 0; i < all.Length; ++i)
            {
                var rt = all[i];
                if (rt == null) continue;
                string n = rt.name.ToLowerInvariant();
                if (!(n.Contains("tooltip") || n.Contains("tool_tip") ||
                      n.Contains("cookingrecipes") || n.Contains("iteminfoview") ||
                      n.Contains("availablerecipes") || n.Contains("quickselectiteminfo") ||
                      n.Contains("recipeinfoview"))) continue;
                if (matched++ >= 80)
                {
                    w.WriteLine("MATCH_LIMIT=80, remaining omitted");
                    break;
                }
                try { WriteFocusedOverlayV169C(w, "matched-root-" + matched, rt); }
                catch (Exception e) { w.WriteLine("matched-root exception=" + e.Message); }
            }
            w.WriteLine("MatchedRootCount=" + matched);
        }

        private static void WriteFocusedOverlayV169C(StreamWriter w, string label, RectTransform rt)
        {
            w.WriteLine();
            if (rt == null)
            {
                w.WriteLine(label + " = UNRESOLVED (not evidence that view is absent)");
                return;
            }
            w.WriteLine(label + " = " + PathOf(rt));
            Transform head = null;
            var p = VRPlayer.Instance;
            if (p != null && p.StereoRender != null) head = p.StereoRender.Head;
            Canvas owningCanvas = rt.GetComponentInParent<Canvas>();
            w.WriteLine("  rootActive=" + rt.gameObject.activeInHierarchy +
                " self=" + rt.gameObject.activeSelf +
                " layer=" + rt.gameObject.layer + " rect=" + rt.rect +
                " anchor=" + rt.anchoredPosition + " sizeDelta=" + rt.sizeDelta +
                " localScale=" + rt.localScale + " " + Pose(rt));
            w.WriteLine("  owningCanvas=" + (owningCanvas == null ? "NONE" :
                PathOf(owningCanvas.transform) + " mode=" + owningCanvas.renderMode +
                " enabled=" + owningCanvas.enabled + " active=" + owningCanvas.gameObject.activeInHierarchy +
                " eventCam=" + (owningCanvas.worldCamera != null ? owningCanvas.worldCamera.name : "null")));
            if (head != null)
            {
                var offset = rt.position - head.position;
                float alongView = Vector3.Dot(offset, head.forward);
                w.WriteLine("  signedHeadForwardDepth=" + alongView.ToString("F4") +
                    "m headLocal=" + head.InverseTransformPoint(rt.position));
                // Visual-depth evidence only: a ray hit does not prove that every
                // pixel of a panel is occluded. Ignore triggers for world geometry.
                if (offset.sqrMagnitude > 0.01f)
                {
                    float distance = offset.magnitude;
                    RaycastHit hit;
                    bool blocked = Physics.Raycast(head.position, offset / distance,
                        out hit, Mathf.Max(0f, distance - 0.025f), ~0,
                        QueryTriggerInteraction.Ignore);
                    w.WriteLine("  physicsHeadRayBeforePivot=" + (blocked ?
                        hit.collider.name + " distance=" + hit.distance.ToString("F3") : "none"));
                }
            }
            Transform current = rt;
            for (int depth = 0; current != null && depth < 8; ++depth, current = current.parent)
            {
                var group = current.GetComponent<CanvasGroup>();
                var graphic = current.GetComponent<Graphic>();
                var renderer = current.GetComponent<CanvasRenderer>();
                w.WriteLine("  ancestor[" + depth + "] " + current.name +
                    " activeSelf=" + current.gameObject.activeSelf +
                    " activeHierarchy=" + current.gameObject.activeInHierarchy +
                    " scale=" + current.localScale +
                    " group=" + (group != null ?
                        "alpha=" + group.alpha + " interactable=" + group.interactable : "none") +
                    " graphic=" + (graphic != null ?
                        "enabled=" + graphic.enabled + " alpha=" + graphic.color.a : "none") +
                    " canvasCull=" + (renderer != null ? renderer.cull.ToString() : "none"));
            }
        }

        private static bool Interesting(string n)
        {
            return n.Contains("recipe") || n.Contains("info") || n.Contains("tip") ||
                   n.Contains("ingredient") || n.Contains("resource") || n.Contains("required") ||
                   n.Contains("craft") || n.Contains("book") || n.Contains("tooltip") ||
                   n.Contains("hover") || n.Contains("mouse") || n.Contains("interact") ||
                   n.Contains("worldscreen") || n.Contains("material") || n.Contains("action") ||
                   n.Contains("prompt") || n.Contains("cog") || n.Contains("item") ||
                   n.Contains("blueprint") || n.Contains("hud");
        }

        private static string Pose(Transform t)
        {
            if (t == null) return "pose=null";
            string rel = "";
            try
            {
                var p = VRPlayer.Instance;
                if (p != null && p.StereoRender != null && p.StereoRender.Head != null)
                {
                    Transform head = p.StereoRender.Head;
                    Vector3 headLocal = head.InverseTransformPoint(t.position);
                    rel = " headLocal=" + headLocal + " headDistance=" +
                          headLocal.magnitude.ToString("F3");
                }
            }
            catch { }
            return "worldPos=" + t.position + " localPos=" + t.localPosition +
                   " lossyScale=" + t.lossyScale + " worldEuler=" + t.eulerAngles + rel;
        }

        private static string PathOf(Transform t)
        {
            if (t == null) return "null";
            var builder = new StringBuilder(t.name);
            int count = 0;
            while (t.parent != null && count++ < 32)
            {
                t = t.parent;
                builder.Insert(0, t.name + "/");
            }
            return builder.ToString();
        }

        private static string Abbrev(string value, int limit)
        {
            value = (value ?? "").Replace("\r", "\\r").Replace("\n", "\\n");
            return value.Length <= limit ? value : value.Substring(0, limit) + "...[TRUNCATED]";
        }
    }
}
