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
#ifndef HLSLPERF_RADIX_RANK_BALLOT
#define HLSLPERF_RADIX_RANK_BALLOT 0
#endif
#ifndef HLSLPERF_RADIX_TILE
#define HLSLPERF_RADIX_TILE 0
#endif
#if HLSLPERF_RADIX_TILE != 0 && HLSLPERF_RADIX_TILE != 1
#error HLSLPERF_RADIX_TILE must be zero or one.
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
#if HLSLPERF_RADIX_RANK_BALLOT
#if HLSLPERF_GROUP_SIZE != 128 || HLSLPERF_ELEMENTS_PER_THREAD != 2 || HLSLPERF_RADIX_BITS != 8 || HLSLPERF_WAVE_SIZE != 32
#error Ballot rank is a fixed group128, two-record, eight-bit, wave32 candidate.
#endif
// Four waves, two original-order records/lane, eight bit planes plus validity.
groupshared uint RadixRankPlanes[4][2][9];
#endif

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
#if HLSLPERF_RADIX_RANK_BALLOT
[WaveSize(32)]
#endif
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
#if HLSLPERF_RADIX_RANK_BALLOT
    const uint rankWave = lane / 32;
    const uint rankLane = WaveGetLaneIndex();
    [unroll]
    for (uint sourceItem = 0; sourceItem < 2; sourceItem++)
    {
        const uint sourceDigit = RadixDigits[lane * 2 + sourceItem];
        const uint valid = WaveActiveBallot(block * ElementsPerBlock + lane * 2 + sourceItem < ElementCount).x;
        if (rankLane == 0) RadixRankPlanes[rankWave][sourceItem][8] = valid;
        [unroll]
        for (uint bit = 0; bit < 8; bit++)
        {
            const uint plane = WaveActiveBallot((sourceDigit & (1u << bit)) != 0).x;
            if (rankLane == 0) RadixRankPlanes[rankWave][sourceItem][bit] = plane;
        }
    }
    GroupMemoryBarrierWithGroupSync();
#endif
    [unroll]
    for (uint item = 0; item < HLSLPERF_ELEMENTS_PER_THREAD; item++)
    {
        const uint local = lane * HLSLPERF_ELEMENTS_PER_THREAD + item;
        const uint index = block * ElementsPerBlock + local;
        if (index < ElementCount)
        {
            const uint digit = RadixDigits[local];
            uint rank = 0;
#if HLSLPERF_RADIX_RANK_BALLOT
            // Count all earlier waves, then earlier lanes and earlier same-lane items.
            // Validity is separate: invalid tail records must never match digit 255.
            for (uint sourceWave = 0; sourceWave <= rankWave; sourceWave++)
            {
                [unroll]
                for (uint sourceItem = 0; sourceItem < 2; sourceItem++)
                {
                    uint matches = RadixRankPlanes[sourceWave][sourceItem][8];
                    [unroll]
                    for (uint bit = 0; bit < 8; bit++)
                    {
                        const uint plane = RadixRankPlanes[sourceWave][sourceItem][bit];
                        matches &= (digit & (1u << bit)) != 0 ? plane : ~plane;
                    }
                    if (sourceWave == rankWave)
                        matches &= ((1u << rankLane) - 1u) | (sourceItem < item ? 1u << rankLane : 0u);
                    rank += countbits(matches);
                }
            }
#else
            // Explicit original-order rank: atomic arrival order never decides stability.
            for (uint previous = 0; previous < local; previous++)
                rank += RadixDigits[previous] == digit ? 1 : 0;
#endif
            const uint offset = Input1.Load((digit * DispatchGroupCount + block) * 4);
            StoreRadixRecord(offset + rank, index, LoadRadixKey(index));
        }
    }
}
#endif

