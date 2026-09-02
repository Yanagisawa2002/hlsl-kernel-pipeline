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

#define HLSLPERF_SCAN_LOGICAL_BLOCK_COUNT Width
#define HLSLPERF_SCAN_DISPATCH_GROUPS_X DispatchGroupsX
#define HLSLPERF_SCAN_DISPATCH_GROUP_COUNT DispatchGroupCount
#include "../kernels/include/hlslperf/scan_u32.hlsli"

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
