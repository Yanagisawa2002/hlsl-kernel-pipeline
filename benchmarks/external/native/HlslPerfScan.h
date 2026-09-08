#pragma once
#include "ReduceThenScan.h"
#include "NativeKernel.h"

// Inherits the pinned native input generator, validators, TestAll and inclusive BatchTiming method.
// Local changes are confined to command recording and the extra full-width look-back state.
class HlslPerfScan : public ReduceThenScan
{
    std::unique_ptr<HlslPerfNativeKernel> reset, scan, inclusive;
    winrt::com_ptr<ID3D12Resource> state;
    uint32_t statePartitions = 0;
public:
    HlslPerfScan(winrt::com_ptr<ID3D12Device> device, GPUPrefixSums::DeviceInfo info)
        : ReduceThenScan(device, info)
    {
        m_alignedSize = 0;
        std::filesystem::path root = HLSLPERF_REPOSITORY;
        std::vector<std::wstring> args = {L"-HV", L"2018", L"-Ges", L"-O3",
            L"-D", L"HLSLPERF_SCAN_WAVE_TILED=1", L"-D", L"HLSLPERF_SCAN_BACKEND=3",
            L"-D", L"HLSLPERF_SCAN_OPERATOR=1", L"-D", L"HLSLPERF_GROUP_SIZE=256",
            L"-D", L"HLSLPERF_ELEMENTS_PER_THREAD=4", L"-D", L"HLSLPERF_SINGLE_PASS_ITEMS_SCALE=4",
            L"-D", L"HLSLPERF_VECTOR_WIDTH=4", L"-D", L"HLSLPERF_WAVE_SIZE=32",
            L"-D", L"HLSLPERF_WAVE_TILED_MAX_POLLS=4"};
        info.SupportedShaderModel = L"cs_6_6";
        reset = std::make_unique<HlslPerfNativeKernel>(device, info, root / "kernels/scan.hlsl", L"ResetWaveTiledState", args);
        scan = std::make_unique<HlslPerfNativeKernel>(device, info, root / "kernels/scan.hlsl", L"SinglePassScanWaveTiled", args);
        inclusive = std::make_unique<HlslPerfNativeKernel>(device, info, root / "benchmarks/external/native/ScanInclusive.hlsl", L"AddInput", std::vector<std::wstring>{L"-HV", L"2018", L"-O3"});
    }
protected:
    void PrepareScanCmdListExclusive() override
    {
        uint32_t partitions = (m_alignedSize + 4095) / 4096;
        if (!state || statePartitions != partitions)
        {
            state = CreateBuffer(m_device, 8ull + 12ull * partitions, D3D12_HEAP_TYPE_DEFAULT,
                D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
            statePartitions = partitions;
        }
        std::array<uint32_t, 8> constants = { m_alignedSize, 4096, partitions };
        reset->Dispatch(m_cmdList, nullptr, nullptr, state.get(), nullptr, constants, 1);
        scan->Dispatch(m_cmdList, m_scanInBuffer.get(), nullptr, m_scanOutBuffer.get(), state.get(),
            constants, std::min(partitions, 256u));
    }
    void PrepareScanCmdListInclusive() override
    {
        PrepareScanCmdListExclusive();
        inclusive->Dispatch(m_cmdList, m_scanInBuffer.get(), nullptr, m_scanOutBuffer.get(), nullptr,
            {m_alignedSize}, 256);
    }
};
