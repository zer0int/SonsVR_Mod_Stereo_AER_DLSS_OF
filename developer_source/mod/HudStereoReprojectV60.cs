using MelonLoader;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace SonsVRMod
{
    /// <summary>
    /// ZER0INT V60 - DIRECT UGUI STEREO REPROJECTION
    ///
    /// V51 can synthesize ordinary opaque world geometry because its source pixels have meaningful
    /// scene depth. Transparent UGUI does not: UI color is in the eye RT while depth still belongs
    /// to the tree/rock/sky behind it. V60 therefore removes successfully snapshotted gameplay UI
    /// from the native HDRP eye render, lets V51 synthesize WORLD ONLY, then redraws the same UGUI
    /// geometry with source-eye and target-eye matrices. No extra Camera and no fake HUD depth.
    /// </summary>
    internal static class HudStereoReprojectV60
    {
        // V62 SAFE ROLLBACK: V61 proved that bypassing CanvasRenderer and replaying raw
        // meshes/materials cannot preserve Unity UGUI clipping/mask/per-renderer state. Keep the
        // experimental helper compiled for archaeology, but hard-disable it while the native HUD
        // remains in the proven V51 eye render path.
        internal static bool Enabled = false;

        private sealed class Snapshot
        {
            internal int GraphicId;
            internal int AbsoluteDepth;
            internal Matrix4x4 LocalToWorld;
            internal Mesh Mesh;
            internal Material Material;
            internal int MaterialShaderId;
            internal CanvasRenderer Renderer;
            internal string Path;
        }

        private sealed class HiddenRenderer
        {
            internal CanvasRenderer Renderer;
            internal bool PreviousCull;
        }

        private static readonly Dictionary<int, Snapshot> _cache = new Dictionary<int, Snapshot>();
        private static readonly List<Snapshot> _frame = new List<Snapshot>(256);
        private static readonly List<HiddenRenderer> _hidden = new List<HiddenRenderer>(256);
        private static readonly HashSet<int> _seenGraphicIds = new HashSet<int>();

        private static MethodInfo _doMeshGeneration;
        private static PropertyInfo _workerMeshProperty;
        private static FieldInfo _workerMeshField;
        private static bool _reflectionResolved;

        private static int _captureFrame = -1;
        private static int _captureEye = -1;
        private static bool _captureActive;
        private static bool _firstCaptureLogged;
        private static bool _firstCompositeLogged;
        private static int _captureFailureLogsRemaining = 16;
        private static int _drawFailureLogsRemaining = 16;

        internal static void Reset()
        {
            RestoreCull();
            _frame.Clear();
            _seenGraphicIds.Clear();
            _captureFrame = -1;
            _captureEye = -1;
            _captureActive = false;
            _firstCaptureLogged = false;
            _firstCompositeLogged = false;
            _captureFailureLogsRemaining = 16;
            _drawFailureLogsRemaining = 16;
        }

        internal static void Shutdown()
        {
            Reset();
            foreach (var kv in _cache)
            {
                Snapshot s = kv.Value;
                if (s == null) continue;
                if (s.Mesh != null)
                {
                    try { UnityEngine.Object.Destroy(s.Mesh); } catch { }
                }
                if (s.Material != null)
                {
                    try { UnityEngine.Object.Destroy(s.Material); } catch { }
                }
            }
            _cache.Clear();
        }

        internal static bool BeginEyeRender(StereoRender stereo, Camera camera, int eye)
        {
            RestoreCull();
            _frame.Clear();
            _seenGraphicIds.Clear();
            _captureFrame = -1;
            _captureEye = -1;
            _captureActive = false;

            if (!Enabled || stereo == null || camera == null || VRPlayer.Instance == null)
                return false;

            try
            {
                VRPlayer player = VRPlayer.Instance;

                // V62 SAFE ROLLBACK: never mutate/rebuild the Canvas from a render callback.
                // V60's ForceUpdateCanvases() collapsed anchored HUD content into a central column.
                // This helper is currently hard-disabled above, but keep the dangerous mutation
                // removed so a future diagnostic toggle cannot silently resurrect that failure.
                EnsureReflection();

                CaptureCanvas(player._screenSpaceCanvas);
                CaptureCanvas(player._playerMiniMapCanvas);

                if (_frame.Count == 0)
                    return false;

                _frame.Sort((a, b) =>
                {
                    int d = a.AbsoluteDepth.CompareTo(b.AbsoluteDepth);
                    return d != 0 ? d : a.GraphicId.CompareTo(b.GraphicId);
                });

                // Hide only successfully captured Graphics. Any unsupported Graphic stays native,
                // giving us a fail-partial rather than a disappearing-HUD failure mode.
                for (int i = 0; i < _frame.Count; ++i)
                {
                    CanvasRenderer cr = _frame[i].Renderer;
                    if (cr == null) continue;
                    bool previousCull = cr.cull;
                    _hidden.Add(new HiddenRenderer { Renderer = cr, PreviousCull = previousCull });
                    cr.cull = true;
                }

                _captureFrame = Time.frameCount;
                _captureEye = eye;
                _captureActive = _hidden.Count > 0;

                if (_captureActive && !_firstCaptureLogged)
                {
                    _firstCaptureLogged = true;
                    MelonLogger.Msg($"[StereoRender][AER V60][UGUI] WORLD-ONLY eye capture armed: eye={(eye == 0 ? "L" : "R")} frame={Time.frameCount} capturedGraphics={_frame.Count} culledRenderers={_hidden.Count}. No extra Camera; source UGUI will be redrawn directly for both eyes after V51 world synthesis.");
                    for (int i = 0; i < Mathf.Min(12, _frame.Count); ++i)
                    {
                        Snapshot s = _frame[i];
                        MelonLogger.Msg($"[StereoRender][AER V60][UGUI] graphic[{i}] depth={s.AbsoluteDepth} path={s.Path} verts={(s.Mesh != null ? s.Mesh.vertexCount : 0)} shader={(s.Material != null && s.Material.shader != null ? s.Material.shader.name : "null")}");
                    }
                }
                return _captureActive;
            }
            catch (Exception e)
            {
                RestoreCull();
                LogCaptureFailure("begin-eye capture failed: " + e.Message);
                return false;
            }
        }

        internal static void EndEyeRender(StereoRender stereo, Camera camera, int eye)
        {
            // The eye has already rendered world-only. Restoring cull now cannot retroactively
            // alter its RT; it simply returns Unity's live UI state to normal.
            RestoreCull();
        }

        internal static bool CompositeSameTimePair(
            StereoRender stereo,
            int sourceEye,
            RenderTexture sourceRT,
            RenderTexture syntheticTargetRT,
            Matrix4x4 sourceWorldToCamera,
            Matrix4x4 sourceProjection,
            Matrix4x4 targetWorldToCamera,
            Matrix4x4 targetProjection)
        {
            if (!Enabled || !_captureActive || _frame.Count == 0 ||
                _captureFrame != Time.frameCount || _captureEye != sourceEye ||
                sourceRT == null || syntheticTargetRT == null ||
                !sourceRT.IsCreated() || !syntheticTargetRT.IsCreated())
                return false;

            // V51 has already consumed sourceRT as WORLD ONLY and completed target-world synthesis.
            // Clear DEPTH ONLY and overlay the identical captured UGUI state in each eye geometry.
            int sourceDrawn = DrawSnapshots(sourceRT, sourceWorldToCamera, sourceProjection, "source");
            int targetDrawn = DrawSnapshots(syntheticTargetRT, targetWorldToCamera, targetProjection, "target");

            bool ok = sourceDrawn > 0 && targetDrawn > 0;
            if (ok && !_firstCompositeLogged)
            {
                _firstCompositeLogged = true;
                MelonLogger.Warning($"[StereoRender][AER V60][UGUI] DIRECT SAME-TIME HUD STEREO IS IN HEADSET PATH: frame={Time.frameCount} source={(sourceEye == 0 ? "L" : "R")} graphics={_frame.Count} sourceDrawn={sourceDrawn} targetDrawn={targetDrawn}. HUD pixels never entered the scene-depth warp.");
            }
            return ok;
        }

        private static void CaptureCanvas(Canvas canvas)
        {
            if (canvas == null || !canvas.gameObject.activeInHierarchy)
                return;

            foreach (Graphic g in canvas.GetComponentsInChildren<Graphic>(true))
            {
                if (g == null || !g.enabled || !g.gameObject.activeInHierarchy)
                    continue;

                int id = g.GetInstanceID();
                if (!_seenGraphicIds.Add(id))
                    continue;

                CanvasRenderer cr = g.canvasRenderer;
                if (cr == null || cr.cull)
                    continue;

                if (TryCaptureGraphic(g, cr, out Snapshot snap))
                    _frame.Add(snap);
            }
        }

        private static bool TryCaptureGraphic(Graphic graphic, CanvasRenderer cr, out Snapshot snap)
        {
            snap = null;
            try
            {
                Mesh sourceMesh = null;

                // Unity's private DoMeshGeneration() invokes the concrete Graphic's virtual
                // OnPopulateMesh(), applies IMeshModifier(s), fills Graphic.workerMesh, and then
                // submits that exact mesh to CanvasRenderer. We clone it immediately.
                if (_doMeshGeneration != null)
                {
                    try
                    {
                        _doMeshGeneration.Invoke(graphic, null);
                        sourceMesh = GetWorkerMesh();
                    }
                    catch (Exception e)
                    {
                        LogCaptureFailure($"DoMeshGeneration failed for '{graphic.name}', trying subclass mesh: {e.Message}");
                    }
                }

                // TMPro/special Graphics can maintain their own mesh. Reflection fallback avoids
                // binding this helper to a particular subclass/API revision.
                if (sourceMesh == null || sourceMesh.vertexCount <= 0)
                {
                    PropertyInfo meshProp = graphic.GetType().GetProperty(
                        "mesh", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (meshProp != null && typeof(Mesh).IsAssignableFrom(meshProp.PropertyType))
                        sourceMesh = meshProp.GetValue(graphic, null) as Mesh;
                }

                if (sourceMesh == null || sourceMesh.vertexCount <= 0)
                    return false;

                Material sourceMaterial = null;
                try
                {
                    if (cr.materialCount > 0)
                        sourceMaterial = cr.GetMaterial(0);
                }
                catch { }
                if (sourceMaterial == null)
                    sourceMaterial = graphic.materialForRendering;
                if (sourceMaterial == null || sourceMaterial.shader == null)
                    return false;

                int id = graphic.GetInstanceID();
                if (!_cache.TryGetValue(id, out snap) || snap == null)
                {
                    snap = new Snapshot
                    {
                        GraphicId = id,
                        Mesh = new Mesh
                        {
                            name = "SonsVR_V60_UGUI_" + id,
                            hideFlags = HideFlags.HideAndDontSave
                        }
                    };
                    _cache[id] = snap;
                }

                CopyMesh(sourceMesh, snap.Mesh);

                int shaderId = sourceMaterial.shader.GetInstanceID();
                if (snap.Material == null || snap.MaterialShaderId != shaderId)
                {
                    if (snap.Material != null)
                    {
                        try { UnityEngine.Object.Destroy(snap.Material); } catch { }
                    }
                    snap.Material = new Material(sourceMaterial)
                    {
                        name = "SonsVR_V60_UGUI_Mat_" + id,
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    snap.MaterialShaderId = shaderId;
                }
                else
                {
                    snap.Material.CopyPropertiesFromMaterial(sourceMaterial);
                    snap.Material.renderQueue = sourceMaterial.renderQueue;
                }

                Texture mainTexture = null;
                try { mainTexture = graphic.mainTexture; } catch { }
                if (mainTexture != null && snap.Material.HasProperty("_MainTex"))
                    snap.Material.SetTexture("_MainTex", mainTexture);

                snap.AbsoluteDepth = cr.absoluteDepth;
                snap.LocalToWorld = graphic.transform.localToWorldMatrix;
                snap.Renderer = cr;
                snap.Path = HierarchyPath(graphic.transform);
                return true;
            }
            catch (Exception e)
            {
                LogCaptureFailure($"graphic '{(graphic != null ? graphic.name : "null")}' capture failed: {e.Message}");
                return false;
            }
        }

        private static void EnsureReflection()
        {
            if (_reflectionResolved) return;
            _reflectionResolved = true;

            Type graphicType = typeof(Graphic);
            _doMeshGeneration = graphicType.GetMethod(
                "DoMeshGeneration", BindingFlags.Instance | BindingFlags.NonPublic);
            _workerMeshProperty = graphicType.GetProperty(
                "workerMesh", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            _workerMeshField = graphicType.GetField(
                "s_Mesh", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            MelonLogger.Msg($"[StereoRender][AER V60][UGUI] reflection: DoMeshGeneration={(_doMeshGeneration != null)} workerMeshProperty={(_workerMeshProperty != null)} s_MeshField={(_workerMeshField != null)}.");
        }

        private static Mesh GetWorkerMesh()
        {
            try
            {
                if (_workerMeshProperty != null)
                    return _workerMeshProperty.GetValue(null, null) as Mesh;
            }
            catch { }
            try
            {
                if (_workerMeshField != null)
                    return _workerMeshField.GetValue(null) as Mesh;
            }
            catch { }
            return null;
        }

        private static void CopyMesh(Mesh source, Mesh destination)
        {
            destination.Clear();
            destination.vertices = source.vertices;
            try { destination.colors32 = source.colors32; } catch { }
            try { destination.uv = source.uv; } catch { }
            try { destination.uv2 = source.uv2; } catch { }
            try { destination.uv3 = source.uv3; } catch { }
            try { destination.uv4 = source.uv4; } catch { }
            try { destination.normals = source.normals; } catch { }
            try { destination.tangents = source.tangents; } catch { }
            destination.triangles = source.triangles;
            destination.bounds = source.bounds;
        }

        private static int DrawSnapshots(
            RenderTexture target,
            Matrix4x4 worldToCamera,
            Matrix4x4 projection,
            string label)
        {
            RenderTexture previous = RenderTexture.active;
            int drawn = 0;
            bool pushed = false;
            try
            {
                Graphics.SetRenderTarget(target);
                GL.Clear(true, false, Color.clear); // depth only; preserve world color
                GL.Viewport(new Rect(0f, 0f, target.width, target.height));
                GL.PushMatrix();
                pushed = true;
                GL.modelview = worldToCamera;
                GL.LoadProjectionMatrix(projection);

                for (int i = 0; i < _frame.Count; ++i)
                {
                    Snapshot s = _frame[i];
                    if (s == null || s.Mesh == null || s.Material == null ||
                        s.Mesh.vertexCount <= 0 || s.Material.shader == null)
                        continue;
                    try
                    {
                        if (!s.Material.SetPass(0)) continue;
                        Graphics.DrawMeshNow(s.Mesh, s.LocalToWorld);
                        drawn++;
                    }
                    catch (Exception e)
                    {
                        LogDrawFailure($"{label} draw failed for '{s.Path}': {e.Message}");
                    }
                }

                GL.PopMatrix();
                pushed = false;
                RenderTexture.active = previous;
                return drawn;
            }
            catch (Exception e)
            {
                if (pushed)
                {
                    try { GL.PopMatrix(); } catch { }
                }
                RenderTexture.active = previous;
                LogDrawFailure($"{label} HUD composite failed: {e.Message}");
                return drawn;
            }
        }

        private static void RestoreCull()
        {
            for (int i = 0; i < _hidden.Count; ++i)
            {
                HiddenRenderer h = _hidden[i];
                if (h == null || h.Renderer == null) continue;
                try { h.Renderer.cull = h.PreviousCull; } catch { }
            }
            _hidden.Clear();
        }

        private static string HierarchyPath(Transform t)
        {
            if (t == null) return "<null>";
            string path = t.name;
            Transform p = t.parent;
            int guard = 0;
            while (p != null && guard++ < 64)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }

        private static void LogCaptureFailure(string message)
        {
            if (_captureFailureLogsRemaining <= 0) return;
            _captureFailureLogsRemaining--;
            MelonLogger.Warning($"[StereoRender][AER V60][UGUI] {message} Logs remaining={_captureFailureLogsRemaining}.");
        }

        private static void LogDrawFailure(string message)
        {
            if (_drawFailureLogsRemaining <= 0) return;
            _drawFailureLogsRemaining--;
            MelonLogger.Warning($"[StereoRender][AER V60][UGUI] {message} Logs remaining={_drawFailureLogsRemaining}.");
        }
    }
}
