#if HLSLPERF_SCAN_DIAGNOSTIC_COUNTERS
#define HLSLPERF_ROOT_SIGNATURE "SRV(t0), SRV(t1), UAV(u0), UAV(u1), UAV(u2), RootConstants(num32BitConstants=8, b0)"
#else
#define HLSLPERF_ROOT_SIGNATURE "SRV(t0), SRV(t1), UAV(u0), UAV(u1), RootConstants(num32BitConstants=8, b0)"
#endif

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

#define HLSLPERF_SCAN_LOGICAL_BLOCK_COUNT Parameter2
#define HLSLPERF_SCAN_DISPATCH_GROUPS_X Parameter6
#define HLSLPERF_SCAN_DISPATCH_GROUP_COUNT Parameter7
#include "include/hlslperf/scan_u32.hlsli"
