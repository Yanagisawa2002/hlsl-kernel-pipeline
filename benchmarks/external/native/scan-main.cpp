#include "ExecutionAuthorization.h"
// Reuse upstream device setup and keep its entry point intact under a local symbol.
#define main HlslPerfOriginalUpstreamMain
#include "GPUPrefixSumsD3D12.cpp"
#undef main
#include "HlslPerfScan.h"

int main(int argc, char** argv)
{
    if (!HlslPerfExecutionAuthorized()) return 77;
    if (argc == 2 && std::strcmp(argv[1], "upstream") == 0) return HlslPerfOriginalUpstreamMain();
    if (argc != 2 || std::strcmp(argv[1], "candidate") != 0) return 2;
    auto device = InitDevice();
    auto info = GetDeviceInfo(device.get());
    if (!info.SupportsWaveIntrinsics || info.SIMDWidth > 32 || info.SIMDMaxWidth < 32 || info.SupportedShaderModel < L"cs_6_6") return 3;
    std::puts("HlslPerf candidate adapter (Unmeasured): inherited upstream input/validator/batch; inclusive conversion included.");
    HlslPerfScan candidate(device, info);
    candidate.TestAll();
    candidate.BatchTimingInclusiveInitOne(1 << 28, 100);
    return 0;
}
