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

#ifndef HLSLPERF_ITEMS_SCALE
#define HLSLPERF_ITEMS_SCALE 1
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

#define HLSLPERF_ITEMS_PER_THREAD (HLSLPERF_ELEMENTS_PER_THREAD * HLSLPERF_ITEMS_SCALE)

// A descriptor represents a sequence as (tail sum after its final head,
// contains-head). Applying it to an incoming prefix is an associative monoid,
// which lets the same operation work at lane, wave, block, and look-back scope.
uint2 SegmentCombine(uint2 left, uint2 right)
{
    return uint2(right.y != 0 ? right.x : left.x + right.x, left.y | right.y);
}

uint2 SegmentIdentity()
{
    return uint2(0, 0);
}

void WaveExclusiveSegmentScan(uint2 value, out uint2 prefix, out uint2 total)
{
    const uint lane = WaveGetLaneIndex();
    const uint waveSize = WaveGetLaneCount();
    uint2 inclusive = value;
    [unroll(6)]
    for (uint offset = 1; offset < 64; offset <<= 1)
    {
        if (offset < waveSize)
        {
            const uint sourceLane = lane >= offset ? lane - offset : lane;
            uint2 previous = uint2(
                WaveReadLaneAt(inclusive.x, sourceLane),
                WaveReadLaneAt(inclusive.y, sourceLane));
            if (lane >= offset)
                inclusive = SegmentCombine(previous, inclusive);
        }
    }

    prefix = lane == 0
        ? SegmentIdentity()
        : uint2(
            WaveReadLaneAt(inclusive.x, lane - 1),
            WaveReadLaneAt(inclusive.y, lane - 1));
    total = uint2(
        WaveReadLaneAt(inclusive.x, waveSize - 1),
        WaveReadLaneAt(inclusive.y, waveSize - 1));
}

groupshared uint2 ThreadDescriptors[HLSLPERF_GROUP_SIZE];
groupshared uint SegmentEpoch;
groupshared uint SegmentBlockIndex;
groupshared uint2 SegmentBlockDescriptor;
groupshared uint2 SegmentBlockPrefix;

