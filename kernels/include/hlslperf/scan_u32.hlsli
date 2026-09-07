#ifndef HLSLPERF_SCAN_U32_INCLUDED
#define HLSLPERF_SCAN_U32_INCLUDED

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

#ifndef HLSLPERF_SCAN_OPERATOR
#define HLSLPERF_SCAN_OPERATOR 1
#endif

#ifndef HLSLPERF_VECTOR_WIDTH
#define HLSLPERF_VECTOR_WIDTH 1
#endif

#ifndef HLSLPERF_WAVE_SIZE
#define HLSLPERF_WAVE_SIZE 0
#endif

#if HLSLPERF_WAVE_SIZE == 32
#define HLSLPERF_WAVE_ATTRIBUTE [WaveSize(32)]
#elif HLSLPERF_WAVE_SIZE == 64
#define HLSLPERF_WAVE_ATTRIBUTE [WaveSize(64)]
#elif HLSLPERF_WAVE_SIZE == 0
#define HLSLPERF_WAVE_ATTRIBUTE
#else
#error HLSLPERF_WAVE_SIZE must be 0, 32, or 64.
#endif

#ifndef HLSLPERF_SCAN_LOGICAL_BLOCK_COUNT
#error HLSLPERF_SCAN_LOGICAL_BLOCK_COUNT must name the root constant containing the logical block count.
#endif

#ifndef HLSLPERF_SCAN_DISPATCH_GROUPS_X
#error HLSLPERF_SCAN_DISPATCH_GROUPS_X must name the root constant containing Dispatch.X.
#endif

#ifndef HLSLPERF_SCAN_DISPATCH_GROUP_COUNT
#error HLSLPERF_SCAN_DISPATCH_GROUP_COUNT must name the root constant containing the logical group count.
#endif

#define HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD \
    (HLSLPERF_ELEMENTS_PER_THREAD * HLSLPERF_SINGLE_PASS_ITEMS_SCALE)

uint HlslPerfScanIdentity()
{
#if HLSLPERF_SCAN_OPERATOR == 2
    return 0xffffffffu;
#else
    return 0;
#endif
}

uint HlslPerfScanCombine(uint left, uint right)
{
#if HLSLPERF_SCAN_OPERATOR == 1
    return left + right;
#elif HLSLPERF_SCAN_OPERATOR == 2
    return min(left, right);
#elif HLSLPERF_SCAN_OPERATOR == 3
    return max(left, right);
#elif HLSLPERF_SCAN_OPERATOR == 4
    return left ^ right;
#else
#error HLSLPERF_SCAN_OPERATOR must be 1(add), 2(min), 3(max), or 4(xor).
#endif
}

void HlslPerfWaveExclusiveScan(uint value, out uint prefix, out uint total)
{
#if HLSLPERF_SCAN_OPERATOR == 1
    prefix = WavePrefixSum(value);
    total = WaveActiveSum(value);
#else
    const uint lane = WaveGetLaneIndex();
    const uint waveSize = WaveGetLaneCount();
    uint inclusive = value;
    // DXC cannot prove WaveGetLaneCount() at compile time even when WaveSize is
    // attached to the entry point. Unroll the architectural maximum and keep
    // the remaining predicate wave-uniform; Wave32 folds away the final step.
    [unroll(6)]
    for (uint offset = 1; offset < 64; offset <<= 1)
    {
        if (offset < waveSize)
        {
            const uint sourceLane = lane >= offset ? lane - offset : lane;
            const uint previous = WaveReadLaneAt(inclusive, sourceLane);
            if (lane >= offset)
                inclusive = HlslPerfScanCombine(previous, inclusive);
        }
    }
    prefix = lane == 0 ? HlslPerfScanIdentity() : WaveReadLaneAt(inclusive, lane - 1);
    total = WaveReadLaneAt(inclusive, waveSize - 1);
#endif
}

groupshared uint ThreadTotals[HLSLPERF_GROUP_SIZE];
groupshared uint SinglePassEpoch;
groupshared uint SinglePassBlockIndex;
groupshared uint SinglePassBlockTotal;
groupshared uint SinglePassBlockPrefix;
#if HLSLPERF_SCAN_DIAGNOSTIC_COUNTERS
RWByteAddressBuffer ScanDiagnosticCounters : register(u2);
#endif

// Single-pass state uses an 8-byte header followed by 12 bytes per logical block:
// [epoch, nextBlock] [aggregate, inclusivePrefix, epoch|status]...
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
HLSLPERF_WAVE_ATTRIBUTE
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
        if (SinglePassBlockIndex >= HLSLPERF_SCAN_LOGICAL_BLOCK_COUNT)
            break;

        const uint blockStart = SinglePassBlockIndex * ElementsPerBlock;
        const uint threadStart = blockStart + groupIndex * HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD;
        uint localPrefix[HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD];
        uint threadTotal = HlslPerfScanIdentity();
