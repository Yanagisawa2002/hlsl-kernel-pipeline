// Same scene, visibility predicate and pixel renderer as the existing demo.
#define HLSLPERF_CROWD_RUNTIME_COUNTS 1
#include "crowd-vfx.hlsl"

// Materialized producers write the vector padding consumed by unmodified RTS.
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void ProducePaddedCrowdVisibilityFlags(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    const uint linearGroup = groupId.y * DispatchGroupsX + groupId.x;
    if (linearGroup >= DispatchGroupCount) return;
    const uint start = linearGroup * ElementsPerBlock + groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;
    [unroll]
    for (uint item = 0; item < HLSLPERF_ELEMENTS_PER_THREAD; ++item)
    {
        const uint index = start + item;
        if (index < ElementCount)
            Output0.Store(index * 4, CrowdVisible(Input0.Load(index * 4), Parameter4, Parameter3, Parameter5) ? 1 : 0);
        else if (index < ((ElementCount + 3) & ~3u))
            Output0.Store(index * 4, 0);
    }
}
