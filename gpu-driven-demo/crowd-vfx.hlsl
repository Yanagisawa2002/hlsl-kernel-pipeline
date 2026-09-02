#define HLSLPERF_ROOT_SIGNATURE "SRV(t0), SRV(t1), UAV(u0), UAV(u1), RootConstants(num32BitConstants=8, b0)"

ByteAddressBuffer Input0 : register(t0);
ByteAddressBuffer Input1 : register(t1);
RWByteAddressBuffer Output0 : register(u0);
globallycoherent RWByteAddressBuffer Output1 : register(u1);

cbuffer DispatchParameters : register(b0)
{
    uint Parameter0;
    uint Parameter1;
    uint Parameter2;
    uint Parameter3;
    uint Parameter4;
    uint Parameter5;
    uint Parameter6;
    uint Parameter7;
};

#ifndef HLSLPERF_CROWD_BACKEND
#define HLSLPERF_CROWD_BACKEND 1
#endif

#ifndef HLSLPERF_CROWD_TILE_SIZE
#define HLSLPERF_CROWD_TILE_SIZE 16
#endif

#ifndef HLSLPERF_CROWD_TILE_BINS
#define HLSLPERF_CROWD_TILE_BINS 512
#endif

#ifndef HLSLPERF_CROWD_TILE_REPLICAS
#define HLSLPERF_CROWD_TILE_REPLICAS 1
#endif

#ifndef HLSLPERF_SCAN_OPERATOR
#define HLSLPERF_SCAN_OPERATOR 1
#endif

#define ElementCount Parameter0
#define ElementsPerBlock Parameter1
#define LogicalBlockCount Parameter2
#define DispatchGroupsX Parameter6
#define DispatchGroupCount Parameter7
#define HLSLPERF_SCAN_LOGICAL_BLOCK_COUNT LogicalBlockCount
#define HLSLPERF_SCAN_DISPATCH_GROUPS_X DispatchGroupsX
#define HLSLPERF_SCAN_DISPATCH_GROUP_COUNT DispatchGroupCount
#include "../kernels/include/hlslperf/scan_u32.hlsli"

#if HLSLPERF_CROWD_BACKEND < 1 || HLSLPERF_CROWD_BACKEND > 2
#error HLSLPERF_CROWD_BACKEND must be 1 (materialized) or 2 (fused).
#endif

#if HLSLPERF_CROWD_TILE_REPLICAS < 1 || HLSLPERF_CROWD_TILE_REPLICAS > 8
#error HLSLPERF_CROWD_TILE_REPLICAS must be in 1..8.
#endif

uint CrowdHash32(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352du;
    value ^= value >> 15;
    value *= 0x846ca68bu;
    value ^= value >> 16;
    return value;
}

uint CrowdLocalX(uint seed, uint frame, uint width)
{
    const uint worldWidth = width * 4;
    const uint origin = CrowdHash32(seed ^ 0x9e3779b9u) % worldWidth;
    const uint speed = 1 + ((seed >> 3) & 3);
    const uint moving = (origin + frame * speed) % worldWidth;
    const uint camera = (frame * 7) % worldWidth;
    return (moving + worldWidth - camera) % worldWidth;
}

uint CrowdLocalY(uint seed, uint frame, uint height)
{
    const uint origin = CrowdHash32(seed ^ 0x85ebca6bu) % height;
    const uint delta = frame * (1 + ((seed >> 7) & 1));
    return (seed & 0x20) == 0
        ? (origin + delta) % height
        : (origin + height - (delta % height)) % height;
}

bool CrowdVisible(uint seed, uint frame, uint width, uint visibilityMask)
{
    return (CrowdHash32(seed ^ 0xd1b54a35u) & visibilityMask) == 0 &&
        CrowdLocalX(seed, frame, width) < width;
}

uint2 CrowdPosition(uint seed, uint frame, uint width, uint height)
{
    return uint2(CrowdLocalX(seed, frame, width), CrowdLocalY(seed, frame, height));
}

