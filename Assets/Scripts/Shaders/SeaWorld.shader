Shader "Custom/Sea World"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        // Physically based Standard lighting model, and enable shadows on all light types
        #pragma surface surf Standard fullforwardshadows //finalcolor:mycolor vertex:myvert
        #pragma multi_compile_fog

        // Use shader model 3.0 target, to get nicer looking lighting
        #pragma target 3.0

        sampler2D _MainTex;
        uniform half4 unity_FogStart;
        uniform half4 unity_FogEnd;

        struct Input
        {
            float2 uv_MainTex;
            half fog;
            //float3 worldPos;
            //float3 worldNormal; INTERNAL_DATA
        };

        float boundsY;
        float normalOffsetWeight;
        float4 params;


        half _Glossiness;
        half _Metallic;
        fixed4 _Color;
        sampler2D ramp;


        void myvert(inout appdata_full v, out Input data) {
            UNITY_INITIALIZE_OUTPUT(Input, data);
            float pos = length(UnityObjectToViewPos(v.vertex).xyz);
            float diff = unity_FogEnd.x - unity_FogStart.x;
            float invDiff = 1.0f / diff;
            data.fog = clamp((unity_FogEnd.x - pos) * invDiff, 0.0, 1.0);
        }


        void mycolor(Input IN, SurfaceOutputStandard o, inout fixed4 color) {
#ifdef UNITY_PASS_FORWARDADD
            UNITY_APPLY_FOG_COLOR(IN.fog, color, float4(0, 0, 0, 0));
#else
            UNITY_APPLY_FOG_COLOR(IN.fog, color, unity_FogColor);
#endif
        }

        /*half4 LightingRamp(SurfaceOutput s, float3 lightDir, half3 viewDir, half atten)
        {
            half3 h = normalize(lightDir + viewDir);
            float nh = max(0, dot(s.Normal, h));
            float spec = pow(nh, s.Specular * 128.0) * s.Gloss;
            float NdotL = dot(s.Normal, lightDir);
            float NdotE = saturate(dot(s.Normal, viewDir));//pow(, _RimPower);
            float diff = (NdotL * 0.5) + 0.5;
            float4 c;
            c.rgb = (s.Albedo * _LightColor0.rgb * lerp(lerp(_UR, _UC, NdotE), lerp(_LR, _LC, NdotE), diff) + _LightColor0.rgb * _SpecColor.rgb * spec) * atten * 2;
            c.a = 1;
            return c;
        }*/


        void surf(Input IN, inout SurfaceOutputStandard o) {
            o.Albedo = tex2D(_MainTex, IN.uv_MainTex).rgb;
            //o.Normal = IN.worldNormal + IN.worldPos;
        }

        /*void surf(Input IN, inout SurfaceOutputStandard o)
        {

            float h = ((IN.worldPos.y + pow(IN.worldNormal.y*.5+.5,params.z) * params.x) / params.y)%1;
            float3 tex = tex2D(ramp, float2(h,.5));
            o.Albedo = tex;
            o.Normal = IN.worldNormal;
   
            // Metallic and smoothness come from slider variables
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
           
        }*/
        ENDCG
    }
    FallBack "Diffuse"
}
