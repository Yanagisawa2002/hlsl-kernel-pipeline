// Faithful boundary conversion from upstream uint32 key/payload SoA to our AoS records.
ByteAddressBuffer Input0 : register(t0);
ByteAddressBuffer Input1 : register(t1);
RWByteAddressBuffer Output0 : register(u0);
cbuffer Constants : register(b0) { uint Count; uint3 unused; uint4 unused2; };
[numthreads(256, 1, 1)]
void Interleave(uint3 id : SV_DispatchThreadID)
{
    for (uint i = id.x; i < Count; i += 256 * 256)
        Output0.Store2(i * 8, uint2(Input0.Load(i * 4), Input1.Load(i * 4)));
}