#if HLSLPERF_RADIX_TILE
#if HLSLPERF_GROUP_SIZE != 128 || HLSLPERF_ELEMENTS_PER_THREAD != 4 || (HLSLPERF_RADIX_BITS != 4 && HLSLPERF_RADIX_BITS != 8) || HLSLPERF_WAVE_SIZE != 32 || HLSLPERF_SCAN_BACKEND != 2 || HLSLPERF_VECTOR_WIDTH != 1 || HLSLPERF_RADIX_RANK_BALLOT
#error Tiled radix requires group128, four records, four/eight bits, wave32, backend2, scalar records and no legacy ballot rank.
#endif

// Independently authored, opt-in, Unmeasured. The histogram is bin-major.
// t1 in scatter: [tile prefixes][summary prefixes][global bin bases].
// Parameter4 = ceil(record tiles / 512); Parameter5 = record tile count.
#define TILE_WAVES 4
#define TILE_SEGMENTS 16
#define TILE_PREFIX_VALUES 512
#define TILE_BINS_PER_THREAD ((RADIX_BINS + HLSLPERF_GROUP_SIZE - 1) / HLSLPERF_GROUP_SIZE)

groupshared uint TileNextWave;
groupshared uint TileWaveTotals[TILE_WAVES];
groupshared uint TileHistogram[RADIX_BINS * TILE_SEGMENTS];
groupshared uint TileBinBase[RADIX_BINS];
groupshared uint TileGlobalBase[RADIX_BINS];
groupshared uint TileKeys[RADIX_BLOCK];
#if HLSLPERF_RADIX_PAIRS
groupshared uint TilePayloads[RADIX_BLOCK];
#endif

// Wave lane order need not match SV_GroupIndex. Assign a unique virtual wave
// and map input records to its lane order; atomic arrival never ranks records.
uint RadixTileThread(uint groupIndex)
{
    if (groupIndex == 0) TileNextWave = 0;
    GroupMemoryBarrierWithGroupSync();
    uint wave = 0;
    if (WaveIsFirstLane()) InterlockedAdd(TileNextWave, 1, wave);
    wave = WaveReadLaneFirst(wave);
    return wave * 32 + WaveGetLaneIndex();
}

