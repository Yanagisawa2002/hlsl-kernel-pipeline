#define HLSLPERF_ROOT_SIGNATURE "SRV(t0), SRV(t1), UAV(u0), UAV(u1), RootConstants(num32BitConstants=8, b0)"

ByteAddressBuffer Input0 : register(t0);
ByteAddressBuffer Input1 : register(t1);
RWByteAddressBuffer Output0 : register(u0);
globallycoherent RWByteAddressBuffer Output1 : register(u1);

cbuffer DispatchParameters : register(b0)
{
    uint ElementCount;
    uint ElementsPerBlock;
    uint RadixBit;
    uint Parameter3;
    uint Parameter4;
    uint Parameter5;
    uint DispatchGroupsX;
    uint DispatchGroupCount;
};

#define HLSLPERF_SCAN_LOGICAL_BLOCK_COUNT RadixBit
#define HLSLPERF_SCAN_DISPATCH_GROUPS_X DispatchGroupsX
#define HLSLPERF_SCAN_DISPATCH_GROUP_COUNT DispatchGroupCount
#ifndef HLSLPERF_SCAN_OPERATOR
#define HLSLPERF_SCAN_OPERATOR 1
#endif
#include "include/hlslperf/scan_u32.hlsli"

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void ProduceZeroFlags(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    const uint linearGroup = groupId.y * DispatchGroupsX + groupId.x;
    if (linearGroup >= DispatchGroupCount)
        return;
    const uint threadStart = linearGroup * ElementsPerBlock + groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;

#if HLSLPERF_VECTOR_WIDTH == 4
    uint4 keys = 0;
    if (threadStart + 3 < ElementCount)
        keys = Input0.Load4(threadStart * 4);
    else
    {
        [unroll]
        for (uint radixTail = 0; radixTail < 4; ++radixTail)
        {
            const uint index = threadStart + radixTail;
            if (index < ElementCount)
                keys[radixTail] = Input0.Load(index * 4);
        }
    }
    [unroll]
    for (uint radixComponent = 0; radixComponent < 4; ++radixComponent)
    {
        const uint index = threadStart + radixComponent;
        if (index < ElementCount)
            Output0.Store(index * 4, ((keys[radixComponent] >> RadixBit) & 1) == 0 ? 1 : 0);
    }
#elif HLSLPERF_VECTOR_WIDTH == 1
    [unroll]
    for (uint radixItem = 0; radixItem < HLSLPERF_ELEMENTS_PER_THREAD; ++radixItem)
    {
        const uint index = threadStart + radixItem;
        if (index < ElementCount)
        {
            const uint key = Input0.Load(index * 4);
            Output0.Store(index * 4, ((key >> RadixBit) & 1) == 0 ? 1 : 0);
        }
    }
#else
#error HLSLPERF_VECTOR_WIDTH must be 1 or 4.
#endif
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void ScatterRadixBit(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    const uint linearGroup = groupId.y * DispatchGroupsX + groupId.x;
    if (linearGroup >= DispatchGroupCount)
        return;
    const uint lastKey = Input0.Load((ElementCount - 1) * 4);
    const uint zeroCount = Input1.Load((ElementCount - 1) * 4) +
        ((((lastKey >> RadixBit) & 1) == 0) ? 1 : 0);
    const uint threadStart = linearGroup * ElementsPerBlock + groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;

    [unroll]
    for (uint scatterItem = 0; scatterItem < HLSLPERF_ELEMENTS_PER_THREAD; ++scatterItem)
    {
        const uint index = threadStart + scatterItem;
        if (index < ElementCount)
        {
            const uint key = Input0.Load(index * 4);
            const uint zeroPrefix = Input1.Load(index * 4);
            const bool isZero = ((key >> RadixBit) & 1) == 0;
            const uint destination = isZero ? zeroPrefix : zeroCount + index - zeroPrefix;
            Output0.Store(destination * 4, key);
        }
    }
}
