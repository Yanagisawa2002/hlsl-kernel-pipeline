#define HLSLPERF_ROOT_SIGNATURE "SRV(t0), SRV(t1), UAV(u0), UAV(u1), RootConstants(num32BitConstants=8, b0)"

ByteAddressBuffer Input0 : register(t0);
RWByteAddressBuffer Output0 : register(u0);

cbuffer DispatchParameters : register(b0)
{
    uint ElementCount;
    uint BinCount;
    uint ElementsPerBlock;
    uint LogicalGroupCount;
    uint DispatchGroupsX;
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

#ifndef HLSLPERF_VECTOR_WIDTH
#define HLSLPERF_VECTOR_WIDTH 1
#endif

#ifndef HLSLPERF_HISTOGRAM_BACKEND
#define HLSLPERF_HISTOGRAM_BACKEND 1
#endif

#ifndef HLSLPERF_HISTOGRAM_BINS
#define HLSLPERF_HISTOGRAM_BINS 256
#endif

#ifndef HLSLPERF_HISTOGRAM_REPLICAS
#define HLSLPERF_HISTOGRAM_REPLICAS 1
#endif

groupshared uint LocalHistogram[HLSLPERF_HISTOGRAM_BINS * HLSLPERF_HISTOGRAM_REPLICAS];
groupshared uint HistogramScan[HLSLPERF_HISTOGRAM_BINS];
groupshared uint HistogramTotal;

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void ResetHistogram(uint groupIndex : SV_GroupIndex)
{
    for (uint resetBin = groupIndex; resetBin < BinCount; resetBin += HLSLPERF_GROUP_SIZE)
        Output0.Store(resetBin * 4, 0);
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void BuildHistogram(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    const uint linearGroup = groupId.y * DispatchGroupsX + groupId.x;
    if (linearGroup >= LogicalGroupCount)
        return;

#if HLSLPERF_HISTOGRAM_BACKEND == 2
    const uint sharedEntryCount = HLSLPERF_HISTOGRAM_BINS * HLSLPERF_HISTOGRAM_REPLICAS;
    for (uint clearEntry = groupIndex; clearEntry < sharedEntryCount; clearEntry += HLSLPERF_GROUP_SIZE)
        LocalHistogram[clearEntry] = 0;
    GroupMemoryBarrierWithGroupSync();
#endif

    const uint threadStart =
        linearGroup * ElementsPerBlock + groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;
#if HLSLPERF_VECTOR_WIDTH == 4
    uint4 values = 0;
    if (threadStart + 3 < ElementCount)
        values = Input0.Load4(threadStart * 4);
    else
    {
        [unroll]
        for (uint histogramTail = 0; histogramTail < 4; ++histogramTail)
        {
            const uint index = threadStart + histogramTail;
            if (index < ElementCount)
                values[histogramTail] = Input0.Load(index * 4);
        }
    }
    [unroll]
    for (uint histogramComponent = 0; histogramComponent < 4; ++histogramComponent)
    {
        const uint index = threadStart + histogramComponent;
        if (index < ElementCount)
        {
            const uint bin = values[histogramComponent] & (HLSLPERF_HISTOGRAM_BINS - 1);
            uint ignored;
#if HLSLPERF_HISTOGRAM_BACKEND == 1
            Output0.InterlockedAdd(bin * 4, 1, ignored);
#else
            const uint replica = groupIndex & (HLSLPERF_HISTOGRAM_REPLICAS - 1);
            InterlockedAdd(LocalHistogram[replica * HLSLPERF_HISTOGRAM_BINS + bin], 1, ignored);
#endif
        }
    }
#elif HLSLPERF_VECTOR_WIDTH == 1
    [unroll]
    for (uint histogramItem = 0; histogramItem < HLSLPERF_ELEMENTS_PER_THREAD; ++histogramItem)
    {
        const uint index = threadStart + histogramItem;
        if (index < ElementCount)
        {
            const uint value = Input0.Load(index * 4);
            const uint bin = value & (HLSLPERF_HISTOGRAM_BINS - 1);
            uint ignored;
#if HLSLPERF_HISTOGRAM_BACKEND == 1
            Output0.InterlockedAdd(bin * 4, 1, ignored);
#else
            const uint replica = groupIndex & (HLSLPERF_HISTOGRAM_REPLICAS - 1);
            InterlockedAdd(LocalHistogram[replica * HLSLPERF_HISTOGRAM_BINS + bin], 1, ignored);
#endif
        }
    }
#else
#error HLSLPERF_VECTOR_WIDTH must be 1 or 4.
#endif

#if HLSLPERF_HISTOGRAM_BACKEND == 2
    GroupMemoryBarrierWithGroupSync();
    for (uint mergeBin = groupIndex; mergeBin < BinCount; mergeBin += HLSLPERF_GROUP_SIZE)
    {
        uint binTotal = 0;
        [unroll]
        for (uint mergeReplica = 0; mergeReplica < HLSLPERF_HISTOGRAM_REPLICAS; ++mergeReplica)
            binTotal += LocalHistogram[mergeReplica * HLSLPERF_HISTOGRAM_BINS + mergeBin];
        if (binTotal != 0)
        {
            uint ignored;
            Output0.InterlockedAdd(mergeBin * 4, binTotal, ignored);
        }
    }
#elif HLSLPERF_HISTOGRAM_BACKEND != 1
#error HLSLPERF_HISTOGRAM_BACKEND must be 1 (global atomics) or 2 (replicated LDS).
#endif
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_HISTOGRAM_BINS, 1, 1)]
void PrefixHistogram(uint groupIndex : SV_GroupIndex)
{
    const uint count = Input0.Load(groupIndex * 4);
    HistogramScan[groupIndex] = count;
    GroupMemoryBarrierWithGroupSync();

    [unroll]
    for (uint upsweep = 1; upsweep < HLSLPERF_HISTOGRAM_BINS; upsweep <<= 1)
    {
        const uint node = (groupIndex + 1) * upsweep * 2 - 1;
        if (node < HLSLPERF_HISTOGRAM_BINS)
            HistogramScan[node] += HistogramScan[node - upsweep];
        GroupMemoryBarrierWithGroupSync();
    }

    if (groupIndex == 0)
    {
        HistogramTotal = HistogramScan[HLSLPERF_HISTOGRAM_BINS - 1];
        HistogramScan[HLSLPERF_HISTOGRAM_BINS - 1] = 0;
    }
    GroupMemoryBarrierWithGroupSync();

    [unroll]
    for (uint downsweep = HLSLPERF_HISTOGRAM_BINS / 2; downsweep > 0; downsweep >>= 1)
    {
        const uint node = (groupIndex + 1) * downsweep * 2 - 1;
        if (node < HLSLPERF_HISTOGRAM_BINS)
        {
            const uint left = HistogramScan[node - downsweep];
            const uint prefix = HistogramScan[node];
            HistogramScan[node - downsweep] = prefix;
            HistogramScan[node] = prefix + left;
        }
        GroupMemoryBarrierWithGroupSync();
    }

    Output0.Store(groupIndex * 4, HistogramScan[groupIndex]);
    if (groupIndex == HLSLPERF_HISTOGRAM_BINS - 1)
        Output0.Store(HLSLPERF_HISTOGRAM_BINS * 4, HistogramTotal);
}
