#define HLSLPERF_ROOT_SIGNATURE "SRV(t0), SRV(t1), UAV(u0), UAV(u1), RootConstants(num32BitConstants=8, b0)"

ByteAddressBuffer Input0 : register(t0);
RWByteAddressBuffer Output0 : register(u0);

cbuffer DispatchParameters : register(b0)
{
    uint Width;
    uint Height;
    uint Parameter2;
    uint Parameter3;
    uint Parameter4;
    uint Parameter5;
    uint Parameter6;
    uint Parameter7;
};

#ifndef HLSLPERF_TILE_DIM
#define HLSLPERF_TILE_DIM 16
#endif

#ifndef HLSLPERF_BLOCK_ROWS
#define HLSLPERF_BLOCK_ROWS 8
#endif

groupshared uint Tile[HLSLPERF_TILE_DIM][HLSLPERF_TILE_DIM + 1];

[RootSignature(HLSLPERF_ROOT_SIGNATURE)]
[numthreads(HLSLPERF_TILE_DIM, HLSLPERF_BLOCK_ROWS, 1)]
void TransposePass(uint3 groupId : SV_GroupID, uint3 groupThreadId : SV_GroupThreadID)
{
    uint inputX = groupId.x * HLSLPERF_TILE_DIM + groupThreadId.x;
    uint inputY = groupId.y * HLSLPERF_TILE_DIM + groupThreadId.y;

    [unroll]
    for (uint loadRow = 0; loadRow < HLSLPERF_TILE_DIM; loadRow += HLSLPERF_BLOCK_ROWS)
    {
        if (groupThreadId.y + loadRow < HLSLPERF_TILE_DIM && inputX < Width && inputY + loadRow < Height)
            Tile[groupThreadId.y + loadRow][groupThreadId.x] = Input0.Load(((inputY + loadRow) * Width + inputX) * 4);
    }
    GroupMemoryBarrierWithGroupSync();

    const uint outputX = groupId.y * HLSLPERF_TILE_DIM + groupThreadId.x;
    const uint outputY = groupId.x * HLSLPERF_TILE_DIM + groupThreadId.y;
    [unroll]
    for (uint storeRow = 0; storeRow < HLSLPERF_TILE_DIM; storeRow += HLSLPERF_BLOCK_ROWS)
    {
        if (groupThreadId.y + storeRow < HLSLPERF_TILE_DIM && outputX < Height && outputY + storeRow < Width)
            Output0.Store(((outputY + storeRow) * Height + outputX) * 4, Tile[groupThreadId.x][groupThreadId.y + storeRow]);
    }
}
