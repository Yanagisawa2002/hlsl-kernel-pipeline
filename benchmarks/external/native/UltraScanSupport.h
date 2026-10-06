#pragma once
#include <cmath>
#include <utility>

// A separate, bounded experiment entry point. The frozen 2^28 protocol and all
// vendored generators, validators, kernels and TimeScan remain unchanged.
inline void HlslPerfUltraHostBudget(const char* phase, uint64_t extra = 0)
{
    MEMORYSTATUSEX memory{};
    memory.dwLength = sizeof(memory);
    if (!GlobalMemoryStatusEx(&memory)) throw std::runtime_error("Host memory query failed.");
    constexpr uint64_t reserve = 4ull << 30;
    printf("HPJSON {\"kind\":\"hostMemory\",\"phase\":\"%s\",\"availablePhysical\":%llu,\"availableCommit\":%llu,\"requiredExtra\":%llu,\"reserve\":%llu}\n",
        phase, memory.ullAvailPhys, memory.ullAvailPageFile, extra, reserve);
    if (memory.ullAvailPhys < reserve + extra || memory.ullAvailPageFile < reserve + extra)
        throw std::runtime_error("Host memory reserve would fall below 4 GiB; stop without allocation.");
}

template<class Base> class HlslPerfUltraScan : public HlslPerfScanValidation<Base>
{
public:
    template<class... Args> explicit HlslPerfUltraScan(Args&&... args)
        : HlslPerfScanValidation<Base>(std::forward<Args>(args)...)
    {
        // Upstream's initial size is not initialized in its base constructor.
        // Normalize the local host wrapper before its first UpdateSize call.
        this->m_alignedSize = 0;
    }
    void Run(uint32_t n, bool exclusive, bool gate)
    {
        const auto type = exclusive ? Base::ValidationType::ONE_EXCLUSIVE : Base::ValidationType::ONE_INCLUSIVE;
        const char* operation = exclusive ? "exclusive" : "inclusive";
        if (gate) this->StrictValidate();
        HlslPerfUltraHostBudget("before-allocation", uint64_t(n) * 12 + (64ull << 20));
        if (!this->ValidateScan(n, 0, type)) throw std::runtime_error("Ultra full-size validation failed before timing.");
        printf("HPJSON {\"kind\":\"ultraValidation\",\"phase\":\"before\",\"count\":%u,\"operation\":\"%s\",\"passed\":true}\n", n, operation);
        HlslPerfRuntimeBudget(this->m_device.get(), "allocated");
        HlslPerfUltraHostBudget("allocated");
        if (gate) return;
        constexpr uint32_t warmup = 8, measured = 100;
        printf("HPJSON {\"kind\":\"ultraBatch\",\"count\":%u,\"operation\":\"%s\",\"warmup\":%u,\"measured\":%u,\"input\":\"upstream-init-one\"}\n", n, operation, warmup, measured);
        double total = 0;
        for (uint32_t i = 0; i < warmup + measured; ++i)
        {
            if (!(i % 16)) HlslPerfUltraHostBudget("running");
            const double seconds = this->TimeScan(0, type);
            if (!std::isfinite(seconds) || seconds <= 0 || seconds > 0.100)
                throw std::runtime_error("Invalid or >100 ms single-operation GPU time; stop the campaign.");
            if (i >= warmup)
            {
                total += seconds;
                printf("HPJSON {\"kind\":\"ultraSample\",\"index\":%u,\"milliseconds\":%.12f}\n", i - warmup, seconds * 1000);
            }
        }
        // Check the actual output left by the final timed scan, without rerunning
        // the algorithm or downloading a multi-GiB array to host RAM.
        if (!this->ValidateOutput(false, type)) throw std::runtime_error("Final timed output failed full-size validation.");
        printf("HPJSON {\"kind\":\"ultraValidation\",\"phase\":\"after\",\"count\":%u,\"operation\":\"%s\",\"passed\":true}\n", n, operation);
        printf("HPJSON {\"kind\":\"ultraTotal\",\"count\":%u,\"operation\":\"%s\",\"measured\":%u,\"meanMs\":%.12f}\n", n, operation, measured, total * 1000 / measured);
        HlslPerfRuntimeBudget(this->m_device.get(), "after");
        HlslPerfUltraHostBudget("after");
    }
};

inline int RuntimeUltraScan(int argc, char** argv)
{
    if (argc != 7) throw std::invalid_argument("ultra-gate|ultra-batch rts|tile-fused count expected-luid exact-adapter inclusive|exclusive");
    const std::string command = argv[1], arm = argv[2], semantics = argv[6];
    const uint64_t parsed = std::stoull(argv[3]);
    // Hard limit: do not push beyond the workload already validated on this GPU.
    if (parsed < (1ull << 26) || parsed > (1ull << 28) || parsed % 4)
        throw std::invalid_argument("Ultra count must be 4-aligned and within 2^26..2^28.");
    if (semantics != "inclusive" && semantics != "exclusive") throw std::invalid_argument("Unknown scan semantics.");
    if (arm != "rts" && arm != "tile-fused") throw std::invalid_argument("Unknown ultra backend.");
    const uint64_t luid = std::stoull(argv[4]);
    if (!luid) throw std::invalid_argument("Ultra operations require an observed nonzero LUID.");
    const uint32_t count = static_cast<uint32_t>(parsed);
    HlslPerfUltraHostBudget("before-device", parsed * 12 + (64ull << 20));
    auto device = HlslPerfRuntimeDevice(luid, std::wstring(winrt::to_hstring(argv[5])));
    HlslPerfRuntimeBudget(device.get(), "before", parsed * 12 + (64ull << 20));
    const auto info = GetDeviceInfo(device.get());
    printf("HPJSON {\"kind\":\"ultraArm\",\"backend\":\"%s\",\"count\":%u,\"operation\":\"%s\"}\n", arm.c_str(), count, semantics.c_str());
    if (arm == "rts")
    {
        HlslPerfUltraScan<ReduceThenScan> operation(device, info);
        operation.Run(count, semantics == "exclusive", command == "ultra-gate");
    }
    else
    {
        HlslPerfUltraScan<HlslPerfScan> operation(device, info, true);
        operation.Run(count, semantics == "exclusive", command == "ultra-gate");
    }
    return 0;
}
