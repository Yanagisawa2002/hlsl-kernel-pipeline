// Independent bounded demo, not a promoted performance candidate.
ByteAddressBuffer Input0 : register(t0);
ByteAddressBuffer Input1 : register(t1);
RWByteAddressBuffer Output0 : register(u0);
RWByteAddressBuffer Output1 : register(u1);
cbuffer Params : register(b0)
{
    uint ElementCount; uint ActiveMode; uint MaximumItems; uint CountOffset;
    uint MetadataWords; uint OverflowProbe; uint Reserved0; uint Reserved1;
};

[numthreads(64,1,1)]
void ResetConsumer(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x < max(MaximumItems, 1)) Output0.Store(tid.x * 4, 0);
    if (tid.x < 8) Output1.Store(tid.x * 4, 0);
}

// Serial stable producer deliberately keeps this ABI example independent of scan providers.
[numthreads(1,1,1)]
void CompactAndCount()
{
    for (uint clear = 0; clear < max(ElementCount, 1); ++clear) Output0.Store(clear * 4, 0);
    for (uint meta = 0; meta < MetadataWords; ++meta) Output1.Store(meta * 4, 0);
    uint active = 0;
    for (uint index = 0; index < ElementCount; ++index)
    {
        uint value = Input0.Load(index * 4);
        if (ActiveMode == 2 || (ActiveMode == 1 && (value & 3) == 0))
            Output0.Store(active++ * 4, value);
    }
    Output1.Store(CountOffset, OverflowProbe != 0 ? 0xffffffff : active);
}

[numthreads(HLSLPERF_GROUP_SIZE,1,1)]
void ConsumeAndBin(uint3 tid : SV_DispatchThreadID)
{
    uint count = min(Input1.Load(CountOffset), MaximumItems);
    if (tid.x >= count) return;
    uint value = Input0.Load(tid.x * 4);
    Output0.Store(tid.x * 4, value * 17 + tid.x);
    uint unused;
    Output1.InterlockedAdd((value & 7) * 4, 1, unused);
}
