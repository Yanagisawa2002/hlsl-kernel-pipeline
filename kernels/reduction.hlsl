#define HLSLPERF_ROOT_SIGNATURE "SRV(t0), SRV(t1), UAV(u0), UAV(u1), RootConstants(num32BitConstants=8, b0)"

ByteAddressBuffer Input0 : register(t0);
RWByteAddressBuffer Output0 : register(u0);

cbuffer DispatchParameters : register(b0)
{
    uint ElementCount;
    uint Parameter1;
    uint Parameter2;
    uint Parameter3;
    uint Parameter4;
    uint Parameter5;
    uint Parameter6;
    uint Parameter7;
};

#ifndef HLSLPERF_GROUP_SIZE
#define HLSLPERF_GROUP_SIZE 256
#endif

#ifndef HLSLPERF_ELEMENTS_PER_THREAD
#define HLSLPERF_ELEMENTS_PER_THREAD 4
#endif

groupshared uint PartialSums[HLSLPERF_GROUP_SIZE];

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void ReducePass(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    const uint groupStart = groupId.x * HLSLPERF_GROUP_SIZE * HLSLPERF_ELEMENTS_PER_THREAD;
    uint sum = 0;

    [unroll]
    for (uint item = 0; item < HLSLPERF_ELEMENTS_PER_THREAD; ++item)
    {
        const uint index = groupStart + groupIndex + item * HLSLPERF_GROUP_SIZE;
        if (index < ElementCount)
            sum += Input0.Load(index * 4);
    }

    PartialSums[groupIndex] = sum;
    GroupMemoryBarrierWithGroupSync();

    [unroll]
    for (uint stride = HLSLPERF_GROUP_SIZE / 2; stride > 0; stride >>= 1)
    {
        if (groupIndex < stride)
            PartialSums[groupIndex] += PartialSums[groupIndex + stride];
        GroupMemoryBarrierWithGroupSync();
    }

    if (groupIndex == 0)
        Output0.Store(groupId.x * 4, PartialSums[0]);
}
