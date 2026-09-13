// Local semantic adapter: the upstream native batch is inclusive; our primitive is exclusive.
// This extra pass is inside the upstream timed operation and never modifies the input.
ByteAddressBuffer Input0 : register(t0);
RWByteAddressBuffer Output0 : register(u0);
cbuffer Constants : register(b0) { uint ElementCount; uint3 unused; uint4 unused2; };
[numthreads(256, 1, 1)]
void AddInput(uint3 id : SV_DispatchThreadID)
{
    // One bounded dispatch also covers upstream's 2^28 default.
    for (uint i = id.x; i < ElementCount; i += 256 * 256)
        Output0.Store(i * 4, Output0.Load(i * 4) + Input0.Load(i * 4));
}