#if HLSLPERF_VECTOR_WIDTH == 4
        [unroll]
        for (uint chunk = 0; chunk < HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD; chunk += 4)
        {
            uint4 values = HlslPerfScanIdentity();
            if (chunk + 3 < HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD && threadStart + chunk + 3 < ElementCount)
                values = Input0.Load4((threadStart + chunk) * 4);
            else
            {
                [unroll]
                for (uint component = 0; component < 4; ++component)
                {
                    const uint item = chunk + component;
                    const uint index = threadStart + item;
                    if (item < HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD && index < ElementCount)
                        values[component] = Input0.Load(index * 4);
                }
            }
            [unroll]
            for (uint component = 0; component < 4; ++component)
            {
                const uint item = chunk + component;
                if (item < HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD)
                {
                    localPrefix[item] = threadTotal;
                    threadTotal = HlslPerfScanCombine(threadTotal, values[component]);
                }
            }
        }
#elif HLSLPERF_VECTOR_WIDTH == 1
        [unroll]
        for (uint item = 0; item < HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD; ++item)
        {
            localPrefix[item] = threadTotal;
            const uint index = threadStart + item;
            if (index < ElementCount)
                threadTotal = HlslPerfScanCombine(threadTotal, Input0.Load(index * 4));
        }
#else
#error HLSLPERF_VECTOR_WIDTH must be 1 or 4.
#endif

        const uint waveSize = WaveGetLaneCount();
        const uint waveIndex = groupIndex / waveSize;
        uint wavePrefix;
        uint waveTotal;
        HlslPerfWaveExclusiveScan(threadTotal, wavePrefix, waveTotal);
        if (WaveIsFirstLane())
            ThreadTotals[waveIndex] = waveTotal;
        GroupMemoryBarrierWithGroupSync();

        const uint waveCount = (HLSLPERF_GROUP_SIZE + waveSize - 1) / waveSize;
        if (groupIndex == 0)
        {
            uint runningWaveTotal = HlslPerfScanIdentity();
            for (uint wave = 0; wave < waveCount; ++wave)
            {
                const uint currentWaveTotal = ThreadTotals[wave];
                ThreadTotals[wave] = runningWaveTotal;
                runningWaveTotal = HlslPerfScanCombine(runningWaveTotal, currentWaveTotal);
            }
            SinglePassBlockTotal = runningWaveTotal;
        }
        GroupMemoryBarrierWithGroupSync();
        const uint localBlockPrefix = HlslPerfScanCombine(ThreadTotals[waveIndex], wavePrefix);

        if (groupIndex == 0)
        {
            const uint aggregateToken = SinglePassEpoch | 1;
            const uint prefixToken = SinglePassEpoch | 2;
            const uint currentState = 8 + SinglePassBlockIndex * 12;
            Output1.Store(currentState, SinglePassBlockTotal);
            DeviceMemoryBarrier();
            uint ignoredStatus;
            Output1.InterlockedExchange(currentState + 8, aggregateToken, ignoredStatus);

            uint blockPrefix = HlslPerfScanIdentity();
            uint lookbackSuffix = HlslPerfScanIdentity();
#if HLSLPERF_SCAN_DIAGNOSTIC_COUNTERS
            uint diagnosticPolls = 0, diagnosticAggregates = 0, diagnosticPrefixes = 0, diagnosticNotReady = 0;
#endif
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
#if HLSLPERF_SCAN_DIAGNOSTIC_COUNTERS
                    diagnosticPolls++;
#endif
                    if (observedStatus == prefixToken)
                    {
#if HLSLPERF_SCAN_DIAGNOSTIC_COUNTERS
                        diagnosticPrefixes++;
#endif
                        DeviceMemoryBarrier();
                        blockPrefix = HlslPerfScanCombine(
                            Output1.Load(previousState + 4),
                            lookbackSuffix);
                        break;
                    }
                    if (observedStatus == aggregateToken)
                    {
#if HLSLPERF_SCAN_DIAGNOSTIC_COUNTERS
                        diagnosticAggregates++;
#endif
                        DeviceMemoryBarrier();
                        lookbackSuffix = HlslPerfScanCombine(
                            Output1.Load(previousState),
                            lookbackSuffix);
                        if (predecessor == 0)
                        {
                            blockPrefix = lookbackSuffix;
                            break;
                        }
                        predecessor--;
                    }
#if HLSLPERF_SCAN_DIAGNOSTIC_COUNTERS
                    else { diagnosticNotReady++; }
#endif
                }
            }
