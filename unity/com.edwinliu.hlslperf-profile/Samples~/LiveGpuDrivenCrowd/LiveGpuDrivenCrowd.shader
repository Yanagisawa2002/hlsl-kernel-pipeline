Shader "HlslPerf/LiveGpuDrivenCrowd"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            StructuredBuffer<float4> _VisibleAgents;
            float3 _CameraRight;
            float3 _CameraUp;

            struct Attributes
            {
                float3 positionOS : POSITION;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 color : TEXCOORD0;
            };

            float Hash11(float p)
            {
                p = frac(p * 0.1031);
                p *= p + 33.33;
                p *= p + p;
                return frac(p);
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float4 agent = _VisibleAgents[input.instanceID];
                float size = max(agent.w, 0.05) * 1.6;
                float3 world = agent.xyz
                    + _CameraRight * input.positionOS.x * size
                    + _CameraUp * input.positionOS.y * size;
                output.positionCS = mul(UNITY_MATRIX_VP, float4(world, 1.0));

                float h = Hash11(dot(agent.xyz, float3(0.071, 0.113, 0.173)));
                output.color = lerp(float3(0.15, 0.72, 1.0), float3(1.0, 0.37, 0.13), h);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                return float4(input.color, 1.0);
            }
            ENDHLSL
        }
    }
}