uint CrowdTileIndex(uint seed, uint frame, uint width, uint height)
{
    const uint2 position = CrowdPosition(seed, frame, width, height);
    const uint tileCountX = (width + HLSLPERF_CROWD_TILE_SIZE - 1) / HLSLPERF_CROWD_TILE_SIZE;
    return (position.y / HLSLPERF_CROWD_TILE_SIZE) * tileCountX +
        position.x / HLSLPERF_CROWD_TILE_SIZE;
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void ProduceCrowdVisibilityFlags(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    const uint linearGroup = groupId.y * DispatchGroupsX + groupId.x;
    if (linearGroup >= DispatchGroupCount)
        return;
    const uint threadStart = linearGroup * ElementsPerBlock + groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;

#if HLSLPERF_VECTOR_WIDTH == 4
    [unroll]
    for (uint chunk = 0; chunk < HLSLPERF_ELEMENTS_PER_THREAD; chunk += 4)
    {
        uint4 seeds = 0;
        if (threadStart + chunk + 3 < ElementCount)
            seeds = Input0.Load4((threadStart + chunk) * 4);
        else
        {
            [unroll]
            for (uint tail = 0; tail < 4; ++tail)
            {
                const uint index = threadStart + chunk + tail;
                if (chunk + tail < HLSLPERF_ELEMENTS_PER_THREAD && index < ElementCount)
                    seeds[tail] = Input0.Load(index * 4);
            }
        }
        [unroll]
        for (uint component = 0; component < 4; ++component)
        {
            const uint item = chunk + component;
            const uint index = threadStart + item;
            if (item < HLSLPERF_ELEMENTS_PER_THREAD && index < ElementCount)
                Output0.Store(index * 4, CrowdVisible(seeds[component], Parameter4, Parameter3, Parameter5) ? 1 : 0);
        }
    }
#else
    [unroll]
    for (uint item = 0; item < HLSLPERF_ELEMENTS_PER_THREAD; ++item)
    {
        const uint index = threadStart + item;
        if (index < ElementCount)
        {
            const uint seed = Input0.Load(index * 4);
            Output0.Store(index * 4, CrowdVisible(seed, Parameter4, Parameter3, Parameter5) ? 1 : 0);
        }
    }
#endif
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void ScatterVisibleCrowdSeeds(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    const uint linearGroup = groupId.y * DispatchGroupsX + groupId.x;
    if (linearGroup >= DispatchGroupCount)
        return;
    const uint threadStart = linearGroup * ElementsPerBlock + groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;
    [unroll]
    for (uint item = 0; item < HLSLPERF_ELEMENTS_PER_THREAD; ++item)
    {
        const uint index = threadStart + item;
        if (index < ElementCount)
        {
            const uint seed = Input0.Load(index * 4);
            const uint prefix = Input1.Load(index * 4);
            const bool visible = CrowdVisible(seed, Parameter4, Parameter3, Parameter5);
            if (visible)
                Output0.Store((prefix + 1) * 4, seed);
            if (index == ElementCount - 1)
                Output0.Store(0, prefix + (visible ? 1 : 0));
        }
    }
}

#define HLSLPERF_CROWD_FUSED_ITEMS_PER_THREAD \
    (HLSLPERF_ELEMENTS_PER_THREAD * HLSLPERF_SINGLE_PASS_ITEMS_SCALE)

// Optimized application path: generate visibility, scan, and scatter directly
// without writing the N-element flag and prefix arrays.
[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
HLSLPERF_WAVE_ATTRIBUTE
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void FusedCrowdVisibilityCompact(uint groupIndex : SV_GroupIndex)
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
        const uint threadStart = blockStart + groupIndex * HLSLPERF_CROWD_FUSED_ITEMS_PER_THREAD;
        uint seeds[HLSLPERF_CROWD_FUSED_ITEMS_PER_THREAD];
        uint flags[HLSLPERF_CROWD_FUSED_ITEMS_PER_THREAD];
        uint localPrefix[HLSLPERF_CROWD_FUSED_ITEMS_PER_THREAD];

#if HLSLPERF_VECTOR_WIDTH == 4
        [unroll]
        for (uint chunk = 0; chunk < HLSLPERF_CROWD_FUSED_ITEMS_PER_THREAD; chunk += 4)
        {
            uint4 loaded = 0;
            if (threadStart + chunk + 3 < ElementCount)
                loaded = Input0.Load4((threadStart + chunk) * 4);
            else
            {
                [unroll]
                for (uint tail = 0; tail < 4; ++tail)
                {
                    const uint item = chunk + tail;
                    const uint index = threadStart + item;
                    if (item < HLSLPERF_CROWD_FUSED_ITEMS_PER_THREAD && index < ElementCount)
                        loaded[tail] = Input0.Load(index * 4);
                }
            }
            [unroll]
            for (uint component = 0; component < 4; ++component)
            {
                const uint item = chunk + component;
                if (item < HLSLPERF_CROWD_FUSED_ITEMS_PER_THREAD)
                    seeds[item] = loaded[component];
            }
        }
#else
        [unroll]
        for (uint fusedLoadItem = 0; fusedLoadItem < HLSLPERF_CROWD_FUSED_ITEMS_PER_THREAD; ++fusedLoadItem)
        {
            const uint index = threadStart + fusedLoadItem;
            seeds[fusedLoadItem] = index < ElementCount ? Input0.Load(index * 4) : 0;
        }
#endif

        uint threadTotal = 0;
        [unroll]
        for (uint fusedPrefixItem = 0; fusedPrefixItem < HLSLPERF_CROWD_FUSED_ITEMS_PER_THREAD; ++fusedPrefixItem)
        {
            const uint index = threadStart + fusedPrefixItem;
            const uint flag = index < ElementCount &&
                CrowdVisible(seeds[fusedPrefixItem], Parameter4, Parameter3, Parameter5) ? 1 : 0;
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
            uint running = 0;
            for (uint wave = 0; wave < waveCount; ++wave)
            {
                const uint current = ThreadTotals[wave];
                ThreadTotals[wave] = running;
                running += current;
            }
            SinglePassBlockTotal = running;
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
        for (uint fusedScatterItem = 0; fusedScatterItem < HLSLPERF_CROWD_FUSED_ITEMS_PER_THREAD; ++fusedScatterItem)
        {
            if (flags[fusedScatterItem] != 0)
            {
                const uint outputIndex =
                    SinglePassBlockPrefix + localBlockPrefix + localPrefix[fusedScatterItem];
                Output0.Store((outputIndex + 1) * 4, seeds[fusedScatterItem]);
            }
        }
        GroupMemoryBarrierWithGroupSync();
    }
}

#if HLSLPERF_CROWD_BACKEND == 2
groupshared uint CrowdLocalTileBins[
    HLSLPERF_CROWD_TILE_BINS * HLSLPERF_CROWD_TILE_REPLICAS];
#endif

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(256, 1, 1)]
void ResetCrowdTileBins(uint dispatchIndex : SV_DispatchThreadID)
{
    if (dispatchIndex < Parameter0)
    {
        Output0.Store(dispatchIndex * 4, 0);
        Output1.Store(dispatchIndex * 4, 0);
    }
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void BuildCrowdTileHistogram(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    const uint linearGroup = groupId.y * Parameter6 + groupId.x;
    if (linearGroup >= Parameter7)
        return;

#if HLSLPERF_CROWD_BACKEND == 2
    for (uint index = groupIndex;
        index < HLSLPERF_CROWD_TILE_BINS * HLSLPERF_CROWD_TILE_REPLICAS;
        index += HLSLPERF_GROUP_SIZE)
        CrowdLocalTileBins[index] = 0;
    GroupMemoryBarrierWithGroupSync();
#endif

    const uint threadStart = linearGroup * Parameter1 +
        groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;
    [unroll]
    for (uint item = 0; item < HLSLPERF_ELEMENTS_PER_THREAD; ++item)
    {
        const uint index = threadStart + item;
        if (index < Parameter0)
        {
            const uint seed = Input0.Load((index + 1) * 4);
            const uint tile = CrowdTileIndex(seed, Parameter5, Parameter3, Parameter4);
            uint ignored;
#if HLSLPERF_CROWD_BACKEND == 2
            const uint replica = (groupIndex + item) % HLSLPERF_CROWD_TILE_REPLICAS;
            InterlockedAdd(
                CrowdLocalTileBins[replica * HLSLPERF_CROWD_TILE_BINS + tile],
                1,
                ignored);
#else
            Output0.InterlockedAdd(tile * 4, 1, ignored);
#endif
        }
    }

#if HLSLPERF_CROWD_BACKEND == 2
    GroupMemoryBarrierWithGroupSync();
    for (uint tile = groupIndex; tile < HLSLPERF_CROWD_TILE_BINS; tile += HLSLPERF_GROUP_SIZE)
    {
        uint total = 0;
        [unroll]
        for (uint replica = 0; replica < HLSLPERF_CROWD_TILE_REPLICAS; ++replica)
            total += CrowdLocalTileBins[replica * HLSLPERF_CROWD_TILE_BINS + tile];
        if (total != 0)
        {
            uint ignored;
            Output0.InterlockedAdd(tile * 4, total, ignored);
        }
    }
#endif
}

groupshared uint CrowdTilePrefix[HLSLPERF_CROWD_TILE_BINS];
groupshared uint CrowdTileTotal;

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_CROWD_TILE_BINS, 1, 1)]
void PrefixCrowdTileHistogram(uint groupIndex : SV_GroupIndex)
{
    CrowdTilePrefix[groupIndex] = Input0.Load(groupIndex * 4);
    GroupMemoryBarrierWithGroupSync();

    [unroll]
    for (uint upsweepOffset = 1; upsweepOffset < HLSLPERF_CROWD_TILE_BINS; upsweepOffset <<= 1)
    {
        const uint node = (groupIndex + 1) * upsweepOffset * 2 - 1;
        if (node < HLSLPERF_CROWD_TILE_BINS)
            CrowdTilePrefix[node] += CrowdTilePrefix[node - upsweepOffset];
        GroupMemoryBarrierWithGroupSync();
    }

    if (groupIndex == 0)
    {
        CrowdTileTotal = CrowdTilePrefix[HLSLPERF_CROWD_TILE_BINS - 1];
        CrowdTilePrefix[HLSLPERF_CROWD_TILE_BINS - 1] = 0;
    }
    GroupMemoryBarrierWithGroupSync();

    [unroll]
    for (uint downsweepOffset = HLSLPERF_CROWD_TILE_BINS / 2; downsweepOffset > 0; downsweepOffset >>= 1)
    {
        const uint node = (groupIndex + 1) * downsweepOffset * 2 - 1;
        if (node < HLSLPERF_CROWD_TILE_BINS)
        {
            const uint left = CrowdTilePrefix[node - downsweepOffset];
            CrowdTilePrefix[node - downsweepOffset] = CrowdTilePrefix[node];
            CrowdTilePrefix[node] += left;
        }
        GroupMemoryBarrierWithGroupSync();
    }

    Output0.Store(groupIndex * 4, CrowdTilePrefix[groupIndex]);
    if (groupIndex == 0)
        Output0.Store(HLSLPERF_CROWD_TILE_BINS * 4, CrowdTileTotal);
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void ScatterCrowdTileSeeds(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    const uint linearGroup = groupId.y * Parameter6 + groupId.x;
    if (linearGroup >= Parameter7)
        return;
    const uint threadStart = linearGroup * Parameter1 +
        groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;
    [unroll]
    for (uint item = 0; item < HLSLPERF_ELEMENTS_PER_THREAD; ++item)
    {
        const uint index = threadStart + item;
        if (index < Parameter0)
        {
            const uint seed = Input0.Load((index + 1) * 4);
            const uint tile = CrowdTileIndex(seed, Parameter5, Parameter3, Parameter4);
            uint localIndex;
            Output1.InterlockedAdd(tile * 4, 1, localIndex);
            const uint tileStart = Input1.Load(tile * 4);
            Output0.Store((tileStart + localIndex) * 4, seed);
        }
    }
}

void CrowdAddSaturated(
    inout uint red,
    inout uint green,
    inout uint blue,
    uint addRed,
    uint addGreen,
    uint addBlue)
{
    red = min(255u, red + addRed);
    green = min(255u, green + addGreen);
    blue = min(255u, blue + addBlue);
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(8, 8, 1)]
void RasterizeCrowdVfx(uint3 dispatchIndex : SV_DispatchThreadID)
{
    const uint x = dispatchIndex.x;
    const uint y = dispatchIndex.y;
    const uint width = Parameter3;
    const uint height = Parameter4;
    const uint frame = Parameter5;
    if (x >= width || y >= height)
        return;

    uint red = 3 + y * 5 / height;
    uint green = 7 + y * 7 / height;
    uint blue = 18 + y * 14 / height;
    if ((x % 48) == 0 || (y % 48) == 0)
        CrowdAddSaturated(red, green, blue, 4, 10, 14);
    const uint pixel = y * width + x;
    const uint star = CrowdHash32(pixel + 0xa511e9b3u);
    if ((star & 2047) < 3)
        CrowdAddSaturated(red, green, blue, 28, 36, 48);

    const int tileX = (int)(x / HLSLPERF_CROWD_TILE_SIZE);
    const int tileY = (int)(y / HLSLPERF_CROWD_TILE_SIZE);
    const int minimumTileX = max(0, tileX - 1);
    const int maximumTileX = min((int)Parameter6 - 1, tileX + 1);
    const int minimumTileY = max(0, tileY - 1);
    const int maximumTileY = min((int)Parameter7 - 1, tileY + 1);
    for (int neighborY = minimumTileY; neighborY <= maximumTileY; ++neighborY)
    {
        for (int neighborX = minimumTileX; neighborX <= maximumTileX; ++neighborX)
        {
            const uint tile = (uint)neighborY * Parameter6 + (uint)neighborX;
            const uint begin = Input1.Load(tile * 4);
            const uint end = Input1.Load((tile + 1) * 4);
            for (uint index = begin; index < end; ++index)
            {
                const uint seed = Input0.Load(index * 4);
                const uint2 center = CrowdPosition(seed, frame, width, height);
                const uint dx = x > center.x ? x - center.x : center.x - x;
                const uint dy = y > center.y ? y - center.y : center.y - y;
                const uint colorHash = CrowdHash32(seed ^ 0xc2b2ae35u);
                const uint radius = 1 + (colorHash & 1);
                const uint distance = max(dx, dy);
                if (distance <= radius)
                {
                    const uint power = (radius + 1 - distance) * 52;
                    const uint kind = (colorHash >> 8) % 3;
                    if (kind == 0)
                        CrowdAddSaturated(red, green, blue, power / 5, power, power);
                    else if (kind == 1)
                        CrowdAddSaturated(red, green, blue, power, power / 4, power);
                    else
                        CrowdAddSaturated(red, green, blue, power, power / 2, power / 8);
                }
            }
        }
    }

    const uint atlasIndex = frame * width * height + pixel;
    Output0.Store(atlasIndex * 4, red | (green << 8) | (blue << 16) | 0xff000000u);
}
