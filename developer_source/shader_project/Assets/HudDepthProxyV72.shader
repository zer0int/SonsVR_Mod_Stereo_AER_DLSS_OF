Shader "Hidden/SonsVR/HudDepthProxyV72"
{
    Properties
    {
        _ProxyRect ("Top-left UV rect", Vector) = (0,0,1,1)
        _ProxyRawDepth ("Raw device depth", Float) = 0.03
        _ProxyShape ("0 rect / 1 circle", Float) = 0
        _HudSourceColor ("V76 current source eye", 2D) = "black" {}
        _HudSourceUvTransform ("V76 local XY to source UV", Vector) = (1,-1,0.5,0.5)
        _HudProxyKind ("V76 proxy kind", Float) = 0
        _HudTextBlackBacking ("V76 solid black text backing", Float) = 1
        _DepthDiagGlobalDepth ("V80 depth input", 2DArray) = "" {}
        _DepthDiagSourceSize ("V80 source size", Vector) = (1,1,1,1)
        _DepthDiagZParams ("V80 z params", Vector) = (1,1,1,1)
        _DepthDiagMaxMeters ("V80 max meters", Float) = 300
        _DepthDiagDiscontinuityMeters ("V80 discontinuity meters", Float) = 0.2
        _DepthDiagDiscontinuityRelative ("V80 discontinuity relative", Float) = 0.04
        _DepthDiagArraySlice ("V80 depth array slice", Float) = 0
        _V81CameraDepthArray ("V81 camera-local depth array", 2DArray) = "" {}
        _V81CameraDepth2D ("V81 camera-local depth 2D", 2D) = "black" {}
        _V81ActiveSize ("V81 active size", Vector) = (1,1,1,1)
        _V81ZParams ("V81 z params", Vector) = (1,1,1,1)
        _V81MaxMeters ("V81 max meters", Float) = 300
        _V81DiscontinuityMeters ("V81 discontinuity meters", Float) = 0.2
        _V81DiscontinuityRelative ("V81 discontinuity relative", Float) = 0.04
        _V81ArraySlice ("V81 array slice", Float) = 0
        _V89CameraMotionArray ("V89 camera-local motion array", 2DArray) = "" {}
        _V89CameraMotion2D ("V89 camera-local motion 2D", 2D) = "black" {}
        _V89MotionActiveSize ("V89 motion active size", Vector) = (1,1,1,1)
        _V89MotionArraySlice ("V89 motion array slice", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "HudDepthProxy"
            Blend Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex Vert
            #pragma fragment Frag

            float4 _ProxyRect;
            float _ProxyRawDepth;
            float _ProxyShape;
            float4x4 _ProxyLocalToClip;

            struct Attributes
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 localUV : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                // C# supplies clip-space vertices directly. UV remains 0..1 local to the proxy.
                output.positionCS = input.vertex;
                output.localUV = input.uv;
                return output;
            }

            float Frag(Varyings input) : SV_Target
            {
                if (_ProxyShape > 0.5)
                {
                    float2 d = (input.localUV - 0.5) * 2.0;
                    if (dot(d, d) > 1.0)
                        discard;
                }

                // This render target is RFloat. We are not drawing visible color; this scalar is
                // the raw device depth AER will later sample for the existing HUD color pixels.
                return saturate(_ProxyRawDepth);
            }
            ENDHLSL
        }

        // V73 pass 1: rasterize the user's canonical baked Mesh directly into the private
        // RFloat depth latch. Vertex geometry is already HUD-local XY with the meaningful
        // authored origin preserved (especially the status ring's main-circle center).
        Pass
        {
            Name "HudDepthProxyExactMeshV73"
            Blend Off
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertMeshV73
            #pragma fragment FragMeshV73

            float _ProxyRawDepth;
            float4x4 _ProxyLocalToClip;

            struct MeshAttributesV73
            {
                float4 vertex : POSITION;
            };

            struct MeshVaryingsV73
            {
                float4 positionCS : SV_POSITION;
            };

            MeshVaryingsV73 VertMeshV73(MeshAttributesV73 input)
            {
                MeshVaryingsV73 o;
                o.positionCS = mul(_ProxyLocalToClip, input.vertex);
                return o;
            }

            float FragMeshV73(MeshVaryingsV73 input) : SV_Target
            {
                return saturate(_ProxyRawDepth);
            }
            ENDHLSL
        }

        // V76 pass 2: dense planar HUD remap.
        // Geometry is the exact baked target-eye proxy mesh. Each target fragment samples the
        // corresponding local coordinate from the CURRENT real source-eye color. No scene depth,
        // sparse splats, hole fill, or stale target HUD participates in the final HUD pixels.
        Pass
        {
            Name "HudDensePlanarRemapV76"
            Blend Off
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertDenseV76
            #pragma fragment FragDenseV76

            sampler2D _HudSourceColor;
            float4x4 _ProxyLocalToClip;
            float4 _HudSourceUvTransform; // x=sx, y=sy, z=cx, w=cy
            float _HudProxyKind;          // 0=disc, 1=ring, 2=text rectangle
            float _HudTextBlackBacking;   // text mode: solid black bar + bright neutral glyphs

            struct DenseAttributesV76
            {
                float4 vertex : POSITION;
            };

            struct DenseVaryingsV76
            {
                float4 positionCS : SV_POSITION;
                float2 sourceUV : TEXCOORD0;
            };

            DenseVaryingsV76 VertDenseV76(DenseAttributesV76 input)
            {
                DenseVaryingsV76 o;
                o.positionCS = mul(_ProxyLocalToClip, input.vertex);
                o.sourceUV = float2(
                    input.vertex.x * _HudSourceUvTransform.x + _HudSourceUvTransform.z,
                    input.vertex.y * _HudSourceUvTransform.y + _HudSourceUvTransform.w);
                return o;
            }

            float4 FragDenseV76(DenseVaryingsV76 input) : SV_Target
            {
                float4 src = tex2D(_HudSourceColor, saturate(input.sourceUV));

                // User explicitly prefers a boring solid black text backing over another
                // transparent pseudo-plane. Keep bright neutral text/icons from the real source
                // eye and make everything else inside the text slab opaque black.
                if (_HudProxyKind > 1.5 && _HudTextBlackBacking > 0.5)
                {
                    float maxc = max(src.r, max(src.g, src.b));
                    float minc = min(src.r, min(src.g, src.b));
                    float neutral = 1.0 - smoothstep(0.18, 0.35, maxc - minc);
                    float bright = smoothstep(0.32, 0.62, maxc);
                    float keep = saturate(neutral * bright);
                    float3 rgb = lerp(float3(0,0,0), src.rgb, keep);
                    return float4(rgb, 1.0);
                }

                return float4(src.rgb, 1.0);
            }
            ENDHLSL
        }

        // V80 pass 3: PASSIVE depth-input forensics. This never feeds rendering.
        // Downsamples the exact global depth input V50 is about to latch into an RGBA8
        // diagnostic map:
        //   R = depth is valid for V51 (raw>0, eyeDepth>0, eyeDepth<=max)
        //   G = raw depth is nonzero
        //   B = local depth discontinuity exceeds V51's own tolerance
        //   A = log-compressed linear eye depth (0..maxMeters)
        Pass
        {
            Name "DepthInputForensicsV80"
            Blend Off
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertDepthDiagV80
            #pragma fragment FragDepthDiagV80

            Texture2DArray<float> _DepthDiagGlobalDepth;
            float4 _DepthDiagSourceSize; // xy=size, zw=1/size
            float4 _DepthDiagZParams;
            float _DepthDiagMaxMeters;
            float _DepthDiagDiscontinuityMeters;
            float _DepthDiagDiscontinuityRelative;
            float _DepthDiagArraySlice;

            struct DepthDiagAttributesV80
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct DepthDiagVaryingsV80
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            DepthDiagVaryingsV80 VertDepthDiagV80(DepthDiagAttributesV80 input)
            {
                DepthDiagVaryingsV80 o;
                o.positionCS = input.vertex;
                o.uv = input.uv;
                return o;
            }

            float EyeDepthV80(float rawDepth)
            {
                return rcp(_DepthDiagZParams.z * rawDepth + _DepthDiagZParams.w);
            }

            float RawAtV80(int2 p)
            {
                int2 maxP = int2((int)_DepthDiagSourceSize.x - 1,
                                 (int)_DepthDiagSourceSize.y - 1);
                p = clamp(p, int2(0,0), maxP);
                return _DepthDiagGlobalDepth.Load(
                    int4(p.x, p.y, (int)_DepthDiagArraySlice, 0));
            }

            float4 FragDepthDiagV80(DepthDiagVaryingsV80 input) : SV_Target
            {
                int2 maxP = int2((int)_DepthDiagSourceSize.x - 1,
                                 (int)_DepthDiagSourceSize.y - 1);

                float2 uv = saturate(input.uv);
                int2 p = clamp(
                    int2(uv * _DepthDiagSourceSize.xy),
                    int2(0,0), maxP);

                float raw = RawAtV80(p);
                float nonzero = raw > 1.0e-8 ? 1.0 : 0.0;
                float eye = nonzero > 0.5 ? EyeDepthV80(raw) : 0.0;
                float valid = (nonzero > 0.5 &&
                               eye > 0.0 &&
                               eye <= _DepthDiagMaxMeters) ? 1.0 : 0.0;

                // Same absolute/relative threshold family used by V51.
                float discontinuity = 0.0;
                if (valid > 0.5)
                {
                    float rawR = RawAtV80(p + int2(1,0));
                    float rawD = RawAtV80(p + int2(0,1));
                    float eyeR = rawR > 1.0e-8 ? EyeDepthV80(rawR) : 0.0;
                    float eyeD = rawD > 1.0e-8 ? EyeDepthV80(rawD) : 0.0;

                    float tol = max(_DepthDiagDiscontinuityMeters,
                                    _DepthDiagDiscontinuityRelative * eye);

                    bool jumpR = rawR <= 1.0e-8 || eyeR <= 0.0 ||
                                 abs(eyeR - eye) > tol;
                    bool jumpD = rawD <= 1.0e-8 || eyeD <= 0.0 ||
                                 abs(eyeD - eye) > tol;
                    discontinuity = (jumpR || jumpD) ? 1.0 : 0.0;
                }

                float maxM = max(1.0, _DepthDiagMaxMeters);
                float logDepth = valid > 0.5
                    ? saturate(log2(1.0 + eye) / log2(1.0 + maxM))
                    : 0.0;

                return float4(valid, nonzero, discontinuity, logDepth);
            }
            ENDHLSL
        }

        HLSLINCLUDE
        // ================================================================
        // V81 CAMERA-LOCAL CUSTOM-PASS DEPTH
        // ================================================================
        // These passes are diagnostic-only in V81. They sample the actual
        // RTHandle supplied by CustomPassContext.cameraDepthBuffer.
        //
        // Array source properties.
        Texture2DArray<float> _V81CameraDepthArray;
        // Plain 2D fallback source properties.
        Texture2D<float> _V81CameraDepth2D;

        float4 _V81ActiveSize; // xy active camera viewport, zw reciprocal
        float4 _V81ZParams;
        float _V81MaxMeters;
        float _V81DiscontinuityMeters;
        float _V81DiscontinuityRelative;
        float _V81ArraySlice;

        // V89: complete HDRP motion vectors copied at BeforePreRefraction.
        // Keep these in the same tiny utility shader as V81 depth so the mod still embeds
        // one auxiliary capture shader rather than multiplying AssetBundle archaeology.
        Texture2DArray<float2> _V89CameraMotionArray;
        Texture2D<float2> _V89CameraMotion2D;
        float4 _V89MotionActiveSize;
        float _V89MotionArraySlice;

        struct V81Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        V81Varyings VertV81(uint vertexID : SV_VertexID)
        {
            // Fullscreen triangle with TOP-LEFT UV convention:
            // uv (0,0) -> clip (-1,+1)
            float2 uv = float2(
                (vertexID << 1) & 2,
                vertexID & 2);

            V81Varyings o;
            o.positionCS = float4(
                uv.x * 2.0 - 1.0,
                1.0 - uv.y * 2.0,
                0.0,
                1.0);
            o.uv = uv;
            return o;
        }

        int2 V81ClampPixel(int2 p)
        {
            int2 maxP = int2(
                (int)_V81ActiveSize.x - 1,
                (int)_V81ActiveSize.y - 1);
            return clamp(p, int2(0,0), maxP);
        }

        float V81RawArray(int2 p)
        {
            p = V81ClampPixel(p);
            return _V81CameraDepthArray.Load(
                int4(p.x, p.y, (int)_V81ArraySlice, 0));
        }

        float V81Raw2D(int2 p)
        {
            p = V81ClampPixel(p);
            return _V81CameraDepth2D.Load(
                int3(p.x, p.y, 0));
        }

        float V81EyeDepth(float raw)
        {
            return rcp(
                _V81ZParams.z * raw +
                _V81ZParams.w);
        }

        float FragV81CaptureArray(
            V81Varyings input) : SV_Target
        {
            int2 p = V81ClampPixel(
                int2(input.positionCS.xy));
            return V81RawArray(p);
        }

        float FragV81Capture2D(
            V81Varyings input) : SV_Target
        {
            int2 p = V81ClampPixel(
                int2(input.positionCS.xy));
            return V81Raw2D(p);
        }

        float4 V81ClassifyRaw(
            float raw,
            float rawR,
            float rawD) 
        {
            float nonzero =
                raw > 1.0e-8 ? 1.0 : 0.0;

            float eye =
                nonzero > 0.5
                    ? V81EyeDepth(raw)
                    : 0.0;

            float valid =
                (nonzero > 0.5 &&
                 eye > 0.0 &&
                 eye <= _V81MaxMeters)
                    ? 1.0
                    : 0.0;

            float discontinuity = 0.0;

            if (valid > 0.5)
            {
                float eyeR =
                    rawR > 1.0e-8
                        ? V81EyeDepth(rawR)
                        : 0.0;

                float eyeD =
                    rawD > 1.0e-8
                        ? V81EyeDepth(rawD)
                        : 0.0;

                float tol = max(
                    _V81DiscontinuityMeters,
                    _V81DiscontinuityRelative *
                    eye);

                bool jumpR =
                    rawR <= 1.0e-8 ||
                    eyeR <= 0.0 ||
                    abs(eyeR - eye) > tol;

                bool jumpD =
                    rawD <= 1.0e-8 ||
                    eyeD <= 0.0 ||
                    abs(eyeD - eye) > tol;

                discontinuity =
                    (jumpR || jumpD)
                        ? 1.0
                        : 0.0;
            }

            float maxM =
                max(1.0, _V81MaxMeters);

            float logDepth =
                valid > 0.5
                    ? saturate(
                        log2(1.0 + eye) /
                        log2(1.0 + maxM))
                    : 0.0;

            return float4(
                valid,
                nonzero,
                discontinuity,
                logDepth);
        }

        float4 FragV81DiagArray(
            V81Varyings input) : SV_Target
        {
            int2 p = V81ClampPixel(
                int2(
                    saturate(input.uv) *
                    _V81ActiveSize.xy));

            return V81ClassifyRaw(
                V81RawArray(p),
                V81RawArray(p + int2(1,0)),
                V81RawArray(p + int2(0,1)));
        }

        float4 FragV81Diag2D(
            V81Varyings input) : SV_Target
        {
            int2 p = V81ClampPixel(
                int2(
                    saturate(input.uv) *
                    _V81ActiveSize.xy));

            return V81ClassifyRaw(
                V81Raw2D(p),
                V81Raw2D(p + int2(1,0)),
                V81Raw2D(p + int2(0,1)));
        }

        int2 V89ClampMotionPixel(int2 p)
        {
            int2 maxP = int2(
                (int)_V89MotionActiveSize.x - 1,
                (int)_V89MotionActiveSize.y - 1);
            return clamp(p, int2(0,0), maxP);
        }

        float2 FragV89MotionArray(V81Varyings input) : SV_Target
        {
            int2 p = V89ClampMotionPixel(int2(input.positionCS.xy));
            return _V89CameraMotionArray.Load(
                int4(p.x, p.y, (int)_V89MotionArraySlice, 0));
        }

        float2 FragV89Motion2D(V81Varyings input) : SV_Target
        {
            int2 p = V89ClampMotionPixel(int2(input.positionCS.xy));
            return _V89CameraMotion2D.Load(int3(p.x, p.y, 0));
        }

        ENDHLSL

        // Pass 4: full-res owned raw depth from camera-local Texture2DArray.
        Pass
        {
            Name "V81CameraLocalDepthCaptureArray"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertV81
            #pragma fragment FragV81CaptureArray
            ENDHLSL
        }

        // Pass 5: 256x256 diagnostic from camera-local Texture2DArray.
        Pass
        {
            Name "V81CameraLocalDepthDiagArray"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertV81
            #pragma fragment FragV81DiagArray
            ENDHLSL
        }

        // Pass 6: full-res owned raw depth from plain Texture2D.
        Pass
        {
            Name "V81CameraLocalDepthCapture2D"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertV81
            #pragma fragment FragV81Capture2D
            ENDHLSL
        }

        // Pass 7: 256x256 diagnostic from plain Texture2D.
        Pass
        {
            Name "V81CameraLocalDepthDiag2D"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertV81
            #pragma fragment FragV81Diag2D
            ENDHLSL
        }

        // V89 pass 8: exact active-viewport motion vectors from HDRP Texture2DArray.
        Pass
        {
            Name "V89CameraMotionCaptureArray"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertV81
            #pragma fragment FragV89MotionArray
            ENDHLSL
        }

        // V89 pass 9: exact active-viewport motion vectors from plain Texture2D.
        Pass
        {
            Name "V89CameraMotionCapture2D"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex VertV81
            #pragma fragment FragV89Motion2D
            ENDHLSL
        }
    }

    Fallback Off
}
