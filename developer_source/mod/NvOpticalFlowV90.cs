using MelonLoader;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace SonsVRMod
{
    /// <summary>
    /// V90 NVIDIA Optical Flow bridge.
    ///
    /// This class deliberately owns ONLY the transport/integration plumbing:
    ///   real eye ARGB32 -> small shader -> R8 luminance ping-pong
    ///   Unity native texture pointers -> SonsVR_NvOF_V90.dll
    ///   NVOFA CURRENT->PREVIOUS optical flow -> R16G16_SInt Unity RenderTexture
    ///
    /// It does not interpret the vectors. StereoRender's existing full-resolution
    /// depth/matrix scatter remains the authority for head/camera/parallax motion;
    /// V90's compute shader camera-subtracts the optical flow and extrapolates only
    /// the residual apparent scene motion.
    /// </summary>
    internal static class NvOpticalFlowV90
    {
        private const string NativeDllFileName = "SonsVR_NvOF_V90.dll";
        private const string NativeDllImportName = "SonsVR_NvOF_V90.dll";

        private sealed class EyeState
        {
            internal RenderTexture Luma0;
            internal RenderTexture Luma1;
            internal RenderTexture Flow;
            internal int CurrentSlot = -1;
            internal int FlowFrame = -1;
            internal bool NativeConfigured;
            internal IntPtr NativeLuma0;
            internal IntPtr NativeLuma1;
            internal IntPtr NativeFlow;
            internal int Width;
            internal int Height;
            internal bool FirstExecuteLogged;
            internal bool PendingLogged;
        }

        private static readonly EyeState[] Eyes =
        {
            new EyeState(),
            new EyeState()
        };

        private static Material _lumaMaterial;
        private static Shader _lumaShader;
        private static bool _nativeLoadAttempted;
        private static bool _nativeAvailable;
        private static IntPtr _nativeModule;
        private static IntPtr _renderEventFunc;
        private static int _eventBase;
        private static string _nativePath = "";
        private static int _lastErrorLogsRemaining = 12;

        internal static bool NativeAvailable => _nativeAvailable;
        internal static string NativePath => _nativePath;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport(NativeDllImportName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NvOFV90_GetRenderEventFunc();

        [DllImport(NativeDllImportName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int NvOFV90_GetEventBase();

        [DllImport(NativeDllImportName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int NvOFV90_ConfigureEye(
            int eyeIndex,
            IntPtr input0,
            IntPtr input1,
            IntPtr output,
            int width,
            int height);

        [DllImport(NativeDllImportName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int NvOFV90_RequestEye(int eyeIndex, int currentSlot, int frame);

        [DllImport(NativeDllImportName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int NvOFV90_GetDxgiFormat(int eyeIndex, int which);

        [DllImport(NativeDllImportName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int NvOFV90_GetState(int eyeIndex);

        [DllImport(NativeDllImportName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int NvOFV90_GetCompletedFrame(int eyeIndex);

        [DllImport(NativeDllImportName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong NvOFV90_GetExecuteCount(int eyeIndex);

        [DllImport(NativeDllImportName, CallingConvention = CallingConvention.Cdecl)]
        private static extern double NvOFV90_GetLastApiCallMs(int eyeIndex);

        [DllImport(NativeDllImportName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int NvOFV90_GetLastError(
            int eyeIndex,
            StringBuilder buffer,
            int capacity);

        [DllImport(NativeDllImportName, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint NvOFV90_GetMaxSupportedApiVersion();

        [DllImport(NativeDllImportName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void NvOFV90_Reset();

        internal static void ResetForActivation()
        {
            // Reset only the LOGICAL per-eye history. Do not tear down NVOFA/D3D11
            // registrations from Unity's main thread: the session was created on the render
            // thread and can safely survive scene/activation churn while its Unity RTs remain
            // alive. The first new real render seeds a fresh pair before any execute.
            foreach (EyeState s in Eyes)
            {
                s.CurrentSlot = -1;
                s.FlowFrame = -1;
                s.FirstExecuteLogged = false;
                s.PendingLogged = false;
            }
        }

        internal static bool EnsureAvailable(Shader lumaShader)
        {
            if (lumaShader == null)
            {
                LogFailure("luminance shader is null");
                return false;
            }

            if (_lumaMaterial == null || _lumaShader != lumaShader)
            {
                if (_lumaMaterial != null)
                    UnityEngine.Object.Destroy(_lumaMaterial);

                _lumaShader = lumaShader;
                _lumaMaterial = new Material(lumaShader)
                {
                    name = "SonsVR_V90_NVOFA_Luma_Material"
                };
            }

            if (_nativeAvailable)
                return true;
            if (_nativeLoadAttempted)
                return false;

            _nativeLoadAttempted = true;

            try
            {
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                {
                    LogFailure(
                        "V90 NVOFA bridge currently targets D3D11, but Unity reports " +
                        SystemInfo.graphicsDeviceType + ". Optical flow stays OFF fail-soft.");
                    return false;
                }

                string gameRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string modsPath = Path.Combine(gameRoot, "Mods", NativeDllFileName);
                string rootPath = Path.Combine(gameRoot, NativeDllFileName);

                string chosen = File.Exists(modsPath) ? modsPath : rootPath;
                if (!File.Exists(chosen))
                {
                    LogFailure(
                        "native bridge not found. Expected either '" + modsPath +
                        "' or '" + rootPath + "'. Copy SonsVR_NvOF_V90.dll beside the mod DLL.");
                    return false;
                }

                _nativeModule = LoadLibraryW(chosen);
                if (_nativeModule == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    LogFailure("LoadLibrary failed for '" + chosen + "' Win32Error=" + error + ".");
                    return false;
                }

                _renderEventFunc = NvOFV90_GetRenderEventFunc();
                _eventBase = NvOFV90_GetEventBase();
                uint maxApi = NvOFV90_GetMaxSupportedApiVersion();

                if (_renderEventFunc == IntPtr.Zero || _eventBase <= 0 || maxApi == 0)
                {
                    LogFailure(
                        "native bridge loaded but NVOFA runtime handshake failed: " +
                        "renderEvent=0x" + _renderEventFunc.ToString("X") +
                        " eventBase=" + _eventBase + " maxApi=0x" + maxApi.ToString("X") + ".");
                    return false;
                }

                _nativePath = chosen;
                _nativeAvailable = true;

                int major = (int)((maxApi >> 4) & 0xF);
                int minor = (int)(maxApi & 0xF);
                MelonLogger.Warning(
                    "[StereoRender][AER V91][NVOFA] NATIVE READY. " +
                    "renderer=" + SystemInfo.graphicsDeviceType +
                    " GPU='" + SystemInfo.graphicsDeviceName + "' " +
                    "driver='" + SystemInfo.graphicsDeviceVersion + "' " +
                    "maxNVOFApi=" + major + "." + minor +
                    " eventBase=0x" + _eventBase.ToString("X") +
                    " bridge='" + chosen + "'.");

                return true;
            }
            catch (Exception e)
            {
                LogFailure("native initialization failed: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        internal static bool CaptureRealEye(
            int eye,
            RenderTexture sourceColor,
            Shader lumaShader,
            int frame,
            float resolutionScale)
        {
            if (eye < 0 || eye > 1 || sourceColor == null || !sourceColor.IsCreated())
                return false;
            if (!EnsureAvailable(lumaShader))
                return false;

            EyeState s = Eyes[eye];

            try
            {
                // V91: optical flow is a motion field, not the final image. V90d proved that
                // presentation-resolution OF is useful but far too expensive in this D3D11 path.
                // Half-linear resolution quarters the OF pixels while preserving normalized-UV
                // motion semantics in the scatter shader.
                float rs = Mathf.Clamp(resolutionScale, 0.25f, 1.0f);
                int flowWidth = Mathf.Max(64, Mathf.RoundToInt(sourceColor.width * rs));
                int flowHeight = Mathf.Max(64, Mathf.RoundToInt(sourceColor.height * rs));
                EnsureResources(s, eye, flowWidth, flowHeight);
                if (!s.NativeConfigured)
                    return false;

                int nextSlot = s.CurrentSlot < 0 ? 0 : 1 - s.CurrentSlot;
                RenderTexture currentLuma = nextSlot == 0 ? s.Luma0 : s.Luma1;

                // GPU-only conversion. The following plugin event is queued after this blit,
                // so NVOFA sees the just-finished real-eye luminance image without CPU copies.
                Graphics.Blit(sourceColor, currentLuma, _lumaMaterial, 0);

                bool hadPrevious = s.CurrentSlot >= 0;
                s.CurrentSlot = nextSlot;

                if (!hadPrevious)
                {
                    // First real render of this eye only seeds history.
                    return false;
                }

                if (NvOFV90_RequestEye(eye, nextSlot, frame) == 0)
                {
                    LogNativeError(eye, "request rejected");
                    return false;
                }

                GL.IssuePluginEvent(_renderEventFunc, _eventBase + eye);
                s.FlowFrame = frame;
                return true;
            }
            catch (Exception e)
            {
                LogFailure(
                    "capture/dispatch failed eye=" + (eye == 0 ? "L" : "R") +
                    " frame=" + frame + ": " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        internal static bool TryGetFlowForHistory(
            int eye,
            int historyFrame,
            out RenderTexture flow,
            out double lastApiCallMs,
            out ulong executeCount)
        {
            flow = null;
            lastApiCallMs = -1.0;
            executeCount = 0;

            if (!_nativeAvailable || eye < 0 || eye > 1 || historyFrame < 0)
                return false;

            EyeState s = Eyes[eye];
            if (!s.NativeConfigured || s.Flow == null || !s.Flow.IsCreated() || s.FlowFrame != historyFrame)
                return false;

            try
            {
                int completed = NvOFV90_GetCompletedFrame(eye);
                int state = NvOFV90_GetState(eye);
                executeCount = NvOFV90_GetExecuteCount(eye);
                lastApiCallMs = NvOFV90_GetLastApiCallMs(eye);

                if (state < 0)
                {
                    LogNativeError(eye, "native state entered ERROR");
                    return false;
                }

                if (completed != historyFrame)
                {
                    if (!s.PendingLogged)
                    {
                        s.PendingLogged = true;
                        MelonLogger.Msg(
                            "[StereoRender][AER V91][NVOFA] flow not completed yet for eye=" +
                            (eye == 0 ? "L" : "R") + " requestedFrame=" + historyFrame +
                            " nativeCompleted=" + completed + " state=" + state +
                            ". Staying depth-only fail-soft.");
                    }
                    return false;
                }

                s.PendingLogged = false;
                flow = s.Flow;

                if (!s.FirstExecuteLogged)
                {
                    s.FirstExecuteLogged = true;
                    int unityIn0 = NvOFV90_GetDxgiFormat(eye, 0);
                    int unityIn1 = NvOFV90_GetDxgiFormat(eye, 1);
                    int unityOut = NvOFV90_GetDxgiFormat(eye, 2);
                    int proxyIn0 = NvOFV90_GetDxgiFormat(eye, 3);
                    int proxyIn1 = NvOFV90_GetDxgiFormat(eye, 4);
                    int proxyOut = NvOFV90_GetDxgiFormat(eye, 5);

                    MelonLogger.Warning(
                        "[StereoRender][AER V91][NVOFA] FLOW READY eye=" +
                        (eye == 0 ? "L" : "R") + " frame=" + historyFrame +
                        " size=" + flow.width + "x" + flow.height +
                        " fmt=" + flow.graphicsFormat +
                        " executeCount=" + executeCount +
                        " nvOFExecuteCpuCall=" + lastApiCallMs.ToString("F3") + "ms; " +
                        "DXGI Unity[in0/in1/out]=" +
                        DxgiLabel(unityIn0) + "/" + DxgiLabel(unityIn1) + "/" + DxgiLabel(unityOut) +
                        " -> typedProxy=" +
                        DxgiLabel(proxyIn0) + "/" + DxgiLabel(proxyIn1) + "/" + DxgiLabel(proxyOut) + ". " +
                        "Direction=CURRENT->PREVIOUS, S10.5 pixels, 1x1 grid.");
                }

                return true;
            }
            catch (Exception e)
            {
                LogFailure("status query failed: " + e.Message);
                return false;
            }
        }

        internal static string GetCompactStatus(int eye)
        {
            if (!_nativeAvailable || eye < 0 || eye > 1)
                return "NVOFA=OFF";
            try
            {
                return "NVOFA[state=" + NvOFV90_GetState(eye) +
                       " done=" + NvOFV90_GetCompletedFrame(eye) +
                       " n=" + NvOFV90_GetExecuteCount(eye) +
                       " apiMs=" + NvOFV90_GetLastApiCallMs(eye).ToString("F3") + "]";
            }
            catch
            {
                return "NVOFA=status-failed";
            }
        }

        private static void EnsureResources(EyeState s, int eye, int width, int height)
        {
            bool resourcesGood =
                s.Width == width && s.Height == height &&
                IsRTGood(s.Luma0, width, height, GraphicsFormat.R8_UNorm) &&
                IsRTGood(s.Luma1, width, height, GraphicsFormat.R8_UNorm) &&
                IsRTGood(s.Flow, width, height, GraphicsFormat.R16G16_SInt);

            if (!resourcesGood)
            {
                ReleaseRT(ref s.Luma0);
                ReleaseRT(ref s.Luma1);
                ReleaseRT(ref s.Flow);

                s.Luma0 = CreateRT(width, height, GraphicsFormat.R8_UNorm,
                    "SonsVR_V90_OF_Luma" + (eye == 0 ? "L" : "R") + "_0", false);
                s.Luma1 = CreateRT(width, height, GraphicsFormat.R8_UNorm,
                    "SonsVR_V90_OF_Luma" + (eye == 0 ? "L" : "R") + "_1", false);
                s.Flow = CreateRT(width, height, GraphicsFormat.R16G16_SInt,
                    "SonsVR_V90_OF_Flow" + (eye == 0 ? "L" : "R"), true);

                s.Width = width;
                s.Height = height;
                s.CurrentSlot = -1;
                s.FlowFrame = -1;
                s.NativeConfigured = false;
                s.NativeLuma0 = IntPtr.Zero;
                s.NativeLuma1 = IntPtr.Zero;
                s.NativeFlow = IntPtr.Zero;
                s.FirstExecuteLogged = false;
                s.PendingLogged = false;
            }

            IntPtr p0 = s.Luma0.GetNativeTexturePtr();
            IntPtr p1 = s.Luma1.GetNativeTexturePtr();
            IntPtr pf = s.Flow.GetNativeTexturePtr();
            if (p0 == IntPtr.Zero || p1 == IntPtr.Zero || pf == IntPtr.Zero)
                throw new Exception("Unity returned a null D3D11 native texture pointer.");

            if (!s.NativeConfigured || p0 != s.NativeLuma0 || p1 != s.NativeLuma1 || pf != s.NativeFlow)
            {
                int ok = NvOFV90_ConfigureEye(eye, p0, p1, pf, width, height);
                if (ok == 0)
                {
                    s.NativeConfigured = false;
                    LogNativeError(eye, "resource configuration rejected");
                    return;
                }

                s.NativeLuma0 = p0;
                s.NativeLuma1 = p1;
                s.NativeFlow = pf;
                s.NativeConfigured = true;

                int unityIn0 = NvOFV90_GetDxgiFormat(eye, 0);
                int unityIn1 = NvOFV90_GetDxgiFormat(eye, 1);
                int unityOut = NvOFV90_GetDxgiFormat(eye, 2);

                MelonLogger.Msg(
                    "[StereoRender][AER V91][NVOFA] resources configured eye=" +
                    (eye == 0 ? "L" : "R") + " size=" + width + "x" + height +
                    " UnityFormats=" + s.Luma0.graphicsFormat + "/" + s.Flow.graphicsFormat +
                    " nativeDXGI=" + DxgiLabel(unityIn0) + "/" + DxgiLabel(unityIn1) +
                    "/" + DxgiLabel(unityOut) +
                    " (TYPELESS is expected and will be GPU-copied through typed NVOFA proxies)" +
                    " nativePtrs=[0x" + p0.ToString("X") + ",0x" + p1.ToString("X") +
                    ",0x" + pf.ToString("X") + "].");
            }
        }

        private static string DxgiLabel(int value)
        {
            switch (value)
            {
                case 33: return "R16G16_TYPELESS(33)";
                case 38: return "R16G16_SINT(38)";
                case 60: return "R8_TYPELESS(60)";
                case 61: return "R8_UNORM(61)";
                case 0: return "UNKNOWN(0)";
                default: return "DXGI(" + value + ")";
            }
        }

        private static bool IsRTGood(RenderTexture rt, int width, int height, GraphicsFormat format)
        {
            return rt != null && rt.width == width && rt.height == height &&
                   rt.graphicsFormat == format && rt.IsCreated();
        }

        private static RenderTexture CreateRT(
            int width,
            int height,
            GraphicsFormat format,
            string name,
            bool randomWrite)
        {
            var desc = new RenderTextureDescriptor(width, height)
            {
                graphicsFormat = format,
                depthBufferBits = 0,
                msaaSamples = 1,
                volumeDepth = 1,
                dimension = TextureDimension.Tex2D,
                enableRandomWrite = randomWrite,
                useMipMap = false,
                autoGenerateMips = false
            };

            var rt = new RenderTexture(desc)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            if (!rt.IsCreated())
                throw new Exception("failed to create " + name + " format=" + format);
            return rt;
        }

        private static void ReleaseRT(ref RenderTexture rt)
        {
            if (rt == null)
                return;
            try { rt.Release(); } catch { }
            UnityEngine.Object.Destroy(rt);
            rt = null;
        }

        private static void LogNativeError(int eye, string context)
        {
            try
            {
                var sb = new StringBuilder(768);
                int code = NvOFV90_GetLastError(eye, sb, sb.Capacity);
                LogFailure(
                    context + " eye=" + (eye == 0 ? "L" : "R") +
                    " nativeCode=" + code + " nativeMessage='" + sb + "'.");
            }
            catch (Exception e)
            {
                LogFailure(context + " (native error query itself failed: " + e.Message + ")");
            }
        }

        private static void LogFailure(string message)
        {
            if (_lastErrorLogsRemaining <= 0)
                return;
            _lastErrorLogsRemaining--;
            MelonLogger.Warning(
                "[StereoRender][AER V91][NVOFA] " + message +
                " Logs remaining=" + _lastErrorLogsRemaining + ".");
        }
    }
}
