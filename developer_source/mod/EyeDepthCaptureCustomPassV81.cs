using MelonLoader;
using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SonsVRMod
{
    /// <summary>
    /// V81 diagnostic CustomPass.
    ///
    /// This is the "stop trusting globals" test:
    ///   CustomPassContext.cameraDepthBuffer
    /// is the RTHandle HDRP explicitly supplies for the exact camera currently
    /// executing this pass.
    ///
    /// Nothing from this class is fed into V51 in V81.
    /// </summary>
    [Serializable]
    internal class EyeDepthCaptureCustomPassV81 : CustomPass
    {
        public EyeDepthCaptureCustomPassV81(IntPtr ptr) : base(ptr) { }

        // CustomPass is not a Component, so managed-side construction is allowed
        // when using the standard Il2CppInterop derived-constructor pattern.
        public EyeDepthCaptureCustomPassV81()
            : base(ClassInjector.DerivedConstructorPointer<EyeDepthCaptureCustomPassV81>())
        {
            ClassInjector.DerivedConstructorBody(this);
        }

        private sealed class EyeState
        {
            internal Camera Camera;
            internal GameObject VolumeObject;
            internal CustomPassVolume Volume;

            internal RenderTexture RawDepth;
            internal RenderTexture DiagSlice0;
            internal RenderTexture DiagSlice1;

            internal Material CaptureSlice0Material;
            internal Material DiagSlice0Material;
            internal Material DiagSlice1Material;

            internal int LastExecuteFrame = -1;
            internal int SourceInstanceId;
            internal string SourceName = "";
            internal int SourceWidth;
            internal int SourceHeight;
            internal int SourceVolumeDepth;
            internal TextureDimension SourceDimension;
            internal GraphicsFormat SourceGraphicsFormat;
            internal int ActualWidth;
            internal int ActualHeight;
            internal Vector4 ZParams;

            // V116 DLSS truth probe: passive HDCamera viewport observation on BOTH AER and 2E.
            // Recorded before the AER-only depth-capture early return, so quality changes can be
            // tested without changing rendering mode or adding another camera/readback.
            internal int ObservedActualWidthV116;
            internal int ObservedActualHeightV116;
            internal int ObservedFrameV116 = -1;

            // V94: capture the exact HDCamera matrices that owned cameraDepthBuffer at the
            // CustomPass injection point. These describe the raster whose depth we copy, rather
            // than re-sampling Camera matrices later in endCameraRendering.
            internal int MatrixFrame = -1;
            internal Matrix4x4 HdProjectionMatrix;
            internal Matrix4x4 HdWorldToCameraRHS;
            internal Matrix4x4 CameraGpuProjectionAtExecute;
            internal Matrix4x4 CameraWorldToCameraAtExecute;
            internal Matrix4x4 CameraProjectionAtExecute;
            internal Matrix4x4 CameraNonJitteredProjectionAtExecute;

            internal bool ReadbackPending0;
            internal bool ReadbackPending1;
            internal AsyncGPUReadbackRequest Readback0;
            internal AsyncGPUReadbackRequest Readback1;
            internal float NextReadbackTime;
            internal int ReadbackFrame0 = -1;
            internal int ReadbackFrame1 = -1;
            internal int ReadbackSlice0;
            internal int ReadbackSlice1;
            internal int ReadbackSourceInstanceId0;
            internal int ReadbackSourceInstanceId1;
            internal string ReadbackSourceName0 = "";
            internal string ReadbackSourceName1 = "";
            internal int ReadbackSourceWidth0;
            internal int ReadbackSourceHeight0;
            internal int ReadbackSourceWidth1;
            internal int ReadbackSourceHeight1;
            internal int ReadbackSourceVolumeDepth0;
            internal int ReadbackSourceVolumeDepth1;
            internal Vector4 ReadbackZParams0;
            internal Vector4 ReadbackZParams1;
        }

        private static readonly EyeState[] _eyes =
        {
            new EyeState(),
            new EyeState()
        };

        private static bool _registered;
        private static bool _installLogged;

        // V81d: one explicit CAMERA-MODE volume per eye.
        // Crucially, passes are created through CustomPassVolume.AddPassOfType(),
        // matching HDRP 14's documented runtime API instead of manually new()+list.Add().
        private static int _setupCountV81d;
        private static int _executeCountV81d;
        private static int _setupLogsRemainingV81d = 4;
        private static int _executeCameraLogsRemainingV81d = 6;
        private static float _installedAtV81d = -1f;
        private static bool _noSetupWarningV81d;
        private static bool _noExecuteWarningV81d;

        // V97: one-shot, measurement-only RTHandle/viewport provenance probe.
        // This does not change any render state; it records the exact coordinate domains
        // HDRP/DLSS says its camera color/depth/motion handles occupy.
        private static readonly bool[] _viewportProbeLoggedV97 = new bool[2];

        private static int _executeFailureLogsRemaining = 12;
        private static int _readbackRequestLogsRemainingV81f = 8;
        private static int _readbackDoneLogsRemainingV81f = 8;
        private const int DiagSize = 256;
        private const float ReadbackInterval = 0.75f;

        internal static void RegisterInjectedType()
        {
            if (_registered)
                return;

            ClassInjector.RegisterTypeInIl2Cpp<EyeDepthCaptureCustomPassV81>();
            _registered = true;
            MelonLogger.Warning(
                "[StereoRender][AER V81][CustomDepth] injected CustomPass type registered.");
        }

        internal static void EnsureVolumes(Camera left, Camera right)
        {
            if (!_registered || left == null || right == null)
                return;

            EnsureCameraModeVolumeV81d(0, left);
            EnsureCameraModeVolumeV81d(1, right);

            if (!_installLogged &&
                _eyes[0].Volume != null &&
                _eyes[1].Volume != null)
            {
                _installLogged = true;
                _installedAtV81d = Time.unscaledTime;

                MelonLogger.Warning(
                    "[StereoRender][AER V81e][CustomDepth] TWO CAMERA-MODE CustomPassVolumes " +
                    "installed via AddPassOfType() at AfterOpaqueDepthAndNormal. " +
                    "Each targetCamera is an exact VR eye. HDRP camera mode is expected to bypass " +
                    "ordinary volume-layer filtering.");
            }
        }

        private static void EnsureCameraModeVolumeV81d(
            int eye,
            Camera camera)
        {
            EyeState s = _eyes[eye];

            if (s.Camera == camera &&
                s.Volume != null &&
                s.VolumeObject != null)
                return;

            DestroyVolumeState(s);
            s.Camera = camera;

            GameObject go = new GameObject(
                eye == 0
                    ? "SonsVR_V81e_CustomDepth_Left"
                    : "SonsVR_V81e_CustomDepth_Right");

            go.SetActive(false);
            go.hideFlags = HideFlags.DontSave;

            // The VR rig/eye cameras live under the persistent [VROrigin].
            // Parent the probe to the actual eye so it cannot be destroyed merely because
            // the temporary loading scene/camera disappears.
            go.transform.SetParent(camera.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            // Belt-and-suspenders: put the probe on a layer already included by this
            // camera's ordinary HDRP Volume Layer Mask, even though targetCamera mode
            // should ignore that mask.
            int volumeMask = -1;
            int chosenLayer = 0;

            try
            {
                HDAdditionalCameraData hdData =
                    camera.GetComponent<HDAdditionalCameraData>();

                if (hdData != null)
                {
                    volumeMask = hdData.volumeLayerMask.value;
                    chosenLayer = FirstSetLayerV81d(volumeMask);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning(
                    "[StereoRender][AER V81e][CustomDepth] could not inspect " +
                    $"volumeLayerMask for camera='{camera.name}': " +
                    e.GetType().Name + ": " + e.Message);
            }

            go.layer = chosenLayer;

            CustomPassVolume volume =
                go.AddComponent<CustomPassVolume>();

            volume.enabled = true;
            volume.isGlobal = true;
            volume.injectionPoint =
                CustomPassInjectionPoint.AfterOpaqueDepthAndNormal;
            volume.priority = 1000000f;

            // THIS is the important V81d change:
            // use HDRP's own pass-construction API, not new()+customPasses.Add().
            Il2CppSystem.Type injectedPassType =
                Il2CppInterop.Runtime.Il2CppType.From(
                    typeof(EyeDepthCaptureCustomPassV81));

            CustomPass rawPass =
                volume.AddPassOfType(injectedPassType);

            EyeDepthCaptureCustomPassV81 pass =
                rawPass as EyeDepthCaptureCustomPassV81;

            if (pass == null)
            {
                throw new Exception(
                    "CustomPassVolume.AddPassOfType(Il2CppType.From(typeof(EyeDepthCaptureCustomPassV81))) " +
                    "returned null/wrong type.");
            }

            pass.enabled = true;
            pass.name =
                eye == 0
                    ? "SonsVR V81e CameraLocal Depth LEFT"
                    : "SonsVR V81e CameraLocal Depth RIGHT";

            // Setting targetCamera selects HDRP's Camera mode.
            volume.targetCamera = camera;

            s.VolumeObject = go;
            s.Volume = volume;

            // Enable only after the full component/pass/target-camera configuration exists.
            go.SetActive(true);

            MelonLogger.Warning(
                "[StereoRender][AER V81e][CustomDepth] installed " +
                $"eye={(eye == 0 ? "L" : "R")} camera='{camera.name}' " +
                $"cameraId={camera.GetInstanceID()} targetCamera='{volume.targetCamera?.name}' " +
                $"isGlobal={volume.isGlobal} enabled={volume.enabled} " +
                $"goLayer={go.layer} volumeLayerMask=0x{volumeMask:X8} " +
                $"passes={volume.customPasses.Count} passType='{pass.GetType().FullName}' " +
                $"pass.enabled={pass.enabled} injection={volume.injectionPoint}.");
        }

        private static int FirstSetLayerV81d(int mask)
        {
            if (mask == 0)
                return 0;

            for (int bit = 0; bit < 32; ++bit)
            {
                if ((mask & (1 << bit)) != 0)
                    return bit;
            }

            return 0;
        }

        private static void DestroyVolumeState(EyeState s)
        {
            if (s == null)
                return;

            if (s.VolumeObject != null)
            {
                try { UnityEngine.Object.Destroy(s.VolumeObject); } catch { }
            }

            DestroyRT(ref s.RawDepth);
            DestroyRT(ref s.DiagSlice0);
            DestroyRT(ref s.DiagSlice1);

            DestroyMaterial(ref s.CaptureSlice0Material);
            DestroyMaterial(ref s.DiagSlice0Material);
            DestroyMaterial(ref s.DiagSlice1Material);

            s.Camera = null;
            s.Volume = null;
            s.VolumeObject = null;
            s.LastExecuteFrame = -1;
        }

        public override void Setup(
            ScriptableRenderContext renderContext,
            CommandBuffer cmd)
        {
            _setupCountV81d++;

            if (_setupLogsRemainingV81d > 0)
            {
                _setupLogsRemainingV81d--;
                MelonLogger.Warning(
                    "[StereoRender][AER V81e][CustomDepth][SETUP-SEEN] " +
                    $"count={_setupCountV81d} pass='{name}' enabled={enabled} " +
                    $"logsRemaining={_setupLogsRemainingV81d}.");
            }

            // Resources are camera-size dependent and are allocated lazily in Execute().
        }

        public override void Execute(CustomPassContext ctx)
        {
            HDCamera hdPre = ctx.hdCamera;
            Camera cameraPre = hdPre != null ? hdPre.camera : null;

            _executeCountV81d++;

            if (_executeCameraLogsRemainingV81d > 0)
            {
                _executeCameraLogsRemainingV81d--;

                string depthDesc = "null";
                try
                {
                    RTHandle h = ctx.cameraDepthBuffer;
                    if (h != null && h.rt != null)
                    {
                        depthDesc =
                            (h.rt.name ?? "<unnamed>") + " " +
                            h.rt.width + "x" + h.rt.height + " " +
                            h.rt.dimension + " slices=" + h.rt.volumeDepth;
                    }
                }
                catch { }

                string v91Size = "";
                try
                {
                    int aw = hdPre != null ? hdPre.actualWidth : -1;
                    int ah = hdPre != null ? hdPre.actualHeight : -1;
                    RenderTexture trt = cameraPre != null ? cameraPre.targetTexture : null;
                    v91Size = $" hdrpActual={aw}x{ah} target={(trt != null ? trt.width + "x" + trt.height : "null")} targetDyn={(trt != null && trt.useDynamicScale)} camDyn={(cameraPre != null && cameraPre.allowDynamicResolution)}";
                }
                catch { }

                MelonLogger.Warning(
                    "[StereoRender][AER V91][CustomDepth][EXEC-SEEN] " +
                    $"camera='{cameraPre?.name ?? "<null>"}' frame={Time.frameCount} " +
                    $"aer={StereoRender.AlternateEyeRenderingEnabled} " +
                    $"depth={depthDesc}{v91Size} logsRemaining={_executeCameraLogsRemainingV81d}.");
            }

            // V116 measurement-only: record the viewport HDRP is ACTUALLY rasterizing for each
            // VR eye before the AER-only custom-depth work returns. This lets the settings UI
            // prove whether Quality vs UltraPerformance changes the real VR-eye render domain.
            try
            {
                if (cameraPre != null && hdPre != null && StereoRender.Instance != null)
                {
                    int observedEyeV116 = cameraPre == StereoRender.Instance.LeftCam ? 0 :
                                          cameraPre == StereoRender.Instance.RightCam ? 1 : -1;
                    if (observedEyeV116 >= 0)
                    {
                        EyeState observedStateV116 = _eyes[observedEyeV116];
                        observedStateV116.ObservedActualWidthV116 = hdPre.actualWidth;
                        observedStateV116.ObservedActualHeightV116 = hdPre.actualHeight;
                        observedStateV116.ObservedFrameV116 = Time.frameCount;
                    }
                }
            }
            catch { }

            if (!StereoRender.AlternateEyeRenderingEnabled)
                return;

            try
            {
                HDCamera hd = ctx.hdCamera;
                Camera camera = hd != null ? hd.camera : null;
                if (camera == null)
                    return;

                int eye;
                if (StereoRender.Instance != null &&
                    camera == StereoRender.Instance.LeftCam)
                    eye = 0;
                else if (StereoRender.Instance != null &&
                         camera == StereoRender.Instance.RightCam)
                    eye = 1;
                else
                    return;

                EyeState s = _eyes[eye];

                // V94b MATRIX TRUTH: in this exact Sons/HDRP IL2CPP surface the shader
                // matrices are not direct HDCamera properties.  HDRP stores them in
                // HDCamera.mainViewConstants (ViewConstants).  Read that container by
                // reflection so we do not depend on a generated-binding accessibility quirk.
                //
                // IMPORTANT: HDRP normally uses camera-relative rendering. Its shader view
                // matrix therefore has translation removed.  TryGetHdrpRasterMatricesV94b()
                // rebuilds an ABSOLUTE world->camera matrix from the HDRP view rotation plus
                // ViewConstants.worldSpaceCameraPos before exposing it to the AER shader.
                // The projection returned by ViewConstants.projMatrix is already the GPU-space
                // projection HDRP writes to _ProjMatrix; do NOT GL-convert it a second time.
                //
                // We retain ordinary Camera equivalents from this same instant as the control.
                // The visual A/B later changes ONLY historical/source unprojection matrices.
                try
                {
                    s.CameraProjectionAtExecute = camera.projectionMatrix;
                    s.CameraNonJitteredProjectionAtExecute = camera.nonJitteredProjectionMatrix;
                    s.CameraGpuProjectionAtExecute =
                        GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
                    s.CameraWorldToCameraAtExecute = camera.worldToCameraMatrix;

                    string hdMatrixPathV94b;
                    Matrix4x4 hdProjectionV94b;
                    Matrix4x4 hdWorldToCameraAbsV94b;
                    if (!TryGetHdrpRasterMatricesV94b(
                            hd,
                            out hdProjectionV94b,
                            out hdWorldToCameraAbsV94b,
                            out hdMatrixPathV94b))
                    {
                        s.MatrixFrame = -1;
                        LogExecuteFailure(
                            $"V94b mainViewConstants matrix capture unavailable " +
                            $"eye={(eye == 0 ? "L" : "R")}; staying fail-soft on Camera matrices.");
                    }
                    else
                    {
                        s.MatrixFrame = Time.frameCount;
                        s.HdProjectionMatrix = hdProjectionV94b;
                        s.HdWorldToCameraRHS = hdWorldToCameraAbsV94b;

                        if ((Time.frameCount % 300) == 0)
                        {
                            float projMax = MatrixMaxAbsDiffV94(
                                s.HdProjectionMatrix,
                                s.CameraGpuProjectionAtExecute);
                            float viewMax = MatrixMaxAbsDiffV94(
                                s.HdWorldToCameraRHS,
                                s.CameraWorldToCameraAtExecute);

                            MelonLogger.Msg(
                                $"[StereoRender][AER V94b][MatrixTruth] eye={(eye == 0 ? "L" : "R")} " +
                                $"frame={Time.frameCount} path={hdMatrixPathV94b} " +
                                $"projMaxAbs={projMax:E3} viewMaxAbs={viewMax:E3} " +
                                $"hdP[m00={s.HdProjectionMatrix.m00:F6},m11={s.HdProjectionMatrix.m11:F6}," +
                                $"m02={s.HdProjectionMatrix.m02:F6},m12={s.HdProjectionMatrix.m12:F6}] " +
                                $"camGpuP[m00={s.CameraGpuProjectionAtExecute.m00:F6}," +
                                $"m11={s.CameraGpuProjectionAtExecute.m11:F6}," +
                                $"m02={s.CameraGpuProjectionAtExecute.m02:F6}," +
                                $"m12={s.CameraGpuProjectionAtExecute.m12:F6}].");
                        }
                    }
                }
                catch (Exception e)
                {
                    s.MatrixFrame = -1;
                    LogExecuteFailure(
                        $"V94b matrix truth capture failed eye={(eye == 0 ? "L" : "R")}: " +
                        e.GetType().Name + ": " + e.Message);
                }

                RTHandle depthHandle = ctx.cameraDepthBuffer;
                if (depthHandle == null || depthHandle.rt == null)
                {
                    LogExecuteFailure(
                        $"eye={(eye == 0 ? "L" : "R")} ctx.cameraDepthBuffer/rt was null.");
                    return;
                }

                RenderTexture source = depthHandle.rt;
                int actualWidth = Mathf.Max(1, hd.actualWidth);
                int actualHeight = Mathf.Max(1, hd.actualHeight);

                if (!_viewportProbeLoggedV97[eye])
                {
                    _viewportProbeLoggedV97[eye] = true;
                    LogViewportMappingV97(
                        eye,
                        hd,
                        camera,
                        ctx.cameraColorBuffer,
                        depthHandle,
                        ctx.cameraMotionVectorsBuffer,
                        actualWidth,
                        actualHeight);
                }

                Shader shader;
                if (!StereoRender.TryGetV81DepthCaptureShader(out shader) ||
                    shader == null)
                {
                    LogExecuteFailure(
                        $"eye={(eye == 0 ? "L" : "R")} secondary diagnostic shader unavailable.");
                    return;
                }

                EnsureResources(s, shader, actualWidth, actualHeight);

                Vector4 zParams = Shader.GetGlobalVector("_ZBufferParams");

                int volumeDepth = 1;
                try { volumeDepth = Mathf.Max(1, source.volumeDepth); }
                catch { volumeDepth = 1; }

                // Slice 0 full-resolution owned raw-depth candidate.
                ConfigureSourceMaterial(
                    s.CaptureSlice0Material,
                    source,
                    actualWidth,
                    actualHeight,
                    zParams,
                    0);

                ctx.cmd.SetRenderTarget(s.RawDepth);
                ctx.cmd.SetViewport(new Rect(
                    0f, 0f,
                    s.RawDepth.width,
                    s.RawDepth.height));
                ctx.cmd.ClearRenderTarget(false, true, Color.clear);
                int capturePass = FindPassOrLogV81c(
                    s.CaptureSlice0Material,
                    source.dimension == TextureDimension.Tex2DArray
                        ? "V81CameraLocalDepthCaptureArray"
                        : "V81CameraLocalDepthCapture2D");
                if (capturePass < 0)
                    return;

                ctx.cmd.DrawProcedural(
                    Matrix4x4.identity,
                    s.CaptureSlice0Material,
                    capturePass,
                    MeshTopology.Triangles,
                    3,
                    1);

                if (StereoRender.AerV85CameraLocalDepthDiagnosticsEnabled)
                {
                    // Slice 0 tiny diagnostic.
                    ConfigureSourceMaterial(
                        s.DiagSlice0Material,
                        source,
                        actualWidth,
                        actualHeight,
                        zParams,
                        0);

                    ctx.cmd.SetRenderTarget(s.DiagSlice0);
                    ctx.cmd.SetViewport(new Rect(0f, 0f, DiagSize, DiagSize));
                    ctx.cmd.ClearRenderTarget(false, true, Color.clear);
                    int diagPass0 = FindPassOrLogV81c(
                        s.DiagSlice0Material,
                        source.dimension == TextureDimension.Tex2DArray
                            ? "V81CameraLocalDepthDiagArray"
                            : "V81CameraLocalDepthDiag2D");
                    if (diagPass0 < 0)
                        return;

                    ctx.cmd.DrawProcedural(
                        Matrix4x4.identity,
                        s.DiagSlice0Material,
                        diagPass0,
                        MeshTopology.Triangles,
                        3,
                        1);

                    // If HDRP hands us multiple array slices, capture slice 1 as well.
                    if (source.dimension == TextureDimension.Tex2DArray &&
                        volumeDepth > 1)
                    {
                        ConfigureSourceMaterial(
                            s.DiagSlice1Material,
                            source,
                            actualWidth,
                            actualHeight,
                            zParams,
                            1);

                        ctx.cmd.SetRenderTarget(s.DiagSlice1);
                        ctx.cmd.SetViewport(new Rect(0f, 0f, DiagSize, DiagSize));
                        ctx.cmd.ClearRenderTarget(false, true, Color.clear);
                        int diagPass1 = FindPassOrLogV81c(
                            s.DiagSlice1Material,
                            "V81CameraLocalDepthDiagArray");
                        if (diagPass1 < 0)
                            return;

                        ctx.cmd.DrawProcedural(
                            Matrix4x4.identity,
                            s.DiagSlice1Material,
                            diagPass1,
                            MeshTopology.Triangles,
                            3,
                            1);
                    }

                }

                s.LastExecuteFrame = Time.frameCount;
                s.SourceInstanceId = source.GetInstanceID();
                s.SourceName = source.name ?? "";
                s.SourceWidth = source.width;
                s.SourceHeight = source.height;
                s.SourceVolumeDepth = volumeDepth;
                s.SourceDimension = source.dimension;
                s.SourceGraphicsFormat = source.graphicsFormat;
                s.ActualWidth = actualWidth;
                s.ActualHeight = actualHeight;
                s.ZParams = zParams;

                if (s.LastExecuteFrame >= 0 &&
                    (s.LastExecuteFrame < 8 ||
                     (s.LastExecuteFrame % 300) == 0))
                {
                    MelonLogger.Msg(
                        $"[StereoRender][AER V81][CustomDepth] EXEC " +
                        $"eye={(eye == 0 ? "L" : "R")} frame={s.LastExecuteFrame} " +
                        $"ctxDepth='{s.SourceName}' id={s.SourceInstanceId} " +
                        $"alloc={s.SourceWidth}x{s.SourceHeight} " +
                        $"actual={actualWidth}x{actualHeight} " +
                        $"dim={s.SourceDimension} slices={s.SourceVolumeDepth} " +
                        $"fmt={s.SourceGraphicsFormat}.");
                }
            }
            catch (Exception e)
            {
                LogExecuteFailure(
                    e.GetType().Name + ": " + e.Message);
            }
        }

        public override void Cleanup()
        {
            // Volumes can be recreated across scene/rig churn. Static eye state
            // owns the external RTs; it is cleaned when the target camera changes.
        }

        private static int FindPassOrLogV81c(Material material, string passName)
        {
            if (material == null)
                return -1;

            int index = material.FindPass(passName);
            if (index < 0)
            {
                LogExecuteFailure(
                    $"shader='{material.shader?.name}' missing pass '{passName}' " +
                    $"passCount={(material.shader != null ? material.shader.passCount : -1)}.");
            }
            return index;
        }

        private static void LogViewportMappingV97(
            int eye,
            HDCamera hd,
            Camera camera,
            RTHandle colorHandle,
            RTHandle depthHandle,
            RTHandle motionHandle,
            int actualWidth,
            int actualHeight)
        {
            string eyeName = eye == 0 ? "L" : "R";

            try
            {
                RenderTexture target = camera != null ? camera.targetTexture : null;
                int targetW = target != null ? target.width : -1;
                int targetH = target != null ? target.height : -1;

                MelonLogger.Warning(
                    $"[StereoRender][AER V97][ViewportProbe] eye={eyeName} " +
                    $"hdActual={actualWidth}x{actualHeight} " +
                    $"cameraPixel={(camera != null ? camera.pixelWidth + "x" + camera.pixelHeight : "null")} " +
                    $"cameraScaledPixel={(camera != null ? camera.scaledPixelWidth + "x" + camera.scaledPixelHeight : "null")} " +
                    $"target={(target != null ? targetW + "x" + targetH : "null")} " +
                    $"targetDyn={(target != null && target.useDynamicScale)} " +
                    $"camDyn={(camera != null && camera.allowDynamicResolution)} " +
                    $"activeToTarget={(targetW > 0 && targetH > 0 ? ((float)actualWidth / targetW).ToString("F9") + "x" + ((float)actualHeight / targetH).ToString("F9") : "n/a")} " +
                    $"scalable={ScalableBufferManager.widthScaleFactor:F9}x{ScalableBufferManager.heightScaleFactor:F9}.");

                LogOneRtHandleV97(eyeName, "COLOR", colorHandle);
                LogOneRtHandleV97(eyeName, "DEPTH", depthHandle);
                LogOneRtHandleV97(eyeName, "MOTION", motionHandle);

                string[] globals = new[]
                {
                    "_RTHandleScale",
                    "_RTHandleScaleHistory",
                    "_ScreenSize",
                    "_PostProcessScreenSize",
                    "_DynamicResolutionScreenSize",
                    "_ScreenCoordScaleBias",
                    "_ScreenSizeOverride",
                    "_GlobalMipBias"
                };

                foreach (string n in globals)
                {
                    Vector4 v = Shader.GetGlobalVector(n);
                    MelonLogger.Msg(
                        $"[StereoRender][AER V97][ShaderGlobal] eye={eyeName} " +
                        $"name={n} value=({v.x:F9},{v.y:F9},{v.z:F9},{v.w:F9}).");
                }

                if (targetW > 0 && targetH > 0)
                {
                    // Purely diagnostic pixel-center relationships. These do not alter any
                    // shader coordinates. They make half/odd-size phase differences explicit.
                    float firstOutX = (0.5f / actualWidth) * targetW - 0.5f;
                    float firstOutY = (0.5f / actualHeight) * targetH - 0.5f;
                    float lastOutX = ((actualWidth - 0.5f) / actualWidth) * targetW - 0.5f;
                    float lastOutY = ((actualHeight - 0.5f) / actualHeight) * targetH - 0.5f;
                    MelonLogger.Msg(
                        $"[StereoRender][AER V97][PixelCenterMap] eye={eyeName} " +
                        $"depthCenter0->target=({firstOutX:F6},{firstOutY:F6}) " +
                        $"depthCenterLast->target=({lastOutX:F6},{lastOutY:F6}) " +
                        $"targetLast=({targetW - 1},{targetH - 1}).");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning(
                    $"[StereoRender][AER V97][ViewportProbe] eye={eyeName} FAILED: " +
                    e.GetType().Name + ": " + e.Message);
            }
        }

        private static void LogOneRtHandleV97(
            string eyeName,
            string label,
            RTHandle handle)
        {
            if (handle == null)
            {
                MelonLogger.Msg(
                    $"[StereoRender][AER V97][RTHandle] eye={eyeName} label={label} null.");
                return;
            }

            try
            {
                RenderTexture rt = handle.rt;
                RTHandleProperties p = handle.rtHandleProperties;
                Vector2Int scaled = handle.GetScaledSize();
                Vector2Int reference = handle.referenceSize;
                Vector2 sf = handle.scaleFactor;

                string alloc = rt != null
                    ? rt.width + "x" + rt.height + "/" + rt.dimension + "/slices=" + rt.volumeDepth
                    : "null";

                MelonLogger.Msg(
                    $"[StereoRender][AER V97][RTHandle] eye={eyeName} label={label} " +
                    $"alloc={alloc} useScaling={handle.useScaling} " +
                    $"reference={reference.x}x{reference.y} scaled={scaled.x}x{scaled.y} " +
                    $"scaleFactor=({sf.x:F9},{sf.y:F9}) " +
                    $"currentViewport={p.currentViewportSize.x}x{p.currentViewportSize.y} " +
                    $"currentRT={p.currentRenderTargetSize.x}x{p.currentRenderTargetSize.y} " +
                    $"previousViewport={p.previousViewportSize.x}x{p.previousViewportSize.y} " +
                    $"previousRT={p.previousRenderTargetSize.x}x{p.previousRenderTargetSize.y} " +
                    $"rtHandleScale=({p.rtHandleScale.x:F9},{p.rtHandleScale.y:F9}," +
                    $"{p.rtHandleScale.z:F9},{p.rtHandleScale.w:F9}).");
            }
            catch (Exception e)
            {
                MelonLogger.Warning(
                    $"[StereoRender][AER V97][RTHandle] eye={eyeName} label={label} FAILED: " +
                    e.GetType().Name + ": " + e.Message);
            }
        }

        private static void ConfigureSourceMaterial(
            Material material,
            RenderTexture source,
            int actualWidth,
            int actualHeight,
            Vector4 zParams,
            int slice)
        {
            if (material == null || source == null)
                return;

            if (source.dimension == TextureDimension.Tex2DArray)
                material.SetTexture("_V81CameraDepthArray", source);
            else
                material.SetTexture("_V81CameraDepth2D", source);

            material.SetVector(
                "_V81ActiveSize",
                new Vector4(
                    actualWidth,
                    actualHeight,
                    1.0f / Mathf.Max(1, actualWidth),
                    1.0f / Mathf.Max(1, actualHeight)));

            material.SetVector("_V81ZParams", zParams);
            material.SetFloat(
                "_V81MaxMeters",
                Mathf.Max(1.0f, StereoRender.AerDepthStereoWarpV48MaxMeters));
            material.SetFloat(
                "_V81DiscontinuityMeters",
                Mathf.Max(0.001f, StereoRender.AerDepthStereoWarpV48DiscontinuityMeters));
            material.SetFloat(
                "_V81DiscontinuityRelative",
                Mathf.Max(0f, StereoRender.AerDepthStereoWarpV48DiscontinuityRelative));
            material.SetFloat("_V81ArraySlice", slice);
        }

        private static void EnsureResources(
            EyeState s,
            Shader shader,
            int width,
            int height)
        {
            if (s.RawDepth == null ||
                !s.RawDepth.IsCreated() ||
                s.RawDepth.width != width ||
                s.RawDepth.height != height)
            {
                DestroyRT(ref s.RawDepth);

                s.RawDepth = new RenderTexture(
                    width,
                    height,
                    0,
                    RenderTextureFormat.RFloat)
                {
                    name = "SonsVR_V81_CameraLocalRawDepth",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    autoGenerateMips = false,
                    useDynamicScale = false
                };
                s.RawDepth.Create();
            }

            EnsureMaterial(
                ref s.CaptureSlice0Material,
                shader,
                "SonsVR_V81_LocalDepthCapture_Mat");

            if (StereoRender.AerV85CameraLocalDepthDiagnosticsEnabled)
            {
                EnsureDiagRT(ref s.DiagSlice0, "SonsVR_V81_LocalDepthDiag_Slice0");
                EnsureDiagRT(ref s.DiagSlice1, "SonsVR_V81_LocalDepthDiag_Slice1");
                EnsureMaterial(ref s.DiagSlice0Material, shader, "SonsVR_V81_LocalDepthDiag0_Mat");
                EnsureMaterial(ref s.DiagSlice1Material, shader, "SonsVR_V81_LocalDepthDiag1_Mat");
            }
        }

        private static void EnsureDiagRT(
            ref RenderTexture rt,
            string name)
        {
            if (rt != null &&
                rt.IsCreated() &&
                rt.width == DiagSize &&
                rt.height == DiagSize)
                return;

            DestroyRT(ref rt);

            rt = new RenderTexture(
                DiagSize,
                DiagSize,
                0,
                RenderTextureFormat.ARGB32)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                useDynamicScale = false
            };
            rt.Create();
        }

        private static void EnsureMaterial(
            ref Material material,
            Shader shader,
            string name)
        {
            if (material != null &&
                material.shader == shader)
                return;

            DestroyMaterial(ref material);
            material = new Material(shader) { name = name };
        }

        private static void DestroyRT(ref RenderTexture rt)
        {
            if (rt == null)
                return;

            try { rt.Release(); } catch { }
            try { UnityEngine.Object.Destroy(rt); } catch { }
            rt = null;
        }

        private static void DestroyMaterial(ref Material material)
        {
            if (material == null)
                return;

            try { UnityEngine.Object.Destroy(material); } catch { }
            material = null;
        }

        internal static void OnEndCameraRendering(Camera camera)
        {
            // V85 consumes the full-res RawDepth directly; diagnostic readback is optional.
            if (!StereoRender.AerV85CameraLocalDepthDiagnosticsEnabled)
                return;

            if (camera == null ||
                StereoRender.Instance == null ||
                !StereoRender.AlternateEyeRenderingEnabled)
                return;

            int eye;
            if (camera == StereoRender.Instance.LeftCam) eye = 0;
            else if (camera == StereoRender.Instance.RightCam) eye = 1;
            else return;

            EyeState s = _eyes[eye];

            PollStateReadbacks(eye, s);

            if (s.LastExecuteFrame != Time.frameCount ||
                s.DiagSlice0 == null ||
                !s.DiagSlice0.IsCreated() ||
                Time.unscaledTime < s.NextReadbackTime)
                return;

            if (s.ReadbackPending0 || s.ReadbackPending1)
                return;

            s.NextReadbackTime =
                Time.unscaledTime + ReadbackInterval;

            try
            {
                s.Readback0 = AsyncGPUReadback.Request(
                    s.DiagSlice0,
                    0,
                    0, DiagSize,
                    0, DiagSize,
                    0, 1,
                    TextureFormat.RGBA32,
                    null);

                s.ReadbackPending0 = true;
                SnapshotReadbackMeta(s, 0, 0);

                if (_readbackRequestLogsRemainingV81f > 0)
                {
                    _readbackRequestLogsRemainingV81f--;
                    MelonLogger.Warning(
                        "[StereoRender][AER V81f][CustomDepth][READBACK-REQUEST] " +
                        $"eye={(eye == 0 ? "L" : "R")} frame={s.LastExecuteFrame} " +
                        $"diag={DiagSize}x{DiagSize} source='{s.SourceName}' " +
                        $"sourceId={s.SourceInstanceId} " +
                        $"logsRemaining={_readbackRequestLogsRemainingV81f}.");
                }

                if (s.SourceDimension == TextureDimension.Tex2DArray &&
                    s.SourceVolumeDepth > 1 &&
                    s.DiagSlice1 != null &&
                    s.DiagSlice1.IsCreated())
                {
                    s.Readback1 = AsyncGPUReadback.Request(
                        s.DiagSlice1,
                        0,
                        0, DiagSize,
                        0, DiagSize,
                        0, 1,
                        TextureFormat.RGBA32,
                        null);

                    s.ReadbackPending1 = true;
                    SnapshotReadbackMeta(s, 1, 1);
                }
            }
            catch (Exception e)
            {
                s.ReadbackPending0 = false;
                s.ReadbackPending1 = false;
                LogExecuteFailure(
                    "AsyncGPUReadback request failed: " +
                    e.GetType().Name + ": " + e.Message);
            }
        }

        internal static void PollReadbacks()
        {
            if (_installedAtV81d >= 0f &&
                Time.unscaledTime > _installedAtV81d + 3.0f)
            {
                if (!_noSetupWarningV81d &&
                    _setupCountV81d == 0)
                {
                    _noSetupWarningV81d = true;
                    MelonLogger.Error(
                        "[StereoRender][AER V81e][CustomDepth] CUSTOM PASS SETUP HAS NOT RUN " +
                        "within 3 seconds. HDRP did not instantiate/schedule the pass lifecycle.");
                }

                if (!_noExecuteWarningV81d &&
                    _executeCountV81d == 0)
                {
                    _noExecuteWarningV81d = true;
                    MelonLogger.Error(
                        "[StereoRender][AER V81e][CustomDepth] CUSTOM PASS EXECUTE HAS NOT RUN " +
                        "within 3 seconds. Do not interpret camera-local depth.");
                }
            }

            PollStateReadbacks(0, _eyes[0]);
            PollStateReadbacks(1, _eyes[1]);
        }

        private static void SnapshotReadbackMeta(
            EyeState s,
            int which,
            int slice)
        {
            if (which == 0)
            {
                s.ReadbackFrame0 = s.LastExecuteFrame;
                s.ReadbackSlice0 = slice;
                s.ReadbackSourceInstanceId0 = s.SourceInstanceId;
                s.ReadbackSourceName0 = s.SourceName;
                s.ReadbackSourceWidth0 = s.SourceWidth;
                s.ReadbackSourceHeight0 = s.SourceHeight;
                s.ReadbackSourceVolumeDepth0 = s.SourceVolumeDepth;
                s.ReadbackZParams0 = s.ZParams;
            }
            else
            {
                s.ReadbackFrame1 = s.LastExecuteFrame;
                s.ReadbackSlice1 = slice;
                s.ReadbackSourceInstanceId1 = s.SourceInstanceId;
                s.ReadbackSourceName1 = s.SourceName;
                s.ReadbackSourceWidth1 = s.SourceWidth;
                s.ReadbackSourceHeight1 = s.SourceHeight;
                s.ReadbackSourceVolumeDepth1 = s.SourceVolumeDepth;
                s.ReadbackZParams1 = s.ZParams;
            }
        }

        private static void PollStateReadbacks(
            int eye,
            EyeState s)
        {
            if (s.ReadbackPending0 && s.Readback0.done)
            {
                s.ReadbackPending0 = false;
                ProcessReadback(
                    eye,
                    s.Readback0,
                    s.ReadbackFrame0,
                    s.ReadbackSlice0,
                    s.ReadbackSourceInstanceId0,
                    s.ReadbackSourceName0,
                    s.ReadbackSourceWidth0,
                    s.ReadbackSourceHeight0,
                    s.ReadbackSourceVolumeDepth0,
                    s.ReadbackZParams0);
            }

            if (s.ReadbackPending1 && s.Readback1.done)
            {
                s.ReadbackPending1 = false;
                ProcessReadback(
                    eye,
                    s.Readback1,
                    s.ReadbackFrame1,
                    s.ReadbackSlice1,
                    s.ReadbackSourceInstanceId1,
                    s.ReadbackSourceName1,
                    s.ReadbackSourceWidth1,
                    s.ReadbackSourceHeight1,
                    s.ReadbackSourceVolumeDepth1,
                    s.ReadbackZParams1);
            }
        }

        private static void ProcessReadback(
            int eye,
            AsyncGPUReadbackRequest req,
            int frame,
            int slice,
            int sourceInstanceId,
            string sourceName,
            int sourceWidth,
            int sourceHeight,
            int sourceVolumeDepth,
            Vector4 zParams)
        {
            if (req.hasError)
            {
                LogExecuteFailure(
                    $"eye={(eye == 0 ? "L" : "R")} slice={slice} local-depth AsyncGPUReadback ERROR.");
                return;
            }

            try
            {
                var native = req.GetData<byte>();
                byte[] bytes = new byte[native.Length];
                for (int i = 0; i < native.Length; ++i)
                    bytes[i] = native[i];

                DepthStats stats = AnalyzeDiag(bytes, DiagSize, DiagSize);

                if (_readbackDoneLogsRemainingV81f > 0)
                {
                    _readbackDoneLogsRemainingV81f--;
                    MelonLogger.Warning(
                        "[StereoRender][AER V81f][CustomDepth][READBACK-DONE] " +
                        $"eye={(eye == 0 ? "L" : "R")} frame={frame} slice={slice} " +
                        $"zeroFrac={stats.ZeroFraction:F6} validFrac={stats.ValidFraction:F6} " +
                        $"medianDepthM={stats.ApproxMedianDepthMeters:F3} " +
                        $"logsRemaining={_readbackDoneLogsRemainingV81f}.");
                }

                HDRPDlssRuntimeTraceV78.RecordLocalDepthV81(
                    eye == 0 ? "L" : "R",
                    frame,
                    slice,
                    sourceWidth,
                    sourceHeight,
                    sourceVolumeDepth,
                    sourceInstanceId,
                    sourceName,
                    stats.ZeroFraction,
                    stats.NonzeroFraction,
                    stats.ValidFraction,
                    stats.TooFarFraction,
                    stats.DiscontinuityFraction,
                    stats.ApproxMedianDepthMeters,
                    stats.ValidTop,
                    stats.ValidMiddle,
                    stats.ValidBottom,
                    stats.ValidLeft,
                    stats.ValidCenter,
                    stats.ValidRight,
                    stats.LargestInvalidFraction,
                    stats.LargestInvalidBBox,
                    stats.LargestInvalidCentroid,
                    zParams);
            }
            catch (Exception e)
            {
                LogExecuteFailure(
                    "local-depth readback processing failed: " +
                    e.GetType().Name + ": " + e.Message);
            }
        }

        private struct DepthStats
        {
            internal float ZeroFraction;
            internal float NonzeroFraction;
            internal float ValidFraction;
            internal float TooFarFraction;
            internal float DiscontinuityFraction;
            internal float ApproxMedianDepthMeters;
            internal float ValidTop;
            internal float ValidMiddle;
            internal float ValidBottom;
            internal float ValidLeft;
            internal float ValidCenter;
            internal float ValidRight;
            internal float LargestInvalidFraction;
            internal Vector4 LargestInvalidBBox;
            internal Vector2 LargestInvalidCentroid;
        }

        private static DepthStats AnalyzeDiag(
            byte[] bytes,
            int width,
            int height)
        {
            int pixels = Mathf.Max(1, width * height);
            int validCount = 0;
            int nonzeroCount = 0;
            int discCount = 0;
            List<float> depthMeters = new List<float>(pixels);
            bool[] invalid = new bool[pixels];

            float maxMeters =
                Mathf.Max(1.0f, StereoRender.AerDepthStereoWarpV48MaxMeters);
            float logDenom =
                Mathf.Log(1.0f + maxMeters, 2.0f);

            for (int p = 0; p < pixels; ++p)
            {
                int bi = p * 4;
                if (bi + 3 >= bytes.Length)
                    break;

                bool valid = bytes[bi] >= 128;
                bool nonzero = bytes[bi + 1] >= 128;
                bool disc = bytes[bi + 2] >= 128;

                if (valid) validCount++;
                if (nonzero) nonzeroCount++;
                if (disc) discCount++;

                invalid[p] = !valid;

                if (valid)
                {
                    float n = bytes[bi + 3] / 255.0f;
                    float eye =
                        Mathf.Pow(2.0f, n * logDenom) - 1.0f;
                    depthMeters.Add(eye);
                }
            }

            depthMeters.Sort();
            float medianDepth = 0f;
            if (depthMeters.Count > 0)
                medianDepth =
                    depthMeters[depthMeters.Count / 2];

            bool[] seen = new bool[pixels];
            int[] queue = new int[pixels];

            int largest = 0;
            int largestMinX = 0, largestMaxX = -1;
            int largestMinY = 0, largestMaxY = -1;
            long largestSumX = 0, largestSumY = 0;

            for (int start = 0; start < pixels; ++start)
            {
                if (!invalid[start] || seen[start])
                    continue;

                int head = 0, tail = 0;
                queue[tail++] = start;
                seen[start] = true;

                int count = 0;
                int minX = width, maxX = -1;
                int minY = height, maxY = -1;
                long sumX = 0, sumY = 0;

                while (head < tail)
                {
                    int idx = queue[head++];
                    int x = idx % width;
                    int y = idx / width;

                    count++;
                    minX = Mathf.Min(minX, x);
                    maxX = Mathf.Max(maxX, x);
                    minY = Mathf.Min(minY, y);
                    maxY = Mathf.Max(maxY, y);
                    sumX += x;
                    sumY += y;

                    if (x > 0)
                    {
                        int n = idx - 1;
                        if (invalid[n] && !seen[n])
                        {
                            seen[n] = true;
                            queue[tail++] = n;
                        }
                    }
                    if (x + 1 < width)
                    {
                        int n = idx + 1;
                        if (invalid[n] && !seen[n])
                        {
                            seen[n] = true;
                            queue[tail++] = n;
                        }
                    }
                    if (y > 0)
                    {
                        int n = idx - width;
                        if (invalid[n] && !seen[n])
                        {
                            seen[n] = true;
                            queue[tail++] = n;
                        }
                    }
                    if (y + 1 < height)
                    {
                        int n = idx + width;
                        if (invalid[n] && !seen[n])
                        {
                            seen[n] = true;
                            queue[tail++] = n;
                        }
                    }
                }

                if (count > largest)
                {
                    largest = count;
                    largestMinX = minX;
                    largestMaxX = maxX;
                    largestMinY = minY;
                    largestMaxY = maxY;
                    largestSumX = sumX;
                    largestSumY = sumY;
                }
            }

            Vector4 bbox = Vector4.zero;
            Vector2 centroid = Vector2.zero;

            if (largest > 0)
            {
                bbox = new Vector4(
                    largestMinX / (float)width,
                    largestMinY / (float)height,
                    (largestMaxX + 1) / (float)width,
                    (largestMaxY + 1) / (float)height);

                centroid = new Vector2(
                    (float)(
                        largestSumX / (double)largest + 0.5) / width,
                    (float)(
                        largestSumY / (double)largest + 0.5) / height);
            }

            int xA = width / 3;
            int xB = (2 * width) / 3;
            int yA = height / 3;
            int yB = (2 * height) / 3;

            float denom = pixels;
            float validFrac = validCount / denom;
            float nonzeroFrac = nonzeroCount / denom;

            DepthStats result = new DepthStats();
            result.ValidFraction = validFrac;
            result.NonzeroFraction = nonzeroFrac;
            result.ZeroFraction = 1f - nonzeroFrac;
            result.TooFarFraction =
                Mathf.Max(0f, nonzeroFrac - validFrac);
            result.DiscontinuityFraction =
                discCount / denom;
            result.ApproxMedianDepthMeters =
                medianDepth;

            result.ValidBottom = RegionValidFraction(
                bytes, width, height,
                0, width, 0, yA);
            result.ValidMiddle = RegionValidFraction(
                bytes, width, height,
                0, width, yA, yB);
            result.ValidTop = RegionValidFraction(
                bytes, width, height,
                0, width, yB, height);

            result.ValidLeft = RegionValidFraction(
                bytes, width, height,
                0, xA, 0, height);
            result.ValidCenter = RegionValidFraction(
                bytes, width, height,
                xA, xB, 0, height);
            result.ValidRight = RegionValidFraction(
                bytes, width, height,
                xB, width, 0, height);

            result.LargestInvalidFraction =
                largest / denom;
            result.LargestInvalidBBox = bbox;
            result.LargestInvalidCentroid = centroid;
            return result;
        }

        private static float RegionValidFraction(
            byte[] bytes,
            int width,
            int height,
            int x0,
            int x1,
            int y0,
            int y1)
        {
            long valid = 0;
            long total = 0;

            for (int y = y0; y < y1; ++y)
            {
                for (int x = x0; x < x1; ++x)
                {
                    int bi = (y * width + x) * 4;
                    if (bi + 3 >= bytes.Length)
                        continue;

                    if (bytes[bi] >= 128)
                        valid++;
                    total++;
                }
            }

            return total > 0
                ? (float)(valid / (double)total)
                : 0f;
        }

        private static object GetMemberValueV94b(object subject, string memberName)
        {
            if (subject == null || string.IsNullOrEmpty(memberName))
                return null;

            Type t = subject.GetType();
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            PropertyInfo p = t.GetProperty(memberName, flags);
            if (p != null)
                return p.GetValue(subject, null);

            FieldInfo f = t.GetField(memberName, flags);
            return f != null ? f.GetValue(subject) : null;
        }

        private static bool TryGetMatrixMemberV94b(
            object subject, string memberName, out Matrix4x4 value)
        {
            value = Matrix4x4.identity;
            object boxed = GetMemberValueV94b(subject, memberName);
            if (boxed is Matrix4x4 m)
            {
                value = m;
                return true;
            }
            return false;
        }

        private static bool TryGetVector3MemberV94b(
            object subject, string memberName, out Vector3 value)
        {
            value = Vector3.zero;
            object boxed = GetMemberValueV94b(subject, memberName);
            if (boxed is Vector3 v)
            {
                value = v;
                return true;
            }
            return false;
        }

        /// <summary>
        /// V94b: fetch HDRP's exact ViewConstants from HDCamera.mainViewConstants without
        /// compile-time dependency on how Il2CppInterop exposed the nested struct.
        ///
        /// HDRP's projMatrix is already the platform/GPU projection written to _ProjMatrix.
        /// Its view matrix is commonly camera-relative (translation removed), so construct an
        /// absolute cameraToWorld by taking the inverse view rotation and restoring the recorded
        /// worldSpaceCameraPos.  Inverting that gives an absolute worldToCamera matrix compatible
        /// with the absolute-world convention used by TemporalScatterV89.compute.
        /// </summary>
        private static bool TryGetHdrpRasterMatricesV94b(
            HDCamera hd,
            out Matrix4x4 gpuProjection,
            out Matrix4x4 absoluteWorldToCamera,
            out string path)
        {
            gpuProjection = Matrix4x4.identity;
            absoluteWorldToCamera = Matrix4x4.identity;
            path = "unavailable";

            if (hd == null)
                return false;

            object viewConstants = GetMemberValueV94b(hd, "mainViewConstants");
            if (viewConstants == null)
                return false;

            Matrix4x4 view;
            Matrix4x4 invView;
            Vector3 cameraPos;
            if (!TryGetMatrixMemberV94b(viewConstants, "projMatrix", out gpuProjection) ||
                !TryGetVector3MemberV94b(viewConstants, "worldSpaceCameraPos", out cameraPos))
                return false;

            bool haveInvView =
                TryGetMatrixMemberV94b(viewConstants, "invViewMatrix", out invView);
            if (!haveInvView)
            {
                if (!TryGetMatrixMemberV94b(viewConstants, "viewMatrix", out view))
                    return false;
                invView = view.inverse;
                path = "mainViewConstants.projMatrix + viewMatrix.inverse + worldSpaceCameraPos";
            }
            else
            {
                path = "mainViewConstants.projMatrix + invViewMatrix + worldSpaceCameraPos";
            }

            // Restore absolute camera translation.  For camera-relative HDRP this column is
            // intentionally zero before this assignment; for non-camera-relative HDRP it should
            // already equal cameraPos, so assigning it again is harmless.
            invView.SetColumn(3, new Vector4(cameraPos.x, cameraPos.y, cameraPos.z, 1f));
            absoluteWorldToCamera = invView.inverse;
            return true;
        }

        private static float MatrixMaxAbsDiffV94(Matrix4x4 a, Matrix4x4 b)
        {
            float maxAbs = 0f;
            for (int r = 0; r < 4; ++r)
            {
                for (int c = 0; c < 4; ++c)
                    maxAbs = Mathf.Max(maxAbs, Mathf.Abs(a[r, c] - b[r, c]));
            }
            return maxAbs;
        }

        /// <summary>
        /// V94: return exact HDRP raster matrices captured in the same CustomPass execution
        /// that produced the owned camera-local depth.  Callers must still verify frame equality.
        /// </summary>
        internal static bool TryGetRasterMatricesV94(
            int eye,
            out int frame,
            out Matrix4x4 hdProjectionMatrix,
            out Matrix4x4 hdWorldToCameraRHS,
            out Matrix4x4 cameraGpuProjectionAtExecute,
            out Matrix4x4 cameraWorldToCameraAtExecute,
            out Matrix4x4 cameraProjectionAtExecute,
            out Matrix4x4 cameraNonJitteredProjectionAtExecute)
        {
            frame = -1;
            hdProjectionMatrix = Matrix4x4.identity;
            hdWorldToCameraRHS = Matrix4x4.identity;
            cameraGpuProjectionAtExecute = Matrix4x4.identity;
            cameraWorldToCameraAtExecute = Matrix4x4.identity;
            cameraProjectionAtExecute = Matrix4x4.identity;
            cameraNonJitteredProjectionAtExecute = Matrix4x4.identity;

            if (eye < 0 || eye > 1)
                return false;

            EyeState s = _eyes[eye];
            if (s.MatrixFrame < 0)
                return false;

            frame = s.MatrixFrame;
            hdProjectionMatrix = s.HdProjectionMatrix;
            hdWorldToCameraRHS = s.HdWorldToCameraRHS;
            cameraGpuProjectionAtExecute = s.CameraGpuProjectionAtExecute;
            cameraWorldToCameraAtExecute = s.CameraWorldToCameraAtExecute;
            cameraProjectionAtExecute = s.CameraProjectionAtExecute;
            cameraNonJitteredProjectionAtExecute = s.CameraNonJitteredProjectionAtExecute;
            return true;
        }

        internal static bool TryGetObservedViewportV116(
            int eye,
            out int actualWidth,
            out int actualHeight,
            out int frame)
        {
            actualWidth = -1;
            actualHeight = -1;
            frame = -1;
            if (eye < 0 || eye > 1) return false;

            EyeState s = _eyes[eye];
            if (s.ObservedFrameV116 < 0) return false;
            actualWidth = s.ObservedActualWidthV116;
            actualHeight = s.ObservedActualHeightV116;
            frame = s.ObservedFrameV116;
            return actualWidth > 0 && actualHeight > 0;
        }

        internal static bool TryGetOwnedRawDepth(
            int eye,
            out RenderTexture depth,
            out int frame)
        {
            depth = null;
            frame = -1;

            if (eye < 0 || eye > 1)
                return false;

            EyeState s = _eyes[eye];
            if (s.RawDepth == null ||
                !s.RawDepth.IsCreated() ||
                s.LastExecuteFrame < 0)
                return false;

            depth = s.RawDepth;
            frame = s.LastExecuteFrame;
            return true;
        }

        private static void LogExecuteFailure(
            string message)
        {
            if (_executeFailureLogsRemaining <= 0)
                return;

            _executeFailureLogsRemaining--;
            MelonLogger.Warning(
                "[StereoRender][AER V81][CustomDepth] " +
                message +
                " Logs remaining=" +
                _executeFailureLogsRemaining + ".");
        }
    }
}
