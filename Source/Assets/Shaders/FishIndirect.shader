Shader "FishSwarm/FishIndirect"
{
    // Indirect-instanced fish shader. Per-instance position/heading/state come from a
    // StructuredBuffer<FishInstance> filled by the CPU sim; the transform is built in the vertex
    // shader (no CPU matrices). Colour is chosen per state; simple lambert + fog to match the scene.
    Properties { }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct FishInstance
            {
                float4 pos; // xyz = world position
                float4 fwd; // xyz = heading (normalised), w = state index
            };

            StructuredBuffer<FishInstance> _Instances;
            float3 _FishScale;
            float4 _StateColors[4];

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 color : TEXCOORD1;
                float fogCoord : TEXCOORD2;
            };

            Varyings vert(Attributes IN, uint instanceID : SV_InstanceID)
            {
                FishInstance inst = _Instances[instanceID];

                float3 p = inst.pos.xyz;
                float3 f = normalize(inst.fwd.xyz);
                // Orthonormal basis around the heading (equivalent to LookRotation, built on the GPU).
                float3 upRef = abs(f.y) > 0.99 ? float3(0, 0, 1) : float3(0, 1, 0);
                float3 r = normalize(cross(upRef, f));
                float3 u = cross(f, r);

                float3 local = IN.positionOS.xyz * _FishScale;
                float3 worldPos = p + r * local.x + u * local.y + f * local.z;
                float3 worldNrm = normalize(r * IN.normalOS.x + u * IN.normalOS.y + f * IN.normalOS.z);

                Varyings o;
                o.positionCS = TransformWorldToHClip(worldPos);
                o.normalWS = worldNrm;
                uint state = (uint)inst.fwd.w;
                o.color = _StateColors[min(state, 3u)].rgb;
                o.fogCoord = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 n = normalize(IN.normalWS);
                Light mainLight = GetMainLight();
                float ndl = saturate(dot(n, mainLight.direction));

                // Ambient probe (SH) + lambert main light, matching URP/Lit's diffuse far more
                // closely than a flat ambient constant, which crushed contrast.
                float3 ambient = SampleSH(n);
                float3 lit = IN.color * (ambient + mainLight.color * ndl);

                lit = MixFog(lit, IN.fogCoord);
                return half4(lit, 1);
            }
            ENDHLSL
        }
    }
}
