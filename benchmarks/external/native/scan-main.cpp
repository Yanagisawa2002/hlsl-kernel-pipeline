#include <cstring>
#include <cstdio>
// Reuse upstream device setup and keep its entry point intact under a local symbol.
#define main HlslPerfOriginalUpstreamMain
#include "GPUPrefixSumsD3D12.cpp"
#undef main
#include "HlslPerfScan.h"
#include "RuntimeSupport.h"
#include "ProfileSupport.h"

int RuntimeScan(int argc, char** argv)
{
    if (argc < 5 || argc > 7) throw std::invalid_argument("probe|validate-only|test-all|batch-only|profile-once|export-input rts|tile|tile-fused count expected-luid [exact-adapter] [input-output-file]");
    std::string command = argv[1], arm = argv[2];
    uint32_t count = static_cast<uint32_t>(std::stoul(argv[3]));
    uint64_t luid = std::stoull(argv[4]);
    if (!luid && command != "probe") throw std::invalid_argument("Non-probe operations require the observed LUID.");
    if ((command == "export-input") != (argc == 7)) throw std::invalid_argument("Only export-input requires an output filename.");
    auto device = HlslPerfRuntimeDevice(luid, argc >= 6 ? std::wstring(winrt::to_hstring(argv[5])) : L"");
    HlslPerfRuntimeBudget(device.get(), "before", count * 12ull + 1024 * 1024);
    if (command == "probe") return 0;
    if (count != (1u << 28)) throw std::invalid_argument("Use the pinned upstream 2^28 scan workload.");
    auto info = GetDeviceInfo(device.get());

    if (command == "profile-once")
    {
        HlslPerfPixRuntime pix;
        printf("HPJSON {\"kind\":\"arm\",\"backend\":\"%s\",\"count\":%u,\"mode\":\"profile-once\",\"marker\":\"HlslPerf.ScanInclusive.ProfileRange\"}\n", arm.c_str(), count);
        if (arm == "rts")
        {
            HlslPerfProfiledScan<ReduceThenScan> operation(pix, device, info);
            operation.ProfileOnceInclusiveInitOne(count);
        }
        else if (arm == "tile")
        {
            HlslPerfProfiledScan<HlslPerfScan> operation(pix, device, info, false);
            operation.ProfileOnceInclusiveInitOne(count);
        }
        else if (arm == "tile-fused")
        {
            HlslPerfProfiledScan<HlslPerfScan> operation(pix, device, info, true);
            operation.ProfileOnceInclusiveInitOne(count);
        }
        else throw std::invalid_argument("Unknown scan backend.");
        HlslPerfRuntimeBudget(device.get(), "after");
        return 0;
    }

    auto run = [&](auto& operation)
    {
        if (command == "export-input") { operation.ExportInput(count, argv[6]); return; }
        printf("HPJSON {\"kind\":\"arm\",\"backend\":\"%s\",\"count\":%u,\"batchSize\":100,\"warmupIterations\":1}\n", arm.c_str(), count);
        if (command == "test-all") operation.TestAll();
        if (command == "validate-only" || command == "test-all") operation.StrictValidate();
        operation.ValidateFullSize(count);
        HlslPerfRuntimeBudget(device.get(), "allocated");
        if (command == "batch-only") operation.BatchTimingInclusiveInitOne(count, 100);
        HlslPerfRuntimeBudget(device.get(), "after");
    };
    if (arm == "rts") { HlslPerfScanValidation<ReduceThenScan> operation(device, info); run(operation); }
    else if (arm == "tile") { HlslPerfScanValidation<HlslPerfScan> operation(device, info); run(operation); }
    else if (arm == "tile-fused") { HlslPerfScanValidation<HlslPerfScan> operation(device, info, true); run(operation); }
    else throw std::invalid_argument("Unknown scan backend.");
    return 0;
}

int main(int argc, char** argv)
{
    if (argc > 1 && (std::strcmp(argv[1], "probe") == 0 || std::strcmp(argv[1], "validate-only") == 0 || std::strcmp(argv[1], "batch-only") == 0 || std::strcmp(argv[1], "profile-once") == 0 || std::strcmp(argv[1], "test-all") == 0 || std::strcmp(argv[1], "export-input") == 0))
    {
        try { return RuntimeScan(argc, argv); }
        catch (const winrt::hresult_error& error) { printf("RUNTIME_FAILED HRESULT %08x\n", uint32_t(error.code().value)); return 5; }
        catch (const std::exception& error) { printf("RUNTIME_FAILED %s\n", error.what()); return 5; }
    }
    if (argc == 1 || (argc == 2 && std::strcmp(argv[1], "--help") == 0))
    {
        std::puts("Usage: scan.exe run-upstream|run-candidate or profile-once rts|tile|tile-fused count expected-luid [exact-adapter]");
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
