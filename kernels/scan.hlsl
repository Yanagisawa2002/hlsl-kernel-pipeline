#define HLSLPERF_ROOT_SIGNATURE "SRV(t0), SRV(t1), UAV(u0), UAV(u1), RootConstants(num32BitConstants=8, b0)"

ByteAddressBuffer Input0 : register(t0);
RWByteAddressBuffer Output0 : register(u0);
globallycoherent RWByteAddressBuffer Output1 : register(u1);

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

#ifndef HLSLPERF_SCAN_BACKEND
#define HLSLPERF_SCAN_BACKEND 1
#endif

#ifndef HLSLPERF_SINGLE_PASS_ITEMS_SCALE
#define HLSLPERF_SINGLE_PASS_ITEMS_SCALE 1
#endif

#define HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD \
    (HLSLPERF_ELEMENTS_PER_THREAD * HLSLPERF_SINGLE_PASS_ITEMS_SCALE)

groupshared uint ThreadTotals[HLSLPERF_GROUP_SIZE];
groupshared uint SinglePassEpoch;
groupshared uint SinglePassBlockIndex;
groupshared uint SinglePassBlockTotal;
groupshared uint SinglePassBlockPrefix;

// Single-pass state uses an 8-byte header followed by 12 bytes per logical block:
// [epoch, nextBlock] [aggregate, inclusivePrefix, epoch|status]...
// The reset is O(1) and is part of the timed plan; it does not touch input data.
[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(1, 1, 1)]
void ResetSinglePassState(uint groupIndex : SV_GroupIndex)
{
    if (groupIndex == 0)
    {
        uint previousEpoch;
        Output0.InterlockedAdd(0, 1, previousEpoch);
        if ((previousEpoch & 0x3fffffffu) == 0x3fffffffu)
            Output0.Store(0, 1);
        Output0.Store(4, 0);
    }
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void SinglePassScan(uint groupIndex : SV_GroupIndex)
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
        if (SinglePassBlockIndex >= Parameter2)
            break;

        const uint blockStart = SinglePassBlockIndex * ElementsPerBlock;
        const uint threadStart = blockStart + groupIndex * HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD;
        uint localPrefix[HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD];
        uint threadTotal = 0;
        [unroll]
        for (uint item = 0; item < HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD; ++item)
        {
            localPrefix[item] = threadTotal;
            const uint index = threadStart + item;
            if (index < ElementCount)
                threadTotal += Input0.Load(index * 4);
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
            for (uint wave = 0; wave < waveCount; ++wave)
            {
                const uint currentWaveTotal = ThreadTotals[wave];
                ThreadTotals[wave] = runningWaveTotal;
                runningWaveTotal += currentWaveTotal;
            }
            SinglePassBlockTotal = runningWaveTotal;
        }
        GroupMemoryBarrierWithGroupSync();
        const uint localBlockPrefix = ThreadTotals[waveIndex] + wavePrefix;

        if (groupIndex == 0)
        {
            // Logical ids are claimed in scheduling order. Therefore every block
            // observed during look-back has already started and can publish an
            // aggregate, avoiding a dependency on an unscheduled threadgroup.
            const uint aggregateToken = SinglePassEpoch | 1;
            const uint prefixToken = SinglePassEpoch | 2;
            const uint currentState = 8 + SinglePassBlockIndex * 12;
            Output1.Store(currentState, SinglePassBlockTotal);
            DeviceMemoryBarrier();
            uint ignoredStatus;
            Output1.InterlockedExchange(currentState + 8, aggregateToken, ignoredStatus);

            uint blockPrefix = 0;
            if (SinglePassBlockIndex > 0)
            {
                uint predecessor = SinglePassBlockIndex - 1;
                // Decoupled look-back accumulates ready aggregates and stops at
                // the nearest completed inclusive prefix instead of serially
                // waiting for the immediate predecessor's full scan.
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
                        blockPrefix += Output1.Load(previousState + 4);
                        break;
                    }
                    if (observedStatus == aggregateToken)
                    {
                        DeviceMemoryBarrier();
                        blockPrefix += Output1.Load(previousState);
                        if (predecessor == 0)
                            break;
                        predecessor--;
                    }
                }
            }
            SinglePassBlockPrefix = blockPrefix;

            Output1.Store(currentState + 4, blockPrefix + SinglePassBlockTotal);
            DeviceMemoryBarrier();
            Output1.InterlockedExchange(currentState + 8, prefixToken, ignoredStatus);
        }
        GroupMemoryBarrierWithGroupSync();

        [unroll]
        for (uint storeItem = 0; storeItem < HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD; ++storeItem)
        {
            const uint index = threadStart + storeItem;
            if (index < ElementCount)
                Output0.Store(
                    index * 4,
                    SinglePassBlockPrefix + localBlockPrefix + localPrefix[storeItem]);
        }
        GroupMemoryBarrierWithGroupSync();
    }
}

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

#if HLSLPERF_SCAN_BACKEND == 2
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
        for (uint wave = 0; wave < waveCount; ++wave)
        {
            const uint currentWaveTotal = ThreadTotals[wave];
            ThreadTotals[wave] = runningWaveTotal;
            runningWaveTotal += currentWaveTotal;
        }
        Output1.Store(groupId.x * 4, runningWaveTotal);
    }
    GroupMemoryBarrierWithGroupSync();
    const uint blockPrefix = ThreadTotals[waveIndex] + wavePrefix;
#else
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
#endif
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
