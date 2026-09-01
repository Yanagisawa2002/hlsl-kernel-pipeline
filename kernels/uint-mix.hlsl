RWByteAddressBuffer Output : register(u0);

cbuffer DispatchParameters : register(b0)
{
    uint WorkItemCount;
    uint Seed;
};

#ifndef HLSLPERF_GROUP_SIZE
#define HLSLPERF_GROUP_SIZE 256
#endif

#ifndef HLSLPERF_ELEMENTS_PER_THREAD
#define HLSLPERF_ELEMENTS_PER_THREAD 1
#endif

#ifndef HLSLPERF_ALU_ROUNDS
#define HLSLPERF_ALU_ROUNDS 64
#endif

[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint baseIndex = dispatchThreadId.x * HLSLPERF_ELEMENTS_PER_THREAD;

    [unroll]
    for (uint element = 0; element < HLSLPERF_ELEMENTS_PER_THREAD; ++element)
    {
        uint index = baseIndex + element;
        if (index >= WorkItemCount)
            continue;

        uint value = index ^ Seed;
        [unroll]
        for (uint round = 0; round < HLSLPERF_ALU_ROUNDS; ++round)
        {
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            value = value * 1664525u + 1013904223u + round;
        }

        Output.Store(index * 4, value);
    }
}
