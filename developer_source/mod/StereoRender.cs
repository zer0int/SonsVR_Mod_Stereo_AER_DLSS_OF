using MelonLoader;
using System;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.XR;
using Valve.VR;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using static SonsVRMod.SonsVRMod;
using Il2CppEndnight.Utilities;
using Mathf = UnityEngine.Mathf;

namespace SonsVRMod
{
    /// <summary>
    /// Manages stereo rendering for VR by creating left/right eye cameras,
    /// rendering to separate RenderTextures, and submitting frames to OpenVR Compositor.
    /// This class replaces Unity's built-in VR pipeline with a custom one compatible with modded scenes.
    /// </summary>
    internal class StereoRender : MonoBehaviour
    {
        public StereoRender(IntPtr value) : base(value) { }

        /// <summary>
        /// Singleton instance for global access.
        /// </summary>
        public static StereoRender Instance;

        /// <summary>
        /// Transform representing the HMD (Head-Mounted Display).
        /// This follows the real-world HMD orientation.
        /// </summary>
        public Transform Head;

        /// <summary>
        /// Reference to the original game camera (usually FPS view).
        /// Not directly used in rendering but may be needed for position synchronization.
        /// </summary>
        public Camera HeadCam;

        /// <summary>
        /// Dedicated cameras for left and right eyes.
        /// These render the scene from slightly offset viewpoints to create stereoscopy.
        /// </summary>
        public Camera LeftCam, RightCam;

        public static bool SkipHdrpCameraDataEntirely = false; // Testing TryEnableCustomPass now - re-enabling HDAdditionalCameraData (with the new CustomPass fix alongside AA/DLSS) to see whether the outline/water issue can be fixed while keeping the anti-aliasing and DLSS benefits. If this doesn't resolve it, revert to true as the safe fallback.

        /// <summary>
        /// Render targets for left and right eyes.
        /// Submitted to OpenVR compositor for final display in the headset.
        /// </summary>
        public RenderTexture LeftRT, RightRT;
        private volatile bool _resolutionUpdateInProgress = false;

        // PORTED from another dev's fork of this mod, per user's own request - a small,
        // capped-resolution copy of an eye texture for the desktop mirror, instead of the
        // native camera doing a full, separate, wasted render of its own. This is what makes it
        // SAFE to later neutralize the native camera (already built, currently off, see
        // VRPlayer.NeutralizeNativeCameraEnabled) without breaking this project's mirror the way
        // that attempt did before - this mirror no longer depends on the native camera's own
        // output at all, once enabled.
        public static bool DesktopMirrorBlitModeEnabled = true; // stage 1 test - native-camera-neutering stays off until this is confirmed working on its own
        public static bool MirrorRightEye = false; // which eye feeds the desktop - purely a diagnostic switch, not a visual preference
        public static int MirrorMaxWidth = 1280; // capped, same reasoning as the other dev's own choice - the desktop has no use for full eye resolution, and copying less costs less
        private RenderTexture _mirrorViewRT;

        /// <summary>
        /// Takes a copy of an eye texture for the desktop mirror to draw from, downscaled to a
        /// capped resolution. Called from StereoRenderPass.Execute() immediately AFTER
        /// OpenVR.Compositor.Submit() - critical ordering, confirmed necessary by the other dev's
        /// own hard-won lesson: capturing before/during submit (e.g. from OnGUI directly, which
        /// runs earlier in the frame) binds the LIVE eye texture as a source and invalidates its
        /// contents before the compositor ever reads it, causing a black eye in the headset. This
        /// capture happens strictly after, so the compositor always receives the eye target
        /// genuinely untouched - meaning the desktop mirror is one frame behind, which nobody
        /// watching a monitor will notice.
        /// </summary>
        public void CaptureMirrorView()
        {
            if (!DesktopMirrorBlitModeEnabled) return;

            RenderTexture source = MirrorRightEye ? RightRT : LeftRT;
            if (source == null || !source.IsCreated()) return;

            if (_mirrorViewRT == null)
            {
                int width = Mathf.Clamp(Screen.width, 320, MirrorMaxWidth);
                int height = Mathf.Max(180, Mathf.RoundToInt(width * (float)source.height / source.width));

                _mirrorViewRT = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    name = "SonsVR_MirrorViewRT",
                    filterMode = FilterMode.Bilinear
                };
                _mirrorViewRT.Create();
            }

            Graphics.Blit(source, _mirrorViewRT);

            // Critical, per the other dev's own documented lesson: Blit leaves its destination
            // bound as the active render target. The eye cameras are the first thing rendered
            // next frame - anything left dangling here would land on them, corrupting the next
            // frame's actual VR output, not just the mirror.
            RenderTexture.active = null;
        }

        /// <summary>
        /// Draws the captured mirror copy to the desktop window. Never draws the live eye
        /// texture directly - see CaptureMirrorView's own comment for why that specifically
        /// caused a black eye in the headset for the other dev.
        /// </summary>
        public void OnGUI()
        {
            if (!DesktopMirrorBlitModeEnabled) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            // V106: the desktop IMGUI mirror itself executes after the compositor submit path and
            // therefore belongs in the same suspect zone as the giant postSubmitGpu spikes. Time
            // its CPU side separately; try/finally guarantees early debug-board returns still end
            // the marker.
            PerformanceForensicsV106.MarkGuiMirrorBegin();
            try
            {
                // Per user's own request: once the native camera's output is redirected away from
                // the screen (see VRPlayer.RedirectNativeCameraFromScreen), nothing ever draws over
                // the margin outside our own ScaleToFit-sized mirror rect again - whatever frame was
                // on screen at the exact moment the redirect took effect just stays there, frozen,
                // since nothing clears or overwrites that region anymore. A full-screen black backdrop
                // drawn first hides that stale frame - purely cosmetic, doesn't affect the actual
                // capture/blit cost at all.
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.blackTexture, ScaleMode.StretchToFill, false);

                if (_mirrorViewRT == null || !_mirrorViewRT.IsCreated()) return;

                // V49 desktop board: fresh real source eye, same-time GPU-synthesized opposite eye,
                // synthetic confidence, and the old raw retained opposite eye for comparison. Headset
                // submission is selected separately inside StereoRenderPass.
                if (AerDepthStereoWarpV48.TryDrawDesktopDebug())
                    return;

                // Dormant fallback retained from V47 for source archaeology; normally disabled in V49.
                if (AerDepthShaderDebugV47.TryDrawDesktopDebug(_mirrorViewRT))
                    return;

                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _mirrorViewRT, ScaleMode.ScaleToFit, false);
            }
            finally
            {
                PerformanceForensicsV106.MarkGuiMirrorEnd();
            }
        }

        /// <summary>
        /// Interpupillary distance (IPD) in meters. Default ~64mm.
        /// Controls the horizontal separation between left and right eye views.
        /// </summary>
        public float separation = 0.064f;

        /// <summary>
        /// Near clipping plane distance (in meters).
        /// </summary>
        private float clipStart = 0.015f;

        /// <summary>
        /// Far clipping plane distance (in meters).
        /// </summary>
        public static float GameplayFarClipMeters = 24000f;
        private float clipEnd = GameplayFarClipMeters;

        /// <summary>
        /// Culling mask used by the original game camera.
        /// Applied to both eye cameras to ensure consistent visibility of game objects.
        /// </summary>
        public static int defaultCullingMask = -1; // -1 = everything

        /// <summary>
        /// Current render resolution (width/height) based on SteamVR’s recommended eye texture size.
        /// </summary>
        private int currentWidth, currentHeight;
        private int lastKnownFullWidth, lastKnownFullHeight; // unscaled SteamVR-recommended size, tracked separately from the (possibly scaled-down) actual render size, so resolution-change detection isn't confused by our own intentional RenderResolutionScale reduction

        /// <summary>
        /// Custom render pass that submits left/right textures to the OpenVR compositor.
        /// </summary>
        public StereoRenderPass stereoRenderPass;

        // Pose arrays for OpenVR: one for rendering, one for game logic (unused here but required by API)
        private readonly TrackedDevicePose_t[] renderPoseArray = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private readonly TrackedDevicePose_t[] gamePoseArray = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

        // ---------------------------------------------------------------------
        // ZER0INT V33 - ALTERNATE EYE RENDERING (AER)
        // ---------------------------------------------------------------------
        // The original fork leaves both ordinary Unity/HDRP cameras enabled every frame, so the
        // entire scene is rendered twice before the two RenderTextures are submitted to OpenVR.
        // V33 keeps both eye textures persistent but only lets ONE eye camera render per Unity
        // frame after a short two-eye warmup. The other eye reuses its previous texture.
        //
        // This is intentionally a self-contained renderer experiment, not a fake "FPS trick":
        // game simulation still advances every frame; only one expensive HDRP eye render is skipped.
        // V34 also tried OpenVR Submit_TextureWithPose with a distinct stored pose per eye. SteamVR
        // accepted the calls but visibly reprojected the scene into a bounded/warped region with the
        // compositor environment showing around it. This is not a "SteamVR Home was left on" state:
        // Valve's own OpenVR issue #1253 documents that SteamVR does not reliably honor distinct
        // per-eye VRTextureWithPose_t poses for alternate-eye rendering (the later Submit can win for
        // both eyes). V35 therefore returned to Submit_Default and proved the important architectural
        // point: the mod CAN alternate one ordinary HDRP eye camera per Unity frame, keep both eye
        // textures persistent, survive gameplay/menu camera changes, and submit a normal full-FOV scene.
        // The remaining problem was exactly what naive AER predicts: one eye is a frame older than the
        // other, so head rotation creates an oscillating binocular disparity / fusion conflict.
        //
        // V36 fixes that WITHOUT asking SteamVR to remember distinct per-eye poses. Before submission,
        // the one-frame-stale eye is rotationally reprojected from the HMD rotation that produced its
        // texture into the HMD rotation that produced the fresh eye. Both textures therefore describe
        // (approximately) the same head orientation and can go through the already-proven Submit_Default
        // path. This is intentionally ROTATION-ONLY reprojection: the fork itself ignores room-scale HMD
        // translation, and without a depth buffer no 2D warp can correctly synthesize translation/parallax
        // anyway. The small IPD-induced eye-center shift during rotation can still leave residual error on
        // very near geometry; this build is meant to remove the dominant whole-world rotational tremor first.
        // V43 boots on the already-proven V40C/full-stereo path. AER is armed only after
        // SonsMain + MainCameraFP + LocalPlayer are confirmed and the gameplay camera's HDRP
        // data has been copied successfully. The actual flag flip happens AFTER that frame's
        // WaitForEndOfFrame so the activation frame itself remains ordinary full stereo; the
        // following frame enters the untouched V38B 0f/1f scheduler.
        public static bool AlternateEyeRenderingEnabled = false;
        // V112: persisted USER preference for gameplay. AlternateEyeRenderingEnabled is the
        // actual live backend state; this preference is applied only after the proven gameplay
        // gate so loading/title never accidentally enter AER.
        public static bool UserGameplayAERV112 = true;
        public static bool AerPoseAwareSubmissionEnabled = false;
        public static bool AerRotationalReprojectionEnabled = false;
        public static bool AerDeferredGameplayActivationEnabled = true;
        public static bool AerGameplayReadyForActivation = false;

        // V44 gameplay-only passive spatial telemetry. Collection begins whenever deferred V43
        // AER is actually active and keeps running continuously. The FIRST real crafting-tarp
        // open is a one-way validity marker: pre-marker samples stay in the log for context, but
        // the telemetry accumulator is reset at that marker so all later windows are guaranteed
        // to come from a run that demonstrably reached ordinary player gameplay. This adds NO
        // OpenVR calls, NO camera/resource mutations, and NO changes to scheduling/submission.
        public static bool AerGameplaySpatialTelemetryV44Enabled = false; // V84 clean performance anchor
        public static bool AerGameplaySpatialTelemetryTarpMarkerSeen = false;
        public static float AerGameplaySpatialTelemetryIntervalSeconds = 2.0f;

        // V46 HDRP depth-access feasibility probe. This does NOT modify rendering or submission.
        // After the already-rendered fresh AER eye reaches EOF, it inspects HDRP's globally-bound
        // camera depth texture and occasionally asks the GPU for a tiny 64x64 RFloat readback.
        // The readback is asynchronous (never WaitForCompletion) and is used only to prove that
        // same-frame depth is accessible and numerically sensible before cross-eye reprojection.
        public static bool AerDepthAccessProbeV46Enabled = false; // V84 clean performance anchor
        public static float AerDepthAccessProbeV46IntervalSeconds = 2.0f;
        public static int AerDepthAccessProbeV46PatchSize = 64;

        // V47's desktop-only board solved the HDRP depth layout: _CameraDepthTexture is the
        // packed depth-pyramid atlas, and HDRP's full-resolution mip 0 lives at pixel offset
        // (0,0). Keep the old board code dormant as a forensic fallback; V49 supersedes it.
        public static bool AerDepthShaderDebugV47Enabled = false;
        public static float AerDepthShaderDebugV47UpdateIntervalSeconds = 0.25f;
        public static int AerDepthShaderDebugV47PanelWidth = 640;
        public static float AerDepthShaderDebugV47MaxMeters = 50.0f;

        // V51 keeps the proven V50 camera-local depth latch + 512x512 forward warp, but removes
        // the remaining SOURCE-EYE fallback. V50's screenshots/logs proved the black packed-atlas
        // corruption was gone, while the headset still showed a flat jittery "holo sheet" and the
        // desktop synthetic eye visibly contained two copies of characters/HUD. That is exactly
        // what a source-eye fallback does in target-eye holes: it inserts an image with the wrong
        // binocular viewpoint. V51 therefore uses every valid synthetic pixel regardless of near/
        // far distance, fills tiny holes spatially, and uses the OLD TARGET eye only for true holes.
        // The old target fallback is rotation-corrected with the existing V36 homography when poses
        // are valid. No source-eye color participates in the final target-eye composite.
        public static bool AerDepthStereoWarpV48Enabled = true;
        public static bool AerDepthStereoWarpV48SubmitSyntheticEye = true;
        public static int AerDepthStereoWarpV48GridSize = 768;
        // V87 FAR-FIELD FIX: the old 300 m cut-off turned valid distant geometry
        // into unchanged-UV same-eye history fallback. Accept valid scene depth out
        // to the eye camera far plane (minus a tiny safety margin). Raw zero/sky
        // depth remains invalid/infinite. This legacy scalar is only a fallback.
        public static float AerDepthStereoWarpV48MaxMeters = 24000.0f;
        public static bool AerDepthStereoWarpV87UseCameraFarClip = true;
        public static float AerDepthStereoWarpV87FarClipSafetyMeters = 1.0f;
        public static float AerDepthStereoWarpV48DiscontinuityMeters = 0.20f;
        public static float AerDepthStereoWarpV48DiscontinuityRelative = 0.04f;
        public static float AerDepthStereoWarpV49NearMonoMeters = 0.45f;
        public static float AerDepthStereoWarpV49NearFullStereoMeters = 0.80f;
        public static float AerDepthStereoWarpV49FarMonoStartMeters = 40.0f;
        public static float AerDepthStereoWarpV49FarMonoEndMeters = 120.0f;
        public static float AerDepthStereoWarpV49QuarantineConfidenceFloor = 0.05f;
        public static float AerDepthStereoWarpV49FillConfidenceDecay = 0.82f;
        public static float AerDepthStereoWarpV49FallbackBlendLowConfidence = 0.10f;
        public static float AerDepthStereoWarpV49FallbackBlendFullConfidence = 0.85f;

        // V50 removes the remaining unsafe dependency on Shader.GetGlobalTexture at EOF.
        // RenderPipelineManager.endCameraRendering identifies the eye camera that actually just
        // rendered, copies THAT camera's mip-0 depth immediately into a dedicated eye-local
        // RFloat RT, and snapshots the source projection / cameraToWorld / Z-buffer parameters.
        // EOF synthesis accepts a latch only when camera, eye, dimensions, and Time.frameCount
        // all match the completed fresh eye. The global HDRP depth binding is never consumed by
        // the headset warp after the callback copy.
        public static bool AerDepthStereoWarpV50CameraLocalDepthLatchEnabled = true;

        // V82: finally stop sourcing the headset warp depth from the global packed
        // _CameraDepthTexture mip chain. Prefer the exact eye-local RFloat copy made
        // inside HDRP's AfterOpaqueDepthAndNormal CustomPass from
        // CustomPassContext.cameraDepthBuffer.
        //
        // V81f proved that, in the same fixed scene:
        //   cameraDepthBuffer: ~99.99-100% valid
        //   global mip-chain:  ~34.8% valid, lower ~65% one connected zero-depth slab
        //
        // The old global V50 capture stays alive ONLY as fallback + V80 forensic A/B.
        public static bool AerDepthStereoWarpV82UseCustomPassDepth = true; // V85: camera-local depth drives SAME-EYE temporal history

        // V83 PRIMARY EXPERIMENT:
        // Render one real eye this frame, and reconstruct the OTHER eye from THAT OTHER EYE'S
        // own previous real color + own previous camera-local depth. This is temporal same-eye
        // reprojection, NOT cross-eye manifestation.
        //
        // Example on a fresh LEFT frame:
        //   real LEFT(t) + [real RIGHT(t-1), RIGHT depth(t-1), RIGHT pose(t-1)]
        //      -> desired RIGHT pose(t) -> predicted RIGHT(t)
        //
        // This removes the permanent binocular-disocclusion impossibility of L(t)->R(t).
        public static bool AerTemporalSameEyeV83Enabled = true; // V85+: same-eye temporal is the primary headset experiment
        public static int AerTemporalSameEyeV83MaxHistoryAgeFrames = 2;

        // V86: V48-V85 passed already perspective-divided source UVs as ordinary
        // perspective interpolants across target-space triangles. That is not
        // projectively correct when triangle vertices have different clip W/depth.
        // The predicted frame can therefore acquire a tiny depth-dependent wobble
        // even when the temporal camera delta is very small.
        //
        // V86 instead interpolates HOMOGENEOUS SOURCE clip coordinates and divides
        // by source W only in the fragment shader. One bool keeps the V85 math
        // available for a compile-time A/B.
        public static bool AerTemporalProjectiveSourceCoordinatesV86Enabled = true;

        // V90 keeps V88's proven full-resolution per-source-pixel depth scatter and uses
        // NVIDIA Optical Flow Accelerator output as the ONE new variable. NVOFA itself runs
        // throughout both phases so the visual A/B is not confounded by turning hardware work
        // on at the switch:
        //   first 30 s = depth/camera reprojection only (NVOFA generated but ignored);
        //   after 30 s = same reprojection + camera-subtracted optical-flow residual.
        //
        // NVOFA is deliberately run CURRENT_REAL -> PREVIOUS_REAL. That gives a backward
        // vector anchored at the current historical pixel. We subtract the camera-only backward
        // displacement reconstructed from depth/matrices, negate the remaining scene residual,
        // and scale it by measured dtPredict / dtFlow rather than assuming exactly 0.5.
        public static bool AerTemporalUseRenderBoundaryPoseV88 = true;
        public static bool AerTemporalPerPixelScatterV88Enabled = true; // V90 substrate; grid is fail-soft only
        public static bool AerTemporalObjectMotionV89Enabled = false;   // V90 isolates NVOFA; V89 remains compiled
        public static float AerTemporalObjectMotionABV89SwitchSeconds = 30.0f;
        public static float AerTemporalObjectMotionScaleV89 = 1.0f;
        public static float AerTemporalObjectMotionMaxPixelsV89 = 96.0f;
        public static bool AerTemporalOpticalFlowV90Enabled = true;
        public static float AerTemporalOpticalFlowABV90SwitchSeconds = 30.0f;
        public static float AerTemporalOpticalFlowScaleV90 = 1.0f;
        public static float AerTemporalOpticalFlowMaxResidualPixelsV90 = 96.0f;
        public static float AerTemporalOpticalFlowMaxTimeScaleV90 = 1.0f;

        // V91: real DLSS + lower-resolution NVOFA. The flow field is sampled in normalized UV
        // space, so half-linear OF resolution is legal and reduces NVOFA pixels by 4x.
        public static bool AerV91RealDlssEnabled = true;

        // V93: isolate the remaining field-dependent top/bottom wobble at the OpenVR
        // compositor boundary.  V34's old pose experiment submitted DISTINCT historical
        // poses for left/right, which SteamVR does not reliably support under AER.  That
        // is NOT this experiment.  Our modern same-eye temporal renderer already aligns
        // BOTH submitted textures to the SAME fresh-eye render time/pose.  Therefore V93
        // can safely ask a much narrower question:
        //
        //   A  0..30 s : ordinary Submit_Default (current proven path)
        //   B 30..60 s : Submit_TextureWithPose for BOTH eyes using the SAME fresh render pose
        //   A2 >60 s   : ordinary Submit_Default again
        //
        // NVOFA remains generated AND applied throughout all three phases.  DLSS, depth,
        // synthesis, workload and resolution are unchanged at the switches.
        public static bool AerV93SameFreshPoseSubmitABEnabled = false; // V93 null; keep code dormant for archaeology.
        public static float AerV93SameFreshPoseSubmitStartSeconds = 30.0f;
        public static float AerV93SameFreshPoseSubmitEndSeconds = 60.0f;

        // V94: the remaining top/bottom field-dependent defect predates NVOFA. Measure the
        // underlying pre-OF jitter while testing whether historical depth is being unprojected
        // with matrices sampled after/different from the exact HDRP raster.
        //
        //   A  0..30 s : existing Camera-derived source matrices
        //   B 30..60 s : exact HDCamera source matrices captured with cameraDepthBuffer
        //   A2 >60 s   : Camera-derived source matrices again
        //
        // NVOFA still EXECUTES at half resolution in every phase (constant workload/history),
        // but its residual is deliberately ignored so it cannot smooth the defect under test.
        public static bool AerV94HdrpSourceMatrixABEnabled = false; // V94b null; keep code dormant for archaeology.
        public static float AerV94HdrpSourceMatrixStartSeconds = 30.0f;
        public static float AerV94HdrpSourceMatrixEndSeconds = 60.0f;
        public static bool AerV94ExposePreOpticalFlowJitter = true;

        // V98: DA TRU DLSS revealed that V88's "per-pixel" scatter stopped being per
        // PRESENTATION pixel. The camera-local depth raster is 1572x1660 while the
        // historical post-DLSS color / synthetic target are 3141x3317. V97 therefore
        // dispatched only ~25% as many source samples as final pixels and HoleFill created
        // most of the final synthetic raster.
        //
        // Clean A/B/A, with all other rendering state fixed:
        //   A  0..30 s : V97 INTERNAL-DEPTH-GRID scatter (1572x1660 source threads)
        //   B 30..60 s : PRESENTATION-GRID scatter (3141x3317 source threads); each final
        //                historical-color pixel gets the nearest paired internal-depth sample
        //   A2 >60 s   : return to INTERNAL-DEPTH-GRID scatter
        //
        // NVOFA continues to execute at identical workload/history but its residual is ignored
        // throughout, so this exposes the original pre-OF jitter rather than jelly-smoothing it.
        public static bool AerV98PresentationGridScatterABEnabled = false; // V99 freezes display path; V98 remains callable in offline truth variants.
        public static float AerV98PresentationGridScatterStartSeconds = 30.0f;
        public static float AerV98PresentationGridScatterEndSeconds = 60.0f;
        public static bool AerV98ExposePreOpticalFlowJitter = true;

        // V99: one-launch forensic truth campaign. Normal displayed AER remains the V97-style
        // internal-depth-grid, pre-NVOFA path. Every N frames, ONE diagnostic frame renders BOTH
        // eyes genuinely. Immediately before that frame we snapshot each physical eye's stale
        // same-eye color/depth/motion/NVOFA history. After both real eyes finish, the snapshots are
        // reconstructed offline several ways and compared against same-frame real ground truth.
        // The truth frame itself submits the two genuine real eyes; diagnostic variants never go
        // to OpenVR. Tiny 16x12 tile atlases are asynchronously read back later.
        public static bool AerV99ForensicsEnabled = false; // V139: armed for one bank ONLY after 180s calibration setup.
        public static int AerV99TruthInitialDelayFrames = 90;
        public static int AerV99TruthIntervalFrames = 90;
        public static int AerV99TruthSampleCount = 6;
        public static int AerV99TileColumns = 16;
        public static int AerV99TileRows = 12;
        public static int AerV99BlockMatchSearchRadiusPixels = 8;
        public static int AerV99BlockMatchPatchRadiusPixels = 16;
        public static int AerV99BlockMatchPatchStridePixels = 4;
        public static bool AerV99CaptureHdrpMotionVectors = true;
        public static bool AerV99ExposePreOpticalFlowJitter = true;

        // V100: V99 found the ancient field-dependent defect's strongest concrete mechanism:
        // after the depth warp + radius-1/radius-2 fills, roughly half of the PHYSICAL HMD TOP
        // can still be a true hole because the source camera depth is invalid/missing there.
        // V51's final fallback then pastes OLD SAME-EYE color at the unchanged UV, so that region
        // does not receive even rotational head-motion compensation.  This predates DLSS/NVOFA.
        //
        // Clean A/B/A on ONLY that final true-hole fallback:
        //   A   0..15 s : current stale SAME-UV fallback
        //   B  15..30 s : rotation-only / infinite-depth inverse gather into stale same-eye color
        //   A2 >30 s    : return to stale SAME-UV fallback
        //
        // Valid-depth pixels, compute scatter, two hole fills, DLSS, NVOFA workload, HUD, and
        // matrices are otherwise unchanged.  NVOFA residual remains suppressed so raw jitter is
        // visible.  V99 truth telemetry stays active, with its HDRP motion registration fixed.
        public static bool AerV100RotationOnlyHoleFallbackABEnabled = false; // V101 promotes rotation-only true-hole fallback to permanent baseline
        public static float AerV100RotationOnlyHoleFallbackStartSeconds = 15.0f;
        public static float AerV100RotationOnlyHoleFallbackEndSeconds = 30.0f;

        // V101: directly test the user's sky/infinity hypothesis. V100 only rotation-corrected
        // TRUE holes after finite-depth scatter + two fill passes. That leaves no-depth sky pixels
        // absent during scatter and lets spatial fill leak finite neighbors into some of them.
        // V101 Phase B additionally forward-scatters SOURCE pixels with rawDepth==0 as infinitely
        // distant rotation-only background BEFORE fill. Finite geometry always wins the depth key.
        // A/A2 keep V100's rotation-only final-hole fallback permanently, so only this pre-fill
        // no-depth treatment changes at the live switch. Offline truth diagnostics simultaneously
        // sweep finite-geometry translation scale and measure depth classes including NO_DEPTH.
        public static bool AerV101SkyInfinityScatterABEnabled = false; // V102: V101 sky pre-scatter increased truth error; retire it from live display.
        public static float AerV101SkyInfinityScatterStartSeconds = 15.0f;
        public static float AerV101SkyInfinityScatterEndSeconds = 30.0f;

        // V102: OCCLUSION / DEPTH-ASSOCIATION FORENSICS.  V101 showed that literal
        // no-depth/sky pixels are not one coherent layer: forcing them all through an
        // infinite-depth pre-scatter massively increases coverage but does not improve truth
        // error.  The user's 50 m tarp test then made the defect MOVE to one occluded
        // rock/tree slice while isolated silhouettes became stable.  V102 therefore keeps
        // the proven V100 rotation-only TRUE-hole fallback as the clean live baseline and
        // measures visibility-layer changes rather than modifying the displayed image.
        //
        // Two truth banks are armed automatically in one launch:
        //   GAMEPLAY_24000M : ordinary initial six samples
        //   TARP_50M        : first far-clip transition <=100 m rearms six samples
        //
        // Offline candidates factor the two most suspicious implementation choices:
        //   source grid: PRESENTATION (post-DLSS color pixels share low-res depth) vs DEPTH grid
        //   spatial fill: radius1+radius2 vs radius1 only vs NO spatial fill
        // Every candidate retains V100's rotation-only final-hole fallback.
        public static bool AerV102OcclusionForensicsEnabled = false; // V103: V102 evidence collected; disable heavy two-bank truth capture.
        public static int AerV102TarpTruthSampleCount = 6;
        public static int AerV102TarpTruthInitialDelayFrames = 30;
        public static int AerV102TarpTruthIntervalFrames = 60;

        // V102b: the tester is physically inside the HMD and cannot inspect MelonLoader logs
        // mid-run. Expose milestone text to VRPlayer so it can show a head-locked prompt ONLY
        // between truth banks / after the final bank. The prompt is hidden before any truth
        // capture resumes, so it cannot contaminate the image/depth measurements.
        public static string AerV102HmdCueText = "";
        public static int AerV102HmdCueRevision = 0;

        internal static void SetAerV102HmdCue(string text)
        {
            text = text ?? "";
            if (AerV102HmdCueText == text) return;
            AerV102HmdCueText = text;
            AerV102HmdCueRevision++;
            MelonLogger.Warning($"[StereoRender][AER V102b][HmdCue] revision={AerV102HmdCueRevision} text='{text.Replace("\n", " / ")}'");
        }

        // V104 PRODUCTION FIX: V103 perceptual testing established the depth-coherence
        // guard as the permanent hole-fill policy. The historical unguarded HoleFill mixed
        // foreground/background colors across depth discontinuities and recursively allowed
        // radius-1 invented pixels to seed radius 2, producing the long-standing foliage/top-field
        // wobble. V140's headset A/B showed that DEPTH-GRID / NO SPATIAL FILL eliminates the
        // remaining jitter at all three DLSS input scales. V141 makes that tested profile the
        // default. This bool is now an opt-in ROLLBACK to the old V103 guarded R1/R2 fill;
        // no path enables unguarded cross-depth filling. V100 true-hole rotation fallback stays on.
        public static bool AerV104DepthGuardedFillPermanentEnabled = false;

        // Retained only as an opt-in historical experiment. OFF in production. Keeping the
        // machinery makes later regression A/B testing possible without touching the baseline.
        public static bool AerV103DepthGuardFillPerceptualTestEnabled = false;
        public static bool AerV103ExposePreOpticalFlowJitter = false;
        public static float AerV103SetupSeconds = 10.0f;
        public static float AerV103PhaseSeconds = 10.0f;

        // V103-tested depth-guard parameters, promoted unchanged to the V104 production baseline.
        public static float AerV103FillDepthAbsToleranceMeters = 0.30f;
        public static float AerV103FillDepthRelativeTolerance = 0.06f;
        // V105: cap the relative tolerance in metres. V104's 6% rule becomes 6m at 100m
        // and 9m at 150m, wide enough to merge distinct mid/far foliage layers.
        public static float AerV105FillDepthMaxToleranceMeters = 1.50f;
        public static float AerV103FillMinSupportFraction = 0.60f;
        public static int AerV103FillMinSupportCount = 2;

        internal static void SetAerV103HmdCue(string text)
        {
            text = text ?? "";
            if (AerV102HmdCueText == text) return;
            AerV102HmdCueText = text;
            AerV102HmdCueRevision++;
            MelonLogger.Warning($"[StereoRender][AER V103][HmdCue] revision={AerV102HmdCueRevision} text='{text.Replace("\n", " / ")}'");
        }

        // V84 CLEAN PERFORMANCE ANCHOR. Recover the comparatively fluid V53-era
        // behavior while retaining later HUD geometry/layout fixes.
        public static bool AerV84V53LikeWorldPathEnabled = false; // V85 keeps V84 only as a known-fast reference path
        public static bool AerV84DesktopDebugEnabled = false;
        public static bool AerV84ForensicsEnabled = false;
        public static bool AerV84CameraLocalCustomPassEnabled = true; // V85: minimal raw-depth CustomPass only

        // V85 PERFORMANCE DISCIPLINE: only the full-resolution camera-local RFloat
        // capture needed by the temporal renderer. All diagnostic passes/readbacks stay off.
        public static bool AerV85CameraLocalDepthDiagnosticsEnabled = false;
        public static bool AerV85DepthTruthBoardEnabled = false;

        // Coupled experimental V72/V76 HUD depth-catcher + dense-remap path.
        // User observed its own jitter field; default OFF, one bool restores both.
        public static bool AerHudDepthCatchersExperimentalEnabled = false; // V111 HARD-OFF: no HUD catcher/proxy runtime path

        public static bool AerDepthStereoWarpV50RequireExactFrame = true;

        // V83: constitutionally HARD-OFF. V36 rotation-only stale-eye homography is not used
        // for the temporal target, not used for hole fallback, and not used as submission fallback.
        public static bool AerDepthStereoWarpV51UseRotationAlignedTargetFallback = false;

        // V69 diagnostic: after AER activates, run a short A/B/A test in which only three
        // known HUD regions (GPS aperture, visible vitals RawImage, MessageList) are reconstructed
        // at the physical HUD plane depth instead of whatever TREE/ROCK/SKY depth happens to sit
        // behind their transparent pixels. This does NOT alter Unity's CanvasRenderer output.
        public static bool AerHudPlaneDepthProbeV69Enabled = false;

        // V74: exact invisible HUD depth meshes, compiled directly into the DLL. V73 never
        // reached these meshes because its second AssetBundle failed LoadFromMemory and fell
        // back to V72 analytic shapes. V74 removes that runtime dependency completely.
        // Dense planar HUD remap is still deliberately NOT enabled in this build.
        public static bool AerHudDepthProxyV72Enabled = false; // V111 HARD-OFF: compiled/analytic HUD depth primitives retired
        // V76: stop treating HUD as near world geometry. Exact HUD silhouettes CUT HOLES in the
        // private source depth (rawDepth=0 -> invalid to V51), so HUD color never enters the sparse
        // world forward warp. After V51 finishes world+fallback, current source-eye HUD pixels are
        // densely remapped onto exact target-eye disc/ring/rect geometry in one coherent pass.
        public static bool AerHudDensePlanarRemapV76Enabled = false; // V111 HARD-OFF: V109 HUD remap experiment fully reverted
        public static bool AerHudDensePlanarTextBlackBackingV76 = false; // V111 HARD-OFF
        public static float AerHudPlaneDepthProbeV69DepthMeters = 0.50f;
        public static float AerHudPlaneDepthProbeV69BaselineSeconds = 4.0f;
        public static float AerHudPlaneDepthProbeV69OverrideSeconds = 8.0f;

        public static float AerDepthStereoWarpV48DesktopUpdateIntervalSeconds = 0.25f;
        public static int AerDepthStereoWarpV48DesktopPanelWidth = 640;

        public static int AerWarmupRenderFrames = 4;
        public static int AerCameraChangeWarmupFrames = 2;

        // V37 render-purity diagnostic. The menu/config cannot be trusted to describe the custom
        // eye cameras' ACTUAL HDRP state, and V35/V36 showed strong monocular ghosting too, which
        // points at a temporal rendering/history problem rather than binocular fusion alone.
        // Therefore force the eye cameras themselves to use NO HDRP AA, NO DLSS, and NO dynamic
        // resolution, then repeatedly disable the source Volume components for the cinematic /
        // temporal effects most capable of leaving image history or distorting screen geometry.
        // This build is intentionally diagnostic; do not use its FPS as a clean benchmark.
        // V91: the old V37 safety kill MUST be off now. It explicitly disabled exactly the
        // per-camera DLSS/dynamic-resolution path we are intentionally testing.
        public static bool AerV37ForceTemporalReconstructionOff = false;
        public static bool AerV37ForceCinematicVolumeEffectsOff = false; // V37 sweep found no matching runtime components and added avoidable diagnostic overhead; leave the proven eye-camera AA/DLSS kill on, but stop rescanning Volumes.
        public static float AerV37TelemetryIntervalSeconds = 2.00f; // V38 keeps only sparse telemetry; V37 already answered the ordering question conclusively.

        // V38: submit only after Unity has actually rendered the eye camera(s) scheduled in this
        // frame. V37 proved MelonMod.OnLateUpdate runs BEFORE StereoRender.LateUpdate on this game,
        // so the old path necessarily submitted the previous frame's fresh eye and a two-frame-old
        // stale eye. WaitForEndOfFrame gives us the desired OpenVR order in one Unity frame:
        // WaitGetPoses -> schedule/render selected eye -> commit its texture/pose -> Submit.
        public static bool AerEndOfFrameSubmissionEnabled = true;

        // -2 = nothing scheduled yet, -1 = both eyes, 0 = left, 1 = right.
        // The pose is captured when we schedule the camera(s). In V38+ the normal AER path commits
        // that schedule from WaitForEndOfFrame, after Unity/HDRP has rendered the selected camera
        // in the SAME frame. CommitPreviouslyScheduledAerRenderV34 remains only as a conservative
        // fallback for non-EOF/transition cases; it is not the intended steady-state AER path.
        private int _aerScheduledEye = -2;
        private HmdMatrix34_t _aerScheduledPose;
        private bool _aerScheduledPoseValid;
        private int _aerScheduledFrame = -1;
        private int _aerWarmupFramesRemaining;
        private int _aerNextEye; // 0 left first, then alternate forever
        private int _aerLastCompletedEye = -1;
        private HmdMatrix34_t _aerLeftRenderPose;
        private HmdMatrix34_t _aerRightRenderPose;
        private bool _aerLeftRenderPoseValid;
        private bool _aerRightRenderPoseValid;
        private int _aerLeftRenderFrame = -1;
        private int _aerRightRenderFrame = -1;
        private HmdMatrix34_t _aerLatestTrackingPose;
        private bool _aerLatestTrackingPoseValid;
        private int _aerLatestTrackingPoseFrame = -1;
        private int _aerStereoLateUpdateFrame = -1;
        private int _aerLastEndOfFrameSubmitFrame = -1;
        private int _aerEndOfFrameMissLogsRemaining = 12;
        private bool _aerLastEnabledState;
        private bool _aerStartupAnnouncementDone;
        private bool _aerGameplayActivationScheduled;
        private bool _aerEndOfFrameLoopStarted;
        // V111: while the native pause menu or SonsVR settings overlay is open, render BOTH
        // eye cameras for real. This removes the one-frame synthetic-eye temporal component from
        // menus without changing ordinary gameplay AER. On exit we warm up both eyes before
        // resuming alternation so stale UI/history cannot leak back into the first gameplay frame.
        private bool _aerUiFullStereoWasForcedV111;

        // OpenVR's native VRTextureWithPose_t is C++ inheritance: Texture_t first, then a
        // HmdMatrix34_t. Different generations of Valve's generated C# bindings represent that
        // inherited struct inconsistently, while CVRCompositor.Submit always accepts ref Texture_t.
        // Define the native layout ourselves and reinterpret its first byte as Texture_t when
        // calling Submit with Submit_TextureWithPose. This avoids depending on whichever generated
        // VRTextureWithPose_t shape happened to ship in SonsSteamVR_IL2CPP.dll.
        [StructLayout(LayoutKind.Sequential)]
        private struct OpenVrTextureWithPoseNative
        {
            public Texture_t texture;
            public HmdMatrix34_t mDeviceToAbsoluteTracking;
        }

        private bool _aerPoseLayoutChecked;
        private bool _aerPoseLayoutValid;
        private bool _aerPoseSubmissionDisabledForSession;
        private bool _aerPoseFallbackLogged;
        private int _aerSubmitErrorLogsRemaining = 3;

        // V36 rotational stale-eye reprojection resources. These are SEPARATE output textures:
        // never overwrite LeftRT/RightRT, because those are the persistent raw eye renders that
        // the skipped camera must retain until it gets its next real HDRP render. Only the stale
        // eye is warped on a given application frame; the fresh eye is submitted untouched.
        private RenderTexture _aerLeftReprojectedRT;
        private RenderTexture _aerRightReprojectedRT;
        private Material _aerReprojectionMaterial;
        private bool _aerReprojectionInitAttempted;
        private bool _aerReprojectionAvailable;
        private bool _aerReprojectionActiveLogged;
        private int _aerReprojectionFailureLogsRemaining = 3;

        /// <summary>
        /// Unity callback called when the script instance is being loaded.
        /// Initializes the stereo rendering pipeline.
        /// </summary>
        public void Awake()
        {
            Instance = this;
            Setup();
        }

        /// <summary>
        /// Creates the HMD rig (Head + LeftEye/RightEye), configures cameras,
        /// sets up render textures, and initializes the stereo render pass.
        /// </summary>
        public void Setup()
        {
            MelonLogger.Msg($"[StereoRender] ### SETTING UP STEREO RENDERING PIPELINE ###");

            // Create or find the HMD transform
            Head = transform.Find("VRHead");
            if (!Head)
            {
                Head = new GameObject("VRHead").transform;
            }
            Head.SetParent(transform, false);
            Head.localPosition = Vector3.zero;
            Head.localRotation = Quaternion.identity;

            // Mark this object as tracked by SteamVR (HMD)
            Head.gameObject.GetOrAddComponent<SteamVR_TrackedObject>().index = SteamVR_TrackedObject.EIndex.Hmd;

            // --- LEFT EYE ---
            var leftEye = Head.Find("VRLeftEye");
            if (!leftEye)
            {
                leftEye = new GameObject("VRLeftEye").transform;
            }
            leftEye.SetParent(Head, false);
            leftEye.localPosition = new Vector3(-separation * 0.5f, 0, 0); // Offset left
            leftEye.localRotation = Quaternion.identity;

            LeftCam = leftEye.gameObject.GetOrAddComponent<Camera>();
            LeftCam.cullingMask = defaultCullingMask;
            LeftCam.stereoTargetEye = StereoTargetEyeMask.None; // We handle stereo manually
            LeftCam.clearFlags = CameraClearFlags.Skybox;
            LeftCam.nearClipPlane = clipStart;
            LeftCam.farClipPlane = clipEnd;
            LeftCam.fieldOfView = 109.363f; // Matches typical headset FoV
            LeftCam.depth = 0;
            LeftCam.enabled = true;

            // --- RIGHT EYE ---
            var rightEye = Head.Find("VRRightEye");
            if (!rightEye)
            {
                rightEye = new GameObject("VRRightEye").transform;
            }
            rightEye.SetParent(Head, false);
            rightEye.localPosition = new Vector3(separation * 0.5f, 0, 0); // Offset right
            rightEye.localRotation = Quaternion.identity;

            RightCam = rightEye.gameObject.GetOrAddComponent<Camera>();
            RightCam.cullingMask = defaultCullingMask;
            RightCam.stereoTargetEye = StereoTargetEyeMask.None;
            RightCam.clearFlags = CameraClearFlags.Skybox;
            RightCam.nearClipPlane = clipStart;
            RightCam.farClipPlane = clipEnd;
            RightCam.fieldOfView = 109.363f;
            RightCam.depth = 0;
            RightCam.enabled = true;

            // Apply projection matrices from SteamVR (non-linear, per-eye)
            UpdateProjectionMatrix();

            // Allocate render textures at recommended resolution
            UpdateResolution();

            // Sons of the Forest uses HDRP, which controls anti-aliasing and DLSS through a
            // separate per-camera component (HDAdditionalCameraData) rather than the legacy
            // RenderTexture.antiAliasing (MSAA) property UpdateResolution() sets above - HDRP
            // mostly ignores that for its own internal rendering, and the game's in-game DLSS
            // setting most likely only configures the game's own native camera, not these
            // separately-created eye cameras. See SonsVRMod.HdrpAntialiasingMode/
            // EnableDlssOnEyeCameras to change these or disable this entirely.
            //
            // DIAGNOSTIC: user confirmed construction ghost-preview outlines invisible in VR
            // starting exactly at the build that introduced this whole function, and setting
            // both AA and DLSS to "off" (None/false) did NOT bring the outline back - ruling out
            // TAA specifically. Testing whether the mere PRESENCE of HDAdditionalCameraData on
            // these cameras (which never had it at all before this function existed) is itself
            // the cause, independent of what fields we set on it - skipping the whole function,
            // including the AddComponent step, not just leaving its fields at HDRP defaults.
            if (SkipHdrpCameraDataEntirely)
            {
                MelonLogger.Msg("[StereoRender] SkipHdrpCameraDataEntirely is set - not touching HDAdditionalCameraData on either eye at all.");
            }
            else
            {
                TryConfigureHdrpCameraSettings(LeftCam);
                TryConfigureHdrpCameraSettings(RightCam);
            }

            // V81e: DO NOT install the camera-local CustomPass here.
            // StereoRender.Setup can run during the pre-spawn/fallback-camera phase
            // (the under-map curved-view interstitial). Installation is deliberately
            // deferred until the already-proven gameplay gate confirms:
            // SonsMain + MainCameraFP + LocalPlayer + one-time MainCameraFP HDRP copy.

            // Initialize the OpenVR submission pass
            stereoRenderPass = new StereoRenderPass(this);

            // V50: install one SRP end-camera hook. It is completely dormant until gameplay AER
            // is active, and then latches only VRLeftEye / VRRightEye depth immediately after the
            // identified eye camera finishes rendering.
            AerDepthStereoWarpV48.InstallCameraDepthLatch(this);

            // V38 moves AER submission to WaitForEndOfFrame. V37 proved, in every one of 521
            // telemetry samples, that MelonMod.OnLateUpdate executes BEFORE this component's
            // LateUpdate. That old ordering was valid only as a one-frame pipeline: current
            // LateUpdate rendered an eye, next Unity frame submitted it. Under AER that means the
            // pair at submit time is ages 1f/2f, which becomes enormous motion error at low FPS.
            // End-of-frame runs after the eye cameras have rendered this frame, allowing ages 0f/1f.
            if (AlternateEyeRenderingEnabled && AerEndOfFrameSubmissionEnabled)
            {
                MelonLoader.MelonCoroutines.Start(AerSubmissionV38.EndOfFrameSubmitLoop(this));
                _aerEndOfFrameLoopStarted = true;
                MelonLogger.Msg("[StereoRender][AER V38B AUDITED] end-of-frame submit loop started; AER submission will occur after this frame's HDRP eye render instead of in MelonMod.OnLateUpdate.");
            }

            // V33 AER starts with both eyes for a few frames. Besides guaranteeing that neither
            // persistent RenderTexture begins black, this gives HDRP/TAA history a normal stereo
            // bootstrap before we start skipping alternate eye renders.
            ResetAerStateV34(this, AerWarmupRenderFrames, "setup", invalidateCompletedPoses: true, log: false);
            MelonLogger.Msg($"[StereoRender] Setup complete - stereoRenderPass created. AER={(AlternateEyeRenderingEnabled ? "ON" : "OFF")} rotational-reprojection={(AerRotationalReprojectionEnabled ? "ON" : "OFF")} pose-aware-submit={(AerPoseAwareSubmissionEnabled ? "requested" : "OFF")} warmup={AerWarmupRenderFrames} frames.");
        }

        public static bool CopyHdrpDataFromNativeCameraEnabled = true; // Likely the actual fix for the construction ghost-preview outline and underwater water-density issue: this project was adding a FRESH, HDRP-default HDAdditionalCameraData to the eye cameras, rather than copying the game's own, already-correctly-configured one from the native camera - confirmed via the other dev's own code and doc comment ("without this the eye cameras render with HDRP defaults, so the VR view loses the post-processing and volume setup the flat view has"). HDRP provides a proper, official HDAdditionalCameraData.CopyTo() method for exactly this. Also sets hasPersistentHistory=true per the other dev's own finding, unrelated to this specific bug but worth taking at the same time - a non-persistent eye camera can have HDRP's temporal history (TAA, motion vectors, volumetric accumulation) dropped between frames, showing up as flicker/ghosting.
        private bool _hdrpDataCopiedOnce;

        // V81e: camera-local depth probe is intentionally a POST-SPAWN object.
        // The mod can create StereoRender long before the final MainCameraFP/LocalPlayer
        // exists, while the user sees the temporary under-map fallback view.
        private float _nextV81eProbeEnsureTime = -1f;
        private bool _v81eProbeGameplayInstallLogged;

        /// <summary>
        /// Copies the native (non-VR) camera's own HDAdditionalCameraData onto both eye cameras -
        /// volume layer mask/anchor, probe layer mask, clear mode, and whatever else makes Custom
        /// Passes (construction ghost-previews, underwater water density, and likely other similar
        /// effects) work correctly - before then reapplying this mod's own AA/DLSS overrides on
        /// top of that copied baseline, so neither cancels the other out.
        ///
        /// FIXED (found via comparing against the other dev's log, investigating the startup FPS
        /// issue): this used to re-run every time the passed-in camera reference changed, which
        /// happens constantly during completely normal play - e.g. every time the tarp/inventory
        /// screen opens or closes, "source" genuinely becomes a different Camera object
        /// (InventoryCamera, then back to MainCameraFP), and each swap re-triggered this entire
        /// operation from scratch, right at the moment that likely caused the tarp black-flash
        /// bug. The same thing happens repeatedly during the new-game intro's own sequence of
        /// camera changes, right in the most GPU-sensitive window of the whole session - a
        /// plausible contributor to the startup FPS issue this whole pause-menu workaround exists
        /// for. The eye cameras only ever need this done ONCE, ever, regardless of how many times
        /// the tracked camera reference later changes for unrelated reasons - now gated on that,
        /// and specifically waits for a genuine MainCameraFP (not any earlier camera like the
        /// title screen's own, or the intro cutscene's) so what gets copied is the real gameplay
        /// configuration.
        /// </summary>
        private void EnsureGameplayDepthProbeV81e()
        {
            if (!AerV84CameraLocalCustomPassEnabled)
                return;

            if (!AerGameplayReadyForActivation ||
                !_hdrpDataCopiedOnce ||
                LeftCam == null ||
                RightCam == null)
                return;

            // Re-check periodically rather than once. If a scene transition or Unity object
            // lifetime event destroys the probe volume, EnsureVolumes recreates it.
            if (Time.unscaledTime < _nextV81eProbeEnsureTime)
                return;

            _nextV81eProbeEnsureTime = Time.unscaledTime + 1.0f;

            EyeDepthCaptureCustomPassV81.EnsureVolumes(LeftCam, RightCam);
            // V99 captures HDRP camera/object motion for telemetry even though the displayed
            // renderer still does NOT apply the V89 residual. This lets one truth run quantify
            // total motion, camera-only motion and residual object/animation motion separately.
            if (AerTemporalObjectMotionV89Enabled ||
                (AerV99ForensicsEnabled && AerV99CaptureHdrpMotionVectors))
                EyeMotionCaptureCustomPassV89.EnsureVolumes(LeftCam, RightCam);

            if (!_v81eProbeGameplayInstallLogged)
            {
                _v81eProbeGameplayInstallLogged = true;

                MelonLogger.Warning(
                    "[StereoRender][AER V81e][CustomDepth] POST-SPAWN probe ensure ACTIVE: " +
                    "SonsMain/MainCameraFP/LocalPlayer gate is armed AND MainCameraFP HDRP data " +
                    "has been copied to the eye cameras. Probe volumes are now allowed to exist. " +
                    $"leftId={LeftCam.GetInstanceID()} rightId={RightCam.GetInstanceID()} " +
                    $"frame={Time.frameCount}.");
            }
        }

        public void CopyHdrpDataFromNativeCamera(Camera source)
        {
            if (!CopyHdrpDataFromNativeCameraEnabled) return;
            if (source == null || LeftCam == null || RightCam == null) return;
            if (!source.name.Equals("MainCameraFP")) return; // wait for the real gameplay camera specifically

            // V43 activation is deliberately tied to the same already-proven gameplay-camera
            // milestone as this HDRP copy, plus VRPlayer's explicit SonsMain/MainCameraFP/
            // LocalPlayer gate. If the HDRP copy happened unusually early, a later call can still
            // schedule activation once that gate becomes true. No AER runs during title/loading.
            if (_hdrpDataCopiedOnce)
            {
                EnsureGameplayDepthProbeV81e();

                if (AerDeferredGameplayActivationEnabled && AerGameplayReadyForActivation &&
                    !AlternateEyeRenderingEnabled && !_aerGameplayActivationScheduled)
                {
                    _aerGameplayActivationScheduled = true;
                    MelonLoader.MelonCoroutines.Start(AerSubmissionV38.ActivateAerAfterEndOfFrame(this));
                    MelonLogger.Warning("[StereoRender][AER V43] gameplay gate is ready and MainCameraFP HDRP data was already copied; scheduling ONE-WAY AER activation after this frame's EOF. This frame remains conventional full stereo.");
                }
                return;
            }

            try
            {
                var sourceData = source.GetComponent<HDAdditionalCameraData>();
                if (sourceData == null)
                {
                    MelonLogger.Warning($"[StereoRender] Native camera '{source.name}' has no HDAdditionalCameraData of its own - nothing to copy, leaving eye cameras on HDRP defaults.");
                    return;
                }

                CopyHdrpDataToEye(sourceData, LeftCam);
                CopyHdrpDataToEye(sourceData, RightCam);

                _hdrpDataCopiedOnce = true;
                MelonLogger.Msg($"[StereoRender] Copied HDAdditionalCameraData from native camera '{source.name}' onto both eyes (once, will not repeat).");

                // If the LocalPlayer gameplay gate is already armed, install now.
                // If it arms a few frames later, LateUpdate's periodic ensure below catches it.
                EnsureGameplayDepthProbeV81e();

                if (AerDeferredGameplayActivationEnabled && AerGameplayReadyForActivation &&
                    !AlternateEyeRenderingEnabled && !_aerGameplayActivationScheduled)
                {
                    _aerGameplayActivationScheduled = true;
                    MelonLoader.MelonCoroutines.Start(AerSubmissionV38.ActivateAerAfterEndOfFrame(this));
                    MelonLogger.Warning("[StereoRender][AER V43] SonsMain + MainCameraFP + LocalPlayer gate AND successful gameplay HDRP copy confirmed; scheduling ONE-WAY AER activation after this frame's EOF. This frame remains conventional full stereo.");
                }

                // V40C's diagnostic policy intentionally applies AA=None, DLSS=OFF and dynamic
                // resolution=OFF even while AER is still off. Therefore the activation itself
                // does not need to mutate HDRP settings; both pre- and post-activation paths use
                // the same eye-camera temporal policy.
                TryConfigureHdrpCameraSettings(LeftCam);
                TryConfigureHdrpCameraSettings(RightCam);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[StereoRender] Failed to copy HDAdditionalCameraData from native camera: {e.Message}");
            }
        }

        private static void CopyHdrpDataToEye(HDAdditionalCameraData sourceData, Camera eyeCam)
        {
            var eyeData = eyeCam.gameObject.GetComponent<HDAdditionalCameraData>();
            if (eyeData == null) eyeData = eyeCam.gameObject.AddComponent<HDAdditionalCameraData>();

            sourceData.CopyTo(eyeData);
            eyeData.hasPersistentHistory = true;
        }

        /// <summary>
        /// Attempts to configure UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData on
        /// the given eye camera via reflection, rather than a direct compile-time reference to
        /// the HDRP assembly - this mod's .csproj may or may not already reference that specific
        /// interop DLL, and getting that wrong with a direct `using` would break the whole build;
        /// resolving the type by name at runtime means this can only ever fail to apply this one
        /// optimization (with a clear log message explaining why), never break compilation or
        /// crash the mod. Sets both the anti-aliasing mode (fixes grainy/aliased foliage, since
        /// HDRP ignores the legacy MSAA property our cameras otherwise rely on) and, separately,
        /// whether DLSS/dynamic resolution are allowed on this specific camera (the game's own
        /// in-game DLSS setting likely doesn't reach these separately-created eye cameras at all).
        /// </summary>
        /// <summary>Public wrapper so VRPlayer.ForceNativeCameraLowRes can opt the native camera into allowDynamicResolution the same way the eye cameras already are.</summary>
        public static void TryConfigureHdrpCameraSettingsPublic(Camera cam) => TryConfigureHdrpCameraSettings(cam);

        // V107: the native MainCameraFP is retained ONLY for Camera.main / mouse-coordinate / UI
        // semantics. V106 snapshots proved it still carried TAA + DLSS/DRS + persistent HDRP
        // history at 3840x2160 even after its culling mask had been reduced to the dummy layer.
        // CopyHdrpDataFromNativeCamera() runs first; only then do we make this desktop camera cheap.
        private static readonly System.Collections.Generic.HashSet<int> _nativeTrashConfiguredV107 = new System.Collections.Generic.HashSet<int>();
        public static void TryConfigureNativeTrashCameraV107(Camera cam)
        {
            if (cam == null) return;
            int id = cam.GetInstanceID();
            if (_nativeTrashConfiguredV107.Contains(id)) return;

            try
            {
                Type hdCameraDataType = FindTypeAcrossLoadedAssemblies("UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData");
                if (hdCameraDataType == null) return;
                Il2CppSystem.Type il2cppHdType = Il2CppType.From(hdCameraDataType);
                Component hdData = cam.GetComponent(il2cppHdType);
                if (hdData == null) return;

                object typedHdData = null;
                try
                {
                    var castMethod = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("Cast")?.MakeGenericMethod(hdCameraDataType);
                    typedHdData = castMethod?.Invoke(hdData, null);
                }
                catch { }
                if (typedHdData == null) return;

                TrySetHdrpField(hdCameraDataType, typedHdData, "antialiasing", "None", cam.name);
                TrySetHdrpField(hdCameraDataType, typedHdData, "allowDeepLearningSuperSampling", "False", cam.name);
                TrySetHdrpField(hdCameraDataType, typedHdData, "allowDynamicResolution", "False", cam.name);
                cam.allowDynamicResolution = false;

                HDAdditionalCameraData data = typedHdData as HDAdditionalCameraData;
                if (data != null)
                {
                    data.hasPersistentHistory = false;
                    FrameSettings settings = data.renderingPathCustomFrameSettings;
                    FrameSettingsOverrideMask overrides = data.renderingPathCustomFrameSettingsOverrideMask;
                    int disabled = 0;
                    disabled += DisableFrameSettingsFields(ref settings, ref overrides, CameraEffectFields, true);
                    disabled += DisableFrameSettingsFields(ref settings, ref overrides, ScreenSpaceFields, true);
                    disabled += DisableFrameSettingsFields(ref settings, ref overrides, VolumetricFields, true);
                    data.renderingPathCustomFrameSettings = settings;
                    data.renderingPathCustomFrameSettingsOverrideMask = overrides;
                    data.customRenderingSettings = true;
                    MelonLogger.Warning($"[StereoRender][V107 NativeCamera] '{cam.name}' stripped: AA=None DLSS=OFF DRS=OFF persistentHistory=OFF and {disabled} optional HDRP effect fields disabled. Camera remains enabled for interaction semantics.");
                }

                _nativeTrashConfiguredV107.Add(id);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[StereoRender][V107 NativeCamera] strip failed on '{cam.name}': {e.Message}");
            }
        }

        private static void TryConfigureHdrpCameraSettings(Camera cam)
        {
            if (cam == null) return;

            try
            {
                Type hdCameraDataType = FindTypeAcrossLoadedAssemblies("UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData");
                if (hdCameraDataType == null)
                {
                    MelonLogger.Warning($"[StereoRender] HDAdditionalCameraData type not found - couldn't configure HDRP anti-aliasing/DLSS on {cam.name}. " +
                        "If the picture looks grainy/aliased, this is likely why; the game's HDRP interop assembly may not be loaded/referenced the way this code expects.");
                    return;
                }

                Component hdData;
                try
                {
                    Il2CppSystem.Type il2cppHdType = Il2CppType.From(hdCameraDataType);
                    Component existing = cam.GetComponent(il2cppHdType);
                    hdData = existing != null ? existing : cam.gameObject.AddComponent(il2cppHdType);
                }
                catch (Exception conversionEx)
                {
                    MelonLogger.Warning($"[StereoRender] Couldn't bridge HDAdditionalCameraData into IL2CPP's type system for {cam.name}: {conversionEx.Message}");
                    return;
                }
                if (hdData == null)
                {
                    MelonLogger.Warning($"[StereoRender] Failed to get/add HDAdditionalCameraData on {cam.name}.");
                    return;
                }

                // FIX: GetComponent/AddComponent via a reflected IL2CPP Type hands back a generic
                // wrapper object - reflection doesn't recognize it as specifically
                // HDAdditionalCameraData, so field.SetValue()/prop.SetValue() below fail with
                // "Object does not match target type" even though the component IS correctly
                // attached (confirmed via log - every single field-set call failed this way).
                // Same lesson already learned once during the A-close investigation: an explicit
                // Cast<T>() to the real reflected type is required before reflection can read or
                // write its members.
                object typedHdData;
                try
                {
                    var castMethod = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("Cast")?.MakeGenericMethod(hdCameraDataType);
                    typedHdData = castMethod?.Invoke(hdData, null);
                }
                catch (Exception castEx)
                {
                    MelonLogger.Warning($"[StereoRender] Failed to Cast<HDAdditionalCameraData> on {cam.name}: {castEx.Message}");
                    return;
                }
                if (typedHdData == null)
                {
                    MelonLogger.Warning($"[StereoRender] Cast<HDAdditionalCameraData> returned null on {cam.name}.");
                    return;
                }

                if (AerV37ForceTemporalReconstructionOff)
                {
                    // Unlike the old meaning of HdrpAntialiasingMode == "None" (which meant
                    // "leave the default alone"), V37 explicitly writes the HDRP enum value None.
                    // We also explicitly DISALLOW DLSS and dynamic resolution on the custom eyes
                    // so no temporal reconstruction path can secretly remain active underneath AER.
                    TrySetHdrpField(hdCameraDataType, typedHdData, "antialiasing", "None", cam.name);
                    TrySetHdrpField(hdCameraDataType, typedHdData, "allowDeepLearningSuperSampling", "False", cam.name);
                    TrySetHdrpField(hdCameraDataType, typedHdData, "allowDynamicResolution", "False", cam.name);
                    cam.allowDynamicResolution = false;
                }
                else
                {
                    if (!string.Equals(HdrpAntialiasingMode, "None", StringComparison.OrdinalIgnoreCase))
                    {
                        TrySetHdrpField(hdCameraDataType, typedHdData, "antialiasing", HdrpAntialiasingMode, cam.name);
                    }

                    bool dlssEnabledV105 = EnableDlssOnEyeCameras && EnableV91RealDlss && AerV91RealDlssEnabled;
                    if (dlssEnabledV105)
                    {
                        TrySetHdrpField(hdCameraDataType, typedHdData, "allowDeepLearningSuperSampling", "True", cam.name);
                        TrySetHdrpField(hdCameraDataType, typedHdData, "allowDynamicResolution", "True", cam.name);
                        cam.allowDynamicResolution = true;
                        DlssRuntimeV91.ConfigureEyeCamera(
                            typedHdData as UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, cam);
                        MelonLogger.Msg($"[StereoRender][AER V105][DLSS] eye camera '{cam.name}' opted into Camera.allowDynamicResolution + HDRP Allow DLSS/DRS.");
                    }
                    else
                    {
                        TrySetHdrpField(hdCameraDataType, typedHdData, "allowDeepLearningSuperSampling", "False", cam.name);
                        TrySetHdrpField(hdCameraDataType, typedHdData, "allowDynamicResolution", "False", cam.name);
                        cam.allowDynamicResolution = false;
                    }
                }

                // Kept fully separate (own try/catch) from the AA/DLSS work above, which is
                // already proven working - a failure here should never risk that.
                try
                {
                    ApplyEyeCameraEffectsOff(typedHdData as UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, cam.name);
                }
                catch (Exception effectsEx)
                {
                    MelonLogger.Warning($"[StereoRender] Failed to disable camera effects on {cam.name}: {effectsEx.Message}");
                }

                // Also kept fully separate (own try/catch) - an experiment, not a proven fix yet.
                try
                {
                    TryEnableCustomPass(typedHdData as UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, cam.name);
                }
                catch (Exception customPassEx)
                {
                    MelonLogger.Warning($"[StereoRender] Failed to enable custom pass on {cam.name}: {customPassEx.Message}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[StereoRender] Failed to configure HDRP camera settings on {cam.name}: {ex}");
            }
        }

        public static bool TryEnableCustomPassEnabled = true; // CONFIRMED NOT EFFECTIVE - user tested with this on, still no outline/water density. The real fix turned out to be broader than one FrameSettingsField: see CopyHdrpDataFromNativeCamera above, which copies the native camera's ENTIRE HDAdditionalCameraData (volume layer mask, probe layer mask, clear mode, and more) rather than trying to guess which single field was missing. Left off/disabled now that the actual fix is in place.

        /// <summary>
        /// Deliberately touches ONLY the CustomPass field, not a broader FrameSettings rebuild -
        /// minimizes risk of accidentally disabling something else that isn't part of this
        /// experiment. Uses customRenderingSettings=true (required for any override to take
        /// effect at all), which is a real, if small, departure from every other camera's
        /// inherited defaults - worth watching for ANY other visual difference, not just the
        /// outline/water this is specifically targeting.
        /// </summary>
        private static void TryEnableCustomPass(HDAdditionalCameraData data, string cameraName)
        {
            if (data == null || !TryEnableCustomPassEnabled) return;

            FrameSettings settings = data.renderingPathCustomFrameSettings;
            FrameSettingsOverrideMask overrides = data.renderingPathCustomFrameSettingsOverrideMask;

            settings.SetEnabled(FrameSettingsField.CustomPass, true);
            overrides.mask[(uint)FrameSettingsField.CustomPass] = true;

            data.renderingPathCustomFrameSettings = settings;
            data.renderingPathCustomFrameSettingsOverrideMask = overrides;
            data.customRenderingSettings = true;

            MelonLogger.Msg($"[StereoRender] {cameraName}: explicitly enabled CustomPass FrameSettingsField.");
        }

        public static bool EyeDisableCameraEffects = false; // CONFIRMED NOT EFFECTIVE - see ApplyEyeCameraEffectsOff's doc comment. Read-back diagnostic proved the write genuinely sticks at the C# level (customRenderingSettings/IsEnabled/override mask all report correctly), yet produces zero visible or FPS change in VR - HDRP's actual renderer isn't honoring these per-camera FrameSettings overrides for reasons not diagnosable via reflection/runtime polling alone (most likely a global Volume-based override taking precedence, or an HDRP-version-specific quirk). Left off. Revisit only with a genuinely different technique (e.g. decompiling HDRP's actual rendering decision code), not by re-testing this same approach.
        public static bool EyeDisableScreenSpaceEffects = false; // Same conclusion as EyeDisableCameraEffects above - confirmed via read-back diagnostic that the write sticks correctly but HDRP doesn't honor it. Left off.
        public static bool EyeDisableVolumetrics = false; // Same conclusion as EyeDisableCameraEffects above - confirmed via read-back diagnostic that the write sticks correctly but HDRP doesn't honor it. Left off.

        private static readonly FrameSettingsField[] CameraEffectFields =
        {
            FrameSettingsField.MotionBlur,
            FrameSettingsField.DepthOfField,
            FrameSettingsField.Distortion,
            FrameSettingsField.RoughDistortion,
            FrameSettingsField.ChromaticAberration
        };

        private static readonly FrameSettingsField[] ScreenSpaceFields =
        {
            FrameSettingsField.SSR,
            FrameSettingsField.TransparentSSR,
            FrameSettingsField.SSAO,
            FrameSettingsField.ContactShadows
        };

        private static readonly FrameSettingsField[] VolumetricFields =
        {
            FrameSettingsField.Volumetrics,
            FrameSettingsField.ReprojectionForVolumetrics,
            FrameSettingsField.VolumetricClouds
        };

        /// <summary>
        /// Gives an eye camera its own, lighter rendering profile by disabling specific HDRP
        /// passes per-camera - the render pipeline itself can't be swapped for a cheaper one (it's
        /// chosen globally, every material in the game is compiled for it), but individual passes
        /// can be skipped per camera via FrameSettings overrides. Unlike the AA/DLSS configuration
        /// above (which uses reflection, deliberately, since HDAdditionalCameraData needed a
        /// Cast&lt;T&gt;() workaround for an IL2CPP interop quirk), this uses HDRP's types
        /// directly - the assembly is already referenced in this project, and the direct API is
        /// simpler and less error-prone for this more involved struct manipulation.
        /// </summary>
        private static void ApplyEyeCameraEffectsOff(HDAdditionalCameraData data, string cameraName)
        {
            if (data == null) return;
            if (!EyeDisableCameraEffects && !EyeDisableScreenSpaceEffects && !EyeDisableVolumetrics) return;

            FrameSettings settings = data.renderingPathCustomFrameSettings;
            FrameSettingsOverrideMask overrides = data.renderingPathCustomFrameSettingsOverrideMask;

            int count = 0;
            count += DisableFrameSettingsFields(ref settings, ref overrides, CameraEffectFields, EyeDisableCameraEffects);
            count += DisableFrameSettingsFields(ref settings, ref overrides, ScreenSpaceFields, EyeDisableScreenSpaceEffects);
            count += DisableFrameSettingsFields(ref settings, ref overrides, VolumetricFields, EyeDisableVolumetrics);

            if (count == 0) return;

            data.renderingPathCustomFrameSettings = settings;
            data.renderingPathCustomFrameSettingsOverrideMask = overrides;
            data.customRenderingSettings = true;

            MelonLogger.Msg($"[StereoRender] {cameraName}: {count} rendering passes switched off");
        }

        private static int DisableFrameSettingsFields(ref FrameSettings settings, ref FrameSettingsOverrideMask overrides, FrameSettingsField[] fields, bool enabled)
        {
            if (!enabled) return 0;

            int count = 0;
            foreach (FrameSettingsField field in fields)
            {
                try
                {
                    settings.SetEnabled(field, false);
                    overrides.mask[(uint)field] = true;
                    count++;
                }
                catch
                {
                    // A field this build of the pipeline doesn't carry - skip it rather than give
                    // up on the whole set.
                }
            }
            return count;
        }

        /// <summary>
        /// Sets a single field or property on an HDAdditionalCameraData instance by name via
        /// reflection, parsing valueText into whatever type that member actually is (enum or
        /// bool) - shared by both the anti-aliasing mode and the DLSS/dynamic-resolution bools
        /// above so this logic (and its defensive fallbacks) only exists once.
        /// </summary>
        private static void TrySetHdrpField(Type hdCameraDataType, object hdData, string memberName, string valueText, string camName)
        {
            try
            {
                FieldInfo field = hdCameraDataType.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
                PropertyInfo prop = field == null ? hdCameraDataType.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance) : null;
                Type memberType = field?.FieldType ?? prop?.PropertyType;

                if (memberType == null)
                {
                    MelonLogger.Warning($"[StereoRender] HDAdditionalCameraData.{memberName} not found as a field or property via reflection - HDRP API may have changed.");
                    return;
                }

                object value = memberType == typeof(bool) ? (object)bool.Parse(valueText) : Enum.Parse(memberType, valueText, ignoreCase: true);

                if (field != null) field.SetValue(hdData, value);
                else prop.SetValue(hdData, value);

                MelonLogger.Msg($"[StereoRender] Set HDAdditionalCameraData.{memberName} to '{valueText}' on {camName}.");
            }
            catch (ArgumentException)
            {
                MelonLogger.Warning($"[StereoRender] '{valueText}' is not a valid value for HDAdditionalCameraData.{memberName} on {camName}. Leaving HDRP default.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[StereoRender] Failed to set HDAdditionalCameraData.{memberName} on {camName}: {ex.Message}");
            }
        }

        private static Type FindTypeAcrossLoadedAssemblies(string fullTypeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = assembly.GetType(fullTypeName);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Dynamically updates the culling mask of both eye cameras.
        /// Useful for temporarily hiding UI, weapons, or other layers in VR.
        /// </summary>
        /// <param name="mask">New culling mask (bitwise layer mask).</param>
        public void SetCameraMask(int mask)
        {
            LeftCam.cullingMask = mask;
            RightCam.cullingMask = mask;
        }

        // REPLACED the earlier Camera.rect-based crop entirely (per user's own report: felt
        // uncomfortable, a subtle "not quite natural" depth perception change - and confirmed
        // they'd felt the same thing with the other dev's own mod too, which uses that identical
        // technique). Best available explanation: Camera.rect doesn't crop-and-leave-the-center-
        // alone the way it sounds - it maps the camera's FULL rendered view into a SMALLER portion
        // of the texture, effectively re-scaling the whole image inside that viewport. The
        // compositor's lens-distortion warp is calibrated for the full FOV arriving at native
        // scale, so that mismatch is a plausible source of the reported discomfort - and would
        // affect anyone using this same technique, not just this project.
        //
        // This version is a genuinely different technique: an actual narrower frustum, built by
        // FIXED A SECOND TIME (confirmed via user report: even after the Y-flip fix, depth
        // layering was wrong - objects rendering in front of things that should occlude them.
        // The hand-built-from-raw-tangents approach (GetProjectionRaw + Matrix4x4.Frustum +
        // GL.GetGPUProjectionMatrix) was still subtly wrong for this specific engine/pipeline
        // (HDRP) in the depth-related rows, even once the Y-axis itself was corrected).
        //
        // Switched to a mathematically much safer approach: rather than rebuild the whole matrix
        // from scratch (risking exactly this kind of subtle per-platform depth convention
        // mismatch), start from the EXACT SAME matrix GetProjectionMatrix() already produces -
        // the one proven correct for this project the whole time, before any of this FOV-crop
        // work began - and surgically modify only the two elements that scaling the FOV tangents
        // actually affects. For an off-center perspective matrix, uniformly scaling left/right/
        // top/bottom toward zero by a factor divides m00 (horizontal) and m11 (vertical) by that
        // same factor, while the off-center skew terms (m02, m12) and every depth/Z-related row
        // stay mathematically unchanged - so leaving those completely untouched isn't a
        // simplification that risks missing something, it's what the math actually says should
        // happen. This can't reintroduce either bug: the Y-axis and Z-depth handling are both
        // inherited byte-for-byte from the already-correct source matrix.
        public static float EyeFovTangentCropHorizontal = 1f; // DISABLED for now (1.0 = no crop) - stepping back to isolate mirror-blit's own gain cleanly, per user's own request, after confirming this causes a noticeable world-movement/distortion artifact when looking at detailed things while moving the head. Root cause understood (see git history/prior comments) but not cleanly fixable without deeper changes to how the texture bounds are communicated to the compositor - revisit later.
        public static float EyeFovTangentCropVertical = 1f; // DISABLED for now, same reason

        /// <summary>
        /// Changes the VR eyes' far clip without reallocating their RenderTextures. This is
        /// deliberately cheap enough for menu transitions: update the Unity camera far planes
        /// and rebuild only the OpenVR projection matrices that encode the same near/far range.
        /// </summary>
        public void SetFarClipDistance(float farClipMeters)
        {
            float next = Mathf.Max(clipStart + 0.1f, farClipMeters);
            if (Mathf.Abs(next - clipEnd) < 0.01f) return;

            clipEnd = next;
            if (LeftCam != null) LeftCam.farClipPlane = clipEnd;
            if (RightCam != null) RightCam.farClipPlane = clipEnd;

            if (LeftCam != null && RightCam != null && OpenVR.System != null)
            {
                UpdateProjectionMatrix();
            }

            // A stale retained eye would otherwise keep the OLD far-plane render for one AER
            // interval while the fresh eye already uses the new menu/gameplay clip. Refresh both
            // together for two frames; cheap compared with leaving a one-eye transition artifact.
            if (AlternateEyeRenderingEnabled)
            {
                ResetAerStateV34(this, AerCameraChangeWarmupFrames, "far clip change",
                    invalidateCompletedPoses: true, log: false);
            }

            AerDepthStereoWarpV48.NotifyFarClipChangeV102(clipEnd);
            MelonLogger.Msg($"[StereoRender] Far clip changed to {clipEnd:F1}m without reallocating eye textures.");
        }

        // V105 settings-overlay apply hook. Reconfigure per-eye HDRP gates and
        // rebuild eye targets when render scale / DLSS state changed. Always reseed temporal state.
        internal void ApplyRuntimeSettingsV105(bool rebuildEyeTextures)
        {
            TryConfigureHdrpCameraSettings(LeftCam);
            TryConfigureHdrpCameraSettings(RightCam);
            if (rebuildEyeTextures)
                UpdateResolution();
            else
                ResetAerStateV34(this, AerCameraChangeWarmupFrames, "V105 runtime settings apply",
                    invalidateCompletedPoses: true, log: false);
            NvOpticalFlowV90.ResetForActivation();
        }

        // V111: retain V110 lift of only the rendered EYE viewpoints for title/loading by 0.30 m. The Head
        // transform itself stays put, so title/pause/menu panels and our head-locked overlays are
        // NOT translated with the world view. This directly addresses the splash camera sitting in
        // animated water without perturbing gameplay eye height or menu placement.
        internal void ApplyTitleLoadingEyeLiftV110()
        {
            if (LeftCam == null || RightCam == null) return;
            string scene = string.Empty;
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? string.Empty; } catch { }
            // V113: BlankScene can remain the active Unity scene after additive gameplay is already
            // alive. V112 therefore left the +0.30m loading lift stuck on during real gameplay.
            // Scene name is only a pre-game hint; the proven gameplay-ready gate always wins.
            bool preGameplayV113 = !AerGameplayReadyForActivation;
            bool titleOrLoadingSceneV113 = scene == "SonsTitleScene" || scene == "SonsMainLoading" || scene == "BlankScene";
            // V158: return-to-title inherits AerGameplayReadyForActivation=True
            // from the preceding session. Do not reset this AER flag; instead
            // explicitly recognize the title camera so V110's +0.30 m eye lift
            // works on BOTH initial startup and menu re-entry.
            bool explicitTitleV158 = VRPlayer.Instance != null &&
                VRPlayer.Instance.IsNativeTitleCameraContextV158;
            // V156: BlankScene + !AerGameplayReadyForActivation also describes the REAL
            // helicopter/crash cinematic. Applying the title/menu lift then raised both
            // stereo eyes by 30 cm above the game's intended camera position. Keep the
            // legacy lift exclusively for actual title/loading, never a cinematic view.
            bool cinematicV156 = VRPlayer.Instance != null &&
                VRPlayer.Instance.SuppressTitleEyeLiftForCinematicV156;
            float y = (((preGameplayV113 && titleOrLoadingSceneV113) || explicitTitleV158) &&
                !cinematicV156) ? 0.30f : 0f;
            // Keep the lift aligned with WORLD up even when the user pitches/rolls the HMD.
            Vector3 liftLocal = Head != null ? Head.InverseTransformVector(Vector3.up * y) : new Vector3(0f, y, 0f);
            LeftCam.transform.localPosition = new Vector3(-separation * 0.5f, 0f, 0f) + liftLocal;
            RightCam.transform.localPosition = new Vector3(separation * 0.5f, 0f, 0f) + liftLocal;
        }

        /// <summary>
        /// Fetches per-eye projection matrices directly from OpenVR, optionally narrowing the
        /// frustum via EyeFovTangentCropHorizontal/Vertical (1.0 = no crop).
        /// </summary>
        public void UpdateProjectionMatrix()
        {
            var leftProj = OpenVR.System.GetProjectionMatrix(EVREye.Eye_Left, clipStart, clipEnd).ConvertToMatrix4x4();
            var rightProj = OpenVR.System.GetProjectionMatrix(EVREye.Eye_Right, clipStart, clipEnd).ConvertToMatrix4x4();

            float cropX = Mathf.Clamp(EyeFovTangentCropHorizontal, 0.4f, 1f);
            float cropY = Mathf.Clamp(EyeFovTangentCropVertical, 0.4f, 1f);

            if (cropX < 0.999f)
            {
                leftProj.m00 /= cropX;
                rightProj.m00 /= cropX;
            }
            if (cropY < 0.999f)
            {
                leftProj.m11 /= cropY;
                rightProj.m11 /= cropY;
            }

            LeftCam.projectionMatrix = leftProj;
            RightCam.projectionMatrix = rightProj;
        }

        /// <summary>
        /// Render-resolution scale applied to both eye textures before submission to OpenVR.
        /// 1.0 = SteamVR's full recommended resolution (previous/default behavior). Lower values
        /// render genuinely fewer pixels per frame - real GPU savings - and OpenVR's own
        /// compositor automatically upscales the submitted texture to the headset's actual
        /// display resolution, exactly like every SteamVR game's built-in "Render Resolution"
        /// slider already does. Deliberately NOT using HDRP's own DynamicResolutionHandler API
        /// (which requires registering a scaler callback via reflection across IL2CPP, and has
        /// documented real-world reports of producing a broken image when misconfigured) - this
        /// approach is universal (works identically on any GPU vendor, not just NVIDIA) and uses
        /// only standard OpenVR behavior already proven safe. 0.8 as a starting test value.
        /// </summary>
        public static float RenderResolutionScale = 1.0f;

        /// <summary>
        /// Recreates left/right RenderTextures at the current recommended SteamVR resolution
        /// (scaled by RenderResolutionScale). Called at startup and when resolution changes.
        /// </summary>
        public void UpdateResolution()
        {
            _resolutionUpdateInProgress = true;

            // Use SteamVR's recommended eye texture size, with fallbacks
            // Note: SteamVR.instance is a lazy singleton that can trigger a full SteamVR/OpenVR
            // initialization on first access - logging before/after to check if THIS is where
            // a stall happens.
            int fullWidth = (SteamVR.instance.sceneWidth > 0) ? (int)SteamVR.instance.sceneWidth : 2208;
            int fullHeight = (SteamVR.instance.sceneHeight > 0) ? (int)SteamVR.instance.sceneHeight : 2452;
            lastKnownFullWidth = fullWidth;
            lastKnownFullHeight = fullHeight;

            float scale = Mathf.Clamp(RenderResolutionScale, 0.1f, 2.0f); // sanity clamp - never allow a nonsensical/zero/huge value to slip through

            // The FOV tangent crop (EyeFovTangentCropHorizontal/Vertical) narrows the actual
            // frustum in UpdateProjectionMatrix() - this is where that narrower view's texture
            // gets shrunk to match, proportionally. This is what actually saves GPU work; a
            // narrower frustum rendered into an unchanged texture size would just render the same
            // narrower view at a higher pixel density, saving nothing.
            float fovScaleX = Mathf.Clamp(EyeFovTangentCropHorizontal, 0.4f, 1f);
            float fovScaleY = Mathf.Clamp(EyeFovTangentCropVertical, 0.4f, 1f);

            currentWidth = Mathf.Max(1, Mathf.RoundToInt(fullWidth * scale * fovScaleX));
            currentHeight = Mathf.Max(1, Mathf.RoundToInt(fullHeight * scale * fovScaleY));

            // V95: passive resolution/bounds provenance probe.  This changes NO rendering state.
            // We need to distinguish three values which the old SteamVR Unity plugin can make
            // very different from one another:
            //   (1) OpenVR's raw GetRecommendedRenderTargetSize() result;
            //   (2) SteamVR.instance.sceneWidth/sceneHeight, which classic SteamVR.cs expands
            //       independently in X/Y to fit the combined asymmetric eye FOV into a shared
            //       scene texture;
            //   (3) the separate per-eye textures this mod actually submits with full 0..1
            //       texture bounds.
            // If (2) matches the classic expansion of (1) but we submit all of (2), that is
            // important evidence of a texture-bounds/projection-domain mismatch.
            LogResolutionProvenanceV95(fullWidth, fullHeight, currentWidth, currentHeight, scale, fovScaleX, fovScaleY);

            // V96: passive OpenVR optical-distortion geometry probe. No rendering state changes.
            // ComputeDistortion maps DISPLAY viewport UV -> SOURCE render-target UV. We sample a
            // 17x17 grid, finite-difference that mapping, invert the local 2x2 Jacobian, and log
            // how a one-source-pixel AER displacement would be magnified/rotated on display.
            LogOpticalDistortionGeometryV96(currentWidth, currentHeight);

            if (!Mathf.Approximately(scale, 1.0f) || !Mathf.Approximately(fovScaleX, 1.0f) || !Mathf.Approximately(fovScaleY, 1.0f))
            {
                MelonLogger.Msg($"[StereoRender] Rendering at {scale:P0} resolution scale x {fovScaleX:P0}/{fovScaleY:P0} FOV crop: {currentWidth}x{currentHeight} (full SteamVR-recommended size is {fullWidth}x{fullHeight}) - OpenVR's compositor will upscale to the headset's actual display resolution on submission.");
            }

            // Clean up old textures. Any V36 reprojection outputs are tied to this exact
            // resolution, so discard them too; they will be recreated lazily on first AER warp.
            ReleaseAerReprojectionTargetsV36(this);
            if (LeftRT != null) Destroy(LeftRT);
            if (RightRT != null) Destroy(RightRT);

            // Create new render textures. Clamp is important for V36's rotational warp: a fast
            // head turn can legitimately map a thin edge of the new view outside the stale eye's
            // old image. Repeating the opposite edge there would be much more objectionable than
            // clamping the nearest valid pixel.
            // V91: these remain full SteamVR presentation-size textures. HDRP/DLSS renders
            // internally at a lower active viewport and reconstructs into these full-size outputs.
            // useDynamicScale is essential: merely setting allowDynamicResolution on the Camera/
            // HDAdditionalCameraData is not enough if the external target itself refuses DRS.
            // MSAA is deliberately 1 here; HDRP DLSS/TAA owns temporal antialiasing/reconstruction.
            bool v91DynamicTarget = AerV91RealDlssEnabled && EnableV91RealDlss && EnableDlssOnEyeCameras;
            LeftRT = new RenderTexture(currentWidth, currentHeight, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = v91DynamicTarget ? 1 : 2,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useDynamicScale = v91DynamicTarget
            };
            RightRT = new RenderTexture(currentWidth, currentHeight, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = v91DynamicTarget ? 1 : 2,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useDynamicScale = v91DynamicTarget
            };

            // V116 DLSS truth breadcrumb: V115 runtime status unexpectedly reported useDynamicScale
            // false even while all three DLSS enable gates were true. Log immediately after RT
            // construction so we can distinguish "creation never accepted dynamic scaling" from
            // "some later path reset the flag" without changing rendering behavior.
            MelonLogger.Warning(
                $"[StereoRender][AER V116][DLSS][TargetBuild] requestedDynamic={v91DynamicTarget} " +
                $"gates=(AerV91={AerV91RealDlssEnabled},global={EnableV91RealDlss},eyes={EnableDlssOnEyeCameras}) " +
                $"leftDyn={LeftRT.useDynamicScale} rightDyn={RightRT.useDynamicScale} size={currentWidth}x{currentHeight} aa={LeftRT.antiAliasing}.");

            // Assign as render targets
            LeftCam.targetTexture = LeftRT;
            RightCam.targetTexture = RightRT;

            // New textures contain no previous-eye image, so AER MUST temporarily render both
            // eyes again. Otherwise the eye that happens not to be selected first would submit a
            // brand-new blank texture for a frame.
            ResetAerStateV34(this, AerWarmupRenderFrames, "resolution change", invalidateCompletedPoses: true, log: false);
            _resolutionUpdateInProgress = false;
        }

        /// <summary>
        /// Called when the component is destroyed.
        /// Cleans up the singleton reference.
        /// </summary>
        public void OnDestroy()
        {
            AerDepthStereoWarpV48.UninstallCameraDepthLatch(this);
            ReleaseAerReprojectionTargetsV36(this);
            if (_aerReprojectionMaterial != null)
            {
                Destroy(_aerReprojectionMaterial);
                _aerReprojectionMaterial = null;
            }
            Instance = null;
        }

        private static int _resolutionProvenanceLogsRemainingV95 = 4;

        private static void LogResolutionProvenanceV95(
            int pluginSceneWidth,
            int pluginSceneHeight,
            int actualEyeWidth,
            int actualEyeHeight,
            float renderScale,
            float fovScaleX,
            float fovScaleY)
        {
            if (_resolutionProvenanceLogsRemainingV95 <= 0 || OpenVR.System == null)
                return;

            try
            {
                uint rawWidth = 0;
                uint rawHeight = 0;
                OpenVR.System.GetRecommendedRenderTargetSize(ref rawWidth, ref rawHeight);

                float ll = 0f, lr = 0f, lt = 0f, lb = 0f;
                float rl = 0f, rr = 0f, rt = 0f, rb = 0f;
                OpenVR.System.GetProjectionRaw(EVREye.Eye_Left, ref ll, ref lr, ref lt, ref lb);
                OpenVR.System.GetProjectionRaw(EVREye.Eye_Right, ref rl, ref rr, ref rt, ref rb);

                float tanHalfX = Mathf.Max(Mathf.Max(-ll, lr), Mathf.Max(-rl, rr));
                float tanHalfY = Mathf.Max(Mathf.Max(-lt, lb), Mathf.Max(-rt, rb));

                float luMin = 0.5f + 0.5f * ll / tanHalfX;
                float luMax = 0.5f + 0.5f * lr / tanHalfX;
                float lvMin = 0.5f - 0.5f * lb / tanHalfY;
                float lvMax = 0.5f - 0.5f * lt / tanHalfY;

                float ruMin = 0.5f + 0.5f * rl / tanHalfX;
                float ruMax = 0.5f + 0.5f * rr / tanHalfX;
                float rvMin = 0.5f - 0.5f * rb / tanHalfY;
                float rvMax = 0.5f - 0.5f * rt / tanHalfY;

                float leftSpanX = Mathf.Abs(luMax - luMin);
                float rightSpanX = Mathf.Abs(ruMax - ruMin);
                float leftSpanY = Mathf.Abs(lvMax - lvMin);
                float rightSpanY = Mathf.Abs(rvMax - rvMin);
                float maxSpanX = Mathf.Max(leftSpanX, rightSpanX);
                float maxSpanY = Mathf.Max(leftSpanY, rightSpanY);

                float classicExpandedWidth = maxSpanX > 1e-6f ? rawWidth / maxSpanX : rawWidth;
                float classicExpandedHeight = maxSpanY > 1e-6f ? rawHeight / maxSpanY : rawHeight;

                float rawAspect = rawHeight > 0 ? (float)rawWidth / rawHeight : 0f;
                float pluginAspect = pluginSceneHeight > 0 ? (float)pluginSceneWidth / pluginSceneHeight : 0f;
                float actualAspect = actualEyeHeight > 0 ? (float)actualEyeWidth / actualEyeHeight : 0f;
                float growX = rawWidth > 0 ? pluginSceneWidth / (float)rawWidth : 0f;
                float growY = rawHeight > 0 ? pluginSceneHeight / (float)rawHeight : 0f;
                float predictedVsPluginX = pluginSceneWidth > 0 ? classicExpandedWidth / pluginSceneWidth : 0f;
                float predictedVsPluginY = pluginSceneHeight > 0 ? classicExpandedHeight / pluginSceneHeight : 0f;

                MelonLogger.Msg(
                    $"[StereoRender][AER V95][ResolutionProvenance] " +
                    $"rawOpenVR={rawWidth}x{rawHeight} aspect={rawAspect:F6}; " +
                    $"SteamVR.scene={pluginSceneWidth}x{pluginSceneHeight} aspect={pluginAspect:F6}; " +
                    $"actualEyeRT={actualEyeWidth}x{actualEyeHeight} aspect={actualAspect:F6}; " +
                    $"RenderScale={renderScale:F3} FovScale={fovScaleX:F3}x{fovScaleY:F3}; " +
                    $"scene/raw grow={growX:F6}x{growY:F6}.");

                MelonLogger.Msg(
                    $"[StereoRender][AER V95][ProjectionRaw] " +
                    $"L(l={ll:F7},r={lr:F7},t={lt:F7},b={lb:F7}) " +
                    $"R(l={rl:F7},r={rr:F7},t={rt:F7},b={rb:F7}) " +
                    $"tanHalfMax=({tanHalfX:F7},{tanHalfY:F7}).");

                MelonLogger.Msg(
                    $"[StereoRender][AER V95][ValveClassicBounds] " +
                    $"L[u={luMin:F7}..{luMax:F7},v={lvMin:F7}..{lvMax:F7}] " +
                    $"R[u={ruMin:F7}..{ruMax:F7},v={rvMin:F7}..{rvMax:F7}] " +
                    $"spanMax={maxSpanX:F7}x{maxSpanY:F7}; " +
                    $"raw/span => {classicExpandedWidth:F3}x{classicExpandedHeight:F3}; " +
                    $"predicted/plugin={predictedVsPluginX:F6}x{predictedVsPluginY:F6}.");

                MelonLogger.Msg(
                    "[StereoRender][AER V95][ActualSubmitBounds] BOTH eyes currently submit " +
                    "uMin=0 vMin=1 uMax=1 vMax=0 (entire texture with Unity/OpenVR V flip). " +
                    "V95 is MEASUREMENT ONLY; it does not substitute the ValveClassicBounds above.");

                _resolutionProvenanceLogsRemainingV95--;
            }
            catch (Exception e)
            {
                MelonLogger.Warning(
                    "[StereoRender][AER V95][ResolutionProvenance] passive probe failed: " +
                    e.GetType().Name + ": " + e.Message);
                _resolutionProvenanceLogsRemainingV95--;
            }
        }


        // ---------------------------------------------------------------------
        // V96 PASSIVE OPTICAL-GEOMETRY PROBE
        // ---------------------------------------------------------------------
        // OpenVR IVRSystem::ComputeDistortion() takes a coordinate in the eye's OUTPUT/display
        // viewport (0,0 = upper-left; 1,1 = lower-right) and returns the source render-target UV
        // sampled by the compositor, separately for R/G/B.  Therefore its differential is:
        //
        //     J_ds = d(sourceUV) / d(displayUV)
        //
        // What AER cares about is the inverse: if our source image is displaced by one pixel,
        // what DISPLAY displacement does that become after lens distortion?  V96 finite-
        // differences J_ds, inverts it, converts the inverse into pixel-equivalent coordinates,
        // and records local gain, anisotropy, and direction across a 17x17 grid per eye.
        //
        // This is diagnostic only. It does not alter camera matrices, resolution, Submit bounds,
        // DLSS, NVOFA, depth, or any compositor state.
        private const int DistortionGridSizeV96 = 17;
        private const float DistortionDerivativeStepV96 = 0.001f;
        private static bool _distortionGeometryProbeDoneV96 = false;

        private struct DistortionMetricsV96
        {
            public bool valid;
            public float displayU, displayV;
            public float sourceU, sourceV;
            public float j00, j01, j10, j11;      // d(source UV) / d(display UV)
            public float p00, p01, p10, p11;      // source-pixel error -> display-pixel-equivalent
            public float gainH, gainV;             // unit source horizontal/vertical pixel error
            public float gainMax, gainMin;         // singular values of pixel-space inverse map
            public float angleHDeg, angleVDeg;     // resulting direction, deg from +display X
            public float anisotropy;
            public float detSourcePerDisplay;
            public float chromaPx;
        }

        private struct DistortionBandAccumV96
        {
            public int n;
            public float sumGainH, sumGainV, sumGainMax, sumGainMin, sumAnisotropy;
            public float sumP00, sumP01, sumP10, sumP11;
            public float maxGainMax, minGainMax, maxChromaPx;

            public void Add(DistortionMetricsV96 m)
            {
                if (!m.valid) return;
                if (n == 0)
                {
                    minGainMax = m.gainMax;
                    maxGainMax = m.gainMax;
                }
                n++;
                sumGainH += m.gainH;
                sumGainV += m.gainV;
                sumGainMax += m.gainMax;
                sumGainMin += m.gainMin;
                sumAnisotropy += m.anisotropy;
                sumP00 += m.p00;
                sumP01 += m.p01;
                sumP10 += m.p10;
                sumP11 += m.p11;
                minGainMax = Mathf.Min(minGainMax, m.gainMax);
                maxGainMax = Mathf.Max(maxGainMax, m.gainMax);
                maxChromaPx = Mathf.Max(maxChromaPx, m.chromaPx);
            }
        }

        private static bool TryDistortionCoordinatesV96(
            EVREye eye,
            float u,
            float v,
            out DistortionCoordinates_t d)
        {
            d = new DistortionCoordinates_t();
            if (OpenVR.System == null) return false;
            try
            {
                return OpenVR.System.ComputeDistortion(
                    eye,
                    Mathf.Clamp01(u),
                    Mathf.Clamp01(v),
                    ref d);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryMeasureDistortionV96(
            EVREye eye,
            float u,
            float v,
            int sourceWidth,
            int sourceHeight,
            out DistortionMetricsV96 m)
        {
            m = new DistortionMetricsV96();
            m.displayU = u;
            m.displayV = v;

            if (sourceWidth <= 0 || sourceHeight <= 0) return false;

            DistortionCoordinates_t c, ux0, ux1, vy0, vy1;
            if (!TryDistortionCoordinatesV96(eye, u, v, out c)) return false;

            float h = DistortionDerivativeStepV96;
            float u0 = Mathf.Max(0f, u - h);
            float u1 = Mathf.Min(1f, u + h);
            float v0 = Mathf.Max(0f, v - h);
            float v1 = Mathf.Min(1f, v + h);
            if (u1 - u0 < 1e-7f || v1 - v0 < 1e-7f) return false;

            if (!TryDistortionCoordinatesV96(eye, u0, v, out ux0) ||
                !TryDistortionCoordinatesV96(eye, u1, v, out ux1) ||
                !TryDistortionCoordinatesV96(eye, u, v0, out vy0) ||
                !TryDistortionCoordinatesV96(eye, u, v1, out vy1))
                return false;

            // Green channel is the geometric center channel used for the local optical map.
            float invDu = 1f / (u1 - u0);
            float invDv = 1f / (v1 - v0);
            float j00 = (ux1.rfGreen0 - ux0.rfGreen0) * invDu;
            float j10 = (ux1.rfGreen1 - ux0.rfGreen1) * invDu;
            float j01 = (vy1.rfGreen0 - vy0.rfGreen0) * invDv;
            float j11 = (vy1.rfGreen1 - vy0.rfGreen1) * invDv;

            float det = j00 * j11 - j01 * j10;
            if (Mathf.Abs(det) < 1e-8f || float.IsNaN(det) || float.IsInfinity(det))
                return false;

            // Inverse normalized-UV map: d(displayUV) / d(sourceUV).
            float inv00 = j11 / det;
            float inv01 = -j01 / det;
            float inv10 = -j10 / det;
            float inv11 = j00 / det;

            // Convert inverse UV differential into source-resolution pixel-equivalent units:
            //     P = diag(W,H) * J^-1 * diag(1/W,1/H)
            // A vector (1,0) means one source X pixel of AER error; P*(1,0) tells us the
            // display displacement expressed in equivalent pixels at the same W x H domain.
            float wh = sourceWidth / (float)sourceHeight;
            float hw = sourceHeight / (float)sourceWidth;
            float p00 = inv00;
            float p01 = inv01 * wh;
            float p10 = inv10 * hw;
            float p11 = inv11;

            float gainH = Mathf.Sqrt(p00 * p00 + p10 * p10);
            float gainV = Mathf.Sqrt(p01 * p01 + p11 * p11);

            // Singular values of P: maximum/minimum amplification over ANY source-error direction.
            float a = p00 * p00 + p10 * p10;
            float b = p00 * p01 + p10 * p11;
            float cc = p01 * p01 + p11 * p11;
            float tr = a + cc;
            float disc = Mathf.Sqrt(Mathf.Max(0f, (a - cc) * (a - cc) + 4f * b * b));
            float lambdaMax = Mathf.Max(0f, 0.5f * (tr + disc));
            float lambdaMin = Mathf.Max(0f, 0.5f * (tr - disc));
            float gainMax = Mathf.Sqrt(lambdaMax);
            float gainMin = Mathf.Sqrt(lambdaMin);

            float angleH = Mathf.Atan2(p10, p00) * Mathf.Rad2Deg;
            float angleV = Mathf.Atan2(p11, p01) * Mathf.Rad2Deg;
            float anisotropy = gainMin > 1e-7f ? gainMax / gainMin : 9999f;

            // Chromatic separation at this point, in source-render pixels. Not directly an AER
            // error metric, but useful to identify strongly warped optical regions.
            float rgx = (c.rfRed0 - c.rfGreen0) * sourceWidth;
            float rgy = (c.rfRed1 - c.rfGreen1) * sourceHeight;
            float bgx = (c.rfBlue0 - c.rfGreen0) * sourceWidth;
            float bgy = (c.rfBlue1 - c.rfGreen1) * sourceHeight;
            float chromaPx = Mathf.Max(
                Mathf.Sqrt(rgx * rgx + rgy * rgy),
                Mathf.Sqrt(bgx * bgx + bgy * bgy));

            m.valid = true;
            m.sourceU = c.rfGreen0;
            m.sourceV = c.rfGreen1;
            m.j00 = j00; m.j01 = j01; m.j10 = j10; m.j11 = j11;
            m.p00 = p00; m.p01 = p01; m.p10 = p10; m.p11 = p11;
            m.gainH = gainH; m.gainV = gainV;
            m.gainMax = gainMax; m.gainMin = gainMin;
            m.angleHDeg = angleH; m.angleVDeg = angleV;
            m.anisotropy = anisotropy;
            m.detSourcePerDisplay = det;
            m.chromaPx = chromaPx;
            return true;
        }

        private static void LogDistortionBandV96(
            EVREye eye,
            string bandName,
            float vMin,
            float vMax,
            DistortionBandAccumV96 a)
        {
            string eyeName = eye == EVREye.Eye_Left ? "L" : "R";
            if (a.n <= 0)
            {
                MelonLogger.Warning(
                    $"[StereoRender][AER V96][DistortionBand] eye={eyeName} band={bandName} " +
                    $"displayV={vMin:F3}..{vMax:F3} valid=0");
                return;
            }

            float invN = 1f / a.n;
            MelonLogger.Msg(
                $"[StereoRender][AER V96][DistortionBand] eye={eyeName} band={bandName} " +
                $"displayV={vMin:F3}..{vMax:F3} n={a.n}; " +
                $"gainH.mean={a.sumGainH * invN:F6} gainV.mean={a.sumGainV * invN:F6}; " +
                $"gainMax.mean={a.sumGainMax * invN:F6} range={a.minGainMax:F6}..{a.maxGainMax:F6}; " +
                $"gainMin.mean={a.sumGainMin * invN:F6} anis.mean={a.sumAnisotropy * invN:F6}; " +
                $"pixelMap.mean=[[{a.sumP00 * invN:F6},{a.sumP01 * invN:F6}]," +
                $"[{a.sumP10 * invN:F6},{a.sumP11 * invN:F6}]]; chroma.maxPx={a.maxChromaPx:F3}");
        }

        private static void LogOpticalDistortionGeometryEyeV96(
            EVREye eye,
            int sourceWidth,
            int sourceHeight)
        {
            string eyeName = eye == EVREye.Eye_Left ? "L" : "R";
            int nGrid = DistortionGridSizeV96;
            int valid = 0;
            DistortionBandAccumV96 top = new DistortionBandAccumV96();
            DistortionBandAccumV96 middle = new DistortionBandAccumV96();
            DistortionBandAccumV96 bottom = new DistortionBandAccumV96();

            MelonLogger.Msg(
                $"[StereoRender][AER V96][DistortionEyeBegin] eye={eyeName} grid={nGrid}x{nGrid} " +
                $"sourceRT={sourceWidth}x{sourceHeight}; displayUV convention: (0,0)=UPPER-LEFT, " +
                $"(1,1)=LOWER-RIGHT; ComputeDistortion is displayUV->sourceUV; logged pixelMap is inverse.");

            for (int iy = 0; iy < nGrid; ++iy)
            {
                float v = iy / (float)(nGrid - 1);
                string rowGain = "";
                string rowVert = "";
                string rowCross = "";

                for (int ix = 0; ix < nGrid; ++ix)
                {
                    float u = ix / (float)(nGrid - 1);
                    DistortionMetricsV96 m;
                    bool ok = TryMeasureDistortionV96(eye, u, v, sourceWidth, sourceHeight, out m);
                    if (!ok)
                    {
                        rowGain += (ix == 0 ? "" : ",") + "nan";
                        rowVert += (ix == 0 ? "" : ",") + "nan";
                        rowCross += (ix == 0 ? "" : ",") + "nan";
                        continue;
                    }

                    valid++;
                    if (v < 1f / 3f) top.Add(m);
                    else if (v < 2f / 3f) middle.Add(m);
                    else bottom.Add(m);

                    rowGain += (ix == 0 ? "" : ",") + m.gainMax.ToString("F4");
                    rowVert += (ix == 0 ? "" : ",") + m.gainV.ToString("F4");
                    // p10 is vertical display displacement caused by a horizontal source-pixel
                    // error; p01 is horizontal displacement caused by a vertical source error.
                    // Logging both as p10/p01 compactly exposes local rotation/shear.
                    rowCross += (ix == 0 ? "" : ",") + m.p10.ToString("F4") + "/" + m.p01.ToString("F4");

                    MelonLogger.Msg(
                        $"[StereoRender][AER V96][DistortionCell] eye={eyeName} ix={ix} iy={iy} " +
                        $"display=({u:F4},{v:F4}) sourceG=({m.sourceU:F6},{m.sourceV:F6}) " +
                        $"Jds=[[{m.j00:F6},{m.j01:F6}],[{m.j10:F6},{m.j11:F6}]] " +
                        $"pixelInv=[[{m.p00:F6},{m.p01:F6}],[{m.p10:F6},{m.p11:F6}]] " +
                        $"gainH={m.gainH:F6} gainV={m.gainV:F6} " +
                        $"gainMin={m.gainMin:F6} gainMax={m.gainMax:F6} anis={m.anisotropy:F6} " +
                        $"angleH={m.angleHDeg:F3}deg angleV={m.angleVDeg:F3}deg " +
                        $"detJ={m.detSourcePerDisplay:F7} chromaPx={m.chromaPx:F3}");
                }

                MelonLogger.Msg(
                    $"[StereoRender][AER V96][DistortionRow] eye={eyeName} iy={iy} displayV={v:F4} " +
                    $"gainMax=[{rowGain}] gainV=[{rowVert}] cross(p10/p01)=[{rowCross}]");
            }

            LogDistortionBandV96(eye, "TOP", 0f, 1f / 3f, top);
            LogDistortionBandV96(eye, "CENTER", 1f / 3f, 2f / 3f, middle);
            LogDistortionBandV96(eye, "BOTTOM", 2f / 3f, 1f, bottom);

            MelonLogger.Msg(
                $"[StereoRender][AER V96][DistortionEyeEnd] eye={eyeName} " +
                $"valid={valid}/{nGrid * nGrid}. V96 is MEASUREMENT ONLY; no optical correction was applied.");
        }

        private static void LogOpticalDistortionGeometryV96(int sourceWidth, int sourceHeight)
        {
            if (_distortionGeometryProbeDoneV96 || OpenVR.System == null) return;
            _distortionGeometryProbeDoneV96 = true;

            try
            {
                MelonLogger.Msg(
                    $"[StereoRender][AER V96][DistortionProbeBegin] sourceRT={sourceWidth}x{sourceHeight}; " +
                    $"grid={DistortionGridSizeV96}x{DistortionGridSizeV96}/eye; derivativeStep={DistortionDerivativeStepV96:F6}. " +
                    "This is a passive startup-only probe.");

                LogOpticalDistortionGeometryEyeV96(EVREye.Eye_Left, sourceWidth, sourceHeight);
                LogOpticalDistortionGeometryEyeV96(EVREye.Eye_Right, sourceWidth, sourceHeight);

                MelonLogger.Msg(
                    "[StereoRender][AER V96][DistortionProbeEnd] complete. " +
                    "Interpretation: gainMax>1 means some one-source-pixel error direction is magnified on display; " +
                    "pixelInv off-diagonals/p10,p01 reveal direction rotation/shear. No render state changed.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning(
                    "[StereoRender][AER V96][DistortionProbe] passive probe failed: " +
                    e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// Checks every physics frame if the recommended render resolution has changed,
        /// and updates textures if needed (e.g., due to dynamic resolution or supersampling changes).
        /// </summary>
        public void FixedUpdate()
        {
            // FIX: compare against the last-known FULL (unscaled) SteamVR resolution, not our
            // own currentWidth/currentHeight - those are now intentionally smaller than SteamVR's
            // reported size whenever RenderResolutionScale < 1.0, which would otherwise make this
            // check permanently think a "resolution change" is pending and rebuild both textures
            // every single physics frame, forever.
            // Allow a small tolerance (-1 pixel) to avoid unnecessary updates
            if (lastKnownFullWidth < (int)SteamVR.instance.sceneWidth - 1 ||
                lastKnownFullHeight < (int)SteamVR.instance.sceneHeight - 1)
            {
                MelonLogger.Msg($"[StereoRender] SteamVR's recommended resolution changed: {lastKnownFullWidth}x{lastKnownFullHeight} -> {(int)SteamVR.instance.sceneWidth}x{(int)SteamVR.instance.sceneHeight} - recreating textures.");
                UpdateResolution();
            }
        }

        /// <summary>
        /// Updates the HMD orientation from OpenVR and refreshes projection matrices.
        /// Called after all Update() methods, ensuring camera pose is up-to-date before rendering.
        /// </summary>
        public void LateUpdate()
        {
            _aerStereoLateUpdateFrame = Time.frameCount;

            // V91: Sons' own DLSS manager can initialize after the VR rig. Retry fail-soft until
            // available, then keep sparse status logs so actual camera eligibility is observable.
            DlssRuntimeV91.Tick(this);
            AerDepthStereoWarpV48.PollForensicsReadbacksV99();

            // V81e: only after the real gameplay camera/player/HDRP baseline exists.
            // Re-ensuring once/sec also survives any scene/object lifetime churn.
            EnsureGameplayDepthProbeV81e();

            // V37 is a deliberately aggressive diagnostic: before HDRP gets to this frame's eye
            // renders, neutralize the source Volume components that can create temporal trails,
            // optical blur/distortion, or other screen-space "cinematic" image history. This is
            // independent of the game's menu settings and therefore gives us a trustworthy test.
            AerDiagnosticsV37.ForceCinematicVolumeEffectsOff(this);

            // The cameras enabled during the PREVIOUS LateUpdate have now gone through Unity's
            // render phase, so their scheduled pose finally describes real pixels in the RT(s).
            CommitPreviouslyScheduledAerRenderV34(this);

            // Fetch latest device poses from OpenVR compositor. WaitGetPoses is also the normal
            // compositor synchronization point; keep exactly one call per application frame.
            // V106 times the CPU block explicitly so a SteamVR cadence/throttle wait can be
            // distinguished from game CPU work and from the compositor's relative timing field.
            long v106WaitStart = System.Diagnostics.Stopwatch.GetTimestamp();
            OpenVR.Compositor.WaitGetPoses(renderPoseArray, gamePoseArray);
            long v106WaitEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            PerformanceForensicsV106.RecordWaitGetPoses(
                (float)((v106WaitEnd - v106WaitStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency));

            // Apply HMD rotation to the Head transform. This fork intentionally ignores tracked
            // HMD translation and handles world position through its own Origin transform; AER's
            // submitted render pose mirrors that choice below by zeroing pose translation.
            var hmdPose = renderPoseArray[(int)OpenVR.k_unTrackedDeviceIndex_Hmd];
            if (hmdPose.bPoseIsValid)
            {
                Head.localRotation = hmdPose.mDeviceToAbsoluteTracking.GetRotation();
                _aerLatestTrackingPose = BuildAerRenderPoseV33(hmdPose.mDeviceToAbsoluteTracking);
                _aerLatestTrackingPoseValid = true;
                _aerLatestTrackingPoseFrame = Time.frameCount;
            }
            else
            {
                _aerLatestTrackingPoseValid = false;
            }

            // Refresh projection matrices (they can change with IPD or calibration) - the frustum
            // FOV crop (EyeFovTangentCropHorizontal/Vertical) is applied directly inside this
            // call now, no separate step needed.
            UpdateProjectionMatrix();

            // Future config/overlay work can flip AlternateEyeRenderingEnabled live. Detect that
            // transition cleanly even though V33 still hardcodes the test default at the top.
            if (_aerLastEnabledState != AlternateEyeRenderingEnabled)
            {
                ResetAerStateV34(this, AerWarmupRenderFrames,
                    AlternateEyeRenderingEnabled ? "AER enabled" : "AER disabled",
                    invalidateCompletedPoses: AlternateEyeRenderingEnabled, log: true);
            }

            ScheduleAerRenderForThisFrameV34(this, hmdPose);
            AerDiagnosticsV37.LogPoseAndEffectiveHdrpState(this);
        }

        // ---------------------------------------------------------------------
        // ZER0INT V38 - END-OF-FRAME AER SUBMISSION
        // ---------------------------------------------------------------------
        // Keep the coroutine/helper implementation in a nested managed helper rather than adding
        // another IEnumerator-returning method directly to the injected StereoRender MonoBehaviour.
        // V33 demonstrated that shrinking StereoRender's IL2CPP registration surface is worth it.
        private static class AerSubmissionV38
        {
            private static float _nextEndOfFrameTelemetryTime;

            // ZER0INT V74: retain the safe pre-render HUD transform refresh, but remove the obsolete
            // V67/V68 15-second A/B control delay. Activation remains EOF-safe.

            internal static System.Collections.IEnumerator ActivateAerAfterEndOfFrame(StereoRender self)
            {
                // IMPORTANT: do not flip AER in VRPlayer/Update/LateUpdate. MelonMod.OnLateUpdate
                // is known to run before StereoRender.LateUpdate, and changing the flag mid-frame
                // can otherwise produce either a double submit or a missing submit depending on
                // callback order. V74 activates at the first safe EOF once gameplay HDRP is ready;
                // the old 15-second causality window is intentionally gone.
                yield return new WaitForEndOfFrame();

                if (self == null || Instance != self)
                    yield break;

                if (!AerDeferredGameplayActivationEnabled || !AerGameplayReadyForActivation)
                    yield break;

                // V74: the old V67/V68 15-second causality window has served its purpose and
                // must not survive into normal HUD work. We are already at an EOF boundary here,
                // so activate immediately and let the existing 4-frame V38B resync/warmup run.
                MelonLogger.Warning(
                    $"[StereoRender][V112][Activation] gameplay HDRP gate passed; applying requested gameplay mode " +
                    $"{(UserGameplayAERV112 ? "AER" : "2E")} at this EOF. Title/loading remain conventional 2E.");

                if (AerEndOfFrameSubmissionEnabled && !self._aerEndOfFrameLoopStarted)
                {
                    MelonLoader.MelonCoroutines.Start(EndOfFrameSubmitLoop(self));
                    self._aerEndOfFrameLoopStarted = true;
                }

                // Fresh telemetry epoch for this deferred gameplay activation. Do NOT gate it on
                // elapsed seconds: collect immediately and continuously. The first crafting-tarp
                // open later marks/zeros the trusted gameplay epoch without touching AER itself.
                AerGameplaySpatialTelemetryTarpMarkerSeen = false;
                AerGameplaySpatialTelemetryV44.ResetForActivation(self);
                AerDepthAccessProbeV46.ResetForActivation(self);
                AerDepthShaderDebugV47.ResetForActivation();
                AerDepthStereoWarpV48.ResetForActivation();

                // V83/V112: do NOT resurrect V36. AER, when selected, uses only the production
                // same-eye 6DoF temporal path. 2E simply leaves both real eye cameras enabled.
                AerRotationalReprojectionEnabled = false;
                AlternateEyeRenderingEnabled = UserGameplayAERV112;

                MelonLogger.Warning(
                    $"[StereoRender][V112] GAMEPLAY MODE ACTIVE: {(AlternateEyeRenderingEnabled ? "AER" : "2E/full stereo")}. " +
                    "AER path, when active: SAME-EYE temporal reprojection + NVOFA residual + V103 depth guard + " +
                    "V100 rotation-only TRUE-hole fallback. Heavy forensic paths remain OFF.");
                MelonLogger.Warning(
                    $"[StereoRender][AER V105][DepthGuard] absTol={AerV103FillDepthAbsToleranceMeters:F3}m " +
                    $"relTol={AerV103FillDepthRelativeTolerance:P1} minSupportFraction={AerV103FillMinSupportFraction:P0} " +
                    $"maxTol={AerV105FillDepthMaxToleranceMeters:F2}m minSupportCount={AerV103FillMinSupportCount}. Guard candidates come ONLY from original direct scatter; " +
                    "radius-1 fabricated pixels never seed radius 2. Guard-resource failure => NO FILL + V100 rotation fallback, never old unguarded fill.");
            }

            internal static System.Collections.IEnumerator EndOfFrameSubmitLoop(StereoRender self)
            {
                // V37 established the actual callback order on this build: MelonMod.OnLateUpdate
                // always runs before StereoRender.LateUpdate. Therefore the old submit path cannot
                // see the eye rendered later in the same frame. Under AER that produced the measured
                // 1f/2f pair. This loop intentionally has NO fallback that submits such an old pair:
                // an EOF submission is allowed only after a schedule from THIS SAME Unity frame has
                // been committed, which is the invariant needed for fresh=0f / stale=1f.
                while (true)
                {
                    yield return new WaitForEndOfFrame();

                    // Each VR rig gets its own loop. Stop the old one cleanly when a scene/camera
                    // swap destroys or supersedes this StereoRender instance.
                    if (self == null || Instance != self)
                        yield break;

                    if (!self.enabled || !AlternateEyeRenderingEnabled || !AerEndOfFrameSubmissionEnabled)
                        continue;

                    // Defensive guard: never submit twice from this EOF loop in the same Unity frame.
                    if (self._aerLastEndOfFrameSubmitFrame == Time.frameCount)
                        continue;

                    if (!CommitCurrentScheduledRender(self))
                    {
                        if (self._aerEndOfFrameMissLogsRemaining > 0)
                        {
                            self._aerEndOfFrameMissLogsRemaining--;
                            MelonLogger.Warning($"[StereoRender][AER V38B][EndOfFrame] no SAME-FRAME completed schedule to submit at frame={Time.frameCount}; scheduledFrame={self._aerScheduledFrame} valid={self._aerScheduledPoseValid}. Skipping submit instead of falling back to the known-bad 1f/2f path. Logs remaining={self._aerEndOfFrameMissLogsRemaining}.");
                        }
                        continue;
                    }

                    self._aerLastEndOfFrameSubmitFrame = Time.frameCount;
                    LogEndOfFrameAges(self);

                    // V99 truth frames deliberately render BOTH eyes. Their stale histories were
                    // snapshotted before rendering, so build offline diagnostic predictions now and
                    // submit the two genuine current eyes. Ordinary AER frames keep the normal stale-eye
                    // synthesis path below.
                    bool v99TruthFrame = AerDepthStereoWarpV48.RunTruthDiagnosticsIfNeededV99(self);
                    if (!v99TruthFrame)
                        AerDepthStereoWarpV48.PrepareSameEyeTemporalPairV83(self, self._aerLastCompletedEye);

                    if (self.stereoRenderPass != null)
                        self.stereoRenderPass.Execute();

                    // V139C: sparse submit heartbeat, including the setup countdown.
                    // Helps distinguish an AER/SteamVR stall from the GPU calibration queue.
                    AerCalibrationV139.RenderHeartbeatV139C();

                    // V139C: queue at most one readback-fenced diagnostic candidate AFTER
                    // actual stereo submission, never during the sensitive render boundary.
                    AerDepthStereoWarpV48.TickStagedTruthV139C();

                    // V85: no desktop GPU copies/readbacks in the clean temporal performance test.
                    if (AerV84DesktopDebugEnabled)
                    {
                        AerDepthStereoWarpV48.CaptureDebugAfterSubmit(self);
                        AerDepthShaderDebugV47.RenderAfterSubmit(self, self._aerLastCompletedEye);
                    }
                }
            }

            private static bool CommitCurrentScheduledRender(StereoRender self)
            {
                if (!self._aerScheduledPoseValid) return false;

                // WaitForEndOfFrame resumes after this frame's enabled eye camera(s) have rendered.
                // Refuse to bind an older schedule to the current textures: if the frame tags do not
                // match, skip this compositor submit and let SteamVR retain/reproject its previous
                // frame. That is safer and diagnostically cleaner than silently restoring 1f/2f AER.
                if (self._aerScheduledFrame != Time.frameCount) return false;

                // V44 observes the stale eye BEFORE replacing any stored render snapshot with
                // this frame's fresh render. Warmup/resync frames render BOTH eyes, so there is no
                // stale-eye sample on those frames; they simply refresh both snapshots.
                AerGameplaySpatialTelemetryV44.OnCompletedRenderAtEof(self, self._aerScheduledEye);
                AerDepthAccessProbeV46.OnCompletedRenderAtEof(self, self._aerScheduledEye);

                if (self._aerScheduledEye == -1 || self._aerScheduledEye == 0)
                {
                    self._aerLeftRenderPose = self._aerScheduledPose;
                    self._aerLeftRenderPoseValid = true;
                    self._aerLeftRenderFrame = self._aerScheduledFrame;
                }
                if (self._aerScheduledEye == -1 || self._aerScheduledEye == 1)
                {
                    self._aerRightRenderPose = self._aerScheduledPose;
                    self._aerRightRenderPoseValid = true;
                    self._aerRightRenderFrame = self._aerScheduledFrame;
                }

                self._aerLastCompletedEye = self._aerScheduledEye;
                self._aerScheduledPoseValid = false;
                self._aerScheduledFrame = -1;
                return true;
            }

            private static void LogEndOfFrameAges(StereoRender self)
            {
                if (Time.unscaledTime < _nextEndOfFrameTelemetryTime) return;
                _nextEndOfFrameTelemetryTime = Time.unscaledTime + 2.0f;

                int leftAge = self._aerLeftRenderFrame >= 0 ? Time.frameCount - self._aerLeftRenderFrame : -1;
                int rightAge = self._aerRightRenderFrame >= 0 ? Time.frameCount - self._aerRightRenderFrame : -1;
                string completed = self._aerLastCompletedEye == 0 ? "L" : self._aerLastCompletedEye == 1 ? "R" : self._aerLastCompletedEye == -1 ? "BOTH" : "none";
                MelonLogger.Msg($"[StereoRender][AER V38B][EndOfFrame] frame={Time.frameCount} completed={completed} Lage={leftAge}f Rage={rightAge}f latestPoseFrame={self._aerLatestTrackingPoseFrame} stereoLateFrame={self._aerStereoLateUpdateFrame}");
            }
        }

        // V139: strictly readback-drained bank transition. The V99 internals remain
        // private to StereoRender; calibration controller cannot modify render history.
        internal static bool TryBeginCalibrationBankV139(int samples)
        {
            // Camera-local motion resources were previously ensured during startup only
            // when V99 was enabled.  V139 starts later after the 180s setup timer.
            if (AerV99CaptureHdrpMotionVectors)
            {
                StereoRender stereo = VRPlayer.Instance != null ? VRPlayer.Instance.StereoRender : null;
                if (stereo != null && stereo.LeftCam != null && stereo.RightCam != null)
                    EyeMotionCaptureCustomPassV89.EnsureVolumes(stereo.LeftCam, stereo.RightCam);
            }
            return AerDepthStereoWarpV48.TryBeginCalibrationBankV139(samples);
        }

        // V81 bridge: retain ownership of the embedded shader bundle inside V50/V51,
        // but allow the injected camera-local CustomPass to create its own materials.
        internal static bool TryGetV81DepthCaptureShader(out Shader shader)
        {
            return AerDepthStereoWarpV48.TryGetSecondaryDiagnosticShaderV81(out shader);
        }

        // ---------------------------------------------------------------------
        // ZER0INT V51 - CAMERA-LOCAL DEPTH + TARGET-EYE HOLE FALLBACK + GPU SAME-TIME REPROJECTION
        // ---------------------------------------------------------------------
        // V49 proved that the geometry warp itself can synthesize useful stereo, but a graphics-
        // setting experiment exposed a much deeper bug: reading the globally-bound HDRP depth
        // pyramid at EOF lets unrelated camera / RTHandle state contaminate the warp. V50 fixes
        // provenance instead of tuning around the symptom.
        //
        // RenderPipelineManager.endCameraRendering tells us WHICH camera just finished. When that
        // camera is VRLeftEye or VRRightEye and gameplay AER is active, V50 immediately copies
        // full-resolution mip 0 from the then-current HDRP depth pyramid into an owned RFloat RT
        // and snapshots that source eye's projection, cameraToWorld, Z-buffer params, dimensions,
        // frame id, and camera id. EOF synthesis refuses to use anything else.
        //
        // Each steady-state AER frame therefore becomes:
        //   identified fresh eye render(t)
        //          -> endCameraRendering: COPY eye-local raw depth + snapshot source matrices
        //          -> EOF: exact-frame latch + source color(t)
        //          -> reconstruct source camera XYZ
        //          -> source cameraToWorld
        //          -> CURRENT opposite-eye worldToCamera + projection
        //          -> forward rasterize into synthetic opposite eye(t)
        //
        // The V49 512x512 confidence/fill/composite path is otherwise retained. If the latch is
        // missing, stale, from the wrong camera, or dimensionally inconsistent, V50 refuses to
        // V83 predicts the stale eye from its own prior real render. If unavailable, submission
        // falls back only to raw retained L/R textures; V36 is hard-disabled.
        private static class AerDepthStereoWarpV48
        {
            private const string BundleResourceName = "SonsVRMod.Resources.sonsvr_depththingy";
            private const string ShaderAssetPath = "Assets/DepthStereoWarp.shader";
            private const string HudDepthProxyShaderAssetPathV72 = "Assets/HudDepthProxyV72.shader";
            private const string TemporalScatterComputeAssetPathV88 = "Assets/TemporalScatterV89.compute";
            private const string OpticalFlowLumaShaderAssetPathV90 = "Assets/OpticalFlowLumaV90.shader";
            private const string AerForensicsComputeAssetPathV99 = "Assets/AerForensicsV99.compute";
            private const string HudDiscMeshAssetPathV73 = "Assets/HudProxyBaked/disc_proxy.asset";
            private const string HudRingMeshAssetPathV73 = "Assets/HudProxyBaked/ring_proxy.asset";
            private const string HudRectMeshAssetPathV73 = "Assets/HudProxyBaked/rect_proxy.asset";

            private static Il2CppAssetBundle _bundle;
            private static Shader _shader;
            private static Material _material;
            private static Shader _hudDepthProxyShaderV72;
            private static Material _hudDepthProxyMaterialV72;
            private static ComputeShader _temporalScatterComputeV88;
            private static Shader _opticalFlowLumaShaderV90;
            private static ComputeShader _forensicsComputeV99;
            private static int _forensicsBlockMatchKernelV99 = -1;
            private static int _forensicsTileStatsKernelV99 = -1;
            private static int _forensicsWarpFieldKernelV99 = -1;
            private static int _forensicsMotionFieldKernelV99 = -1;
            private static int _forensicsOpticalFlowFieldKernelV99 = -1;
            private static int _forensicsDepthFieldKernelV99 = -1;
            private static int _forensicsCoverageFieldKernelV99 = -1;
            private static int _forensicsSceneStructureKernelV99 = -1;
            private static int _forensicsDepthClassStatsKernelV101 = -1;
            private static int _forensicsSkyRayClassKernelV101 = -1;
            private static int _forensicsOcclusionClassStatsKernelV102 = -1;
            private static int _forensicsOcclusionComplexityKernelV102 = -1;
            private static int _forensicsDepthColorRiskKernelV102 = -1;
            private static int _scatterKernelClearV88 = -1;
            private static int _scatterKernelDepthV88 = -1;
            private static int _scatterKernelResolveV88 = -1;
            private static Mesh _hudDiscMeshV73;
            private static Mesh _hudRingMeshV73;
            private static Mesh _hudRectMeshV73;
            private static bool _hudExactMeshesAvailableV73;
            private static Mesh _warpMesh;

            private static RenderTexture _syntheticLeftRT;
            private static RenderTexture _syntheticRightRT;
            private static RenderTexture _warpLeftRT;
            private static RenderTexture _warpRightRT;
            private static RenderTexture _fillLeftRT;
            private static RenderTexture _fillRightRT;

            // V88 compute-scatter scratch. Shared between eyes because only one stale eye
            // is reconstructed on any Unity frame. Both are R32_UInt UAVs:
            // depthKey = atomic target z-buffer, sourceIndex = winning source pixel XY.
            private static RenderTexture _scatterDepthKeyRTV88;
            private static RenderTexture _scatterSourceIndexRTV88;
            private static RenderTexture _dummyOpticalFlowRTV90;

            private sealed class EyeDepthLatch
            {
                internal RenderTexture DepthRT;
                internal bool Valid;
                internal int Eye = -1;
                internal int Frame = -1;
                internal int CameraInstanceId;
                internal int SourceTargetInstanceId;
                internal int SourceWidth;
                internal int SourceHeight;
                internal int GlobalAtlasWidth;
                internal int GlobalAtlasHeight;
                internal bool UsedCustomPassDepthV82;
                internal int CustomPassDepthFrameV82 = -1;
                internal Vector4 ZParams;
                internal Vector4 RtHandleScale;
                internal Vector4 ScreenSize;
                internal Matrix4x4 SourceInvGpuProjection;
                internal Matrix4x4 SourceCameraToWorld;
                internal Matrix4x4 SourceWorldToCamera;
                internal Matrix4x4 SourceProjection;
                internal Matrix4x4 TargetWorldToCamera;
                internal Matrix4x4 TargetProjection;
                internal Matrix4x4 TargetGpuProjection;

                // V94 exact HDRP raster-matrix truth, captured in the same CustomPass
                // execution that owns cameraDepthBuffer for this REAL eye render.
                internal bool HdrpRasterMatricesValidV94;
                internal int HdrpRasterMatrixFrameV94 = -1;
                internal Matrix4x4 HdrpProjectionV94;
                internal Matrix4x4 HdrpWorldToCameraRHSV94;
                internal Matrix4x4 CameraGpuProjectionAtDepthV94;
                internal Matrix4x4 CameraWorldToCameraAtDepthV94;
                internal Matrix4x4 CameraProjectionAtDepthV94;
                internal Matrix4x4 CameraNonJitteredProjectionAtDepthV94;

                // V89 motion history paired to this exact REAL eye render. MotionRTV89 is
                // owned by the per-eye BeforePreRefraction CustomPass and therefore remains
                // stable until this physical eye renders again. PreviousReal* stores the
                // preceding REAL render of the same eye so compute can subtract camera motion
                // from HDRP's total vector before extrapolating object motion.
                internal RenderTexture MotionRTV89;
                internal bool UsedCustomPassMotionV89;
                internal int CustomPassMotionFrameV89 = -1;
                internal bool PreviousRealCameraValidV89;
                internal int PreviousRealFrameV89 = -1;
                internal Matrix4x4 PreviousRealWorldToCameraV89;
                internal Matrix4x4 PreviousRealGpuProjectionV89;
                internal float PreviousRealCaptureUnscaledTimeV90 = -1f;

                internal float CaptureUnscaledTime = -1f;
                internal Vector3 SourcePosition;
                internal Quaternion SourceRotation = Quaternion.identity;
                internal string CameraName = "?";
                // V139C: immutable metadata snapshot for delayed, time-sliced truth analysis.
                internal EyeDepthLatch SnapshotMetadataV139C() => (EyeDepthLatch)MemberwiseClone();
            }

            private static readonly EyeDepthLatch _leftDepthLatch = new EyeDepthLatch();
            private static readonly EyeDepthLatch _rightDepthLatch = new EyeDepthLatch();
            private static StereoRender _depthLatchOwner;
            private static bool _depthLatchCallbackInstalled;
            // IL2CPP note: RenderPipelineManager.endCameraRendering is exposed by the generated
            // Unity bindings as an Il2CppSystem.Action event. C# method-group +=/-= syntax does
            // not bind directly here, so keep ONE converted delegate instance and use the
            // generated add_/remove_ accessors explicitly. Keeping the same instance is also
            // important for reliable removal across rig respawns.
            private static Il2CppSystem.Action<ScriptableRenderContext, Camera> _endCameraRenderingDelegateV50;
            // V60: hide only successfully snapshotted gameplay UGUI immediately before the
            // scheduled AER eye renders, leaving a world-only source RT for V51.
            private static Il2CppSystem.Action<ScriptableRenderContext, Camera> _beginCameraRenderingDelegateV60;
            // V138: sparse, metadata-only calibration evidence. No GPU readback, no changes to
            // the V103 guarded fill, camera/depth matrices, NVOFA, or presentation.
            private static readonly float[] _v138NextRasterLog = new float[2];
            private static readonly int[] _v138PreviousDepthWidth = { -1, -1 };
            private static readonly int[] _v138PreviousDepthHeight = { -1, -1 };
            private static bool _firstLeftLatchLogged;
            private static bool _firstRightLatchLogged;
            private static bool _renderBoundaryHudSyncLoggedV66;
            // V68: one SAFE HUD transform refresh per Unity frame, before the first VR eye
            // renders, in BOTH full-stereo and AER modes. This removes the V67 confound where
            // enabling AER also enabled the pre-render HUD sync for the first time.
            private static int _renderBoundaryHudSyncFrameV68 = -1;
            private static bool _renderBoundaryHudSyncLoggedV68;
            private static int _depthLatchFailureLogsRemaining = 16;

            // V69 HUD-plane-depth causality probe state.
            private static float _hudPlaneProbeActivationTimeV69 = -1f;
            private static int _hudPlaneProbePhaseV69 = -1; // 0=baseline, 1=override, 2=reverted
            private static bool _hudPlaneProbeLoggedLeftRectsV69;
            private static bool _hudPlaneProbeLoggedRightRectsV69;

            // V72 depth-proxy diagnostics. One log per eye is enough; the proxy geometry itself
            // is recomputed every rendered eye because the source eye alternates under AER.
            private static bool _hudDepthProxyLoggedLeftV72;
            private static bool _hudDepthProxyLoggedRightV72;
            private static bool _denseHudCompositeLoggedV76;
            private static bool _viewportSanitizerLoggedV77;
            private static int _hudDepthProxyFailureLogsRemainingV72 = 8;

            // Small desktop-only copies, updated only after OpenVR has already consumed the pair.
            private static RenderTexture _debugSourceRT;
            private static RenderTexture _debugSyntheticRT;
            private static RenderTexture _debugDepthRT;
            private static RenderTexture _debugOldStaleRT;

            // V83 6-panel desktop board: two additional same-frame truth panels.
            // LOCAL = raw CustomPassContext.cameraDepthBuffer copied before HUD depth proxies.
            // GLOBAL = _CameraDepthTexture decoded exactly like V47's lower-left mip-0 candidate.
            private static RenderTexture _debugTruthLocalRTV83;
            private static RenderTexture _debugTruthGlobalMip0RTV83;
            private static float _nextDepthTruthCaptureTimeV83;
            private static int _depthTruthFrameV83 = -1;
            private static string _depthTruthEyeV83 = "?";

            private static int _preparedHistoryFrameV83 = -1;
            private static int _preparedHistoryAgeFramesV83 = -1;
            private static float _preparedHistoryAgeMsV83 = -1f;
            private static string _lastFreshLabelV83 = "?";
            private static float _nextTemporalTelemetryTimeV83;

            // V87 CPU-only render-boundary target-pose snapshots.
            private static int _poseSnapshotFrameV87 = -1;
            private static int _poseSnapshotFreshEyeV87 = -1;
            private static bool _poseSnapshotValidV87;
            private static Matrix4x4 _poseSnapshotLeftWorldToCameraV87;
            private static Matrix4x4 _poseSnapshotRightWorldToCameraV87;
            private static Matrix4x4 _poseSnapshotLeftProjectionV87;
            private static Matrix4x4 _poseSnapshotRightProjectionV87;
            private static Vector3 _poseSnapshotLeftPositionV87;
            private static Vector3 _poseSnapshotRightPositionV87;
            private static Quaternion _poseSnapshotLeftRotationV87 = Quaternion.identity;
            private static Quaternion _poseSnapshotRightRotationV87 = Quaternion.identity;
            private static float _poseTimingAbStartTimeV87 = -1f;
            private static bool _poseTimingAbSwitchLoggedV87;
            private static bool _poseTimingAbMissingSnapshotLoggedV87;

            // V90 depth-only -> depth + NVOFA residual same-run A/B. NVOFA executes from
            // the start so phase timing/perf remains matched; only consumption of its vector
            // field changes at the switch. V89 engine-motion state remains dormant.
            private static float _opticalFlowAbStartTimeV90 = -1f;
            private static bool _opticalFlowSwitchLoggedV90;
            private static bool _opticalFlowUnavailableLoggedV90;
            private static bool _opticalFlowPairingLoggedV90;

            // V94 source-raster matrix A/B/A. Starts only once a usable same-eye
            // temporal history exists, so loading/warmup cannot consume Phase A.
            private static float _hdrpMatrixAbStartTimeV94 = -1f;
            private static int _hdrpMatrixAbLastPhaseV94 = -1;
            private static bool _hdrpMatrixUnavailableLoggedV94;

            // V98 presentation-grid scatter A/B/A. Starts only once usable same-eye history exists.
            private static float _presentationGridScatterAbStartTimeV98 = -1f;
            private static int _presentationGridScatterAbLastPhaseV98 = -1;

            // V100 true-hole fallback A/B/A.  Starts only once a usable temporal history exists.
            private static float _rotationHoleFallbackAbStartTimeV100 = -1f;
            private static int _rotationHoleFallbackAbLastPhaseV100 = -1;

            // V101 sky/infinity pre-fill scatter A/B/A. Starts with usable temporal history.
            private static float _skyInfinityScatterAbStartTimeV101 = -1f;
            private static int _skyInfinityScatterAbLastPhaseV101 = -1;

            private static bool _perPixelUnavailableLoggedV88;
            private static bool _perPixelRuntimeFailureLoggedV88;
            private static bool _scatterRuntimeDisabledV88;

            private static bool _initAttempted;
            private static bool _available;
            private static bool _prepared;
            private static int _preparedFrame = -1;
            private static int _preparedSourceEye = -2;
            private static int _preparedTargetEye = -2;
            private static RenderTexture _preparedSourceRT;
            private static RenderTexture _preparedSyntheticRT;
            private static RenderTexture _preparedOldStaleRT;
            private static Texture _preparedDepthTexture;
            private static Vector4 _preparedZParams;
            private static int _preparedSourceWidth;
            private static int _preparedSourceHeight;
            private static int _preparedDepthWidth;
            private static int _preparedDepthHeight;
            private static string _lastSourceLabel = "?";
            private static string _lastTargetLabel = "?";
            private static int _lastDebugFrame = -1;

            // V79 passive V51 confidence-mask measurement.
            private static bool _coverageReadbackPendingV79;
            private static AsyncGPUReadbackRequest _coverageReadbackV79;
            private static float _nextCoverageRequestTimeV79;
            private static string _coverageSourceEyeV79;
            private static string _coverageTargetEyeV79;
            private static int _coverageSourceFrameV79;
            private static int _coverageWidthV79;
            private static int _coverageHeightV79;
            private const float CoverageReadbackIntervalV79 = 0.75f;

            // V80: passive source-depth input diagnostics. Small RGBA8 map only.
            private static RenderTexture _depthDiagRTV80;
            private static bool _depthDiagReadbackPendingV80;
            private static AsyncGPUReadbackRequest _depthDiagReadbackV80;
            private static float _nextDepthDiagRequestTimeV80;
            private static string _depthDiagEyeV80;
            private static int _depthDiagFrameV80;
            private static int _depthDiagSourceWidthV80;
            private static int _depthDiagSourceHeightV80;
            private static int _depthDiagAtlasWidthV80;
            private static int _depthDiagAtlasHeightV80;
            private static int _depthDiagGlobalInstanceIdV80;
            private static string _depthDiagGlobalNameV80;
            private static float _depthDiagMaxMetersV80;
            private const int DepthDiagSizeV80 = 256;
            private const float DepthDiagIntervalV80 = 0.75f;
            private static float _nextDebugCaptureTime;
            private static bool _firstPreparedLogged;
            private static bool _firstSubmissionLogged;
            private static int _failureLogsRemaining = 12;

            internal static void ResetForActivation()
            {
                _prepared = false;
                _preparedFrame = -1;
                _preparedSourceEye = -2;
                _preparedTargetEye = -2;
                _preparedSourceRT = null;
                _preparedSyntheticRT = null;
                _preparedOldStaleRT = null;
                _preparedDepthTexture = null;
                _preparedHistoryFrameV83 = -1;
                _preparedHistoryAgeFramesV83 = -1;
                _preparedHistoryAgeMsV83 = -1f;
                _lastFreshLabelV83 = "?";
                _nextTemporalTelemetryTimeV83 = 0f;
                _poseSnapshotFrameV87 = -1;
                _poseSnapshotFreshEyeV87 = -1;
                _poseSnapshotValidV87 = false;
                _poseTimingAbStartTimeV87 = -1f;
                _poseTimingAbSwitchLoggedV87 = false;
                _poseTimingAbMissingSnapshotLoggedV87 = false;
                _opticalFlowAbStartTimeV90 = -1f;
                _opticalFlowSwitchLoggedV90 = false;
                _opticalFlowUnavailableLoggedV90 = false;
                _opticalFlowPairingLoggedV90 = false;
                _hdrpMatrixAbStartTimeV94 = -1f;
                _hdrpMatrixAbLastPhaseV94 = -1;
                _hdrpMatrixUnavailableLoggedV94 = false;
                _presentationGridScatterAbStartTimeV98 = -1f;
                _presentationGridScatterAbLastPhaseV98 = -1;
                _rotationHoleFallbackAbStartTimeV100 = -1f;
                _rotationHoleFallbackAbLastPhaseV100 = -1;
                _skyInfinityScatterAbStartTimeV101 = -1f;
                _skyInfinityScatterAbLastPhaseV101 = -1;
                _perPixelUnavailableLoggedV88 = false;
                _perPixelRuntimeFailureLoggedV88 = false;
                _scatterRuntimeDisabledV88 = false;
                _nextDepthTruthCaptureTimeV83 = 0f;
                _depthTruthFrameV83 = -1;
                _depthTruthEyeV83 = "?";
                _nextDebugCaptureTime = 0f;
                _lastDebugFrame = -1;
                _failureLogsRemaining = 12;
                _firstPreparedLogged = false;
                _firstSubmissionLogged = false;
                _firstLeftLatchLogged = false;
                _firstRightLatchLogged = false;
                _renderBoundaryHudSyncLoggedV66 = false;
                _renderBoundaryHudSyncFrameV68 = -1;
                _renderBoundaryHudSyncLoggedV68 = false;
                _depthLatchFailureLogsRemaining = 16;
                _hudPlaneProbeActivationTimeV69 = Time.unscaledTime;
                _hudPlaneProbePhaseV69 = -1;
                _hudPlaneProbeLoggedLeftRectsV69 = false;
                _hudPlaneProbeLoggedRightRectsV69 = false;
                _hudDepthProxyLoggedLeftV72 = false;
                _hudDepthProxyLoggedRightV72 = false;
                _denseHudCompositeLoggedV76 = false;
                _viewportSanitizerLoggedV77 = false;
                _hudDepthProxyFailureLogsRemainingV72 = 8;
                HudStereoReprojectV60.Reset();
                NvOpticalFlowV90.ResetForActivation();
                ResetForensicsV99();
                InvalidateDepthLatch(_leftDepthLatch);
                InvalidateDepthLatch(_rightDepthLatch);
            }

            private static void CapturePoseTimingSnapshotV87(StereoRender self, int freshEye)
            {
                if (!AerTemporalUseRenderBoundaryPoseV88 || self == null ||
                    self.LeftCam == null || self.RightCam == null)
                    return;

                try
                {
                    _poseSnapshotFrameV87 = Time.frameCount;
                    _poseSnapshotFreshEyeV87 = freshEye;
                    _poseSnapshotLeftWorldToCameraV87 = self.LeftCam.worldToCameraMatrix;
                    _poseSnapshotRightWorldToCameraV87 = self.RightCam.worldToCameraMatrix;
                    _poseSnapshotLeftProjectionV87 = self.LeftCam.projectionMatrix;
                    _poseSnapshotRightProjectionV87 = self.RightCam.projectionMatrix;
                    _poseSnapshotLeftPositionV87 = self.LeftCam.transform.position;
                    _poseSnapshotRightPositionV87 = self.RightCam.transform.position;
                    _poseSnapshotLeftRotationV87 = self.LeftCam.transform.rotation;
                    _poseSnapshotRightRotationV87 = self.RightCam.transform.rotation;
                    _poseSnapshotValidV87 = true;
                }
                catch (Exception e)
                {
                    _poseSnapshotValidV87 = false;
                    if (!_poseTimingAbMissingSnapshotLoggedV87)
                    {
                        _poseTimingAbMissingSnapshotLoggedV87 = true;
                        MelonLogger.Warning(
                            "[StereoRender][AER V91][Pose] render-boundary snapshot failed; " +
                            "staying on EOF-late matrices. " + e.GetType().Name + ": " + e.Message);
                    }
                }
            }

            private static float ResolveMaxWarpMetersV87(Camera sourceEyeCam, Camera targetEyeCam)
            {
                float fallback = Mathf.Max(1.0f, AerDepthStereoWarpV48MaxMeters);
                if (!AerDepthStereoWarpV87UseCameraFarClip)
                    return fallback;

                float far = float.PositiveInfinity;
                if (sourceEyeCam != null && sourceEyeCam.farClipPlane > 1f)
                    far = Mathf.Min(far, sourceEyeCam.farClipPlane);
                if (targetEyeCam != null && targetEyeCam.farClipPlane > 1f)
                    far = Mathf.Min(far, targetEyeCam.farClipPlane);

                if (float.IsInfinity(far) || float.IsNaN(far) || far <= 1f)
                    return fallback;

                return Mathf.Max(1.0f, far - Mathf.Max(0.01f, AerDepthStereoWarpV87FarClipSafetyMeters));
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

            private static float MatrixMaxAbsFromIdentityV94(Matrix4x4 m)
            {
                float maxAbs = 0f;
                for (int r = 0; r < 4; ++r)
                {
                    for (int c = 0; c < 4; ++c)
                    {
                        float expected = r == c ? 1f : 0f;
                        maxAbs = Mathf.Max(maxAbs, Mathf.Abs(m[r, c] - expected));
                    }
                }
                return maxAbs;
            }

            internal static void InstallCameraDepthLatch(StereoRender self)
            {
                if (!AerDepthStereoWarpV50CameraLocalDepthLatchEnabled || self == null)
                    return;

                try
                {
                    // Static callback: remove-before-add prevents duplicate subscriptions across
                    // scene/rig respawns. The owner check in Uninstall prevents an old rig's
                    // OnDestroy from removing a newer rig's hook.
                    if (_endCameraRenderingDelegateV50 == null)
                    {
                        System.Action<ScriptableRenderContext, Camera> managedCallback = OnEndCameraRenderingV50;
                        _endCameraRenderingDelegateV50 = managedCallback;
                    }

                    // Generated IL2CPP event accessors: direct +=/-= with a method group does not
                    // compile against MelonLoader's converted UnityEngine.CoreModule bindings.
                    // Remove-before-add prevents duplicate subscriptions after scene/rig respawns.
                    try
                    {
                        RenderPipelineManager.remove_endCameraRendering(_endCameraRenderingDelegateV50);
                    }
                    catch
                    {
                        // Harmless on first install when the delegate was not subscribed yet.
                    }
                    RenderPipelineManager.add_endCameraRendering(_endCameraRenderingDelegateV50);

                    if (_beginCameraRenderingDelegateV60 == null)
                    {
                        System.Action<ScriptableRenderContext, Camera> managedBeginCallback = OnBeginCameraRenderingV60;
                        _beginCameraRenderingDelegateV60 = managedBeginCallback;
                    }
                    try
                    {
                        RenderPipelineManager.remove_beginCameraRendering(_beginCameraRenderingDelegateV60);
                    }
                    catch
                    {
                        // Harmless on first install.
                    }
                    RenderPipelineManager.add_beginCameraRendering(_beginCameraRenderingDelegateV60);

                    _depthLatchOwner = self;
                    _depthLatchCallbackInstalled = true;
                    InvalidateDepthLatch(_leftDepthLatch);
                    InvalidateDepthLatch(_rightDepthLatch);
                    MelonLogger.Msg("[StereoRender][AER V66][CameraHooks] V50 endCameraRendering depth latch installed. beginCameraRendering now performs SAFE transform-only HUD/minimap synchronization for the scheduled AER eye; raw UGUI replay remains disabled and no Canvas rebuild/cull/manual draw occurs.");
                }
                catch (Exception e)
                {
                    _depthLatchCallbackInstalled = false;
                    _depthLatchOwner = null;
                    LogDepthLatchFailure("failed to install endCameraRendering hook: " + e.Message);
                }
            }

            internal static void UninstallCameraDepthLatch(StereoRender self)
            {
                if (!_depthLatchCallbackInstalled || self == null || _depthLatchOwner != self)
                    return;

                try
                {
                    if (_endCameraRenderingDelegateV50 != null)
                        RenderPipelineManager.remove_endCameraRendering(_endCameraRenderingDelegateV50);
                    if (_beginCameraRenderingDelegateV60 != null)
                        RenderPipelineManager.remove_beginCameraRendering(_beginCameraRenderingDelegateV60);
                }
                catch (Exception e)
                {
                    LogDepthLatchFailure("failed to uninstall endCameraRendering hook: " + e.Message);
                }

                _depthLatchCallbackInstalled = false;
                _depthLatchOwner = null;
                HudStereoReprojectV60.Shutdown();
                ReleaseDepthLatch(_leftDepthLatch);
                ReleaseDepthLatch(_rightDepthLatch);
                ReleaseScatterTargetsV88();
            }

            private static void OnBeginCameraRenderingV60(
                ScriptableRenderContext context, Camera camera)
            {
                // V114 census records actual render activity for every camera with near-zero per-frame work.
                CameraCompositorAuditV114.OnCameraBegin(camera);
                // V106: global camera census/timing runs before any eye-specific early return.
                PerformanceForensicsV106.OnCameraBegin(camera);
                // V78 passive state capture BEFORE HDRP renders this camera.
                HDRPDlssRuntimeTraceV78.CameraBoundary("SRP_BEGIN", camera);
                StereoRender self = _depthLatchOwner;
                if (!_depthLatchCallbackInstalled || self == null || Instance != self ||
                    camera == null)
                    return;

                int eye;
                if (camera == self.LeftCam) eye = 0;
                else if (camera == self.RightCam) eye = 1;
                else return;


// V68 CAUSALITY FIX:
                // V67 was accidentally confounded. During its 15-second full-stereo control,
                // AlternateEyeRenderingEnabled=false caused this callback to return above, so the
                // SAFE pre-render HUD transform refresh was OFF. Once AER activated, the refresh
                // suddenly became ON too. The user's observation ("map locomotes in full stereo,
                // then gets stapled in place when AER starts") therefore cannot be attributed to
                // AER alone.
                //
                // Run the transform-only refresh ONCE PER UNITY FRAME before the first VR eye
                // renders, regardless of full-stereo vs AER. This keeps one identical HUD pose for
                // both eyes in conventional stereo and the same timing when AER later begins.
                // No Canvas rebuild, no UGUI culling, no manual draw.
                if (_renderBoundaryHudSyncFrameV68 != Time.frameCount && VRPlayer.Instance != null)
                {
                    _renderBoundaryHudSyncFrameV68 = Time.frameCount;
                    try
                    {
                        VRPlayer.Instance.RefreshHudLayoutForRenderV60();
                        if (!_renderBoundaryHudSyncLoggedV68)
                        {
                            _renderBoundaryHudSyncLoggedV68 = true;
                            MelonLogger.Warning(
                                $"[StereoRender][AER V68][HudTiming] SAFE PRE-RENDER HUD SYNC ACTIVE IN FULL-STEREO + AER: " +
                                $"firstEye={(eye == 0 ? "L" : "R")} frame={Time.frameCount} aer={AlternateEyeRenderingEnabled}. " +
                                "One transform refresh per Unity frame; UpdateCanvasPositionForVR + " +
                                "UpdateMinimapPositionEndOfFrame only; NO ForceUpdateCanvases, NO UGUI culling, NO manual draw.");
                        }
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Warning($"[StereoRender][AER V68][HudTiming] pre-render sync failed: {e.GetType().Name}: {e.Message}");
                    }
                }

                // Everything below this line is AER-only. Conventional full stereo exits here,
                // AFTER receiving the same HUD transform timing as AER.
                if (!AlternateEyeRenderingEnabled ||
                    self._aerScheduledEye != eye || self._aerScheduledEye < 0)
                    return;

                // V87: capture BOTH desired eye poses at the fresh real eye's render boundary.
                // Phase A only records these; Phase B consumes them after the 30 s auto-switch.
                CapturePoseTimingSnapshotV87(self, eye);

                // Raw UGUI replay remains quarantined. If a future diagnostic explicitly enables
                // it, it still runs after the safe transform sync above.
                if (HudStereoReprojectV60.Enabled)
                    HudStereoReprojectV60.BeginEyeRender(self, camera, eye);
            }

            private static void OnEndCameraRenderingV50(ScriptableRenderContext context, Camera camera)
            {
                // V114 census records actual render activity for every camera with near-zero per-frame work.
                CameraCompositorAuditV114.OnCameraEnd(camera);
                // V106: global camera census/timing runs before any eye-specific early return.
                PerformanceForensicsV106.OnCameraEnd(camera);
                // V78 passive state capture at the exact callback where V50 reads
                // _CameraDepthTexture.
                HDRPDlssRuntimeTraceV78.CameraBoundary("SRP_END_PRE_DEPTH", camera);
                StereoRender self = _depthLatchOwner;
                if (!AerDepthStereoWarpV50CameraLocalDepthLatchEnabled || !_depthLatchCallbackInstalled ||
                    self == null || Instance != self || !AlternateEyeRenderingEnabled || camera == null)
                    return;

                int eye;
                if (camera == self.LeftCam) eye = 0;
                else if (camera == self.RightCam) eye = 1;
                else return;

                // V81f: ACTUAL post-render location.
                // AfterOpaqueDepthAndNormal CustomPass command buffer has already executed
                // for this exact eye, so its owned diagnostic RT is now safe to read back.
                EyeDepthCaptureCustomPassV81.OnEndCameraRendering(camera);

                // Eye render is complete. Restoring CanvasRenderer state now cannot alter the
                // world-only source RT; it merely returns live Unity UI state to normal.
                HudStereoReprojectV60.EndEyeRender(self, camera, eye);

                RenderTexture sourceRT = eye == 0 ? self.LeftRT : self.RightRT;
                if (sourceRT == null || !sourceRT.IsCreated() || camera.targetTexture != sourceRT)
                {
                    LogDepthLatchFailure($"eye={(eye == 0 ? "L" : "R")} callback had no matching created target RT at frame={Time.frameCount}.");
                    return;
                }

                // IMPORTANT: the global binding is touched ONLY here, while SRP has told us the
                // exact eye camera that just completed. The raw mip-0 values are copied immediately
                // into an owned eye-local RFloat RT; EOF never samples this global texture directly.
                Texture globalDepth = Shader.GetGlobalTexture("_CameraDepthTexture");
                if (globalDepth == null)
                {
                    LogDepthLatchFailure($"eye={(eye == 0 ? "L" : "R")} endCameraRendering had null _CameraDepthTexture at frame={Time.frameCount}.");
                    return;
                }

                if (!EnsureLoaded())
                    return;

                EyeDepthLatch latch = eye == 0 ? _leftDepthLatch : _rightDepthLatch;
                RenderTexture previous = RenderTexture.active;
                try
                {
                    // V89: preserve the PRECEDING real render of this same physical eye before
                    // overwriting the latch. HDRP's captured total motion vector uses a previous
                    // camera transform; this pair lets compute reconstruct that camera-only
                    // screen displacement and subtract it, leaving object/animation motion.
                    bool previousRealCameraValidV89 =
                        latch.Valid &&
                        latch.Eye == eye &&
                        latch.CameraInstanceId == camera.GetInstanceID() &&
                        latch.SourceTargetInstanceId == sourceRT.GetInstanceID() &&
                        latch.Frame >= 0;
                    int previousRealFrameV89 = previousRealCameraValidV89 ? latch.Frame : -1;
                    Matrix4x4 previousRealWorldToCameraV89 = previousRealCameraValidV89
                        ? latch.SourceWorldToCamera
                        : Matrix4x4.identity;
                    Matrix4x4 previousRealGpuProjectionV89 = previousRealCameraValidV89
                        ? GL.GetGPUProjectionMatrix(latch.SourceProjection, true)
                        : Matrix4x4.identity;
                    float previousRealCaptureUnscaledTimeV90 = previousRealCameraValidV89
                        ? latch.CaptureUnscaledTime
                        : -1f;

                    // V91: DO NOT allocate the depth latch from the final eye color size here.
                    // With real HDRP dynamic resolution, camera-local depth is intentionally at
                    // HDCamera.actualWidth/actualHeight while sourceRT remains full presentation
                    // resolution after DLSS. Allocate only after the exact depth source is known.
                    Vector4 zParams = Shader.GetGlobalVector("_ZBufferParams");
                    Vector4 rtHandleScale = Shader.GetGlobalVector("_RTHandleScale");
                    Vector4 screenSize = Shader.GetGlobalVector("_ScreenSize");
                    Matrix4x4 sourceGpuProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
                    Camera targetCamAtCapture = eye == 0 ? self.RightCam : self.LeftCam;
                    if (targetCamAtCapture == null)
                        throw new Exception("opposite-eye camera was null during camera-local depth capture.");
                    Matrix4x4 targetGpuProjectionAtCapture = GL.GetGPUProjectionMatrix(targetCamAtCapture.projectionMatrix, true);

                    bool usedCustomPassDepthV82 = false;
                    int customPassDepthFrameV82 = -1;
                    RenderTexture customPassDepthV82 = null;

                    // V94: retrieve the exact HDCamera matrices recorded at the same
                    // AfterOpaqueDepthAndNormal CustomPass execution that produced depth.
                    int hdrpRasterMatrixFrameV94 = -1;
                    Matrix4x4 hdrpProjectionV94 = Matrix4x4.identity;
                    Matrix4x4 hdrpWorldToCameraRHSV94 = Matrix4x4.identity;
                    Matrix4x4 cameraGpuProjectionAtDepthV94 = Matrix4x4.identity;
                    Matrix4x4 cameraWorldToCameraAtDepthV94 = Matrix4x4.identity;
                    Matrix4x4 cameraProjectionAtDepthV94 = Matrix4x4.identity;
                    Matrix4x4 cameraNonJitteredProjectionAtDepthV94 = Matrix4x4.identity;
                    bool hdrpRasterMatricesValidV94 =
                        EyeDepthCaptureCustomPassV81.TryGetRasterMatricesV94(
                            eye,
                            out hdrpRasterMatrixFrameV94,
                            out hdrpProjectionV94,
                            out hdrpWorldToCameraRHSV94,
                            out cameraGpuProjectionAtDepthV94,
                            out cameraWorldToCameraAtDepthV94,
                            out cameraProjectionAtDepthV94,
                            out cameraNonJitteredProjectionAtDepthV94) &&
                        hdrpRasterMatrixFrameV94 == Time.frameCount;

                    if (AerDepthStereoWarpV82UseCustomPassDepth &&
                        EyeDepthCaptureCustomPassV81.TryGetOwnedRawDepth(
                            eye,
                            out customPassDepthV82,
                            out customPassDepthFrameV82) &&
                        customPassDepthV82 != null &&
                        customPassDepthV82.IsCreated() &&
                        customPassDepthFrameV82 == Time.frameCount)
                    {
                        // V91: this is now expected to be LOWER resolution than sourceRT when
                        // DLSS/DRS is active. Preserve the exact active HDRP depth raster instead
                        // of upscaling depth just to satisfy an obsolete width==color invariant.
                        latch.DepthRT = EnsureLatchedDepthTarget(
                            latch.DepthRT, customPassDepthV82.width, customPassDepthV82.height, eye);
                        if (latch.DepthRT == null || !latch.DepthRT.IsCreated())
                            throw new Exception("dynamic-size camera-local RFloat depth latch RT is unavailable.");

                        Graphics.CopyTexture(customPassDepthV82, latch.DepthRT);
                        usedCustomPassDepthV82 = true;
                    }
                    else
                    {
                        // Safety fallback: preserve the entire proven V50 path if the custom-pass
                        // source is unavailable or stale for this exact eye/frame. The fallback
                        // remains presentation-sized because the old global texture path was
                        // written around that contract; it is fail-soft, not the intended V91 path.
                        latch.DepthRT = EnsureLatchedDepthTarget(
                            latch.DepthRT, sourceRT.width, sourceRT.height, eye);
                        if (latch.DepthRT == null || !latch.DepthRT.IsCreated())
                            throw new Exception("fallback RFloat depth latch RT is unavailable.");
                        _material.SetTexture("_CameraDepthTexture", globalDepth);
                        _material.SetVector("_SourceSize", new Vector4(
                            sourceRT.width, sourceRT.height,
                            1.0f / Mathf.Max(1, sourceRT.width),
                            1.0f / Mathf.Max(1, sourceRT.height)));
                        _material.SetVector("_DepthAtlasSize", new Vector4(
                            globalDepth.width, globalDepth.height,
                            1.0f / Mathf.Max(1, globalDepth.width),
                            1.0f / Mathf.Max(1, globalDepth.height)));
                        _material.SetFloat("_DepthArraySlice", 0f);

                        SetRenderTargetFullViewportV77(latch.DepthRT);
                        GL.Clear(false, true, Color.clear);
                        if (!_material.SetPass(4))
                            throw new Exception(
                                "DepthStereoWarp Material.SetPass(4) global fallback capture returned false.");
                        DrawFullscreenTopLeftUvQuad();
                    }

                    // Keep V80's GLOBAL diagnostic alive even when V82 is using the camera-local
                    // depth for actual V51 synthesis. This gives a same-run forensic A/B without
                    // allowing the bad global texture to influence the headset warp.
                    if (AerV84ForensicsEnabled)
                    {
                        RequestDepthInputForensicsV80(
                            globalDepth,
                            sourceRT,
                            zParams,
                            camera.projectionMatrix,
                            sourceGpuProjection,
                            targetCamAtCapture.projectionMatrix,
                            targetGpuProjectionAtCapture,
                            eye);
                    }

                    // V83 desktop truth board: compare the raw camera-local depth against the
                    // old V47 lower-left interpretation of the packed global mip pyramid on THIS
                    // SAME eye/frame. Capture BEFORE HUD proxies mutate latch.DepthRT.
                    if (AerV85DepthTruthBoardEnabled && usedCustomPassDepthV82)
                    {
                        CaptureDepthTruthPanelsV83(
                            customPassDepthV82,
                            globalDepth,
                            sourceRT,
                            zParams,
                            eye);
                    }

                    // V89: the motion CustomPass runs later in HDRP (BeforePreRefraction) but still
                    // before endCameraRendering, so its owned RGHalf target should now describe the
                    // exact same eye/frame as the color + depth latch. Do not CPU-copy it: each eye
                    // owns a persistent texture that cannot change again until THAT eye really renders.
                    RenderTexture customPassMotionV89 = null;
                    int customPassMotionFrameV89 = -1;
                    bool wantMotionCaptureV99 =
                        AerTemporalObjectMotionV89Enabled ||
                        (AerV99ForensicsEnabled && AerV99CaptureHdrpMotionVectors);
                    bool usedCustomPassMotionV89 =
                        wantMotionCaptureV99 &&
                        EyeMotionCaptureCustomPassV89.TryGetOwnedMotion(
                            eye,
                            out customPassMotionV89,
                            out customPassMotionFrameV89) &&
                        customPassMotionV89 != null &&
                        customPassMotionV89.IsCreated() &&
                        customPassMotionFrameV89 == Time.frameCount &&
                        // Under DLSS/DRS the motion buffer intentionally follows HDCamera.actual
                        // (the same internal raster as camera-local depth), NOT the 3141x3317
                        // post-DLSS presentation color. V89's old ==sourceRT check would therefore
                        // silently reject every real motion capture in the modern path.
                        (!usedCustomPassDepthV82 ||
                         (customPassMotionV89.width == latch.DepthRT.width &&
                          customPassMotionV89.height == latch.DepthRT.height));

                    // V72: after the exact HDRP eye depth has been copied, paint only the AER-owned
                    // depth latch with simple HUD support geometry. This is the depth equivalent of
                    // putting the existing 2D HUD texture on a thin physical slab: the color image
                    // is unchanged, but AER now reconstructs those pixels from a nearby real plane
                    // instead of the tree/rock/sky depth behind them.
                    ApplyHudDepthProxiesV72(self, camera, latch, eye);

                    // V90: make a GPU-only R8 luminance copy of THIS freshly rendered eye and
                    // queue NVOFA CURRENT->PREVIOUS on Unity's render thread. NVOFA runs from
                    // the beginning of the experiment even while Phase A ignores its output,
                    // keeping the visual A/B perf-matched. No CPU image copy/readback occurs.
                    bool opticalFlowQueuedV90 =
                        AerTemporalOpticalFlowV90Enabled &&
                        _opticalFlowLumaShaderV90 != null &&
                        NvOpticalFlowV90.CaptureRealEye(
                            eye, sourceRT, _opticalFlowLumaShaderV90, Time.frameCount,
                            Mathf.Clamp(V91NvofaResolutionScale, 0.25f, 1.0f));

                    RestoreRenderTargetViewportV77(previous);

                    latch.Valid = true;
                    latch.Eye = eye;
                    latch.Frame = Time.frameCount;
                    latch.CameraInstanceId = camera.GetInstanceID();
                    latch.SourceTargetInstanceId = sourceRT.GetInstanceID();
                    latch.SourceWidth = sourceRT.width;
                    latch.SourceHeight = sourceRT.height;
                    latch.GlobalAtlasWidth = globalDepth.width;
                    latch.GlobalAtlasHeight = globalDepth.height;
                    latch.UsedCustomPassDepthV82 = usedCustomPassDepthV82;
                    latch.CustomPassDepthFrameV82 = customPassDepthFrameV82;
                    latch.ZParams = zParams;
                    latch.RtHandleScale = rtHandleScale;
                    latch.ScreenSize = screenSize;
                    latch.SourceInvGpuProjection = sourceGpuProjection.inverse;
                    latch.SourceCameraToWorld = camera.cameraToWorldMatrix;
                    latch.SourceWorldToCamera = camera.worldToCameraMatrix;
                    latch.SourceProjection = camera.projectionMatrix;
                    latch.TargetWorldToCamera = targetCamAtCapture.worldToCameraMatrix;
                    latch.TargetProjection = targetCamAtCapture.projectionMatrix;
                    latch.TargetGpuProjection = targetGpuProjectionAtCapture;

                    latch.HdrpRasterMatricesValidV94 = hdrpRasterMatricesValidV94;
                    latch.HdrpRasterMatrixFrameV94 = hdrpRasterMatrixFrameV94;
                    latch.HdrpProjectionV94 = hdrpProjectionV94;
                    latch.HdrpWorldToCameraRHSV94 = hdrpWorldToCameraRHSV94;
                    latch.CameraGpuProjectionAtDepthV94 = cameraGpuProjectionAtDepthV94;
                    latch.CameraWorldToCameraAtDepthV94 = cameraWorldToCameraAtDepthV94;
                    latch.CameraProjectionAtDepthV94 = cameraProjectionAtDepthV94;
                    latch.CameraNonJitteredProjectionAtDepthV94 =
                        cameraNonJitteredProjectionAtDepthV94;

                    latch.MotionRTV89 = usedCustomPassMotionV89 ? customPassMotionV89 : null;
                    latch.UsedCustomPassMotionV89 = usedCustomPassMotionV89;
                    latch.CustomPassMotionFrameV89 = customPassMotionFrameV89;
                    latch.PreviousRealCameraValidV89 = previousRealCameraValidV89;
                    latch.PreviousRealFrameV89 = previousRealFrameV89;
                    latch.PreviousRealWorldToCameraV89 = previousRealWorldToCameraV89;
                    latch.PreviousRealGpuProjectionV89 = previousRealGpuProjectionV89;
                    latch.PreviousRealCaptureUnscaledTimeV90 = previousRealCaptureUnscaledTimeV90;
                    latch.CaptureUnscaledTime = Time.unscaledTime;
                    latch.SourcePosition = camera.transform.position;
                    latch.SourceRotation = camera.transform.rotation;
                    latch.CameraName = camera.name;

                    // V138: dimensions come from the same actual-eye CustomPass that captured
                    // depth; color is the full-resolution DLSS output. Changes are logged once
                    // per eye and every ~4 seconds, not every rendered frame.
                    if (AlternateEyeRenderingEnabled && latch.DepthRT != null &&
                        (latch.DepthRT.width != _v138PreviousDepthWidth[eye] ||
                         latch.DepthRT.height != _v138PreviousDepthHeight[eye] ||
                         Time.unscaledTime >= _v138NextRasterLog[eye]))
                    {
                        _v138PreviousDepthWidth[eye] = latch.DepthRT.width;
                        _v138PreviousDepthHeight[eye] = latch.DepthRT.height;
                        _v138NextRasterLog[eye] = Time.unscaledTime + 4f;
                        int viewportW, viewportH, viewportFrame;
                        bool viewportValid = EyeDepthCaptureCustomPassV81.TryGetObservedViewportV116(
                            eye, out viewportW, out viewportH, out viewportFrame);
                        bool paired = viewportValid && viewportFrame == latch.Frame;
                        bool coherent = paired &&
                            Mathf.Abs(viewportW - latch.DepthRT.width) <= 4 &&
                            Mathf.Abs(viewportH - latch.DepthRT.height) <= 4;
                        MelonLogger.Warning(
                            "[V138 AER-DEPTH-RASTER] eye=" + (eye == 0 ? "L" : "R") +
                            " frame=" + latch.Frame +
                            " output=" + sourceRT.width + "x" + sourceRT.height +
                            " capturedDepth=" + latch.DepthRT.width + "x" + latch.DepthRT.height +
                            " hdrpViewport=" + (viewportValid ? viewportW + "x" + viewportH : "NA") +
                            " viewportFrame=" + viewportFrame +
                            " paired=" + paired + " coherent=" + coherent +
                            " customDepth=" + latch.UsedCustomPassDepthV82 +
                            " flow=" + latch.UsedCustomPassMotionV89 +
                            " depthGuard=V103 " +
                            "absTolMeters=" + AerV103FillDepthAbsToleranceMeters.ToString("F2") +
                            " relTol=" + AerV103FillDepthRelativeTolerance.ToString("F3") +
                            " (metadata only)");
                    }

                    bool first = eye == 0 ? !_firstLeftLatchLogged : !_firstRightLatchLogged;
                    if (first)
                    {
                        if (eye == 0) _firstLeftLatchLogged = true; else _firstRightLatchLogged = true;
                        MelonLogger.Msg(
                            $"[StereoRender][AER V91][DepthLatch] CAPTURED eye={(eye == 0 ? "L" : "R")} " +
                            $"camera='{camera.name}' frame={latch.Frame} sourceRT={sourceRT.width}x{sourceRT.height} " +
                            $"-> ownedRFloat={latch.DepthRT.width}x{latch.DepthRT.height}; " +
                            $"DEPTH-SOURCE={(usedCustomPassDepthV82 ? "CUSTOMPASS-cameraDepthBuffer" : "GLOBAL-FALLBACK")} " +
                            $"customPassFrame={customPassDepthFrameV82} " +
                            $"v94HdrpMatrices={(hdrpRasterMatricesValidV94 ? "PAIRED" : "UNAVAILABLE")} " +
                            $"v94MatrixFrame={hdrpRasterMatrixFrameV94} " +
                            $"v89Motion={(usedCustomPassMotionV89 ? "CUSTOMPASS-complete" : "OFF")} " +
                            $"motionFrame={customPassMotionFrameV89} prevRealFrame={previousRealFrameV89} " +
                            $"nvofaQueued={opticalFlowQueuedV90} " +
                            $"globalAtlasDiagnosticOnly={globalDepth.width}x{globalDepth.height} " +
                            $"zParams=({zParams.x:F4},{zParams.y:F4},{zParams.z:F4},{zParams.w:F4}).");
                    }
                }
                catch (Exception e)
                {
                    RestoreRenderTargetViewportV77(previous);
                    InvalidateDepthLatch(latch);
                    LogDepthLatchFailure($"eye={(eye == 0 ? "L" : "R")} capture failed at frame={Time.frameCount}: {e.Message}");
                }
            }

            private static void InvalidateDepthLatch(EyeDepthLatch latch)
            {
                if (latch == null) return;
                latch.Valid = false;
                latch.Eye = -1;
                latch.Frame = -1;
                latch.CameraInstanceId = 0;
                latch.SourceTargetInstanceId = 0;
                latch.UsedCustomPassDepthV82 = false;
                latch.CustomPassDepthFrameV82 = -1;
                latch.HdrpRasterMatricesValidV94 = false;
                latch.HdrpRasterMatrixFrameV94 = -1;
                latch.MotionRTV89 = null;
                latch.UsedCustomPassMotionV89 = false;
                latch.CustomPassMotionFrameV89 = -1;
                latch.PreviousRealCameraValidV89 = false;
                latch.PreviousRealFrameV89 = -1;
                latch.PreviousRealCaptureUnscaledTimeV90 = -1f;
                latch.CaptureUnscaledTime = -1f;
                latch.SourcePosition = Vector3.zero;
                latch.SourceRotation = Quaternion.identity;
            }

            private static void ReleaseDepthLatch(EyeDepthLatch latch)
            {
                if (latch == null) return;
                InvalidateDepthLatch(latch);
                if (latch.DepthRT != null)
                {
                    try { latch.DepthRT.Release(); } catch { }
                    UnityEngine.Object.Destroy(latch.DepthRT);
                    latch.DepthRT = null;
                }
            }

            private static void ReleaseScatterTargetsV88()
            {
                if (_scatterDepthKeyRTV88 != null)
                {
                    try { _scatterDepthKeyRTV88.Release(); } catch { }
                    UnityEngine.Object.Destroy(_scatterDepthKeyRTV88);
                    _scatterDepthKeyRTV88 = null;
                }

                if (_scatterSourceIndexRTV88 != null)
                {
                    try { _scatterSourceIndexRTV88.Release(); } catch { }
                    UnityEngine.Object.Destroy(_scatterSourceIndexRTV88);
                    _scatterSourceIndexRTV88 = null;
                }
            }

            private static RenderTexture EnsureLatchedDepthTarget(
                RenderTexture target, int width, int height, int eye)
            {
                width = Mathf.Max(1, width);
                height = Mathf.Max(1, height);

                if (target != null && target.width == width && target.height == height &&
                    target.format == RenderTextureFormat.RFloat && target.IsCreated())
                    return target;

                if (target != null)
                {
                    try { target.Release(); } catch { }
                    UnityEngine.Object.Destroy(target);
                }

                target = new RenderTexture(
                    width, height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
                {
                    name = eye == 0 ? "SonsVR_V91_LatchedDepthLeft" : "SonsVR_V91_LatchedDepthRight",
                    antiAliasing = 1,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    useDynamicScale = false
                };
                target.Create();
                return target;
            }

            private static RenderTexture EnsureLatchedDepthTarget(
                RenderTexture target, RenderTexture sourceRT, int eye)
            {
                return EnsureLatchedDepthTarget(target, sourceRT.width, sourceRT.height, eye);
            }

            private static bool TryGetExactDepthLatch(
                StereoRender self, int completedEye, Camera sourceCam, RenderTexture sourceRT, out EyeDepthLatch latch)
            {
                latch = completedEye == 0 ? _leftDepthLatch : _rightDepthLatch;
                if (latch == null || !latch.Valid || latch.DepthRT == null || !latch.DepthRT.IsCreated())
                    return false;

                if (latch.Eye != completedEye || sourceCam == null || sourceRT == null ||
                    latch.CameraInstanceId != sourceCam.GetInstanceID() ||
                    latch.SourceTargetInstanceId != sourceRT.GetInstanceID() ||
                    latch.SourceWidth != sourceRT.width || latch.SourceHeight != sourceRT.height ||
                    latch.DepthRT.width != sourceRT.width || latch.DepthRT.height != sourceRT.height)
                    return false;

                if (AerDepthStereoWarpV50RequireExactFrame && latch.Frame != Time.frameCount)
                    return false;

                return true;
            }

            private static bool TryGetTemporalHistoryDepthV83(
                int historyEye,
                Camera historyEyeCamera,
                RenderTexture historyColorRT,
                out EyeDepthLatch latch,
                out int ageFrames)
            {
                latch = historyEye == 0 ? _leftDepthLatch : _rightDepthLatch;
                ageFrames = int.MaxValue;

                if (!AerTemporalSameEyeV83Enabled ||
                    latch == null ||
                    !latch.Valid ||
                    latch.DepthRT == null ||
                    !latch.DepthRT.IsCreated() ||
                    historyEyeCamera == null ||
                    historyColorRT == null ||
                    !historyColorRT.IsCreated())
                    return false;

                if (latch.Eye != historyEye ||
                    latch.CameraInstanceId != historyEyeCamera.GetInstanceID() ||
                    latch.SourceTargetInstanceId != historyColorRT.GetInstanceID() ||
                    latch.SourceWidth != historyColorRT.width ||
                    latch.SourceHeight != historyColorRT.height)
                    return false;

                // V91: camera-local depth may intentionally be lower-resolution than the final
                // DLSS-upscaled historical color. Compute uses normalized UVs to join them.
                if (latch.DepthRT.width < 1 || latch.DepthRT.height < 1)
                    return false;

                // V83 intentionally starts with the camera-local depth source identified in V81f.
                // Do not silently fall back to the packed mip pyramid inside the temporal renderer.
                if (!latch.UsedCustomPassDepthV82)
                    return false;

                ageFrames = Time.frameCount - latch.Frame;
                int maxAge = Mathf.Max(1, AerTemporalSameEyeV83MaxHistoryAgeFrames);

                // The target eye should normally be exactly one Unity frame old. Allow one extra
                // frame for a missed schedule/transition, but refuse current/future or ancient data.
                if (ageFrames < 1 || ageFrames > maxAge)
                    return false;

                return true;
            }

            private static bool TryGetTemporalHistoryMotionV89(
                int historyEye,
                EyeDepthLatch historyLatch,
                out RenderTexture motionRT)
            {
                motionRT = null;

                if (!AerTemporalObjectMotionV89Enabled ||
                    historyLatch == null ||
                    !historyLatch.Valid ||
                    historyLatch.Eye != historyEye ||
                    !historyLatch.UsedCustomPassMotionV89 ||
                    historyLatch.MotionRTV89 == null ||
                    !historyLatch.MotionRTV89.IsCreated() ||
                    historyLatch.CustomPassMotionFrameV89 != historyLatch.Frame ||
                    !historyLatch.PreviousRealCameraValidV89 ||
                    historyLatch.PreviousRealFrameV89 < 0 ||
                    historyLatch.PreviousRealFrameV89 >= historyLatch.Frame)
                    return false;

                if (historyLatch.MotionRTV89.width != historyLatch.SourceWidth ||
                    historyLatch.MotionRTV89.height != historyLatch.SourceHeight)
                    return false;

                // The pointer is owned by the per-eye CustomPass. Because this physical eye has
                // not rendered again while it is the stale/predicted eye, the texture still
                // contains the motion field paired to historyLatch.Frame.
                motionRT = historyLatch.MotionRTV89;
                return true;
            }

            private static bool TryGetTemporalOpticalFlowV90(
                int historyEye,
                EyeDepthLatch historyLatch,
                out RenderTexture flowRT,
                out float timeScale,
                out double nativeApiCallMs,
                out ulong executeCount)
            {
                flowRT = null;
                timeScale = 0f;
                nativeApiCallMs = -1.0;
                executeCount = 0;

                if (!AerTemporalOpticalFlowV90Enabled ||
                    historyLatch == null ||
                    !historyLatch.Valid ||
                    historyLatch.Eye != historyEye ||
                    historyLatch.Frame < 0 ||
                    !historyLatch.PreviousRealCameraValidV89 ||
                    historyLatch.PreviousRealFrameV89 < 0 ||
                    historyLatch.PreviousRealFrameV89 >= historyLatch.Frame ||
                    historyLatch.PreviousRealCaptureUnscaledTimeV90 < 0f ||
                    historyLatch.CaptureUnscaledTime < 0f)
                    return false;

                float flowInterval =
                    historyLatch.CaptureUnscaledTime -
                    historyLatch.PreviousRealCaptureUnscaledTimeV90;
                float predictionHorizon =
                    Time.unscaledTime - historyLatch.CaptureUnscaledTime;

                if (!(flowInterval > 1.0e-4f) || predictionHorizon < 0f)
                    return false;

                if (!NvOpticalFlowV90.TryGetFlowForHistory(
                        historyEye,
                        historyLatch.Frame,
                        out flowRT,
                        out nativeApiCallMs,
                        out executeCount))
                    return false;

                if (flowRT == null || !flowRT.IsCreated() ||
                    flowRT.width < 1 || flowRT.height < 1)
                    return false;

                // One AER prediction horizon is normally about half of the same-eye real-render
                // interval, but use timestamps rather than baking in 0.5. Clamp only catastrophic
                // hitches; the user-visible scalar remains a separate V90 research knob.
                float measured = predictionHorizon / flowInterval;
                timeScale = Mathf.Clamp(
                    measured * Mathf.Max(0f, AerTemporalOpticalFlowScaleV90),
                    0f,
                    Mathf.Max(0.01f, AerTemporalOpticalFlowMaxTimeScaleV90));
                return true;
            }

            private static bool EnsureExactHudProxyMeshesV74()
            {
                // V111: retired. Historical HUD depth-catcher meshes remain impossible to allocate. The baked/compiled
                // disc-ring-rect proxy path is not merely disabled by a top-level
                // experiment flag; it is made incapable of allocating any Mesh objects at runtime.
                _hudExactMeshesAvailableV73 = false;
                _hudDiscMeshV73 = null;
                _hudRingMeshV73 = null;
                _hudRectMeshV73 = null;
                return false;
            }

            private static Mesh GetExactHudProxyMeshV73(int proxyKind)
            {
                // Existing V72 meta.z convention:
                //   0 = GPS disc, 1 = status/vitals ring, 2 = message rectangle.
                if (proxyKind == 0) return _hudDiscMeshV73;
                if (proxyKind == 1) return _hudRingMeshV73;
                if (proxyKind == 2) return _hudRectMeshV73;
                return null;
            }

            private static Matrix4x4 BuildHudMeshLocalToClipV73(
                Vector4 referenceRectTopLeftUv,
                Mesh mesh,
                int proxyKind)
            {
                Bounds b = mesh.bounds;

                float centerUvX = 0.5f * (referenceRectTopLeftUv.x + referenceRectTopLeftUv.z);
                float centerUvY = 0.5f * (referenceRectTopLeftUv.y + referenceRectTopLeftUv.w);
                float halfUvX = 0.5f * Mathf.Max(1.0e-6f, referenceRectTopLeftUv.z - referenceRectTopLeftUv.x);
                float halfUvY = 0.5f * Mathf.Max(1.0e-6f, referenceRectTopLeftUv.w - referenceRectTopLeftUv.y);

                float unitsX;
                float unitsY;

                if (proxyKind == 1)
                {
                    // User deliberately placed the ring origin at the MAIN circle center while
                    // appendages extend only toward +X/+Y. The canonical baked ring therefore has
                    // min.x ~= min.y ~= -mainRadius. Use that main radius as the scale reference
                    // instead of fitting the asymmetric whole bounding box into the target rect.
                    unitsX = Mathf.Max(1.0e-6f, -b.min.x);
                    unitsY = Mathf.Max(1.0e-6f, -b.min.y);
                }
                else
                {
                    // Disc and text slab are symmetric around their authored origins.
                    unitsX = Mathf.Max(1.0e-6f, b.extents.x);
                    unitsY = Mathf.Max(1.0e-6f, b.extents.y);
                }

                float sxClip = 2f * halfUvX / unitsX;
                float syClip = 2f * halfUvY / unitsY;
                float cxClip = centerUvX * 2f - 1f;
                float cyClip = 1f - centerUvY * 2f;

                Matrix4x4 m = Matrix4x4.identity;
                m.m00 = sxClip;
                // V75: the private RFloat render target uses the opposite raster Y convention
                // from the canonical baked HUD mesh. Trees/world depth are already correct;
                // ONLY the mesh-local HUD silhouette was vertically mirrored. Flip mesh Y
                // around its projected center; X and center position remain unchanged.
                m.m11 = -syClip;
                m.m03 = cxClip;
                m.m13 = cyClip;
                return m;
            }

            private static Vector4 BuildHudMeshLocalToSourceUvV76(
                Vector4 referenceRectTopLeftUv,
                Mesh mesh,
                int proxyKind)
            {
                Bounds b = mesh.bounds;

                float centerUvX = 0.5f * (referenceRectTopLeftUv.x + referenceRectTopLeftUv.z);
                float centerUvY = 0.5f * (referenceRectTopLeftUv.y + referenceRectTopLeftUv.w);
                float halfUvX = 0.5f * Mathf.Max(1.0e-6f, referenceRectTopLeftUv.z - referenceRectTopLeftUv.x);
                float halfUvY = 0.5f * Mathf.Max(1.0e-6f, referenceRectTopLeftUv.w - referenceRectTopLeftUv.y);

                float unitsX;
                float unitsY;

                if (proxyKind == 1)
                {
                    // Ring main-circle origin/radius convention from V73/V74.
                    unitsX = Mathf.Max(1.0e-6f, -b.min.x);
                    unitsY = Mathf.Max(1.0e-6f, -b.min.y);
                }
                else
                {
                    unitsX = Mathf.Max(1.0e-6f, b.extents.x);
                    unitsY = Mathf.Max(1.0e-6f, b.extents.y);
                }

                // Canonical +Y is screen-UP. Top-left UV screen-UP means decreasing V.
                return new Vector4(
                    halfUvX / unitsX,
                    -halfUvY / unitsY,
                    centerUvX,
                    centerUvY);
            }

            private static bool CompositeDenseHudV76(
                StereoRender self,
                int completedEye,
                Camera sourceCamera,
                Camera targetCamera,
                RenderTexture sourceRT,
                RenderTexture syntheticRT)
            {
                if (!AerHudDepthCatchersExperimentalEnabled ||
                    !AerHudDensePlanarRemapV76Enabled ||
                    self == null || sourceCamera == null || targetCamera == null ||
                    sourceRT == null || syntheticRT == null ||
                    !sourceRT.IsCreated() || !syntheticRT.IsCreated() ||
                    _hudDepthProxyMaterialV72 == null || VRPlayer.Instance == null)
                    return false;

                if (!EnsureExactHudProxyMeshesV74())
                    return false;

                Vector4 s0, t0, s1, t1, s2, t2;
                int count;
                try
                {
                    count = VRPlayer.Instance.GetHudDenseRemapPairsV76(
                        sourceCamera, targetCamera,
                        out s0, out t0,
                        out s1, out t1,
                        out s2, out t2);
                }
                catch (Exception e)
                {
                    LogHudDepthProxyFailureV72(
                        "V76 dense HUD pair collection failed: " +
                        e.GetType().Name + ": " + e.Message);
                    return false;
                }

                if (count <= 0)
                    return false;

                _hudDepthProxyMaterialV72.SetTexture("_HudSourceColor", sourceRT);
                _hudDepthProxyMaterialV72.SetFloat(
                    "_HudTextBlackBacking",
                    AerHudDensePlanarTextBlackBackingV76 ? 1f : 0f);

                SetRenderTargetFullViewportV77(syntheticRT);

                int drawn = 0;
                for (int kind = 0; kind < count && kind < 3; ++kind)
                {
                    Vector4 sourceRect = kind == 0 ? s0 : (kind == 1 ? s1 : s2);
                    Vector4 targetRect = kind == 0 ? t0 : (kind == 1 ? t1 : t2);
                    if ((sourceRect.z - sourceRect.x) <= 1.0e-6f ||
                        (sourceRect.w - sourceRect.y) <= 1.0e-6f ||
                        (targetRect.z - targetRect.x) <= 1.0e-6f ||
                        (targetRect.w - targetRect.y) <= 1.0e-6f)
                        continue;

                    Mesh mesh = GetExactHudProxyMeshV73(kind);
                    if (mesh == null)
                        continue;

                    Matrix4x4 targetLocalToClip = BuildHudMeshLocalToClipV73(
                        targetRect, mesh, kind);
                    Vector4 sourceUvTransform = BuildHudMeshLocalToSourceUvV76(
                        sourceRect, mesh, kind);

                    _hudDepthProxyMaterialV72.SetMatrix(
                        "_ProxyLocalToClip", targetLocalToClip);
                    _hudDepthProxyMaterialV72.SetVector(
                        "_HudSourceUvTransform", sourceUvTransform);
                    _hudDepthProxyMaterialV72.SetFloat("_HudProxyKind", kind);

                    if (!_hudDepthProxyMaterialV72.SetPass(2))
                    {
                        LogHudDepthProxyFailureV72(
                            "V76 dense HUD Material.SetPass(2) returned false.");
                        return false;
                    }

                    Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
                    drawn++;
                }

                if (drawn > 0 && !_denseHudCompositeLoggedV76)
                {
                    _denseHudCompositeLoggedV76 = true;
                    MelonLogger.Warning(
                        $"[StereoRender][AER V77][DenseHUD] DIRECT PLANAR HUD COMPOSITE IS ACTIVE: " +
                        $"frame={Time.frameCount} source={(completedEye == 0 ? "L" : "R")} " +
                        $"target={(completedEye == 0 ? "R" : "L")} drawn={drawn}. " +
                        "HUD silhouettes were cut out of source depth before V51, then current source " +
                        "pixels were densely remapped after V51 fallback. Ring/map use exact baked meshes; " +
                        $"textBlackBacking={AerHudDensePlanarTextBlackBackingV76}.");
                }

                return drawn > 0;
            }

            private static void ApplyHudDepthProxiesV72(
                StereoRender self,
                Camera sourceCamera,
                EyeDepthLatch latch,
                int eye)
            {
                if (!AerHudDepthCatchersExperimentalEnabled ||
                    !AerHudDepthProxyV72Enabled || self == null || sourceCamera == null || latch == null ||
                    latch.DepthRT == null || _hudDepthProxyMaterialV72 == null || VRPlayer.Instance == null)
                    return;

                Vector4 rect0, meta0, rect1, meta1, rect2, meta2, rect3, meta3;
                int count;
                try
                {
                    count = VRPlayer.Instance.GetHudDepthProxiesV72(
                        sourceCamera,
                        out rect0, out meta0,
                        out rect1, out meta1,
                        out rect2, out meta2,
                        out rect3, out meta3);
                }
                catch (Exception e)
                {
                    LogHudDepthProxyFailureV72("proxy collection failed: " + e.GetType().Name + ": " + e.Message);
                    return;
                }

                if (count <= 0)
                {
                    LogHudDepthProxyFailureV72("no active HUD proxy regions were found for eye=" + (eye == 0 ? "L" : "R") + ".");
                    return;
                }

                Vector4 z = latch.ZParams;
                bool denseHudCutoutV76 = AerHudDensePlanarRemapV76Enabled;
                if (!denseHudCutoutV76 && Mathf.Abs(z.z) < 1.0e-8f)
                {
                    LogHudDepthProxyFailureV72("_ZBufferParams.z was zero; cannot convert proxy eye depth to raw device depth.");
                    return;
                }

                SetRenderTargetFullViewportV77(latch.DepthRT);

                bool exactMeshes = EnsureExactHudProxyMeshesV74();

                for (int i = 0; i < count; i++)
                {
                    Vector4 rect;
                    Vector4 meta;
                    if (i == 0) { rect = rect0; meta = meta0; }
                    else if (i == 1) { rect = rect1; meta = meta1; }
                    else if (i == 2) { rect = rect2; meta = meta2; }
                    else { rect = rect3; meta = meta3; }

                    float eyeDepth = Mathf.Max(0.05f, meta.x);
                    // V76 dense architecture: HUD is NOT world geometry anymore.
                    // Raw depth 0 is explicitly invalid in the unchanged V51 shader, so exact
                    // HUD silhouettes punch source-depth holes and their color never enters the
                    // sparse forward warp. If V76 is disabled, retain old V72/V75 fake-depth mode.
                    float rawDepth = denseHudCutoutV76
                        ? 0.0f
                        : Mathf.Clamp01((1.0f / eyeDepth - z.w) / z.z);

                    int proxyKind = Mathf.RoundToInt(meta.z);
                    Mesh exactMesh = exactMeshes ? GetExactHudProxyMeshV73(proxyKind) : null;

                    _hudDepthProxyMaterialV72.SetFloat("_ProxyRawDepth", rawDepth);

                    if (exactMesh != null)
                    {
                        Matrix4x4 localToClip = BuildHudMeshLocalToClipV73(rect, exactMesh, proxyKind);
                        _hudDepthProxyMaterialV72.SetMatrix("_ProxyLocalToClip", localToClip);

                        if (!_hudDepthProxyMaterialV72.SetPass(1))
                        {
                            LogHudDepthProxyFailureV72("V73 exact-mesh Material.SetPass(1) returned false.");
                            return;
                        }

                        Graphics.DrawMeshNow(exactMesh, Matrix4x4.identity);
                    }
                    else
                    {
                        // Safe V72 fallback if the user-baked bundle cannot load for any reason.
                        _hudDepthProxyMaterialV72.SetVector("_ProxyRect", rect);
                        _hudDepthProxyMaterialV72.SetFloat("_ProxyShape", meta.y);

                        if (!_hudDepthProxyMaterialV72.SetPass(0))
                        {
                            LogHudDepthProxyFailureV72("HudDepthProxyV72 fallback Material.SetPass(0) returned false.");
                            return;
                        }

                        DrawHudDepthProxyQuadTopLeftUvV72(rect);
                    }
                }

                bool shouldLog = eye == 0 ? !_hudDepthProxyLoggedLeftV72 : !_hudDepthProxyLoggedRightV72;
                if (shouldLog)
                {
                    if (eye == 0) _hudDepthProxyLoggedLeftV72 = true;
                    else _hudDepthProxyLoggedRightV72 = true;

                    MelonLogger.Warning(
                        $"[StereoRender][AER V77][HudMask] APPLIED eye={(eye == 0 ? "L" : "R")} count={count} " +
                        $"mode={(exactMeshes ? "EXACT-COMPILED-MESH" : "V72-ANALYTIC-FALLBACK")}. " +
                        $"p0 GPS rect={rect0} depth={meta0.x:F3}m; " +
                        $"p1 RING referenceRect={rect1} depth={meta1.x:F3}m; " +
                        $"p2 TEXT rect={rect2} depth={meta2.x:F3}m. " +
                        $"depthMode={(denseHudCutoutV76 ? "INVALID-CUTOUT-FOR-DENSE-HUD" : "FAKE-0.5M-DEPTH")}. " +
                        "No visible geometry is rendered by this pass.");
                }
            }

            private static void DrawHudDepthProxyQuadTopLeftUvV72(Vector4 rect)
            {
                float x0 = rect.x * 2f - 1f;
                float x1 = rect.z * 2f - 1f;
                float y0 = 1f - rect.y * 2f;
                float y1 = 1f - rect.w * 2f;

                GL.PushMatrix();
                GL.LoadIdentity();
                GL.Begin(GL.QUADS);
                GL.TexCoord2(0f, 0f); GL.Vertex3(x0, y0, 0f);
                GL.TexCoord2(1f, 0f); GL.Vertex3(x1, y0, 0f);
                GL.TexCoord2(1f, 1f); GL.Vertex3(x1, y1, 0f);
                GL.TexCoord2(0f, 1f); GL.Vertex3(x0, y1, 0f);
                GL.End();
                GL.PopMatrix();
            }

            private static void LogHudDepthProxyFailureV72(string message)
            {
                if (_hudDepthProxyFailureLogsRemainingV72 <= 0)
                    return;
                _hudDepthProxyFailureLogsRemainingV72--;
                MelonLogger.Warning(
                    $"[StereoRender][AER V72][HudDepthProxy] {message} " +
                    $"Logs remaining={_hudDepthProxyFailureLogsRemainingV72}.");
            }

            private static bool ConfigureHudPlaneDepthProbeV69(
                StereoRender self,
                int completedEye,
                Camera sourceCamera)
            {
                if (_material == null)
                    return false;

                float baselineSeconds = Mathf.Max(0f, AerHudPlaneDepthProbeV69BaselineSeconds);
                float overrideSeconds = Mathf.Max(0f, AerHudPlaneDepthProbeV69OverrideSeconds);
                float elapsed = _hudPlaneProbeActivationTimeV69 >= 0f
                    ? Mathf.Max(0f, Time.unscaledTime - _hudPlaneProbeActivationTimeV69)
                    : 0f;

                int phase;
                bool overrideActive;
                if (!AerHudPlaneDepthProbeV69Enabled)
                {
                    phase = 2;
                    overrideActive = false;
                }
                else if (elapsed < baselineSeconds)
                {
                    phase = 0;
                    overrideActive = false;
                }
                else if (elapsed < baselineSeconds + overrideSeconds)
                {
                    phase = 1;
                    overrideActive = true;
                }
                else
                {
                    phase = 2;
                    overrideActive = false;
                }

                if (phase != _hudPlaneProbePhaseV69)
                {
                    _hudPlaneProbePhaseV69 = phase;
                    if (phase == 0)
                    {
                        MelonLogger.Warning(
                            $"[StereoRender][AER V69][HudDepthProbe] PHASE A BASELINE: native scene depth for HUD pixels. " +
                            $"Watch GPS/ring/MessageList jitter for {baselineSeconds:F1}s.");
                    }
                    else if (phase == 1)
                    {
                        MelonLogger.Warning(
                            $"[StereoRender][AER V69][HudDepthProbe] PHASE B FIXED HUD-PLANE DEPTH ACTIVE for " +
                            $"{overrideSeconds:F1}s: GPS aperture + visible vitals RawImage + MessageList use " +
                            $"{AerHudPlaneDepthProbeV69DepthMeters:F3}m reconstruction depth. Unity UI rendering itself is untouched.");
                    }
                    else
                    {
                        MelonLogger.Warning(
                            "[StereoRender][AER V69][HudDepthProbe] PHASE C REVERTED TO NATIVE SCENE DEPTH. " +
                            "If jitter vanished in B and returns now, the AER background-depth poisoning mechanism is directly confirmed.");
                    }
                }

                Vector4 r0 = Vector4.zero;
                Vector4 r1 = Vector4.zero;
                Vector4 r2 = Vector4.zero;
                Vector4 r3 = Vector4.zero;
                int rectCount = 0;

                if (overrideActive && VRPlayer.Instance != null && sourceCamera != null)
                {
                    try
                    {
                        rectCount = VRPlayer.Instance.GetHudDepthProbeRectsV69(
                            sourceCamera, out r0, out r1, out r2, out r3);
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Warning(
                            $"[StereoRender][AER V69][HudDepthProbe] rect projection failed: " +
                            $"{e.GetType().Name}: {e.Message}");
                        rectCount = 0;
                    }
                }

                bool actuallyActive = overrideActive && rectCount > 0;
                _material.SetFloat("_HudPlaneDepthOverrideEnabled", actuallyActive ? 1f : 0f);
                _material.SetFloat("_HudPlaneDepthMeters",
                    Mathf.Max(0.05f, AerHudPlaneDepthProbeV69DepthMeters));
                _material.SetFloat("_HudRectCount", rectCount);
                _material.SetVector("_HudRect0", r0);
                _material.SetVector("_HudRect1", r1);
                _material.SetVector("_HudRect2", r2);
                _material.SetVector("_HudRect3", r3);

                if (actuallyActive)
                {
                    bool shouldLog = completedEye == 0
                        ? !_hudPlaneProbeLoggedLeftRectsV69
                        : !_hudPlaneProbeLoggedRightRectsV69;
                    if (shouldLog)
                    {
                        if (completedEye == 0) _hudPlaneProbeLoggedLeftRectsV69 = true;
                        else _hudPlaneProbeLoggedRightRectsV69 = true;

                        MelonLogger.Warning(
                            $"[StereoRender][AER V69][HudDepthProbe] sourceEye={(completedEye == 0 ? "L" : "R")} " +
                            $"rectCount={rectCount} depth={AerHudPlaneDepthProbeV69DepthMeters:F3}m " +
                            $"r0={r0} r1={r1} r2={r2} r3={r3}. " +
                            "Rects are TOP-LEFT source-UV bounds; background inside them is intentionally flattened too for this diagnostic.");
                    }
                }

                return actuallyActive;
            }

            internal static bool PrepareV53LikeSameTimePairV84(
                StereoRender self,
                int completedEye)
            {
                _prepared = false;
                _preparedFrame = -1;

                if (!AerDepthStereoWarpV48Enabled ||
                    !AerV84V53LikeWorldPathEnabled ||
                    self == null ||
                    !AlternateEyeRenderingEnabled)
                    return false;

                if (completedEye != 0 && completedEye != 1)
                {
                    EnsureLoaded();
                    return false;
                }

                if (!EnsureLoaded())
                    return false;

                int targetEye = completedEye == 0 ? 1 : 0;
                Camera sourceCam = completedEye == 0 ? self.LeftCam : self.RightCam;
                Camera targetCam = targetEye == 0 ? self.LeftCam : self.RightCam;
                RenderTexture sourceRT = completedEye == 0 ? self.LeftRT : self.RightRT;
                RenderTexture oldTargetRT = targetEye == 0 ? self.LeftRT : self.RightRT;

                if (sourceCam == null || targetCam == null ||
                    sourceRT == null || oldTargetRT == null ||
                    !sourceRT.IsCreated() || !oldTargetRT.IsCreated())
                    return false;

                EyeDepthLatch depthLatch;
                if (!TryGetExactDepthLatch(self, completedEye, sourceCam, sourceRT, out depthLatch))
                {
                    EyeDepthLatch observed = completedEye == 0 ? _leftDepthLatch : _rightDepthLatch;
                    string observedState = observed == null ? "null" :
                        $"valid={observed.Valid} eye={observed.Eye} frame={observed.Frame} " +
                        $"camId={observed.CameraInstanceId} targetId={observed.SourceTargetInstanceId} " +
                        $"size={observed.SourceWidth}x{observed.SourceHeight} customDepth={observed.UsedCustomPassDepthV82}";
                    LogFailure(
                        $"V84 exact source depth unavailable: source={(completedEye == 0 ? "L" : "R")} " +
                        $"currentFrame={Time.frameCount}; observed[{observedState}]. Raw retained stereo will be used.");
                    return false;
                }

                RenderTexture depthRT = depthLatch.DepthRT;
                RenderTexture previous = RenderTexture.active;
                try
                {
                    RenderTexture syntheticRT = EnsureSyntheticTarget(targetEye, sourceRT);
                    RenderTexture warpRT = EnsureWarpTarget(targetEye, sourceRT);
                    RenderTexture fillRT = EnsureFillTarget(targetEye, sourceRT);
                    if (syntheticRT == null || warpRT == null || fillRT == null ||
                        !syntheticRT.IsCreated() || !warpRT.IsCreated() || !fillRT.IsCreated())
                        throw new Exception("one or more V84 synthetic/warp/fill RenderTextures are unavailable.");

                    EnsureWarpMesh();

                    _material.SetTexture("_SourceColor", sourceRT);
                    _material.SetTexture("_LatchedDepthTexture", depthRT);
                    _material.SetTexture("_FallbackTargetColor", oldTargetRT);
                    _material.SetVector("_ZBufferParams", depthLatch.ZParams);
                    _material.SetVector("_SourceSize", new Vector4(
                        sourceRT.width, sourceRT.height,
                        1.0f / Mathf.Max(1, sourceRT.width),
                        1.0f / Mathf.Max(1, sourceRT.height)));
                    _material.SetVector("_TargetSize", new Vector4(
                        syntheticRT.width, syntheticRT.height,
                        1.0f / Mathf.Max(1, syntheticRT.width),
                        1.0f / Mathf.Max(1, syntheticRT.height)));
                    _material.SetVector("_DepthAtlasSize", new Vector4(
                        depthRT.width, depthRT.height,
                        1.0f / Mathf.Max(1, depthRT.width),
                        1.0f / Mathf.Max(1, depthRT.height)));
                    _material.SetFloat("_DepthArraySlice", 0f);
                    _material.SetFloat("_MaxWarpMeters", Mathf.Max(1.0f, AerDepthStereoWarpV48MaxMeters));
                    _material.SetFloat("_DepthDiscontinuityMeters", Mathf.Max(0.001f, AerDepthStereoWarpV48DiscontinuityMeters));
                    _material.SetFloat("_DepthDiscontinuityRelative", Mathf.Max(0f, AerDepthStereoWarpV48DiscontinuityRelative));
                    _material.SetFloat("_NearMonoMeters", Mathf.Max(0.01f, AerDepthStereoWarpV49NearMonoMeters));
                    _material.SetFloat("_NearFullStereoMeters", Mathf.Max(AerDepthStereoWarpV49NearMonoMeters + 0.01f, AerDepthStereoWarpV49NearFullStereoMeters));
                    _material.SetFloat("_FarMonoStartMeters", Mathf.Max(1.0f, AerDepthStereoWarpV49FarMonoStartMeters));
                    _material.SetFloat("_FarMonoEndMeters", Mathf.Max(AerDepthStereoWarpV49FarMonoStartMeters + 1.0f, AerDepthStereoWarpV49FarMonoEndMeters));
                    _material.SetFloat("_QuarantineConfidenceFloor", Mathf.Clamp(AerDepthStereoWarpV49QuarantineConfidenceFloor, 0.001f, 0.25f));
                    _material.SetFloat("_FallbackBlendLowConfidence", Mathf.Clamp01(AerDepthStereoWarpV49FallbackBlendLowConfidence));
                    _material.SetFloat("_FallbackBlendFullConfidence", Mathf.Clamp01(AerDepthStereoWarpV49FallbackBlendFullConfidence));
                    _material.SetMatrix("_SourceInvGpuProjection", depthLatch.SourceInvGpuProjection);
                    _material.SetMatrix("_SourceCameraToWorld", depthLatch.SourceCameraToWorld);
                    _material.SetMatrix("_TargetWorldToCamera", depthLatch.TargetWorldToCamera);
                    _material.SetMatrix("_TargetGpuProjection", depthLatch.TargetGpuProjection);
                    _material.SetFloat("_ProjectiveSourceUvV86", 0f);
                    _material.SetFloat("_HudPlaneDepthOverrideEnabled", 0f);
                    _material.SetFloat("_HudRectCount", 0f);

                    SetRenderTargetFullViewportV77(warpRT);
                    GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
                    int warpPass = _material.FindPass("StereoWarp");
                    if (warpPass < 0 || !_material.SetPass(warpPass))
                        throw new Exception("DepthStereoWarp StereoWarp pass unavailable.");
                    Graphics.DrawMeshNow(_warpMesh, Matrix4x4.identity);

                    _material.SetTexture("_WarpInput", warpRT);
                    _material.SetFloat("_FillRadiusPixels", 1.0f);
                    _material.SetFloat("_FillConfidenceDecay", Mathf.Clamp01(AerDepthStereoWarpV49FillConfidenceDecay));
                    SetRenderTargetFullViewportV77(syntheticRT);
                    GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
                    int fillPass = _material.FindPass("HoleFill");
                    if (fillPass < 0 || !_material.SetPass(fillPass))
                        throw new Exception("DepthStereoWarp HoleFill pass unavailable.");
                    DrawFullscreenTopLeftUvQuad();

                    _material.SetTexture("_WarpInput", syntheticRT);
                    _material.SetFloat("_FillRadiusPixels", 2.0f);
                    SetRenderTargetFullViewportV77(fillRT);
                    GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
                    if (!_material.SetPass(fillPass))
                        throw new Exception("DepthStereoWarp HoleFill radius-2 SetPass failed.");
                    DrawFullscreenTopLeftUvQuad();

                    // V36 remains forbidden. Remaining holes use raw old TARGET-eye color.
                    _material.SetTexture("_WarpInput", fillRT);
                    _material.SetTexture("_FallbackTargetColor", oldTargetRT);
                    SetRenderTargetFullViewportV77(syntheticRT);
                    GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
                    int fallbackPass = _material.FindPass("TargetEyeHoleFallback");
                    if (fallbackPass < 0 || !_material.SetPass(fallbackPass))
                        throw new Exception("DepthStereoWarp TargetEyeHoleFallback pass unavailable.");
                    DrawFullscreenTopLeftUvQuad();

                    // Keep the latest HUD work rather than reverting to V53 HUD rectangles.
                    CompositeDenseHudV76(self, completedEye, sourceCam, targetCam, sourceRT, syntheticRT);
                    RestoreRenderTargetViewportV77(previous);

                    _prepared = true;
                    _preparedFrame = Time.frameCount;
                    _preparedSourceEye = completedEye;
                    _preparedTargetEye = targetEye;
                    _preparedSourceRT = sourceRT;
                    _preparedSyntheticRT = syntheticRT;
                    _preparedOldStaleRT = oldTargetRT;
                    _preparedDepthTexture = depthRT;
                    _preparedZParams = depthLatch.ZParams;
                    _preparedSourceWidth = sourceRT.width;
                    _preparedSourceHeight = sourceRT.height;
                    _preparedDepthWidth = depthRT.width;
                    _preparedDepthHeight = depthRT.height;
                    _preparedHistoryFrameV83 = -1;
                    _preparedHistoryAgeFramesV83 = -1;
                    _preparedHistoryAgeMsV83 = -1f;
                    _lastFreshLabelV83 = completedEye == 0 ? "L" : "R";
                    _lastSourceLabel = completedEye == 0 ? "L" : "R";
                    _lastTargetLabel = targetEye == 0 ? "L" : "R";

                    if (!_firstPreparedLogged)
                    {
                        _firstPreparedLogged = true;
                        MelonLogger.Warning(
                            $"[StereoRender][AER V84][PerfAnchor] V53-LIKE WORLD PATH READY: " +
                            $"fresh={_lastSourceLabel}(t) -> synthetic={_lastTargetLabel}(t); " +
                            "depth=OLD GLOBAL _CameraDepthTexture mip0 latch; latest V76 HUD retained; " +
                            "V36 OFF; CustomPass/forensics/desktop board OFF.");
                    }
                    return true;
                }
                catch (Exception e)
                {
                    RestoreRenderTargetViewportV77(previous);
                    _prepared = false;
                    LogFailure("V84 V53-like synthesis failed: " + e.Message + "; raw retained stereo will be used.");
                    return false;
                }
            }

            internal static bool PrepareSameEyeTemporalPairV83(
                StereoRender self,
                int completedEye)
            {
                _prepared = false;
                _preparedFrame = -1;

                if (!AerDepthStereoWarpV48Enabled ||
                    !AerTemporalSameEyeV83Enabled ||
                    self == null ||
                    !AlternateEyeRenderingEnabled)
                    return false;

                // Warmup/resync renders BOTH eyes for real. No prediction is needed.
                if (completedEye != 0 && completedEye != 1)
                {
                    EnsureLoaded();
                    return false;
                }

                if (!EnsureLoaded())
                    return false;

                int targetEye = completedEye == 0 ? 1 : 0;

                Camera freshCam = completedEye == 0 ? self.LeftCam : self.RightCam;
                Camera targetCam = targetEye == 0 ? self.LeftCam : self.RightCam;

                RenderTexture freshRT = completedEye == 0 ? self.LeftRT : self.RightRT;

                // CRITICAL V83 distinction:
                // The temporal source is the TARGET EYE'S OWN LAST REAL RENDER.
                // If LEFT just rendered, source history is old RIGHT color/depth and target is
                // desired RIGHT now. There is no L->R world synthesis anywhere in this method.
                RenderTexture historyColorRT = targetEye == 0 ? self.LeftRT : self.RightRT;

                if (freshCam == null || targetCam == null ||
                    freshRT == null || historyColorRT == null ||
                    !freshRT.IsCreated() || !historyColorRT.IsCreated())
                    return false;

                EyeDepthLatch historyLatch;
                int historyAgeFrames;
                if (!TryGetTemporalHistoryDepthV83(
                    targetEye,
                    targetCam,
                    historyColorRT,
                    out historyLatch,
                    out historyAgeFrames))
                {
                    EyeDepthLatch observed = targetEye == 0
                        ? _leftDepthLatch
                        : _rightDepthLatch;

                    string observedState = observed == null
                        ? "null"
                        : $"valid={observed.Valid} eye={observed.Eye} frame={observed.Frame} " +
                          $"camId={observed.CameraInstanceId} targetId={observed.SourceTargetInstanceId} " +
                          $"size={observed.SourceWidth}x{observed.SourceHeight} " +
                          $"customDepth={observed.UsedCustomPassDepthV82}";

                    LogFailure(
                        $"V85 SAME-EYE history unavailable: fresh={(completedEye == 0 ? "L" : "R")} " +
                        $"target/history={(targetEye == 0 ? "L" : "R")} currentFrame={Time.frameCount}; " +
                        $"observed[{observedState}]. V36 is HARD-OFF; raw retained pair will be used.");
                    return false;
                }

                RenderTexture historyDepthRT = historyLatch.DepthRT;
                RenderTexture previous = RenderTexture.active;

                try
                {
                    RenderTexture syntheticRT = EnsureSyntheticTarget(targetEye, freshRT);
                    RenderTexture warpRT = EnsureWarpTarget(targetEye, freshRT);
                    RenderTexture fillRT = EnsureFillTarget(targetEye, freshRT);

                    if (syntheticRT == null || warpRT == null || fillRT == null ||
                        !syntheticRT.IsCreated() || !warpRT.IsCreated() || !fillRT.IsCreated())
                        throw new Exception("one or more V85 synthetic/warp/fill RenderTextures are unavailable.");

                    EnsureWarpMesh();

                    // History source and desired target are THE SAME EYE at different times.
                    Vector4 zParams = historyLatch.ZParams;

                    // V94: A/B/A ONLY the matrices used to unproject the historical depth.
                    // Phase B uses the exact HDCamera projection/view captured in the same
                    // CustomPass execution that produced historyDepthRT. Target/current matrices
                    // remain untouched, so this isolates a source-raster truth mismatch.
                    if (_hdrpMatrixAbStartTimeV94 < 0f)
                        _hdrpMatrixAbStartTimeV94 = Time.unscaledTime;

                    float hdrpMatrixElapsedV94 =
                        Time.unscaledTime - _hdrpMatrixAbStartTimeV94;
                    int hdrpMatrixPhaseV94 = 0; // A
                    if (AerV94HdrpSourceMatrixABEnabled &&
                        hdrpMatrixElapsedV94 >= Mathf.Max(1f, AerV94HdrpSourceMatrixStartSeconds) &&
                        hdrpMatrixElapsedV94 < Mathf.Max(
                            AerV94HdrpSourceMatrixStartSeconds + 1f,
                            AerV94HdrpSourceMatrixEndSeconds))
                        hdrpMatrixPhaseV94 = 1; // B
                    else if (AerV94HdrpSourceMatrixABEnabled &&
                             hdrpMatrixElapsedV94 >= Mathf.Max(
                                 AerV94HdrpSourceMatrixStartSeconds + 1f,
                                 AerV94HdrpSourceMatrixEndSeconds))
                        hdrpMatrixPhaseV94 = 2; // A2

                    bool hdrpSourceMatricesPairedV94 =
                        historyLatch.HdrpRasterMatricesValidV94 &&
                        historyLatch.HdrpRasterMatrixFrameV94 == historyLatch.Frame;

                    // Fail-soft convention guard. If the generated HDRP binding unexpectedly
                    // exposes a differently-handed/projected matrix convention, do NOT feed it
                    // to the headset merely because the property name looked right. A genuine
                    // timing/jitter correction should be a small perturbation around identity.
                    float hdrpProjConventionDeltaV94 = hdrpSourceMatricesPairedV94
                        ? MatrixMaxAbsFromIdentityV94(
                            historyLatch.HdrpProjectionV94 *
                            historyLatch.CameraGpuProjectionAtDepthV94.inverse)
                        : -1f;
                    float hdrpViewConventionDeltaV94 = hdrpSourceMatricesPairedV94
                        ? MatrixMaxAbsFromIdentityV94(
                            historyLatch.HdrpWorldToCameraRHSV94 *
                            historyLatch.CameraWorldToCameraAtDepthV94.inverse)
                        : -1f;

                    bool hdrpSourceMatricesAvailableV94 =
                        hdrpSourceMatricesPairedV94 &&
                        hdrpProjConventionDeltaV94 < 0.25f &&
                        hdrpViewConventionDeltaV94 < 0.25f;

                    bool useHdrpSourceMatricesV94 =
                        hdrpMatrixPhaseV94 == 1 &&
                        hdrpSourceMatricesAvailableV94;

                    Matrix4x4 sourceInvGpuProjection =
                        useHdrpSourceMatricesV94
                            ? historyLatch.HdrpProjectionV94.inverse
                            : historyLatch.SourceInvGpuProjection;
                    Matrix4x4 sourceCameraToWorld =
                        useHdrpSourceMatricesV94
                            ? historyLatch.HdrpWorldToCameraRHSV94.inverse
                            : historyLatch.SourceCameraToWorld;

                    if (hdrpMatrixPhaseV94 == 1 &&
                        !hdrpSourceMatricesAvailableV94 &&
                        !_hdrpMatrixUnavailableLoggedV94)
                    {
                        _hdrpMatrixUnavailableLoggedV94 = true;
                        MelonLogger.Warning(
                            $"[StereoRender][AER V94][MatrixAB] Phase B requested exact HDCamera " +
                            $"source matrices, but the pair is missing or failed the convention guard " +
                            $"(historyFrame={historyLatch.Frame}, matrixFrame=" +
                            $"{historyLatch.HdrpRasterMatrixFrameV94}, paired={hdrpSourceMatricesPairedV94}, " +
                            $"projDelta={hdrpProjConventionDeltaV94:E3}, viewDelta={hdrpViewConventionDeltaV94:E3}). " +
                            $"Staying on Camera matrices fail-soft.");
                    }

                    if (hdrpMatrixPhaseV94 != _hdrpMatrixAbLastPhaseV94)
                    {
                        _hdrpMatrixAbLastPhaseV94 = hdrpMatrixPhaseV94;

                        float projMaxV94 = hdrpSourceMatricesAvailableV94
                            ? MatrixMaxAbsDiffV94(
                                historyLatch.HdrpProjectionV94,
                                historyLatch.CameraGpuProjectionAtDepthV94)
                            : -1f;
                        float viewMaxV94 = hdrpSourceMatricesAvailableV94
                            ? MatrixMaxAbsDiffV94(
                                historyLatch.HdrpWorldToCameraRHSV94,
                                historyLatch.CameraWorldToCameraAtDepthV94)
                            : -1f;

                        Matrix4x4 endGpuProjectionV94 =
                            GL.GetGPUProjectionMatrix(historyLatch.SourceProjection, true);
                        float cameraDepthToEndProjMaxV94 = hdrpSourceMatricesAvailableV94
                            ? MatrixMaxAbsDiffV94(
                                historyLatch.CameraGpuProjectionAtDepthV94,
                                endGpuProjectionV94)
                            : -1f;
                        float cameraDepthToEndViewMaxV94 = hdrpSourceMatricesAvailableV94
                            ? MatrixMaxAbsDiffV94(
                                historyLatch.CameraWorldToCameraAtDepthV94,
                                historyLatch.SourceWorldToCamera)
                            : -1f;

                        string phaseTextV94 =
                            hdrpMatrixPhaseV94 == 1
                                ? "B EXACT-HDCAMERA-SOURCE"
                                : hdrpMatrixPhaseV94 == 2
                                    ? "A2 RETURN-CAMERA-SOURCE"
                                    : "A CAMERA-SOURCE baseline";

                        MelonLogger.Warning(
                            $"[StereoRender][AER V94][MatrixAB] phase={phaseTextV94} " +
                            $"elapsed={hdrpMatrixElapsedV94:F1}s historyEye={(targetEye == 0 ? "L" : "R")} " +
                            $"historyFrame={historyLatch.Frame} pairedHdrp={hdrpSourceMatricesPairedV94} " +
                            $"compatibleHdrp={hdrpSourceMatricesAvailableV94} " +
                            $"projConventionDelta={hdrpProjConventionDeltaV94:E3} " +
                            $"viewConventionDelta={hdrpViewConventionDeltaV94:E3} " +
                            $"hdVsCamAtDepthProj={projMaxV94:E3} hdVsCamAtDepthView={viewMaxV94:E3} " +
                            $"camDepthVsEndProj={cameraDepthToEndProjMaxV94:E3} " +
                            $"camDepthVsEndView={cameraDepthToEndViewMaxV94:E3}. " +
                            $"NVOFA executes but residual is ignored throughout V94.");
                    }

                    // V88: V87 showed no perceptual benefit from alternating pose-timing
                    // phases, so remove that confound. Prefer the render-boundary snapshot
                    // every frame; fail soft to EOF-late only if the exact snapshot is absent.
                    bool poseSnapshotCurrentV88 =
                        AerTemporalUseRenderBoundaryPoseV88 &&
                        _poseSnapshotValidV87 &&
                        _poseSnapshotFrameV87 == Time.frameCount &&
                        _poseSnapshotFreshEyeV87 == completedEye;
                    bool useFrozenPoseV87 = poseSnapshotCurrentV88;

                    Matrix4x4 targetWorldToCamera;
                    Matrix4x4 targetProjection;
                    Vector3 targetPositionForTelemetryV87;
                    Quaternion targetRotationForTelemetryV87;

                    if (useFrozenPoseV87)
                    {
                        if (targetEye == 0)
                        {
                            targetWorldToCamera = _poseSnapshotLeftWorldToCameraV87;
                            targetProjection = _poseSnapshotLeftProjectionV87;
                            targetPositionForTelemetryV87 = _poseSnapshotLeftPositionV87;
                            targetRotationForTelemetryV87 = _poseSnapshotLeftRotationV87;
                        }
                        else
                        {
                            targetWorldToCamera = _poseSnapshotRightWorldToCameraV87;
                            targetProjection = _poseSnapshotRightProjectionV87;
                            targetPositionForTelemetryV87 = _poseSnapshotRightPositionV87;
                            targetRotationForTelemetryV87 = _poseSnapshotRightRotationV87;
                        }
                    }
                    else
                    {
                        targetWorldToCamera = targetCam.worldToCameraMatrix;
                        targetProjection = targetCam.projectionMatrix;
                        targetPositionForTelemetryV87 = targetCam.transform.position;
                        targetRotationForTelemetryV87 = targetCam.transform.rotation;

                        if (AerTemporalUseRenderBoundaryPoseV88 &&
                            !_poseTimingAbMissingSnapshotLoggedV87)
                        {
                            _poseTimingAbMissingSnapshotLoggedV87 = true;
                            MelonLogger.Warning(
                                $"[StereoRender][AER V91][Pose] current-frame render-boundary " +
                                $"snapshot unavailable (snapshotFrame={_poseSnapshotFrameV87}, " +
                                $"current={Time.frameCount}, snapshotFresh={_poseSnapshotFreshEyeV87}, " +
                                $"completedFresh={completedEye}). Using EOF-LATE matrices fail-soft.");
                        }
                    }

                    Matrix4x4 targetGpuProjection =
                        GL.GetGPUProjectionMatrix(targetProjection, true);
                    float maxWarpMetersV87 = ResolveMaxWarpMetersV87(targetCam, targetCam);

                    _material.SetTexture("_SourceColor", historyColorRT);
                    _material.SetTexture("_LatchedDepthTexture", historyDepthRT);
                    _material.SetTexture("_FallbackTargetColor", historyColorRT);

                    _material.SetVector("_ZBufferParams", zParams);
                    _material.SetVector("_SourceSize", new Vector4(
                        historyColorRT.width, historyColorRT.height,
                        1.0f / Mathf.Max(1, historyColorRT.width),
                        1.0f / Mathf.Max(1, historyColorRT.height)));
                    _material.SetVector("_TargetSize", new Vector4(
                        syntheticRT.width, syntheticRT.height,
                        1.0f / Mathf.Max(1, syntheticRT.width),
                        1.0f / Mathf.Max(1, syntheticRT.height)));
                    _material.SetVector("_DepthAtlasSize", new Vector4(
                        historyDepthRT.width, historyDepthRT.height,
                        1.0f / Mathf.Max(1, historyDepthRT.width),
                        1.0f / Mathf.Max(1, historyDepthRT.height)));

                    _material.SetFloat("_DepthArraySlice", 0f);
                    _material.SetFloat("_MaxWarpMeters", maxWarpMetersV87);
                    _material.SetFloat("_DepthDiscontinuityMeters",
                        Mathf.Max(0.001f, AerDepthStereoWarpV48DiscontinuityMeters));
                    _material.SetFloat("_DepthDiscontinuityRelative",
                        Mathf.Max(0f, AerDepthStereoWarpV48DiscontinuityRelative));
                    _material.SetFloat("_NearMonoMeters",
                        Mathf.Max(0.01f, AerDepthStereoWarpV49NearMonoMeters));
                    _material.SetFloat("_NearFullStereoMeters",
                        Mathf.Max(AerDepthStereoWarpV49NearMonoMeters + 0.01f,
                                  AerDepthStereoWarpV49NearFullStereoMeters));
                    _material.SetFloat("_FarMonoStartMeters",
                        Mathf.Max(1.0f, AerDepthStereoWarpV49FarMonoStartMeters));
                    _material.SetFloat("_FarMonoEndMeters",
                        Mathf.Max(AerDepthStereoWarpV49FarMonoStartMeters + 1.0f,
                                  AerDepthStereoWarpV49FarMonoEndMeters));
                    _material.SetFloat("_QuarantineConfidenceFloor",
                        Mathf.Clamp(AerDepthStereoWarpV49QuarantineConfidenceFloor, 0.001f, 0.25f));
                    _material.SetFloat("_FallbackBlendLowConfidence",
                        Mathf.Clamp01(AerDepthStereoWarpV49FallbackBlendLowConfidence));
                    _material.SetFloat("_FallbackBlendFullConfidence",
                        Mathf.Clamp01(AerDepthStereoWarpV49FallbackBlendFullConfidence));

                    _material.SetMatrix("_SourceInvGpuProjection", sourceInvGpuProjection);
                    _material.SetMatrix("_SourceCameraToWorld", sourceCameraToWorld);
                    _material.SetMatrix("_TargetWorldToCamera", targetWorldToCamera);
                    _material.SetMatrix("_TargetGpuProjection", targetGpuProjection);
                    _material.SetFloat(
                        "_ProjectiveSourceUvV86",
                        AerTemporalProjectiveSourceCoordinatesV86Enabled ? 1f : 0f);

                    // Old V69 A/B is disabled; explicitly clear material state.
                    _material.SetFloat("_HudPlaneDepthOverrideEnabled", 0f);
                    _material.SetFloat("_HudRectCount", 0f);

                    // V94 keeps NVOFA's hardware workload/history alive but deliberately
                    // ignores its residual so the original pre-OF field-dependent jitter is
                    // visible. The timed A/B/A changes only historical SOURCE raster matrices.
                    if (_opticalFlowAbStartTimeV90 < 0f)
                        _opticalFlowAbStartTimeV90 = Time.unscaledTime;

                    bool opticalFlowRequestedV90 =
                        AerTemporalOpticalFlowV90Enabled;

                    RenderTexture historyOpticalFlowRTV90;
                    float opticalFlowTimeScaleV90;
                    double opticalFlowNativeApiMsV90;
                    ulong opticalFlowExecuteCountV90;
                    bool opticalFlowAvailableV90 =
                        TryGetTemporalOpticalFlowV90(
                            targetEye,
                            historyLatch,
                            out historyOpticalFlowRTV90,
                            out opticalFlowTimeScaleV90,
                            out opticalFlowNativeApiMsV90,
                            out opticalFlowExecuteCountV90);

                    bool suppressOpticalFlowResidualV94 =
                        AerV94HdrpSourceMatrixABEnabled &&
                        AerV94ExposePreOpticalFlowJitter;
                    bool suppressOpticalFlowResidualV98 =
                        AerV98PresentationGridScatterABEnabled &&
                        AerV98ExposePreOpticalFlowJitter;
                    bool suppressOpticalFlowResidualV99 =
                        AerV99ForensicsEnabled && AerV99ExposePreOpticalFlowJitter;
                    bool suppressOpticalFlowResidualV103 =
                        AerV103DepthGuardFillPerceptualTestEnabled && AerV103ExposePreOpticalFlowJitter;
                    bool suppressOpticalFlowResidualDiagnostic =
                        suppressOpticalFlowResidualV94 || suppressOpticalFlowResidualV98 ||
                        suppressOpticalFlowResidualV99 || suppressOpticalFlowResidualV103;

                    bool useOpticalFlowV90 =
                        opticalFlowRequestedV90 &&
                        opticalFlowAvailableV90 &&
                        !suppressOpticalFlowResidualDiagnostic;

                    // V98 A/B/A: compare the current DLSS-internal depth-grid dispatch against
                    // a presentation-grid dispatch.  In Phase B each historical POST-DLSS color
                    // pixel gets its own source thread/ray, while depth is looked up from the same
                    // paired low-resolution camera-local depth texture by normalized nearest UV.
                    // This restores V88's original meaning of "per source color pixel" without
                    // inventing extra depth detail.
                    if (_presentationGridScatterAbStartTimeV98 < 0f)
                        _presentationGridScatterAbStartTimeV98 = Time.unscaledTime;

                    float presentationGridElapsedV98 =
                        Time.unscaledTime - _presentationGridScatterAbStartTimeV98;
                    int presentationGridPhaseV98 = 0; // A
                    if (AerV98PresentationGridScatterABEnabled &&
                        presentationGridElapsedV98 >= Mathf.Max(1f, AerV98PresentationGridScatterStartSeconds) &&
                        presentationGridElapsedV98 < Mathf.Max(
                            AerV98PresentationGridScatterStartSeconds + 1f,
                            AerV98PresentationGridScatterEndSeconds))
                        presentationGridPhaseV98 = 1; // B
                    else if (AerV98PresentationGridScatterABEnabled &&
                             presentationGridElapsedV98 >= Mathf.Max(
                                 AerV98PresentationGridScatterStartSeconds + 1f,
                                 AerV98PresentationGridScatterEndSeconds))
                        presentationGridPhaseV98 = 2; // A2

                    bool usePresentationGridScatterV98 =
                        AerV98PresentationGridScatterABEnabled &&
                        presentationGridPhaseV98 == 1;

                    // V141: preserve the depth-grid scatter in production. The historical
                    // V98 AB switch remains OFF; no presentation-grid override is installed.

                    if (presentationGridPhaseV98 != _presentationGridScatterAbLastPhaseV98)
                    {
                        _presentationGridScatterAbLastPhaseV98 = presentationGridPhaseV98;
                        double depthSamplesV98 =
                            (double)historyDepthRT.width * historyDepthRT.height;
                        double presentationSamplesV98 =
                            (double)historyColorRT.width * historyColorRT.height;
                        double directDensityV98 = presentationSamplesV98 > 0.0
                            ? depthSamplesV98 / presentationSamplesV98
                            : 0.0;
                        string phaseTextV98 = presentationGridPhaseV98 == 1
                            ? "B PRESENTATION-GRID"
                            : presentationGridPhaseV98 == 2
                                ? "A2 RETURN-INTERNAL-DEPTH-GRID"
                                : "A INTERNAL-DEPTH-GRID baseline";

                        MelonLogger.Warning(
                            $"[StereoRender][AER V98][PresentationGridAB] phase={phaseTextV98} " +
                            $"elapsed={presentationGridElapsedV98:F1}s " +
                            $"depthGrid={historyDepthRT.width}x{historyDepthRT.height} " +
                            $"historyColor={historyColorRT.width}x{historyColorRT.height} " +
                            $"target={warpRT.width}x{warpRT.height} " +
                            $"depthPixels/presentationPixels={directDensityV98:F4}. " +
                            "NVOFA hardware executes but residual is ignored throughout V98.");
                    }

                    // V101 live A/B/A: source pixels with NO opaque depth (HDRP sky/background)
                    // either keep the ancestral "drop them from scatter" behavior (A/A2), or in B
                    // rotate their rays as infinity BEFORE hole fill.  Thread lattice, DLSS, finite
                    // depth math and NVOFA workload remain unchanged at the switch.
                    if (_skyInfinityScatterAbStartTimeV101 < 0f)
                        _skyInfinityScatterAbStartTimeV101 = Time.unscaledTime;

                    float skyInfinityElapsedV101 =
                        Time.unscaledTime - _skyInfinityScatterAbStartTimeV101;
                    int skyInfinityPhaseV101 = 0; // A
                    if (AerV101SkyInfinityScatterABEnabled &&
                        skyInfinityElapsedV101 >= Mathf.Max(1f, AerV101SkyInfinityScatterStartSeconds) &&
                        skyInfinityElapsedV101 < Mathf.Max(
                            AerV101SkyInfinityScatterStartSeconds + 1f,
                            AerV101SkyInfinityScatterEndSeconds))
                        skyInfinityPhaseV101 = 1; // B
                    else if (AerV101SkyInfinityScatterABEnabled &&
                             skyInfinityElapsedV101 >= Mathf.Max(
                                 AerV101SkyInfinityScatterStartSeconds + 1f,
                                 AerV101SkyInfinityScatterEndSeconds))
                        skyInfinityPhaseV101 = 2; // A2

                    bool useInvalidDepthRotationScatterV101 =
                        AerV101SkyInfinityScatterABEnabled && skyInfinityPhaseV101 == 1;

                    if (skyInfinityPhaseV101 != _skyInfinityScatterAbLastPhaseV101)
                    {
                        _skyInfinityScatterAbLastPhaseV101 = skyInfinityPhaseV101;
                        string phaseTextV101 = skyInfinityPhaseV101 == 1
                            ? "B ROTATION-SCATTER NO-DEPTH/SKY BEFORE FILL"
                            : skyInfinityPhaseV101 == 2
                                ? "A2 RETURN DROP-NO-DEPTH-SOURCE baseline"
                                : "A DROP-NO-DEPTH-SOURCE baseline";
                        MelonLogger.Warning(
                            $"[StereoRender][AER V101][SkyInfinityAB] phase={phaseTextV101} " +
                            $"elapsed={skyInfinityElapsedV101:F1}s. " +
                            "V100 rotation-only FINAL-hole fallback remains ON in every phase; " +
                            "only pre-fill treatment of rawDepth==0 source pixels changes.");
                    }

                    bool usedPerPixelScatterV88 = false;
                    if (AerTemporalPerPixelScatterV88Enabled)
                    {
                        usedPerPixelScatterV88 = TryRenderPerPixelScatterV90(
                            historyColorRT,
                            historyDepthRT,
                            null, // V90 isolates NVOFA; V89 engine-motion texture intentionally unused
                            historyOpticalFlowRTV90,
                            warpRT,
                            zParams,
                            sourceInvGpuProjection,
                            sourceCameraToWorld,
                            targetWorldToCamera,
                            targetGpuProjection,
                            historyLatch.PreviousRealWorldToCameraV89,
                            historyLatch.PreviousRealGpuProjectionV89,
                            false,
                            AerTemporalObjectMotionScaleV89,
                            AerTemporalObjectMotionMaxPixelsV89,
                            useOpticalFlowV90,
                            opticalFlowTimeScaleV90,
                            AerTemporalOpticalFlowMaxResidualPixelsV90,
                            usePresentationGridScatterV98,
                            useInvalidDepthRotationScatterV101,
                            maxWarpMetersV87);
                    }

                    if (usedPerPixelScatterV88 &&
                        opticalFlowAvailableV90 &&
                        suppressOpticalFlowResidualDiagnostic &&
                        !_opticalFlowSwitchLoggedV90)
                    {
                        _opticalFlowSwitchLoggedV90 = true;
                        MelonLogger.Warning(
                            $"[StereoRender][AER V98][NVOFA] hardware flow is RUNNING but residual " +
                            $"is intentionally IGNORED so the raw pre-OF scatter-lattice defect remains visible. " +
                            $"measuredTimeScale={opticalFlowTimeScaleV90:F3} " +
                            $"executeCount={opticalFlowExecuteCountV90}.");
                    }
                    else if (usedPerPixelScatterV88 && useOpticalFlowV90 &&
                             !_opticalFlowSwitchLoggedV90)
                    {
                        _opticalFlowSwitchLoggedV90 = true;
                        MelonLogger.Warning(
                            $"[StereoRender][AER V94][NVOFA] residual ACTIVE (V94 raw-jitter " +
                            $"exposure knob is OFF). measuredTimeScale={opticalFlowTimeScaleV90:F3} " +
                            $"executeCount={opticalFlowExecuteCountV90}.");
                    }
                    else if (opticalFlowRequestedV90 && !opticalFlowAvailableV90 &&
                             !_opticalFlowUnavailableLoggedV90)
                    {
                        _opticalFlowUnavailableLoggedV90 = true;
                        MelonLogger.Warning(
                            "[StereoRender][AER V91][OpticalFlowAB] optical-flow phase requested but " +
                            "the exact CURRENT->PREVIOUS NVOFA field is unavailable; continuing V88 " +
                            "depth-only compute fail-soft. Check [NVOFA] bridge logs.");
                    }

                    if (opticalFlowAvailableV90 && !_opticalFlowPairingLoggedV90)
                    {
                        _opticalFlowPairingLoggedV90 = true;
                        float flowIntervalMsV90 =
                            (historyLatch.CaptureUnscaledTime -
                             historyLatch.PreviousRealCaptureUnscaledTimeV90) * 1000f;
                        float predictionHorizonMsV90 =
                            (Time.unscaledTime - historyLatch.CaptureUnscaledTime) * 1000f;
                        MelonLogger.Msg(
                            $"[StereoRender][AER V91][OpticalFlowPair] READY eye={(targetEye == 0 ? "L" : "R")} " +
                            $"flowFrame={historyLatch.Frame} prevRealFrame={historyLatch.PreviousRealFrameV89} " +
                            $"size={historyOpticalFlowRTV90.width}x{historyOpticalFlowRTV90.height} " +
                            $"dtFlow={flowIntervalMsV90:F1}ms dtPredict={predictionHorizonMsV90:F1}ms " +
                            $"timeScale={opticalFlowTimeScaleV90:F3} nativeApiCall={opticalFlowNativeApiMsV90:F3}ms " +
                            $"executeCount={opticalFlowExecuteCountV90}. " +
                            "NVOFA direction=CURRENT->PREVIOUS; camera residual is subtracted in compute.");
                    }

                    if (!usedPerPixelScatterV88)
                    {
                        if (!_perPixelUnavailableLoggedV88)
                        {
                            _perPixelUnavailableLoggedV88 = true;
                            MelonLogger.Warning(
                                "[StereoRender][AER V91][Scatter] full-res compute unavailable; " +
                                "using 768 mesh fail-soft. This run is not a valid NVOFA A/B until compute recovers.");
                        }

                        SetRenderTargetFullViewportV77(warpRT);
                        GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));

                        int warpPass = _material.FindPass("StereoWarp");
                        if (warpPass < 0 || !_material.SetPass(warpPass))
                            throw new Exception("DepthStereoWarp StereoWarp pass unavailable.");

                        Graphics.DrawMeshNow(_warpMesh, Matrix4x4.identity);
                    }

                    string opticalFlowModeV90 =
                        useOpticalFlowV90
                            ? "NVOFA-RESIDUAL"
                            : (suppressOpticalFlowResidualDiagnostic && opticalFlowAvailableV90
                                ? "NVOFA-GENERATED-IGNORED-V99"
                                : (opticalFlowRequestedV90 ? "REQUESTED-UNAVAILABLE" : "OFF"));

                    // V141 PRODUCTION: pin V140 Profile B (DEPTH GRID / NO SPATIAL FILL).
                    // 1=V103 guarded spatial R1/R2 fill (config rollback), 2=skip both passes.
                    // The proven V100 rotation-only true-hole fallback runs in either case.
                    // The historical experimental phase switch remains disabled in production.
                    int fillModeV103 = AerV104DepthGuardedFillPermanentEnabled ? 1 : 2;
                    if (AerV103DepthGuardFillPerceptualTestEnabled)
                        fillModeV103 = UpdateV103PerceptualPhase();
                    if (fillModeV103 != _v141LastSpatialFillMode)
                    {
                        _v141LastSpatialFillMode = fillModeV103;
                        MelonLogger.Msg("[V141 AER-PROFILE] ACTIVE " +
                            (fillModeV103 == 2 ? "DEPTH_GRID_NO_SPATIAL_FILL" :
                             fillModeV103 == 1 ? "V103_GUARDED_FILL_ROLLBACK" : "HISTORICAL_MODE") +
                            " depth=" + historyDepthRT.width + "x" + historyDepthRT.height +
                            " color=" + historyColorRT.width + "x" + historyColorRT.height +
                            " V100RotationFallback=ON");
                    }

                    bool canDepthGuardFillV103 =
                        fillModeV103 == 1 &&
                        usedPerPixelScatterV88 &&
                        _scatterDepthKeyRTV88 != null &&
                        _scatterDepthKeyRTV88.IsCreated();

                    // V104 fail-safe: NEVER resurrect the convicted legacy cross-depth fill.
                    // If the guard cannot read the direct-scatter depth key, leave spatial holes
                    // untouched and let V100's rotation-only true-hole fallback handle them.
                    if (fillModeV103 == 1 && !canDepthGuardFillV103)
                    {
                        fillModeV103 = 2;
                        if (!_v103DepthGuardUnavailableLogged)
                        {
                            _v103DepthGuardUnavailableLogged = true;
                            MelonLogger.Warning(
                                "[StereoRender][AER V104][DepthGuard] guarded fill requested but direct-scatter " +
                                "depth-key is unavailable; FAIL-SAFE = NO spatial fill + V100 rotation fallback. " +
                                "Legacy unguarded HoleFill will NOT be used.");
                        }
                    }

                    RenderTexture preFallbackRTV103 = warpRT;
                    _material.SetFloat("_FillConfidenceDecay",
                        Mathf.Clamp01(AerDepthStereoWarpV49FillConfidenceDecay));

                    if (fillModeV103 != 2)
                    {
                        int fillPassV103;
                        if (fillModeV103 == 1)
                        {
                            fillPassV103 = _material.FindPass("HoleFillDepthGuardV103");
                            if (fillPassV103 < 0)
                                throw new Exception(
                                    "DepthStereoWarp HoleFillDepthGuardV103 pass unavailable.");

                            // The guarded pass may preserve center pixels from the previous fill
                            // stage, but every NEW candidate is sampled only from the original
                            // direct scatter + its winning target-depth key.  This prevents R1
                            // colors from recursively crossing a layer boundary during R2.
                            _material.SetTexture("_DirectWarpInputV103", warpRT);
                            _material.SetTexture("_ScatterDepthKeyV103", _scatterDepthKeyRTV88);
                            _material.SetFloat("_V103FillDepthAbsToleranceMeters",
                                Mathf.Max(0.001f, AerV103FillDepthAbsToleranceMeters));
                            _material.SetFloat("_V103FillDepthRelativeTolerance",
                                Mathf.Max(0f, AerV103FillDepthRelativeTolerance));
                            _material.SetFloat("_V105FillDepthMaxToleranceMeters",
                                Mathf.Max(AerV103FillDepthAbsToleranceMeters, AerV105FillDepthMaxToleranceMeters));
                            // V141 rollback restores the exact original V103 guard thresholds.
                            _material.SetFloat("_V103FillMinSupportFraction",
                                Mathf.Clamp01(AerV103FillMinSupportFraction));
                            _material.SetFloat("_V103FillMinSupportCount",
                                Mathf.Max(1, AerV103FillMinSupportCount));
                        }
                        else
                        {
                            fillPassV103 = _material.FindPass("HoleFill");
                            if (fillPassV103 < 0)
                                throw new Exception("DepthStereoWarp HoleFill pass unavailable.");
                        }

                        _material.SetTexture("_WarpInput", warpRT);
                        _material.SetFloat("_FillRadiusPixels", 1.0f);

                        SetRenderTargetFullViewportV77(syntheticRT);
                        GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
                        if (!_material.SetPass(fillPassV103))
                            throw new Exception("DepthStereoWarp V103 radius-1 fill SetPass failed.");
                        DrawFullscreenTopLeftUvQuad();

                        _material.SetTexture("_WarpInput", syntheticRT);
                        _material.SetFloat("_FillRadiusPixels", 2.0f);

                        SetRenderTargetFullViewportV77(fillRT);
                        GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
                        if (!_material.SetPass(fillPassV103))
                            throw new Exception("DepthStereoWarp V103 radius-2 fill SetPass failed.");
                        DrawFullscreenTopLeftUvQuad();

                        preFallbackRTV103 = fillRT;
                    }

                    // 3) Keep V100's proven rotation-only/infinite-depth FINAL true-hole fallback
                    // ON permanently. After V104's guarded fill, only genuine/ambiguous holes reach it.
                    bool useRotationHoleFallbackV100 = true;

                    _material.SetTexture("_WarpInput", preFallbackRTV103);
                    _material.SetTexture("_FallbackTargetColor", historyColorRT);
                    _material.SetMatrix("_V100TargetInvGpuProjection", targetGpuProjection.inverse);
                    _material.SetMatrix("_V100TargetCameraToWorld", targetWorldToCamera.inverse);
                    _material.SetMatrix("_V100SourceWorldToCamera", sourceCameraToWorld.inverse);
                    _material.SetMatrix("_V100SourceGpuProjection", sourceInvGpuProjection.inverse);
                    _material.SetFloat("_RotationHoleFallbackEnabledV100", useRotationHoleFallbackV100 ? 1f : 0f);

                    SetRenderTargetFullViewportV77(syntheticRT);
                    GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));

                    int fallbackPass = _material.FindPass("TargetEyeHoleFallback");
                    if (fallbackPass < 0 || !_material.SetPass(fallbackPass))
                        throw new Exception("DepthStereoWarp TargetEyeHoleFallback pass unavailable.");

                    DrawFullscreenTopLeftUvQuad();
                    _material.SetFloat("_RotationHoleFallbackEnabledV100", 0f);

                    // 4) Preserve current HUD work: current HUD after temporal WORLD reprojection.
                    CompositeDenseHudV76(
                        self,
                        completedEye,
                        freshCam,
                        targetCam,
                        freshRT,
                        syntheticRT);

                    HudStereoReprojectV60.CompositeSameTimePair(
                        self,
                        completedEye,
                        freshRT,
                        syntheticRT,
                        freshCam.worldToCameraMatrix,
                        freshCam.projectionMatrix,
                        targetWorldToCamera,
                        targetProjection);

                    RestoreRenderTargetViewportV77(previous);

                    float historyAgeMs = historyLatch.CaptureUnscaledTime >= 0f
                        ? Mathf.Max(0f,
                            (Time.unscaledTime - historyLatch.CaptureUnscaledTime) * 1000f)
                        : -1f;

                    float poseDeltaMm =
                        (targetPositionForTelemetryV87 - historyLatch.SourcePosition).magnitude * 1000f;
                    float poseDeltaDeg =
                        Quaternion.Angle(historyLatch.SourceRotation, targetRotationForTelemetryV87);

                    _prepared = true;
                    _preparedFrame = Time.frameCount;
                    _preparedSourceEye = completedEye;
                    _preparedTargetEye = targetEye;
                    _preparedSourceRT = freshRT;
                    _preparedSyntheticRT = syntheticRT;
                    _preparedOldStaleRT = historyColorRT;
                    _preparedDepthTexture = historyDepthRT;
                    _preparedZParams = zParams;
                    _preparedSourceWidth = historyColorRT.width;
                    _preparedSourceHeight = historyColorRT.height;
                    _preparedDepthWidth = historyDepthRT.width;
                    _preparedDepthHeight = historyDepthRT.height;
                    _preparedHistoryFrameV83 = historyLatch.Frame;
                    _preparedHistoryAgeFramesV83 = historyAgeFrames;
                    _preparedHistoryAgeMsV83 = historyAgeMs;

                    _lastFreshLabelV83 = completedEye == 0 ? "L" : "R";

                    // Coverage telemetry source and target are deliberately the SAME eye now.
                    _lastSourceLabel = targetEye == 0 ? "L" : "R";
                    _lastTargetLabel = targetEye == 0 ? "L" : "R";

                    if (!_firstPreparedLogged)
                    {
                        _firstPreparedLogged = true;
                        MelonLogger.Warning(
                            $"[StereoRender][AER V91][Temporal] SAME-EYE TEMPORAL READY: " +
                            $"fresh REAL={_lastFreshLabelV83}(t); predict {_lastTargetLabel}(t) " +
                            $"from REAL {_lastTargetLabel}(historyFrame={historyLatch.Frame}) + " +
                            $"OWN cameraDepthBuffer depth from that same render. " +
                            $"historyAge={historyAgeFrames}f/{historyAgeMs:F1}ms " +
                            $"poseDelta={poseDeltaMm:F2}mm/{poseDeltaDeg:F3}deg; " +
                            $"projectiveSourceUV={(AerTemporalProjectiveSourceCoordinatesV86Enabled ? "ON" : "OLD-V85")} " +
                            $"reprojection={(usedPerPixelScatterV88 ? "PER-PIXEL-COMPUTE" : "GRID-768-FALLBACK")} " +
                            $"sourceMatrix={(useHdrpSourceMatricesV94 ? "HDCAMERA-EXACT" : "CAMERA-DERIVED")} " +
                            $"matrixPhase={hdrpMatrixPhaseV94} " +
                            $"opticalFlow={opticalFlowModeV90} ofScale={opticalFlowTimeScaleV90:F3} " +
                            $"ofExec={opticalFlowExecuteCountV90} prevRealFrame={historyLatch.PreviousRealFrameV89} " +
                            $"maxWarp={maxWarpMetersV87:F1}m " +
                            $"poseTiming={(useFrozenPoseV87 ? "BEGIN-FROZEN" : "EOF-LATE")} " +
                            $"NO cross-eye world synthesis; V36 HARD-OFF; " +
                            $"raw same-eye history is hole fallback only.");
                    }

                    if (Time.unscaledTime >= _nextTemporalTelemetryTimeV83)
                    {
                        _nextTemporalTelemetryTimeV83 = Time.unscaledTime + 2.0f;
                        Vector3 worldPoseDeltaV86 =
                            targetPositionForTelemetryV87 - historyLatch.SourcePosition;
                        Vector3 targetLocalDeltaMmV86 =
                            (Quaternion.Inverse(targetRotationForTelemetryV87) * worldPoseDeltaV86) * 1000f;

                        MelonLogger.Msg(
                            $"[StereoRender][AER V91][TemporalTelemetry] frame={Time.frameCount} " +
                            $"fresh={_lastFreshLabelV83} predicted={_lastTargetLabel} " +
                            $"historyFrame={historyLatch.Frame} age={historyAgeFrames}f/{historyAgeMs:F1}ms " +
                            $"poseDelta={poseDeltaMm:F2}mm/{poseDeltaDeg:F3}deg " +
                            $"targetLocalDeltaMm=({targetLocalDeltaMmV86.x:F2}," +
                            $"{targetLocalDeltaMmV86.y:F2},{targetLocalDeltaMmV86.z:F2}) " +
                            $"projectiveSourceUV={(AerTemporalProjectiveSourceCoordinatesV86Enabled ? "ON" : "OLD-V85")} " +
                            $"reprojection={(usedPerPixelScatterV88 ? "PER-PIXEL-COMPUTE" : "GRID-768-FALLBACK")} " +
                            $"sourceMatrix={(useHdrpSourceMatricesV94 ? "HDCAMERA-EXACT" : "CAMERA-DERIVED")} " +
                            $"matrixPhase={hdrpMatrixPhaseV94} " +
                            $"opticalFlow={opticalFlowModeV90} ofScale={opticalFlowTimeScaleV90:F3} " +
                            $"ofExec={opticalFlowExecuteCountV90} {NvOpticalFlowV90.GetCompactStatus(targetEye)} " +
                            $"prevRealFrame={historyLatch.PreviousRealFrameV89} " +
                            $"maxWarp={maxWarpMetersV87:F1}m " +
                            $"poseTiming={(useFrozenPoseV87 ? "BEGIN-FROZEN" : "EOF-LATE")} " +
                            $"depth=CUSTOMPASS-cameraDepthBuffer " +
                            $"historyRT={historyColorRT.width}x{historyColorRT.height}.");
                    }

                    return true;
                }
                catch (Exception e)
                {
                    RestoreRenderTargetViewportV77(previous);
                    _prepared = false;
                    LogFailure(
                        "V90 same-eye temporal synthesis failed: " +
                        e.Message +
                        " V36 is HARD-OFF; raw retained pair will be used.");
                    return false;
                }
            }

            internal static bool TryGetSubmissionTextures(
                StereoRender self, out RenderTexture leftSubmitRT, out RenderTexture rightSubmitRT)
            {
                leftSubmitRT = null;
                rightSubmitRT = null;

                if (!AerDepthStereoWarpV48Enabled || !AerDepthStereoWarpV48SubmitSyntheticEye ||
                    !_available || !_prepared || self == null || _preparedFrame != Time.frameCount)
                    return false;

                if (_preparedSourceEye == 0 && _preparedTargetEye == 1 &&
                    _preparedSyntheticRT != null && _preparedSyntheticRT.IsCreated())
                {
                    leftSubmitRT = self.LeftRT;
                    rightSubmitRT = _preparedSyntheticRT;
                }
                else if (_preparedSourceEye == 1 && _preparedTargetEye == 0 &&
                         _preparedSyntheticRT != null && _preparedSyntheticRT.IsCreated())
                {
                    leftSubmitRT = _preparedSyntheticRT;
                    rightSubmitRT = self.RightRT;
                }
                else
                {
                    return false;
                }

                if (!_firstSubmissionLogged)
                {
                    _firstSubmissionLogged = true;
                    MelonLogger.Warning(
                        $"[StereoRender][AER V91][Temporal] HEADSET PATH ACTIVE: " +
                        $"frame={Time.frameCount} fresh={_lastSourceLabel}(t) synthetic={_lastTargetLabel}(t). " +
                        "same-eye temporal DRS-aware depth scatter; DLSS Quality; half-res NVOFA optical-flow A/B; V89 motion pass OFF; " +
                        "HUD depth catchers OFF; diagnostics OFF; V36 OFF.");
                }
                return true;
            }

            private static RenderTexture EnsureDepthDiagTargetV80()
            {
                if (_depthDiagRTV80 != null &&
                    _depthDiagRTV80.IsCreated() &&
                    _depthDiagRTV80.width == DepthDiagSizeV80 &&
                    _depthDiagRTV80.height == DepthDiagSizeV80)
                    return _depthDiagRTV80;

                if (_depthDiagRTV80 != null)
                {
                    try { _depthDiagRTV80.Release(); } catch { }
                    UnityEngine.Object.Destroy(_depthDiagRTV80);
                }

                _depthDiagRTV80 = new RenderTexture(
                    DepthDiagSizeV80,
                    DepthDiagSizeV80,
                    0,
                    RenderTextureFormat.ARGB32)
                {
                    name = "SonsVR_V80_DepthInputDiag",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    autoGenerateMips = false,
                    useDynamicScale = false
                };
                _depthDiagRTV80.Create();
                return _depthDiagRTV80;
            }

            private static void RequestDepthInputForensicsV80(
                Texture globalDepth,
                RenderTexture sourceRT,
                Vector4 zParams,
                Matrix4x4 sourceProjection,
                Matrix4x4 sourceGpuProjection,
                Matrix4x4 targetProjection,
                Matrix4x4 targetGpuProjection,
                int eye)
            {
                if (_hudDepthProxyMaterialV72 == null ||
                    globalDepth == null ||
                    sourceRT == null ||
                    _depthDiagReadbackPendingV80 ||
                    Time.unscaledTime < _nextDepthDiagRequestTimeV80)
                    return;

                _nextDepthDiagRequestTimeV80 =
                    Time.unscaledTime + DepthDiagIntervalV80;

                RenderTexture diag = EnsureDepthDiagTargetV80();
                if (diag == null || !diag.IsCreated())
                    return;

                RenderTexture previous = RenderTexture.active;
                try
                {
                    _hudDepthProxyMaterialV72.SetTexture(
                        "_DepthDiagGlobalDepth", globalDepth);
                    _hudDepthProxyMaterialV72.SetVector(
                        "_DepthDiagSourceSize",
                        new Vector4(
                            sourceRT.width, sourceRT.height,
                            1.0f / Mathf.Max(1, sourceRT.width),
                            1.0f / Mathf.Max(1, sourceRT.height)));
                    _hudDepthProxyMaterialV72.SetVector(
                        "_DepthDiagZParams", zParams);
                    _hudDepthProxyMaterialV72.SetFloat(
                        "_DepthDiagMaxMeters",
                        Mathf.Max(1.0f, AerDepthStereoWarpV48MaxMeters));
                    _hudDepthProxyMaterialV72.SetFloat(
                        "_DepthDiagDiscontinuityMeters",
                        Mathf.Max(0.001f, AerDepthStereoWarpV48DiscontinuityMeters));
                    _hudDepthProxyMaterialV72.SetFloat(
                        "_DepthDiagDiscontinuityRelative",
                        Mathf.Max(0f, AerDepthStereoWarpV48DiscontinuityRelative));
                    _hudDepthProxyMaterialV72.SetFloat("_DepthDiagArraySlice", 0f);

                    SetRenderTargetFullViewportV77(diag);
                    GL.Clear(false, true, Color.clear);

                    int depthDiagPassV80 =
                        _hudDepthProxyMaterialV72.FindPass("DepthInputForensicsV80");
                    if (depthDiagPassV80 < 0 ||
                        !_hudDepthProxyMaterialV72.SetPass(depthDiagPassV80))
                    {
                        MelonLogger.Warning(
                            "[StereoRender][AER V80][DepthInput] diagnostic pass unavailable: " +
                            $"shader='{_hudDepthProxyMaterialV72.shader?.name}' " +
                            $"passCount={_hudDepthProxyMaterialV72.shader?.passCount} " +
                            $"FindPass={depthDiagPassV80}.");
                        return;
                    }

                    DrawFullscreenTopLeftUvQuad();

                    _depthDiagReadbackV80 = AsyncGPUReadback.Request(
                        diag, 0,
                        0, diag.width,
                        0, diag.height,
                        0, 1,
                        TextureFormat.RGBA32,
                        null);

                    _depthDiagReadbackPendingV80 = true;
                    _depthDiagEyeV80 = eye == 0 ? "L" : "R";
                    _depthDiagFrameV80 = Time.frameCount;
                    _depthDiagSourceWidthV80 = sourceRT.width;
                    _depthDiagSourceHeightV80 = sourceRT.height;
                    _depthDiagAtlasWidthV80 = globalDepth.width;
                    _depthDiagAtlasHeightV80 = globalDepth.height;
                    _depthDiagGlobalInstanceIdV80 = globalDepth.GetInstanceID();
                    _depthDiagGlobalNameV80 = globalDepth.name ?? "";
                    _depthDiagMaxMetersV80 =
                        Mathf.Max(1.0f, AerDepthStereoWarpV48MaxMeters);

                    HDRPDlssRuntimeTraceV78.RecordProjectionV80(
                        _depthDiagEyeV80,
                        sourceProjection,
                        sourceGpuProjection,
                        targetProjection,
                        targetGpuProjection,
                        zParams);
                }
                catch (Exception e)
                {
                    _depthDiagReadbackPendingV80 = false;
                    MelonLogger.Warning(
                        "[StereoRender][AER V80][DepthInput] request failed: " +
                        e.GetType().Name + ": " + e.Message);
                }
                finally
                {
                    if (previous != null)
                        SetRenderTargetFullViewportV77(previous);
                    else
                        RestoreRenderTargetViewportV77(null);
                }
            }

            private static float RegionValidFractionV80(
                byte[] bytes, int width, int height,
                int x0, int x1, int y0, int y1)
            {
                long valid = 0;
                long total = 0;
                for (int y = y0; y < y1; ++y)
                {
                    for (int x = x0; x < x1; ++x)
                    {
                        int bi = (y * width + x) * 4;
                        if (bi + 3 >= bytes.Length) continue;
                        if (bytes[bi] >= 128) valid++;
                        total++;
                    }
                }
                return total > 0 ? (float)(valid / (double)total) : 0f;
            }

            private static void PollDepthInputReadbackV80()
            {
                if (!_depthDiagReadbackPendingV80 ||
                    !_depthDiagReadbackV80.done)
                    return;

                _depthDiagReadbackPendingV80 = false;

                if (_depthDiagReadbackV80.hasError)
                {
                    MelonLogger.Warning(
                        "[StereoRender][AER V80][DepthInput] AsyncGPUReadback ERROR.");
                    return;
                }

                try
                {
                    var native = _depthDiagReadbackV80.GetData<byte>();
                    byte[] bytes = new byte[native.Length];
                    for (int i = 0; i < native.Length; ++i)
                        bytes[i] = native[i];

                    int width = DepthDiagSizeV80;
                    int height = DepthDiagSizeV80;
                    int pixels = Mathf.Max(1, width * height);

                    int validCount = 0;
                    int nonzeroCount = 0;
                    int discCount = 0;
                    List<float> depthMeters = new List<float>(pixels);

                    bool[] invalid = new bool[pixels];

                    float logDenom = Mathf.Log(
                        1.0f + Mathf.Max(1.0f, _depthDiagMaxMetersV80),
                        2.0f);

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
                        medianDepth = depthMeters[depthMeters.Count / 2];

                    // Connected components of invalid source depth.
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
                                { seen[n] = true; queue[tail++] = n; }
                            }
                            if (x + 1 < width)
                            {
                                int n = idx + 1;
                                if (invalid[n] && !seen[n])
                                { seen[n] = true; queue[tail++] = n; }
                            }
                            if (y > 0)
                            {
                                int n = idx - width;
                                if (invalid[n] && !seen[n])
                                { seen[n] = true; queue[tail++] = n; }
                            }
                            if (y + 1 < height)
                            {
                                int n = idx + width;
                                if (invalid[n] && !seen[n])
                                { seen[n] = true; queue[tail++] = n; }
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

                    Vector4 invalidBBox = Vector4.zero;
                    Vector2 invalidCentroid = Vector2.zero;
                    if (largest > 0)
                    {
                        invalidBBox = new Vector4(
                            largestMinX / (float)width,
                            largestMinY / (float)height,
                            (largestMaxX + 1) / (float)width,
                            (largestMaxY + 1) / (float)height);
                        invalidCentroid = new Vector2(
                            (float)(largestSumX / (double)largest + 0.5) / width,
                            (float)(largestSumY / (double)largest + 0.5) / height);
                    }

                    int xA = width / 3;
                    int xB = (2 * width) / 3;
                    int yA = height / 3;
                    int yB = (2 * height) / 3;

                    float validBottom = RegionValidFractionV80(
                        bytes, width, height, 0, width, 0, yA);
                    float validMiddle = RegionValidFractionV80(
                        bytes, width, height, 0, width, yA, yB);
                    float validTop = RegionValidFractionV80(
                        bytes, width, height, 0, width, yB, height);

                    float validLeft = RegionValidFractionV80(
                        bytes, width, height, 0, xA, 0, height);
                    float validCenter = RegionValidFractionV80(
                        bytes, width, height, xA, xB, 0, height);
                    float validRight = RegionValidFractionV80(
                        bytes, width, height, xB, width, 0, height);

                    float denom = pixels;
                    float validFrac = validCount / denom;
                    float nonzeroFrac = nonzeroCount / denom;
                    float zeroFrac = 1f - nonzeroFrac;
                    float tooFarFrac =
                        Mathf.Max(0f, nonzeroFrac - validFrac);
                    float discFrac = discCount / denom;
                    float largestInvalidFrac = largest / denom;

                    HDRPDlssRuntimeTraceV78.RecordDepthInputV80(
                        _depthDiagEyeV80,
                        _depthDiagFrameV80,
                        _depthDiagSourceWidthV80,
                        _depthDiagSourceHeightV80,
                        _depthDiagAtlasWidthV80,
                        _depthDiagAtlasHeightV80,
                        _depthDiagGlobalInstanceIdV80,
                        _depthDiagGlobalNameV80,
                        zeroFrac,
                        nonzeroFrac,
                        validFrac,
                        tooFarFrac,
                        discFrac,
                        medianDepth,
                        validTop,
                        validMiddle,
                        validBottom,
                        validLeft,
                        validCenter,
                        validRight,
                        largestInvalidFrac,
                        invalidBBox,
                        invalidCentroid);
                }
                catch (Exception e)
                {
                    MelonLogger.Warning(
                        "[StereoRender][AER V80][DepthInput] processing failed: " +
                        e.GetType().Name + ": " + e.Message);
                }
            }

            private static void PollCoverageReadbackV79()
            {
                if (!_coverageReadbackPendingV79 || !_coverageReadbackV79.done)
                    return;

                _coverageReadbackPendingV79 = false;

                if (_coverageReadbackV79.hasError)
                {
                    MelonLogger.Warning(
                        "[StereoRender][AER V79][Coverage] AsyncGPUReadback ERROR; " +
                        "passive measurement skipped.");
                    return;
                }

                try
                {
                    var data = _coverageReadbackV79.GetData<byte>();
                    int width = _coverageWidthV79;
                    int height = _coverageHeightV79;
                    int pixels = Mathf.Max(1, width * height);

                    long confSum = 0;
                    int holeCount = 0;
                    int coveredCount = 0;
                    int highCount = 0;

                    // Coarse connected-components grid. 4x4 debug pixels per cell keeps this
                    // extremely cheap while preserving the giant failure-region geometry.
                    const int stride = 4;
                    int gw = Mathf.Max(1, (width + stride - 1) / stride);
                    int gh = Mathf.Max(1, (height + stride - 1) / stride);
                    bool[] holeGrid = new bool[gw * gh];

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int pixelIndex = y * width + x;
                            int byteIndex = pixelIndex * 4;
                            if (byteIndex + 3 >= data.Length)
                                continue;

                            byte c = data[byteIndex]; // grayscale confidence, RGBA32
                            confSum += c;

                            if (c <= 2)
                                holeCount++;
                            else
                                coveredCount++;

                            if (c >= 220)
                                highCount++;
                        }
                    }

                    // Mark a coarse cell as a hole only if the center sample is black.
                    for (int gy = 0; gy < gh; gy++)
                    {
                        int y = Mathf.Min(height - 1, gy * stride + stride / 2);
                        for (int gx = 0; gx < gw; gx++)
                        {
                            int x = Mathf.Min(width - 1, gx * stride + stride / 2);
                            int bi = (y * width + x) * 4;
                            holeGrid[gy * gw + gx] =
                                bi + 3 < data.Length && data[bi] <= 2;
                        }
                    }

                    bool[] seen = new bool[holeGrid.Length];
                    int[] queue = new int[holeGrid.Length];

                    int largest = 0;
                    int largestMinX = 0, largestMaxX = -1;
                    int largestMinY = 0, largestMaxY = -1;
                    long largestSumX = 0, largestSumY = 0;

                    for (int start = 0; start < holeGrid.Length; start++)
                    {
                        if (!holeGrid[start] || seen[start])
                            continue;

                        int head = 0, tail = 0;
                        queue[tail++] = start;
                        seen[start] = true;

                        int count = 0;
                        int minX = gw, maxX = -1, minY = gh, maxY = -1;
                        long sumX = 0, sumY = 0;

                        while (head < tail)
                        {
                            int idx = queue[head++];
                            int x = idx % gw;
                            int y = idx / gw;
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
                                if (holeGrid[n] && !seen[n]) { seen[n] = true; queue[tail++] = n; }
                            }
                            if (x + 1 < gw)
                            {
                                int n = idx + 1;
                                if (holeGrid[n] && !seen[n]) { seen[n] = true; queue[tail++] = n; }
                            }
                            if (y > 0)
                            {
                                int n = idx - gw;
                                if (holeGrid[n] && !seen[n]) { seen[n] = true; queue[tail++] = n; }
                            }
                            if (y + 1 < gh)
                            {
                                int n = idx + gw;
                                if (holeGrid[n] && !seen[n]) { seen[n] = true; queue[tail++] = n; }
                            }
                        }

                        if (count > largest)
                        {
                            largest = count;
                            largestMinX = minX; largestMaxX = maxX;
                            largestMinY = minY; largestMaxY = maxY;
                            largestSumX = sumX; largestSumY = sumY;
                        }
                    }

                    float denom = Mathf.Max(1f, pixels);
                    float holeFrac = holeCount / denom;
                    float coveredFrac = coveredCount / denom;
                    float highFrac = highCount / denom;
                    float meanConf = (float)(confSum / (255.0 * denom));

                    float gridDenom = Mathf.Max(1f, gw * gh);
                    float largestHoleFrac = largest / gridDenom;

                    Vector4 bbox = Vector4.zero;
                    Vector2 centroid = Vector2.zero;
                    if (largest > 0 && largestMaxX >= largestMinX && largestMaxY >= largestMinY)
                    {
                        bbox = new Vector4(
                            largestMinX / (float)gw,
                            largestMinY / (float)gh,
                            (largestMaxX + 1) / (float)gw,
                            (largestMaxY + 1) / (float)gh);

                        centroid = new Vector2(
                            (float)(largestSumX / (double)largest + 0.5) / gw,
                            (float)(largestSumY / (double)largest + 0.5) / gh);
                    }

                    HDRPDlssRuntimeTraceV78.RecordCoverageV79(
                        _coverageSourceEyeV79,
                        _coverageTargetEyeV79,
                        _coverageSourceFrameV79,
                        width,
                        height,
                        holeFrac,
                        coveredFrac,
                        highFrac,
                        meanConf,
                        largestHoleFrac,
                        bbox,
                        centroid);
                }
                catch (Exception e)
                {
                    MelonLogger.Warning(
                        "[StereoRender][AER V79][Coverage] processing failed: " +
                        e.GetType().Name + ": " + e.Message);
                }
            }

            private static void RequestCoverageReadbackV79(RenderTexture confidenceDebugRT)
            {
                if (confidenceDebugRT == null || !confidenceDebugRT.IsCreated() ||
                    _coverageReadbackPendingV79 ||
                    Time.unscaledTime < _nextCoverageRequestTimeV79)
                    return;

                _nextCoverageRequestTimeV79 =
                    Time.unscaledTime + CoverageReadbackIntervalV79;

                try
                {
                    _coverageReadbackV79 = AsyncGPUReadback.Request(
                        confidenceDebugRT, 0,
                        0, confidenceDebugRT.width,
                        0, confidenceDebugRT.height,
                        0, 1,
                        TextureFormat.RGBA32,
                        null);

                    _coverageReadbackPendingV79 = true;
                    _coverageSourceEyeV79 = _lastSourceLabel;
                    _coverageTargetEyeV79 = _lastTargetLabel;
                    _coverageSourceFrameV79 = _preparedHistoryFrameV83 >= 0
                        ? _preparedHistoryFrameV83
                        : (_lastDebugFrame >= 0 ? _lastDebugFrame : Time.frameCount);
                    _coverageWidthV79 = confidenceDebugRT.width;
                    _coverageHeightV79 = confidenceDebugRT.height;
                }
                catch (Exception e)
                {
                    _coverageReadbackPendingV79 = false;
                    MelonLogger.Warning(
                        "[StereoRender][AER V79][Coverage] request failed: " +
                        e.GetType().Name + ": " + e.Message);
                }
            }

            private static void CaptureDepthTruthPanelsV83(
                RenderTexture cameraLocalRawDepth,
                Texture globalPackedDepth,
                RenderTexture sourceRT,
                Vector4 zParams,
                int eye)
            {
                if (_material == null ||
                    cameraLocalRawDepth == null ||
                    !cameraLocalRawDepth.IsCreated() ||
                    globalPackedDepth == null ||
                    sourceRT == null ||
                    !sourceRT.IsCreated() ||
                    Time.unscaledTime < _nextDepthTruthCaptureTimeV83)
                    return;

                _nextDepthTruthCaptureTimeV83 =
                    Time.unscaledTime +
                    Mathf.Max(0.05f, AerDepthStereoWarpV48DesktopUpdateIntervalSeconds);

                RenderTexture previous = RenderTexture.active;

                try
                {
                    EnsureDebugTargets(sourceRT);

                    _material.SetVector("_ZBufferParams", zParams);
                    _material.SetVector("_SourceSize", new Vector4(
                        sourceRT.width,
                        sourceRT.height,
                        1.0f / Mathf.Max(1, sourceRT.width),
                        1.0f / Mathf.Max(1, sourceRT.height)));
                    _material.SetFloat("_MaxWarpMeters",
                        Mathf.Max(1.0f, AerDepthStereoWarpV48MaxMeters));
                    _material.SetFloat("_DepthArraySlice", 0f);

                    // LOCAL: exact raw RFloat copy from CustomPassContext.cameraDepthBuffer.
                    _material.SetTexture("_LatchedDepthTexture", cameraLocalRawDepth);
                    int localPass = _material.FindPass("DebugDepthLocal2D");
                    if (localPass < 0)
                        throw new Exception("DebugDepthLocal2D pass missing.");

                    SetRenderTargetFullViewportV77(_debugTruthLocalRTV83);
                    GL.Clear(false, true, Color.black);
                    if (!_material.SetPass(localPass))
                        throw new Exception("DebugDepthLocal2D SetPass failed.");
                    DrawFullscreenTopLeftUvQuad();

                    // GLOBAL: old V47 candidate A exactly — mip0 assumed at pixel offset (0,0).
                    _material.SetTexture("_CameraDepthTexture", globalPackedDepth);
                    int globalPass = _material.FindPass("DebugDepthGlobalMip0");
                    if (globalPass < 0)
                        throw new Exception("DebugDepthGlobalMip0 pass missing.");

                    SetRenderTargetFullViewportV77(_debugTruthGlobalMip0RTV83);
                    GL.Clear(false, true, Color.black);
                    if (!_material.SetPass(globalPass))
                        throw new Exception("DebugDepthGlobalMip0 SetPass failed.");
                    DrawFullscreenTopLeftUvQuad();

                    _depthTruthFrameV83 = Time.frameCount;
                    _depthTruthEyeV83 = eye == 0 ? "L" : "R";
                }
                catch (Exception e)
                {
                    LogFailure("V83 depth-truth panel capture failed: " + e.Message);
                }
                finally
                {
                    RestoreRenderTargetViewportV77(previous);
                }
            }

            internal static void CaptureDebugAfterSubmit(StereoRender self)
            {
                if (!AerV84DesktopDebugEnabled)
                    return;

                PollDepthInputReadbackV80();
                PollCoverageReadbackV79();

                if (!_prepared || _preparedFrame != Time.frameCount || !_available ||
                    _preparedSourceRT == null || _preparedSyntheticRT == null ||
                    _preparedOldStaleRT == null)
                    return;

                if (Time.unscaledTime < _nextDebugCaptureTime)
                    return;
                _nextDebugCaptureTime = Time.unscaledTime + Mathf.Max(0.05f, AerDepthStereoWarpV48DesktopUpdateIntervalSeconds);

                RenderTexture previous = RenderTexture.active;
                try
                {
                    EnsureDebugTargets(_preparedSourceRT);

                    // Safe now: StereoRenderPass.Execute has already submitted both eyes to OpenVR.
                    Graphics.Blit(_preparedSourceRT, _debugSourceRT);
                    Graphics.Blit(_preparedSyntheticRT, _debugSyntheticRT);
                    Graphics.Blit(_preparedOldStaleRT, _debugOldStaleRT);

                    // V49's bottom-left panel is no longer raw depth. The synthetic RT keeps its
                    // pre-composite confidence in alpha: white=direct/high-confidence synthetic,
                    // gray=filled/conservative blend, black=same-time monoscopic fallback.
                    _material.SetTexture("_WarpInput", _preparedSyntheticRT);
                    _material.SetVector("_TargetSize", new Vector4(
                        _preparedSourceWidth, _preparedSourceHeight,
                        1.0f / Mathf.Max(1, _preparedSourceWidth),
                        1.0f / Mathf.Max(1, _preparedSourceHeight)));

                    Graphics.SetRenderTarget(_debugDepthRT);
                    GL.Clear(false, true, Color.black);
                    if (!_material.SetPass(3))
                        throw new Exception("DepthStereoWarp Material.SetPass(3) confidence debug returned false.");
                    DrawFullscreenTopLeftUvQuad();
                    RenderTexture.active = previous;

                    _lastDebugFrame = Time.frameCount;
                    RequestCoverageReadbackV79(_debugDepthRT);
                }
                catch (Exception e)
                {
                    RenderTexture.active = previous;
                    LogFailure("desktop post-submit capture failed: " + e.Message);
                }
            }

            internal static bool TryDrawDesktopDebug()
            {
                if (!_available ||
                    _lastDebugFrame < 0 ||
                    _debugSourceRT == null ||
                    _debugSyntheticRT == null ||
                    _debugDepthRT == null ||
                    _debugOldStaleRT == null ||
                    _debugTruthLocalRTV83 == null ||
                    _debugTruthGlobalMip0RTV83 == null)
                    return false;

                float sw = Mathf.Max(1f, Screen.width);
                float sh = Mathf.Max(1f, Screen.height);
                float cellW = sw / 3.0f;
                float cellH = sh * 0.5f;
                float labelH = Mathf.Clamp(sh * 0.032f, 18f, 34f);

                DrawCell(
                    new Rect(0f, 0f, cellW, cellH),
                    _debugSourceRT,
                    $"V83 FRESH REAL {_lastFreshLabelV83}(t)  frame={_lastDebugFrame}",
                    labelH);

                DrawCell(
                    new Rect(cellW, 0f, cellW, cellH),
                    _debugSyntheticRT,
                    $"V83 PREDICTED {_lastTargetLabel}(t) <- SAME-EYE HISTORY",
                    labelH);

                DrawCell(
                    new Rect(cellW * 2f, 0f, cellW, cellH),
                    _debugOldStaleRT,
                    $"REAL {_lastTargetLabel} HISTORY frame={_preparedHistoryFrameV83} " +
                    $"age={_preparedHistoryAgeFramesV83}f/{_preparedHistoryAgeMsV83:F1}ms",
                    labelH);

                DrawCell(
                    new Rect(0f, cellH, cellW, cellH),
                    _debugDepthRT,
                    "TEMPORAL COVERAGE: white=direct gray=fill black=raw SAME-EYE history fallback",
                    labelH);

                DrawCell(
                    new Rect(cellW, cellH, cellW, cellH),
                    _debugTruthLocalRTV83,
                    $"DEPTH LOCAL ctx.cameraDepthBuffer eye={_depthTruthEyeV83} frame={_depthTruthFrameV83}",
                    labelH);

                DrawCell(
                    new Rect(cellW * 2f, cellH, cellW, cellH),
                    _debugTruthGlobalMip0RTV83,
                    $"DEPTH GLOBAL old-V47 lower-left mip0 eye={_depthTruthEyeV83} frame={_depthTruthFrameV83}",
                    labelH);

                return true;
            }

            private static void DrawCell(Rect cell, RenderTexture texture, string label, float labelH)
            {
                GUI.Box(new Rect(cell.x, cell.y, cell.width, labelH), label);
                Rect imageRect = new Rect(cell.x, cell.y + labelH, cell.width, Mathf.Max(1f, cell.height - labelH));
                GUI.DrawTexture(imageRect, texture, ScaleMode.ScaleToFit, false);
            }

            internal static bool TryGetSecondaryDiagnosticShaderV81(out Shader shader)
            {
                shader = null;
                if (!EnsureLoaded())
                    return false;

                shader = _hudDepthProxyShaderV72;
                return shader != null;
            }

            private static bool EnsureLoaded()
            {
                if (_available && _material != null && _warpMesh != null)
                    return true;
                if (_initAttempted)
                    return false;
                _initAttempted = true;

                try
                {
                    Assembly asm = Assembly.GetExecutingAssembly();
                    using Stream stream = asm.GetManifestResourceStream(BundleResourceName);
                    if (stream == null)
                    {
                        string names = string.Join(", ", asm.GetManifestResourceNames());
                        throw new Exception($"embedded resource '{BundleResourceName}' not found. Present resources: {names}");
                    }

                    if (stream.Length <= 0 || stream.Length > int.MaxValue)
                        throw new Exception($"embedded V50 THINGY has invalid length {stream.Length} bytes.");

                    int bundleBytes = (int)stream.Length;
                    byte[] bundleData = new byte[bundleBytes];
                    int totalRead = 0;
                    while (totalRead < bundleBytes)
                    {
                        int n = stream.Read(bundleData, totalRead, bundleBytes - totalRead);
                        if (n <= 0) break;
                        totalRead += n;
                    }
                    if (totalRead != bundleBytes)
                        throw new Exception($"embedded V50 THINGY short read: expected {bundleBytes}, got {totalRead}.");

                    string magic = bundleBytes >= 7 ? System.Text.Encoding.ASCII.GetString(bundleData, 0, 7) : "<short>";
                    Il2CppStructArray<byte> il2cppBundleData = bundleData;
                    if (il2cppBundleData == null)
                        throw new Exception("managed byte[] -> Il2CppStructArray<byte> conversion returned null.");

                    _bundle = Il2CppAssetBundleManager.LoadFromMemory(il2cppBundleData);
                    if (_bundle == null)
                        throw new Exception($"Il2CppAssetBundleManager.LoadFromMemory returned null (bytes={bundleBytes}, magic='{magic}').");

                    _shader = _bundle.LoadAsset<Shader>(ShaderAssetPath);
                    if (_shader == null)
                        _shader = _bundle.LoadAsset<Shader>(ShaderAssetPath.ToLowerInvariant());
                    if (_shader == null)
                        throw new Exception($"shader asset '{ShaderAssetPath}' was not found in the loaded bundle.");

                    _material = new Material(_shader) { name = "SonsVR_V50_DepthStereoWarp_Material" };

                    // V90 NVOFA input conversion. Keep this explicit R8 luminance pass even
                    // though the eye target is ARGB32: NVOFA's D3D11 input formats are narrow,
                    // and relying on Unity's native channel-order mapping would make this build
                    // backend-dependent for no scientific benefit.
                    _opticalFlowLumaShaderV90 =
                        _bundle.LoadAsset<Shader>(OpticalFlowLumaShaderAssetPathV90);
                    if (_opticalFlowLumaShaderV90 == null)
                        _opticalFlowLumaShaderV90 =
                            _bundle.LoadAsset<Shader>(OpticalFlowLumaShaderAssetPathV90.ToLowerInvariant());
                    if (_opticalFlowLumaShaderV90 == null)
                        MelonLogger.Warning(
                            "[StereoRender][AER V91][NVOFA] luminance shader asset missing; " +
                            "optical flow will stay OFF fail-soft while depth-only AER remains active.");
                    else
                        MelonLogger.Msg(
                            $"[StereoRender][AER V91][NVOFA] luminance shader loaded: " +
                            $"'{_opticalFlowLumaShaderV90.name}'.");

                    // V88 optional full-resolution compute scatter. The 768 mesh remains
                    // a complete fail-soft fallback if loading or kernel lookup fails.
                    _temporalScatterComputeV88 =
                        _bundle.LoadAsset<ComputeShader>(TemporalScatterComputeAssetPathV88);
                    if (_temporalScatterComputeV88 == null)
                        _temporalScatterComputeV88 =
                            _bundle.LoadAsset<ComputeShader>(
                                TemporalScatterComputeAssetPathV88.ToLowerInvariant());

                    if (_temporalScatterComputeV88 != null)
                    {
                        try
                        {
                            _scatterKernelClearV88 =
                                _temporalScatterComputeV88.FindKernel("ClearScatter");
                            _scatterKernelDepthV88 =
                                _temporalScatterComputeV88.FindKernel("SplatDepth");
                            _scatterKernelResolveV88 =
                                _temporalScatterComputeV88.FindKernel("ResolveSource");

                            MelonLogger.Msg(
                                $"[StereoRender][AER V91][Scatter] compute loaded: " +
                                $"clear={_scatterKernelClearV88} depth={_scatterKernelDepthV88} " +
                                $"resolve={_scatterKernelResolveV88}.");
                        }
                        catch (Exception e)
                        {
                            _temporalScatterComputeV88 = null;
                            _scatterKernelClearV88 = -1;
                            _scatterKernelDepthV88 = -1;
                            _scatterKernelResolveV88 = -1;
                            MelonLogger.Warning(
                                "[StereoRender][AER V91][Scatter] compute kernel lookup failed; " +
                                "768 mesh will remain active. " +
                                e.GetType().Name + ": " + e.Message);
                        }
                    }
                    else
                    {
                        MelonLogger.Warning(
                            "[StereoRender][AER V91][Scatter] compute asset not found; " +
                            "768 mesh will remain active.");
                    }

                    // V99 one-launch forensic compute. Entirely diagnostic: failure disables only
                    // truth metrics and never the displayed AER path.
                    _forensicsComputeV99 = _bundle.LoadAsset<ComputeShader>(AerForensicsComputeAssetPathV99);
                    if (_forensicsComputeV99 == null)
                        _forensicsComputeV99 = _bundle.LoadAsset<ComputeShader>(AerForensicsComputeAssetPathV99.ToLowerInvariant());
                    if (_forensicsComputeV99 != null)
                    {
                        try
                        {
                            _forensicsBlockMatchKernelV99 = _forensicsComputeV99.FindKernel("BlockMatch");
                            _forensicsTileStatsKernelV99 = _forensicsComputeV99.FindKernel("TileStats");
                            _forensicsWarpFieldKernelV99 = _forensicsComputeV99.FindKernel("WarpField");
                            _forensicsMotionFieldKernelV99 = _forensicsComputeV99.FindKernel("MotionField");
                            _forensicsOpticalFlowFieldKernelV99 = _forensicsComputeV99.FindKernel("OpticalFlowField");
                            _forensicsDepthFieldKernelV99 = _forensicsComputeV99.FindKernel("DepthField");
                            _forensicsCoverageFieldKernelV99 = _forensicsComputeV99.FindKernel("CoverageField");
                            _forensicsSceneStructureKernelV99 = _forensicsComputeV99.FindKernel("SceneStructureField");
                            _forensicsDepthClassStatsKernelV101 = _forensicsComputeV99.FindKernel("DepthClassStatsV101");
                            _forensicsSkyRayClassKernelV101 = _forensicsComputeV99.FindKernel("SkyRayClassFieldV101");
                            _forensicsOcclusionClassStatsKernelV102 = _forensicsComputeV99.FindKernel("OcclusionClassStatsV102");
                            _forensicsOcclusionComplexityKernelV102 = _forensicsComputeV99.FindKernel("OcclusionComplexityFieldV102");
                            _forensicsDepthColorRiskKernelV102 = _forensicsComputeV99.FindKernel("DepthColorRiskFieldV102");
                            MelonLogger.Warning(
                                $"[StereoRender][AER V102][Forensics] compute loaded: block={_forensicsBlockMatchKernelV99} " +
                                $"stats={_forensicsTileStatsKernelV99} warp={_forensicsWarpFieldKernelV99} " +
                                $"motion={_forensicsMotionFieldKernelV99} of={_forensicsOpticalFlowFieldKernelV99} " +
                                $"depth={_forensicsDepthFieldKernelV99} coverage={_forensicsCoverageFieldKernelV99} " +
                                $"scene={_forensicsSceneStructureKernelV99} depthClass={_forensicsDepthClassStatsKernelV101} " +
                                $"skyRays={_forensicsSkyRayClassKernelV101} occClass={_forensicsOcclusionClassStatsKernelV102} " +
                                $"occComplex={_forensicsOcclusionComplexityKernelV102} depthColorRisk={_forensicsDepthColorRiskKernelV102}.");
                        }
                        catch (Exception e)
                        {
                            _forensicsComputeV99 = null;
                            MelonLogger.Warning("[StereoRender][AER V99][Forensics] kernel lookup failed; diagnostic truth campaign disabled: " + e.Message);
                        }
                    }
                    else
                    {
                        MelonLogger.Warning("[StereoRender][AER V99][Forensics] compute asset missing; diagnostic truth campaign disabled.");
                    }

                    // V72 secondary shader: optional and isolated. If it fails to load, the known-good
                    // V51/V50 world path continues untouched; only the HUD depth-proxy experiment is off.
                    _hudDepthProxyShaderV72 = _bundle.LoadAsset<Shader>(HudDepthProxyShaderAssetPathV72);
                    if (_hudDepthProxyShaderV72 == null)
                        _hudDepthProxyShaderV72 = _bundle.LoadAsset<Shader>(HudDepthProxyShaderAssetPathV72.ToLowerInvariant());
                    if (_hudDepthProxyShaderV72 != null)
                    {
                        _hudDepthProxyMaterialV72 = new Material(_hudDepthProxyShaderV72)
                        {
                            name = "SonsVR_V72_HudDepthProxy_Material"
                        };
                        int p0 = _hudDepthProxyMaterialV72.FindPass("HudDepthProxy");
                        int p1 = _hudDepthProxyMaterialV72.FindPass("HudDepthProxyExactMeshV73");
                        int p2 = _hudDepthProxyMaterialV72.FindPass("HudDensePlanarRemapV76");
                        int p3 = _hudDepthProxyMaterialV72.FindPass("DepthInputForensicsV80");
                        int p4 = _hudDepthProxyMaterialV72.FindPass("V81CameraLocalDepthCaptureArray");
                        int p5 = _hudDepthProxyMaterialV72.FindPass("V81CameraLocalDepthDiagArray");
                        int p6 = _hudDepthProxyMaterialV72.FindPass("V81CameraLocalDepthCapture2D");
                        int p7 = _hudDepthProxyMaterialV72.FindPass("V81CameraLocalDepthDiag2D");

                        MelonLogger.Warning(
                            $"[StereoRender][AER V81c][ShaderAudit] secondary='{_hudDepthProxyShaderV72.name}' " +
                            $"passCount={_hudDepthProxyShaderV72.passCount} " +
                            $"passes=[hud:{p0},mesh:{p1},dense:{p2},v80:{p3}," +
                            $"v81arrCapture:{p4},v81arrDiag:{p5},v81_2dCapture:{p6},v81_2dDiag:{p7}].");
                    }
                    else
                    {
                        _hudDepthProxyMaterialV72 = null;
                        MelonLogger.Warning(
                            "[StereoRender][AER V72][HudDepthProxy] optional secondary shader not found; " +
                            "world V51 remains enabled, proxies disabled.");
                    }

                    EnsureWarpMesh();
                    _available = true;
                    MelonLogger.Msg($"[StereoRender][AER V50][DepthWarp] GPU warp THINGY loaded from embedded DLL memory: bytes={bundleBytes} magic='{magic}' shader='{_shader.name}' grid={Mathf.Clamp(AerDepthStereoWarpV48GridSize, 64, 1024)}. No temp file.");
                    return true;
                }
                catch (Exception e)
                {
                    _available = false;
                    LogFailure("initialization failed: " + e.Message);
                    return false;
                }
            }

            private static void EnsureWarpMesh()
            {
                int grid = Mathf.Clamp(AerDepthStereoWarpV48GridSize, 64, 1024);
                int wantedVertices = grid * grid;
                if (_warpMesh != null && _warpMesh.vertexCount == wantedVertices)
                    return;

                if (_warpMesh != null)
                    UnityEngine.Object.Destroy(_warpMesh);

                var vertices = new Vector3[wantedVertices];
                var uvs = new Vector2[wantedVertices];
                int triCount = (grid - 1) * (grid - 1) * 2;
                var indices = new int[triCount * 3];

                int v = 0;
                for (int y = 0; y < grid; y++)
                {
                    float fy = y / (float)(grid - 1); // 0 = TOP source row by convention in shader
                    for (int x = 0; x < grid; x++, v++)
                    {
                        float fx = x / (float)(grid - 1);
                        vertices[v] = new Vector3(fx * 2f - 1f, 1f - fy * 2f, 0f);
                        uvs[v] = new Vector2(fx, fy);
                    }
                }

                int t = 0;
                for (int y = 0; y < grid - 1; y++)
                {
                    int row = y * grid;
                    int next = (y + 1) * grid;
                    for (int x = 0; x < grid - 1; x++)
                    {
                        int i0 = row + x;
                        int i1 = row + x + 1;
                        int i2 = next + x;
                        int i3 = next + x + 1;

                        indices[t++] = i0; indices[t++] = i2; indices[t++] = i1;
                        indices[t++] = i1; indices[t++] = i2; indices[t++] = i3;
                    }
                }

                _warpMesh = new Mesh
                {
                    name = $"SonsVR_V50_DepthWarpGrid_{grid}x{grid}",
                    indexFormat = IndexFormat.UInt32
                };
                _warpMesh.vertices = vertices;
                _warpMesh.uv = uvs;
                _warpMesh.triangles = indices;
                _warpMesh.RecalculateBounds();
                _warpMesh.UploadMeshData(true);
            }

            private static RenderTexture EnsureSyntheticTarget(int targetEye, RenderTexture sourceRT)
            {
                RenderTexture target = targetEye == 0 ? _syntheticLeftRT : _syntheticRightRT;
                string name = targetEye == 0 ? "SonsVR_V50_SyntheticLeft" : "SonsVR_V50_SyntheticRight";
                target = EnsureWorkingTarget(target, sourceRT, 0, name);

                if (targetEye == 0)
                    _syntheticLeftRT = target;
                else
                    _syntheticRightRT = target;

                return target;
            }

            private static RenderTexture EnsureWarpTarget(int targetEye, RenderTexture sourceRT)
            {
                RenderTexture target = targetEye == 0 ? _warpLeftRT : _warpRightRT;
                string name = targetEye == 0 ? "SonsVR_V50_WarpLeft" : "SonsVR_V50_WarpRight";
                target = EnsureWorkingTarget(target, sourceRT, 24, name);

                if (targetEye == 0)
                    _warpLeftRT = target;
                else
                    _warpRightRT = target;

                return target;
            }

            private static RenderTexture EnsureFillTarget(int targetEye, RenderTexture sourceRT)
            {
                RenderTexture target = targetEye == 0 ? _fillLeftRT : _fillRightRT;
                string name = targetEye == 0 ? "SonsVR_V50_FillLeft" : "SonsVR_V50_FillRight";
                target = EnsureWorkingTarget(target, sourceRT, 0, name);

                if (targetEye == 0)
                    _fillLeftRT = target;
                else
                    _fillRightRT = target;

                return target;
            }

            private static RenderTexture EnsureWorkingTarget(
                RenderTexture target, RenderTexture sourceRT, int depthBits, string name)
            {
                if (target != null && target.width == sourceRT.width && target.height == sourceRT.height &&
                    target.depth == depthBits && target.IsCreated())
                    return target;

                if (target != null)
                {
                    try { target.Release(); } catch { }
                    UnityEngine.Object.Destroy(target);
                }

                target = new RenderTexture(sourceRT.width, sourceRT.height, depthBits, RenderTextureFormat.ARGB32)
                {
                    name = name,
                    antiAliasing = 1,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                target.Create();
                return target;
            }

            private static RenderTexture EnsureScatterUIntTargetV88(
                RenderTexture target, int width, int height, string name)
            {
                if (target != null &&
                    target.width == width &&
                    target.height == height &&
                    target.graphicsFormat == GraphicsFormat.R32_UInt &&
                    target.enableRandomWrite &&
                    target.IsCreated())
                    return target;

                if (target != null)
                {
                    try { target.Release(); } catch { }
                    UnityEngine.Object.Destroy(target);
                }

                var desc = new RenderTextureDescriptor(width, height)
                {
                    graphicsFormat = GraphicsFormat.R32_UInt,
                    depthBufferBits = 0,
                    msaaSamples = 1,
                    volumeDepth = 1,
                    dimension = TextureDimension.Tex2D,
                    enableRandomWrite = true,
                    useMipMap = false,
                    autoGenerateMips = false
                };

                target = new RenderTexture(desc)
                {
                    name = name,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                target.Create();

                if (!target.IsCreated())
                    throw new Exception("failed to create V88 R32_UInt UAV: " + name);

                return target;
            }

            private static RenderTexture EnsureDummyOpticalFlowTargetV90()
            {
                if (_dummyOpticalFlowRTV90 != null &&
                    _dummyOpticalFlowRTV90.graphicsFormat == GraphicsFormat.R16G16_SInt &&
                    _dummyOpticalFlowRTV90.IsCreated())
                    return _dummyOpticalFlowRTV90;

                if (_dummyOpticalFlowRTV90 != null)
                {
                    try { _dummyOpticalFlowRTV90.Release(); } catch { }
                    UnityEngine.Object.Destroy(_dummyOpticalFlowRTV90);
                }

                var desc = new RenderTextureDescriptor(1, 1)
                {
                    graphicsFormat = GraphicsFormat.R16G16_SInt,
                    depthBufferBits = 0,
                    msaaSamples = 1,
                    volumeDepth = 1,
                    dimension = TextureDimension.Tex2D,
                    enableRandomWrite = true,
                    useMipMap = false,
                    autoGenerateMips = false
                };

                _dummyOpticalFlowRTV90 = new RenderTexture(desc)
                {
                    name = "SonsVR_V90_DummyOpticalFlow",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                _dummyOpticalFlowRTV90.Create();
                if (!_dummyOpticalFlowRTV90.IsCreated())
                    throw new Exception("failed to create V90 dummy R16G16_SInt optical-flow texture.");
                return _dummyOpticalFlowRTV90;
            }

            private static bool TryRenderPerPixelScatterV90(
                RenderTexture historyColorRT,
                RenderTexture historyDepthRT,
                RenderTexture historyMotionRTV89,
                RenderTexture historyOpticalFlowRTV90,
                RenderTexture warpRT,
                Vector4 zParams,
                Matrix4x4 sourceInvGpuProjection,
                Matrix4x4 sourceCameraToWorld,
                Matrix4x4 targetWorldToCamera,
                Matrix4x4 targetGpuProjection,
                Matrix4x4 previousSourceWorldToCameraV89,
                Matrix4x4 previousSourceGpuProjectionV89,
                bool useObjectMotionV89,
                float objectMotionScaleV89,
                float maxObjectMotionPixelsV89,
                bool useOpticalFlowV90,
                float opticalFlowTimeScaleV90,
                float maxOpticalFlowResidualPixelsV90,
                bool usePresentationGridScatterV98,
                bool useInvalidDepthRotationScatterV101,
                float maxWarpMetersV88,
                bool diagnosticOnlyV99 = false)
            {
                if (_scatterRuntimeDisabledV88 ||
                    _temporalScatterComputeV88 == null ||
                    _scatterKernelClearV88 < 0 ||
                    _scatterKernelDepthV88 < 0 ||
                    _scatterKernelResolveV88 < 0)
                    return false;

                try
                {
                    if (useObjectMotionV89 &&
                        (historyMotionRTV89 == null || !historyMotionRTV89.IsCreated() ||
                         historyMotionRTV89.width != historyDepthRT.width ||
                         historyMotionRTV89.height != historyDepthRT.height))
                        throw new Exception(
                            "V89 object motion requested without an exact full-resolution paired motion RT.");

                    if (useOpticalFlowV90 &&
                        (historyOpticalFlowRTV90 == null || !historyOpticalFlowRTV90.IsCreated() ||
                         historyOpticalFlowRTV90.width < 1 || historyOpticalFlowRTV90.height < 1 ||
                         historyOpticalFlowRTV90.graphicsFormat != GraphicsFormat.R16G16_SInt))
                        throw new Exception(
                            "V91 NVOFA requested without a valid R16G16_SInt flow RT.");

                    _scatterDepthKeyRTV88 = EnsureScatterUIntTargetV88(
                        _scatterDepthKeyRTV88,
                        warpRT.width,
                        warpRT.height,
                        "SonsVR_V89_ScatterDepthKey");

                    _scatterSourceIndexRTV88 = EnsureScatterUIntTargetV88(
                        _scatterSourceIndexRTV88,
                        warpRT.width,
                        warpRT.height,
                        "SonsVR_V89_ScatterSourceIndex");

                    int sourceGridWidthV98 = usePresentationGridScatterV98
                        ? historyColorRT.width
                        : historyDepthRT.width;
                    int sourceGridHeightV98 = usePresentationGridScatterV98
                        ? historyColorRT.height
                        : historyDepthRT.height;

                    Vector4 sourceSize = new Vector4(
                        sourceGridWidthV98,
                        sourceGridHeightV98,
                        1.0f / Mathf.Max(1, sourceGridWidthV98),
                        1.0f / Mathf.Max(1, sourceGridHeightV98));
                    Vector4 depthSizeV98 = new Vector4(
                        historyDepthRT.width,
                        historyDepthRT.height,
                        1.0f / Mathf.Max(1, historyDepthRT.width),
                        1.0f / Mathf.Max(1, historyDepthRT.height));
                    Vector4 targetSize = new Vector4(
                        warpRT.width,
                        warpRT.height,
                        1.0f / Mathf.Max(1, warpRT.width),
                        1.0f / Mathf.Max(1, warpRT.height));
                    RenderTexture flowSizeRTV91 = historyOpticalFlowRTV90 != null
                        ? historyOpticalFlowRTV90
                        : EnsureDummyOpticalFlowTargetV90();
                    Vector4 opticalFlowSizeV91 = new Vector4(
                        flowSizeRTV91.width,
                        flowSizeRTV91.height,
                        1.0f / Mathf.Max(1, flowSizeRTV91.width),
                        1.0f / Mathf.Max(1, flowSizeRTV91.height));

                    ComputeShader cs = _temporalScatterComputeV88;
                    cs.SetVector("_ZBufferParams", zParams);
                    cs.SetVector("_SourceSize", sourceSize);
                    cs.SetVector("_DepthSizeV98", depthSizeV98);
                    cs.SetVector("_TargetSize", targetSize);
                    cs.SetVector("_OpticalFlowSizeV91", opticalFlowSizeV91);
                    cs.SetFloat("_MaxWarpMeters", maxWarpMetersV88);
                    cs.SetFloat("_UseInvalidDepthRotationScatterV101", useInvalidDepthRotationScatterV101 ? 1f : 0f);
                    cs.SetMatrix("_SourceInvGpuProjection", sourceInvGpuProjection);
                    cs.SetMatrix("_SourceCameraToWorld", sourceCameraToWorld);
                    cs.SetMatrix("_TargetWorldToCamera", targetWorldToCamera);
                    cs.SetMatrix("_TargetGpuProjection", targetGpuProjection);
                    cs.SetMatrix("_PrevSourceWorldToCamera", previousSourceWorldToCameraV89);
                    cs.SetMatrix("_PrevSourceGpuProjection", previousSourceGpuProjectionV89);
                    cs.SetFloat("_UseObjectMotion", useObjectMotionV89 ? 1f : 0f);
                    cs.SetFloat("_ObjectMotionScale", objectMotionScaleV89);
                    cs.SetFloat("_MaxObjectMotionPixels",
                        Mathf.Max(0f, maxObjectMotionPixelsV89));
                    cs.SetFloat("_UseOpticalFlowV90", useOpticalFlowV90 ? 1f : 0f);
                    cs.SetFloat("_OpticalFlowTimeScaleV90", Mathf.Max(0f, opticalFlowTimeScaleV90));
                    cs.SetFloat("_MaxOpticalFlowResidualPixelsV90",
                        Mathf.Max(0f, maxOpticalFlowResidualPixelsV90));

                    // Bind valid textures in both phases. In depth-only Phase A the shader
                    // never consumes _SourceMotion, but binding historyDepthRT as a harmless
                    // fallback avoids backend-specific null-SRV behavior before V89 has a pair.
                    Texture motionBindingV89 = historyMotionRTV89 != null
                        ? (Texture)historyMotionRTV89
                        : (Texture)historyDepthRT;
                    RenderTexture opticalFlowBindingV90 = historyOpticalFlowRTV90 != null
                        ? historyOpticalFlowRTV90
                        : EnsureDummyOpticalFlowTargetV90();

                    int targetGroupsX = (warpRT.width + 7) / 8;
                    int targetGroupsY = (warpRT.height + 7) / 8;
                    int sourceGroupsX = (sourceGridWidthV98 + 7) / 8;
                    int sourceGroupsY = (sourceGridHeightV98 + 7) / 8;

                    cs.SetTexture(
                        _scatterKernelClearV88,
                        "_DepthKey",
                        _scatterDepthKeyRTV88);
                    cs.SetTexture(
                        _scatterKernelClearV88,
                        "_SourceIndex",
                        _scatterSourceIndexRTV88);
                    cs.Dispatch(
                        _scatterKernelClearV88,
                        targetGroupsX,
                        targetGroupsY,
                        1);

                    cs.SetTexture(
                        _scatterKernelDepthV88,
                        "_SourceDepth",
                        historyDepthRT);
                    cs.SetTexture(
                        _scatterKernelDepthV88,
                        "_SourceMotion",
                        motionBindingV89);
                    cs.SetTexture(
                        _scatterKernelDepthV88,
                        "_SourceOpticalFlowV90",
                        opticalFlowBindingV90);
                    cs.SetTexture(
                        _scatterKernelDepthV88,
                        "_DepthKey",
                        _scatterDepthKeyRTV88);
                    cs.Dispatch(
                        _scatterKernelDepthV88,
                        sourceGroupsX,
                        sourceGroupsY,
                        1);

                    cs.SetTexture(
                        _scatterKernelResolveV88,
                        "_SourceDepth",
                        historyDepthRT);
                    cs.SetTexture(
                        _scatterKernelResolveV88,
                        "_SourceMotion",
                        motionBindingV89);
                    cs.SetTexture(
                        _scatterKernelResolveV88,
                        "_SourceOpticalFlowV90",
                        opticalFlowBindingV90);
                    cs.SetTexture(
                        _scatterKernelResolveV88,
                        "_DepthKey",
                        _scatterDepthKeyRTV88);
                    cs.SetTexture(
                        _scatterKernelResolveV88,
                        "_SourceIndex",
                        _scatterSourceIndexRTV88);
                    cs.Dispatch(
                        _scatterKernelResolveV88,
                        sourceGroupsX,
                        sourceGroupsY,
                        1);

                    // Resolve winning historical SOURCE pixel coordinates to color using
                    // the ordinary material sampling path. This avoids relying on compute
                    // sampling semantics for the possibly-MSAA historical color RT.
                    _material.SetTexture("_SourceColor", historyColorRT);
                    _material.SetTexture(
                        "_ScatterSourceIndexV88",
                        _scatterSourceIndexRTV88);
                    _material.SetVector("_SourceSize", sourceSize);
                    _material.SetVector("_TargetSize", targetSize);

                    SetRenderTargetFullViewportV77(warpRT);
                    GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));

                    int resolvePass =
                        _material.FindPass("PerPixelScatterResolveV88");
                    if (resolvePass < 0 || !_material.SetPass(resolvePass))
                        throw new Exception(
                            "DepthStereoWarp PerPixelScatterResolveV88 pass unavailable.");

                    DrawFullscreenTopLeftUvQuad();
                    return true;
                }
                catch (Exception e)
                {
                    // A compute-path exception is assumed structural for this activation.
                    // Disable further dispatch attempts so a fail-soft build cannot turn
                    // one bad API/resource state into a repeated per-frame hitch.
                    if (!diagnosticOnlyV99)
                        _scatterRuntimeDisabledV88 = true;

                    if (!diagnosticOnlyV99 && !_perPixelRuntimeFailureLoggedV88)
                    {
                        _perPixelRuntimeFailureLoggedV88 = true;
                        MelonLogger.Warning(
                            "[StereoRender][AER V91][Scatter] runtime failure; " +
                            "compute disabled for the remainder of this run; 768 mesh retained. " +
                            e.GetType().Name + ": " + e.Message);
                    }
                    return false;
                }
            }

            // -----------------------------------------------------------------
            // V99 - ONE-LAUNCH GROUND-TRUTH FORENSICS
            // -----------------------------------------------------------------
            // V102 reuses the V99 truth-frame machinery but turns the candidate matrix into a
            // clean 2 x 3 factorial test of the two strongest suspects left after V101:
            //
            //   source grid  : PRESENTATION post-DLSS color pixels vs the native DEPTH lattice
            //   spatial fill : radius1+radius2 vs radius1 only vs NO spatial fill
            //
            // Every reconstructed candidate uses the same full 6DoF finite warp and V100's
            // rotation-only final TRUE-hole fallback.  Thus differences isolate either
            // post-DLSS-color/low-res-depth association or cross-layer HoleFill behavior.
            // V139: original V99/V102 7 candidates + five guarded-fill grid variants.
            private const int V99CandidateCount = 12;
            private const int V101DepthClassCount = 5;
            private const int V102OcclusionClassCount = 6;
            private const int V101FiniteStatsBase = V99CandidateCount;
            private const int V101ClassStatsBase = V101FiniteStatsBase + V99CandidateCount;
            private const int V102OcclusionStatsBase = V101ClassStatsBase + V99CandidateCount * V101DepthClassCount;
            private const int V101FieldBase = V102OcclusionStatsBase + V99CandidateCount * V102OcclusionClassCount;
            private const int V101BandGeometric = V101FieldBase + 0;
            private const int V101BandHdrpTotal = V101FieldBase + 1;
            private const int V101BandHdrpResidual = V101FieldBase + 2;
            private const int V101BandNvofaPredictiveTotal = V101FieldBase + 3;
            private const int V101BandNvofaPredictiveResidual = V101FieldBase + 4;
            private const int V101BandHistoryDepth = V101FieldBase + 5;
            private const int V101BandTruthDepth = V101FieldBase + 6;
            private const int V101CoverageBase = V101FieldBase + 7; // reconstructed candidates 1..6
            private const int V101BandExactNvofa = V101CoverageBase + (V99CandidateCount - 1);
            private const int V101BandExactNvofaResidual = V101BandExactNvofa + 1;
            private const int V101BandSceneStructure = V101BandExactNvofaResidual + 1;
            private const int V101BandSkyRayClass = V101BandSceneStructure + 1;
            private const int V102BandOcclusionComplexity = V101BandSkyRayClass + 1;
            private const int V102BandDepthColorRisk = V102BandOcclusionComplexity + 1;
            private const int V99AtlasBands = V102BandDepthColorRisk + 1;

            private static readonly string[] V99CandidateNames = new string[]
            {
                "RAW_HISTORY",
                "PRESENTATION_FILL_R1_R2",
                "PRESENTATION_FILL_R1",
                "PRESENTATION_NO_FILL",
                "DEPTHGRID_FILL_R1_R2",
                "DEPTHGRID_FILL_R1",
                "DEPTHGRID_NO_FILL",
                "V103_GUARD_BASE",
                "V103_GUARD_STRICT",
                "V103_GUARD_RELAXED",
                "V103_GUARD_MID",
                "V103_GUARD_HIGH_SUPPORT"
            };

            // Candidate indices 1..6.  This is deliberately symmetric so differences can be
            // interpreted as a source-grid effect, a fill effect, or their interaction.
            private static readonly bool[] V102PresentationGrid = new bool[]
            { true, true, true, false, false, false };
            private static readonly int[] V102FillPasses = new int[]
            { 2, 1, 0, 2, 1, 0 };

            private static readonly string[] V101DepthClassNames = new string[]
            {
                "NEAR_0_2M",
                "MID_2_10M",
                "FAR_10_50M",
                "DISTANT_50M_PLUS",
                "NO_DEPTH_SKYLIKE"
            };

            private static readonly string[] V102OcclusionClassNames = new string[]
            {
                "STABLE_NO_DEPTH",
                "CURRENT_NO_HISTORY_FINITE",
                "CURRENT_FINITE_HISTORY_NO",
                "FINITE_AGREE",
                "HISTORY_NEARER_DISOCCLUSION",
                "HISTORY_FARTHER_NEW_OCCLUDER"
            };

            private sealed class V99HistorySnapshot
            {
                internal bool Valid;
                internal int Eye;
                internal int Frame;
                internal int PreviousRealFrame;
                internal float CaptureTime;
                internal float PreviousRealCaptureTime;
                internal Vector4 ZParams;
                internal Matrix4x4 SourceInvGpuProjection;
                internal Matrix4x4 SourceCameraToWorld;
                internal Matrix4x4 SourceWorldToCamera;
                internal Matrix4x4 SourceProjection;
                internal Matrix4x4 PreviousRealWorldToCamera;
                internal Matrix4x4 PreviousRealGpuProjection;
                internal Vector3 SourcePosition;
                internal Quaternion SourceRotation;
                internal RenderTexture Color;
                internal RenderTexture Depth;
                internal RenderTexture Motion;
                internal RenderTexture Flow;
                internal bool MotionValid;
                internal bool FlowValid;
                internal int FlowWidth;
                internal int FlowHeight;
            }

            private sealed class V99PendingReadback
            {
                internal bool Pending;
                internal AsyncGPUReadbackRequest Request;
                internal int SampleId;
                internal int Eye;
                internal int TruthFrame;
                internal int HistoryFrame;
                internal int PresentationWidth;
                internal int PresentationHeight;
                internal int Columns;
                internal int Rows;
                internal Matrix4x4 TargetProjection;
                internal float HistoryAgeMs;
                internal float OpticalFlowScale;
                internal string MotionStatus;
                internal string FlowStatus;
                internal string TruthFlowStatus;
                internal string EpochV102;
                internal int EpochSampleV102;
                internal float FarClipMetersV102;
                internal string CameraNameV102;
            }

            private static readonly V99HistorySnapshot[] _v99Snapshots =
                new V99HistorySnapshot[] { new V99HistorySnapshot(), new V99HistorySnapshot() };
            private static readonly V99PendingReadback[] _v99Readbacks =
                new V99PendingReadback[] { new V99PendingReadback(), new V99PendingReadback() };
            // V139C: keep the genuine truth frame and matching history IMMUTABLE while
            // dispatching one candidate per confirmed GPU-completed stage. All textures are
            // diagnostic; live AER and SteamVR submission use their original render targets.
            private sealed class V139CStagedEye
            {
                internal EyeDepthLatch TruthLatch;
                internal RenderTexture TruthColor;
                internal RenderTexture TruthDepth;
                internal RenderTexture TruthMotion;
                internal RenderTexture Atlas;
                internal int Sample;
                internal int Eye;
                internal int Candidate;
                internal int Columns;
                internal int Rows;
                internal bool Ready;
                internal bool FieldsDone;
                internal float FlowScale;
                internal float MaxWarpMeters;
            }
            private static readonly V139CStagedEye[] _v139cStaged =
                { new V139CStagedEye(), new V139CStagedEye() };
            private static bool _v139cJobActive;
            private static int _v139cPendingEye = -1;
            private static AsyncGPUReadbackRequest _v139cFence;
            private static float _v139cFenceStarted;
            private static int _v139cLastScheduledFrame = -1;
            private static int _v139cStagesSubmitted;
            private static int _v139cStagesConfirmed;
            private static float _v139cLastHeartBeat;

            private static RenderTexture _v99WarpRT;
            private static RenderTexture _v99FillRT;
            private static RenderTexture _v99FinalRT;
            private static readonly RenderTexture[] _v99AtlasRT = new RenderTexture[2];
            private static int _v99NextTruthFrame = -1;
            private static int _v99TruthFrame = -1;
            private static int _v99SamplesScheduled;
            private static int _v99SampleId;
            private static bool _v99TruthPending;
            private static bool _v99EyeGeometryLogged;
            private static int _v99FailureLogsRemaining = 16;

            // V102 truth-bank state.  Initial activation is GAMEPLAY_24000M.  The first
            // far-clip transition <=100m switches to TARP_50M and rearms a fresh sample bank.
            // If the tarp closes before all samples finish, scheduling pauses and resumes on
            // the next <=100m transition rather than silently collecting gameplay frames.
            private static string _v102TruthEpoch = "GAMEPLAY_24000M";
            private static float _v102CurrentFarClipMeters = -1f;
            private static bool _v102TarpBankArmed;
            private static int _v102GameplayFinalReadbackMask;
            private static int _v102TarpFinalReadbackMask;

            // V103 perceptual fill test state.  Fill mode: 0=current baseline, 1=depth-guard,
            // 2=no spatial fill.  The same four-phase sequence is run once on the tarp's
            // problematic layered tree/rock slice and once on close grass/feet.
            private static float _v103TarpDetectedTime = -1f;
            private static bool _v103TarpActive;
            private static int _v103LastPhase = -999;
            // V141: log only first effective AER frame and genuine profile transitions.
            private static int _v141LastSpatialFillMode = -1;
            private static bool _v103DepthGuardUnavailableLogged;

            private static void ResetV103PerceptualTest()
            {
                _v103TarpDetectedTime = -1f;
                _v103TarpActive = false;
                _v103LastPhase = -999;
                _v103DepthGuardUnavailableLogged = false;
                if (AerV103DepthGuardFillPerceptualTestEnabled)
                    StereoRender.SetAerV103HmdCue(
                        "V103 READY\nOPEN TARP - FIND WOBBLY ROCK + TREES");
            }

            private static void NotifyFarClipChangeV103(float farClipMeters)
            {
                if (!AerV103DepthGuardFillPerceptualTestEnabled) return;

                _v103TarpActive = farClipMeters <= 100.0f;
                if (_v103TarpActive && _v103TarpDetectedTime < 0f)
                {
                    _v103TarpDetectedTime = Time.unscaledTime;
                    _v103LastPhase = -999;
                    MelonLogger.Warning(
                        $"[StereoRender][AER V103][Start] tarp farClip={farClipMeters:F1}m detected. " +
                        "Beginning HMD-guided TREE then GRASS A/B/C/A2 fill test.");
                }
            }

            // Returns the live fill mode for the current V103 perceptual phase:
            //   0 = current unguarded R1+R2 fill
            //   1 = V103 depth-coherence guarded R1+R2 fill
            //   2 = no spatial fill; V100 rotation-only final-hole fallback handles holes
            private static int UpdateV103PerceptualPhase()
            {
                if (!AerV103DepthGuardFillPerceptualTestEnabled)
                    return 0;

                if (_v103TarpDetectedTime < 0f)
                {
                    StereoRender.SetAerV103HmdCue(
                        "V103 READY\nOPEN TARP - FIND WOBBLY ROCK + TREES");
                    return 0;
                }

                if (!_v103TarpActive)
                {
                    StereoRender.SetAerV103HmdCue(
                        "V103 PAUSED\nREOPEN TARP");
                    return 0;
                }

                float setup = Mathf.Max(3f, AerV103SetupSeconds);
                float phase = Mathf.Max(4f, AerV103PhaseSeconds);
                float phaseCueSeconds = Mathf.Min(3.0f, phase * 0.35f);
                float t = Mathf.Max(0f, Time.unscaledTime - _v103TarpDetectedTime);

                int logicalPhase;
                int fillMode;
                string cue;

                if (t < setup)
                {
                    logicalPhase = 0; fillMode = 0;
                    int remain = Mathf.Max(1, Mathf.CeilToInt(setup - t));
                    cue = $"TREE/ROCK TEST\nFIND WOBBLY SLICE - STARTS IN {remain}";
                }
                else if ((t -= setup) < phase)
                {
                    logicalPhase = 1; fillMode = 0;
                    cue = t < phaseCueSeconds ? "TREE/ROCK  A / 4\nBASELINE - CARD VANISHES = SWEEP" : "";
                }
                else if ((t -= phase) < phase)
                {
                    logicalPhase = 2; fillMode = 1;
                    cue = t < phaseCueSeconds ? "TREE/ROCK  B / 4\nDEPTH-GUARD - CARD VANISHES = SAME SWEEP" : "";
                }
                else if ((t -= phase) < phase)
                {
                    logicalPhase = 3; fillMode = 2;
                    cue = t < phaseCueSeconds ? "TREE/ROCK  C / 4\nNO FILL - CARD VANISHES = SAME SWEEP" : "";
                }
                else if ((t -= phase) < phase)
                {
                    logicalPhase = 4; fillMode = 0;
                    cue = t < phaseCueSeconds ? "TREE/ROCK  A2 / 4\nBASELINE AGAIN - CARD VANISHES = SAME SWEEP" : "";
                }
                else if ((t -= phase) < setup)
                {
                    logicalPhase = 5; fillMode = 0;
                    int remain = Mathf.Max(1, Mathf.CeilToInt(setup - t));
                    cue = $"CONTROL TEST\nLOOK DOWN AT CLOSE GRASS/FEET - {remain}";
                }
                else if ((t -= setup) < phase)
                {
                    logicalPhase = 6; fillMode = 0;
                    cue = t < phaseCueSeconds ? "CLOSE GRASS  A / 4\nBASELINE - CARD VANISHES = SWEEP" : "";
                }
                else if ((t -= phase) < phase)
                {
                    logicalPhase = 7; fillMode = 1;
                    cue = t < phaseCueSeconds ? "CLOSE GRASS  B / 4\nDEPTH-GUARD - CARD VANISHES = SAME SWEEP" : "";
                }
                else if ((t -= phase) < phase)
                {
                    logicalPhase = 8; fillMode = 2;
                    cue = t < phaseCueSeconds ? "CLOSE GRASS  C / 4\nNO FILL - CARD VANISHES = SAME SWEEP" : "";
                }
                else if ((t -= phase) < phase)
                {
                    logicalPhase = 9; fillMode = 0;
                    cue = t < phaseCueSeconds ? "CLOSE GRASS  A2 / 4\nBASELINE AGAIN - CARD VANISHES = SAME SWEEP" : "";
                }
                else
                {
                    logicalPhase = 10; fillMode = 0;
                    cue = "V103 TEST COMPLETE\nREPORT TREE + GRASS: A / B / C / A2";
                }

                StereoRender.SetAerV103HmdCue(cue);
                if (logicalPhase != _v103LastPhase)
                {
                    _v103LastPhase = logicalPhase;
                    string mode = fillMode == 1 ? "DEPTH_GUARD" :
                                  fillMode == 2 ? "NO_FILL" : "BASELINE_R1_R2";
                    MelonLogger.Warning(
                        $"[StereoRender][AER V103][Phase] logical={logicalPhase} fillMode={mode} " +
                        $"elapsedSinceTarp={Time.unscaledTime - _v103TarpDetectedTime:F1}s.");
                }
                return fillMode;
            }

            private static void ResetForensicsV99()
            {
                _v99NextTruthFrame = -1;
                _v99TruthFrame = -1;
                _v99SamplesScheduled = 0;
                _v99SampleId = 0;
                _v99TruthPending = false;
                _v99EyeGeometryLogged = false;
                _v99FailureLogsRemaining = 16;
                _v102TruthEpoch = "GAMEPLAY_24000M";
                _v102CurrentFarClipMeters = -1f;
                _v102TarpBankArmed = false;
                _v102GameplayFinalReadbackMask = 0;
                _v102TarpFinalReadbackMask = 0;
                StereoRender.SetAerV102HmdCue("");
                ResetV103PerceptualTest();
                for (int i = 0; i < 2; ++i)
                {
                    _v99Snapshots[i].Valid = false;
                    _v99Readbacks[i].Pending = false;
                }
            }

            internal static void NotifyFarClipChangeV102(float farClipMeters)
            {
                _v102CurrentFarClipMeters = farClipMeters;
                NotifyFarClipChangeV103(farClipMeters);
                if (!AerV102OcclusionForensicsEnabled || farClipMeters > 100.0f)
                    return;

                if (!_v102TarpBankArmed)
                {
                    _v102TarpBankArmed = true;
                    _v102TruthEpoch = "TARP_50M";
                    _v99SamplesScheduled = 0;
                    _v102TarpFinalReadbackMask = 0;
                    // V102b: the disappearing "OPEN TARP NOW" prompt is itself the tester's
                    // confirmation that farClip<=100 m was detected. Keep the headset clean
                    // throughout the following delay and entire TARP truth bank.
                    StereoRender.SetAerV102HmdCue("");
                    _v99NextTruthFrame = Time.frameCount + Mathf.Max(10, AerV102TarpTruthInitialDelayFrames);
                    MelonLogger.Warning(
                        $"[StereoRender][AER V102][TruthEpoch] armed epoch={_v102TruthEpoch} " +
                        $"farClip={farClipMeters:F1}m initialDelay={AerV102TarpTruthInitialDelayFrames}f " +
                        $"samples={AerV102TarpTruthSampleCount}. Keep the tarp open until sample bank completes.");
                }
                else
                {
                    MelonLogger.Msg(
                        $"[StereoRender][AER V102][TruthEpoch] tarp farClip active again at {farClipMeters:F1}m; " +
                        $"epoch sample bank will resume at {_v99SamplesScheduled}/{Mathf.Max(1,AerV102TarpTruthSampleCount)}.");
                }
            }

            // V102b: mark ONLY the two eye readbacks of the final sample in each bank.
            // The milestone is announced after BOTH readbacks have actually been processed, not
            // merely when sample 6 was scheduled. This removes all need for the tester to see logs.
            private static void MarkV102ReadbackMilestone(V99PendingReadback p)
            {
                if (!AerV102OcclusionForensicsEnabled || p == null) return;

                int finalSample = p.EpochV102 == "TARP_50M"
                    ? Mathf.Max(1, AerV102TarpTruthSampleCount)
                    : Mathf.Max(1, AerV99TruthSampleCount);
                if (p.EpochSampleV102 != finalSample) return;

                int bit = 1 << Mathf.Clamp(p.Eye, 0, 1);
                if (p.EpochV102 == "TARP_50M")
                {
                    _v102TarpFinalReadbackMask |= bit;
                    if ((_v102TarpFinalReadbackMask & 3) == 3)
                    {
                        StereoRender.SetAerV102HmdCue("V102 CAPTURE COMPLETE\nDONE - YOU CAN PACK THE TRACE");
                        MelonLogger.Warning("[StereoRender][AER V102b][Milestone] TARP_50M BOTH final-eye readbacks complete. Tester may close tarp / end run / PACK_V102_TRACE.bat.");
                    }
                }
                else if (p.EpochV102 == "GAMEPLAY_24000M")
                {
                    _v102GameplayFinalReadbackMask |= bit;
                    if ((_v102GameplayFinalReadbackMask & 3) == 3)
                    {
                        StereoRender.SetAerV102HmdCue("V102 GAMEPLAY CAPTURE COMPLETE\nOPEN TARP NOW");
                        MelonLogger.Warning("[StereoRender][AER V102b][Milestone] GAMEPLAY_24000M BOTH final-eye readbacks complete. Tester should OPEN TARP NOW.");
                    }
                }
            }

            private static void V99LogFailure(string message)
            {
                if (_v99FailureLogsRemaining <= 0) return;
                _v99FailureLogsRemaining--;
                MelonLogger.Warning("[StereoRender][AER V99][Forensics] " + message +
                    " logsRemaining=" + _v99FailureLogsRemaining);
            }

            private static RenderTexture EnsureV99CopyTarget(
                RenderTexture existing,
                RenderTexture source,
                string name)
            {
                if (source == null || !source.IsCreated()) return null;
                if (existing != null && existing.IsCreated() &&
                    existing.width == source.width && existing.height == source.height &&
                    existing.graphicsFormat == source.graphicsFormat)
                    return existing;

                if (existing != null)
                {
                    try { existing.Release(); } catch { }
                    UnityEngine.Object.Destroy(existing);
                }

                RenderTextureDescriptor d = source.descriptor;
                d.depthBufferBits = 0;
                d.msaaSamples = 1;
                d.bindMS = false;
                d.useMipMap = false;
                d.autoGenerateMips = false;
                d.enableRandomWrite = false;
                RenderTexture rt = new RenderTexture(d)
                {
                    name = name,
                    filterMode = source.filterMode,
                    wrapMode = TextureWrapMode.Clamp,
                    useDynamicScale = false
                };
                rt.Create();
                return rt;
            }

            private static RenderTexture EnsureV99PresentationTarget(
                RenderTexture existing,
                int width,
                int height,
                string name,
                bool randomWrite)
            {
                if (existing != null && existing.IsCreated() &&
                    existing.width == width && existing.height == height)
                    return existing;
                if (existing != null)
                {
                    try { existing.Release(); } catch { }
                    UnityEngine.Object.Destroy(existing);
                }
                RenderTexture rt = new RenderTexture(
                    width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                {
                    name = name,
                    antiAliasing = 1,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    useDynamicScale = false,
                    useMipMap = false,
                    autoGenerateMips = false
                };
                rt.Create();
                return rt;
            }

            private static RenderTexture EnsureV99Atlas(int eye, int columns, int rows)
            {
                int width = Mathf.Max(4, columns);
                int height = Mathf.Max(4, rows) * V99AtlasBands;
                RenderTexture rt = _v99AtlasRT[eye];
                if (rt != null && rt.IsCreated() && rt.width == width && rt.height == height &&
                    rt.graphicsFormat == GraphicsFormat.R32G32B32A32_SFloat)
                    return rt;
                if (rt != null)
                {
                    try { rt.Release(); } catch { }
                    UnityEngine.Object.Destroy(rt);
                }
                RenderTextureDescriptor d = new RenderTextureDescriptor(width, height)
                {
                    graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat,
                    depthBufferBits = 0,
                    msaaSamples = 1,
                    volumeDepth = 1,
                    dimension = TextureDimension.Tex2D,
                    enableRandomWrite = true,
                    useMipMap = false,
                    autoGenerateMips = false
                };
                rt = new RenderTexture(d)
                {
                    name = "SonsVR_V99_ForensicsAtlas_" + (eye == 0 ? "L" : "R"),
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    useDynamicScale = false
                };
                rt.Create();
                _v99AtlasRT[eye] = rt;
                return rt;
            }

            private static bool CopyV99Texture(RenderTexture source, ref RenderTexture target, string name, bool blit)
            {
                if (source == null || !source.IsCreated()) return false;
                target = EnsureV99CopyTarget(target, source, name);
                if (target == null || !target.IsCreated()) return false;
                if (blit) Graphics.Blit(source, target);
                else Graphics.CopyTexture(source, target);
                return true;
            }

            private static void LogEyeGeometryV99(StereoRender self)
            {
                if (_v99EyeGeometryLogged || self == null || OpenVR.System == null) return;
                _v99EyeGeometryLogged = true;
                try
                {
                    HmdMatrix34_t l = OpenVR.System.GetEyeToHeadTransform(EVREye.Eye_Left);
                    HmdMatrix34_t r = OpenVR.System.GetEyeToHeadTransform(EVREye.Eye_Right);
                    Vector3 lp = self.LeftCam != null ? self.LeftCam.transform.localPosition : Vector3.zero;
                    Vector3 rp = self.RightCam != null ? self.RightCam.transform.localPosition : Vector3.zero;
                    Vector3 lrt = new Vector3(l.m3,l.m7,l.m11);
                    Vector3 rrt = new Vector3(r.m3,r.m7,r.m11);
                    MelonLogger.Warning(
                        "[StereoRender][AER V99][EyeGeometry] OpenVR eyeToHead " +
                        $"L.t=({l.m3:F6},{l.m7:F6},{l.m11:F6}) " +
                        $"R.t=({r.m3:F6},{r.m7:F6},{r.m11:F6}) " +
                        $"runtimeSeparation={(rrt-lrt).magnitude:F6}m; " +
                        $"UnityLocal L=({lp.x:F6},{lp.y:F6},{lp.z:F6}) " +
                        $"R=({rp.x:F6},{rp.y:F6},{rp.z:F6}) " +
                        $"UnitySeparation={(rp-lp).magnitude:F6}m. " +
                        $"eyeToHeadBasisL=[{l.m0:F6},{l.m1:F6},{l.m2:F6};{l.m4:F6},{l.m5:F6},{l.m6:F6};{l.m8:F6},{l.m9:F6},{l.m10:F6}] " +
                        $"eyeToHeadBasisR=[{r.m0:F6},{r.m1:F6},{r.m2:F6};{r.m4:F6},{r.m5:F6},{r.m6:F6};{r.m8:F6},{r.m9:F6},{r.m10:F6}]. " +
                        "This directly measures the original mod's fixed-eye geometry against runtime calibration.");
                }
                catch (Exception e)
                {
                    V99LogFailure("GetEyeToHeadTransform telemetry failed: " + e.Message);
                }
            }

            private static bool SnapshotEyeHistoryV99(StereoRender self, int eye)
            {
                V99HistorySnapshot s = _v99Snapshots[eye];
                EyeDepthLatch latch = eye == 0 ? _leftDepthLatch : _rightDepthLatch;
                RenderTexture color = eye == 0 ? self.LeftRT : self.RightRT;
                if (latch == null || !latch.Valid || latch.DepthRT == null ||
                    !latch.DepthRT.IsCreated() || color == null || !color.IsCreated())
                    return false;

                if (!CopyV99Texture(color, ref s.Color,
                    "SonsVR_V99_HistoryColor_" + (eye == 0 ? "L" : "R"), true))
                    return false;
                if (!CopyV99Texture(latch.DepthRT, ref s.Depth,
                    "SonsVR_V99_HistoryDepth_" + (eye == 0 ? "L" : "R"), false))
                    return false;

                s.MotionValid = false;
                if (latch.UsedCustomPassMotionV89 && latch.MotionRTV89 != null && latch.MotionRTV89.IsCreated())
                {
                    s.MotionValid = CopyV99Texture(
                        latch.MotionRTV89, ref s.Motion,
                        "SonsVR_V99_HistoryMotion_" + (eye == 0 ? "L" : "R"), false);
                }

                s.FlowValid = false;
                RenderTexture flow;
                double apiMs;
                ulong execCount;
                if (NvOpticalFlowV90.TryGetFlowForHistory(eye, latch.Frame, out flow, out apiMs, out execCount) &&
                    flow != null && flow.IsCreated())
                {
                    s.FlowValid = CopyV99Texture(
                        flow, ref s.Flow,
                        "SonsVR_V99_HistoryFlow_" + (eye == 0 ? "L" : "R"), false);
                    if (s.FlowValid)
                    {
                        s.FlowWidth = flow.width;
                        s.FlowHeight = flow.height;
                    }
                }

                s.Eye = eye;
                s.Frame = latch.Frame;
                s.PreviousRealFrame = latch.PreviousRealFrameV89;
                s.CaptureTime = latch.CaptureUnscaledTime;
                s.PreviousRealCaptureTime = latch.PreviousRealCaptureUnscaledTimeV90;
                s.ZParams = latch.ZParams;
                s.SourceInvGpuProjection = latch.SourceInvGpuProjection;
                s.SourceCameraToWorld = latch.SourceCameraToWorld;
                s.SourceWorldToCamera = latch.SourceWorldToCamera;
                s.SourceProjection = latch.SourceProjection;
                s.PreviousRealWorldToCamera = latch.PreviousRealWorldToCameraV89;
                s.PreviousRealGpuProjection = latch.PreviousRealGpuProjectionV89;
                s.SourcePosition = latch.SourcePosition;
                s.SourceRotation = latch.SourceRotation;
                s.Valid = true;
                return true;
            }

            // V139: begin independent truth bank after prior bank's BOTH final readbacks
            // completed; no pending asynchronous GPU readbacks may be dropped.
            internal static bool TryBeginCalibrationBankV139(int samples)
            {
                if (_v139cJobActive || _v139cPendingEye >= 0 || _v99TruthPending ||
                    _v99Readbacks[0].Pending || _v99Readbacks[1].Pending)
                    return false;
                ResetForensicsV99();
                AerV99TruthSampleCount = Mathf.Clamp(samples, 2, 12);
                AerV99TruthInitialDelayFrames = 40;
                AerV99TruthIntervalFrames = 45;
                AerV99TileColumns = 16;
                AerV99TileRows = 12;
                // V139C: 6px search rather than 8px: 169 vs 289 displacements/tile.
                // Readback keeps full error/confidence; tiles saturating the range
                // must not be interpreted as valid convergence by the postprocessor.
                AerV99BlockMatchSearchRadiusPixels = 6;
                // V99 deliberately exposed pre-OF jitter in its historical test.  V139
                // must NOT change the live displayed temporal correction policy.
                AerV99ExposePreOpticalFlowJitter = false;
                AerV99ForensicsEnabled = true;
                return true;
            }

            internal static bool TryScheduleTruthFrameV99(
                StereoRender self,
                HmdMatrix34_t renderPose)
            {
                if (!AerV99ForensicsEnabled || self == null)
                    return false;

                if (_v102CurrentFarClipMeters < 0f && self.LeftCam != null)
                    _v102CurrentFarClipMeters = self.LeftCam.farClipPlane;

                bool tarpEpochV102 = AerV102OcclusionForensicsEnabled && _v102TruthEpoch == "TARP_50M";
                int epochSampleLimitV102 = tarpEpochV102
                    ? Mathf.Max(1, AerV102TarpTruthSampleCount)
                    : Mathf.Max(1, AerV99TruthSampleCount);

                // A partially collected tarp bank must never silently spill into ordinary gameplay.
                if (tarpEpochV102 && _v102CurrentFarClipMeters > 100.0f)
                    return false;
                if (_v99SamplesScheduled >= epochSampleLimitV102)
                    return false;
                if (!EnsureLoaded() || _forensicsComputeV99 == null)
                    return false;

                int initialDelayV102 = tarpEpochV102
                    ? Mathf.Max(10, AerV102TarpTruthInitialDelayFrames)
                    : Mathf.Max(30, AerV99TruthInitialDelayFrames);
                int intervalV102 = tarpEpochV102
                    ? Mathf.Max(20, AerV102TarpTruthIntervalFrames)
                    : Mathf.Max(30, AerV99TruthIntervalFrames);

                if (_v99NextTruthFrame < 0)
                    _v99NextTruthFrame = Time.frameCount + initialDelayV102;
                if (Time.frameCount < _v99NextTruthFrame || _v99TruthPending)
                    return false;
                if (_v139cJobActive || _v139cPendingEye >= 0 ||
                    _v99Readbacks[0].Pending || _v99Readbacks[1].Pending)
                    return false;

                LogEyeGeometryV99(self);
                if (!SnapshotEyeHistoryV99(self, 0) || !SnapshotEyeHistoryV99(self, 1))
                {
                    _v99NextTruthFrame = Time.frameCount + 30;
                    V99LogFailure("truth frame postponed because one/both same-eye histories were not snapshot-ready.");
                    return false;
                }

                _v99TruthPending = true;
                _v99TruthFrame = Time.frameCount;
                _v99SampleId++;
                _v99SamplesScheduled++;
                _v99NextTruthFrame = Time.frameCount + intervalV102;

                self.LeftCam.enabled = true;
                self.RightCam.enabled = true;
                self._aerScheduledEye = -1;
                self._aerScheduledPose = renderPose;
                self._aerScheduledPoseValid = true;
                self._aerScheduledFrame = Time.frameCount;
                // Consume the alternation slot this truth frame replaced.  Since BOTH eyes render
                // genuinely, the next ordinary frame should be the opposite of the eye that would
                // otherwise have rendered now; this avoids immediately rendering one eye twice.
                self._aerNextEye = self._aerNextEye == 0 ? 1 : 0;

                MelonLogger.Warning(
                    $"[StereoRender][AER V102][TruthSchedule] epoch={_v102TruthEpoch} " +
                    $"epochSample={_v99SamplesScheduled}/{epochSampleLimitV102} globalSample={_v99SampleId} " +
                    $"frame={Time.frameCount} farClip={_v102CurrentFarClipMeters:F1}m BOTH eyes will render genuine current truth. " +
                    $"historyL={_v99Snapshots[0].Frame} historyR={_v99Snapshots[1].Frame} " +
                    $"motionL={_v99Snapshots[0].MotionValid} motionR={_v99Snapshots[1].MotionValid} " +
                    $"flowL={_v99Snapshots[0].FlowValid} flowR={_v99Snapshots[1].FlowValid}. " +
                    "Diagnostic predictions are offline only; this truth frame submits real L+R.");
                return true;
            }

            private static void ConfigureForensicsCommonV99(
                ComputeShader cs,
                RenderTexture atlas,
                int columns,
                int rows,
                int presentationWidth,
                int presentationHeight)
            {
                cs.SetVector("_PresentationSize", new Vector4(
                    presentationWidth, presentationHeight,
                    1f / Mathf.Max(1, presentationWidth),
                    1f / Mathf.Max(1, presentationHeight)));
                cs.SetVector("_TileGrid", new Vector4(
                    columns, rows,
                    1f / Mathf.Max(1, columns),
                    1f / Mathf.Max(1, rows)));
                cs.SetTexture(_forensicsBlockMatchKernelV99, "_Atlas", atlas);
                cs.SetTexture(_forensicsTileStatsKernelV99, "_Atlas", atlas);
                cs.SetTexture(_forensicsWarpFieldKernelV99, "_Atlas", atlas);
                cs.SetTexture(_forensicsMotionFieldKernelV99, "_Atlas", atlas);
                cs.SetTexture(_forensicsOpticalFlowFieldKernelV99, "_Atlas", atlas);
                cs.SetTexture(_forensicsDepthFieldKernelV99, "_Atlas", atlas);
                cs.SetTexture(_forensicsCoverageFieldKernelV99, "_Atlas", atlas);
                cs.SetTexture(_forensicsSceneStructureKernelV99, "_Atlas", atlas);
                cs.SetTexture(_forensicsDepthClassStatsKernelV101, "_Atlas", atlas);
                cs.SetTexture(_forensicsSkyRayClassKernelV101, "_Atlas", atlas);
                cs.SetTexture(_forensicsOcclusionClassStatsKernelV102, "_Atlas", atlas);
                cs.SetTexture(_forensicsOcclusionComplexityKernelV102, "_Atlas", atlas);
                cs.SetTexture(_forensicsDepthColorRiskKernelV102, "_Atlas", atlas);
            }

            private static void DispatchCandidateMetricsV99(
                V99HistorySnapshot historyV102,
                EyeDepthLatch truthLatchV102,
                RenderTexture candidate,
                RenderTexture truth,
                RenderTexture truthDepth,
                Vector4 truthZParams,
                RenderTexture atlas,
                int candidateIndex,
                int columns,
                int rows)
            {
                ComputeShader cs = _forensicsComputeV99;
                int statsBand = V101FiniteStatsBase + candidateIndex;
                cs.SetInt("_OutBand", candidateIndex);
                cs.SetInt("_SearchRadius", Mathf.Clamp(AerV99BlockMatchSearchRadiusPixels, 1, 16));
                cs.SetInt("_PatchRadius", Mathf.Clamp(AerV99BlockMatchPatchRadiusPixels, 4, 32));
                cs.SetInt("_PatchStride", Mathf.Clamp(AerV99BlockMatchPatchStridePixels, 1, 8));
                cs.SetVector("_TruthDepthSize", new Vector4(
                    truthDepth.width, truthDepth.height,
                    1f / truthDepth.width, 1f / truthDepth.height));
                cs.SetVector("_ZBufferParams", truthZParams);
                cs.SetTexture(_forensicsBlockMatchKernelV99, "_Candidate", candidate);
                cs.SetTexture(_forensicsBlockMatchKernelV99, "_Truth", truth);
                cs.SetTexture(_forensicsBlockMatchKernelV99, "_TruthDepth", truthDepth);
                cs.Dispatch(_forensicsBlockMatchKernelV99, columns, rows, 1);

                // Legacy finite-depth stats remain useful for the angular matcher summary.
                cs.SetInt("_OutBand", statsBand);
                cs.SetTexture(_forensicsTileStatsKernelV99, "_Candidate", candidate);
                cs.SetTexture(_forensicsTileStatsKernelV99, "_Truth", truth);
                cs.SetTexture(_forensicsTileStatsKernelV99, "_TruthDepth", truthDepth);
                cs.Dispatch(_forensicsTileStatsKernelV99, columns, rows, 1);

                // V101: measure error by actual CURRENT truth-depth class, including the class
                // V99/V100 deliberately excluded: rawDepth==0 (sky/background/no opaque surface).
                cs.SetTexture(_forensicsDepthClassStatsKernelV101, "_Candidate", candidate);
                cs.SetTexture(_forensicsDepthClassStatsKernelV101, "_Truth", truth);
                cs.SetTexture(_forensicsDepthClassStatsKernelV101, "_TruthDepth", truthDepth);
                for (int dc = 0; dc < V101DepthClassCount; ++dc)
                {
                    int band = V101ClassStatsBase + candidateIndex * V101DepthClassCount + dc;
                    cs.SetInt("_DepthClassV101", dc);
                    cs.SetInt("_OutBand", band);
                    cs.Dispatch(_forensicsDepthClassStatsKernelV101, columns, rows, 1);
                }

                // V102: stratify the SAME candidate error by temporal visibility class.  This is
                // the direct test for layered foliage / silhouette disocclusion.
                cs.SetVector("_HistoryDepthSize", new Vector4(
                    historyV102.Depth.width, historyV102.Depth.height,
                    1f / historyV102.Depth.width, 1f / historyV102.Depth.height));
                cs.SetMatrix("_CurrentInvGpuProjection",
                    GL.GetGPUProjectionMatrix(truthLatchV102.SourceProjection, true).inverse);
                cs.SetMatrix("_CurrentCameraToWorld", truthLatchV102.SourceCameraToWorld);
                cs.SetMatrix("_PreviousWorldToCamera", historyV102.SourceWorldToCamera);
                cs.SetMatrix("_PreviousGpuProjection", historyV102.SourceInvGpuProjection.inverse);
                cs.SetTexture(_forensicsOcclusionClassStatsKernelV102, "_Candidate", candidate);
                cs.SetTexture(_forensicsOcclusionClassStatsKernelV102, "_Truth", truth);
                cs.SetTexture(_forensicsOcclusionClassStatsKernelV102, "_TruthDepth", truthDepth);
                cs.SetTexture(_forensicsOcclusionClassStatsKernelV102, "_HistoryDepth", historyV102.Depth);
                for (int oc = 0; oc < V102OcclusionClassCount; ++oc)
                {
                    int band = V102OcclusionStatsBase + candidateIndex * V102OcclusionClassCount + oc;
                    cs.SetInt("_OcclusionClassV102", oc);
                    cs.SetInt("_OutBand", band);
                    cs.Dispatch(_forensicsOcclusionClassStatsKernelV102, columns, rows, 1);
                }
            }

            private static bool RenderCandidateV99(
                V99HistorySnapshot h,
                EyeDepthLatch truthLatch,
                RenderTexture truthRT,
                bool presentationGrid,
                bool useMotion,
                bool useFlow,
                bool useRotationHoleFallbackV100,
                float translationScaleV101,
                bool useInvalidDepthRotationScatterV101,
                int fillPassCountV102,
                float maxWarpMetersV102,
                int guardVariantV139, // -1 = historical V102 candidate; >=0 = V103 guarded fill
                int coverageBand,
                RenderTexture atlas,
                int columns,
                int rows,
                out RenderTexture finalCandidate,
                out float flowScale)
            {
                finalCandidate = null;
                flowScale = 0f;
                if (h == null || !h.Valid || truthLatch == null || !truthLatch.Valid)
                    return false;

                int w = truthRT.width, hh = truthRT.height;
                _v99WarpRT = EnsureV99PresentationTarget(_v99WarpRT, w, hh, "SonsVR_V99_Warp", false);
                _v99FillRT = EnsureV99PresentationTarget(_v99FillRT, w, hh, "SonsVR_V99_Fill", false);
                _v99FinalRT = EnsureV99PresentationTarget(_v99FinalRT, w, hh, "SonsVR_V99_Final", false);

                bool motion = useMotion && h.MotionValid && h.Motion != null && h.Motion.IsCreated();
                bool flow = useFlow && h.FlowValid && h.Flow != null && h.Flow.IsCreated();
                if (h.CaptureTime >= 0f && h.PreviousRealCaptureTime >= 0f)
                {
                    float flowDt = h.CaptureTime - h.PreviousRealCaptureTime;
                    float predictDt = truthLatch.CaptureUnscaledTime - h.CaptureTime;
                    if (flowDt > 1e-5f && predictDt >= 0f)
                        flowScale = Mathf.Clamp(
                            predictDt / flowDt,
                            0f,
                            Mathf.Max(0.01f, AerTemporalOpticalFlowMaxTimeScaleV90));
                }

                RenderTexture flowBinding = flow ? h.Flow : EnsureDummyOpticalFlowTargetV90();

                // V101 translation sweep: retain the CURRENT target eye rotation/projection but
                // scale only the camera-position delta from stale source -> current truth.
                // scale=0 is rotation-only finite geometry; scale=1 is today's full 6DoF warp.
                Matrix4x4 candidateTargetCameraToWorldV101 = truthLatch.SourceCameraToWorld;
                Vector3 candidatePositionV101 = h.SourcePosition +
                    (truthLatch.SourcePosition - h.SourcePosition) * translationScaleV101;
                candidateTargetCameraToWorldV101.m03 = candidatePositionV101.x;
                candidateTargetCameraToWorldV101.m13 = candidatePositionV101.y;
                candidateTargetCameraToWorldV101.m23 = candidatePositionV101.z;
                Matrix4x4 candidateTargetWorldToCameraV101 = candidateTargetCameraToWorldV101.inverse;

                bool ok = TryRenderPerPixelScatterV90(
                    h.Color,
                    h.Depth,
                    motion ? h.Motion : null,
                    flowBinding,
                    _v99WarpRT,
                    h.ZParams,
                    h.SourceInvGpuProjection,
                    h.SourceCameraToWorld,
                    candidateTargetWorldToCameraV101,
                    GL.GetGPUProjectionMatrix(truthLatch.SourceProjection, true),
                    h.PreviousRealWorldToCamera,
                    h.PreviousRealGpuProjection,
                    motion,
                    motion ? flowScale * AerTemporalObjectMotionScaleV89 : 0f,
                    AerTemporalObjectMotionMaxPixelsV89,
                    flow,
                    flowScale,
                    AerTemporalOpticalFlowMaxResidualPixelsV90,
                    presentationGrid,
                    useInvalidDepthRotationScatterV101,
                    Mathf.Max(1.0f, maxWarpMetersV102),
                    true);
                if (!ok) return false;

                // Direct-scatter coverage before any spatial fill.
                if (_scatterSourceIndexRTV88 != null && coverageBand >= 0)
                {
                    ComputeShader cs = _forensicsComputeV99;
                    cs.SetInt("_OutBand", coverageBand);
                    cs.SetTexture(_forensicsCoverageFieldKernelV99, "_SourceIndex", _scatterSourceIndexRTV88);
                    cs.Dispatch(_forensicsCoverageFieldKernelV99, columns, rows, 1);
                }

                // V102 diagnostic fill factorial.  The live renderer is unchanged; only offline
                // truth candidates vary whether radius-1 / radius-2 spatial color fill is allowed
                // to bridge temporal holes.  This directly tests the user's "cutout depth slice"
                // observation at stacked foliage/rock silhouettes.
                int requestedFillPassesV102 = Mathf.Clamp(fillPassCountV102, 0, 2);
                RenderTexture preFallbackV102 = _v99WarpRT;
                bool guardedV139 = guardVariantV139 >= 0;
                int fillPass = _material.FindPass(guardedV139 ? "HoleFillDepthGuardV103" : "HoleFill");
                if (requestedFillPassesV102 > 0 && fillPass < 0) return false;
                if (guardedV139)
                {
                    if (_scatterDepthKeyRTV88 == null || !_scatterDepthKeyRTV88.IsCreated()) return false;
                    // V139: offscreen only.  The original direct warp and depth key remain
                    // unmodified; radius-2 cannot promote invented radius-1 evidence.
                    float absTol = new float[] { 0.30f, 0.15f, 0.60f, 0.20f, 0.30f }[guardVariantV139];
                    float relTol = new float[] { 0.06f, 0.03f, 0.10f, 0.06f, 0.06f }[guardVariantV139];
                    float maxTol = new float[] { 1.50f, 0.75f, 2.00f, 1.50f, 1.50f }[guardVariantV139];
                    float minSupport = new float[] { 0.60f, 0.75f, 0.50f, 0.60f, 0.80f }[guardVariantV139];
                    float minCount = new float[] { 2f, 3f, 2f, 2f, 3f }[guardVariantV139];
                    _material.SetTexture("_DirectWarpInputV103", _v99WarpRT);
                    _material.SetTexture("_ScatterDepthKeyV103", _scatterDepthKeyRTV88);
                    _material.SetFloat("_V103FillDepthAbsToleranceMeters", absTol);
                    _material.SetFloat("_V103FillDepthRelativeTolerance", relTol);
                    _material.SetFloat("_V105FillDepthMaxToleranceMeters", maxTol);
                    _material.SetFloat("_V103FillMinSupportFraction", minSupport);
                    _material.SetFloat("_V103FillMinSupportCount", minCount);
                }

                if (requestedFillPassesV102 >= 1)
                {
                    _material.SetTexture("_WarpInput", _v99WarpRT);
                    _material.SetFloat("_FillRadiusPixels", 1.0f);
                    _material.SetFloat("_FillConfidenceDecay", Mathf.Clamp01(AerDepthStereoWarpV49FillConfidenceDecay));
                    SetRenderTargetFullViewportV77(_v99FillRT);
                    GL.Clear(false, true, new Color(0f,0f,0f,0f));
                    if (!_material.SetPass(fillPass)) return false;
                    DrawFullscreenTopLeftUvQuad();
                    preFallbackV102 = _v99FillRT;
                }

                if (requestedFillPassesV102 >= 2)
                {
                    _material.SetTexture("_WarpInput", _v99FillRT);
                    _material.SetFloat("_FillRadiusPixels", 2.0f);
                    SetRenderTargetFullViewportV77(_v99FinalRT);
                    GL.Clear(false, true, new Color(0f,0f,0f,0f));
                    if (!_material.SetPass(fillPass)) return false;
                    DrawFullscreenTopLeftUvQuad();
                    preFallbackV102 = _v99FinalRT;
                }

                _material.SetTexture("_WarpInput", preFallbackV102);
                _material.SetTexture("_FallbackTargetColor", h.Color);

                // V100 offline truth candidate: compare the exact same depth reconstruction
                // with a rotation-only/infinite-depth gather for ONLY pixels that remain holes.
                Matrix4x4 sourceGpuProjectionV100 = h.SourceInvGpuProjection.inverse;
                Matrix4x4 targetWorldToCameraV100 = truthLatch.SourceWorldToCamera;
                Matrix4x4 targetGpuProjectionV100 = GL.GetGPUProjectionMatrix(truthLatch.SourceProjection, true);
                _material.SetMatrix("_V100TargetInvGpuProjection", targetGpuProjectionV100.inverse);
                _material.SetMatrix("_V100TargetCameraToWorld", targetWorldToCameraV100.inverse);
                _material.SetMatrix("_V100SourceWorldToCamera", h.SourceCameraToWorld.inverse);
                _material.SetMatrix("_V100SourceGpuProjection", sourceGpuProjectionV100);
                _material.SetFloat("_RotationHoleFallbackEnabledV100", useRotationHoleFallbackV100 ? 1f : 0f);

                // Avoid read/write aliasing: two fills end in _v99FinalRT, so recycle the
                // original scatter RT as the fallback destination in that case.
                RenderTexture fallbackOutputV102 = object.ReferenceEquals(preFallbackV102, _v99FinalRT)
                    ? _v99WarpRT
                    : _v99FinalRT;
                SetRenderTargetFullViewportV77(fallbackOutputV102);
                GL.Clear(false, true, new Color(0f,0f,0f,0f));
                int fallbackPass = _material.FindPass("TargetEyeHoleFallback");
                if (fallbackPass < 0 || !_material.SetPass(fallbackPass))
                {
                    _material.SetFloat("_RotationHoleFallbackEnabledV100", 0f);
                    return false;
                }
                DrawFullscreenTopLeftUvQuad();
                _material.SetFloat("_RotationHoleFallbackEnabledV100", 0f);
                finalCandidate = fallbackOutputV102;
                return true;
            }

            private static bool DispatchFieldMetricsV99(
                V99HistorySnapshot h,
                EyeDepthLatch truthLatch,
                RenderTexture atlas,
                int columns,
                int rows,
                int eye,
                RenderTexture truthColor,
                out double truthFlowApiMs,
                out ulong truthFlowExecCount)
            {
                truthFlowApiMs = -1.0;
                truthFlowExecCount = 0;
                bool truthFlowExactAvailable = false;
                ComputeShader cs = _forensicsComputeV99;
                RenderTexture truthDepth = truthLatch.DepthRT;

                cs.SetVector("_ZBufferParams", truthLatch.ZParams);
                cs.SetVector("_HistoryDepthSize", new Vector4(
                    h.Depth.width,h.Depth.height,1f/h.Depth.width,1f/h.Depth.height));
                cs.SetVector("_TruthDepthSize", new Vector4(
                    truthDepth.width,truthDepth.height,1f/truthDepth.width,1f/truthDepth.height));
                cs.SetMatrix("_HistoryInvGpuProjection", h.SourceInvGpuProjection);
                cs.SetMatrix("_HistoryCameraToWorld", h.SourceCameraToWorld);
                cs.SetMatrix("_TargetWorldToCamera", truthLatch.SourceWorldToCamera);
                cs.SetMatrix("_TargetGpuProjection", GL.GetGPUProjectionMatrix(truthLatch.SourceProjection, true));
                cs.SetTexture(_forensicsWarpFieldKernelV99, "_HistoryDepth", h.Depth);
                cs.SetInt("_OutBand", V101BandGeometric);
                cs.Dispatch(_forensicsWarpFieldKernelV99, columns, rows, 1);

                if (truthLatch.UsedCustomPassMotionV89 && truthLatch.MotionRTV89 != null &&
                    truthLatch.MotionRTV89.IsCreated() && truthLatch.PreviousRealCameraValidV89)
                {
                    cs.SetVector("_MotionSize", new Vector4(
                        truthLatch.MotionRTV89.width,truthLatch.MotionRTV89.height,
                        1f/truthLatch.MotionRTV89.width,1f/truthLatch.MotionRTV89.height));
                    cs.SetMatrix("_CurrentInvGpuProjection", truthLatch.SourceInvGpuProjection);
                    cs.SetMatrix("_CurrentCameraToWorld", truthLatch.SourceCameraToWorld);
                    cs.SetMatrix("_PreviousWorldToCamera", truthLatch.PreviousRealWorldToCameraV89);
                    cs.SetMatrix("_PreviousGpuProjection", truthLatch.PreviousRealGpuProjectionV89);
                    cs.SetTexture(_forensicsMotionFieldKernelV99, "_TruthDepth", truthDepth);
                    cs.SetTexture(_forensicsMotionFieldKernelV99, "_Motion", truthLatch.MotionRTV89);
                    cs.SetInt("_OutBand", V101BandHdrpTotal);
                    cs.SetInt("_OutBand2", V101BandHdrpResidual);
                    cs.Dispatch(_forensicsMotionFieldKernelV99, columns, rows, 1);
                }

                if (h.FlowValid && h.Flow != null && h.Flow.IsCreated() && h.PreviousRealFrame >= 0)
                {
                    cs.SetVector("_FlowSize", new Vector4(
                        h.Flow.width,h.Flow.height,1f/h.Flow.width,1f/h.Flow.height));
                    cs.SetMatrix("_PreviousWorldToCamera", h.PreviousRealWorldToCamera);
                    cs.SetMatrix("_PreviousGpuProjection", h.PreviousRealGpuProjection);
                    cs.SetTexture(_forensicsOpticalFlowFieldKernelV99, "_HistoryDepth", h.Depth);
                    cs.SetTexture(_forensicsOpticalFlowFieldKernelV99, "_OpticalFlow", h.Flow);
                    cs.SetInt("_OutBand", V101BandNvofaPredictiveTotal);
                    cs.SetInt("_OutBand2", V101BandNvofaPredictiveResidual);
                    cs.Dispatch(_forensicsOpticalFlowFieldKernelV99, columns, rows, 1);
                }

                // V99 exact-interval NVOFA truth field, if the plugin event from THIS genuine
                // current eye has completed by EOF.  Unlike bands 15/16 (the older flow that the
                // predictor actually had available), this flow spans current-truth -> stale-history
                // over the exact interval whose synthetic error we are measuring.
                RenderTexture truthFlowV99;
                if (NvOpticalFlowV90.TryGetFlowForHistory(
                        eye, truthLatch.Frame, out truthFlowV99, out truthFlowApiMs, out truthFlowExecCount) &&
                    truthFlowV99 != null && truthFlowV99.IsCreated())
                {
                    truthFlowExactAvailable = true;
                    cs.SetVector("_FlowSize", new Vector4(
                        truthFlowV99.width,truthFlowV99.height,1f/truthFlowV99.width,1f/truthFlowV99.height));
                    cs.SetVector("_HistoryDepthSize", new Vector4(
                        truthDepth.width,truthDepth.height,1f/truthDepth.width,1f/truthDepth.height));
                    cs.SetMatrix("_HistoryInvGpuProjection", truthLatch.SourceInvGpuProjection);
                    cs.SetMatrix("_HistoryCameraToWorld", truthLatch.SourceCameraToWorld);
                    cs.SetMatrix("_PreviousWorldToCamera", h.SourceWorldToCamera);
                    cs.SetMatrix("_PreviousGpuProjection", GL.GetGPUProjectionMatrix(h.SourceProjection, true));
                    cs.SetTexture(_forensicsOpticalFlowFieldKernelV99, "_HistoryDepth", truthDepth);
                    cs.SetTexture(_forensicsOpticalFlowFieldKernelV99, "_OpticalFlow", truthFlowV99);
                    cs.SetInt("_OutBand", V101BandExactNvofa);
                    cs.SetInt("_OutBand2", V101BandExactNvofaResidual);
                    cs.Dispatch(_forensicsOpticalFlowFieldKernelV99, columns, rows, 1);
                }

                cs.SetVector("_HistoryDepthSize", new Vector4(
                    h.Depth.width,h.Depth.height,1f/h.Depth.width,1f/h.Depth.height));
                cs.SetTexture(_forensicsDepthFieldKernelV99, "_HistoryDepth", h.Depth);
                cs.SetVector("_ZBufferParams", h.ZParams);
                cs.SetInt("_OutBand", V101BandHistoryDepth);
                cs.Dispatch(_forensicsDepthFieldKernelV99, columns, rows, 1);

                cs.SetVector("_HistoryDepthSize", new Vector4(
                    truthDepth.width,truthDepth.height,1f/truthDepth.width,1f/truthDepth.height));
                cs.SetTexture(_forensicsDepthFieldKernelV99, "_HistoryDepth", truthDepth);
                cs.SetVector("_ZBufferParams", truthLatch.ZParams);
                cs.SetInt("_OutBand", V101BandTruthDepth);
                cs.Dispatch(_forensicsDepthFieldKernelV99, columns, rows, 1);

                // Scene-content context for interpreting registration confidence: flat sky should
                // not be treated like a failed match, and high-frequency/depth-discontinuous foliage
                // is exactly where the historical artifact is perceptually strongest.
                cs.SetVector("_TruthDepthSize", new Vector4(
                    truthDepth.width,truthDepth.height,1f/truthDepth.width,1f/truthDepth.height));
                cs.SetVector("_ZBufferParams", truthLatch.ZParams);
                cs.SetTexture(_forensicsSceneStructureKernelV99, "_Truth", truthColor);
                cs.SetTexture(_forensicsSceneStructureKernelV99, "_TruthDepth", truthDepth);
                cs.SetInt("_OutBand", V101BandSceneStructure);
                cs.Dispatch(_forensicsSceneStructureKernelV99, columns, rows, 1);

                // V101 GPU ray census: this is the actual rendered-depth equivalent of firing
                // "Star Wars lasers" through the HMD field.  It asks whether the current ray
                // and its rotation-mapped stale counterpart own finite opaque depth.
                cs.SetVector("_HistoryDepthSize", new Vector4(
                    h.Depth.width,h.Depth.height,1f/h.Depth.width,1f/h.Depth.height));
                cs.SetVector("_TruthDepthSize", new Vector4(
                    truthDepth.width,truthDepth.height,1f/truthDepth.width,1f/truthDepth.height));
                cs.SetMatrix("_CurrentInvGpuProjection", GL.GetGPUProjectionMatrix(truthLatch.SourceProjection, true).inverse);
                cs.SetMatrix("_CurrentCameraToWorld", truthLatch.SourceCameraToWorld);
                cs.SetMatrix("_PreviousWorldToCamera", h.SourceWorldToCamera);
                cs.SetMatrix("_PreviousGpuProjection", h.SourceInvGpuProjection.inverse);
                cs.SetTexture(_forensicsSkyRayClassKernelV101, "_TruthDepth", truthDepth);
                cs.SetTexture(_forensicsSkyRayClassKernelV101, "_HistoryDepth", h.Depth);
                cs.SetInt("_OutBand", V101BandSkyRayClass);
                cs.Dispatch(_forensicsSkyRayClassKernelV101, columns, rows, 1);

                // V102 raster-level proxies for stacked alpha cards / occlusion boundaries and
                // the post-DLSS-color vs half-linear-depth association hazard.
                cs.SetTexture(_forensicsOcclusionComplexityKernelV102, "_TruthDepth", truthDepth);
                cs.SetInt("_OutBand", V102BandOcclusionComplexity);
                cs.Dispatch(_forensicsOcclusionComplexityKernelV102, columns, rows, 1);

                cs.SetTexture(_forensicsDepthColorRiskKernelV102, "_Truth", truthColor);
                cs.SetTexture(_forensicsDepthColorRiskKernelV102, "_TruthDepth", truthDepth);
                cs.SetInt("_OutBand", V102BandDepthColorRisk);
                cs.Dispatch(_forensicsDepthColorRiskKernelV102, columns, rows, 1);
                return truthFlowExactAvailable;
            }

            private static void RunEyeTruthDiagnosticsV99(StereoRender self, int eye, int sampleId)
            {
                V99HistorySnapshot h = _v99Snapshots[eye];
                EyeDepthLatch truthLatch = eye == 0 ? _leftDepthLatch : _rightDepthLatch;
                RenderTexture truthRT = eye == 0 ? self.LeftRT : self.RightRT;
                if (!h.Valid || truthLatch == null || !truthLatch.Valid || truthLatch.Frame != Time.frameCount ||
                    truthRT == null || !truthRT.IsCreated() || truthLatch.DepthRT == null || !truthLatch.DepthRT.IsCreated())
                {
                    V99LogFailure("truth diagnostics missing exact current latch for eye=" + (eye==0?"L":"R"));
                    return;
                }

                int cols = Mathf.Clamp(AerV99TileColumns, 8, 32);
                int rows = Mathf.Clamp(AerV99TileRows, 6, 24);
                RenderTexture atlas = EnsureV99Atlas(eye, cols, rows);
                if (atlas == null || !atlas.IsCreated()) return;
                RenderTexture previous = RenderTexture.active;
                SetRenderTargetFullViewportV77(atlas);
                GL.Clear(false, true, Color.clear);
                RestoreRenderTargetViewportV77(previous);

                ConfigureForensicsCommonV99(_forensicsComputeV99, atlas, cols, rows, truthRT.width, truthRT.height);
                _forensicsComputeV99.SetTexture(_forensicsBlockMatchKernelV99, "_Truth", truthRT);
                _forensicsComputeV99.SetTexture(_forensicsTileStatsKernelV99, "_Truth", truthRT);

                Camera truthCameraV102 = eye == 0 ? self.LeftCam : self.RightCam;
                float diagnosticMaxWarpV102 = ResolveMaxWarpMetersV87(truthCameraV102, truthCameraV102);

                // Candidate 0: raw old same-eye image, no geometric correction.
                DispatchCandidateMetricsV99(
                    h, truthLatch,
                    h.Color, truthRT, truthLatch.DepthRT, truthLatch.ZParams,
                    atlas, 0, cols, rows);

                // V102 candidates 1..6: a clean 2x3 factorial.  Full 6DoF finite geometry and
                // V100 rotation-only final-hole fallback are fixed.  Only source lattice and
                // spatial fill count change.  This is the direct test for post-DLSS color/depth
                // misassociation versus cross-layer hole filling at alpha/occlusion silhouettes.
                RenderTexture candidate;
                float diagnosticFlowScaleV102 = 0f;
                for (int ci = 1; ci <= 6; ++ci)
                {
                    int spec = ci - 1;
                    int coverageBand = V101CoverageBase + spec;
                    float candidateFlowScale;
                    if (RenderCandidateV99(
                            h, truthLatch, truthRT,
                            V102PresentationGrid[spec],
                            false,  // HDRP object motion stays telemetry-only
                            false,  // NVOFA stays telemetry-only; pre-OF jitter remains exposed
                            true,   // V100 rotation-only final TRUE-hole fallback
                            1.0f,   // V101 established full translation scale as the finite baseline
                            false,  // V101 sky pre-scatter retired: coverage up, truth error not down
                            V102FillPasses[spec],
                            diagnosticMaxWarpV102,
                            -1, // original unguarded forensic candidate; no live renderer changes
                            coverageBand,
                            atlas, cols, rows,
                            out candidate, out candidateFlowScale))
                    {
                        DispatchCandidateMetricsV99(
                            h, truthLatch,
                            candidate, truthRT, truthLatch.DepthRT, truthLatch.ZParams,
                            atlas, ci, cols, rows);
                        if (ci == 1)
                            diagnosticFlowScaleV102 = candidateFlowScale;
                    }
                }

                // V139: try five V103 *guarded* depth-fill configurations OFFSCREEN,
                // using the same captured history and genuine current 2E reference.
                // Never promote a candidate to the live AER renderer in this experiment.
                if (AerCalibrationV139.ActiveCapture)
                {
                    for (int variant = 0; variant < 5; ++variant)
                    {
                        int ci = 7 + variant;
                        int coverageBand = V101CoverageBase + (ci - 1);
                        RenderTexture guardedCandidate;
                        float guardedFlowScale;
                        if (RenderCandidateV99(
                                h, truthLatch, truthRT,
                                false, // live V103 uses native depth-grid scatter
                                false, false, true, 1.0f, false,
                                2, diagnosticMaxWarpV102, variant, coverageBand,
                                atlas, cols, rows,
                                out guardedCandidate, out guardedFlowScale))
                        {
                            DispatchCandidateMetricsV99(h, truthLatch,
                                guardedCandidate, truthRT, truthLatch.DepthRT, truthLatch.ZParams,
                                atlas, ci, cols, rows);
                        }
                        else
                        {
                            MelonLogger.Warning("[V139 CAL] guarded candidate unavailable variant=" +
                                variant + " eye=" + (eye == 0 ? "L" : "R"));
                        }
                    }
                }

                double truthFlowApiMsV99;
                ulong truthFlowExecCountV99;
                bool truthFlowExactV99 = DispatchFieldMetricsV99(
                    h, truthLatch, atlas, cols, rows, eye, truthRT,
                    out truthFlowApiMsV99, out truthFlowExecCountV99);

                V99PendingReadback p = _v99Readbacks[eye];
                p.SampleId = sampleId;
                p.Eye = eye;
                p.TruthFrame = Time.frameCount;
                p.HistoryFrame = h.Frame;
                p.PresentationWidth = truthRT.width;
                p.PresentationHeight = truthRT.height;
                p.Columns = cols;
                p.Rows = rows;
                p.TargetProjection = truthLatch.SourceProjection;
                p.HistoryAgeMs = h.CaptureTime >= 0f ? Mathf.Max(0f,(truthLatch.CaptureUnscaledTime-h.CaptureTime)*1000f) : -1f;
                p.OpticalFlowScale = diagnosticFlowScaleV102;
                p.MotionStatus = h.MotionValid ? "historyMotion=yes" : "historyMotion=no";
                p.FlowStatus = h.FlowValid ? "historyFlow=yes" : "historyFlow=no";
                p.TruthFlowStatus = truthFlowExactV99
                    ? $"truthExactFlow=yes(apiMs={truthFlowApiMsV99:F3},exec={truthFlowExecCountV99})"
                    : "truthExactFlow=no/pending";
                p.EpochV102 = _v102TruthEpoch;
                p.EpochSampleV102 = _v99SamplesScheduled;
                p.FarClipMetersV102 = truthCameraV102 != null ? truthCameraV102.farClipPlane : _v102CurrentFarClipMeters;
                p.CameraNameV102 = truthLatch.CameraName;
                try
                {
                    p.Request = AsyncGPUReadback.Request(
                        atlas, 0,
                        0, atlas.width,
                        0, atlas.height,
                        0, 1,
                        TextureFormat.RGBAFloat,
                        null);
                    p.Pending = true;
                }
                catch (Exception e)
                {
                    p.Pending = false;
                    V99LogFailure("AsyncGPUReadback request failed eye=" + (eye==0?"L":"R") + ": " + e.Message);
                }

                Matrix4x4 nj = truthLatch.CameraNonJitteredProjectionAtDepthV94;
                Matrix4x4 pj = truthLatch.SourceProjection;
                Vector3 poseDeltaWorldV99 = truthLatch.SourcePosition - h.SourcePosition;
                Vector3 poseDeltaLocalV99 = Quaternion.Inverse(truthLatch.SourceRotation) * poseDeltaWorldV99;
                Quaternion poseRotDeltaV99 = Quaternion.Inverse(h.SourceRotation) * truthLatch.SourceRotation;
                Vector3 poseEulerV99 = poseRotDeltaV99.eulerAngles;
                poseEulerV99.x = Mathf.DeltaAngle(0f, poseEulerV99.x);
                poseEulerV99.y = Mathf.DeltaAngle(0f, poseEulerV99.y);
                poseEulerV99.z = Mathf.DeltaAngle(0f, poseEulerV99.z);
                MelonLogger.Warning(
                    $"[StereoRender][AER V102][PoseInterval] epoch={_v102TruthEpoch} epochSample={_v99SamplesScheduled} sample={sampleId} eye={(eye==0?"L":"R")} " +
                    $"frames={truthLatch.Frame-h.Frame} age={p.HistoryAgeMs:F2}ms " +
                    $"translationWorldMm=({poseDeltaWorldV99.x*1000f:F3},{poseDeltaWorldV99.y*1000f:F3},{poseDeltaWorldV99.z*1000f:F3}) " +
                    $"translationTruthLocalMm=({poseDeltaLocalV99.x*1000f:F3},{poseDeltaLocalV99.y*1000f:F3},{poseDeltaLocalV99.z*1000f:F3}) " +
                    $"rotationEulerDeg=({poseEulerV99.x:F5},{poseEulerV99.y:F5},{poseEulerV99.z:F5}) " +
                    $"rotationAngle={Quaternion.Angle(h.SourceRotation,truthLatch.SourceRotation):F5}deg.");
                MelonLogger.Warning(
                    $"[StereoRender][AER V102][TruthEye] epoch={_v102TruthEpoch} epochSample={_v99SamplesScheduled} sample={sampleId} eye={(eye==0?"L":"R")} " +
                    $"truthFrame={truthLatch.Frame} historyFrame={h.Frame} age={p.HistoryAgeMs:F2}ms " +
                    $"presentation={truthRT.width}x{truthRT.height} truthDepth={truthLatch.DepthRT.width}x{truthLatch.DepthRT.height} " +
                    $"historyDepth={h.Depth.width}x{h.Depth.height} historyMotion={h.MotionValid} historyFlow={h.FlowValid} " +
                    $"flowScale={p.OpticalFlowScale:F4}; " +
                    $"proj(m00/m11/m02/m12)=({pj.m00:F7},{pj.m11:F7},{pj.m02:F7},{pj.m12:F7}) " +
                    $"nonJitteredDelta02/12=({(pj.m02-nj.m02):F8},{(pj.m12-nj.m12):F8}).");
                try
                {
                    Camera tc = eye == 0 ? self.LeftCam : self.RightCam;
                    Vector4 rtScale = Shader.GetGlobalVector("_RTHandleScale");
                    Vector4 rtHist = Shader.GetGlobalVector("_RTHandleScaleHistory");
                    Vector4 screen = Shader.GetGlobalVector("_ScreenSize");
                    Vector4 post = Shader.GetGlobalVector("_PostProcessScreenSize");
                    Vector4 dyn = Shader.GetGlobalVector("_DynamicResolutionScreenSize");
                    Vector4 bias = Shader.GetGlobalVector("_ScreenCoordScaleBias");
                    Vector4 taa = Shader.GetGlobalVector("_TaaFrameInfo");
                    Vector4 taaJ = Shader.GetGlobalVector("_TaaJitter");
                    Vector4 mip = Shader.GetGlobalVector("_GlobalMipBias");
                    MelonLogger.Msg(
                        $"[StereoRender][AER V102][RasterState] epoch={_v102TruthEpoch} epochSample={_v99SamplesScheduled} sample={sampleId} eye={(eye==0?"L":"R")} " +
                        $"cameraPixel={tc.pixelWidth}x{tc.pixelHeight} scaled={tc.scaledPixelWidth}x{tc.scaledPixelHeight} " +
                        $"ScalableBuffer=({ScalableBufferManager.widthScaleFactor:F6},{ScalableBufferManager.heightScaleFactor:F6}) " +
                        $"RTScale=({rtScale.x:F6},{rtScale.y:F6},{rtScale.z:F6},{rtScale.w:F6}) " +
                        $"RTHistory=({rtHist.x:F6},{rtHist.y:F6},{rtHist.z:F6},{rtHist.w:F6}) " +
                        $"Screen=({screen.x:F2},{screen.y:F2},{screen.z:F7},{screen.w:F7}) " +
                        $"Post=({post.x:F2},{post.y:F2},{post.z:F7},{post.w:F7}) " +
                        $"DRS=({dyn.x:F2},{dyn.y:F2},{dyn.z:F7},{dyn.w:F7}) " +
                        $"ScaleBias=({bias.x:F6},{bias.y:F6},{bias.z:F6},{bias.w:F6}) " +
                        $"TaaFrame=({taa.x:F6},{taa.y:F6},{taa.z:F6},{taa.w:F6}) " +
                        $"TaaJitter=({taaJ.x:F7},{taaJ.y:F7},{taaJ.z:F7},{taaJ.w:F7}) " +
                        $"GlobalMipBias=({mip.x:F6},{mip.y:F6},{mip.z:F6},{mip.w:F6}).");
                }
                catch (Exception e)
                {
                    V99LogFailure("raster-state telemetry failed: " + e.Message);
                }
            }

            // V139C: freeze both truth eyes and their metadata NOW. They will be
            // overwritten by the normal alternating-eye renderer in subsequent frames.
            // We do not alter any actual displayed texture or live shader parameters.
            private static bool BeginStagedTruthV139C(StereoRender self, int sample)
            {
                try
                {
                    for (int eye = 0; eye < 2; ++eye)
                    {
                        V139CStagedEye j = _v139cStaged[eye];
                        EyeDepthLatch src = eye == 0 ? _leftDepthLatch : _rightDepthLatch;
                        RenderTexture color = eye == 0 ? self.LeftRT : self.RightRT;
                        if (src == null || !src.Valid || src.Frame != Time.frameCount ||
                            src.DepthRT == null || color == null) throw new InvalidOperationException(
                                "truth latch or color missing on current 2E frame eye=" + eye);
                        if (!CopyV99Texture(color, ref j.TruthColor, "V139C_TruthColor" + eye, true) ||
                            !CopyV99Texture(src.DepthRT, ref j.TruthDepth, "V139C_TruthDepth" + eye, false))
                            throw new InvalidOperationException("truth image snapshot failed eye=" + eye);
                        j.TruthLatch = src.SnapshotMetadataV139C();
                        j.TruthLatch.DepthRT = j.TruthDepth;
                        // Freeze optional motion too: never read a later frame's motion as truth.
                        j.TruthLatch.UsedCustomPassMotionV89 = false;
                        if (src.UsedCustomPassMotionV89 && src.MotionRTV89 != null &&
                            CopyV99Texture(src.MotionRTV89, ref j.TruthMotion,
                                "V139C_TruthMotion" + eye, false))
                        {
                            j.TruthLatch.MotionRTV89 = j.TruthMotion;
                            j.TruthLatch.UsedCustomPassMotionV89 = true;
                        }
                        j.Columns = Mathf.Clamp(AerV99TileColumns, 8, 32);
                        j.Rows = Mathf.Clamp(AerV99TileRows, 6, 24);
                        j.Atlas = EnsureV99Atlas(eye, j.Columns, j.Rows);
                        if (j.Atlas == null || !j.Atlas.IsCreated())
                            throw new InvalidOperationException("no atlas for eye=" + eye);
                        RenderTexture previous = RenderTexture.active;
                        SetRenderTargetFullViewportV77(j.Atlas);
                        GL.Clear(false, true, Color.clear);
                        RestoreRenderTargetViewportV77(previous);
                        j.Sample = sample;
                        j.Eye = eye;
                        j.Candidate = 0;
                        j.FieldsDone = false;
                        j.FlowScale = 0f;
                        Camera truthCamera = eye == 0 ? self.LeftCam : self.RightCam;
                        j.MaxWarpMeters = ResolveMaxWarpMetersV87(truthCamera, truthCamera);
                        j.Ready = true;
                        AerCalibrationV139.TraceGpuV139C("SNAPSHOT sample=" + sample +
                            " eye=" + eye + " truthFrame=" + j.TruthLatch.Frame +
                            " color=" + color.width + "x" + color.height +
                            " depth=" + j.TruthDepth.width + "x" + j.TruthDepth.height);
                    }
                    _v139cPendingEye = -1;
                    _v139cJobActive = true;
                    _v139cStagesSubmitted = 0;
                    _v139cStagesConfirmed = 0;
                    _v139cLastHeartBeat = Time.unscaledTime;
                    return true;
                }
                catch (Exception ex)
                {
                    AerCalibrationV139.FailGpuV139C("SNAPSHOT_FAILED " + ex.GetType().Name + " " + ex.Message);
                    _v139cJobActive = false;
                    return false;
                }
            }

            private static void RequestStagedFenceV139C(V139CStagedEye j, string stage)
            {
                // Read a small atlas rectangle. Its GPU completion certifies ALL earlier
                // dispatches on this ordered Unity graphics queue have completed.
                _v139cFence = AsyncGPUReadback.Request(
                    j.Atlas, 0, 0, j.Columns, 0, j.Rows, 0, 1, TextureFormat.RGBAFloat, null);
                _v139cPendingEye = j.Eye;
                _v139cFenceStarted = Time.unscaledTime;
                _v139cStagesSubmitted++;
                AerCalibrationV139.TraceGpuV139C("GPU_SUBMITTED sample=" + j.Sample +
                    " eye=" + j.Eye + " stage=" + stage + " frame=" + Time.frameCount +
                    " submitted=" + _v139cStagesSubmitted);
            }

            // Called AFTER compositor submission, never inside begin-render/eye rendering.
            // One candidate, one eye, and one readback-fenced workload at a time.
            internal static void TickStagedTruthV139C()
            {
                if (!_v139cJobActive || !AerCalibrationV139.ActiveCapture) return;
                if (_v139cPendingEye >= 0)
                {
                    if (_v139cFence.done)
                    {
                        int finishedEye = _v139cPendingEye;
                        _v139cPendingEye = -1;
                        if (_v139cFence.hasError)
                        {
                            _v139cJobActive = false;
                            AerCalibrationV139.FailGpuV139C("GPU_FENCE_ERROR eye=" + finishedEye);
                            return;
                        }
                        _v139cStagesConfirmed++;
                        AerCalibrationV139.TraceGpuV139C("GPU_CONFIRMED eye=" + finishedEye +
                            " latencyMs=" + ((Time.unscaledTime - _v139cFenceStarted) * 1000f).ToString("F1") +
                            " confirmed=" + _v139cStagesConfirmed);
                    }
                    else
                    {
                        if (Time.unscaledTime - _v139cLastHeartBeat > 3f)
                        {
                            _v139cLastHeartBeat = Time.unscaledTime;
                            AerCalibrationV139.TraceGpuV139C("GPU_WAIT eye=" + _v139cPendingEye +
                                " ageMs=" + ((Time.unscaledTime - _v139cFenceStarted) * 1000f).ToString("F1") +
                                " managedMemoryMB=" + (GC.GetTotalMemory(false) / (1024 * 1024)));
                        }
                        if (Time.unscaledTime - _v139cFenceStarted > 12f)
                        {
                            _v139cJobActive = false;
                            AerCalibrationV139.FailGpuV139C("GPU_FENCE_TIMEOUT after 12s eye=" + _v139cPendingEye);
                        }
                        return;
                    }
                }
                // Never dispatch multiple calibration stages in one Unity frame.
                if (_v139cLastScheduledFrame == Time.frameCount) return;
                V139CStagedEye j = null;
                for (int eye = 0; eye < 2; ++eye)
                    if (_v139cStaged[eye].Ready &&
                        (_v139cStaged[eye].Candidate < V99CandidateCount || !_v139cStaged[eye].FieldsDone))
                    { j = _v139cStaged[eye]; break; }
                if (j == null)
                {
                    _v139cJobActive = false;
                    AerCalibrationV139.TraceGpuV139C("STAGES_DONE sample=" + _v139cStaged[0].Sample +
                        " submitted=" + _v139cStagesSubmitted +
                        " confirmed=" + _v139cStagesConfirmed);
                    for (int eye = 0; eye < 2; ++eye)
                    {
                        V139CStagedEye done = _v139cStaged[eye];
                        V99PendingReadback p = _v99Readbacks[eye];
                        V99HistorySnapshot h = _v99Snapshots[eye];
                        p.SampleId = done.Sample;
                        p.Eye = eye;
                        p.TruthFrame = done.TruthLatch.Frame;
                        p.HistoryFrame = h.Frame;
                        p.PresentationWidth = done.TruthColor.width;
                        p.PresentationHeight = done.TruthColor.height;
                        p.Columns = done.Columns;
                        p.Rows = done.Rows;
                        p.TargetProjection = done.TruthLatch.SourceProjection;
                        p.HistoryAgeMs = h.CaptureTime >= 0f ?
                            Mathf.Max(0f, (done.TruthLatch.CaptureUnscaledTime-h.CaptureTime)*1000f) : -1f;
                        p.OpticalFlowScale = done.FlowScale;
                        p.MotionStatus = h.MotionValid ? "historyMotion=yes" : "historyMotion=no";
                        p.FlowStatus = h.FlowValid ? "historyFlow=yes" : "historyFlow=no";
                        p.TruthFlowStatus = "truthExactFlow=no/staged";
                        p.EpochV102 = _v102TruthEpoch;
                        p.EpochSampleV102 = _v99SamplesScheduled;
                        p.FarClipMetersV102 = _v102CurrentFarClipMeters;
                        p.CameraNameV102 = done.TruthLatch.CameraName;
                        try
                        {
                            p.Request = AsyncGPUReadback.Request(done.Atlas, 0, 0, done.Atlas.width,
                                0, done.Atlas.height, 0, 1, TextureFormat.RGBAFloat, null);
                            p.Pending = true;
                            AerCalibrationV139.TraceGpuV139C("ATLAS_READBACK_REQUEST sample=" + done.Sample +
                                " eye=" + eye + " bytes=" + (done.Atlas.width*done.Atlas.height*16));
                        }
                        catch(Exception ex)
                        {
                            AerCalibrationV139.FailGpuV139C("ATLAS_READBACK_FAILED " + ex.Message);
                            return;
                        }
                    }
                    return;
                }
                _v139cLastScheduledFrame = Time.frameCount;
                try
                {
                    V99HistorySnapshot h = _v99Snapshots[j.Eye];
                    EyeDepthLatch truthLatch = j.TruthLatch;
                    RenderTexture truthRT = j.TruthColor;
                    ConfigureForensicsCommonV99(_forensicsComputeV99, j.Atlas,
                        j.Columns,j.Rows,truthRT.width,truthRT.height);
                    string stage;
                    if (j.Candidate < V99CandidateCount)
                    {
                        int ci = j.Candidate;
                        stage = "candidate=" + ci + "/" + V99CandidateNames[ci];
                        AerCalibrationV139.TraceGpuV139C("STAGE_BEGIN sample=" + j.Sample+
                            " eye="+j.Eye+" "+stage+" frame="+Time.frameCount);
                        RenderTexture candidate = h.Color;
                        bool success = true;
                        float flow = 0f;
                        if (ci >= 1 && ci <= 6)
                        {
                            int spec = ci-1;
                            success = RenderCandidateV99(h,truthLatch,truthRT,
                                V102PresentationGrid[spec],false,false,true,1.0f,false,
                                V102FillPasses[spec],j.MaxWarpMeters,-1,
                                V101CoverageBase+spec,j.Atlas,j.Columns,j.Rows,
                                out candidate,out flow);
                        }
                        else if (ci >= 7)
                        {
                            success = RenderCandidateV99(h,truthLatch,truthRT,
                                false,false,false,true,1.0f,false,2,
                                j.MaxWarpMeters,ci-7,
                                V101CoverageBase+(ci-1),j.Atlas,j.Columns,j.Rows,
                                out candidate,out flow);
                        }
                        if (!success || candidate == null)
                            throw new InvalidOperationException("candidate render failed " + stage);
                        DispatchCandidateMetricsV99(h,truthLatch,candidate,truthRT,
                            truthLatch.DepthRT,truthLatch.ZParams,j.Atlas,ci,j.Columns,j.Rows);
                        if (ci == 1) j.FlowScale = flow;
                        j.Candidate++;
                    }
                    else
                    {
                        stage = "field_metrics";
                        AerCalibrationV139.TraceGpuV139C("STAGE_BEGIN sample=" + j.Sample+
                            " eye="+j.Eye+" "+stage+" frame="+Time.frameCount);
                        double api;
                        ulong count;
                        DispatchFieldMetricsV99(h,truthLatch,j.Atlas,j.Columns,j.Rows,j.Eye,
                            truthRT,out api,out count);
                        j.FieldsDone = true;
                    }
                    AerCalibrationV139.SetWorkerStatusV139C("sample " + j.Sample +
                        " eye " + (j.Eye==0?"L":"R") + " " + stage);
                    RequestStagedFenceV139C(j,stage);
                }
                catch(Exception ex)
                {
                    _v139cJobActive = false;
                    AerCalibrationV139.FailGpuV139C("STAGE_EXCEPTION "+ex.GetType().Name+" "+ex.Message);
                }
            }

            internal static bool RunTruthDiagnosticsIfNeededV99(StereoRender self)
            {
                if (!AerV99ForensicsEnabled || !_v99TruthPending || Time.frameCount != _v99TruthFrame)
                    return false;
                _v99TruthPending = false;
                if (_forensicsComputeV99 == null || !EnsureLoaded())
                {
                    V99LogFailure("truth frame occurred but forensic compute/bundle unavailable.");
                    return true;
                }

                int sample = _v99SampleId;
                MelonLogger.Warning(
                    $"[StereoRender][AER V102][TruthRun] epoch={_v102TruthEpoch} epochSample={_v99SamplesScheduled} " +
                    $"sample={sample} frame={Time.frameCount} both current eyes are real. " +
                    "Building offline stale->current occlusion/depth-association candidates now.");
                try
                {
                    if (AerCalibrationV139.ActiveCapture)
                    {
                        // V139C: only freeze the reference here; no candidate dispatches.
                        // Normal AER submission proceeds immediately, next frames do work.
                        BeginStagedTruthV139C(self, sample);
                    }
                    else
                    {
                        RunEyeTruthDiagnosticsV99(self, 0, sample);
                        RunEyeTruthDiagnosticsV99(self, 1, sample);
                    }
                }
                catch (Exception e)
                {
                    V99LogFailure("truth diagnostics exception: " + e.GetType().Name + ": " + e.Message);
                }
                return true;
            }

            private static Vector3 RayFromTopLeftUvV99(Matrix4x4 projection, float u, float v)
            {
                Matrix4x4 inv = projection.inverse;
                Vector4 h = inv * new Vector4(u * 2f - 1f, 1f - v * 2f, 1f, 1f);
                Vector3 p = Mathf.Abs(h.w) > 1e-8f
                    ? new Vector3(h.x / h.w, h.y / h.w, h.z / h.w)
                    : new Vector3(h.x,h.y,h.z);
                return p.normalized;
            }

            private static void PixelShiftToAngularV99(
                Matrix4x4 projection,
                int width,
                int height,
                float u,
                float v,
                float dx,
                float dy,
                out float yawDeg,
                out float pitchDeg,
                out float magnitudeDeg)
            {
                Vector3 a = RayFromTopLeftUvV99(projection, u, v);
                Vector3 b = RayFromTopLeftUvV99(
                    projection,
                    Mathf.Clamp01(u + dx / Mathf.Max(1,width)),
                    Mathf.Clamp01(v + dy / Mathf.Max(1,height)));
                float yawA = Mathf.Atan2(a.x, Mathf.Max(1e-8f,Mathf.Abs(a.z))) * Mathf.Rad2Deg;
                float yawB = Mathf.Atan2(b.x, Mathf.Max(1e-8f,Mathf.Abs(b.z))) * Mathf.Rad2Deg;
                float pitchA = Mathf.Atan2(a.y, Mathf.Sqrt(a.x*a.x+a.z*a.z)) * Mathf.Rad2Deg;
                float pitchB = Mathf.Atan2(b.y, Mathf.Sqrt(b.x*b.x+b.z*b.z)) * Mathf.Rad2Deg;
                yawDeg = Mathf.DeltaAngle(yawA,yawB);
                pitchDeg = pitchB-pitchA;
                magnitudeDeg = Vector3.Angle(a,b);
            }

            private static float AtlasValueV99(Unity.Collections.NativeArray<float> d, int cols, int rows, int band, int x, int y, int c)
            {
                int index = (((band * rows + y) * cols + x) * 4) + c;
                return index >= 0 && index < d.Length ? d[index] : 0f;
            }

            private static void LogFieldRowsV99(
                Unity.Collections.NativeArray<float> d,
                V99PendingReadback p,
                int band,
                string name,
                bool vectorXY)
            {
                for (int y=0;y<p.Rows;y++)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    for (int x=0;x<p.Columns;x++)
                    {
                        if (x>0) sb.Append(',');
                        float a=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,0);
                        float b=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,1);
                        float c=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,2);
                        float e=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,3);
                        if (vectorXY) sb.AppendFormat("{0:F2}/{1:F2}/{2:F2}/{3:F3}",a,b,c,e);
                        else sb.AppendFormat("{0:F3}/{1:F3}/{2:F3}/{3:F3}",a,b,c,e);
                    }
                    MelonLogger.Msg(
                        $"[StereoRender][AER V99][FieldRow] sample={p.SampleId} eye={(p.Eye==0?"L":"R")} " +
                        $"field={name} iy={y} values=[{sb}]");
                }
            }

            private static void LogPairFieldRowsV99(
                Unity.Collections.NativeArray<float> d,
                V99PendingReadback p,
                int band,
                string name)
            {
                for (int y=0;y<p.Rows;y++)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    for (int x=0;x<p.Columns;x++)
                    {
                        if (x>0) sb.Append(',');
                        float a=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,0);
                        float b=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,1);
                        float c=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,2);
                        float e=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,3);
                        sb.AppendFormat("{0:F2}/{1:F2}|{2:F2}/{3:F2}",a,b,c,e);
                    }
                    MelonLogger.Msg(
                        $"[StereoRender][AER V99][PairFieldRow] sample={p.SampleId} eye={(p.Eye==0?"L":"R")} " +
                        $"field={name} iy={y} firstXY|secondXY values=[{sb}]");
                }
            }

            private static void LogDepthClassRowsV101(
                Unity.Collections.NativeArray<float> d,
                V99PendingReadback p,
                int candidateIndex,
                int depthClass)
            {
                int band = V101ClassStatsBase + candidateIndex * V101DepthClassCount + depthClass;
                for (int y=0; y<p.Rows; ++y)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    for (int x=0; x<p.Columns; ++x)
                    {
                        if (x>0) sb.Append(',');
                        float mae=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,0);
                        float rmse=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,1);
                        float edge=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,2);
                        float frac=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,3);
                        sb.AppendFormat("{0:F4}/{1:F4}/{2:F4}/{3:F3}",mae,rmse,edge,frac);
                    }
                    MelonLogger.Msg(
                        $"[StereoRender][AER V102][DepthClassRow] epoch={p.EpochV102} sample={p.SampleId} eye={(p.Eye==0?"L":"R")} " +
                        $"candidate={V99CandidateNames[candidateIndex]} class={V101DepthClassNames[depthClass]} " +
                        $"iy={y} cell=MAE/RMSE/edgeMAE/classFraction values=[{sb}]");
                }
            }

            private static void LogDepthClassSummariesV101(
                Unity.Collections.NativeArray<float> d,
                V99PendingReadback p,
                int candidateIndex)
            {
                for (int dc=0; dc<V101DepthClassCount; ++dc)
                {
                    int band = V101ClassStatsBase + candidateIndex * V101DepthClassCount + dc;
                    double sumW=0, sumMae=0, sumRmseSq=0, sumEdge=0;
                    double[] bandW=new double[3];
                    double[] bandMae=new double[3];
                    double[] bandRmseSq=new double[3];
                    double[] bandEdge=new double[3];
                    int[] bandTiles=new int[3];

                    for(int y=0;y<p.Rows;y++) for(int x=0;x<p.Columns;x++)
                    {
                        float mae=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,0);
                        float rmse=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,1);
                        float edge=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,2);
                        float frac=Mathf.Clamp01(AtlasValueV99(d,p.Columns,p.Rows,band,x,y,3));
                        int bi = y<p.Rows/3 ? 0 : (y<(2*p.Rows)/3 ? 1 : 2);
                        bandTiles[bi]++;
                        bandW[bi]+=frac;
                        bandMae[bi]+=mae*frac;
                        bandRmseSq[bi]+=rmse*rmse*frac;
                        bandEdge[bi]+=edge*frac;
                        sumW+=frac; sumMae+=mae*frac; sumRmseSq+=rmse*rmse*frac; sumEdge+=edge*frac;
                    }

                    double meanMae=sumW>1e-9?sumMae/sumW:0;
                    double meanRmse=sumW>1e-9?Math.Sqrt(Math.Max(0,sumRmseSq/sumW)):0;
                    double meanEdge=sumW>1e-9?sumEdge/sumW:0;
                    double classFraction=sumW/Math.Max(1,p.Columns*p.Rows);

                    double[] bFrac=new double[3];
                    double[] bMae=new double[3];
                    double[] bRmse=new double[3];
                    for(int bi=0;bi<3;bi++)
                    {
                        bFrac[bi]=bandW[bi]/Math.Max(1,bandTiles[bi]);
                        bMae[bi]=bandW[bi]>1e-9?bandMae[bi]/bandW[bi]:0;
                        bRmse[bi]=bandW[bi]>1e-9?Math.Sqrt(Math.Max(0,bandRmseSq[bi]/bandW[bi])):0;
                    }

                    MelonLogger.Warning(
                        $"[StereoRender][AER V102][DepthClassSummary] epoch={p.EpochV102} sample={p.SampleId} eye={(p.Eye==0?"L":"R")} " +
                        $"candidate={V99CandidateNames[candidateIndex]} class={V101DepthClassNames[dc]} " +
                        $"classFraction={classFraction:F4} MAE={meanMae:F5} RMSE={meanRmse:F5} edgeMAE={meanEdge:F5}; " +
                        $"rawTop(HMD_BOTTOM)/rawCenter/rawBottom(HMD_TOP) fraction=" +
                        $"{bFrac[0]:F4}/{bFrac[1]:F4}/{bFrac[2]:F4} MAE={bMae[0]:F5}/{bMae[1]:F5}/{bMae[2]:F5} " +
                        $"RMSE={bRmse[0]:F5}/{bRmse[1]:F5}/{bRmse[2]:F5}.");
                }

                // Detailed spatial maps only for raw + the two full-fill grid baselines.
                if (candidateIndex == 0 || candidateIndex == 1 || candidateIndex == 4)
                    LogDepthClassRowsV101(d,p,candidateIndex,4); // NO_DEPTH / sky-like
                if (candidateIndex == 1 || candidateIndex == 4)
                    LogDepthClassRowsV101(d,p,candidateIndex,3); // >=50m finite
            }

            private static void LogOcclusionClassRowsV102(
                Unity.Collections.NativeArray<float> d,
                V99PendingReadback p,
                int candidateIndex,
                int occlusionClass)
            {
                int band = V102OcclusionStatsBase + candidateIndex * V102OcclusionClassCount + occlusionClass;
                for (int y=0; y<p.Rows; ++y)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    for (int x=0; x<p.Columns; ++x)
                    {
                        if (x>0) sb.Append(',');
                        float mae=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,0);
                        float rmse=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,1);
                        float edge=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,2);
                        float frac=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,3);
                        sb.AppendFormat("{0:F4}/{1:F4}/{2:F4}/{3:F3}",mae,rmse,edge,frac);
                    }
                    MelonLogger.Msg(
                        $"[StereoRender][AER V102][OcclusionClassRow] epoch={p.EpochV102} sample={p.SampleId} eye={(p.Eye==0?"L":"R")} " +
                        $"candidate={V99CandidateNames[candidateIndex]} class={V102OcclusionClassNames[occlusionClass]} " +
                        $"iy={y} cell=MAE/RMSE/edgeMAE/classFraction values=[{sb}]");
                }
            }

            private static void LogOcclusionClassSummariesV102(
                Unity.Collections.NativeArray<float> d,
                V99PendingReadback p,
                int candidateIndex)
            {
                for (int oc=0; oc<V102OcclusionClassCount; ++oc)
                {
                    int band=V102OcclusionStatsBase+candidateIndex*V102OcclusionClassCount+oc;
                    double sumW=0,sumMae=0,sumRmseSq=0,sumEdge=0;
                    double[] bandW=new double[3];
                    double[] bandMae=new double[3];
                    double[] bandRmseSq=new double[3];
                    int[] bandTiles=new int[3];
                    for(int y=0;y<p.Rows;y++) for(int x=0;x<p.Columns;x++)
                    {
                        float mae=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,0);
                        float rmse=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,1);
                        float edge=AtlasValueV99(d,p.Columns,p.Rows,band,x,y,2);
                        float frac=Mathf.Clamp01(AtlasValueV99(d,p.Columns,p.Rows,band,x,y,3));
                        int bi=y<p.Rows/3?0:(y<(2*p.Rows)/3?1:2);
                        bandTiles[bi]++; bandW[bi]+=frac; bandMae[bi]+=mae*frac; bandRmseSq[bi]+=rmse*rmse*frac;
                        sumW+=frac; sumMae+=mae*frac; sumRmseSq+=rmse*rmse*frac; sumEdge+=edge*frac;
                    }
                    double classFraction=sumW/Math.Max(1,p.Columns*p.Rows);
                    double meanMae=sumW>1e-9?sumMae/sumW:0;
                    double meanRmse=sumW>1e-9?Math.Sqrt(Math.Max(0,sumRmseSq/sumW)):0;
                    double meanEdge=sumW>1e-9?sumEdge/sumW:0;
                    double[] bFrac=new double[3],bMae=new double[3],bRmse=new double[3];
                    for(int bi=0;bi<3;bi++)
                    {
                        bFrac[bi]=bandW[bi]/Math.Max(1,bandTiles[bi]);
                        bMae[bi]=bandW[bi]>1e-9?bandMae[bi]/bandW[bi]:0;
                        bRmse[bi]=bandW[bi]>1e-9?Math.Sqrt(Math.Max(0,bandRmseSq[bi]/bandW[bi])):0;
                    }
                    MelonLogger.Warning(
                        $"[StereoRender][AER V102][OcclusionClassSummary] epoch={p.EpochV102} sample={p.SampleId} eye={(p.Eye==0?"L":"R")} " +
                        $"candidate={V99CandidateNames[candidateIndex]} class={V102OcclusionClassNames[oc]} " +
                        $"classFraction={classFraction:F4} MAE={meanMae:F5} RMSE={meanRmse:F5} edgeMAE={meanEdge:F5}; " +
                        $"rawTop(HMD_BOTTOM)/rawCenter/rawBottom(HMD_TOP) fraction={bFrac[0]:F4}/{bFrac[1]:F4}/{bFrac[2]:F4} " +
                        $"MAE={bMae[0]:F5}/{bMae[1]:F5}/{bMae[2]:F5} RMSE={bRmse[0]:F5}/{bRmse[1]:F5}/{bRmse[2]:F5}.");
                }

                // Spatial maps of the four classes that actually represent a visibility change.
                // Baseline only: candidate comparisons are already summarized above.
                if (candidateIndex == 1)
                {
                    LogOcclusionClassRowsV102(d,p,candidateIndex,1);
                    LogOcclusionClassRowsV102(d,p,candidateIndex,2);
                    LogOcclusionClassRowsV102(d,p,candidateIndex,4);
                    LogOcclusionClassRowsV102(d,p,candidateIndex,5);
                }
            }

            private static void LogWarpVsMotionErrorV99(
                Unity.Collections.NativeArray<float> d,
                V99PendingReadback p)
            {
                float top=0,mid=0,bot=0; int nt=0,nm=0,nb=0;
                for (int y=0;y<p.Rows;y++)
                {
                    System.Text.StringBuilder sb=new System.Text.StringBuilder();
                    for(int x=0;x<p.Columns;x++)
                    {
                        float wx=AtlasValueV99(d,p.Columns,p.Rows,V101BandGeometric,x,y,0);
                        float wy=AtlasValueV99(d,p.Columns,p.Rows,V101BandGeometric,x,y,1);
                        float mx=AtlasValueV99(d,p.Columns,p.Rows,V101BandHdrpTotal,x,y,0);
                        float my=AtlasValueV99(d,p.Columns,p.Rows,V101BandHdrpTotal,x,y,1);
                        float dx=wx-mx, dy=wy-my;
                        float u=(x+0.5f)/p.Columns, v=(y+0.5f)/p.Rows;
                        float yaw,pitch,ang;
                        PixelShiftToAngularV99(p.TargetProjection,p.PresentationWidth,p.PresentationHeight,u,v,dx,dy,out yaw,out pitch,out ang);
                        if(x>0)sb.Append(',');
                        sb.AppendFormat("{0:F2}/{1:F2}|{2:F4}/{3:F4}",dx,dy,yaw,pitch);
                        if(y<p.Rows/3){top+=ang;nt++;}
                        else if(y<(2*p.Rows)/3){mid+=ang;nm++;}
                        else{bot+=ang;nb++;}
                    }
                    MelonLogger.Msg(
                        $"[StereoRender][AER V99][WarpVsMotionRow] sample={p.SampleId} eye={(p.Eye==0?"L":"R")} iy={y} " +
                        "cell=geomMinusHDRPTotal_px|yaw/pitch_deg values=["+sb+"]");
                }
                MelonLogger.Warning(
                    $"[StereoRender][AER V99][WarpVsMotionSummary] sample={p.SampleId} eye={(p.Eye==0?"L":"R")} " +
                    $"angularMagnitude rawTop(HMD_BOTTOM)/rawCenter/rawBottom(HMD_TOP)=" +
                    $"{(nt>0?top/nt:0):F5}/{(nm>0?mid/nm:0):F5}/{(nb>0?bot/nb:0):F5}deg. " +
                    "Tile-mean diagnostic only: geometric warp is anchored on stale-source samples, while HDRP motion is anchored on current truth pixels; use this as a field-shape comparison, not a per-pixel identity test.");
            }

            private static void LogProjectionGainFieldV99(V99PendingReadback p)
            {
                for (int y=0;y<p.Rows;y++)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    for (int x=0;x<p.Columns;x++)
                    {
                        float u=(x+0.5f)/p.Columns, v=(y+0.5f)/p.Rows;
                        float yx,px,ax, yy,py,ay;
                        PixelShiftToAngularV99(p.TargetProjection,p.PresentationWidth,p.PresentationHeight,u,v,1f,0f,out yx,out px,out ax);
                        PixelShiftToAngularV99(p.TargetProjection,p.PresentationWidth,p.PresentationHeight,u,v,0f,1f,out yy,out py,out ay);
                        if(x>0)sb.Append(',');
                        sb.AppendFormat("{0:F5}/{1:F5}|{2:F5}/{3:F5}|{4:F5}/{5:F5}",yx,px,yy,py,ax,ay);
                    }
                    MelonLogger.Msg(
                        $"[StereoRender][AER V99][ProjectionGainRow] eye={(p.Eye==0?"L":"R")} iy={y} " +
                        "cell=onePixelX(yaw/pitch)|onePixelY(yaw/pitch)|angularMagX/angularMagY_deg values=["+sb+"]");
                }
            }

            private static void LogHdrpVsExactFlowV99(
                Unity.Collections.NativeArray<float> d,
                V99PendingReadback p)
            {
                float top=0,mid=0,bot=0; int nt=0,nm=0,nb=0;
                for(int y=0;y<p.Rows;y++)
                {
                    System.Text.StringBuilder sb=new System.Text.StringBuilder();
                    for(int x=0;x<p.Columns;x++)
                    {
                        // HDRP is forward current-minus-previous.  NVOFA is backward
                        // current-to-previous.  A perfect agreement therefore sums to zero.
                        float hx=AtlasValueV99(d,p.Columns,p.Rows,V101BandHdrpTotal,x,y,0);
                        float hy=AtlasValueV99(d,p.Columns,p.Rows,V101BandHdrpTotal,x,y,1);
                        float ox=AtlasValueV99(d,p.Columns,p.Rows,V101BandExactNvofa,x,y,0);
                        float oy=AtlasValueV99(d,p.Columns,p.Rows,V101BandExactNvofa,x,y,1);
                        float dx=hx+ox, dy=hy+oy;
                        float u=(x+0.5f)/p.Columns, v=(y+0.5f)/p.Rows;
                        float yaw,pitch,ang;
                        PixelShiftToAngularV99(p.TargetProjection,p.PresentationWidth,p.PresentationHeight,u,v,dx,dy,out yaw,out pitch,out ang);
                        if(x>0)sb.Append(',');
                        sb.AppendFormat("{0:F2}/{1:F2}|{2:F4}/{3:F4}",dx,dy,yaw,pitch);
                        if(y<p.Rows/3){top+=ang;nt++;}
                        else if(y<(2*p.Rows)/3){mid+=ang;nm++;}
                        else{bot+=ang;nb++;}
                    }
                    MelonLogger.Msg(
                        $"[StereoRender][AER V99][HDRPvsExactNVOFRow] sample={p.SampleId} eye={(p.Eye==0?"L":"R")} iy={y} " +
                        "cell=HDRPforward_PLUS_NVOFAbackward_px|yaw/pitch_deg values=["+sb+"]");
                }
                MelonLogger.Warning(
                    $"[StereoRender][AER V99][HDRPvsExactNVOFSummary] sample={p.SampleId} eye={(p.Eye==0?"L":"R")} " +
                    $"angularDisagreement rawTop(HMD_BOTTOM)/rawCenter/rawBottom(HMD_TOP)={(nt>0?top/nt:0):F5}/{(nm>0?mid/nm:0):F5}/{(nb>0?bot/nb:0):F5}deg.");
            }

            private static void ProcessForensicsReadbackV99(V99PendingReadback p)
            {
                if (p.Request.hasError)
                {
                    V99LogFailure("forensic atlas readback ERROR eye=" + (p.Eye==0?"L":"R"));
                    AerCalibrationV139.OnReadbackError(p.SampleId,p.Eye);
                    return;
                }
                var d = p.Request.GetData<float>();
                MelonLogger.Warning(
                    $"[StereoRender][AER V102][ReadbackBegin] epoch={p.EpochV102} epochSample={p.EpochSampleV102} " +
                    $"sample={p.SampleId} eye={(p.Eye==0?"L":"R")} camera={p.CameraNameV102} farClip={p.FarClipMetersV102:F1}m " +
                    $"truthFrame={p.TruthFrame} historyFrame={p.HistoryFrame} age={p.HistoryAgeMs:F2}ms " +
                    $"grid={p.Columns}x{p.Rows} presentation={p.PresentationWidth}x{p.PresentationHeight} " +
                    $"ofScale={p.OpticalFlowScale:F4} {p.MotionStatus} {p.FlowStatus} {p.TruthFlowStatus}. " +
                    "IMPORTANT: diagnostic raw texture Y is inverted by OpenVR submission; raw TOP = physical HMD BOTTOM, raw BOTTOM = physical HMD TOP.");

                // V139C: compact calibration path. Full per-tile data goes to durable
                // CSV; avoid thousands of formatted MelonLogger rows on the main thread.
                if (AerCalibrationV139.ActiveCapture)
                {
                    int accepted = 0, saturated = 0;
                    for (int ci = 0; ci < V99CandidateCount; ++ci)
                        for (int y = 0; y < p.Rows; ++y)
                            for (int x = 0; x < p.Columns; ++x)
                            {
                                float dx = AtlasValueV99(d,p.Columns,p.Rows,ci,x,y,0);
                                float dy = AtlasValueV99(d,p.Columns,p.Rows,ci,x,y,1);
                                float best = AtlasValueV99(d,p.Columns,p.Rows,ci,x,y,2);
                                float second = AtlasValueV99(d,p.Columns,p.Rows,ci,x,y,3);
                                float confidence = (second > 1e-6f && best >= 0f)
                                    ? Mathf.Clamp01((second-best)/second) : 0f;
                                if (best >= 0f && confidence >= 0.005f) accepted++;
                                if (Mathf.Abs(dx) >= AerV99BlockMatchSearchRadiusPixels ||
                                    Mathf.Abs(dy) >= AerV99BlockMatchSearchRadiusPixels) saturated++;
                                float yaw,pitch,angular;
                                PixelShiftToAngularV99(p.TargetProjection,p.PresentationWidth,p.PresentationHeight,
                                    (x+0.5f)/p.Columns,(y+0.5f)/p.Rows,dx,dy,
                                    out yaw,out pitch,out angular);
                                int band = V101FiniteStatsBase + ci;
                                AerCalibrationV139.RecordTile(p.SampleId,p.Eye,V99CandidateNames[ci],
                                    x,y,p.Columns,p.Rows,yaw,pitch,confidence,
                                    AtlasValueV99(d,p.Columns,p.Rows,band,x,y,2),
                                    AtlasValueV99(d,p.Columns,p.Rows,band,x,y,3),
                                    p.HistoryAgeMs,
                                    AtlasValueV99(d,p.Columns,p.Rows,V101BandTruthDepth,x,y,0),
                                    AtlasValueV99(d,p.Columns,p.Rows,V101BandSceneStructure,x,y,2),
                                    AtlasValueV99(d,p.Columns,p.Rows,V101BandSceneStructure,x,y,3),
                                    Mathf.Abs(dx)>=AerV99BlockMatchSearchRadiusPixels ||
                                    Mathf.Abs(dy)>=AerV99BlockMatchSearchRadiusPixels);
                            }
                    AerCalibrationV139.TraceGpuV139C("ATLAS_PARSED sample=" + p.SampleId +
                        " eye=" + p.Eye + " acceptedTiles=" + accepted +
                        " searchBoundaryTiles=" + saturated);
                    MarkV102ReadbackMilestone(p);
                    AerCalibrationV139.OnReadbackComplete(p.SampleId,p.Eye);
                    return;
                }

                for (int ci=0;ci<V99CandidateCount;ci++)
                {
                    float sumDx=0,sumDy=0,sumAng=0,sumImp=0,sumMae=0,sumRmse=0,sumEdge=0,sumCov=0;
                    int n=0;
                    float topAng=0,midAng=0,botAng=0;
                    float topDx=0,topDy=0,midDx=0,midDy=0,botDx=0,botDy=0;
                    float topYaw=0,topPitch=0,midYaw=0,midPitch=0,botYaw=0,botPitch=0;
                    int nt=0,nm=0,nb=0;
                    for (int y=0;y<p.Rows;y++)
                    {
                        System.Text.StringBuilder row = new System.Text.StringBuilder();
                        for (int x=0;x<p.Columns;x++)
                        {
                            float dx=AtlasValueV99(d,p.Columns,p.Rows,ci,x,y,0);
                            float dy=AtlasValueV99(d,p.Columns,p.Rows,ci,x,y,1);
                            float best=AtlasValueV99(d,p.Columns,p.Rows,ci,x,y,2);
                            float second=AtlasValueV99(d,p.Columns,p.Rows,ci,x,y,3);
                            float u=(x+0.5f)/p.Columns;
                            float v=(y+0.5f)/p.Rows;
                            float yaw,pitch,ang;
                            PixelShiftToAngularV99(p.TargetProjection,p.PresentationWidth,p.PresentationHeight,u,v,dx,dy,out yaw,out pitch,out ang);
                            float distinct=(second>1e-6f && best>=0f)?Mathf.Clamp01((second-best)/second):0f;
                            if(x>0)row.Append(',');
                            row.AppendFormat("{0:F1}/{1:F1}|{2:F4}/{3:F4}|{4:F3}",dx,dy,yaw,pitch,distinct);
                            // Require a locally identifiable match for angular summaries. Flat sky
                            // and repeated textures still remain in the raw row/MAE telemetry, but
                            // cannot manufacture a fake +/-8 px vector just because all shifts tie.
                            if(best>=0f && distinct>=0.005f)
                            {
                                sumDx+=dx;sumDy+=dy;sumAng+=ang;sumImp+=distinct;n++;
                                if(y<p.Rows/3){topAng+=ang;topDx+=dx;topDy+=dy;topYaw+=yaw;topPitch+=pitch;nt++;}
                                else if(y<(2*p.Rows)/3){midAng+=ang;midDx+=dx;midDy+=dy;midYaw+=yaw;midPitch+=pitch;nm++;}
                                else {botAng+=ang;botDx+=dx;botDy+=dy;botYaw+=yaw;botPitch+=pitch;nb++;}
                                int sb=V101FiniteStatsBase+ci;
                                sumMae+=AtlasValueV99(d,p.Columns,p.Rows,sb,x,y,0);
                                sumRmse+=AtlasValueV99(d,p.Columns,p.Rows,sb,x,y,1);
                                sumEdge+=AtlasValueV99(d,p.Columns,p.Rows,sb,x,y,2);
                                sumCov+=AtlasValueV99(d,p.Columns,p.Rows,sb,x,y,3);
                            }
                        }
                        MelonLogger.Msg(
                            $"[StereoRender][AER V99][AngularRow] sample={p.SampleId} eye={(p.Eye==0?"L":"R")} " +
                            $"candidate={V99CandidateNames[ci]} iy={y} " +
                            "cell=dxPx/dyPx|yawDeg/pitchDeg|matchDistinctiveness values=["+row+"]");
                    }
                    float inv=n>0?1f/n:0f;
                    MelonLogger.Warning(
                        $"[StereoRender][AER V99][CandidateSummary] sample={p.SampleId} eye={(p.Eye==0?"L":"R")} " +
                        $"candidate={V99CandidateNames[ci]} n={n} meanShiftPx=({sumDx*inv:F3},{sumDy*inv:F3}) " +
                        $"meanAngularError={sumAng*inv:F5}deg rawTop(HMD_BOTTOM)/rawCenter/rawBottom(HMD_TOP)=" +
                        $"{(nt>0?topAng/nt:0):F5}/{(nm>0?midAng/nm:0):F5}/{(nb>0?botAng/nb:0):F5}deg; " +
                        $"bandShiftPx RAW_TOP(HMD_BOTTOM)=({(nt>0?topDx/nt:0):F3},{(nt>0?topDy/nt:0):F3}) " +
                        $"RAW_CENTER=({(nm>0?midDx/nm:0):F3},{(nm>0?midDy/nm:0):F3}) " +
                        $"RAW_BOTTOM(HMD_TOP)=({(nb>0?botDx/nb:0):F3},{(nb>0?botDy/nb:0):F3}); " +
                        $"bandAngularSigned RAW_TOP(HMD_BOTTOM)=({(nt>0?topYaw/nt:0):F5},{(nt>0?topPitch/nt:0):F5}) " +
                        $"RAW_CENTER=({(nm>0?midYaw/nm:0):F5},{(nm>0?midPitch/nm:0):F5}) " +
                        $"RAW_BOTTOM(HMD_TOP)=({(nb>0?botYaw/nb:0):F5},{(nb>0?botPitch/nb:0):F5}) yaw/pitch-deg; " +
                        $"meanMatchDistinctiveness={sumImp*inv:F3} lumaMAE={sumMae*inv:F5} RMSE={sumRmse*inv:F5} " +
                        $"edgeWeightedMAE={sumEdge*inv:F5} syntheticDirectOrFilledCoverage={sumCov*inv:F3}.");
                    LogFieldRowsV99(
                        d,p,V101FiniteStatsBase+ci,
                        "FINITE_IMAGE_ERROR_"+V99CandidateNames[ci]+"(MAE/RMSE/edgeMAE/coverage)",false);
                    LogDepthClassSummariesV101(d,p,ci);
                    LogOcclusionClassSummariesV102(d,p,ci);
                }

                // V139: per-tile compact machine-readable truth score.  Physical HMD top
                // is RAW image bottom because the SteamVR submission flips texture Y.
                // A tile is included only when block registration has identifiable matches.
                if (AerCalibrationV139.ActiveCapture)
                {
                    for (int ci = 0; ci < V99CandidateCount; ++ci)
                        for (int y = 0; y < p.Rows; ++y)
                            for (int x = 0; x < p.Columns; ++x)
                            {
                                float dx = AtlasValueV99(d,p.Columns,p.Rows,ci,x,y,0);
                                float dy = AtlasValueV99(d,p.Columns,p.Rows,ci,x,y,1);
                                float best = AtlasValueV99(d,p.Columns,p.Rows,ci,x,y,2);
                                float second = AtlasValueV99(d,p.Columns,p.Rows,ci,x,y,3);
                                float confidence = (second>1e-6f && best>=0f)
                                    ? Mathf.Clamp01((second-best)/second) : 0f;
                                float yaw,pitch,angular;
                                PixelShiftToAngularV99(p.TargetProjection,p.PresentationWidth,p.PresentationHeight,
                                    (x+0.5f)/p.Columns,(y+0.5f)/p.Rows,dx,dy,
                                    out yaw,out pitch,out angular);
                                int band = V101FiniteStatsBase + ci;
                                float edge = AtlasValueV99(d,p.Columns,p.Rows,band,x,y,2);
                                float cover = AtlasValueV99(d,p.Columns,p.Rows,band,x,y,3);
                                float depthMean = AtlasValueV99(d,p.Columns,p.Rows,V101BandTruthDepth,x,y,0);
                                float sceneGradient = AtlasValueV99(d,p.Columns,p.Rows,V101BandSceneStructure,x,y,2);
                                float depthEdge = AtlasValueV99(d,p.Columns,p.Rows,V101BandSceneStructure,x,y,3);
                                AerCalibrationV139.RecordTile(p.SampleId,p.Eye,V99CandidateNames[ci],
                                    x,y,p.Columns,p.Rows,yaw,pitch,confidence,edge,cover,p.HistoryAgeMs,
                                    depthMean,sceneGradient,depthEdge);
                            }
                }

                // Analytic geometric warp, real HDRP motion and NVOFA fields.
                LogFieldRowsV99(d,p,V101BandGeometric,"GEOMETRIC_WARP_px(dx/dy/mag)",true);
                LogPairFieldRowsV99(d,p,V101BandHdrpTotal,"HDRP_TOTAL_px|CAMERA_ONLY_px");
                LogFieldRowsV99(d,p,V101BandHdrpResidual,"HDRP_OBJECT_RESIDUAL_px(dx/dy/mag)",true);
                LogPairFieldRowsV99(d,p,V101BandNvofaPredictiveTotal,"NVOFA_BACKWARD_TOTAL_px|CAMERA_ONLY_px");
                LogFieldRowsV99(d,p,V101BandNvofaPredictiveResidual,"NVOFA_PREDICTIVE_RESIDUAL_px(dx/dy/mag)",true);
                LogFieldRowsV99(d,p,V101BandHistoryDepth,"HISTORY_DEPTH_m(mean/min/max)",false);
                LogFieldRowsV99(d,p,V101BandTruthDepth,"TRUTH_DEPTH_m(mean/min/max)",false);
                LogWarpVsMotionErrorV99(d,p);
                if (p.TruthFlowStatus != null && p.TruthFlowStatus.StartsWith("truthExactFlow=yes"))
                {
                    LogPairFieldRowsV99(d,p,V101BandExactNvofa,"NVOFA_EXACT_CURRENT_TO_STALE_px|CAMERA_ONLY_BACKWARD_px");
                    LogFieldRowsV99(d,p,V101BandExactNvofaResidual,"NVOFA_EXACT_OBJECT_RESIDUAL_px(dx/dy/mag)",true);
                    LogHdrpVsExactFlowV99(d,p);
                }
                LogFieldRowsV99(d,p,V101BandSceneStructure,"SCENE_STRUCTURE(meanLuma/stddev/gradient/depthEdgeFraction)",false);
                LogFieldRowsV99(d,p,V101BandSkyRayClass,"V101_SKY_RAYS(currentNoDepth/historyNoDepthSameUV/historyNoDepthRotMapped/currentAndRotNoDepth)",false);
                LogFieldRowsV99(d,p,V102BandOcclusionComplexity,"V102_OCCLUSION_COMPLEXITY(noDepthFrac/validNoDepthBoundary/finiteDepthEdge/severeLayerJump)",false);
                LogFieldRowsV99(d,p,V102BandDepthColorRisk,"V102_DEPTH_COLOR_RISK(meanWithinDepthCellLumaRange/highColorFrac/depthMixedFrac/jointRiskFrac)",false);

                if (p.SampleId == 1)
                    LogProjectionGainFieldV99(p);

                for (int ci=1; ci<V99CandidateCount; ++ci)
                {
                    int b = V101CoverageBase + (ci - 1);
                    float top=0,mid=0,bot=0;int nt=0,nm=0,nb=0;
                    for(int y=0;y<p.Rows;y++)for(int x=0;x<p.Columns;x++)
                    {
                        float v=AtlasValueV99(d,p.Columns,p.Rows,b,x,y,0);
                        if(y<p.Rows/3){top+=v;nt++;}
                        else if(y<(2*p.Rows)/3){mid+=v;nm++;}
                        else{bot+=v;nb++;}
                    }
                    MelonLogger.Msg(
                        $"[StereoRender][AER V102][DirectCoverage] epoch={p.EpochV102} sample={p.SampleId} eye={(p.Eye==0?"L":"R")} " +
                        $"variant={V99CandidateNames[ci]} band={b} rawTop(HMD_BOTTOM)/rawCenter/rawBottom(HMD_TOP)=" +
                        $"{(nt>0?top/nt:0):F4}/{(nm>0?mid/nm:0):F4}/{(nb>0?bot/nb:0):F4}.");
                }
                MelonLogger.Warning(
                    $"[StereoRender][AER V102][ReadbackEnd] epoch={p.EpochV102} sample={p.SampleId} eye={(p.Eye==0?"L":"R")} complete.");
                MarkV102ReadbackMilestone(p);
                AerCalibrationV139.OnReadbackComplete(p.SampleId, p.Eye);
            }

            internal static void PollForensicsReadbacksV99()
            {
                if (!AerV99ForensicsEnabled) return;
                for(int eye=0;eye<2;eye++)
                {
                    V99PendingReadback p=_v99Readbacks[eye];
                    if(!p.Pending || !p.Request.done) continue;
                    p.Pending=false;
                    try { ProcessForensicsReadbackV99(p); }
                    catch(Exception e)
                    {
                        V99LogFailure("readback processing failed: "+e.GetType().Name+": "+e.Message);
                        if (AerCalibrationV139.ActiveCapture)
                            AerCalibrationV139.FailGpuV139C("ATLAS_PARSE_EXCEPTION eye="+eye+
                                " sample="+p.SampleId+" "+e.GetType().Name+" "+e.Message);
                    }
                }
            }

            private static void EnsureDebugTargets(RenderTexture sourceRT)
            {
                int width = Mathf.Clamp(AerDepthStereoWarpV48DesktopPanelWidth, 256, 1024);
                int height = Mathf.Clamp(Mathf.RoundToInt(width * (float)sourceRT.height / Mathf.Max(1, sourceRT.width)), 256, 1200);
                EnsureDebugTarget(ref _debugSourceRT, width, height, "SonsVR_V50_Debug_Source");
                EnsureDebugTarget(ref _debugSyntheticRT, width, height, "SonsVR_V50_Debug_Synthetic");
                EnsureDebugTarget(ref _debugDepthRT, width, height, "SonsVR_V83_Debug_TemporalCoverage");
                EnsureDebugTarget(ref _debugOldStaleRT, width, height, "SonsVR_V83_Debug_SameEyeHistory");
                EnsureDebugTarget(ref _debugTruthLocalRTV83, width, height, "SonsVR_V83_Debug_LocalDepthTruth");
                EnsureDebugTarget(ref _debugTruthGlobalMip0RTV83, width, height, "SonsVR_V83_Debug_GlobalMip0Truth");
            }

            private static void EnsureDebugTarget(ref RenderTexture rt, int width, int height, string name)
            {
                if (rt != null && rt.width == width && rt.height == height && rt.IsCreated())
                    return;

                if (rt != null)
                {
                    try { rt.Release(); } catch { }
                    UnityEngine.Object.Destroy(rt);
                }

                rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    name = name,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                rt.Create();
            }

            // V77: HDRP/DLSS/RTHandle rendering can leave a non-fullscreen GL viewport
            // behind as global immediate-mode state. Graphics.SetRenderTarget() changes the target
            // but does NOT give our hand-drawn GL/DrawMeshNow passes a trustworthy viewport.
            //
            // V36 already learned this lesson and explicitly calls GL.Viewport() for its stale-eye
            // reprojection. V50/V51 never did, which lets a dynamic-resolution/DLSS viewport clip
            // the synthetic eye into a giant flashing rectangle. Always bind off-screen targets
            // through this helper.
            private static void SetRenderTargetFullViewportV77(RenderTexture target)
            {
                if (target == null)
                    return;

                Graphics.SetRenderTarget(target);
                GL.Viewport(new Rect(0f, 0f, target.width, target.height));
            }

            private static void RestoreRenderTargetViewportV77(RenderTexture previous)
            {
                RenderTexture.active = previous;

                if (previous != null)
                    GL.Viewport(new Rect(0f, 0f, previous.width, previous.height));
                else
                    GL.Viewport(new Rect(0f, 0f, Screen.width, Screen.height));
            }

            private static void DrawFullscreenTopLeftUvQuad()
            {
                GL.PushMatrix();
                GL.LoadIdentity();
                GL.Begin(GL.QUADS);
                // Explicit TOP-LEFT UV convention. Clip-space +Y is top.
                GL.TexCoord2(0f, 0f); GL.Vertex3(-1f,  1f, 0f);
                GL.TexCoord2(1f, 0f); GL.Vertex3( 1f,  1f, 0f);
                GL.TexCoord2(1f, 1f); GL.Vertex3( 1f, -1f, 0f);
                GL.TexCoord2(0f, 1f); GL.Vertex3(-1f, -1f, 0f);
                GL.End();
                GL.PopMatrix();
            }

            private static void LogDepthLatchFailure(string message)
            {
                if (_depthLatchFailureLogsRemaining <= 0)
                    return;
                _depthLatchFailureLogsRemaining--;
                MelonLogger.Warning($"[StereoRender][AER V50][DepthLatch] {message} Logs remaining={_depthLatchFailureLogsRemaining}.");
            }

            private static void LogFailure(string message)
            {
                if (_failureLogsRemaining <= 0)
                    return;
                _failureLogsRemaining--;
                MelonLogger.Warning($"[StereoRender][AER V50][DepthWarp] {message} Logs remaining={_failureLogsRemaining}.");
            }
        }

        // ---------------------------------------------------------------------
        // ZER0INT V47 - EMBEDDED DEPTH SHADER EXECUTION / RTHandle UV BOARD
        // ---------------------------------------------------------------------
        // The Unity Editor-compiled AssetBundle supplied by the user is embedded as a managed
        // resource in SonsVR_Mod.dll. V47D keeps it memory-only, but uses MelonLoader's IL2CPP-specific AssetBundle bridge:
        // managed byte[] -> Il2CppStructArray<byte> -> Il2CppAssetBundleManager.LoadFromMemory.
        // The ordinary generated UnityEngine.AssetBundle LoadFromFile/LoadFromMemory wrappers both
        // returned null in V47/V47A even though the UnityFS bytes were valid. Nothing
        // is extracted to %TEMP% or written beside the mod. Initialization remains lazy and can
        // happen only after V43 gameplay AER activation.
        //
        // Crucial safety boundary: V47 never writes LeftRT/RightRT, never changes the textures
        // passed to OpenVR, and runs only after StereoRenderPass.Execute() has already submitted
        // both eyes. Its products are small standalone debug RTs shown ONLY on the desktop mirror.
        private static class AerDepthShaderDebugV47
        {
            private const string BundleResourceName = "SonsVRMod.Resources.sonsvr_depththingy";
            private const string ShaderAssetPath = "Assets/DepthStereoDebug.shader";

            private static Il2CppAssetBundle _bundle;
            private static Shader _shader;
            private static Material _material;
            private static RenderTexture _colorRT;
            private static RenderTexture _depthLowerLeftRT;
            private static RenderTexture _depthCenteredRT;
            private static RenderTexture _depthRawRT;
            private static bool _initAttempted;
            private static bool _available;
            private static bool _hasDebugFrame;
            private static float _nextUpdateTime;
            private static string _lastEye = "?";
            private static int _lastSourceFrame = -1;
            private static int _lastDepthWidth = -1, _lastDepthHeight = -1;
            private static int _lastEyeWidth = -1, _lastEyeHeight = -1;
            private static int _failureLogsRemaining = 8;

            internal static void ResetForActivation()
            {
                _hasDebugFrame = false;
                _nextUpdateTime = 0f;
                _lastEye = "?";
                _lastSourceFrame = -1;
                _failureLogsRemaining = 8;
                // Keep a successfully loaded bundle/material across StereoRender instance churn;
                // they are process-level resources and do not belong to any particular scene.
            }

            internal static void RenderAfterSubmit(StereoRender self, int completedEye)
            {
                if (!AerDepthShaderDebugV47Enabled || self == null || !AlternateEyeRenderingEnabled)
                    return;

                // BOTH means warmup/resync. The global HDRP depth binding after two eye renders is
                // intentionally ambiguous, so wait for a normal single-eye AER frame.
                if (completedEye != 0 && completedEye != 1)
                    return;

                if (Time.unscaledTime < _nextUpdateTime)
                    return;
                _nextUpdateTime = Time.unscaledTime + Mathf.Max(0.05f, AerDepthShaderDebugV47UpdateIntervalSeconds);

                if (!EnsureLoaded())
                    return;

                Camera cam = completedEye == 0 ? self.LeftCam : self.RightCam;
                RenderTexture sourceEye = completedEye == 0 ? self.LeftRT : self.RightRT;
                if (cam == null || sourceEye == null || !sourceEye.IsCreated())
                    return;

                Texture depthTexture = Shader.GetGlobalTexture("_CameraDepthTexture");
                if (depthTexture == null)
                {
                    LogFailure("_CameraDepthTexture was null after fresh-eye submit; desktop debug frame skipped.");
                    return;
                }

                try
                {
                    EnsureDebugTargets(sourceEye);

                    // Snapshot globals BEFORE our own blits. Everything needed by the material is
                    // then set explicitly so later Graphics/GUI work cannot change what it samples.
                    Vector4 zParams = Shader.GetGlobalVector("_ZBufferParams");
                    float scaleX = Mathf.Clamp01((float)sourceEye.width / Mathf.Max(1, depthTexture.width));
                    float scaleY = Mathf.Clamp01((float)sourceEye.height / Mathf.Max(1, depthTexture.height));
                    Vector4 lowerLeft = new Vector4(scaleX, scaleY, 0f, 0f);
                    Vector4 centered = new Vector4(scaleX, scaleY, (1f - scaleX) * 0.5f, (1f - scaleY) * 0.5f);
                    Vector4 raw = new Vector4(1f, 1f, 0f, 0f);

                    // Same-frame/fresh-eye color reference for visual registration. This copy is
                    // deliberately after OpenVR submit, exactly like the existing desktop mirror.
                    Graphics.Blit(sourceEye, _colorRT);
                    RenderTexture.active = null;

                    RenderDepthPanel(depthTexture, zParams, lowerLeft, _depthLowerLeftRT);
                    RenderDepthPanel(depthTexture, zParams, centered, _depthCenteredRT);
                    RenderDepthPanel(depthTexture, zParams, raw, _depthRawRT);

                    _hasDebugFrame = true;
                    _lastEye = completedEye == 0 ? "L" : "R";
                    _lastSourceFrame = Time.frameCount;

                    if (_lastDepthWidth != depthTexture.width || _lastDepthHeight != depthTexture.height ||
                        _lastEyeWidth != sourceEye.width || _lastEyeHeight != sourceEye.height)
                    {
                        _lastDepthWidth = depthTexture.width;
                        _lastDepthHeight = depthTexture.height;
                        _lastEyeWidth = sourceEye.width;
                        _lastEyeHeight = sourceEye.height;
                        MelonLogger.Msg($"[StereoRender][AER V47D][DepthShader] live board: shader='{_shader.name}' freshEye={_lastEye} eyeRT={sourceEye.width}x{sourceEye.height} depth={depthTexture.width}x{depthTexture.height} dimension={depthTexture.dimension} lowerLeftScale=({scaleX:F4},{scaleY:F4}) centeredBias=({centered.z:F4},{centered.w:F4}) maxMeters={AerDepthShaderDebugV47MaxMeters:F1}. Headset submission remains untouched.");
                    }
                }
                catch (Exception e)
                {
                    RenderTexture.active = null;
                    LogFailure("render failed: " + e.Message);
                }
            }

            internal static bool TryDrawDesktopDebug(RenderTexture fallbackMirror)
            {
                if (!AerDepthShaderDebugV47Enabled || !_available || !_hasDebugFrame ||
                    _colorRT == null || _depthLowerLeftRT == null || _depthCenteredRT == null || _depthRawRT == null)
                    return false;

                float sw = Mathf.Max(1f, Screen.width);
                float sh = Mathf.Max(1f, Screen.height);
                float cellW = sw * 0.5f;
                float cellH = sh * 0.5f;
                float labelH = Mathf.Clamp(sh * 0.035f, 18f, 36f);

                DrawCell(new Rect(0f, 0f, cellW, cellH), _colorRT,
                    $"V47 FRESH COLOR  eye={_lastEye}  frame={_lastSourceFrame}", labelH);
                DrawCell(new Rect(cellW, 0f, cellW, cellH), _depthLowerLeftRT,
                    "DEPTH A: active viewport from lower-left", labelH);
                DrawCell(new Rect(0f, cellH, cellW, cellH), _depthCenteredRT,
                    "DEPTH B: active viewport centered", labelH);
                DrawCell(new Rect(cellW, cellH, cellW, cellH), _depthRawRT,
                    "DEPTH C: raw full allocation", labelH);
                return true;
            }

            private static void DrawCell(Rect cell, RenderTexture texture, string label, float labelH)
            {
                GUI.Box(new Rect(cell.x, cell.y, cell.width, labelH), label);
                Rect imageRect = new Rect(cell.x, cell.y + labelH, cell.width, Mathf.Max(1f, cell.height - labelH));
                GUI.DrawTexture(imageRect, texture, ScaleMode.ScaleToFit, false);
            }

            private static bool EnsureLoaded()
            {
                if (_available && _material != null)
                    return true;
                if (_initAttempted)
                    return false;
                _initAttempted = true;

                try
                {
                    Assembly asm = Assembly.GetExecutingAssembly();
                    using Stream stream = asm.GetManifestResourceStream(BundleResourceName);
                    if (stream == null)
                    {
                        string names = string.Join(", ", asm.GetManifestResourceNames());
                        throw new Exception($"embedded resource '{BundleResourceName}' not found. Present resources: {names}");
                    }

                    long bundleBytesLong = stream.Length;
                    if (bundleBytesLong <= 0 || bundleBytesLong > int.MaxValue)
                        throw new Exception($"embedded THINGY has invalid length {bundleBytesLong} bytes.");

                    int bundleBytes = (int)bundleBytesLong;
                    byte[] bundleData = new byte[bundleBytes];
                    int totalRead = 0;
                    while (totalRead < bundleBytes)
                    {
                        int n = stream.Read(bundleData, totalRead, bundleBytes - totalRead);
                        if (n <= 0) break;
                        totalRead += n;
                    }
                    if (totalRead != bundleBytes)
                        throw new Exception($"embedded THINGY short read: expected {bundleBytes} bytes, got {totalRead}.");

                    string magic = bundleBytes >= 7 ? System.Text.Encoding.ASCII.GetString(bundleData, 0, 7) : "<short>";

                    // IL2CPP IMPORTANT: do not call the normal generated AssetBundle wrapper here.
                    // V47 (LoadFromFile) and V47A (LoadFromMemory(byte[])) both returned null.
                    // MelonLoader ships UnityEngine.Il2CppAssetBundleManager specifically for IL2CPP
                    // games; it marshals the managed bytes into an actual IL2CPP byte[] and invokes
                    // Unity's internal AssetBundle icall directly.
                    Il2CppStructArray<byte> il2cppBundleData = bundleData;
                    if (il2cppBundleData == null)
                        throw new Exception("managed byte[] -> Il2CppStructArray<byte> conversion returned null.");

                    _bundle = Il2CppAssetBundleManager.LoadFromMemory(il2cppBundleData);
                    if (_bundle == null)
                        throw new Exception($"Il2CppAssetBundleManager.LoadFromMemory returned null (bytes={bundleBytes}, il2cppBytes={il2cppBundleData.Length}, magic='{magic}').");

                    _shader = _bundle.LoadAsset<Shader>(ShaderAssetPath);
                    if (_shader == null)
                        _shader = _bundle.LoadAsset<Shader>(ShaderAssetPath.ToLowerInvariant());
                    if (_shader == null)
                        throw new Exception($"shader asset '{ShaderAssetPath}' was not found in the loaded bundle.");

                    _material = new Material(_shader) { name = "SonsVR_V47_DepthStereoDebug_Material" };
                    _available = true;
                    MelonLogger.Msg($"[StereoRender][AER V47D][DepthShader] THINGY loaded through MelonLoader IL2CPP bridge directly from embedded DLL memory: bundleBytes={bundleBytes} il2cppBytes={il2cppBundleData.Length} magic='{magic}' shader='{_shader.name}'. No temp file was created. Desktop-only depth board READY.");
                    return true;
                }
                catch (Exception e)
                {
                    _available = false;
                    LogFailure("initialization failed: " + e.Message);
                    return false;
                }
            }

            private static void EnsureDebugTargets(RenderTexture sourceEye)
            {
                int width = Mathf.Clamp(AerDepthShaderDebugV47PanelWidth, 256, 1024);
                int height = Mathf.Clamp(Mathf.RoundToInt(width * (float)sourceEye.height / Mathf.Max(1, sourceEye.width)), 256, 1200);

                EnsureTarget(ref _colorRT, width, height, "SonsVR_V47_FreshColor");
                EnsureTarget(ref _depthLowerLeftRT, width, height, "SonsVR_V47_Depth_LowerLeft");
                EnsureTarget(ref _depthCenteredRT, width, height, "SonsVR_V47_Depth_Centered");
                EnsureTarget(ref _depthRawRT, width, height, "SonsVR_V47_Depth_Raw");
            }

            private static void EnsureTarget(ref RenderTexture rt, int width, int height, string name)
            {
                if (rt != null && rt.width == width && rt.height == height && rt.IsCreated())
                    return;

                if (rt != null)
                {
                    try { rt.Release(); } catch { }
                    UnityEngine.Object.Destroy(rt);
                }

                rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    name = name,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                rt.Create();
            }

            private static void RenderDepthPanel(Texture depthTexture, Vector4 zParams, Vector4 uvScaleBias, RenderTexture target)
            {
                _material.SetTexture("_CameraDepthTexture", depthTexture);
                _material.SetVector("_ZBufferParams", zParams);
                _material.SetVector("_DepthUvScaleBias", uvScaleBias);
                _material.SetFloat("_DepthArraySlice", 0f);
                _material.SetFloat("_DepthMetersMax", Mathf.Max(0.1f, AerDepthShaderDebugV47MaxMeters));

                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = target;
                GL.Clear(false, true, Color.black);

                if (!_material.SetPass(0))
                    throw new Exception("Material.SetPass(0) returned false.");

                // The secretary-built debug shader intentionally treats POSITION as clip space.
                // Draw explicit [-1,+1] clip-space vertices rather than Graphics.Blit's internal
                // quad so this works regardless of how that Unity version constructs Blit geometry.
                GL.PushMatrix();
                GL.LoadIdentity();
                GL.Begin(GL.QUADS);
                GL.TexCoord2(0f, 0f); GL.Vertex3(-1f, -1f, 0f);
                GL.TexCoord2(1f, 0f); GL.Vertex3( 1f, -1f, 0f);
                GL.TexCoord2(1f, 1f); GL.Vertex3( 1f,  1f, 0f);
                GL.TexCoord2(0f, 1f); GL.Vertex3(-1f,  1f, 0f);
                GL.End();
                GL.PopMatrix();

                RenderTexture.active = previous;
            }

            private static void LogFailure(string message)
            {
                if (_failureLogsRemaining <= 0)
                    return;
                _failureLogsRemaining--;
                MelonLogger.Warning($"[StereoRender][AER V47D][DepthShader] {message} Headset rendering/submission unchanged. Logs remaining={_failureLogsRemaining}.");
            }
        }

        // ---------------------------------------------------------------------
        // ZER0INT V46 - PASSIVE HDRP DEPTH-ACCESS FEASIBILITY PROBE
        // ---------------------------------------------------------------------
        // Goal: prove we can obtain the SAME-FRAME depth buffer corresponding to the fresh HDRP
        // eye render before writing an actual cross-eye reprojection shader. HDRP exposes camera
        // depth to its own passes as _CameraDepthTexture. V38B EOF runs after this frame's eye
        // camera has rendered, so this is the least-invasive place to inspect it.
        //
        // IMPORTANT: no synchronous GPU readback. A tiny center patch is requested sparsely with
        // AsyncGPUReadback; later EOFs merely poll completion. Any failure is diagnostic only.
        private static class AerDepthAccessProbeV46
        {
            private static readonly string[] CandidateDepthTextureNames = new string[]
            {
                "_CameraDepthTexture",
                "_DepthPyramidTexture",
                "_CameraDepthTextureMipChain",
                "_DepthPyramidTextureMipChain"
            };

            private static bool _metadataLogged;
            private static float _nextRequestTime;
            private static bool _requestPending;
            private static AsyncGPUReadbackRequest _request;
            private static string _requestPropertyName;
            private static string _requestEye;
            private static int _requestFrame;
            private static int _requestX, _requestY, _requestWidth, _requestHeight;
            private static Vector4 _requestZBufferParams;
            private static int _readbackErrorLogsRemaining = 8;

            internal static void ResetForActivation(StereoRender self)
            {
                _metadataLogged = false;
                _nextRequestTime = 0f;
                _requestPending = false;
                _requestPropertyName = null;
                _requestEye = null;
                _requestFrame = -1;
                _readbackErrorLogsRemaining = 8;
            }

            internal static void OnCompletedRenderAtEof(StereoRender self, int completedEye)
            {
                if (!AerDepthAccessProbeV46Enabled || self == null || !AlternateEyeRenderingEnabled)
                    return;

                PollCompletedReadback();

                // Warmup/resync frames render BOTH eyes, so the final global binding is ambiguous.
                if (completedEye != 0 && completedEye != 1)
                    return;

                if (_requestPending || Time.unscaledTime < _nextRequestTime)
                    return;

                Camera cam = completedEye == 0 ? self.LeftCam : self.RightCam;
                if (cam == null || cam.targetTexture == null)
                    return;

                Texture depthTexture = null;
                string propertyName = null;

                for (int i = 0; i < CandidateDepthTextureNames.Length; i++)
                {
                    Texture candidate = Shader.GetGlobalTexture(CandidateDepthTextureNames[i]);
                    if (candidate == null) continue;

                    if (!_metadataLogged)
                        LogCandidateMetadata(cam, CandidateDepthTextureNames[i], candidate);

                    if (depthTexture == null)
                    {
                        depthTexture = candidate;
                        propertyName = CandidateDepthTextureNames[i];
                    }

                    if (CandidateDepthTextureNames[i] == "_CameraDepthTexture")
                    {
                        depthTexture = candidate;
                        propertyName = CandidateDepthTextureNames[i];
                        break;
                    }
                }

                _metadataLogged = true;
                _nextRequestTime = Time.unscaledTime + Mathf.Max(0.5f, AerDepthAccessProbeV46IntervalSeconds);

                if (depthTexture == null)
                {
                    if (_readbackErrorLogsRemaining > 0)
                    {
                        _readbackErrorLogsRemaining--;
                        MelonLogger.Warning("[StereoRender][AER V46][DepthTap] no HDRP depth texture was globally bound at fresh-eye EOF (_CameraDepthTexture and fallbacks all null). Rendering/submission unchanged.");
                    }
                    return;
                }

                int patch = Mathf.Clamp(AerDepthAccessProbeV46PatchSize, 8, 128);
                int width = Mathf.Min(patch, Mathf.Max(1, depthTexture.width));
                int height = Mathf.Min(patch, Mathf.Max(1, depthTexture.height));
                int x = Mathf.Max(0, (depthTexture.width - width) / 2);
                int y = Mathf.Max(0, (depthTexture.height - height) / 2);

                try
                {
                    _request = AsyncGPUReadback.Request(
                        depthTexture, 0,
                        x, width, y, height, 0, 1,
                        TextureFormat.RFloat, null);

                    _requestPending = true;
                    _requestPropertyName = propertyName;
                    _requestEye = completedEye == 0 ? "L" : "R";
                    _requestFrame = Time.frameCount;
                    _requestX = x;
                    _requestY = y;
                    _requestWidth = width;
                    _requestHeight = height;
                    _requestZBufferParams = Shader.GetGlobalVector("_ZBufferParams");
                }
                catch (Exception e)
                {
                    _requestPending = false;
                    if (_readbackErrorLogsRemaining > 0)
                    {
                        _readbackErrorLogsRemaining--;
                        MelonLogger.Warning($"[StereoRender][AER V46][DepthTap] AsyncGPUReadback request failed to start for {propertyName}: {e.Message}. Rendering/submission unchanged.");
                    }
                }
            }

            private static void PollCompletedReadback()
            {
                if (!_requestPending || !_request.done)
                    return;

                _requestPending = false;

                if (_request.hasError)
                {
                    if (_readbackErrorLogsRemaining > 0)
                    {
                        _readbackErrorLogsRemaining--;
                        MelonLogger.Warning($"[StereoRender][AER V46][DepthTap] readback ERROR property={_requestPropertyName} eye={_requestEye} sourceFrame={_requestFrame} region={_requestWidth}x{_requestHeight}@({_requestX},{_requestY}). Direct RFloat readback is unsupported on this path; rendering/submission unchanged.");
                    }
                    return;
                }

                try
                {
                    var data = _request.GetData<float>();
                    int count = data.Length;
                    if (count <= 0)
                    {
                        MelonLogger.Warning($"[StereoRender][AER V46][DepthTap] readback completed but returned zero samples property={_requestPropertyName} eye={_requestEye} sourceFrame={_requestFrame}.");
                        return;
                    }

                    float rawMin = float.PositiveInfinity;
                    float rawMax = float.NegativeInfinity;
                    double rawSum = 0.0;
                    int finiteCount = 0;
                    int nearZeroCount = 0;
                    int nearOneCount = 0;
                    float[] linearMeters = new float[count];
                    int linearCount = 0;

                    for (int i = 0; i < count; i++)
                    {
                        float z = data[i];
                        if (float.IsNaN(z) || float.IsInfinity(z)) continue;

                        finiteCount++;
                        rawMin = Mathf.Min(rawMin, z);
                        rawMax = Mathf.Max(rawMax, z);
                        rawSum += z;
                        if (z <= 1e-6f) nearZeroCount++;
                        if (z >= 0.999999f) nearOneCount++;

                        float denom = _requestZBufferParams.z * z + _requestZBufferParams.w;
                        if (Mathf.Abs(denom) > 1e-12f)
                        {
                            float meters = 1.0f / denom;
                            if (!float.IsNaN(meters) && !float.IsInfinity(meters) && meters > 0f)
                                linearMeters[linearCount++] = meters;
                        }
                    }

                    if (finiteCount == 0)
                    {
                        MelonLogger.Warning($"[StereoRender][AER V46][DepthTap] readback contained no finite RFloat samples property={_requestPropertyName} eye={_requestEye} sourceFrame={_requestFrame}.");
                        return;
                    }

                    float linearMin = -1f, median = -1f, p90 = -1f, linearMax = -1f;
                    if (linearCount > 0)
                    {
                        Array.Sort(linearMeters, 0, linearCount);
                        linearMin = linearMeters[0];
                        linearMax = linearMeters[linearCount - 1];
                        median = linearMeters[Mathf.Clamp((int)(linearCount * 0.50f), 0, linearCount - 1)];
                        p90 = linearMeters[Mathf.Clamp((int)(linearCount * 0.90f), 0, linearCount - 1)];
                    }

                    string validity = AerGameplaySpatialTelemetryTarpMarkerSeen ? "POST_TARP" : "PRE_TARP";
                    float rawMean = (float)(rawSum / finiteCount);
                    float zeroPct = 100f * nearZeroCount / finiteCount;
                    float onePct = 100f * nearOneCount / finiteCount;

                    MelonLogger.Msg($"[StereoRender][AER V46][DepthTap] validity={validity} property={_requestPropertyName} eye={_requestEye} sourceFrame={_requestFrame} region={_requestWidth}x{_requestHeight} finite={finiteCount}/{count} rawZ(min/mean/max)={rawMin:F6}/{rawMean:F6}/{rawMax:F6} endpointPct(zero/one)={zeroPct:F1}/{onePct:F1} linearEyeMeters(min/median/p90/max)={linearMin:F3}/{median:F3}/{p90:F3}/{linearMax:F3} zParams=({_requestZBufferParams.x:F4},{_requestZBufferParams.y:F4},{_requestZBufferParams.z:F4},{_requestZBufferParams.w:F4})");
                }
                catch (Exception e)
                {
                    if (_readbackErrorLogsRemaining > 0)
                    {
                        _readbackErrorLogsRemaining--;
                        MelonLogger.Warning($"[StereoRender][AER V46][DepthTap] readback parsing failed: {e.Message}. Rendering/submission unchanged.");
                    }
                }
            }

            private static void LogCandidateMetadata(Camera cam, string propertyName, Texture texture)
            {
                try
                {
                    RenderTexture rt = texture as RenderTexture;
                    string rtInfo = rt != null ? $" rtAA={rt.antiAliasing} depthBits={rt.depth} created={rt.IsCreated()}" : "";
                    MelonLogger.Msg($"[StereoRender][AER V46][DepthTap][Candidate] property={propertyName} type={texture.GetType().FullName} name='{texture.name}' size={texture.width}x{texture.height} dimension={texture.dimension} graphicsFormat={texture.graphicsFormat}{rtInfo} freshCam={cam.name} eyeTarget={cam.targetTexture.width}x{cam.targetTexture.height} frame={Time.frameCount}");
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[StereoRender][AER V46][DepthTap][Candidate] metadata log failed for {propertyName}: {e.Message}");
                }
            }
        }

        // ---------------------------------------------------------------------
        // ZER0INT V44 - PASSIVE GAMEPLAY STALE-EYE SPATIAL TELEMETRY
        // ---------------------------------------------------------------------
        // Nested managed helper: no new methods are exposed on the injected MonoBehaviour's
        // IL2CPP registration surface. Critically, this does NOT call WaitGetPoses/OpenVR, touch
        // camera enablement, allocate RenderTextures, change reprojection, or alter submission.
        // It only reads transforms that already exist after rendering and accumulates numbers.
        private static class AerGameplaySpatialTelemetryV44
        {
            // Keep ALL V44 per-eye snapshot state inside this nested managed helper rather than
            // adding fields to the injected StereoRender MonoBehaviour. V33 taught us to keep the
            // IL2CPP registration surface as boring and unchanged as possible.
            private static Vector3 _leftRenderedWorldPos, _rightRenderedWorldPos;
            private static Quaternion _leftRenderedWorldRot = Quaternion.identity, _rightRenderedWorldRot = Quaternion.identity;
            private static Vector3 _leftRenderedHeadWorldPos, _rightRenderedHeadWorldPos;
            private static Vector3 _leftRenderedRigWorldPos, _rightRenderedRigWorldPos;
            private static float _leftRenderedRealtime, _rightRenderedRealtime;
            private static bool _leftRenderedTransformValid, _rightRenderedTransformValid;

            private static bool _markerEpochActive;
            private static float _nextLogTime;
            private static int _n;
            private static int _lastStaleEye = -1;

            private static double _sumAgeMs2, _sumEyeMm2, _sumRotDeg2, _sumHeadMm2, _sumRigMm2;
            private static double _sumLateralMm2, _sumForwardMm2;
            private static double _sumPx05m2, _sumPx1m2, _sumPx2m2, _sumPx5m2, _sumPx10m2;
            private static float _maxAgeMs, _maxEyeMm, _maxRotDeg, _maxHeadMm, _maxRigMm;
            private static float _maxLateralMm, _maxForwardMm;
            private static float _maxPx05m, _maxPx1m, _maxPx2m, _maxPx5m, _maxPx10m;

            internal static void ResetForActivation(StereoRender self)
            {
                _leftRenderedTransformValid = false;
                _rightRenderedTransformValid = false;
                _leftRenderedRealtime = 0f;
                _rightRenderedRealtime = 0f;
                _leftRenderedWorldRot = Quaternion.identity;
                _rightRenderedWorldRot = Quaternion.identity;

                _markerEpochActive = false;
                _nextLogTime = Time.unscaledTime + Mathf.Max(0.25f, AerGameplaySpatialTelemetryIntervalSeconds);
                ResetWindow();
            }

            private static void ResetWindow()
            {
                _n = 0;
                _lastStaleEye = -1;
                _sumAgeMs2 = _sumEyeMm2 = _sumRotDeg2 = _sumHeadMm2 = _sumRigMm2 = 0.0;
                _sumLateralMm2 = _sumForwardMm2 = 0.0;
                _sumPx05m2 = _sumPx1m2 = _sumPx2m2 = _sumPx5m2 = _sumPx10m2 = 0.0;
                _maxAgeMs = _maxEyeMm = _maxRotDeg = _maxHeadMm = _maxRigMm = 0f;
                _maxLateralMm = _maxForwardMm = 0f;
                _maxPx05m = _maxPx1m = _maxPx2m = _maxPx5m = _maxPx10m = 0f;
            }

            private static void HandleValidityMarkerIfNeeded()
            {
                if (!AerGameplaySpatialTelemetryTarpMarkerSeen || _markerEpochActive) return;

                _markerEpochActive = true;
                ResetWindow();
                _nextLogTime = Time.unscaledTime + Mathf.Max(0.25f, AerGameplaySpatialTelemetryIntervalSeconds);
                MelonLogger.Warning("[StereoRender][AER V44][VALIDITY] first crafting-tarp open observed. Telemetry accumulator reset NOW; every V44 summary after this marker is POST_TARP gameplay-valid. Pre-marker summaries remain diagnostic context only.");
            }

            internal static void OnCompletedRenderAtEof(StereoRender self, int completedEye)
            {
                if (!AerGameplaySpatialTelemetryV44Enabled || self == null || !AlternateEyeRenderingEnabled)
                    return;

                HandleValidityMarkerIfNeeded();

                // -1 means a warmup/resync frame where both eyes were genuinely rendered. It is
                // useful only to refresh the baseline; there is no stale eye to score.
                if (completedEye == -1)
                {
                    SnapshotRenderedEye(self, 0);
                    SnapshotRenderedEye(self, 1);
                    return;
                }

                if (completedEye != 0 && completedEye != 1)
                    return;

                int staleEye = completedEye == 0 ? 1 : 0;
                SampleStaleEye(self, staleEye);
                SnapshotRenderedEye(self, completedEye);
                MaybeLog(self);
            }

            private static void SnapshotRenderedEye(StereoRender self, int eye)
            {
                Camera cam = eye == 0 ? self.LeftCam : self.RightCam;
                if (cam == null || self.Head == null) return;

                Vector3 pos = cam.transform.position;
                Quaternion rot = cam.transform.rotation;
                Vector3 headPos = self.Head.position;
                Vector3 rigPos = self.transform.position;
                float now = Time.realtimeSinceStartup;

                if (eye == 0)
                {
                    _leftRenderedWorldPos = pos;
                    _leftRenderedWorldRot = rot;
                    _leftRenderedHeadWorldPos = headPos;
                    _leftRenderedRigWorldPos = rigPos;
                    _leftRenderedRealtime = now;
                    _leftRenderedTransformValid = true;
                }
                else
                {
                    _rightRenderedWorldPos = pos;
                    _rightRenderedWorldRot = rot;
                    _rightRenderedHeadWorldPos = headPos;
                    _rightRenderedRigWorldPos = rigPos;
                    _rightRenderedRealtime = now;
                    _rightRenderedTransformValid = true;
                }
            }

            private static void SampleStaleEye(StereoRender self, int staleEye)
            {
                Camera cam = staleEye == 0 ? self.LeftCam : self.RightCam;
                if (cam == null || self.Head == null) return;

                bool valid = staleEye == 0
                    ? _leftRenderedTransformValid
                    : _rightRenderedTransformValid;
                if (!valid) return;

                Vector3 oldPos = staleEye == 0 ? _leftRenderedWorldPos : _rightRenderedWorldPos;
                Quaternion oldRot = staleEye == 0 ? _leftRenderedWorldRot : _rightRenderedWorldRot;
                Vector3 oldHead = staleEye == 0 ? _leftRenderedHeadWorldPos : _rightRenderedHeadWorldPos;
                Vector3 oldRig = staleEye == 0 ? _leftRenderedRigWorldPos : _rightRenderedRigWorldPos;
                float oldTime = staleEye == 0 ? _leftRenderedRealtime : _rightRenderedRealtime;

                Vector3 curPos = cam.transform.position;
                Quaternion curRot = cam.transform.rotation;
                Vector3 deltaWorld = curPos - oldPos;
                Vector3 deltaCam = Quaternion.Inverse(curRot) * deltaWorld;

                float ageMs = Mathf.Max(0f, (Time.realtimeSinceStartup - oldTime) * 1000f);
                float eyeMm = deltaWorld.magnitude * 1000f;
                float rotDeg = Quaternion.Angle(oldRot, curRot);
                float headMm = Vector3.Distance(oldHead, self.Head.position) * 1000f;
                float rigMm = Vector3.Distance(oldRig, self.transform.position) * 1000f;
                float lateralMm = Mathf.Sqrt(deltaCam.x * deltaCam.x + deltaCam.y * deltaCam.y) * 1000f;
                float forwardMm = Mathf.Abs(deltaCam.z) * 1000f;

                Matrix4x4 p = cam.projectionMatrix;
                float fx = Mathf.Abs(p.m00) * Mathf.Max(1, cam.targetTexture != null ? cam.targetTexture.width : self.currentWidth) * 0.5f;
                float fy = Mathf.Abs(p.m11) * Mathf.Max(1, cam.targetTexture != null ? cam.targetTexture.height : self.currentHeight) * 0.5f;

                float px05 = TranslationPixelsAtDepth(deltaCam, fx, fy, 0.5f);
                float px1 = TranslationPixelsAtDepth(deltaCam, fx, fy, 1.0f);
                float px2 = TranslationPixelsAtDepth(deltaCam, fx, fy, 2.0f);
                float px5 = TranslationPixelsAtDepth(deltaCam, fx, fy, 5.0f);
                float px10 = TranslationPixelsAtDepth(deltaCam, fx, fy, 10.0f);

                _n++;
                _lastStaleEye = staleEye;
                AddSquared(ref _sumAgeMs2, ageMs);      _maxAgeMs = Mathf.Max(_maxAgeMs, ageMs);
                AddSquared(ref _sumEyeMm2, eyeMm);      _maxEyeMm = Mathf.Max(_maxEyeMm, eyeMm);
                AddSquared(ref _sumRotDeg2, rotDeg);    _maxRotDeg = Mathf.Max(_maxRotDeg, rotDeg);
                AddSquared(ref _sumHeadMm2, headMm);    _maxHeadMm = Mathf.Max(_maxHeadMm, headMm);
                AddSquared(ref _sumRigMm2, rigMm);      _maxRigMm = Mathf.Max(_maxRigMm, rigMm);
                AddSquared(ref _sumLateralMm2, lateralMm); _maxLateralMm = Mathf.Max(_maxLateralMm, lateralMm);
                AddSquared(ref _sumForwardMm2, forwardMm); _maxForwardMm = Mathf.Max(_maxForwardMm, forwardMm);
                AddSquared(ref _sumPx05m2, px05);       _maxPx05m = Mathf.Max(_maxPx05m, px05);
                AddSquared(ref _sumPx1m2, px1);         _maxPx1m = Mathf.Max(_maxPx1m, px1);
                AddSquared(ref _sumPx2m2, px2);         _maxPx2m = Mathf.Max(_maxPx2m, px2);
                AddSquared(ref _sumPx5m2, px5);         _maxPx5m = Mathf.Max(_maxPx5m, px5);
                AddSquared(ref _sumPx10m2, px10);       _maxPx10m = Mathf.Max(_maxPx10m, px10);
            }

            private static void AddSquared(ref double sum, float value)
            {
                sum += (double)value * value;
            }

            private static float TranslationPixelsAtDepth(Vector3 cameraTranslation, float fx, float fy, float depthMeters)
            {
                if (depthMeters <= 0f) return 0f;
                float x = fx * cameraTranslation.x / depthMeters;
                float y = fy * cameraTranslation.y / depthMeters;
                return Mathf.Sqrt(x * x + y * y);
            }

            private static float Rms(double sumSquares)
            {
                return _n > 0 ? Mathf.Sqrt((float)(sumSquares / _n)) : 0f;
            }

            private static void MaybeLog(StereoRender self)
            {
                if (_n <= 0 || Time.unscaledTime < _nextLogTime) return;
                _nextLogTime = Time.unscaledTime + Mathf.Max(0.25f, AerGameplaySpatialTelemetryIntervalSeconds);

                string validity = _markerEpochActive ? "POST_TARP" : "PRE_TARP";
                string stale = _lastStaleEye == 0 ? "L" : _lastStaleEye == 1 ? "R" : "?";

                MelonLogger.Msg($"[StereoRender][AER V44][GameplaySpatial] validity={validity} n={_n} lastStale={stale} ageMs(rms/max)={Rms(_sumAgeMs2):F1}/{_maxAgeMs:F1} eyeDeltaMm={Rms(_sumEyeMm2):F2}/{_maxEyeMm:F2} rotDeg={Rms(_sumRotDeg2):F3}/{_maxRotDeg:F3} headWorldMm={Rms(_sumHeadMm2):F2}/{_maxHeadMm:F2} rigWorldMm={Rms(_sumRigMm2):F2}/{_maxRigMm:F2} lateralMm={Rms(_sumLateralMm2):F2}/{_maxLateralMm:F2} forwardMm={Rms(_sumForwardMm2):F2}/{_maxForwardMm:F2}");
                MelonLogger.Msg($"[StereoRender][AER V44][DepthPx] validity={validity} translationOnlyPx(rms/max) @0.5m={Rms(_sumPx05m2):F1}/{_maxPx05m:F1} @1m={Rms(_sumPx1m2):F1}/{_maxPx1m:F1} @2m={Rms(_sumPx2m2):F1}/{_maxPx2m:F1} @5m={Rms(_sumPx5m2):F1}/{_maxPx5m:F1} @10m={Rms(_sumPx10m2):F1}/{_maxPx10m:F1} @inf=0.0 (camera-translation component only; V36 rotation correction not included)");

                ResetWindow();
            }
        }

        /// <summary>
        /// Called by VRPlayer whenever Sons genuinely swaps its native source camera (for example
        /// MainCameraFP &lt;-&gt; InventoryCamera). AER briefly renders both eyes so a stale texture from
        /// the previous camera context cannot linger in one eye across the transition.
        /// </summary>
        // V112: backend truth for the in-HMD telemetry. "2E" means both eye cameras are
        // genuinely rendered this frame. "2E/UI" is the temporary pause/settings pin, and
        // "2E/WARM" is the safe both-eye warmup before returning to alternating AER.
        internal string GetActualRenderModeV112()
        {
            if (!AerGameplayReadyForActivation)
                return "2E/PRE";
            if (!AlternateEyeRenderingEnabled)
                return "2E";
            if (_aerUiFullStereoWasForcedV111)
                return "2E/UI";
            if (_aerWarmupFramesRemaining > 0 || _aerScheduledEye == -1)
                return "2E/WARM";
            if (_aerScheduledEye == 0 || _aerScheduledEye == 1)
                return "AER";
            return "AER";
        }

        public void RequestAerResyncV33(string reason)
        {
            ResetAerStateV34(this, AerCameraChangeWarmupFrames, reason ?? "camera change",
                invalidateCompletedPoses: true, log: AlternateEyeRenderingEnabled);
        }

        private static void ResetAerStateV34(StereoRender self, int warmupFrames, string reason, bool invalidateCompletedPoses, bool log)
        {
            self._aerWarmupFramesRemaining = Mathf.Max(1, warmupFrames);
            self._aerScheduledEye = -2;
            self._aerScheduledPoseValid = false;
            self._aerScheduledFrame = -1;
            self._aerNextEye = 0;
            self._aerLastCompletedEye = -1;
            self._aerLastEnabledState = AlternateEyeRenderingEnabled;

            if (invalidateCompletedPoses)
            {
                self._aerLeftRenderPoseValid = false;
                self._aerRightRenderPoseValid = false;
                self._aerLeftRenderFrame = -1;
                self._aerRightRenderFrame = -1;
            }

            if (self.LeftCam != null) self.LeftCam.enabled = true;
            if (self.RightCam != null) self.RightCam.enabled = true;

            if (log && AlternateEyeRenderingEnabled)
                MelonLogger.Msg($"[StereoRender][AER V36] resync: {reason}; rendering both eyes for {self._aerWarmupFramesRemaining} frame(s) before alternating.");
        }

        private static void CommitPreviouslyScheduledAerRenderV34(StereoRender self)
        {
            if (!self._aerScheduledPoseValid) return;

            // Rendering of a schedule created in LateUpdate(frame N) only completes later in
            // that frame. Never mark it as completed while Time.frameCount is still N. This
            // frame tag makes the bookkeeping correct regardless of whether MelonLoader's
            // OnLateUpdate callback happens before or after this MonoBehaviour's LateUpdate.
            if (self._aerScheduledFrame < 0 || self._aerScheduledFrame >= Time.frameCount) return;

            if (self._aerScheduledEye == -1 || self._aerScheduledEye == 0)
            {
                self._aerLeftRenderPose = self._aerScheduledPose;
                self._aerLeftRenderPoseValid = true;
                self._aerLeftRenderFrame = self._aerScheduledFrame;
            }
            if (self._aerScheduledEye == -1 || self._aerScheduledEye == 1)
            {
                self._aerRightRenderPose = self._aerScheduledPose;
                self._aerRightRenderPoseValid = true;
                self._aerRightRenderFrame = self._aerScheduledFrame;
            }

            self._aerLastCompletedEye = self._aerScheduledEye;
            self._aerScheduledPoseValid = false;
            self._aerScheduledFrame = -1;
        }

        private static void ScheduleAerRenderForThisFrameV34(StereoRender self, TrackedDevicePose_t hmdPose)
        {
            if (self.LeftCam == null || self.RightCam == null) return;

            // If AER is off, preserve the original renderer exactly: both ordinary cameras render
            // every frame and Submit_Default is used by StereoRenderPass.
            if (!AlternateEyeRenderingEnabled)
            {
                self.LeftCam.enabled = true;
                self.RightCam.enabled = true;
                self._aerScheduledEye = -2;
                self._aerScheduledPoseValid = false;
                self._aerScheduledFrame = -1;
                return;
            }

            // Don't create a one-eyed frame from an invalid tracking sample. Falling back to a
            // normal two-eye render for that frame is expensive but safe. Also invalidate stored
            // render poses and restart the short warmup so pose-aware submission cannot later pair
            // a texture from this untracked frame with an older tracked pose.
            if (!hmdPose.bPoseIsValid)
            {
                ResetAerStateV34(self, AerCameraChangeWarmupFrames, "invalid HMD pose",
                    invalidateCompletedPoses: true, log: false);
                return;
            }

            HmdMatrix34_t renderPose = BuildAerRenderPoseV33(hmdPose.mDeviceToAbsoluteTracking);

            // V111 MENU PIN: pause/settings are configuration states, not performance-critical
            // gameplay. Render both eyes genuinely while either is open so text/UI cannot inherit
            // one-frame AER prediction error. This does NOT run merely because the always-on FPS
            // overlay is visible; doing that would defeat AER during normal gameplay.
            // V112: V111 used the broad PauseMenu hierarchy signal here. That signal can remain
            // alive after the visible menu is gone, which can strand normal gameplay in expensive
            // 2E without any obvious UI. In single-player, timeScale==0 is the strict pause truth.
            // Our own settings panel is explicit and always forces 2E while visible.
            bool forceUiFullStereoV111 = VRPlayer.IsTimeScalePausedV112 || RuntimeSettingsV105.IsPanelOpenV111;
            if (forceUiFullStereoV111)
            {
                self.LeftCam.enabled = true;
                self.RightCam.enabled = true;
                self._aerScheduledEye = -1;
                self._aerScheduledPose = renderPose;
                self._aerScheduledPoseValid = true;
                self._aerScheduledFrame = Time.frameCount;
                if (!self._aerUiFullStereoWasForcedV111)
                {
                    self._aerUiFullStereoWasForcedV111 = true;
                    MelonLogger.Msg("[StereoRender][V112][UiPin] single-player pause/settings active -> genuine 2E rendering; AER synthesis bypassed for UI stability.");
                }
                return;
            }

            if (self._aerUiFullStereoWasForcedV111)
            {
                self._aerUiFullStereoWasForcedV111 = false;
                ResetAerStateV34(self, AerCameraChangeWarmupFrames,
                    "V112 pause/settings UI pin ended", invalidateCompletedPoses: true, log: true);
                // ResetAerStateV34 arms warmup and enables both cameras. Continue below so THIS
                // frame receives a valid BOTH-eye schedule/pose rather than losing one submit.
            }

            if (self._aerWarmupFramesRemaining > 0)
            {
                self.LeftCam.enabled = true;
                self.RightCam.enabled = true;
                self._aerScheduledEye = -1;
                self._aerScheduledPose = renderPose;
                self._aerScheduledPoseValid = true;
                self._aerScheduledFrame = Time.frameCount;
                self._aerWarmupFramesRemaining--;

                if (!self._aerStartupAnnouncementDone && self._aerWarmupFramesRemaining == 0)
                {
                    self._aerStartupAnnouncementDone = true;
                    MelonLogger.Msg("[StereoRender][AER V36] warmup complete; now alternating one HDRP eye camera per Unity frame (L/R/L/R...).");
                }
                return;
            }

            // V99: occasional BOTH-eye truth frame for offline diagnostics. This happens only
            // after ordinary AER warmup. The helper snapshots both stale same-eye histories before
            // either camera can overwrite them, then schedules both cameras for this one frame.
            if (AerDepthStereoWarpV48.TryScheduleTruthFrameV99(self, renderPose))
                return;

            bool renderLeft = self._aerNextEye == 0;
            self.LeftCam.enabled = renderLeft;
            self.RightCam.enabled = !renderLeft;
            self._aerScheduledEye = renderLeft ? 0 : 1;
            self._aerScheduledPose = renderPose;
            self._aerScheduledPoseValid = true;
            self._aerScheduledFrame = Time.frameCount;
            self._aerNextEye = renderLeft ? 1 : 0;
        }

        /// <summary>
        /// This fork renders HMD ROTATION but deliberately not room-scale HMD translation: Head's
        /// local position stays zero and the mod moves the separate Origin from game logic instead.
        /// VRTextureWithPose_t must describe the pose actually used to render the texture, so keep
        /// OpenVR's tracking-space rotation but zero its translation. Otherwise SteamVR would try
        /// to reproject from a physical head position that the Unity eye cameras never used.
        /// </summary>
        private static HmdMatrix34_t BuildAerRenderPoseV33(HmdMatrix34_t rawPose)
        {
            rawPose.m3 = 0f;
            rawPose.m7 = 0f;
            rawPose.m11 = 0f;
            return rawPose;
        }

        // ---------------------------------------------------------------------
        // ZER0INT V37 - HARD TEMPORAL/POST-FX KILL + EFFECTIVE-STATE DIAGNOSTICS
        // ---------------------------------------------------------------------
        // Kept in a nested managed helper rather than adding more exotic method signatures to the
        // injected StereoRender MonoBehaviour itself. V33 already taught us that Il2CppInterop will
        // happily inspect helper signatures during ClassInjector.RegisterTypeInIl2Cpp and then face-
        // plant on types it cannot expose. This helper is never registered as an IL2CPP component.
        private static class AerDiagnosticsV37
        {
            private static readonly System.Collections.Generic.HashSet<string> TargetVolumeComponentNames =
                new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "MotionBlur",
                    "DepthOfField",
                    "ChromaticAberration",
                    "FilmGrain",
                    "Bloom",
                    "LensDistortion",
                    "PaniniProjection",
                    "Vignette",
                    "ScreenSpaceReflection",
                    "ScreenSpaceAmbientOcclusion",
                    "ContactShadows",
                    "MicroShadowing"
                };

            private static readonly System.Collections.Generic.HashSet<int> LoggedVolumeComponents =
                new System.Collections.Generic.HashSet<int>();

            private static float _nextVolumeSweepTime;
            private static float _nextTelemetryTime;
            private static bool _hdCameraReflectionFailureLogged;

            internal static void ForceCinematicVolumeEffectsOff(StereoRender self)
            {
                if (!AlternateEyeRenderingEnabled || !AerV37ForceCinematicVolumeEffectsOff) return;
                if (Time.unscaledTime < _nextVolumeSweepTime) return;
                _nextVolumeSweepTime = Time.unscaledTime + 0.25f;

                try
                {
                    Volume[] volumes = UnityEngine.Object.FindObjectsOfType<Volume>();
                    int touched = 0;
                    int matched = 0;

                    foreach (Volume volume in volumes)
                    {
                        if (volume == null || !volume.enabled || !volume.gameObject.activeInHierarchy) continue;
                        VolumeProfile profile = volume.profile;
                        if (profile == null || profile.components == null) continue;

                        foreach (VolumeComponent component in profile.components)
                        {
                            if (component == null) continue;

                            string runtimeTypeName = component.GetType().Name;
                            string objectName = component.name ?? string.Empty;
                            bool target = TargetVolumeComponentNames.Contains(runtimeTypeName) ||
                                          TargetVolumeComponentNames.Contains(objectName);
                            if (!target) continue;

                            matched++;
                            bool wasActive = component.active;
                            component.active = false;
                            if (wasActive) touched++;

                            int id = component.GetInstanceID();
                            if (LoggedVolumeComponents.Add(id))
                            {
                                MelonLogger.Msg($"[StereoRender][AER V37][VolumeKill] volume='{volume.name}' profile='{profile.name}' componentType='{runtimeTypeName}' componentName='{objectName}' activeBefore={wasActive} -> activeNow={component.active}");
                            }
                        }
                    }

                    // Low-frequency heartbeat proves the sweep is still finding the same runtime
                    // state after scene/camera changes without flooding one line per component.
                    if (matched > 0 && Time.frameCount % 120 == 0)
                        MelonLogger.Msg($"[StereoRender][AER V37][VolumeKill] sweep matched={matched} newlyDisabledThisSweep={touched} uniqueSeen={LoggedVolumeComponents.Count}");
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[StereoRender][AER V37][VolumeKill] sweep failed: {e.Message}");
                }
            }

            private static string GetEffectiveHDCameraSummary(Camera cam)
            {
                if (cam == null) return "camera=null";

                try
                {
                    HDCamera hd = HDCamera.GetOrCreate(cam, 0);
                    if (hd == null) return "HDCamera=null";

                    Type hdType = hd.GetType();
                    BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                    string aa = "?";
                    PropertyInfo aaProp = hdType.GetProperty("antialiasing", flags);
                    if (aaProp != null)
                    {
                        object aaValue = aaProp.GetValue(hd);
                        aa = aaValue != null ? aaValue.ToString() : "null";
                    }

                    string frame = "frameSettings=?";
                    PropertyInfo fsProp = hdType.GetProperty("frameSettings", flags);
                    if (fsProp != null)
                    {
                        object boxed = fsProp.GetValue(hd);
                        if (boxed is FrameSettings fs)
                        {
                            frame =
                                $"MB={fs.IsEnabled(FrameSettingsField.MotionBlur)} " +
                                $"DoF={fs.IsEnabled(FrameSettingsField.DepthOfField)} " +
                                $"Dist={fs.IsEnabled(FrameSettingsField.Distortion)} " +
                                $"CA={fs.IsEnabled(FrameSettingsField.ChromaticAberration)} " +
                                $"SSR={fs.IsEnabled(FrameSettingsField.SSR)} " +
                                $"SSAO={fs.IsEnabled(FrameSettingsField.SSAO)} " +
                                $"Contact={fs.IsEnabled(FrameSettingsField.ContactShadows)} " +
                                $"Vol={fs.IsEnabled(FrameSettingsField.Volumetrics)} " +
                                $"VolReproj={fs.IsEnabled(FrameSettingsField.ReprojectionForVolumetrics)}";
                        }
                    }

                    HDAdditionalCameraData data = cam.GetComponent<HDAdditionalCameraData>();
                    string additional = data == null
                        ? "HDAdditionalCameraData=null"
                        : $"dataAA={data.antialiasing} DLSSallow={data.allowDeepLearningSuperSampling} dynAllow={data.allowDynamicResolution} customFS={data.customRenderingSettings}";

                    return $"HDCameraAA={aa} {additional} {frame}";
                }
                catch (Exception e)
                {
                    if (!_hdCameraReflectionFailureLogged)
                    {
                        _hdCameraReflectionFailureLogged = true;
                        MelonLogger.Warning($"[StereoRender][AER V37] effective HDCamera diagnostic failed: {e.Message}");
                    }
                    return "HDCameraDiag=FAILED";
                }
            }

            internal static void LogPoseAndEffectiveHdrpState(StereoRender self)
            {
                if (!AlternateEyeRenderingEnabled) return;
                if (Time.unscaledTime < _nextTelemetryTime) return;
                _nextTelemetryTime = Time.unscaledTime + Mathf.Max(0.1f, AerV37TelemetryIntervalSeconds);

                float leftAngle = -1f;
                float rightAngle = -1f;
                if (self._aerLatestTrackingPoseValid && self._aerLeftRenderPoseValid)
                    leftAngle = Quaternion.Angle(self._aerLeftRenderPose.GetRotation(), self._aerLatestTrackingPose.GetRotation());
                if (self._aerLatestTrackingPoseValid && self._aerRightRenderPoseValid)
                    rightAngle = Quaternion.Angle(self._aerRightRenderPose.GetRotation(), self._aerLatestTrackingPose.GetRotation());

                int leftAge = self._aerLeftRenderFrame >= 0 ? Time.frameCount - self._aerLeftRenderFrame : -1;
                int rightAge = self._aerRightRenderFrame >= 0 ? Time.frameCount - self._aerRightRenderFrame : -1;
                string completed = self._aerLastCompletedEye == 0 ? "L" : self._aerLastCompletedEye == 1 ? "R" : self._aerLastCompletedEye == -1 ? "BOTH" : "none";
                string scheduled = self._aerScheduledEye == 0 ? "L" : self._aerScheduledEye == 1 ? "R" : self._aerScheduledEye == -1 ? "BOTH" : "none";

                MelonLogger.Msg(
                    $"[StereoRender][AER V37][Pose] frame={Time.frameCount} dt={Time.unscaledDeltaTime * 1000f:F1}ms " +
                    $"latestPoseFrame={self._aerLatestTrackingPoseFrame} lastCompleted={completed} scheduled={scheduled}@{self._aerScheduledFrame} " +
                    $"Lage={leftAge}f LcorrToLatest={leftAngle:F3}deg Rage={rightAge}f RcorrToLatest={rightAngle:F3}deg " +
                    $"camEnabled=L:{(self.LeftCam != null && self.LeftCam.enabled)} R:{(self.RightCam != null && self.RightCam.enabled)}");

                MelonLogger.Msg($"[StereoRender][AER V37][Effective] LEFT  {GetEffectiveHDCameraSummary(self.LeftCam)}");
                MelonLogger.Msg($"[StereoRender][AER V37][Effective] RIGHT {GetEffectiveHDCameraSummary(self.RightCam)}");

                // Ask the compositor for its own timing counters too. This is far more useful than
                // inferring reprojection from visual vibes: it tells us how much SteamVR is already
                // synthesizing/reprojecting because this game is frequently far below the Quest 3's
                // 72 Hz display cadence. The SteamVR logs from V35/V36 already showed very large
                // reprojected-frame totals plus D3D11 AcquireSync timeouts; this makes those counters
                // available in the same experiment log and aligned with our AER pose samples.
                try
                {
                    Compositor_FrameTiming timing = new Compositor_FrameTiming();
                    timing.m_nSize = (uint)Marshal.SizeOf(typeof(Compositor_FrameTiming));
                    if (OpenVR.Compositor != null && OpenVR.Compositor.GetFrameTiming(ref timing, 0))
                    {
                        MelonLogger.Msg(
                            $"[StereoRender][AER V37][CompositorFrame] idx={timing.m_nFrameIndex} presents={timing.m_nNumFramePresents} " +
                            $"dropped={timing.m_nNumDroppedFrames} misPresented={timing.m_nNumMisPresented} " +
                            $"clientInterval={timing.m_flClientFrameIntervalMs:F2}ms totalRenderGpu={timing.m_flTotalRenderGpuMs:F2}ms " +
                            $"preSubmitGpu={timing.m_flPreSubmitGpuMs:F2}ms postSubmitGpu={timing.m_flPostSubmitGpuMs:F2}ms " +
                            $"waitGetPosesCalled={timing.m_flWaitGetPosesCalledMs:F2}ms submitFrame={timing.m_flSubmitFrameMs:F2}ms reprojFlags=0x{timing.m_nReprojectionFlags:X}");

                        // Reprojected-frame totals are not part of Compositor_FrameTiming in this
                        // SteamVR/OpenVR C# wrapper. They live in Compositor_CumulativeStats.
                        // Query that struct separately rather than assuming a newer/native field
                        // layout on Compositor_FrameTiming (which caused the original V37 compile
                        // error against the user's bundled SonsSteamVR_IL2CPP.dll).
                        Compositor_CumulativeStats cumulative = new Compositor_CumulativeStats();
                        OpenVR.Compositor.GetCumulativeStats(
                            ref cumulative,
                            (uint)Marshal.SizeOf(typeof(Compositor_CumulativeStats)));

                        MelonLogger.Msg(
                            $"[StereoRender][AER V37][CompositorCumulative] pid={cumulative.m_nPid} " +
                            $"presents={cumulative.m_nNumFramePresents} dropped={cumulative.m_nNumDroppedFrames} " +
                            $"reprojected={cumulative.m_nNumReprojectedFrames} " +
                            $"startup(P/D/R)={cumulative.m_nNumFramePresentsOnStartup}/{cumulative.m_nNumDroppedFramesOnStartup}/{cumulative.m_nNumReprojectedFramesOnStartup} " +
                            $"loading(P/D/R)={cumulative.m_nNumFramePresentsLoading}/{cumulative.m_nNumDroppedFramesLoading}/{cumulative.m_nNumReprojectedFramesLoading} " +
                            $"timeout(P/D/R)={cumulative.m_nNumFramePresentsTimedOut}/{cumulative.m_nNumDroppedFramesTimedOut}/{cumulative.m_nNumReprojectedFramesTimedOut}");
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[StereoRender][AER V37][Compositor] GetFrameTiming failed: {e.Message}");
                }
            }

            internal static void LogSubmitOrdering(StereoRender self)
            {
                if (!AlternateEyeRenderingEnabled) return;
                // This answers an unresolved architecture question from V33-V36 directly: is the
                // MelonMod.OnLateUpdate submission happening before or after StereoRender.LateUpdate
                // (which performs WaitGetPoses and schedules this frame's eye)? A single bit in each
                // telemetry sample tells us whether Submit is using this frame's tracking sample or
                // the previous one.
                if (Time.unscaledTime + 0.001f < _nextTelemetryTime) return;
                bool stereoLateAlreadyRan = self._aerStereoLateUpdateFrame == Time.frameCount;
                MelonLogger.Msg($"[StereoRender][AER V37][SubmitOrder] frame={Time.frameCount} StereoRender.LateUpdateAlreadyRan={stereoLateAlreadyRan} stereoLateFrame={self._aerStereoLateUpdateFrame}");
            }
        }

        // ---------------------------------------------------------------------
        // ZER0INT V36 - ROTATIONAL STALE-EYE REPROJECTION
        // ---------------------------------------------------------------------
        // Pure camera rotation maps one perspective image to another by a homography. We do not
        // need a custom shader to express that transform: take the four corners of the stale eye's
        // image plane, unproject them through that eye's exact OpenVR projection matrix, rotate that
        // plane from OLD camera coordinates into TARGET camera coordinates, then draw the resulting
        // textured quad through the SAME projection matrix. Normal perspective-correct texture
        // interpolation performs the projective warp for us. This is exact for rotation-only camera
        // motion; no grid approximation is involved.
        //
        // Why target the FRESH eye's render pose rather than the newest HMD sample? Submit_Default
        // already has working compositor reprojection for a conventional stereo pair. The specific
        // AER bug is that our two textures originate from DIFFERENT render times. Aligning the stale
        // texture to the fresh texture's pose makes the pair conventional again and leaves SteamVR's
        // ordinary final timewarp untouched instead of accidentally timewarping twice.
        private static bool EnsureAerReprojectionResourcesV36(StereoRender self)
        {
            if (!AerRotationalReprojectionEnabled) return false;
            if (self == null || self.LeftRT == null || self.RightRT == null) return false;

            bool targetsValid =
                self._aerLeftReprojectedRT != null && self._aerLeftReprojectedRT.IsCreated() &&
                self._aerRightReprojectedRT != null && self._aerRightReprojectedRT.IsCreated() &&
                self._aerLeftReprojectedRT.width == self.LeftRT.width &&
                self._aerLeftReprojectedRT.height == self.LeftRT.height &&
                self._aerRightReprojectedRT.width == self.RightRT.width &&
                self._aerRightReprojectedRT.height == self.RightRT.height;

            if (!targetsValid)
            {
                ReleaseAerReprojectionTargetsV36(self);
                self._aerLeftReprojectedRT = CreateAerReprojectionTargetV36(self.LeftRT, "SonsVR_AER_Left_Reprojected");
                self._aerRightReprojectedRT = CreateAerReprojectionTargetV36(self.RightRT, "SonsVR_AER_Right_Reprojected");
            }

            if (self._aerReprojectionMaterial == null && !self._aerReprojectionInitAttempted)
            {
                self._aerReprojectionInitAttempted = true;

                // Prefer UI/Default because Sons demonstrably ships and uses Unity UI throughout its
                // menus; Unlit/Texture and Sprites/Default are ordinary textured fallbacks. We intentionally avoid compiling/injecting a custom shader asset for this
                // first proof-of-concept so V36 remains a one-DLL MelonLoader drop-in.
                Shader shader = Shader.Find("UI/Default");
                if (shader == null) shader = Shader.Find("Unlit/Texture");
                if (shader == null) shader = Shader.Find("Sprites/Default");

                if (shader != null)
                {
                    self._aerReprojectionMaterial = new Material(shader)
                    {
                        name = "SonsVR_AER_RotationalReprojection_Material",
                        hideFlags = HideFlags.HideAndDontSave
                    };

                    if (self._aerReprojectionMaterial.HasProperty("_Color"))
                        self._aerReprojectionMaterial.SetColor("_Color", Color.white);
                    if (self._aerReprojectionMaterial.HasProperty("_Cull"))
                        self._aerReprojectionMaterial.SetInt("_Cull", (int)CullMode.Off);
                    if (self._aerReprojectionMaterial.HasProperty("_ZWrite"))
                        self._aerReprojectionMaterial.SetInt("_ZWrite", 0);
                    if (self._aerReprojectionMaterial.HasProperty("_ZTest"))
                        self._aerReprojectionMaterial.SetInt("_ZTest", (int)CompareFunction.Always);

                    self._aerReprojectionAvailable = true;
                    MelonLogger.Msg($"[StereoRender][AER V36] rotational reprojection resources ready using shader '{shader.name}' at {self.LeftRT.width}x{self.LeftRT.height}.");
                }
                else
                {
                    self._aerReprojectionAvailable = false;
                    MelonLogger.Warning("[StereoRender][AER V36] couldn't find UI/Default, Unlit/Texture, or Sprites/Default. Rotational reprojection disabled for this session; naive V35 AER remains active.");
                }
            }

            return self._aerReprojectionAvailable &&
                   self._aerReprojectionMaterial != null &&
                   self._aerLeftReprojectedRT != null && self._aerLeftReprojectedRT.IsCreated() &&
                   self._aerRightReprojectedRT != null && self._aerRightReprojectedRT.IsCreated();
        }

        private static RenderTexture CreateAerReprojectionTargetV36(RenderTexture source, string name)
        {
            if (source == null) return null;

            var rt = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGB32)
            {
                name = name,
                antiAliasing = 1,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            rt.Create();
            return rt;
        }

        private static void ReleaseAerReprojectionTargetsV36(StereoRender self)
        {
            if (self == null) return;
            if (self._aerLeftReprojectedRT != null)
            {
                try { self._aerLeftReprojectedRT.Release(); } catch { }
                UnityEngine.Object.Destroy(self._aerLeftReprojectedRT);
                self._aerLeftReprojectedRT = null;
            }
            if (self._aerRightReprojectedRT != null)
            {
                try { self._aerRightReprojectedRT.Release(); } catch { }
                UnityEngine.Object.Destroy(self._aerRightReprojectedRT);
                self._aerRightReprojectedRT = null;
            }
        }

        private static Vector3 UnprojectAerCornerV36(Matrix4x4 inverseProjection, float ndcX, float ndcY)
        {
            // Any clip-space depth strictly inside the frustum works for a pure directional
            // transform; x/y after re-projection are independent of the arbitrary distance.
            Vector4 h = inverseProjection * new Vector4(ndcX, ndcY, 0f, 1f);
            if (Mathf.Abs(h.w) > 1e-6f)
            {
                float invW = 1f / h.w;
                return new Vector3(h.x * invW, h.y * invW, h.z * invW);
            }
            return new Vector3(h.x, h.y, h.z);
        }

        private static void EmitAerVertexV36(Vector3 position, float u, float v)
        {
            GL.Color(Color.white);
            GL.TexCoord2(u, v);
            GL.Vertex3(position.x, position.y, position.z);
        }

        private static bool ReprojectEyeRotationV36(
            StereoRender self,
            RenderTexture source,
            RenderTexture destination,
            HmdMatrix34_t sourceRenderPose,
            HmdMatrix34_t targetRenderPose,
            Matrix4x4 projection,
            string eyeLabel)
        {
            if (!EnsureAerReprojectionResourcesV36(self) || source == null || destination == null)
                return false;

            RenderTexture previousActive = RenderTexture.active;
            bool matrixPushed = false;
            bool glBegun = false;
            try
            {
                Quaternion sourceRotation = sourceRenderPose.GetRotation();
                Quaternion targetRotation = targetRenderPose.GetRotation();

                // Forward transform from a ray/point in the stale camera's local coordinates to
                // the same world direction expressed in the fresh camera's local coordinates:
                // p_target = inverse(R_target) * R_source * p_source.
                Quaternion sourceToTarget = Quaternion.Inverse(targetRotation) * sourceRotation;
                float correctionDegrees = Quaternion.Angle(sourceRotation, targetRotation);

                Matrix4x4 invProjection = projection.inverse;
                Vector3 p00 = sourceToTarget * UnprojectAerCornerV36(invProjection, -1f, -1f);
                Vector3 p10 = sourceToTarget * UnprojectAerCornerV36(invProjection,  1f, -1f);
                Vector3 p11 = sourceToTarget * UnprojectAerCornerV36(invProjection,  1f,  1f);
                Vector3 p01 = sourceToTarget * UnprojectAerCornerV36(invProjection, -1f,  1f);

                RenderTexture.active = destination;
                GL.Viewport(new Rect(0f, 0f, destination.width, destination.height));
                GL.Clear(true, true, Color.black);
                GL.PushMatrix();
                matrixPushed = true;
                GL.LoadIdentity();
                GL.LoadProjectionMatrix(projection);

                self._aerReprojectionMaterial.mainTexture = source;
                if (!self._aerReprojectionMaterial.SetPass(0))
                    throw new InvalidOperationException("reprojection material SetPass(0) returned false");

                // Two triangles forming the rotated old image plane. Because the vertices are
                // genuinely 3D and rendered through the target perspective projection, the GPU's
                // normal perspective-correct UV interpolation implements the rotation homography.
                GL.Begin(GL.TRIANGLES);
                glBegun = true;
                EmitAerVertexV36(p00, 0f, 0f);
                EmitAerVertexV36(p10, 1f, 0f);
                EmitAerVertexV36(p11, 1f, 1f);

                EmitAerVertexV36(p00, 0f, 0f);
                EmitAerVertexV36(p11, 1f, 1f);
                EmitAerVertexV36(p01, 0f, 1f);
                GL.End();
                glBegun = false;
                GL.PopMatrix();
                matrixPushed = false;

                if (!self._aerReprojectionActiveLogged)
                {
                    self._aerReprojectionActiveLogged = true;
                    MelonLogger.Msg($"[StereoRender][AER V36] rotational stale-eye reprojection ACTIVE ({eyeLabel} warped to fresh-eye head rotation; first correction={correctionDegrees:F3} deg). Submit_Default remains in use; no SteamVR per-eye pose extension.");
                }
                return true;
            }
            catch (Exception e)
            {
                if (glBegun)
                {
                    try { GL.End(); } catch { }
                }
                if (matrixPushed)
                {
                    try { GL.PopMatrix(); } catch { }
                }

                if (self._aerReprojectionFailureLogsRemaining > 0)
                {
                    self._aerReprojectionFailureLogsRemaining--;
                    MelonLogger.Warning($"[StereoRender][AER V36] rotational reprojection failed for {eyeLabel}: {e.Message}. Submitting raw V35 texture this frame. Logs remaining={self._aerReprojectionFailureLogsRemaining}.");
                }
                return false;
            }
            finally
            {
                RenderTexture.active = previousActive;
                // GL.Viewport is global immediate-mode state. Restore a sensible viewport too so
                // the mirror/UI or any later custom drawing in this frame cannot inherit the eye
                // texture dimensions from our off-screen warp.
                if (previousActive != null)
                    GL.Viewport(new Rect(0f, 0f, previousActive.width, previousActive.height));
                else
                    GL.Viewport(new Rect(0f, 0f, Screen.width, Screen.height));
            }
        }

        private static void PrepareAerSubmissionTexturesV36(
            StereoRender self,
            out RenderTexture leftSubmit,
            out RenderTexture rightSubmit)
        {
            leftSubmit = self.LeftRT;
            rightSubmit = self.RightRT;

            if (!AlternateEyeRenderingEnabled || !AerRotationalReprojectionEnabled) return;
            if (!self._aerLeftRenderPoseValid || !self._aerRightRenderPoseValid) return;
            if (self._aerLastCompletedEye != 0 && self._aerLastCompletedEye != 1) return;
            if (!EnsureAerReprojectionResourcesV36(self)) return;

            // The most recently COMPLETED eye is the fresh raw texture. Warp only the other eye
            // from its older render rotation into that fresh eye's HMD rotation. Per-eye projection
            // remains that eye's own asymmetric OpenVR projection matrix.
            if (self._aerLastCompletedEye == 0)
            {
                if (ReprojectEyeRotationV36(self,
                    self.RightRT, self._aerRightReprojectedRT,
                    self._aerRightRenderPose, self._aerLeftRenderPose,
                    self.RightCam.projectionMatrix, "RIGHT"))
                {
                    rightSubmit = self._aerRightReprojectedRT;
                }
            }
            else
            {
                if (ReprojectEyeRotationV36(self,
                    self.LeftRT, self._aerLeftReprojectedRT,
                    self._aerLeftRenderPose, self._aerRightRenderPose,
                    self.LeftCam.projectionMatrix, "LEFT"))
                {
                    leftSubmit = self._aerLeftReprojectedRT;
                }
            }
        }

        private static bool EnsureAerPoseSubmitLayoutV34(StereoRender self)
        {
            if (self._aerPoseLayoutChecked) return self._aerPoseLayoutValid;
            self._aerPoseLayoutChecked = true;

            try
            {
                int textureSize = Marshal.SizeOf(typeof(Texture_t));
                int poseOffset = (int)Marshal.OffsetOf(typeof(OpenVrTextureWithPoseNative), nameof(OpenVrTextureWithPoseNative.mDeviceToAbsoluteTracking));
                int nativeSize = Marshal.SizeOf(typeof(OpenVrTextureWithPoseNative));
                int matrixSize = Marshal.SizeOf(typeof(HmdMatrix34_t));
                self._aerPoseLayoutValid = poseOffset == textureSize && nativeSize >= textureSize + matrixSize;

                MelonLogger.Msg($"[StereoRender][AER V34] pose-submit layout check: Texture_t={textureSize} bytes poseOffset={poseOffset} wrapper={nativeSize} bytes matrix={matrixSize} bytes valid={self._aerPoseLayoutValid}.");
            }
            catch (Exception e)
            {
                self._aerPoseLayoutValid = false;
                MelonLogger.Warning($"[StereoRender][AER V34] couldn't validate VRTextureWithPose native layout; falling back to Submit_Default: {e.Message}");
            }

            return self._aerPoseLayoutValid;
        }

        private static bool CanUseAerPoseSubmissionV34(StereoRender self)
        {
            // See the frame-tag comment in CommitPreviouslyScheduledAerRenderV34: this also makes
            // submission correct if MelonLoader.OnLateUpdate runs before StereoRender.LateUpdate.
            CommitPreviouslyScheduledAerRenderV34(self);

            return AlternateEyeRenderingEnabled &&
                   AerPoseAwareSubmissionEnabled &&
                   !self._aerPoseSubmissionDisabledForSession &&
                   self._aerLeftRenderPoseValid &&
                   self._aerRightRenderPoseValid &&
                   EnsureAerPoseSubmitLayoutV34(self);
        }

        private static unsafe EVRCompositorError SubmitEyeWithStoredPoseV34(
            EVREye eye, ref Texture_t baseTexture, ref VRTextureBounds_t textureBounds, HmdMatrix34_t renderPose)
        {
            var posedTexture = new OpenVrTextureWithPoseNative
            {
                texture = baseTexture,
                mDeviceToAbsoluteTracking = renderPose
            };

            // CVRCompositor.Submit's generated C# signature is always ref Texture_t even when
            // Submit_TextureWithPose tells native OpenVR that a larger derived struct follows it.
            // Because Texture_t is the FIRST field above and all involved structs are blittable,
            // reinterpret the same stack address as Texture_t and native OpenVR sees the complete
            // Texture_t + HmdMatrix34_t payload contiguously.
            OpenVrTextureWithPoseNative* pPosed = &posedTexture;
            Texture_t* pBase = (Texture_t*)pPosed;
            ref Texture_t baseRef = ref *pBase;
            return OpenVR.Compositor.Submit(eye, ref baseRef, ref textureBounds, EVRSubmitFlags.Submit_TextureWithPose);
        }

        private static EVRCompositorError SubmitEyeV34(StereoRender self,
            EVREye eye, ref Texture_t texture, ref VRTextureBounds_t textureBounds, HmdMatrix34_t renderPose, bool usePose)
        {
            if (!usePose)
                return OpenVR.Compositor.Submit(eye, ref texture, ref textureBounds, EVRSubmitFlags.Submit_Default);

            EVRCompositorError poseError;
            try
            {
                poseError = SubmitEyeWithStoredPoseV34(eye, ref texture, ref textureBounds, renderPose);
            }
            catch (Exception e)
            {
                if (!self._aerPoseFallbackLogged)
                {
                    self._aerPoseFallbackLogged = true;
                    MelonLogger.Warning($"[StereoRender][AER V34] pose-aware Submit threw '{e.Message}'. Disabling pose-aware submission for this session and retrying with Submit_Default; AER camera alternation remains active.");
                }
                self._aerPoseSubmissionDisabledForSession = true;
                return OpenVR.Compositor.Submit(eye, ref texture, ref textureBounds, EVRSubmitFlags.Submit_Default);
            }

            if (poseError == EVRCompositorError.None) return poseError;

            // Distinguish a malformed/unsupported pose payload from an ordinary transient
            // compositor error (lost focus, already submitted, etc.): retry the SAME eye once with
            // the old Submit_Default path. Only if the default call succeeds do we know the pose
            // extension itself was the difference, and then disable it for the rest of the session.
            EVRCompositorError defaultError = OpenVR.Compositor.Submit(eye, ref texture, ref textureBounds, EVRSubmitFlags.Submit_Default);
            if (defaultError == EVRCompositorError.None)
            {
                self._aerPoseSubmissionDisabledForSession = true;
                if (!self._aerPoseFallbackLogged)
                {
                    self._aerPoseFallbackLogged = true;
                    MelonLogger.Warning($"[StereoRender][AER V34] pose-aware Submit returned {poseError} while Submit_Default succeeded. Pose-aware submission disabled for this session; AER itself stays enabled.");
                }
            }
            else if (self._aerSubmitErrorLogsRemaining > 0)
            {
                self._aerSubmitErrorLogsRemaining--;
                MelonLogger.Warning($"[StereoRender][AER V34] compositor submit error eye={eye}: pose={poseError}, defaultRetry={defaultError} (likely transient; pose mode not disabled). Logs remaining={self._aerSubmitErrorLogsRemaining}.");
            }

            return defaultError;
        }

        /// <summary>
        /// Custom render pass that submits left and right eye textures to the OpenVR compositor.
        /// This is the final step that makes the rendered frames visible in the VR headset.
        /// </summary>
        public class StereoRenderPass
        {
            private readonly StereoRender stereoRender;
            public bool isRendering;

            // V93 A/B/A starts only once the modern temporal path has produced a valid
            // fresh+synthetic pair.  This prevents loading/warmup time from consuming Phase A.
            private float _v93SubmitAbStartTime = -1f;
            private int _v93LastLoggedPhase = -1;
            private int _v93PoseFailureLogsRemaining = 4;

            public StereoRenderPass(StereoRender stereoRender)
            {
                this.stereoRender = stereoRender;
            }

            /// <summary>
            /// Submits the left and right eye RenderTextures to OpenVR for display.
            /// Note: Texture UV bounds are flipped vertically (vMin=1, vMax=0) because
            /// Unity’s texture coordinate system is top-left origin, while OpenVR expects bottom-left.
            /// </summary>
            public void Execute()
            {
                if (!stereoRender.enabled)
                    return;

                AerDiagnosticsV37.LogSubmitOrdering(stereoRender);

                // Avoid submitting a texture handle while it's being destroyed/recreated by
                // UpdateResolution() on a different part of the frame - a genuine race
                // condition that could explain the native crashes, especially given the very
                // high resolution (3797x3927) suggesting the "recommended" value from SteamVR
                // might be jittering and retriggering recreation more often than expected.
                if (stereoRender._resolutionUpdateInProgress)
                    return;

                if (stereoRender.LeftRT == null || stereoRender.RightRT == null)
                    return;

                // Commit whichever camera render actually completed since the previous schedule
                // BEFORE deciding which eye is stale. CanUseAerPoseSubmissionV34() contains that
                // frame-tagged commit even when pose-aware submission itself is disabled; keeping
                // this here makes V36 correct regardless of the exact ordering between MelonLoader's
                // OnLateUpdate and this MonoBehaviour's own LateUpdate on this Unity build.
                bool usePose = CanUseAerPoseSubmissionV34(stereoRender);

                // V83: temporal same-eye prediction takes priority. If unavailable during warmup,
                // transition, or an error, submit raw retained L/R textures. NEVER invoke V36.
                RenderTexture leftSubmitRT;
                RenderTexture rightSubmitRT;
                bool preparedTemporalPairV93 =
                    AerDepthStereoWarpV48.TryGetSubmissionTextures(
                        stereoRender,
                        out leftSubmitRT,
                        out rightSubmitRT);
                if (!preparedTemporalPairV93)
                {
                    leftSubmitRT = stereoRender.LeftRT;
                    rightSubmitRT = stereoRender.RightRT;
                }

                // Wrap the selected raw-or-V83 render texture handles into
                // OpenVR-compatible Texture_t structs.
                var leftTex = new Texture_t
                {
                    handle = leftSubmitRT.GetNativeTexturePtr(),
                    eType = SteamVR.instance.textureType,
                    eColorSpace = EColorSpace.Auto
                };

                var rightTex = new Texture_t
                {
                    handle = rightSubmitRT.GetNativeTexturePtr(),
                    eType = SteamVR.instance.textureType,
                    eColorSpace = EColorSpace.Auto
                };

                // Flip V coordinates to match OpenVR's expected texture layout
                var textureBounds = new VRTextureBounds_t
                {
                    uMin = 0,
                    vMin = 1, // Top in Unity = bottom in OpenVR
                    uMax = 1,
                    vMax = 0  // Bottom in Unity = top in OpenVR
                };

                // V79 passive submit-path capture. If V51 coverage stays fixed while
                // the headset artifact moves, this tells us whether the texture identity, native
                // handles, SteamVR recommended size, or bounds changed downstream.
                try
                {
                    HDRPDlssRuntimeTraceV78.RecordSubmissionV79(
                        leftSubmitRT,
                        rightSubmitRT,
                        textureBounds.uMin,
                        textureBounds.vMin,
                        textureBounds.uMax,
                        textureBounds.vMax,
                        (int)SteamVR.instance.sceneWidth,
                        (int)SteamVR.instance.sceneHeight,
                        SteamVR.instance.textureType.ToString());
                }
                catch (Exception e)
                {
                    MelonLogger.Warning(
                        "[StereoRender][AER V79][SubmitTrace] passive trace failed: " +
                        e.GetType().Name + ": " + e.Message);
                }

                // V93 compositor-boundary A/B/A.
                //
                // Historical V34 used *different* per-eye poses and exposed SteamVR's unreliable
                // distinct-pose AER semantics. Modern same-eye temporal AER is fundamentally
                // different: immediately before this submit the stale eye has already been
                // synthesized to the CURRENT fresh eye's target pose. Both textures therefore
                // describe one common HMD render pose. Phase B submits that SAME fresh pose for
                // BOTH eyes explicitly. If the upper/lower field-dependent wobble is caused by a
                // mismatch between the pose our textures describe and Submit_Default's implicit
                // compositor pose, Phase B should change it without touching reconstruction math.
                bool modernTemporalPairV93 =
                    preparedTemporalPairV93 &&
                    leftSubmitRT != null && rightSubmitRT != null &&
                    AerDepthStereoWarpV48Enabled &&
                    AerDepthStereoWarpV48SubmitSyntheticEye &&
                    stereoRender._aerLastCompletedEye >= 0;

                if (modernTemporalPairV93 && _v93SubmitAbStartTime < 0f)
                    _v93SubmitAbStartTime = Time.unscaledTime;

                float v93Elapsed = _v93SubmitAbStartTime >= 0f
                    ? Time.unscaledTime - _v93SubmitAbStartTime
                    : 0f;

                int v93Phase = 0; // A: Submit_Default
                if (AerV93SameFreshPoseSubmitABEnabled &&
                    _v93SubmitAbStartTime >= 0f &&
                    v93Elapsed >= Mathf.Max(1f, AerV93SameFreshPoseSubmitStartSeconds) &&
                    v93Elapsed < Mathf.Max(
                        AerV93SameFreshPoseSubmitStartSeconds + 1f,
                        AerV93SameFreshPoseSubmitEndSeconds))
                    v93Phase = 1; // B: explicit SAME fresh pose
                else if (AerV93SameFreshPoseSubmitABEnabled &&
                         _v93SubmitAbStartTime >= 0f &&
                         v93Elapsed >= Mathf.Max(
                             AerV93SameFreshPoseSubmitStartSeconds + 1f,
                             AerV93SameFreshPoseSubmitEndSeconds))
                    v93Phase = 2; // A2: back to Submit_Default

                if (AerV93SameFreshPoseSubmitABEnabled &&
                    v93Phase != _v93LastLoggedPhase && modernTemporalPairV93)
                {
                    _v93LastLoggedPhase = v93Phase;
                    string phaseText =
                        v93Phase == 1
                            ? "B SAME-FRESH-POSE Submit_TextureWithPose for BOTH eyes"
                            : v93Phase == 2
                                ? "A2 RETURN TO Submit_Default"
                                : "A Submit_Default baseline";
                    MelonLogger.Warning(
                        $"[StereoRender][AER V93][SubmitPoseAB] phase={phaseText} " +
                        $"elapsed={v93Elapsed:F1}s freshEye=" +
                        $"{(stereoRender._aerLastCompletedEye == 0 ? "L" : "R")} " +
                        $"frame={Time.frameCount}. NVOFA/DLSS/reconstruction unchanged.");
                }

                bool useSameFreshPoseV93 = v93Phase == 1 && modernTemporalPairV93;
                HmdMatrix34_t sameFreshPoseV93 =
                    stereoRender._aerLastCompletedEye == 0
                        ? stereoRender._aerLeftRenderPose
                        : stereoRender._aerRightRenderPose;
                bool sameFreshPoseValidV93 =
                    stereoRender._aerLastCompletedEye == 0
                        ? (stereoRender._aerLeftRenderPoseValid &&
                           stereoRender._aerLeftRenderFrame == Time.frameCount)
                        : (stereoRender._aerRightRenderPoseValid &&
                           stereoRender._aerRightRenderFrame == Time.frameCount);

                EVRCompositorError errorL;
                EVRCompositorError errorR;

                // V106 compositor boundary: camera callbacks after the second Submit are the
                // prime suspects for the 100-230ms post-submit GPU pathology.
                PerformanceForensicsV106.MarkSubmitBegin();

                if (useSameFreshPoseV93 && sameFreshPoseValidV93)
                {
                    try
                    {
                        errorL = SubmitEyeWithStoredPoseV34(
                            EVREye.Eye_Left, ref leftTex, ref textureBounds, sameFreshPoseV93);
                        if (errorL != EVRCompositorError.None)
                            errorL = OpenVR.Compositor.Submit(
                                EVREye.Eye_Left, ref leftTex, ref textureBounds, EVRSubmitFlags.Submit_Default);
                    }
                    catch (Exception e)
                    {
                        if (_v93PoseFailureLogsRemaining > 0)
                        {
                            _v93PoseFailureLogsRemaining--;
                            MelonLogger.Warning(
                                $"[StereoRender][AER V93][SubmitPoseAB] LEFT same-pose submit threw " +
                                $"{e.GetType().Name}: {e.Message}; fail-soft Submit_Default. " +
                                $"logsRemaining={_v93PoseFailureLogsRemaining}.");
                        }
                        errorL = OpenVR.Compositor.Submit(
                            EVREye.Eye_Left, ref leftTex, ref textureBounds, EVRSubmitFlags.Submit_Default);
                    }

                    try
                    {
                        errorR = SubmitEyeWithStoredPoseV34(
                            EVREye.Eye_Right, ref rightTex, ref textureBounds, sameFreshPoseV93);
                        if (errorR != EVRCompositorError.None)
                            errorR = OpenVR.Compositor.Submit(
                                EVREye.Eye_Right, ref rightTex, ref textureBounds, EVRSubmitFlags.Submit_Default);
                    }
                    catch (Exception e)
                    {
                        if (_v93PoseFailureLogsRemaining > 0)
                        {
                            _v93PoseFailureLogsRemaining--;
                            MelonLogger.Warning(
                                $"[StereoRender][AER V93][SubmitPoseAB] RIGHT same-pose submit threw " +
                                $"{e.GetType().Name}: {e.Message}; fail-soft Submit_Default. " +
                                $"logsRemaining={_v93PoseFailureLogsRemaining}.");
                        }
                        errorR = OpenVR.Compositor.Submit(
                            EVREye.Eye_Right, ref rightTex, ref textureBounds, EVRSubmitFlags.Submit_Default);
                    }
                }
                else
                {
                    if (useSameFreshPoseV93 && !sameFreshPoseValidV93 &&
                        _v93PoseFailureLogsRemaining > 0)
                    {
                        _v93PoseFailureLogsRemaining--;
                        MelonLogger.Warning(
                            $"[StereoRender][AER V93][SubmitPoseAB] Phase B requested but current " +
                            $"fresh render pose is unavailable/mismatched at frame={Time.frameCount}; " +
                            $"using Submit_Default. logsRemaining={_v93PoseFailureLogsRemaining}.");
                    }

                    // V93 deliberately bypasses V34's old DISTINCT-per-eye pose mode. Outside
                    // Phase B this is the exact proven Submit_Default path.
                    errorL = OpenVR.Compositor.Submit(
                        EVREye.Eye_Left, ref leftTex, ref textureBounds, EVRSubmitFlags.Submit_Default);
                    errorR = OpenVR.Compositor.Submit(
                        EVREye.Eye_Right, ref rightTex, ref textureBounds, EVRSubmitFlags.Submit_Default);
                }

                PerformanceForensicsV106.MarkSecondSubmitCompleted(errorL, errorR);

                // Strictly after submit - see CaptureMirrorView's own comment for why this
                // ordering is critical, not just convenient. V106 times the CPU-side blit too;
                // OpenVR's postSubmitGpu bucket will tell us whether later GPU work explodes.
                PerformanceForensicsV106.MarkMirrorBegin();
                stereoRender.CaptureMirrorView();
                PerformanceForensicsV106.MarkMirrorEnd();

                // Optional: log errors during development
                // if (errorL != EVRCompositorError.None) MelonLogger.Warning($"Left eye submit error: {errorL}");
                // if (errorR != EVRCompositorError.None) MelonLogger.Warning($"Right eye submit error: {errorR}");
            }
        }
    }
}