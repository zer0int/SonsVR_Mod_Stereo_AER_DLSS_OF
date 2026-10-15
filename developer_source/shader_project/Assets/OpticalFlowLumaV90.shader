Shader "Hidden/SonsVR/OpticalFlowLumaV90"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "OpticalFlowLumaV90"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            struct Attributes
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.position = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float Frag(Varyings i) : SV_Target
            {
                float3 rgb = tex2D(_MainTex, i.uv).rgb;
                // Rec.709 luminance.  The eye RT is already postprocessed LDR ARGB32;
                // this R8 copy exists only to give NVOFA an unambiguous supported format.
                return saturate(dot(rgb, float3(0.2126, 0.7152, 0.0722)));
            }
            ENDHLSL
        }
    }
}
