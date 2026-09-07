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

#ifndef HLSLPERF_RADIX_PAIRS
#define HLSLPERF_RADIX_PAIRS 0
#endif
#ifndef HLSLPERF_RADIX_BITS
#define HLSLPERF_RADIX_BITS 1
#endif
#define RADIX_STRIDE (4 + 4 * HLSLPERF_RADIX_PAIRS)

uint LoadRadixKey(uint index) { return Input0.Load(index * RADIX_STRIDE); }
void StoreRadixRecord(uint destination, uint source, uint key)
{
#if HLSLPERF_RADIX_PAIRS
    Output0.Store2(destination * 8, uint2(key, Input0.Load(source * 8 + 4)));
#else
    Output0.Store(destination * 4, key);
#endif
}

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
                keys[radixTail] = LoadRadixKey(index);
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
            const uint key = LoadRadixKey(index);
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
    const uint lastKey = LoadRadixKey(ElementCount - 1);
    const uint zeroCount = Input1.Load((ElementCount - 1) * 4) +
        ((((lastKey >> RadixBit) & 1) == 0) ? 1 : 0);
    const uint threadStart = linearGroup * ElementsPerBlock + groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;

    [unroll]
    for (uint scatterItem = 0; scatterItem < HLSLPERF_ELEMENTS_PER_THREAD; ++scatterItem)
    {
        const uint index = threadStart + scatterItem;
        if (index < ElementCount)
        {
            const uint key = LoadRadixKey(index);
            const uint zeroPrefix = Input1.Load(index * 4);
            const bool isZero = ((key >> RadixBit) & 1) == 0;
            const uint destination = isZero ? zeroPrefix : zeroCount + index - zeroPrefix;
            StoreRadixRecord(destination, index, key);
        }
    }
}

// Bin-major block histograms make a single exclusive scan supply both
// lower-bin totals and preceding-block totals. Every cell is overwritten.
#if HLSLPERF_RADIX_BITS == 4 || HLSLPERF_RADIX_BITS == 8
#define RADIX_BINS (1 << HLSLPERF_RADIX_BITS)
#define RADIX_BLOCK (HLSLPERF_GROUP_SIZE * HLSLPERF_ELEMENTS_PER_THREAD)
groupshared uint RadixHistogram[RADIX_BINS];
groupshared uint RadixDigits[RADIX_BLOCK];

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void BuildRadixHistogram(uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex)
{
    const uint block = groupId.y * DispatchGroupsX + groupId.x;
    if (block >= DispatchGroupCount) return;
    for (uint bin = lane; bin < RADIX_BINS; bin += HLSLPERF_GROUP_SIZE)
        RadixHistogram[bin] = 0;
    GroupMemoryBarrierWithGroupSync();
    [unroll]
    for (uint item = 0; item < HLSLPERF_ELEMENTS_PER_THREAD; item++)
    {
        const uint index = block * ElementsPerBlock + lane * HLSLPERF_ELEMENTS_PER_THREAD + item;
        if (index < ElementCount)
        {
            const uint digit = (LoadRadixKey(index) >> RadixBit) & Parameter3;
            InterlockedAdd(RadixHistogram[digit], 1);
        }
    }
    GroupMemoryBarrierWithGroupSync();
    for (uint storeBin = lane; storeBin < RADIX_BINS; storeBin += HLSLPERF_GROUP_SIZE)
        Output0.Store((storeBin * DispatchGroupCount + block) * 4, RadixHistogram[storeBin]);
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void ScatterRadixDigit(uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex)
{
    const uint block = groupId.y * DispatchGroupsX + groupId.x;
    if (block >= DispatchGroupCount) return;
    [unroll]
    for (uint loadItem = 0; loadItem < HLSLPERF_ELEMENTS_PER_THREAD; loadItem++)
    {
        const uint local = lane * HLSLPERF_ELEMENTS_PER_THREAD + loadItem;
        const uint index = block * ElementsPerBlock + local;
        RadixDigits[local] = index < ElementCount ? (LoadRadixKey(index) >> RadixBit) & Parameter3 : 0xffffffff;
    }
    GroupMemoryBarrierWithGroupSync();
    [unroll]
    for (uint item = 0; item < HLSLPERF_ELEMENTS_PER_THREAD; item++)
    {
        const uint local = lane * HLSLPERF_ELEMENTS_PER_THREAD + item;
        const uint index = block * ElementsPerBlock + local;
        if (index < ElementCount)
        {
            const uint digit = RadixDigits[local];
            uint rank = 0;
            // Explicit original-order rank: atomic arrival order never decides stability.
            for (uint previous = 0; previous < local; previous++)
                rank += RadixDigits[previous] == digit ? 1 : 0;
            const uint offset = Input1.Load((digit * DispatchGroupCount + block) * 4);
            StoreRadixRecord(offset + rank, index, LoadRadixKey(index));
        }
    }
}
#endif

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void EmptyRadix(uint lane : SV_GroupIndex)
{
    if (lane == 0)
    {
        Output0.Store(0, 0);
#if HLSLPERF_RADIX_PAIRS
        Output0.Store(4, 0);
#endif
    }
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void SplitRadixPairs(uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex)
{
    const uint block = groupId.y * DispatchGroupsX + groupId.x;
    if (block >= DispatchGroupCount) return;
    [unroll]
    for (uint item = 0; item < HLSLPERF_ELEMENTS_PER_THREAD; item++)
    {
        const uint index = block * ElementsPerBlock + lane * HLSLPERF_ELEMENTS_PER_THREAD + item;
        if (index < max(ElementCount, 1u))
        {
            const uint2 record = Input0.Load2(index * 8);
            Output0.Store(index * 4, record.x);
            Output1.Store(index * 4, record.y);
        }
    }
}
