#include <cstring>
#include <cstdio>
#define main HlslPerfOriginalUpstreamMain
#include "GPUSortingD3D12.cpp"
#undef main
#include "HlslPerfSort.h"
#include "RuntimeSupport.h"

int RuntimeSort(int argc, char** argv)
{
    if (argc != 5) throw std::invalid_argument("probe|validate-only|batch-only device|onesweep|ffx|tile4|tile8 count expected-luid");
    std::string command = argv[1], arm = argv[2];
    uint32_t count = static_cast<uint32_t>(std::stoul(argv[3]));
    auto device = HlslPerfRuntimeDevice(std::stoull(argv[4]));
    HlslPerfRuntimeBudget(device.get(), "before", count * 40ull + 1024ull * 1024 * 1024 * 2);
    if (command == "probe") return 0;
    if (count != (1u << 28) && count != (1u << 25)) throw std::invalid_argument("Use a pinned upstream 2^28 or 2^25 sort workload.");
    auto info = GetDeviceInfo(device.get());
    auto run = [&](auto& operation)
    {
        printf("HPJSON {\"kind\":\"arm\",\"backend\":\"%s\",\"count\":%u,\"batchSize\":100,\"seed\":10,\"entropyPreset\":0,\"warmupIterations\":1}\n", arm.c_str(), count);
        if (command == "validate-only") operation.StrictValidate();
        operation.ValidateFullSize(count);
        HlslPerfRuntimeBudget(device.get(), "allocated");
        if (command == "batch-only") operation.BatchTiming(count, 100, 10, GPUSorting::ENTROPY_PRESET_1);
        HlslPerfRuntimeBudget(device.get(), "after");
    };
    if (arm == "tile4" || arm == "tile8") { HlslPerfSortValidation<HlslPerfSort> operation(device, info, arm == "tile4" ? 4 : 8); run(operation); }
    else if (arm == "device") { HlslPerfSortValidation<DeviceRadixSort> operation(device, info, GPUSorting::ORDER_ASCENDING, GPUSorting::KEY_UINT32, GPUSorting::PAYLOAD_UINT32); run(operation); }
    else if (arm == "onesweep") { HlslPerfSortValidation<OneSweep> operation(device, info, GPUSorting::ORDER_ASCENDING, GPUSorting::KEY_UINT32, GPUSorting::PAYLOAD_UINT32); run(operation); }
    else if (arm == "ffx") { HlslPerfSortValidation<FFXParallelSort> operation(device, info, GPUSorting::ORDER_ASCENDING, GPUSorting::KEY_UINT32, GPUSorting::PAYLOAD_UINT32); run(operation); }
    else throw std::invalid_argument("Unknown sort backend.");
    return 0;
}

int main(int argc, char** argv)
{
    if (argc > 1 && (std::strcmp(argv[1], "probe") == 0 || std::strcmp(argv[1], "validate-only") == 0 || std::strcmp(argv[1], "batch-only") == 0))
    {
        try { return RuntimeSort(argc, argv); }
        catch (const winrt::hresult_error& error) { printf("RUNTIME_FAILED HRESULT %08x\n", uint32_t(error.code().value)); return 5; }
        catch (const std::exception& error) { printf("RUNTIME_FAILED %s\n", error.what()); return 5; }
    }
    if (argc == 1 || (argc == 2 && std::strcmp(argv[1], "--help") == 0))
    {
        std::puts("Usage: sort.exe run-upstream|run-candidate (executes native GPU tests and benchmark)");
        return 0;
    }
    if (argc == 2 && std::strcmp(argv[1], "run-upstream") == 0) return HlslPerfOriginalUpstreamMain();
    if (argc != 2 || std::strcmp(argv[1], "run-candidate") != 0) return 2;
    auto device = InitDevice();
    auto info = GetDeviceInfo(device.get());
    if (!info.SupportsWaveIntrinsics || info.SIMDWidth > 32 || info.SIMDMaxWidth < 32 || info.SupportedShaderModel < L"cs_6_6") return 3;
    std::puts("HlslPerf candidate adapter (Unmeasured): inherited upstream input/validator/batch; SoA conversion included.");
    HlslPerfSort candidate(device, info);
    if (!candidate.TestAll()) return 4;
    candidate.BatchTiming(1 << 28, 100, 10, GPUSorting::ENTROPY_PRESET_1);
    return 0;
}
