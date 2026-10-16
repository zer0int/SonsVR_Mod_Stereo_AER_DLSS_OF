using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering.HighDefinition;

namespace SonsVRMod
{
    /// <summary>
    /// V169G: READ-ONLY, ON-DEMAND black-surface provenance probe.
    /// Called exclusively from F9 snapshot capture. No per-frame scans, hooks,
    /// UI modifications, native-camera changes, material instances, or raycasts.
    ///
    /// V169F's synthetic tarp tutorial background is confirmed absent while the
    /// black region persists. The old snapshot never collected Graphic fills,
    /// nearby mesh materials, or camera clear colors, so disabling another
    /// object before measuring its actual renderer would be speculation.
    /// </summary>
    internal static class BlackBackdropProbeV169G
    {
        private sealed class SurfaceCandidate
        {
            internal float Score;
            internal string Description;
        }

        private const int MaxGraphicCandidates = 240;
        private const int MaxRendererCandidates = 160;
        // V169I: F9 is explicitly allowed to hitch; never truncate the renderer universe.

        internal static void Capture(StreamWriter writer)
        {
            var player = VRPlayer.Instance;
            Transform head = player != null && player.StereoRender != null
                ? player.StereoRender.Head : null;
            if (head == null)
            {
                writer.WriteLine("HMD unavailable; black-surface geometry probe skipped");
                return;
            }
            writer.WriteLine("V169G: read-only F9 probe. All near-field candidate positions are headset-local.");
            writer.WriteLine("No object was enabled, disabled, recolored or moved.");
            CaptureCameraClearState(writer);
            CaptureUiGraphics(writer, head);
            CaptureWorldRenderers(writer, head);
        }

        private static void CaptureCameraClearState(StreamWriter writer)
        {
            writer.WriteLine("\n--- CAMERA CLEAR COLOR / HDRP ---");
            var cameras = UnityEngine.Object.FindObjectsOfType<Camera>(true);
            foreach (Camera camera in cameras)
            {
                try
                {
                    if (camera == null) continue;
                    var hd = camera.GetComponent<HDAdditionalCameraData>();
                    writer.WriteLine(PathOf(camera.transform) +
                        " active=" + camera.gameObject.activeInHierarchy +
                        " enabled=" + camera.enabled +
                        " clearFlags=" + camera.clearFlags +
                        " backgroundColor=" + camera.backgroundColor +
                        " cullingMask=0x" + camera.cullingMask.ToString("X8") +
                        " depth=" + camera.depth +
                        " targetTexture=" + (camera.targetTexture != null ? camera.targetTexture.name : "null/backbuffer") +
                        " targetDisplay=" + camera.targetDisplay +
                        " stereoTargetEye=" + camera.stereoTargetEye +
                        " HDRP=" + (hd != null ?
                            "clearColorMode=" + hd.clearColorMode +
                            " backgroundColorHDR=" + hd.backgroundColorHDR +
                            " volumeMask=0x" + hd.volumeLayerMask.value.ToString("X8") : "none"));
                }
                catch (Exception ex) { writer.WriteLine("CAMERA_ERROR: " + ex.Message); }
            }
        }

        private static void CaptureUiGraphics(StreamWriter writer, Transform head)
        {
            writer.WriteLine("\n--- ACTIVE UGUI GRAPHICS / NEAR-FIELD SURFACES ---");
            var graphics = UnityEngine.Object.FindObjectsOfType<Graphic>(true);
            writer.WriteLine("totalGraphics=" + graphics.Length +
                " candidateFilter=active+enabled+headZ(0.04..4m)+nearView+size, sorted by projected area");
            var candidates = new List<SurfaceCandidate>();
            foreach (Graphic graphic in graphics)
            {
                try
                {
                    if (graphic == null || !graphic.isActiveAndEnabled) continue;
                    RectTransform rt = graphic.GetComponent<RectTransform>();
                    if (rt == null) continue;
                    Vector3[] corners = new Vector3[4];
                    rt.GetWorldCorners(corners);
                    float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
                    float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
                    float nearZ = float.PositiveInfinity, farZ = float.NegativeInfinity;
                    for (int i = 0; i < 4; i++)
                    {
                        Vector3 local = head.InverseTransformPoint(corners[i]);
                        minX = Mathf.Min(minX, local.x); maxX = Mathf.Max(maxX, local.x);
                        minY = Mathf.Min(minY, local.y); maxY = Mathf.Max(maxY, local.y);
                        nearZ = Mathf.Min(nearZ, local.z); farZ = Mathf.Max(farZ, local.z);
                    }
                    if (farZ < 0.04f || nearZ > 4.0f) continue;
                    float dx = maxX - minX, dy = maxY - minY;
                    if (dx < 0.20f && dy < 0.20f) continue;
                    float depth = Mathf.Max(0.15f, Mathf.Abs((nearZ + farZ) * 0.5f));
                    if (minX > 2f * depth || maxX < -2f * depth ||
                        minY > 2f * depth || maxY < -2f * depth) continue;
                    // Favor near, wide opaque surfaces over tiny icons; not a claim
                    // that each candidate actually rendered a black pixel.
                    float area = dx * dy / (depth * depth);
                    Color color = graphic.color;
                    var group = graphic.GetComponentInParent<CanvasGroup>();
                    string material = MaterialSummary(graphic.material);
                    string texture = graphic.mainTexture != null ? graphic.mainTexture.name : "null";
                    candidates.Add(new SurfaceCandidate {
                        Score = area,
                        Description = "areaOverDepth2=" + area.ToString("F3") +
                            " type=" + graphic.GetType().Name +
                            " path=" + PathOf(rt) +
                            " rect=" + rt.rect +
                            " headBoundsX=[" + minX.ToString("F2") + "," + maxX.ToString("F2") + "]" +
                            " Y=[" + minY.ToString("F2") + "," + maxY.ToString("F2") + "]" +
                            " Z=[" + nearZ.ToString("F2") + "," + farZ.ToString("F2") + "]" +
                            " color=" + color + " groupAlpha=" +
                            (group != null ? group.alpha.ToString("F3") : "none") +
                            " raycast=" + graphic.raycastTarget + " texture=" + texture +
                            " " + material
                    });
                }
                catch (Exception ex) { writer.WriteLine("GRAPHIC_ERROR: " + ex.Message); }
            }
            candidates.Sort((a,b) => b.Score.CompareTo(a.Score));
            writer.WriteLine("nearFieldCandidates=" + candidates.Count +
                " showingTop=" + Mathf.Min(MaxGraphicCandidates, candidates.Count));
            for (int i = 0; i < candidates.Count && i < MaxGraphicCandidates; ++i)
                writer.WriteLine("UGUI[" + i + "] " + candidates[i].Description);
        }

