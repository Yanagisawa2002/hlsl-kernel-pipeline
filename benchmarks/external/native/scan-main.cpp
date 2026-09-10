#include <cstring>
#include <cstdio>
// Reuse upstream device setup and keep its entry point intact under a local symbol.
#define main HlslPerfOriginalUpstreamMain
#include "GPUPrefixSumsD3D12.cpp"
#undef main
#include "HlslPerfScan.h"
#include "RuntimeSupport.h"

int RuntimeScan(int argc, char** argv)
{
    if (argc != 5) throw std::invalid_argument("probe|validate-only|batch-only rts|tile count expected-luid");
    std::string command = argv[1], arm = argv[2];
    uint32_t count = static_cast<uint32_t>(std::stoul(argv[3]));
    auto device = HlslPerfRuntimeDevice(std::stoull(argv[4]));
    HlslPerfRuntimeBudget(device.get(), "before", count * 12ull + 1024 * 1024);
    if (command == "probe") return 0;
    if (count != (1u << 28)) throw std::invalid_argument("Use the pinned upstream 2^28 scan workload.");
    auto info = GetDeviceInfo(device.get());
    auto run = [&](auto& operation)
    {
        printf("HPJSON {\"kind\":\"arm\",\"backend\":\"%s\",\"count\":%u,\"batchSize\":100,\"warmupIterations\":1}\n", arm.c_str(), count);
        if (command == "validate-only") operation.StrictValidate();
        operation.ValidateFullSize(count);
        HlslPerfRuntimeBudget(device.get(), "allocated");
        if (command == "batch-only") operation.BatchTimingInclusiveInitOne(count, 100);
        HlslPerfRuntimeBudget(device.get(), "after");
    };
    if (arm == "rts") { HlslPerfScanValidation<ReduceThenScan> operation(device, info); run(operation); }
    else if (arm == "tile") { HlslPerfScanValidation<HlslPerfScan> operation(device, info); run(operation); }
    else throw std::invalid_argument("Unknown scan backend.");
    return 0;
}

int main(int argc, char** argv)
{
    if (argc > 1 && (std::strcmp(argv[1], "probe") == 0 || std::strcmp(argv[1], "validate-only") == 0 || std::strcmp(argv[1], "batch-only") == 0))
    {
        try { return RuntimeScan(argc, argv); }
        catch (const winrt::hresult_error& error) { printf("RUNTIME_FAILED HRESULT %08x\n", uint32_t(error.code().value)); return 5; }
        catch (const std::exception& error) { printf("RUNTIME_FAILED %s\n", error.what()); return 5; }
    }
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
