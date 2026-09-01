#define HLSLPERF_ROOT_SIGNATURE "SRV(t0), SRV(t1), UAV(u0), UAV(u1), RootConstants(num32BitConstants=8, b0)"

ByteAddressBuffer Input0 : register(t0);
RWByteAddressBuffer Output0 : register(u0);
RWByteAddressBuffer Output1 : register(u1);

cbuffer DispatchParameters : register(b0)
{
    uint ElementCount;
    uint ElementsPerBlock;
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

groupshared uint ThreadTotals[HLSLPERF_GROUP_SIZE];

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void BlockScanPass(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    const uint blockStart = groupId.x * HLSLPERF_GROUP_SIZE * HLSLPERF_ELEMENTS_PER_THREAD;
    const uint threadStart = blockStart + groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;
    uint localPrefix[HLSLPERF_ELEMENTS_PER_THREAD];
    uint threadTotal = 0;

    [unroll]
    for (uint loadItem = 0; loadItem < HLSLPERF_ELEMENTS_PER_THREAD; ++loadItem)
    {
        localPrefix[loadItem] = threadTotal;
        const uint index = threadStart + loadItem;
        if (index < ElementCount)
            threadTotal += Input0.Load(index * 4);
    }

    ThreadTotals[groupIndex] = threadTotal;
    GroupMemoryBarrierWithGroupSync();

    [unroll]
    for (uint upsweepOffset = 1; upsweepOffset < HLSLPERF_GROUP_SIZE; upsweepOffset <<= 1)
    {
        const uint node = (groupIndex + 1) * upsweepOffset * 2 - 1;
        if (node < HLSLPERF_GROUP_SIZE)
            ThreadTotals[node] += ThreadTotals[node - upsweepOffset];
        GroupMemoryBarrierWithGroupSync();
    }

    if (groupIndex == 0)
    {
        Output1.Store(groupId.x * 4, ThreadTotals[HLSLPERF_GROUP_SIZE - 1]);
        ThreadTotals[HLSLPERF_GROUP_SIZE - 1] = 0;
    }
    GroupMemoryBarrierWithGroupSync();

    [unroll]
    for (uint downsweepOffset = HLSLPERF_GROUP_SIZE / 2; downsweepOffset > 0; downsweepOffset >>= 1)
    {
        const uint node = (groupIndex + 1) * downsweepOffset * 2 - 1;
        if (node < HLSLPERF_GROUP_SIZE)
        {
            const uint left = ThreadTotals[node - downsweepOffset];
            ThreadTotals[node - downsweepOffset] = ThreadTotals[node];
            ThreadTotals[node] += left;
        }
        GroupMemoryBarrierWithGroupSync();
    }

    const uint blockPrefix = ThreadTotals[groupIndex];
    [unroll]
    for (uint storeItem = 0; storeItem < HLSLPERF_ELEMENTS_PER_THREAD; ++storeItem)
    {
        const uint index = threadStart + storeItem;
        if (index < ElementCount)
            Output0.Store(index * 4, blockPrefix + localPrefix[storeItem]);
    }
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void AddScanOffsets(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    const uint blockPrefix = Input0.Load(groupId.x * 4);
    const uint threadStart = groupId.x * ElementsPerBlock + groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;
    [unroll]
    for (uint item = 0; item < HLSLPERF_ELEMENTS_PER_THREAD; ++item)
    {
        const uint index = threadStart + item;
        if (index < ElementCount)
            Output0.Store(index * 4, Output0.Load(index * 4) + blockPrefix);
    }
}
