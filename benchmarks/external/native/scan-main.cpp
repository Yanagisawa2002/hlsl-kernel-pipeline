#include <cstring>
#include <cstdio>
// Reuse upstream device setup and keep its entry point intact under a local symbol.
#define main HlslPerfOriginalUpstreamMain
#include "GPUPrefixSumsD3D12.cpp"
#undef main
#include "HlslPerfScan.h"

int main(int argc, char** argv)
{
    if (argc == 1 || (argc == 2 && std::strcmp(argv[1], "--help") == 0))
    {
        std::puts("Usage: scan.exe run-upstream|run-candidate (executes native GPU tests and benchmark)");
        return 0;
    }
    if (argc == 2 && std::strcmp(argv[1], "run-upstream") == 0) return HlslPerfOriginalUpstreamMain();
    if (argc != 2 || std::strcmp(argv[1], "run-candidate") != 0) return 2;
    auto device = InitDevice();
    auto info = GetDeviceInfo(device.get());
    if (!info.SupportsWaveIntrinsics || info.SIMDWidth > 32 || info.SIMDMaxWidth < 32 || info.SupportedShaderModel < L"cs_6_6") return 3;
    std::puts("HlslPerf candidate adapter (Unmeasured): inherited upstream input/validator/batch; inclusive conversion included.");
    HlslPerfScan candidate(device, info);
    candidate.TestAll();
    candidate.BatchTimingInclusiveInitOne(1 << 28, 100);
    return 0;
}