#if HLSLPERF_SCAN_DIAGNOSTIC_COUNTERS
            ScanDiagnosticCounters.Store4(SinglePassBlockIndex * 16,
                uint4(diagnosticPolls, diagnosticAggregates, diagnosticPrefixes, diagnosticNotReady));
#endif
            SinglePassBlockPrefix = blockPrefix;

            Output1.Store(
                currentState + 4,
                HlslPerfScanCombine(blockPrefix, SinglePassBlockTotal));
            DeviceMemoryBarrier();
            Output1.InterlockedExchange(currentState + 8, prefixToken, ignoredStatus);
        }
        GroupMemoryBarrierWithGroupSync();

#if HLSLPERF_VECTOR_WIDTH == 4
        [unroll]
        for (uint storeChunk = 0; storeChunk < HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD; storeChunk += 4)
        {
            uint4 outputValues;
            [unroll]
            for (uint component = 0; component < 4; ++component)
            {
                const uint item = storeChunk + component;
                outputValues[component] = item < HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD
                    ? HlslPerfScanCombine(
                        SinglePassBlockPrefix,
                        HlslPerfScanCombine(localBlockPrefix, localPrefix[item]))
                    : HlslPerfScanIdentity();
            }
            if (storeChunk + 3 < HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD &&
                threadStart + storeChunk + 3 < ElementCount)
            {
                Output0.Store4((threadStart + storeChunk) * 4, outputValues);
            }
            else
            {
                [unroll]
                for (uint component = 0; component < 4; ++component)
                {
                    const uint item = storeChunk + component;
                    const uint index = threadStart + item;
                    if (item < HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD && index < ElementCount)
                        Output0.Store(index * 4, outputValues[component]);
                }
            }
        }
#else
        [unroll]
        for (uint storeItem = 0; storeItem < HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD; ++storeItem)
        {
            const uint index = threadStart + storeItem;
            if (index < ElementCount)
                Output0.Store(
                    index * 4,
                    HlslPerfScanCombine(
                        SinglePassBlockPrefix,
                        HlslPerfScanCombine(localBlockPrefix, localPrefix[storeItem])));
        }
