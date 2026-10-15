Shader "Hidden/SonsVR/DepthStereoWarp"
{
    Properties
    {
        _DepthArraySlice ("Depth array slice", Float) = 0
        _MaxWarpMeters ("Maximum warp depth", Float) = 24000
        _DepthDiscontinuityMeters ("Depth discontinuity absolute tolerance", Float) = 0.20
        _DepthDiscontinuityRelative ("Depth discontinuity relative tolerance", Float) = 0.04
        _NearMonoMeters ("Near monoscopic fallback start", Float) = 0.45
        _NearFullStereoMeters ("Near full stereo end", Float) = 0.80
        _FarMonoStartMeters ("Far stereo fade start", Float) = 40
        _FarMonoEndMeters ("Far monoscopic fallback end", Float) = 120
        _QuarantineConfidenceFloor ("Minimum confidence for valid-but-quarantined pixels", Float) = 0.05
        _FillRadiusPixels ("Hole-fill sample radius", Float) = 1
        _FillConfidenceDecay ("Hole-fill confidence decay", Float) = 0.82
        _V103FillDepthAbsToleranceMeters ("V103 fill absolute depth tolerance", Float) = 0.30
        _V103FillDepthRelativeTolerance ("V103 fill relative depth tolerance", Float) = 0.06
        _V105FillDepthMaxToleranceMeters ("V105 fill max depth tolerance meters", Float) = 1.50
        _V103FillMinSupportFraction ("V103 fill minimum dominant-layer fraction", Float) = 0.60
        _V103FillMinSupportCount ("V103 fill minimum same-layer neighbors", Float) = 2
        _FallbackBlendLowConfidence ("Fallback blend low confidence", Float) = 0.10
        _FallbackBlendFullConfidence ("Fallback blend full confidence", Float) = 0.85
        _ProjectiveSourceUvV86 ("V86 projective source coordinates", Float) = 0
        _RotationHoleFallbackEnabledV100 ("V100 rotation-only true-hole fallback", Float) = 0
        _HudPlaneDepthOverrideEnabled ("V69 HUD-plane depth override", Float) = 0
        _HudPlaneDepthMeters ("V69 HUD plane depth meters", Float) = 0.50
        _HudRectCount ("V69 HUD rect count", Float) = 0
        _HudRect0 ("V69 HUD rect 0", Vector) = (0,0,0,0)
        _HudRect1 ("V69 HUD rect 1", Vector) = (0,0,0,0)
        _HudRect2 ("V69 HUD rect 2", Vector) = (0,0,0,0)
        _HudRect3 ("V69 HUD rect 3", Vector) = (0,0,0,0)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off

        HLSLINCLUDE
        Texture2D<float4> _SourceColor;
        SamplerState sampler_SourceColor;
        // V51: holes are filled from the OLD TARGET EYE (rotation-aligned when available),
        // never from the source eye. Source-eye fallback was the flat holo-sheet/double-image culprit.
        Texture2D<float4> _FallbackTargetColor;
        SamplerState sampler_FallbackTargetColor;
        // _CameraDepthTexture is used ONLY by V50's endCameraRendering capture pass while
        // HDRP has identified the exact eye camera that just finished. The headset warp itself
        // reads only the owned 2D RFloat copy below.
        Texture2DArray<float> _CameraDepthTexture;
        Texture2D<float> _LatchedDepthTexture;
        Texture2D<float4> _WarpInput;
        // V88: per-target-pixel lookup written by full-resolution compute scatter.
        // 0xFFFFFFFF means no historical source sample reached this target pixel.
        Texture2D<uint> _ScatterSourceIndexV88;
        // V103 guarded fill reads ONLY the original direct-scatter color/depth-key pair.
        // Previously-filled pixels are never allowed to seed another layer across a silhouette.
        Texture2D<float4> _DirectWarpInputV103;
        Texture2D<uint> _ScatterDepthKeyV103;

        float4 _ZBufferParams;
        float4 _SourceSize;      // width, height, 1/width, 1/height
        float4 _TargetSize;      // width, height, 1/width, 1/height
        float4 _DepthAtlasSize;  // width, height, 1/width, 1/height (diagnostic only)
        float _DepthArraySlice;
        float _MaxWarpMeters;
        float _DepthDiscontinuityMeters;
        float _DepthDiscontinuityRelative;
        float _NearMonoMeters;
        float _NearFullStereoMeters;
        float _FarMonoStartMeters;
        float _FarMonoEndMeters;
        float _QuarantineConfidenceFloor;
        float _FillRadiusPixels;
        float _FillConfidenceDecay;
        float _V103FillDepthAbsToleranceMeters;
        float _V103FillDepthRelativeTolerance;
        float _V105FillDepthMaxToleranceMeters;
        float _V103FillMinSupportFraction;
        float _V103FillMinSupportCount;
        float _FallbackBlendLowConfidence;
        float _FallbackBlendFullConfidence;
        float _ProjectiveSourceUvV86;

        // V69 diagnostic. Rects are source-eye TOP-LEFT UV bounds:
        // (xmin, ymin, xmax, ymax). Only the warp geometry/depth changes; Unity UGUI rendering
        // remains untouched in the source color buffer.
        float _HudPlaneDepthOverrideEnabled;
        float _HudPlaneDepthMeters;
        float _HudRectCount;
        float4 _HudRect0;
        float4 _HudRect1;
        float4 _HudRect2;
        float4 _HudRect3;

        float4x4 _SourceInvGpuProjection;
        float4x4 _SourceCameraToWorld;
        float4x4 _TargetWorldToCamera;
        float4x4 _TargetGpuProjection;

        // V100 true-hole fallback matrices.  These are deliberately separate from the
        // finite-depth warp matrices so the fallback can inverse-gather by DIRECTION only:
        // current target pixel -> current view ray -> world direction -> stale source view
        // ray -> stale source UV.  Camera translation never enters the mapping.
        float _RotationHoleFallbackEnabledV100;
        float4x4 _V100TargetInvGpuProjection;
        float4x4 _V100TargetCameraToWorld;
        float4x4 _V100SourceWorldToCamera;
        float4x4 _V100SourceGpuProjection;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv : TEXCOORD0;
        };

        struct WarpVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 sourceUV : TEXCOORD0;
            float sourceEyeDepth : TEXCOORD1;
            float valid : TEXCOORD2;
            float hudDepthOverride : TEXCOORD3;

            // V86: true homogeneous SOURCE-camera clip coordinate.
            // Divide only in FragWarp after target-space interpolation.
            float4 sourceClipV86 : TEXCOORD4;
        };

        struct CopyVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 sourceUV : TEXCOORD0;
        };

        uint2 PixelFromTopLeftUv(float2 uv, float2 size)
        {
            float2 clampedUv = saturate(uv);
            float2 pixel = floor(clampedUv * max(size - 1.0, 0.0));
            return uint2(pixel);
        }

        uint2 SourcePixel(float2 uv)
        {
            return PixelFromTopLeftUv(uv, _SourceSize.xy);
        }

        uint2 TargetPixel(float2 uv)
        {
            return PixelFromTopLeftUv(uv, _TargetSize.xy);
        }

        float RawDepthAt(float2 sourceUv)
        {
            uint2 p = SourcePixel(sourceUv);
            // V50: this is an owned, eye-local 2D RFloat copy made at that eye camera's
            // endCameraRendering callback. No packed global atlas is sampled during synthesis.
            return _LatchedDepthTexture.Load(int3((int)p.x, (int)p.y, 0));
        }

        float2 ProjectiveSourceUvV86(WarpVaryings input)
        {
            if (_ProjectiveSourceUvV86 < 0.5 || abs(input.sourceClipV86.w) <= 1.0e-8)
                return saturate(input.sourceUV);

            float2 sourceNdc = input.sourceClipV86.xy / input.sourceClipV86.w;
            return saturate(float2(
                sourceNdc.x * 0.5 + 0.5,
                0.5 - sourceNdc.y * 0.5));
        }

        float LinearEyeDepthFromRaw(float rawDepth)
        {
            return rcp(_ZBufferParams.z * rawDepth + _ZBufferParams.w);
        }

        float DistanceStereoConfidence(float eyeDepth)
        {
            float nearDen = max(_NearFullStereoMeters - _NearMonoMeters, 1.0e-4);
            float farDen = max(_FarMonoEndMeters - _FarMonoStartMeters, 1.0e-4);
            float nearConfidence = smoothstep(_NearMonoMeters, _NearFullStereoMeters, eyeDepth);
            float farConfidence = 1.0 - smoothstep(_FarMonoStartMeters, _FarMonoEndMeters, eyeDepth);
            return saturate(min(nearConfidence, farConfidence));
        }

        bool UvInsideRect(float2 uv, float4 r)
        {
            return uv.x >= r.x && uv.x <= r.z && uv.y >= r.y && uv.y <= r.w;
        }

        bool IsHudPlaneDepthOverrideUv(float2 uv)
        {
            if (_HudPlaneDepthOverrideEnabled < 0.5)
                return false;

            int count = clamp((int)round(_HudRectCount), 0, 4);
            if (count > 0 && UvInsideRect(uv, _HudRect0)) return true;
            if (count > 1 && UvInsideRect(uv, _HudRect1)) return true;
            if (count > 2 && UvInsideRect(uv, _HudRect2)) return true;
            if (count > 3 && UvInsideRect(uv, _HudRect3)) return true;
            return false;
        }

        float RawDepthFromLinearEye(float eyeDepth)
        {
            float d = max(eyeDepth, 1.0e-4);
            return (rcp(d) - _ZBufferParams.w) / _ZBufferParams.z;
        }

        WarpVaryings VertWarp(Attributes input)
        {
            WarpVaryings output;

            uint2 p = SourcePixel(input.uv);
            float2 sourceUv = (float2(p) + 0.5) * _SourceSize.zw;
            float rawDepthScene = _LatchedDepthTexture.Load(int3((int)p.x, (int)p.y, 0));

            bool hudDepthOverride = IsHudPlaneDepthOverrideUv(sourceUv);
            float eyeDepth = hudDepthOverride
                ? max(_HudPlaneDepthMeters, 0.05)
                : LinearEyeDepthFromRaw(rawDepthScene);
            float rawDepthForProjection = hudDepthOverride
                ? RawDepthFromLinearEye(eyeDepth)
                : rawDepthScene;

            bool validDepth = hudDepthOverride ||
                (rawDepthScene > 1.0e-8 && eyeDepth > 0.0 && eyeDepth <= _MaxWarpMeters);

            // D3D viewport convention: pixel y=0 is TOP while NDC y=+1 is TOP.
            float2 ndc = float2(sourceUv.x * 2.0 - 1.0, 1.0 - sourceUv.y * 2.0);
            float4 normalizedSourceClip =
                float4(ndc, rawDepthForProjection, 1.0);
            float4 sourceViewH = mul(_SourceInvGpuProjection, normalizedSourceClip);
            bool validW = abs(sourceViewH.w) > 1.0e-8;
            float3 sourceView = sourceViewH.xyz / (validW ? sourceViewH.w : 1.0);

            // P_source * sourceView == normalizedSourceClip / sourceViewH.w.
            // Carrying this homogeneous SOURCE clip value across the target triangle
            // and dividing in FragWarp gives the correct projective mapping.
            float4 sourceClipV86 = validW
                ? normalizedSourceClip / sourceViewH.w
                : normalizedSourceClip;

            float4 world = mul(_SourceCameraToWorld, float4(sourceView, 1.0));
            float4 targetView = mul(_TargetWorldToCamera, world);
            float4 targetClip = mul(_TargetGpuProjection, targetView);
            bool inFront = targetClip.w > 1.0e-6;
            bool valid = validDepth && validW && inFront;

            // V48 moved invalid vertices to (-2,-2), which can create giant partially-valid
            // triangles pointing toward a corner when only one vertex is invalid. V49 leaves the
            // invalid vertex at its local source-screen position and rejects the whole mixed-valid
            // triangle in FragWarp via the near-1.0 interpolated-valid test below.
            output.positionCS = valid ? targetClip : float4(input.positionOS.xy, 0.0, 1.0);
            output.sourceUV = sourceUv;
            output.sourceEyeDepth = eyeDepth;
            output.valid = valid ? 1.0 : 0.0;
            output.hudDepthOverride = hudDepthOverride ? 1.0 : 0.0;
            output.sourceClipV86 = sourceClipV86;
            return output;
        }

        float4 FragWarp(WarpVaryings input) : SV_Target
        {
            // A triangle with even one invalid vertex interpolates below 1.0 almost everywhere.
            // Reject it instead of allowing a half-valid Minecraft wedge to survive.
            if (input.valid < 0.999)
                discard;

            bool hudDepthOverride = input.hudDepthOverride > 0.5;
            float2 sourceUvV86 = ProjectiveSourceUvV86(input);
            float rawDepth = RawDepthAt(sourceUvV86);
            float actualEyeDepth = hudDepthOverride
                ? input.sourceEyeDepth
                : LinearEyeDepthFromRaw(rawDepth);

            if (!hudDepthOverride)
            {
                if (rawDepth <= 1.0e-8 || actualEyeDepth <= 0.0 || actualEyeDepth > _MaxWarpMeters)
                    discard;
            }

            float tolerance = max(_DepthDiscontinuityMeters,
                                  _DepthDiscontinuityRelative * actualEyeDepth);
            float depthError = hudDepthOverride ? 0.0 : abs(actualEyeDepth - input.sourceEyeDepth);
            if (!hudDepthOverride && depthError > tolerance)
                discard;

            // V51: confidence describes GEOMETRY QUALITY ONLY.
            // V49/V50 deliberately lowered confidence for near (<0.8m) and far (>40m) pixels
            // and then blended those pixels toward the SOURCE eye. In-headset that produced the
            // exact flat, jittery holo-sheet / double-image the user reported. Keep near/far
            // parameters for lineage/diagnostics, but do NOT use distance to force source-eye
            // fallback. Any geometrically valid warp remains a target-eye pixel.
            float geometryConfidence = saturate(1.0 - depthError / max(tolerance, 1.0e-4));
            float confidence = max(0.25, geometryConfidence);

            float4 color = _SourceColor.SampleLevel(
                sampler_SourceColor, sourceUvV86, 0);
            color.a = confidence;
            return color;
        }

        CopyVaryings VertCopy(Attributes input)
        {
            CopyVaryings output;
            output.positionCS = float4(input.positionOS.xy, 0.0, 1.0);
            output.sourceUV = input.uv;
            return output;
        }

        void AccumulateFillCandidate(
            int2 p,
            float distanceWeight,
            inout float3 colorSum,
            inout float weightSum,
            inout float bestConfidence)
        {
            int2 maxP = int2((int)_TargetSize.x - 1, (int)_TargetSize.y - 1);
            p = clamp(p, int2(0, 0), maxP);
            float4 s = _WarpInput.Load(int3(p, 0));
            if (s.a <= 0.001)
                return;

            float w = s.a * distanceWeight;
            colorSum += s.rgb * w;
            weightSum += w;
            bestConfidence = max(bestConfidence, s.a);
        }

        float4 FragHoleFill(CopyVaryings input) : SV_Target
        {
            uint2 up = TargetPixel(input.sourceUV);
            int2 p = int2((int)up.x, (int)up.y);
            float4 center = _WarpInput.Load(int3(p, 0));

            // Any non-zero-alpha direct/quarantined/previously-filled pixel is already occupied.
            // Only true holes are eligible for dilation.
            if (center.a > 0.001)
                return center;

            int r = max(1, (int)round(_FillRadiusPixels));
            float3 colorSum = 0.0;
            float weightSum = 0.0;
            float bestConfidence = 0.0;

            AccumulateFillCandidate(p + int2(-r,  0), 1.0000, colorSum, weightSum, bestConfidence);
            AccumulateFillCandidate(p + int2( r,  0), 1.0000, colorSum, weightSum, bestConfidence);
            AccumulateFillCandidate(p + int2( 0, -r), 1.0000, colorSum, weightSum, bestConfidence);
            AccumulateFillCandidate(p + int2( 0,  r), 1.0000, colorSum, weightSum, bestConfidence);
            AccumulateFillCandidate(p + int2(-r, -r), 0.7071, colorSum, weightSum, bestConfidence);
            AccumulateFillCandidate(p + int2( r, -r), 0.7071, colorSum, weightSum, bestConfidence);
            AccumulateFillCandidate(p + int2(-r,  r), 0.7071, colorSum, weightSum, bestConfidence);
            AccumulateFillCandidate(p + int2( r,  r), 0.7071, colorSum, weightSum, bestConfidence);

            if (weightSum <= 1.0e-6)
                return float4(0, 0, 0, 0);

            float confidence = saturate(bestConfidence * _FillConfidenceDecay);
            return float4(colorSum / weightSum, confidence);
        }

        int2 V103FillOffset(int i, int r)
        {
            if (i == 0) return int2(-r,  0);
            if (i == 1) return int2( r,  0);
            if (i == 2) return int2( 0, -r);
            if (i == 3) return int2( 0,  r);
            if (i == 4) return int2(-r, -r);
            if (i == 5) return int2( r, -r);
            if (i == 6) return int2(-r,  r);
            return int2(r, r);
        }

        float V103DistanceWeight(int i)
        {
            return i < 4 ? 1.0 : 0.7071;
        }

        bool ReadV103DirectFillCandidate(int2 p, out float4 sampleColor, out float depthMeters)
        {
            int2 maxP = int2((int)_TargetSize.x - 1, (int)_TargetSize.y - 1);
            p = clamp(p, int2(0, 0), maxP);

            sampleColor = _DirectWarpInputV103.Load(int3(p, 0));
            uint key = _ScatterDepthKeyV103.Load(int3(p, 0));
            if (sampleColor.a <= 0.001 || key == 0xFFFFFFFFu || key == 0xFFFFFFFEu)
            {
                depthMeters = 0.0;
                return false;
            }

            depthMeters = asfloat(key);
            return depthMeters > 0.0 && depthMeters == depthMeters && depthMeters < 1.0e7;
        }

        bool V103DepthsAgree(float a, float b)
        {
            float relativeTol = max(0.0, _V103FillDepthRelativeTolerance) * min(a, b);
            float cappedRelativeTol = min(relativeTol, max(0.001, _V105FillDepthMaxToleranceMeters));
            float tol = max(max(0.001, _V103FillDepthAbsToleranceMeters), cappedRelativeTol);
            return abs(a - b) <= tol;
        }

        // V103: depth-coherence guarded hole fill.
        //
        // The old HoleFill averages every occupied neighbor, even when those neighbors belong
        // to different depth layers (e.g. leaf card at 6m + rock at 18m + sky hole). V102 showed
        // that this is exactly where fill increases truth error. This pass:
        //   1) samples ONLY original direct-scatter pixels, never recursively-filled pixels;
        //   2) finds the dominant mutually-consistent target-depth cluster;
        //   3) fills only when that cluster has enough support and dominates the neighborhood.
        //
        // Ambiguous silhouette/disocclusion holes remain alpha=0 and therefore fall through to
        // V100's rotation-only same-eye fallback, which is safer than welding two depth slices.
        float4 FragHoleFillDepthGuardV103(CopyVaryings input) : SV_Target
        {
            uint2 up = TargetPixel(input.sourceUV);
            int2 p = int2((int)up.x, (int)up.y);
            float4 center = _WarpInput.Load(int3(p, 0));
            if (center.a > 0.001)
                return center;

            int r = max(1, (int)round(_FillRadiusPixels));

            float4 colors[8];
            float depths[8];
            float distWeights[8];
            int validCount = 0;

            [unroll]
            for (int i = 0; i < 8; ++i)
            {
                float4 c;
                float d;
                if (ReadV103DirectFillCandidate(p + V103FillOffset(i, r), c, d))
                {
                    colors[validCount] = c;
                    depths[validCount] = d;
                    distWeights[validCount] = V103DistanceWeight(i);
                    validCount++;
                }
            }

            int minSupport = max(1, (int)round(_V103FillMinSupportCount));
            if (validCount < minSupport)
                return float4(0, 0, 0, 0);

            int bestSeed = -1;
            int bestSupport = 0;
            float bestSupportConfidence = -1.0;

            [loop]
            for (int i = 0; i < validCount; ++i)
            {
                int support = 0;
                float confidenceSum = 0.0;
                [loop]
                for (int j = 0; j < validCount; ++j)
                {
                    if (V103DepthsAgree(depths[i], depths[j]))
                    {
                        support++;
                        confidenceSum += colors[j].a;
                    }
                }

                if (support > bestSupport ||
                    (support == bestSupport && confidenceSum > bestSupportConfidence))
                {
                    bestSeed = i;
                    bestSupport = support;
                    bestSupportConfidence = confidenceSum;
                }
            }

            float supportFraction = validCount > 0 ? bestSupport / (float)validCount : 0.0;
            if (bestSeed < 0 || bestSupport < minSupport ||
                supportFraction < saturate(_V103FillMinSupportFraction))
                return float4(0, 0, 0, 0);

            float3 colorSum = 0.0;
            float weightSum = 0.0;
            float bestConfidence = 0.0;
            [loop]
            for (int j = 0; j < validCount; ++j)
            {
                if (!V103DepthsAgree(depths[bestSeed], depths[j]))
                    continue;
                float w = colors[j].a * distWeights[j];
                colorSum += colors[j].rgb * w;
                weightSum += w;
                bestConfidence = max(bestConfidence, colors[j].a);
            }

            if (weightSum <= 1.0e-6)
                return float4(0, 0, 0, 0);

            return float4(
                colorSum / weightSum,
                saturate(bestConfidence * _FillConfidenceDecay));
        }

        float2 RotationOnlyFallbackUvV100(float2 targetUv, out bool valid)
        {
            // Raw D3D render-target convention used everywhere else in this shader:
            // texture y=0 is TOP, NDC y=+1 is TOP.
            float2 targetNdc = float2(
                targetUv.x * 2.0 - 1.0,
                1.0 - targetUv.y * 2.0);

            // Any finite clip-space z on a perspective ray gives the same x/z and y/z
            // direction after inverse projection.  0.5 avoids relying on normal vs reversed-Z
            // near/far endpoint conventions.
            float4 targetViewH = mul(
                _V100TargetInvGpuProjection,
                float4(targetNdc, 0.5, 1.0));

            bool targetOk = abs(targetViewH.w) > 1.0e-8;
            float3 targetViewDir = targetViewH.xyz / (targetOk ? targetViewH.w : 1.0);

            // w=0 is the entire point: rotate the ray between camera frames while removing
            // translation.  That is the correct infinity/far-field approximation for pixels
            // whose finite depth is unavailable.
            float3 worldDir = mul(_V100TargetCameraToWorld, float4(targetViewDir, 0.0)).xyz;
            float3 sourceViewDir = mul(_V100SourceWorldToCamera, float4(worldDir, 0.0)).xyz;
            float4 sourceClip = mul(_V100SourceGpuProjection, float4(sourceViewDir, 1.0));

            bool sourceOk = sourceClip.w > 1.0e-6;
            float safeW = sourceOk ? sourceClip.w : 1.0;
            float2 sourceNdc = sourceClip.xy / safeW;
            float2 sourceUv = float2(
                sourceNdc.x * 0.5 + 0.5,
                0.5 - sourceNdc.y * 0.5);

            valid = targetOk && sourceOk &&
                    all(sourceUv >= float2(0.0, 0.0)) &&
                    all(sourceUv <= float2(1.0, 1.0));
            return sourceUv;
        }

        float4 FragFallbackComposite(CopyVaryings input) : SV_Target
        {
            uint2 p = TargetPixel(input.sourceUV);
            float4 warped = _WarpInput.Load(int3((int)p.x, (int)p.y, 0));
            float2 fallbackUv = saturate(input.sourceUV);
            if (_RotationHoleFallbackEnabledV100 > 0.5 && warped.a < 0.001)
            {
                bool rotationUvValidV100;
                float2 rotationUvV100 = RotationOnlyFallbackUvV100(input.sourceUV, rotationUvValidV100);
                if (rotationUvValidV100)
                    fallbackUv = rotationUvV100;
            }

            float4 targetFallback = _FallbackTargetColor.SampleLevel(
                sampler_FallbackTargetColor, fallbackUv, 0);

            // V51: NEVER blend the source eye into the synthetic target. That was literally
            // manufacturing a second, zero-/wrong-parallax picture and is an excellent match for
            // the user's "flat glass hologram just in front of me" observation.
            //
            // If we have ANY synthetic/fill coverage, use it outright. Only a TRUE hole uses the
            // previous TARGET-eye image. V100 can rotation-align ONLY those true holes by
            // inverse-gathering the stale target eye at infinite depth. That fallback can still
            // be temporally imperfect, but it
            // preserves target-eye binocular geometry instead of drawing a second source-eye view.
            float useWarp = step(0.001, warped.a);
            float3 rgb = lerp(targetFallback.rgb, warped.rgb, useWarp);

            // Preserve the synthetic confidence in alpha for desktop debug. Black now means
            // TARGET-EYE fallback, not source-eye fallback.
            return float4(rgb, saturate(warped.a));
        }

        float4 FragConfidenceDebug(CopyVaryings input) : SV_Target
        {
            uint2 p = TargetPixel(input.sourceUV);
            float confidence = saturate(_WarpInput.Load(int3((int)p.x, (int)p.y, 0)).a);
            return float4(confidence, confidence, confidence, 1.0);
        }

        float4 FragScatterResolveV88(CopyVaryings input) : SV_Target
        {
            uint2 targetP = TargetPixel(input.sourceUV);
            uint packed = _ScatterSourceIndexV88.Load(
                int3((int)targetP.x, (int)targetP.y, 0));

            if (packed == 0xFFFFFFFFu)
                return float4(0, 0, 0, 0);

            uint sourceX = packed & 0xFFFFu;
            uint sourceY = (packed >> 16) & 0xFFFFu;
            if (sourceX >= (uint)_SourceSize.x || sourceY >= (uint)_SourceSize.y)
                return float4(0, 0, 0, 0);

            float2 sourceUv =
                (float2(sourceX, sourceY) + 0.5) * _SourceSize.zw;

            // Compute handles visibility/z arbitration; ordinary shader sampling handles
            // the (possibly MSAA-backed) historical color RT exactly like the proven mesh path.
            float4 color = _SourceColor.SampleLevel(
                sampler_SourceColor, saturate(sourceUv), 0);
            color.a = 1.0;
            return color;
        }

        float4 DebugDepthGrayV83(float rawDepth)
        {
            if (rawDepth <= 1.0e-8)
                return float4(0, 0, 0, 1);

            float eyeDepth = LinearEyeDepthFromRaw(rawDepth);
            if (eyeDepth <= 0.0 || eyeDepth > _MaxWarpMeters)
                return float4(0, 0, 0, 1);

            // Log scale preserves near-field structure while keeping distant terrain visible.
            float maxM = max(1.0, _MaxWarpMeters);
            float n = saturate(log2(1.0 + eyeDepth) / log2(1.0 + maxM));
            float g = 1.0 - 0.88 * n;
            return float4(g, g, g, 1);
        }

        float4 FragDebugDepthLocal2D(CopyVaryings input) : SV_Target
        {
            uint2 p = SourcePixel(input.sourceUV);
            float raw = _LatchedDepthTexture.Load(int3((int)p.x, (int)p.y, 0));
            return DebugDepthGrayV83(raw);
        }

        float4 FragDebugDepthGlobalMip0(CopyVaryings input) : SV_Target
        {
            uint2 p = SourcePixel(input.sourceUV);

            // Deliberately reproduce V47 candidate A exactly:
            // full-resolution mip0 begins at packed atlas pixel offset (0,0).
            float raw = _CameraDepthTexture.Load(
                int4((int)p.x, (int)p.y, (int)_DepthArraySlice, 0));

            return DebugDepthGrayV83(raw);
        }

        float FragCaptureEyeDepth(CopyVaryings input) : SV_Target
        {
            // Camera-local V50 latch: while endCameraRendering identifies the exact eye, copy
            // full-resolution mip 0 out of HDRP's packed Texture2DArray into a plain RFloat RT.
            // Use raster pixel coordinates directly rather than re-deriving them from normalized
            // UV, so the copy is exact even at the final row/column.
            int2 maxP = int2((int)_SourceSize.x - 1, (int)_SourceSize.y - 1);
            int2 p = clamp(int2(input.positionCS.xy), int2(0, 0), maxP);
            return _CameraDepthTexture.Load(int4(p.x, p.y, (int)_DepthArraySlice, 0));
        }
        ENDHLSL

        // Pass 0: forward source-depth reprojection into an RGBA confidence-bearing warp RT.
        Pass
        {
            Name "StereoWarp"
            ZWrite On
            ZTest LEqual
            Blend Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertWarp
            #pragma fragment FragWarp
            ENDHLSL
        }

        // Pass 1: small confidence-aware spatial hole fill. V49 runs it twice (radius 1 then 2).
        Pass
        {
            Name "HoleFill"
            ZWrite Off
            ZTest Always
            Blend Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertCopy
            #pragma fragment FragHoleFill
            ENDHLSL
        }

        // V103: geometry-coherent alternative to HoleFill.  Pass name lookup is used,
        // so inserting this does not depend on historical numeric pass indices.
        Pass
        {
            Name "HoleFillDepthGuardV103"
            ZWrite Off
            ZTest Always
            Blend Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertCopy
            #pragma fragment FragHoleFillDepthGuardV103
            ENDHLSL
        }

        // Final composite. Any covered synthetic pixel wins outright; TRUE holes
        // fall back to the old TARGET eye (rotation-aligned by C# when available). No source-eye blend.
        Pass
        {
            Name "TargetEyeHoleFallback"
            ZWrite Off
            ZTest Always
            Blend Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertCopy
            #pragma fragment FragFallbackComposite
            ENDHLSL
        }

        // Pass 3: desktop confidence visualization. White=direct/high confidence, gray=filled,
        // black=true hole using old target-eye fallback.
        Pass
        {
            Name "ConfidenceDebug"
            ZWrite Off
            ZTest Always
            Blend Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertCopy
            #pragma fragment FragConfidenceDebug
            ENDHLSL
        }

        // Pass 4: V50 camera-local depth latch. Invoked only from SRP endCameraRendering for
        // VRLeftEye / VRRightEye; writes raw device depth into an owned full-resolution RFloat RT.
        Pass
        {
            Name "CaptureEyeDepth"
            ZWrite Off
            ZTest Always
            Blend Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertCopy
            #pragma fragment FragCaptureEyeDepth
            ENDHLSL
        }

        // Pass 5: desktop-only camera-local raw depth visualization.
        Pass
        {
            Name "DebugDepthLocal2D"
            ZWrite Off
            ZTest Always
            Blend Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertCopy
            #pragma fragment FragDebugDepthLocal2D
            ENDHLSL
        }

        // Pass 6: desktop-only old-V47 lower-left packed mip0 visualization.
        Pass
        {
            Name "DebugDepthGlobalMip0"
            ZWrite Off
            ZTest Always
            Blend Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertCopy
            #pragma fragment FragDebugDepthGlobalMip0
            ENDHLSL
        }

        // Pass 7: V88 full-resolution compute-scatter resolve.
        // Compute writes a winning SOURCE pixel coordinate per TARGET pixel; this pass
        // samples historical color with the same proven texture path as V87 and emits
        // alpha=0 only for genuine temporal disocclusion / uncovered target pixels.
        Pass
        {
            Name "PerPixelScatterResolveV88"
            ZWrite Off
            ZTest Always
            Blend Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertCopy
            #pragma fragment FragScatterResolveV88
            ENDHLSL
        }
    }

    Fallback Off
}
