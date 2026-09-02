#define HLSLPERF_ROOT_SIGNATURE "SRV(t0), SRV(t1), UAV(u0), UAV(u1), RootConstants(num32BitConstants=8, b0)"

ByteAddressBuffer Input0 : register(t0);
ByteAddressBuffer Input1 : register(t1);
RWByteAddressBuffer Output0 : register(u0);
globallycoherent RWByteAddressBuffer Output1 : register(u1);

cbuffer DispatchParameters : register(b0)
{
    uint ElementCount;
    uint ElementsPerBlock;
    uint Width;
    uint Height;
    uint FrameCount;
    uint Seed;
    uint DispatchGroupsX;
    uint DispatchGroupCount;
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
        if (SinglePassBlockIndex >= Width)
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
    const uint linearGroup = groupId.y * DispatchGroupsX + groupId.x;
    if (linearGroup >= DispatchGroupCount)
        return;
    const uint blockStart = linearGroup * HLSLPERF_GROUP_SIZE * HLSLPERF_ELEMENTS_PER_THREAD;
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
        Output1.Store(linearGroup * 4, runningWaveTotal);
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
        Output1.Store(linearGroup * 4, ThreadTotals[HLSLPERF_GROUP_SIZE - 1]);
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
    const uint linearGroup = groupId.y * DispatchGroupsX + groupId.x;
    if (linearGroup >= DispatchGroupCount)
        return;
    const uint blockPrefix = Input0.Load(linearGroup * 4);
    const uint threadStart = linearGroup * ElementsPerBlock + groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;
    [unroll]
    for (uint item = 0; item < HLSLPERF_ELEMENTS_PER_THREAD; ++item)
    {
        const uint index = threadStart + item;
        if (index < ElementCount)
            Output0.Store(index * 4, Output0.Load(index * 4) + blockPrefix);
    }
}

uint Hash32(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352du;
    value ^= value >> 15;
    value *= 0x846ca68bu;
    value ^= value >> 16;
    return value;
}

uint3 AddSaturated(uint3 left, uint3 right)
{
    return min(left + right, uint3(255, 255, 255));
}

#define HLSLPERF_VISUAL_TILE_WIDTH 16
#define HLSLPERF_VISUAL_TILE_HEIGHT 16
#define HLSLPERF_VISUAL_TRAIL_COUNT 6

groupshared uint VisualTrailSamples[HLSLPERF_VISUAL_TILE_WIDTH * HLSLPERF_VISUAL_TRAIL_COUNT];
groupshared uint VisualTrailCenters[HLSLPERF_VISUAL_TILE_WIDTH * HLSLPERF_VISUAL_TRAIL_COUNT];
groupshared uint VisualTrailFlags[HLSLPERF_VISUAL_TILE_WIDTH * HLSLPERF_VISUAL_TRAIL_COUNT];
groupshared uint VisualTrailRadii[HLSLPERF_VISUAL_TILE_WIDTH * HLSLPERF_VISUAL_TRAIL_COUNT];

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_VISUAL_TILE_WIDTH, HLSLPERF_VISUAL_TILE_HEIGHT, 1)]
void VisualizeScan(
    uint3 groupId : SV_GroupID,
    uint3 groupThreadId : SV_GroupThreadID,
    uint3 dispatchThreadId : SV_DispatchThreadID)
{
    const uint frame = groupId.z;
    const uint x = dispatchThreadId.x;
    const uint y = dispatchThreadId.y;
    const uint stride = max(1, ElementCount / Width);
    const uint scale = max(1, ElementCount / max(1, Height * 2));
    const uint segment = max(1, ElementCount / HLSLPERF_VISUAL_TRAIL_COUNT);

    // Every trail descriptor is constant across all Y lanes for one X column.
    // One row loads it once and the 16x16 tile reuses it from groupshared memory.
    if (groupThreadId.y == 0)
    {
        [unroll]
        for (uint trail = 0; trail < HLSLPERF_VISUAL_TRAIL_COUNT; ++trail)
        {
            const uint descriptor = groupThreadId.x * HLSLPERF_VISUAL_TRAIL_COUNT + trail;
            if (x < Width && frame < FrameCount)
            {
                const uint sample =
                    (x * stride + trail * segment + frame * (stride * 3 + 17)) % ElementCount;
                const uint prefix = Input0.Load(sample * 4);
                VisualTrailSamples[descriptor] = sample;
                VisualTrailCenters[descriptor] =
                    (prefix / scale + trail * max(1, Height / HLSLPERF_VISUAL_TRAIL_COUNT) +
                        frame * (trail + 2) * 3) % Height;
                VisualTrailFlags[descriptor] = Input1.Load(sample * 4);
                VisualTrailRadii[descriptor] =
                    2 + (Hash32(sample ^ Seed ^ (trail * 0x9e3779b9u)) & 1);
            }
            else
            {
                VisualTrailSamples[descriptor] = 0;
                VisualTrailCenters[descriptor] = 0;
                VisualTrailFlags[descriptor] = 0;
                VisualTrailRadii[descriptor] = 0;
            }
        }
    }
    GroupMemoryBarrierWithGroupSync();

    if (x >= Width || y >= Height || frame >= FrameCount)
        return;

    const uint pixelCount = Width * Height;
    const uint pixel = y * Width + x;
    uint3 color = uint3(3, 7, 18);

    const uint star = Hash32(pixel + Seed);
    if ((star & 2047) < 5)
    {
        const uint pulse = 18 + ((star >> 8) + frame * 7) % 36;
        color = AddSaturated(color, uint3(pulse / 2, pulse, pulse));
    }

    [unroll]
    for (uint trail = 0; trail < HLSLPERF_VISUAL_TRAIL_COUNT; ++trail)
    {
        const uint descriptor = groupThreadId.x * HLSLPERF_VISUAL_TRAIL_COUNT + trail;
        const uint sample = VisualTrailSamples[descriptor];
        const uint center = VisualTrailCenters[descriptor];
        const uint flag = VisualTrailFlags[descriptor];
        const uint directDistance = y > center ? y - center : center - y;
        const uint distance = min(directDistance, Height - directDistance);
        const uint radius = VisualTrailRadii[descriptor];

        if (distance <= radius)
        {
            const uint power = (radius + 1 - distance) * 54;
            if (trail % 3 == 0)
                color = AddSaturated(color, uint3(power / 5, power, power));
            else if (trail % 3 == 1)
                color = AddSaturated(color, uint3(power, power / 4, power));
            else
                color = AddSaturated(color, uint3(power, power / 2, power / 8));
        }

        if (flag != 0 && distance < 10 && (Hash32(sample + frame * 131 + Seed) & 63) < 2)
            color = AddSaturated(color, uint3(42, 52, 68));
    }

    const uint packed = color.x | (color.y << 8) | (color.z << 16) | 0xff000000;
    Output0.Store((frame * pixelCount + pixel) * 4, packed);
}
