#pragma once
#include "DeviceRadixSort.h"
#include "NativeKernel.h"
#include <limits>

// Reuses the native input, keys/payload validator and batch methods; changes only the candidate operation.
// Pairs, ascending full32, four-bit tiled radix. No tuning is performed by this adapter.
class HlslPerfSort : public DeviceRadixSort
{
    uint32_t radixBits;
    std::unique_ptr<HlslPerfNativeKernel> interleave, histogram, prefixTiles, prefixBins, scatter, scatterPairs;
    winrt::com_ptr<ID3D12Resource> aosA, aosB, hist, prefix, sums;
public:
    HlslPerfSort(winrt::com_ptr<ID3D12Device> device, GPUSorting::DeviceInfo info, uint32_t bits = 4)
        : DeviceRadixSort(device, info, GPUSorting::ORDER_ASCENDING, GPUSorting::KEY_UINT32, GPUSorting::PAYLOAD_UINT32), radixBits(bits)
    {
        if (bits != 4 && bits != 8) throw std::invalid_argument("Native tiled radix requires four or eight bits.");
        std::filesystem::path root = HLSLPERF_REPOSITORY;
        info.SupportedShaderModel = L"cs_6_6";
        std::vector<std::wstring> args = {L"-HV", L"2018", L"-Ges", L"-O3",
            L"-D", L"HLSLPERF_RADIX_TILE=1", L"-D", L"HLSLPERF_RADIX_BITS=" + std::to_wstring(bits), L"-D", L"HLSLPERF_RADIX_PAIRS=1",
            L"-D", L"HLSLPERF_GROUP_SIZE=128", L"-D", L"HLSLPERF_ELEMENTS_PER_THREAD=4", L"-D", L"HLSLPERF_SCAN_BACKEND=2",
            L"-D", L"HLSLPERF_SCAN_OPERATOR=1", L"-D", L"HLSLPERF_VECTOR_WIDTH=1", L"-D", L"HLSLPERF_WAVE_SIZE=32"};
        auto source = root / "kernels/radix-sort.hlsl";
        interleave = std::make_unique<HlslPerfNativeKernel>(device, info, root / "benchmarks/external/native/SortInput.hlsl", L"Interleave", std::vector<std::wstring>{L"-HV", L"2018", L"-O3"});
        histogram = std::make_unique<HlslPerfNativeKernel>(device, info, source, L"BuildRadixHistogram", args);
        prefixTiles = std::make_unique<HlslPerfNativeKernel>(device, info, source, L"PrefixRadixHistogramTiles", args);
        prefixBins = std::make_unique<HlslPerfNativeKernel>(device, info, source, L"PrefixRadixHistogramBins", args);
        scatter = std::make_unique<HlslPerfNativeKernel>(device, info, source, L"ScatterRadixTile", args);
        scatterPairs = std::make_unique<HlslPerfNativeKernel>(device, info, source, L"ScatterRadixTileToPairs", args);
    }
protected:
    void InitBuffers(uint32_t count, uint32_t upstreamPartitions) override
    {
        if (count == 0 || static_cast<uint64_t>(count) * 8 > std::numeric_limits<uint32_t>::max())
            throw std::invalid_argument("Native pair buffers require a positive uint-byte-addressable size.");
        DeviceRadixSort::InitBuffers(count, upstreamPartitions);
        uint64_t tiles = (static_cast<uint64_t>(count) + 511) / 512, chunks = (tiles + 511) / 512;
        auto buffer = [this](uint64_t bytes) {
            if (bytes == 0 || bytes > std::numeric_limits<uint32_t>::max())
                throw std::overflow_error("Native scratch exceeds the upstream buffer ABI.");
            return CreateBuffer(m_device, static_cast<uint32_t>(bytes), D3D12_HEAP_TYPE_DEFAULT,
                D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
        };
        aosA = buffer(static_cast<uint64_t>(count) * 8); aosB = buffer(static_cast<uint64_t>(count) * 8);
        uint64_t bins = 1ull << radixBits;
        hist = buffer(bins * tiles * 4); sums = buffer(bins * chunks * 4); prefix = buffer((bins * tiles + bins * chunks + bins) * 4);
    }
    void DisposeBuffers() override
    {
        DeviceRadixSort::DisposeBuffers();
        aosA = nullptr; aosB = nullptr; hist = nullptr; prefix = nullptr; sums = nullptr;
    }
    void PrepareSortCmdList() override
    {
        uint32_t tiles = (m_numKeys + 511) / 512, chunks = (tiles + 511) / 512;
        interleave->Dispatch(m_cmdList, m_sortBuffer.get(), m_sortPayloadBuffer.get(), aosA.get(), nullptr, {m_numKeys}, 256);
        uint32_t bins = 1u << radixBits, digits = 32 / radixBits;
        for (uint32_t digit = 0; digit < digits; ++digit)
        {
            auto source = digit % 2 == 0 ? aosA.get() : aosB.get();
            auto destination = digit % 2 == 0 ? aosB.get() : aosA.get();
            auto dispatch = [&](HlslPerfNativeKernel* kernel, ID3D12Resource* in0, ID3D12Resource* in1,
                ID3D12Resource* out0, ID3D12Resource* out1, uint32_t groups)
            {
                uint32_t x = std::min(groups, 65535u), y = (groups + x - 1) / x;
                kernel->Dispatch(m_cmdList, in0, in1, out0, out1,
                    {m_numKeys, 512, digit * radixBits, bins - 1, chunks, tiles, x, groups}, x, y);
            };
            dispatch(histogram.get(), source, nullptr, hist.get(), nullptr, tiles);
            dispatch(prefixTiles.get(), hist.get(), nullptr, prefix.get(), sums.get(), bins * chunks);
            dispatch(prefixBins.get(), sums.get(), nullptr, prefix.get(), nullptr, 1);
            if (digit == digits - 1) dispatch(scatterPairs.get(), source, prefix.get(), m_sortBuffer.get(), m_sortPayloadBuffer.get(), tiles);
            else dispatch(scatter.get(), source, prefix.get(), destination, nullptr, tiles);
        }
    }
};
