#include "ExecutionAuthorization.h"
#define main HlslPerfOriginalUpstreamMain
#include "GPUSortingD3D12.cpp"
#undef main
#include "HlslPerfSort.h"

int main(int argc, char** argv)
{
    if (!HlslPerfExecutionAuthorized()) return 77;
    if (argc == 2 && std::strcmp(argv[1], "upstream") == 0) return HlslPerfOriginalUpstreamMain();
    if (argc != 2 || std::strcmp(argv[1], "candidate") != 0) return 2;
    auto device = InitDevice();
    auto info = GetDeviceInfo(device.get());
    if (!info.SupportsWaveIntrinsics || info.SIMDWidth > 32 || info.SIMDMaxWidth < 32) return 3;
    std::puts("HlslPerf candidate adapter (Unmeasured): inherited upstream input/validator/batch; SoA conversion included.");
    HlslPerfSort candidate(device, info);
    if (!candidate.TestAll()) return 4;
    candidate.BatchTiming(1 << 28, 100, 10, GPUSorting::ENTROPY_PRESET_1);
    return 0;
}