#endif
        GroupMemoryBarrierWithGroupSync();
    }
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
HLSLPERF_WAVE_ATTRIBUTE
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void BlockScanPass(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    const uint linearGroup = groupId.y * HLSLPERF_SCAN_DISPATCH_GROUPS_X + groupId.x;
    if (linearGroup >= HLSLPERF_SCAN_DISPATCH_GROUP_COUNT)
        return;
    const uint blockStart = linearGroup * HLSLPERF_GROUP_SIZE * HLSLPERF_ELEMENTS_PER_THREAD;
    const uint threadStart = blockStart + groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;
    uint localPrefix[HLSLPERF_ELEMENTS_PER_THREAD];
    uint threadTotal = HlslPerfScanIdentity();

#if HLSLPERF_VECTOR_WIDTH == 4
    [unroll]
    for (uint chunk = 0; chunk < HLSLPERF_ELEMENTS_PER_THREAD; chunk += 4)
    {
        uint4 values = HlslPerfScanIdentity();
        if (chunk + 3 < HLSLPERF_ELEMENTS_PER_THREAD && threadStart + chunk + 3 < ElementCount)
            values = Input0.Load4((threadStart + chunk) * 4);
        else
        {
            [unroll]
            for (uint component = 0; component < 4; ++component)
            {
                const uint item = chunk + component;
                const uint index = threadStart + item;
                if (item < HLSLPERF_ELEMENTS_PER_THREAD && index < ElementCount)
                    values[component] = Input0.Load(index * 4);
            }
        }
        [unroll]
        for (uint component = 0; component < 4; ++component)
        {
            const uint item = chunk + component;
            if (item < HLSLPERF_ELEMENTS_PER_THREAD)
            {
                localPrefix[item] = threadTotal;
                threadTotal = HlslPerfScanCombine(threadTotal, values[component]);
            }
        }
    }
#else
    [unroll]
    for (uint loadItem = 0; loadItem < HLSLPERF_ELEMENTS_PER_THREAD; ++loadItem)
    {
        localPrefix[loadItem] = threadTotal;
        const uint index = threadStart + loadItem;
        if (index < ElementCount)
            threadTotal = HlslPerfScanCombine(threadTotal, Input0.Load(index * 4));
    }
#endif

#if HLSLPERF_SCAN_BACKEND == 2
    const uint waveSize = WaveGetLaneCount();
    const uint waveIndex = groupIndex / waveSize;
    uint wavePrefix;
    uint waveTotal;
    HlslPerfWaveExclusiveScan(threadTotal, wavePrefix, waveTotal);
    if (WaveIsFirstLane())
        ThreadTotals[waveIndex] = waveTotal;
    GroupMemoryBarrierWithGroupSync();

    const uint waveCount = (HLSLPERF_GROUP_SIZE + waveSize - 1) / waveSize;
    if (groupIndex == 0)
    {
        uint runningWaveTotal = HlslPerfScanIdentity();
        for (uint wave = 0; wave < waveCount; ++wave)
        {
            const uint currentWaveTotal = ThreadTotals[wave];
            ThreadTotals[wave] = runningWaveTotal;
            runningWaveTotal = HlslPerfScanCombine(runningWaveTotal, currentWaveTotal);
        }
        Output1.Store(linearGroup * 4, runningWaveTotal);
    }
    GroupMemoryBarrierWithGroupSync();
    const uint blockPrefix = HlslPerfScanCombine(ThreadTotals[waveIndex], wavePrefix);
#else
    ThreadTotals[groupIndex] = threadTotal;
    GroupMemoryBarrierWithGroupSync();

    [unroll]
    for (uint upsweepOffset = 1; upsweepOffset < HLSLPERF_GROUP_SIZE; upsweepOffset <<= 1)
    {
        const uint node = (groupIndex + 1) * upsweepOffset * 2 - 1;
        if (node < HLSLPERF_GROUP_SIZE)
            ThreadTotals[node] = HlslPerfScanCombine(
                ThreadTotals[node - upsweepOffset],
                ThreadTotals[node]);
        GroupMemoryBarrierWithGroupSync();
    }

    if (groupIndex == 0)
    {
        Output1.Store(linearGroup * 4, ThreadTotals[HLSLPERF_GROUP_SIZE - 1]);
        ThreadTotals[HLSLPERF_GROUP_SIZE - 1] = HlslPerfScanIdentity();
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
            ThreadTotals[node] = HlslPerfScanCombine(ThreadTotals[node], left);
        }
        GroupMemoryBarrierWithGroupSync();
    }

    const uint blockPrefix = ThreadTotals[groupIndex];
#endif
#if HLSLPERF_VECTOR_WIDTH == 4
    [unroll]
    for (uint storeChunk = 0; storeChunk < HLSLPERF_ELEMENTS_PER_THREAD; storeChunk += 4)
    {
        uint4 outputValues;
        [unroll]
        for (uint component = 0; component < 4; ++component)
        {
            const uint item = storeChunk + component;
            outputValues[component] = item < HLSLPERF_ELEMENTS_PER_THREAD
                ? HlslPerfScanCombine(blockPrefix, localPrefix[item])
                : HlslPerfScanIdentity();
        }
        if (storeChunk + 3 < HLSLPERF_ELEMENTS_PER_THREAD && threadStart + storeChunk + 3 < ElementCount)
            Output0.Store4((threadStart + storeChunk) * 4, outputValues);
        else
        {
            [unroll]
            for (uint component = 0; component < 4; ++component)
            {
                const uint item = storeChunk + component;
                const uint index = threadStart + item;
                if (item < HLSLPERF_ELEMENTS_PER_THREAD && index < ElementCount)
                    Output0.Store(index * 4, outputValues[component]);
            }
        }
    }
#else
    [unroll]
    for (uint storeItem = 0; storeItem < HLSLPERF_ELEMENTS_PER_THREAD; ++storeItem)
    {
        const uint index = threadStart + storeItem;
        if (index < ElementCount)
            Output0.Store(index * 4, HlslPerfScanCombine(blockPrefix, localPrefix[storeItem]));
    }
#endif
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void AddScanOffsets(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    const uint linearGroup = groupId.y * HLSLPERF_SCAN_DISPATCH_GROUPS_X + groupId.x;
    if (linearGroup >= HLSLPERF_SCAN_DISPATCH_GROUP_COUNT)
        return;
    const uint blockPrefix = Input0.Load(linearGroup * 4);
    const uint threadStart = linearGroup * ElementsPerBlock + groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;
    [unroll]
    for (uint item = 0; item < HLSLPERF_ELEMENTS_PER_THREAD; ++item)
    {
        const uint index = threadStart + item;
        if (index < ElementCount)
            Output0.Store(
                index * 4,
                HlslPerfScanCombine(blockPrefix, Output0.Load(index * 4)));
    }
}

#endif
