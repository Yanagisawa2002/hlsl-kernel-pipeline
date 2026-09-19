Shader "HlslPerf/Crossover"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Cull Off
            ZWrite On
            ZTest LEqual

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
                // Stable agent-specific depth, exactly spaced within the float mantissa.
                // Both arms share this rule so append ordering cannot change overlap colors.
                float depth = (agentIndex + 1u) * (1.0 / 8388608.0);
                #if defined(UNITY_REVERSED_Z)
                depth = 1.0 - depth;
                #endif
                output.positionCS = float4(clip.x, clip.y, depth, 1);
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