void RadixTileGroupPrefix(uint thread, uint value, out uint prefix, out uint total)
{
    const uint wave = thread / 32;
    prefix = WavePrefixSum(value);
    const uint waveTotal = WaveActiveSum(value);
    if (WaveIsFirstLane()) TileWaveTotals[wave] = waveTotal;
    GroupMemoryBarrierWithGroupSync();
    total = 0;
    [unroll]
    for (uint prefixWave = 0; prefixWave < TILE_WAVES; prefixWave++)
    {
        const uint subtotal = TileWaveTotals[prefixWave];
        if (prefixWave < wave) prefix += subtotal;
        total += subtotal;
    }
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[WaveSize(32)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void PrefixRadixHistogramTiles(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    const uint linearTile = groupId.y * DispatchGroupsX + groupId.x;
    if (linearTile >= DispatchGroupCount) return;
    const uint thread = RadixTileThread(groupIndex);
    const uint bin = linearTile / Parameter4;
    const uint chunk = linearTile % Parameter4;
    const uint first = chunk * TILE_PREFIX_VALUES + thread * 4;
    uint values[4];
    uint sum = 0;
    [unroll]
    for (uint load = 0; load < 4; load++)
    {
        values[load] = first + load < Parameter5 ? Input0.Load((bin * Parameter5 + first + load) * 4) : 0;
        sum += values[load];
    }
    uint prefix, total;
    RadixTileGroupPrefix(thread, sum, prefix, total);
    [unroll]
    for (uint store = 0; store < 4; store++)
    {
        if (first + store < Parameter5) Output0.Store((bin * Parameter5 + first + store) * 4, prefix);
        prefix += values[store];
    }
    if (thread == 0) Output1.Store(linearTile * 4, total);
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[WaveSize(32)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void PrefixRadixHistogramBins(uint groupIndex : SV_GroupIndex)
{
    const uint thread = RadixTileThread(groupIndex);
    const uint summaryOffset = RADIX_BINS * Parameter5;
    const uint binOffset = summaryOffset + RADIX_BINS * Parameter4;
    uint totals[TILE_BINS_PER_THREAD];
    uint threadTotal = 0;
    [unroll]
    for (uint binItem = 0; binItem < TILE_BINS_PER_THREAD; binItem++)
    {
        const uint bin = thread * TILE_BINS_PER_THREAD + binItem;
        uint sum = 0;
        if (bin < RADIX_BINS)
        {
            // One thread owns a bin. At 4 Mi records this loop has 16 summaries;
            // very large inputs remain correct but this serial work may limit scale.
            for (uint chunk = 0; chunk < Parameter4; chunk++)
            {
                const uint index = bin * Parameter4 + chunk;
                Output0.Store((summaryOffset + index) * 4, sum);
                sum += Input0.Load(index * 4);
            }
        }
        totals[binItem] = sum;
        threadTotal += sum;
    }
    uint prefix, total;
    RadixTileGroupPrefix(thread, threadTotal, prefix, total);
    [unroll]
    for (uint storeBin = 0; storeBin < TILE_BINS_PER_THREAD; storeBin++)
    {
        const uint bin = thread * TILE_BINS_PER_THREAD + storeBin;
        if (bin < RADIX_BINS) Output0.Store((binOffset + bin) * 4, prefix);
        prefix += totals[storeBin];
    }
}

void ScatterRadixTileImpl(uint3 groupId, uint groupIndex, bool splitPairs)
{
    const uint block = groupId.y * DispatchGroupsX + groupId.x;
    if (block >= DispatchGroupCount) return;
    const uint thread = RadixTileThread(groupIndex);
    const uint waveLane = WaveGetLaneIndex();
    const uint wave = thread / 32;
    const uint blockStart = block * RADIX_BLOCK;
    const uint validCount = min((uint)RADIX_BLOCK, ElementCount - blockStart);
    for (uint clear = thread; clear < RADIX_BINS * TILE_SEGMENTS; clear += HLSLPERF_GROUP_SIZE)
        TileHistogram[clear] = 0;
    GroupMemoryBarrierWithGroupSync();

    uint keys[4], ranks[4];
#if HLSLPERF_RADIX_PAIRS
    uint payloads[4];
#endif
    [unroll]
    for (uint loadItem = 0; loadItem < 4; loadItem++)
    {
        const uint local = loadItem * HLSLPERF_GROUP_SIZE + thread;
        const bool valid = local < validCount;
        keys[loadItem] = 0;
#if HLSLPERF_RADIX_PAIRS
        uint2 record = 0;
        if (valid) record = Input0.Load2((blockStart + local) * 8);
        keys[loadItem] = record.x;
        payloads[loadItem] = record.y;
#else
        if (valid) keys[loadItem] = Input0.Load((blockStart + local) * 4);
#endif
        const uint digit = (keys[loadItem] >> RadixBit) & Parameter3;
        uint matches = WaveActiveBallot(valid).x;
        [unroll]
        for (uint bit = 0; bit < HLSLPERF_RADIX_BITS; bit++)
        {
            const uint plane = WaveActiveBallot((digit & (1u << bit)) != 0).x;
            matches &= (digit & (1u << bit)) != 0 ? plane : ~plane;
        }
        ranks[loadItem] = countbits(matches & ((1u << waveLane) - 1u));
        // Exactly one lane writes each nonempty segment/bin. Tails are excluded
        // by validity, including digit zero, 255 and a partial final digit.
        if (valid && ranks[loadItem] == 0)
            TileHistogram[digit * TILE_SEGMENTS + loadItem * TILE_WAVES + wave] = countbits(matches);
    }
    GroupMemoryBarrierWithGroupSync();

    uint binTotals[TILE_BINS_PER_THREAD];
    uint threadTotal = 0;
    [unroll]
    for (uint binItem = 0; binItem < TILE_BINS_PER_THREAD; binItem++)
    {
        const uint bin = thread * TILE_BINS_PER_THREAD + binItem;
        uint sum = 0;
        if (bin < RADIX_BINS)
        {
            [unroll]
            for (uint segment = 0; segment < TILE_SEGMENTS; segment++)
            {
                const uint offset = bin * TILE_SEGMENTS + segment;
                const uint count = TileHistogram[offset];
                TileHistogram[offset] = sum;
                sum += count;
            }
            // Cache the three global prefix components once per occupied bin.
            // Equal/duplicate-heavy tiles never load prefixes for unused bins.
            uint globalOffset = 0;
            if (sum != 0)
            {
                const uint histogramValues = RADIX_BINS * Parameter5;
                const uint summary = bin * Parameter4 + block / TILE_PREFIX_VALUES;
                globalOffset = Input1.Load((histogramValues + RADIX_BINS * Parameter4 + bin) * 4)
                    + Input1.Load((histogramValues + summary) * 4)
                    + Input1.Load((bin * Parameter5 + block) * 4);
            }
            TileGlobalBase[bin] = globalOffset;
        }
        binTotals[binItem] = sum;
        threadTotal += sum;
    }
    uint binPrefix, blockTotal;
    RadixTileGroupPrefix(thread, threadTotal, binPrefix, blockTotal);
    [unroll]
    for (uint baseItem = 0; baseItem < TILE_BINS_PER_THREAD; baseItem++)
    {
        const uint bin = thread * TILE_BINS_PER_THREAD + baseItem;
        if (bin < RADIX_BINS) TileBinBase[bin] = binPrefix;
        binPrefix += binTotals[baseItem];
    }
    GroupMemoryBarrierWithGroupSync();
    [unroll]
    for (uint reorderItem = 0; reorderItem < 4; reorderItem++)
    {
        if (reorderItem * HLSLPERF_GROUP_SIZE + thread < validCount)
        {
            const uint digit = (keys[reorderItem] >> RadixBit) & Parameter3;
            const uint local = TileBinBase[digit] + TileHistogram[digit * TILE_SEGMENTS + reorderItem * TILE_WAVES + wave] + ranks[reorderItem];
            TileKeys[local] = keys[reorderItem];
#if HLSLPERF_RADIX_PAIRS
            TilePayloads[local] = payloads[reorderItem];
#endif
        }
    }
    GroupMemoryBarrierWithGroupSync();
    [unroll]
    for (uint scatterItem = 0; scatterItem < 4; scatterItem++)
    {
        const uint local = scatterItem * HLSLPERF_GROUP_SIZE + thread;
        if (local < validCount)
        {
            const uint key = TileKeys[local];
            const uint digit = (key >> RadixBit) & Parameter3;
            const uint destination = TileGlobalBase[digit] + local - TileBinBase[digit];
#if HLSLPERF_RADIX_PAIRS
            if (splitPairs)
            {
                Output0.Store(destination * 4, key);
                Output1.Store(destination * 4, TilePayloads[local]);
            }
            else Output0.Store2(destination * 8, uint2(key, TilePayloads[local]));
#else
            Output0.Store(destination * 4, key);
#endif
        }
    }
}

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[WaveSize(32)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void ScatterRadixTile(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    ScatterRadixTileImpl(groupId, groupIndex, false);
}

#if HLSLPERF_RADIX_PAIRS
[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[WaveSize(32)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void ScatterRadixTileToPairs(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    ScatterRadixTileImpl(groupId, groupIndex, true);
}
#endif
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

// Optional native-host SoA -> AoS boundary. Include this operation when timing
// an adapter whose producer supplies separate keys and payloads. It does not
// generate or reinterpret payloads. Managed radix workloads already supply AoS.
[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void PackRadixPairs(uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex)
{
    const uint block = groupId.y * DispatchGroupsX + groupId.x;
    if (block >= DispatchGroupCount) return;
    [unroll]
    for (uint item = 0; item < HLSLPERF_ELEMENTS_PER_THREAD; item++)
    {
        const uint index = block * ElementsPerBlock + lane * HLSLPERF_ELEMENTS_PER_THREAD + item;
        if (index < ElementCount) Output0.Store2(index * 8, uint2(Input0.Load(index * 4), Input1.Load(index * 4)));
        else if (ElementCount == 0 && index == 0) Output0.Store2(0, uint2(0, 0));
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
