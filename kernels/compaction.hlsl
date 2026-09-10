#define HLSLPERF_ROOT_SIGNATURE "SRV(t0), SRV(t1), UAV(u0), UAV(u1), RootConstants(num32BitConstants=8, b0)"

ByteAddressBuffer Input0 : register(t0);
ByteAddressBuffer Input1 : register(t1);
RWByteAddressBuffer Output0 : register(u0);
globallycoherent RWByteAddressBuffer Output1 : register(u1);

cbuffer DispatchParameters : register(b0)
{
    uint ElementCount;
    uint ElementsPerBlock;
    uint LogicalBlockCount;
    uint PredicateMask;
    uint Parameter4;
    uint Parameter5;
    uint DispatchGroupsX;
    uint DispatchGroupCount;
};

#define HLSLPERF_SCAN_LOGICAL_BLOCK_COUNT LogicalBlockCount
#define HLSLPERF_SCAN_DISPATCH_GROUPS_X DispatchGroupsX
#define HLSLPERF_SCAN_DISPATCH_GROUP_COUNT DispatchGroupCount
#ifndef HLSLPERF_SCAN_OPERATOR
#define HLSLPERF_SCAN_OPERATOR 1
#endif
#include "include/hlslperf/scan_u32.hlsli"

bool CompactionPredicate(uint value)
{
    return (value & PredicateMask) == 0;
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void ProduceCompactionFlags(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    const uint linearGroup = groupId.y * DispatchGroupsX + groupId.x;
    if (linearGroup >= DispatchGroupCount)
        return;
    const uint threadStart = linearGroup * ElementsPerBlock + groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;

#if HLSLPERF_VECTOR_WIDTH == 4
    uint4 values = 0;
    if (threadStart + 3 < ElementCount)
        values = Input0.Load4(threadStart * 4);
    else
    {
        [unroll]
        for (uint producerTail = 0; producerTail < 4; ++producerTail)
        {
            const uint index = threadStart + producerTail;
            if (index < ElementCount)
                values[producerTail] = Input0.Load(index * 4);
        }
    }
    [unroll]
    for (uint producerComponent = 0; producerComponent < 4; ++producerComponent)
    {
        const uint index = threadStart + producerComponent;
        if (index < ElementCount)
            Output0.Store(index * 4, CompactionPredicate(values[producerComponent]) ? 1 : 0);
    }
#elif HLSLPERF_VECTOR_WIDTH == 1
    [unroll]
    for (uint producerItem = 0; producerItem < HLSLPERF_ELEMENTS_PER_THREAD; ++producerItem)
    {
        const uint index = threadStart + producerItem;
        if (index < ElementCount)
        {
            const uint value = Input0.Load(index * 4);
            Output0.Store(index * 4, CompactionPredicate(value) ? 1 : 0);
        }
    }
#else
#error HLSLPERF_VECTOR_WIDTH must be 1 or 4.
#endif
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void ScatterCompactedValues(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    const uint linearGroup = groupId.y * DispatchGroupsX + groupId.x;
    if (linearGroup >= DispatchGroupCount)
        return;
    const uint threadStart = linearGroup * ElementsPerBlock + groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;

    [unroll]
    for (uint scatterItem = 0; scatterItem < HLSLPERF_ELEMENTS_PER_THREAD; ++scatterItem)
    {
        const uint index = threadStart + scatterItem;
        if (index < ElementCount)
        {
            const uint value = Input0.Load(index * 4);
            const uint prefix = Input1.Load(index * 4);
            if (CompactionPredicate(value))
                Output0.Store((prefix + 1) * 4, value);
            if (index == ElementCount - 1)
                Output0.Store(0, prefix + (CompactionPredicate(value) ? 1 : 0));
        }
    }
}

#define HLSLPERF_FUSED_ITEMS_PER_THREAD \
    (HLSLPERF_ELEMENTS_PER_THREAD * HLSLPERF_SINGLE_PASS_ITEMS_SCALE)

// This is the fused producer -> exclusive scan -> scatter path. It never
// materializes the flag or prefix arrays and evaluates the predicate once.
[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
HLSLPERF_WAVE_ATTRIBUTE
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void FusedCompactSinglePass(uint groupIndex : SV_GroupIndex)
{
    if (groupIndex == 0)
        SinglePassEpoch = (Output1.Load(0) & 0x3fffffffu) << 2;
    GroupMemoryBarrierWithGroupSync();

    [loop]
    while (true)
    {
        if (groupIndex == 0)
            Output1.InterlockedAdd(4, 1, SinglePassBlockIndex);
        GroupMemoryBarrierWithGroupSync();
        if (SinglePassBlockIndex >= LogicalBlockCount)
            break;

        const uint blockStart = SinglePassBlockIndex * ElementsPerBlock;
        const uint threadStart = blockStart + groupIndex * HLSLPERF_FUSED_ITEMS_PER_THREAD;
        uint values[HLSLPERF_FUSED_ITEMS_PER_THREAD];
        uint flags[HLSLPERF_FUSED_ITEMS_PER_THREAD];
        uint localPrefix[HLSLPERF_FUSED_ITEMS_PER_THREAD];

#if HLSLPERF_VECTOR_WIDTH == 4
        [unroll]
        for (uint fusedChunk = 0; fusedChunk < HLSLPERF_FUSED_ITEMS_PER_THREAD; fusedChunk += 4)
        {
            uint4 loaded = 0;
            if (fusedChunk + 3 < HLSLPERF_FUSED_ITEMS_PER_THREAD &&
                threadStart + fusedChunk + 3 < ElementCount)
            {
                loaded = Input0.Load4((threadStart + fusedChunk) * 4);
            }
            else
            {
                [unroll]
                for (uint fusedTail = 0; fusedTail < 4; ++fusedTail)
                {
                    const uint item = fusedChunk + fusedTail;
                    const uint index = threadStart + item;
                    if (item < HLSLPERF_FUSED_ITEMS_PER_THREAD && index < ElementCount)
                        loaded[fusedTail] = Input0.Load(index * 4);
                }
            }
            [unroll]
            for (uint fusedComponent = 0; fusedComponent < 4; ++fusedComponent)
            {
                const uint item = fusedChunk + fusedComponent;
                if (item < HLSLPERF_FUSED_ITEMS_PER_THREAD)
                    values[item] = loaded[fusedComponent];
            }
        }
#else
        [unroll]
        for (uint fusedLoadItem = 0; fusedLoadItem < HLSLPERF_FUSED_ITEMS_PER_THREAD; ++fusedLoadItem)
        {
            const uint index = threadStart + fusedLoadItem;
            values[fusedLoadItem] = index < ElementCount ? Input0.Load(index * 4) : 0;
        }
#endif

        uint threadTotal = 0;
        [unroll]
        for (uint fusedPrefixItem = 0; fusedPrefixItem < HLSLPERF_FUSED_ITEMS_PER_THREAD; ++fusedPrefixItem)
        {
            const uint index = threadStart + fusedPrefixItem;
            const uint flag = index < ElementCount && CompactionPredicate(values[fusedPrefixItem]) ? 1 : 0;
            flags[fusedPrefixItem] = flag;
            localPrefix[fusedPrefixItem] = threadTotal;
            threadTotal += flag;
        }

        const uint waveSize = WaveGetLaneCount();
        const uint waveIndex = groupIndex / waveSize;
        const uint wavePrefix = WavePrefixSum(threadTotal);
        const uint waveTotal = WaveActiveSum(threadTotal);
        if (WaveIsFirstLane())
            ThreadTotals[waveIndex] = waveTotal;
        GroupMemoryBarrierWithGroupSync();

        const uint waveCount = (HLSLPERF_GROUP_SIZE + waveSize - 1) / waveSize;
        if (groupIndex == 0)
        {
            uint runningWaveTotal = 0;
            for (uint fusedWave = 0; fusedWave < waveCount; ++fusedWave)
            {
                const uint currentWaveTotal = ThreadTotals[fusedWave];
                ThreadTotals[fusedWave] = runningWaveTotal;
                runningWaveTotal += currentWaveTotal;
            }
            SinglePassBlockTotal = runningWaveTotal;
        }
        GroupMemoryBarrierWithGroupSync();
        const uint localBlockPrefix = ThreadTotals[waveIndex] + wavePrefix;

        if (groupIndex == 0)
        {
            const uint aggregateToken = SinglePassEpoch | 1;
            const uint prefixToken = SinglePassEpoch | 2;
            const uint currentState = 8 + SinglePassBlockIndex * 12;
            Output1.Store(currentState, SinglePassBlockTotal);
            DeviceMemoryBarrier();
            uint ignoredStatus;
            Output1.InterlockedExchange(currentState + 8, aggregateToken, ignoredStatus);

            uint blockPrefix = 0;
            uint lookbackSuffix = 0;
            if (SinglePassBlockIndex > 0)
            {
                uint predecessor = SinglePassBlockIndex - 1;
                [allow_uav_condition]
                while (true)
                {
                    const uint previousState = 8 + predecessor * 12;
                    uint observedStatus;
                    Output1.InterlockedCompareExchange(
                        previousState + 8,
                        0xffffffffu,
                        0xffffffffu,
                        observedStatus);
                    if (observedStatus == prefixToken)
                    {
                        DeviceMemoryBarrier();
                        blockPrefix = Output1.Load(previousState + 4) + lookbackSuffix;
                        break;
                    }
                    if (observedStatus == aggregateToken)
                    {
                        DeviceMemoryBarrier();
                        lookbackSuffix += Output1.Load(previousState);
                        if (predecessor == 0)
                        {
                            blockPrefix = lookbackSuffix;
                            break;
                        }
                        predecessor--;
                    }
                }
            }
            SinglePassBlockPrefix = blockPrefix;
            Output1.Store(currentState + 4, blockPrefix + SinglePassBlockTotal);
            DeviceMemoryBarrier();
            Output1.InterlockedExchange(currentState + 8, prefixToken, ignoredStatus);
            if (SinglePassBlockIndex == LogicalBlockCount - 1)
                Output0.Store(0, blockPrefix + SinglePassBlockTotal);
        }
        GroupMemoryBarrierWithGroupSync();

        [unroll]
        for (uint fusedScatterItem = 0; fusedScatterItem < HLSLPERF_FUSED_ITEMS_PER_THREAD; ++fusedScatterItem)
        {
            if (flags[fusedScatterItem] != 0)
            {
                const uint outputIndex =
                    SinglePassBlockPrefix + localBlockPrefix + localPrefix[fusedScatterItem];
                Output0.Store((outputIndex + 1) * 4, values[fusedScatterItem]);
            }
        }
        GroupMemoryBarrierWithGroupSync();
    }
}

#if HLSLPERF_SCAN_WAVE_TILED
#define HLSLPERF_WAVE_TILED_COMPACTION 1
#include "include/hlslperf/scan_wave_tiled_u32.hlsli"
#endif
