Shader "HlslPerf/LiveGpuDrivenCrowd"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            struct Agent
            {
                float2 position;
                float size;
                float4 color;
            };

            StructuredBuffer<Agent> _Agents;
            StructuredBuffer<uint> _VisibleAgents;
            float4 _ViewRect; // center.xy, half extents.zw

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR0;
            };

            static const float2 Quad[6] =
            {
                float2(-1, -1), float2(-1, 1), float2(1, 1),
                float2(-1, -1), float2(1, 1), float2(1, -1)
            };

            Varyings Vert(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
            {
                Varyings output;
                uint agentIndex = _VisibleAgents[instanceId];
                Agent agent = _Agents[agentIndex];
                float2 world = agent.position + Quad[vertexId] * agent.size;
                float2 clip = (world - _ViewRect.xy) / _ViewRect.zw;
                output.positionCS = float4(clip.x, clip.y, 0, 1);
                output.color = agent.color;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                return input.color;
            }
            ENDHLSL
        }
    }
}