// State: 8-byte [epoch,nextBlock] header, then 20 bytes per logical block:
// aggregate pair, inclusive-prefix pair, epoch|status.
[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(1, 1, 1)]
void ResetSegmentedScanState(uint groupIndex : SV_GroupIndex)
{
    if (groupIndex == 0)
    {
        uint previousEpoch;
        Output1.InterlockedAdd(0, 1, previousEpoch);
        if ((previousEpoch & 0x3fffffffu) == 0x3fffffffu)
            Output1.Store(0, 1);
        Output1.Store(4, 0);
    }
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
HLSLPERF_WAVE_ATTRIBUTE
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void SegmentedScanSinglePass(uint groupIndex : SV_GroupIndex)
{
    if (groupIndex == 0)
        SegmentEpoch = (Output1.Load(0) & 0x3fffffffu) << 2;
    GroupMemoryBarrierWithGroupSync();

    [loop]
    while (true)
    {
        if (groupIndex == 0)
            Output1.InterlockedAdd(4, 1, SegmentBlockIndex);
        GroupMemoryBarrierWithGroupSync();
        if (SegmentBlockIndex >= LogicalBlockCount)
            break;

        const uint blockStart = SegmentBlockIndex * ElementsPerBlock;
        const uint threadStart = blockStart + groupIndex * HLSLPERF_ITEMS_PER_THREAD;
        uint values[HLSLPERF_ITEMS_PER_THREAD];
        uint heads[HLSLPERF_ITEMS_PER_THREAD];

#if HLSLPERF_VECTOR_WIDTH == 4
        [unroll]
        for (uint chunk = 0; chunk < HLSLPERF_ITEMS_PER_THREAD; chunk += 4)
        {
            uint4 loadedValues = 0;
            uint4 loadedHeads = 0;
            if (chunk + 3 < HLSLPERF_ITEMS_PER_THREAD && threadStart + chunk + 3 < ElementCount)
            {
                loadedValues = Input0.Load4((threadStart + chunk) * 4);
                loadedHeads = Input1.Load4((threadStart + chunk) * 4);
            }
            else
            {
                [unroll]
                for (uint component = 0; component < 4; ++component)
                {
                    const uint item = chunk + component;
                    const uint index = threadStart + item;
                    if (item < HLSLPERF_ITEMS_PER_THREAD && index < ElementCount)
                    {
                        loadedValues[component] = Input0.Load(index * 4);
                        loadedHeads[component] = Input1.Load(index * 4);
                    }
                }
            }
            [unroll]
            for (uint component = 0; component < 4; ++component)
            {
                const uint item = chunk + component;
                if (item < HLSLPERF_ITEMS_PER_THREAD)
                {
                    values[item] = loadedValues[component];
                    heads[item] = loadedHeads[component];
                }
            }
        }
#elif HLSLPERF_VECTOR_WIDTH == 1
        [unroll]
        for (uint loadItem = 0; loadItem < HLSLPERF_ITEMS_PER_THREAD; ++loadItem)
        {
            const uint index = threadStart + loadItem;
            values[loadItem] = index < ElementCount ? Input0.Load(index * 4) : 0;
            heads[loadItem] = index < ElementCount ? Input1.Load(index * 4) : 0;
        }
#else
#error HLSLPERF_VECTOR_WIDTH must be 1 or 4.
#endif

        uint2 threadDescriptor = SegmentIdentity();
        [unroll]
        for (uint descriptorItem = 0; descriptorItem < HLSLPERF_ITEMS_PER_THREAD; ++descriptorItem)
            threadDescriptor = SegmentCombine(
                threadDescriptor,
                uint2(values[descriptorItem], heads[descriptorItem]));

        const uint waveSize = WaveGetLaneCount();
        const uint waveIndex = groupIndex / waveSize;
        uint2 wavePrefix;
        uint2 waveTotal;
        WaveExclusiveSegmentScan(threadDescriptor, wavePrefix, waveTotal);
        if (WaveIsFirstLane())
            ThreadDescriptors[waveIndex] = waveTotal;
        GroupMemoryBarrierWithGroupSync();

        const uint waveCount = (HLSLPERF_GROUP_SIZE + waveSize - 1) / waveSize;
        if (groupIndex == 0)
        {
            uint2 running = SegmentIdentity();
            for (uint wave = 0; wave < waveCount; ++wave)
            {
                uint2 current = ThreadDescriptors[wave];
                ThreadDescriptors[wave] = running;
                running = SegmentCombine(running, current);
            }
            SegmentBlockDescriptor = running;
        }
        GroupMemoryBarrierWithGroupSync();
        uint2 localBlockPrefix = SegmentCombine(ThreadDescriptors[waveIndex], wavePrefix);

        if (groupIndex == 0)
        {
            const uint aggregateToken = SegmentEpoch | 1;
            const uint prefixToken = SegmentEpoch | 2;
            const uint currentState = 8 + SegmentBlockIndex * 20;
            Output1.Store2(currentState, SegmentBlockDescriptor);
            DeviceMemoryBarrier();
            uint ignoredStatus;
            Output1.InterlockedExchange(currentState + 16, aggregateToken, ignoredStatus);

            uint2 blockPrefix = SegmentIdentity();
            uint2 lookbackSuffix = SegmentIdentity();
            if (SegmentBlockIndex > 0)
            {
                uint predecessor = SegmentBlockIndex - 1;
                [allow_uav_condition]
                while (true)
                {
                    const uint previousState = 8 + predecessor * 20;
                    uint observedStatus;
                    Output1.InterlockedCompareExchange(
                        previousState + 16,
                        0xffffffffu,
                        0xffffffffu,
                        observedStatus);
                    if (observedStatus == prefixToken)
                    {
                        DeviceMemoryBarrier();
                        blockPrefix = SegmentCombine(Output1.Load2(previousState + 8), lookbackSuffix);
                        break;
                    }
                    if (observedStatus == aggregateToken)
                    {
                        DeviceMemoryBarrier();
                        lookbackSuffix = SegmentCombine(Output1.Load2(previousState), lookbackSuffix);
                        if (predecessor == 0)
                        {
                            blockPrefix = lookbackSuffix;
                            break;
                        }
                        predecessor--;
                    }
                }
            }
            SegmentBlockPrefix = blockPrefix;

            Output1.Store2(
                currentState + 8,
                SegmentCombine(blockPrefix, SegmentBlockDescriptor));
            DeviceMemoryBarrier();
            Output1.InterlockedExchange(currentState + 16, prefixToken, ignoredStatus);
        }
        GroupMemoryBarrierWithGroupSync();

        uint2 threadContext = SegmentCombine(SegmentBlockPrefix, localBlockPrefix);
        uint runningValue = threadContext.x;
        [unroll]
        for (uint outputItem = 0; outputItem < HLSLPERF_ITEMS_PER_THREAD; ++outputItem)
        {
            const uint index = threadStart + outputItem;
            if (heads[outputItem] != 0)
                runningValue = 0;
            if (index < ElementCount)
            {
                Output0.Store(index * 4, runningValue);
                runningValue += values[outputItem];
            }
        }
        GroupMemoryBarrierWithGroupSync();
    }
}