        private static void CaptureWorldRenderers(StreamWriter writer, Transform head)
        {
            writer.WriteLine("\n--- ACTIVE NEAR-FIELD RENDERERS / MESHS ---");
            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            var candidates = new List<SurfaceCandidate>();
            int visited = 0;
            for (int i = 0; i < renderers.Length; ++i)
            {
                Renderer renderer = renderers[i];
                visited++;
                try
                {
                    if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                        continue;
                    Bounds bounds = renderer.bounds;
                    // Include any renderer within approximately four meters of the head,
                    // even when a wide plane has a distant center.
                    Vector3 nearest = bounds.ClosestPoint(head.position);
                    if (Vector3.Distance(nearest, head.position) > 3.0f) continue;
                    Vector3 local = head.InverseTransformPoint(bounds.center);
                    Vector3 size = bounds.size;
                    bool namedLikePanel = LooksLikePanel(renderer.name);
                    float extent = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                    if (extent < 0.35f && !namedLikePanel) continue;
                    Material material = renderer.sharedMaterial; // never renderer.material: no instantiation
                    float score = extent / Mathf.Max(0.25f, Vector3.Distance(nearest, head.position));
                    candidates.Add(new SurfaceCandidate {
                        Score = score,
                        Description = "score=" + score.ToString("F3") +
                            " type=" + renderer.GetType().Name +
                            " path=" + PathOf(renderer.transform) +
                            " layer=" + renderer.gameObject.layer +
                            " nearestDistance=" + Vector3.Distance(nearest, head.position).ToString("F3") +
                            " boundsCenterHead=" + local + " boundsWorldSize=" + size +
                            " " + MaterialSummary(material)
                    });
                }
                catch (Exception ex) { writer.WriteLine("RENDERER_ERROR: " + ex.Message); }
            }
            candidates.Sort((a,b) => b.Score.CompareTo(a.Score));
            writer.WriteLine("totalRenderers=" + renderers.Length +
                " inspected=" + visited +
                " nearFieldCandidates=" + candidates.Count +
                " showingTop=" + Mathf.Min(MaxRendererCandidates, candidates.Count));
            for (int i = 0; i < candidates.Count && i < MaxRendererCandidates; ++i)
                writer.WriteLine("RENDERER[" + i + "] " + candidates[i].Description);
        }

        private static bool LooksLikePanel(string name)
        {
            if (name == null) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("black") || n.Contains("backdrop") ||
                n.Contains("background") || n.Contains("plane") ||
                n.Contains("panel") || n.Contains("quad") ||
                n.Contains("inventory") || n.Contains("craftmat");
        }

        private static string MaterialSummary(Material material)
        {
            if (material == null) return "material=null";
            try
            {
                string output = "material='" + material.name + "'" +
                    " shader='" + (material.shader != null ? material.shader.name : "null") + "'" +
                    " renderQueue=" + material.renderQueue;
                if (material.HasProperty("_BaseColor")) output += " baseColor=" + material.GetColor("_BaseColor");
                else if (material.HasProperty("_Color")) output += " tint=" + material.GetColor("_Color");
                return output;
            }
            catch (Exception ex) { return "materialError=" + ex.Message; }
        }

        private static string PathOf(Transform t)
        {
            if (t == null) return "null";
            string path = t.name;
            int depth = 0;
            while (t.parent != null && depth++ < 32)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }
    }
}
