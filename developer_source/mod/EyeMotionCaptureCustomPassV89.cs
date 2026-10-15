using MelonLoader;
using System;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SonsVRMod
{
    /// <summary>
    /// V89 camera-local HDRP motion-vector capture.
    ///
    /// WHY THIS IS A SEPARATE CUSTOM PASS FROM V81 DEPTH:
    /// HDRP's AfterOpaqueDepthAndNormal point is early enough for exact camera depth, but
    /// its motion-vector target is not yet a complete read source. HDRP documents the
    /// BeforePreRefraction point as the first point where the motion buffer contains the
    /// completed opaque object + camera vectors. V89 therefore keeps V81 depth exactly
    /// where it is and installs one second camera-mode pass per physical eye here.
    ///
    /// The owned RGHalf texture is intentionally persistent per eye. Under AER each eye
    /// really renders only on its own alternating frames, so that texture naturally remains
    /// the exact motion field belonging to the eye's most recent REAL color/depth history.
    /// No CPU readback is performed.
    /// </summary>
    [Serializable]
    internal class EyeMotionCaptureCustomPassV89 : CustomPass
    {
        public EyeMotionCaptureCustomPassV89(IntPtr ptr) : base(ptr) { }

        public EyeMotionCaptureCustomPassV89()
            : base(ClassInjector.DerivedConstructorPointer<EyeMotionCaptureCustomPassV89>())
        {
            ClassInjector.DerivedConstructorBody(this);
        }

        private sealed class EyeState
        {
            internal Camera Camera;
            internal GameObject VolumeObject;
            internal CustomPassVolume Volume;
            internal RenderTexture RawMotion;
            internal Material CaptureMaterial;

            internal int LastExecuteFrame = -1;
            internal int SourceInstanceId;
            internal string SourceName = "";
            internal int SourceWidth;
            internal int SourceHeight;
            internal int SourceVolumeDepth;
            internal TextureDimension SourceDimension;
            internal UnityEngine.Experimental.Rendering.GraphicsFormat SourceGraphicsFormat;
            internal int ActualWidth;
            internal int ActualHeight;
        }

        private static readonly EyeState[] _eyes =
        {
            new EyeState(),
            new EyeState()
        };

        private static bool _registered;
        private static bool _installLogged;
        private static int _setupLogsRemaining = 4;
        private static int _executeLogsRemaining = 8;
        private static int _failureLogsRemaining = 12;

        internal static void RegisterInjectedType()
        {
            if (_registered)
                return;

            ClassInjector.RegisterTypeInIl2Cpp<EyeMotionCaptureCustomPassV89>();
            _registered = true;
            MelonLogger.Warning(
                "[StereoRender][AER V89][CustomMotion] injected CustomPass type registered.");
        }

        internal static void EnsureVolumes(Camera left, Camera right)
        {
            if (!_registered || left == null || right == null)
                return;

            EnsureCameraModeVolume(0, left);
            EnsureCameraModeVolume(1, right);

            if (!_installLogged && _eyes[0].Volume != null && _eyes[1].Volume != null)
            {
                _installLogged = true;
                MelonLogger.Warning(
                    "[StereoRender][AER V89][CustomMotion] TWO CAMERA-MODE motion CustomPassVolumes " +
                    "installed at BeforePreRefraction. This is deliberately later than V81 depth: " +
                    "HDRP's cameraMotionVectorsBuffer is complete here (opaque object + camera motion). " +
                    "No readback/diagnostic board is enabled.");
            }
        }

        private static void EnsureCameraModeVolume(int eye, Camera camera)
        {
            EyeState s = _eyes[eye];
            if (s.Camera == camera && s.Volume != null && s.VolumeObject != null)
                return;

            DestroyVolumeState(s);
            s.Camera = camera;

            GameObject go = new GameObject(
                eye == 0
                    ? "SonsVR_V89_CustomMotion_Left"
                    : "SonsVR_V89_CustomMotion_Right");

            go.SetActive(false);
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(camera.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            int volumeMask = -1;
            int chosenLayer = 0;
            try
            {
                HDAdditionalCameraData hdData = camera.GetComponent<HDAdditionalCameraData>();
                if (hdData != null)
                {
                    volumeMask = hdData.volumeLayerMask.value;
                    chosenLayer = FirstSetLayer(volumeMask);
                }
            }
            catch (Exception e)
            {
                LogFailure(
                    $"could not inspect volumeLayerMask for camera='{camera.name}': " +
                    e.GetType().Name + ": " + e.Message);
            }

            go.layer = chosenLayer;

            CustomPassVolume volume = go.AddComponent<CustomPassVolume>();
            volume.enabled = true;
            volume.isGlobal = true;
            volume.injectionPoint = CustomPassInjectionPoint.BeforePreRefraction;
            volume.priority = 1000001f;

            Il2CppSystem.Type injectedPassType =
                Il2CppInterop.Runtime.Il2CppType.From(typeof(EyeMotionCaptureCustomPassV89));
            CustomPass rawPass = volume.AddPassOfType(injectedPassType);
            EyeMotionCaptureCustomPassV89 pass = rawPass as EyeMotionCaptureCustomPassV89;
            if (pass == null)
                throw new Exception(
                    "CustomPassVolume.AddPassOfType(EyeMotionCaptureCustomPassV89) returned null/wrong type.");

            pass.enabled = true;
            pass.name = eye == 0
                ? "SonsVR V89 CameraLocal Motion LEFT"
                : "SonsVR V89 CameraLocal Motion RIGHT";

            volume.targetCamera = camera;
            s.VolumeObject = go;
            s.Volume = volume;
            go.SetActive(true);

            MelonLogger.Warning(
                "[StereoRender][AER V89][CustomMotion] installed " +
                $"eye={(eye == 0 ? "L" : "R")} camera='{camera.name}' " +
                $"cameraId={camera.GetInstanceID()} targetCamera='{volume.targetCamera?.name}' " +
                $"goLayer={go.layer} volumeLayerMask=0x{volumeMask:X8} " +
                $"passes={volume.customPasses.Count} injection={volume.injectionPoint}.");
        }

        private static int FirstSetLayer(int mask)
        {
            if (mask == 0)
                return 0;
            for (int bit = 0; bit < 32; ++bit)
                if ((mask & (1 << bit)) != 0)
                    return bit;
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

            DestroyRT(ref s.RawMotion);
            DestroyMaterial(ref s.CaptureMaterial);
            s.Camera = null;
            s.VolumeObject = null;
            s.Volume = null;
            s.LastExecuteFrame = -1;
        }

        public override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            if (_setupLogsRemaining > 0)
            {
                _setupLogsRemaining--;
                MelonLogger.Warning(
                    "[StereoRender][AER V89][CustomMotion][SETUP-SEEN] " +
                    $"pass='{name}' enabled={enabled} logsRemaining={_setupLogsRemaining}.");
            }
        }

        public override void Execute(CustomPassContext ctx)
        {
            if (!StereoRender.AlternateEyeRenderingEnabled)
                return;

            try
            {
                HDCamera hd = ctx.hdCamera;
                Camera camera = hd != null ? hd.camera : null;
                if (camera == null || StereoRender.Instance == null)
                    return;

                int eye;
                if (camera == StereoRender.Instance.LeftCam) eye = 0;
                else if (camera == StereoRender.Instance.RightCam) eye = 1;
                else return;

                RTHandle motionHandle = ctx.cameraMotionVectorsBuffer;
                if (motionHandle == null || motionHandle.rt == null)
                {
                    LogFailure(
                        $"eye={(eye == 0 ? "L" : "R")} ctx.cameraMotionVectorsBuffer/rt was null.");
                    return;
                }

                RenderTexture source = motionHandle.rt;
                int actualWidth = Mathf.Max(1, hd.actualWidth);
                int actualHeight = Mathf.Max(1, hd.actualHeight);

                Shader shader;
                if (!StereoRender.TryGetV81DepthCaptureShader(out shader) || shader == null)
                {
                    LogFailure("shared V81/V89 capture shader unavailable.");
                    return;
                }

                EyeState s = _eyes[eye];
                EnsureResources(s, shader, actualWidth, actualHeight);

                int volumeDepth = 1;
                try { volumeDepth = Mathf.Max(1, source.volumeDepth); }
                catch { volumeDepth = 1; }

                ConfigureSourceMaterial(s.CaptureMaterial, source, actualWidth, actualHeight, 0);

                ctx.cmd.SetRenderTarget(s.RawMotion);
                ctx.cmd.SetViewport(new Rect(0f, 0f, s.RawMotion.width, s.RawMotion.height));
                ctx.cmd.ClearRenderTarget(false, true, Color.clear);

                string passName = source.dimension == TextureDimension.Tex2DArray
                    ? "V89CameraMotionCaptureArray"
                    : "V89CameraMotionCapture2D";
                int capturePass = s.CaptureMaterial.FindPass(passName);
                if (capturePass < 0)
                {
                    LogFailure(
                        $"shared shader missing pass '{passName}' passCount={s.CaptureMaterial.passCount}.");
                    return;
                }

                ctx.cmd.DrawProcedural(
                    Matrix4x4.identity,
                    s.CaptureMaterial,
                    capturePass,
                    MeshTopology.Triangles,
                    3,
                    1);

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

                if (_executeLogsRemaining > 0)
                {
                    _executeLogsRemaining--;
                    MelonLogger.Warning(
                        "[StereoRender][AER V89][CustomMotion][CAPTURE] " +
                        $"eye={(eye == 0 ? "L" : "R")} frame={s.LastExecuteFrame} " +
                        $"ctxMotion='{s.SourceName}' id={s.SourceInstanceId} " +
                        $"alloc={s.SourceWidth}x{s.SourceHeight} actual={actualWidth}x{actualHeight} " +
                        $"dim={s.SourceDimension} slices={s.SourceVolumeDepth} fmt={s.SourceGraphicsFormat} " +
                        $"owned={s.RawMotion.width}x{s.RawMotion.height} RGHalf. " +
                        $"logsRemaining={_executeLogsRemaining}.");
                }
            }
            catch (Exception e)
            {
                LogFailure(e.GetType().Name + ": " + e.Message);
            }
        }

        public override void Cleanup()
        {
            // Static EyeState owns the persistent external RT. It is destroyed/recreated when
            // the target eye camera changes, matching V81's lifetime strategy.
        }

        internal static bool TryGetOwnedMotion(
            int eye,
            out RenderTexture motion,
            out int frame)
        {
            motion = null;
            frame = -1;
            if (eye < 0 || eye > 1)
                return false;

            EyeState s = _eyes[eye];
            if (s.RawMotion == null || !s.RawMotion.IsCreated() || s.LastExecuteFrame < 0)
                return false;

            motion = s.RawMotion;
            frame = s.LastExecuteFrame;
            return true;
        }

        private static void EnsureResources(EyeState s, Shader shader, int width, int height)
        {
            if (s.RawMotion == null || !s.RawMotion.IsCreated() ||
                s.RawMotion.width != width || s.RawMotion.height != height)
            {
                DestroyRT(ref s.RawMotion);
                s.RawMotion = new RenderTexture(
                    width,
                    height,
                    0,
                    RenderTextureFormat.RGHalf,
                    RenderTextureReadWrite.Linear)
                {
                    name = "SonsVR_V89_CameraLocalMotion",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    autoGenerateMips = false,
                    useDynamicScale = false,
                    antiAliasing = 1
                };
                s.RawMotion.Create();
                if (!s.RawMotion.IsCreated())
                    throw new Exception("failed to create owned V89 RGHalf motion RT.");
            }

            if (s.CaptureMaterial == null || s.CaptureMaterial.shader != shader)
            {
                DestroyMaterial(ref s.CaptureMaterial);
                s.CaptureMaterial = new Material(shader)
                {
                    name = "SonsVR_V89_LocalMotionCapture_Mat"
                };
            }
        }

        private static void ConfigureSourceMaterial(
            Material material,
            RenderTexture source,
            int actualWidth,
            int actualHeight,
            int slice)
        {
            if (material == null || source == null)
                return;

            if (source.dimension == TextureDimension.Tex2DArray)
                material.SetTexture("_V89CameraMotionArray", source);
            else
                material.SetTexture("_V89CameraMotion2D", source);

            material.SetVector(
                "_V89MotionActiveSize",
                new Vector4(
                    actualWidth,
                    actualHeight,
                    1.0f / Mathf.Max(1, actualWidth),
                    1.0f / Mathf.Max(1, actualHeight)));
            material.SetFloat("_V89MotionArraySlice", slice);
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

        private static void LogFailure(string message)
        {
            if (_failureLogsRemaining <= 0)
                return;

            _failureLogsRemaining--;
            MelonLogger.Warning(
                "[StereoRender][AER V89][CustomMotion] " + message +
                " Logs remaining=" + _failureLogsRemaining + ".");
        }
    }
}
