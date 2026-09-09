#pragma once
#include <dxgi1_6.h>
#include <functional>
#include <numeric>
#include <iomanip>

inline winrt::com_ptr<ID3D12Device> HlslPerfRuntimeDevice(uint64_t expectedLuid)
{
    winrt::com_ptr<IDXGIFactory6> factory;
    winrt::check_hresult(CreateDXGIFactory2(0, IID_PPV_ARGS(factory.put())));
    for (UINT index = 0; ; ++index)
    {
        winrt::com_ptr<IDXGIAdapter1> adapter;
        if (factory->EnumAdapters1(index, adapter.put()) == DXGI_ERROR_NOT_FOUND) break;
        DXGI_ADAPTER_DESC1 desc{};
        winrt::check_hresult(adapter->GetDesc1(&desc));
        uint64_t luid = (uint64_t(uint32_t(desc.AdapterLuid.HighPart)) << 32) | desc.AdapterLuid.LowPart;
        if (std::wstring(desc.Description).find(L"R9700") == std::wstring::npos || luid != expectedLuid) continue;
        LARGE_INTEGER driver{};
        winrt::check_hresult(adapter->CheckInterfaceSupport(__uuidof(IDXGIDevice), &driver));
        winrt::com_ptr<ID3D12Device> device;
        winrt::check_hresult(D3D12CreateDevice(adapter.get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(device.put())));
        D3D12_FEATURE_DATA_D3D12_OPTIONS1 wave{};
        winrt::check_hresult(device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS1, &wave, sizeof(wave)));
        D3D12_FEATURE_DATA_SHADER_MODEL model{D3D_SHADER_MODEL_6_7};
        winrt::check_hresult(device->CheckFeatureSupport(D3D12_FEATURE_SHADER_MODEL, &model, sizeof(model)));
        D3D_FEATURE_LEVEL requested[] = { D3D_FEATURE_LEVEL_12_2, D3D_FEATURE_LEVEL_12_1, D3D_FEATURE_LEVEL_12_0 };
        D3D12_FEATURE_DATA_FEATURE_LEVELS feature{3, requested, D3D_FEATURE_LEVEL_12_0};
        winrt::check_hresult(device->CheckFeatureSupport(D3D12_FEATURE_FEATURE_LEVELS, &feature, sizeof(feature)));
        if (!wave.WaveOps || wave.WaveLaneCountMin > 32 || wave.WaveLaneCountMax < 32 || model.HighestShaderModel < D3D_SHADER_MODEL_6_6)
            throw std::runtime_error("R9700 feature/wave contract unsupported.");
        printf("HPJSON {\"kind\":\"device\",\"adapter\":\"AMD Radeon AI PRO R9700\",\"luid\":%llu,\"vendorId\":%u,\"deviceId\":%u,\"driver\":\"%u.%u.%u.%u\",\"dedicatedBytes\":%llu,\"waveMin\":%u,\"waveMax\":%u,\"shaderModel\":%u,\"featureLevel\":%u}\n",
            luid, desc.VendorId, desc.DeviceId, HIWORD(driver.HighPart), LOWORD(driver.HighPart), HIWORD(driver.LowPart), LOWORD(driver.LowPart),
            uint64_t(desc.DedicatedVideoMemory), wave.WaveLaneCountMin, wave.WaveLaneCountMax, model.HighestShaderModel, feature.MaxSupportedFeatureLevel);
        return device;
    }
    throw std::runtime_error("The exact R9700 LUID was not found; default/integrated GPU fallback is forbidden.");
}

inline void HlslPerfRuntimeBudget(ID3D12Device* device, const char* phase, uint64_t required = 0)
{
    winrt::com_ptr<IDXGIFactory4> factory;
    winrt::check_hresult(CreateDXGIFactory2(0, IID_PPV_ARGS(factory.put())));
    winrt::com_ptr<IDXGIAdapter3> adapter;
    winrt::check_hresult(factory->EnumAdapterByLuid(device->GetAdapterLuid(), IID_PPV_ARGS(adapter.put())));
    DXGI_QUERY_VIDEO_MEMORY_INFO memory{};
    winrt::check_hresult(adapter->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &memory));
    printf("HPJSON {\"kind\":\"memory\",\"phase\":\"%s\",\"budget\":%llu,\"usage\":%llu,\"requiredBytes\":%llu}\n", phase, memory.Budget, memory.CurrentUsage, required);
    if (required && (memory.CurrentUsage >= memory.Budget || required > (memory.Budget - memory.CurrentUsage) * 3 / 4))
        throw std::runtime_error("Declared buffers exceed 75% of available DXGI local budget.");
}

inline uint32_t HlslPerfNext(uint32_t& s) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }
inline std::vector<uint32_t> HlslPerfInput(uint32_t count, uint32_t pattern)
{
    uint32_t s = 0x917923;
    std::vector<uint32_t> values(count);
    const uint32_t extremes[] = {0, 0xffffffffu, 0x80000000u, 1};
    for (uint32_t i = 0; i < count; ++i)
        values[i] = pattern == 0 ? HlslPerfNext(s) : pattern == 1 ? 0xffffffffu : pattern == 2 ? extremes[i % 4] : (HlslPerfNext(s) % 7) * 0x24924924u;
    return values;
}
inline void HlslPerfUpload(winrt::com_ptr<ID3D12Device> device, winrt::com_ptr<ID3D12GraphicsCommandList> commands,
    ID3D12Resource* destination, const std::vector<uint32_t>& values, const std::function<void()>& execute)
{
    uint32_t bytes = static_cast<uint32_t>(values.size() * 4);
    if (destination->GetDesc().Width < bytes) throw std::runtime_error("Validation upload exceeds resource.");
    auto upload = CreateBuffer(device, bytes, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ, D3D12_RESOURCE_FLAG_NONE);
    void* mapped;
    winrt::check_hresult(upload->Map(0, nullptr, &mapped));
    memcpy(mapped, values.data(), bytes); upload->Unmap(0, nullptr);
    auto before = CD3DX12_RESOURCE_BARRIER::Transition(destination, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST);
    auto after = CD3DX12_RESOURCE_BARRIER::Transition(destination, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COMMON);
    commands->ResourceBarrier(1, &before); commands->CopyBufferRegion(destination, 0, upload.get(), 0, bytes); commands->ResourceBarrier(1, &after);
    execute();
}
inline std::vector<uint32_t> HlslPerfDownload(winrt::com_ptr<ID3D12Device> device, winrt::com_ptr<ID3D12GraphicsCommandList> commands,
    ID3D12Resource* source, uint32_t count, const std::function<void()>& execute)
{
    auto readback = CreateBuffer(device, count * 4, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_FLAG_NONE);
    auto before = CD3DX12_RESOURCE_BARRIER::Transition(source, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE);
    auto after = CD3DX12_RESOURCE_BARRIER::Transition(source, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COMMON);
    commands->ResourceBarrier(1, &before); commands->CopyBufferRegion(readback.get(), 0, source, 0, count * 4); commands->ResourceBarrier(1, &after);
    execute();
    return ReadBackBuffer(readback, count);
}

template<class Base> class HlslPerfScanValidation : public Base
{
public:
    using Base::Base;
    void StrictValidate()
    {
        for (uint32_t n : {4u, 32u, 128u, 256u, 4092u, 4096u, 4100u, 8196u, 1048580u})
        for (uint32_t pattern = 0; pattern < 3; ++pattern)
        {
            this->UpdateSize(n, Base::ValidationType::ONE_INCLUSIVE);
            auto input = HlslPerfInput(n, pattern);
            auto execute = [this]() { this->ExecuteCommandList(); };
            for (uint32_t repeat = 0; repeat < 4; ++repeat)
            {
                HlslPerfUpload(this->m_device, this->m_cmdList, this->m_scanInBuffer.get(), input, execute);
                HlslPerfUpload(this->m_device, this->m_cmdList, this->m_scanOutBuffer.get(), std::vector<uint32_t>(n, repeat & 1 ? 0x5a5a5a5a : 0xa5a5a5a5), execute);
                if (repeat & 1) this->PrepareScanCmdListExclusive(); else this->PrepareScanCmdListInclusive();
                this->ExecuteCommandList();
                auto actual = HlslPerfDownload(this->m_device, this->m_cmdList, this->m_scanOutBuffer.get(), n, execute);
                uint32_t sum = 0;
                for (uint32_t i = 0; i < n; ++i)
                {
                    uint32_t expected = (repeat & 1) ? sum : sum + input[i];
                    if (actual[i] != expected) throw std::runtime_error("Native full32 scan mismatch at " + std::to_string(i));
                    sum += input[i];
                }
            }
            printf("HPJSON {\"kind\":\"correctness\",\"operation\":\"scan\",\"count\":%u,\"pattern\":%u,\"passed\":true,\"repeats\":4}\n", n, pattern);
        }
    }
    void ValidateFullSize(uint32_t n)
    {
        if (!this->ValidateScan(n, 0, Base::ValidationType::ONE_INCLUSIVE)) throw std::runtime_error("Native large scan validation failed.");
        printf("HPJSON {\"kind\":\"fullSizeValidation\",\"count\":%u,\"passed\":true}\n", n);
    }
};

template<class Base> class HlslPerfSortValidation : public Base
{
public:
    using Base::Base;
    void StrictValidate()
    {
        for (uint32_t n : {1u, 31u, 32u, 33u, 127u, 128u, 129u, 511u, 512u, 513u, 4097u, 262145u, 1048583u})
        for (uint32_t pattern = 0; pattern < 4; ++pattern)
        {
            this->UpdateSize(n);
            auto keys = HlslPerfInput(n, pattern);
            uint32_t state = 0x781239;
            std::vector<uint32_t> payloads(n), order(n);
            for (auto& value : payloads) value = HlslPerfNext(state);
            std::iota(order.begin(), order.end(), 0);
            std::stable_sort(order.begin(), order.end(), [&keys](uint32_t a, uint32_t b) { return keys[a] < keys[b]; });
            auto execute = [this]() { this->ExecuteCommandList(); };
            for (uint32_t repeat = 0; repeat < 3; ++repeat)
            {
                HlslPerfUpload(this->m_device, this->m_cmdList, this->m_sortBuffer.get(), keys, execute);
                HlslPerfUpload(this->m_device, this->m_cmdList, this->m_sortPayloadBuffer.get(), payloads, execute);
                HlslPerfUpload(this->m_device, this->m_cmdList, this->m_altBuffer.get(), std::vector<uint32_t>(n, 0xa5a5a5a5u ^ repeat), execute);
                HlslPerfUpload(this->m_device, this->m_cmdList, this->m_altPayloadBuffer.get(), std::vector<uint32_t>(n, 0x5a5a5a5au ^ repeat), execute);
                this->PrepareSortCmdList(); this->ExecuteCommandList();
                auto actual = HlslPerfDownload(this->m_device, this->m_cmdList, this->m_sortBuffer.get(), n, execute);
                auto values = HlslPerfDownload(this->m_device, this->m_cmdList, this->m_sortPayloadBuffer.get(), n, execute);
                for (uint32_t i = 0; i < n; ++i)
                    if (actual[i] != keys[order[i]] || values[i] != payloads[order[i]])
                        throw std::runtime_error("Native stable key/payload mismatch at " + std::to_string(i) + " count " + std::to_string(n));
            }
            printf("HPJSON {\"kind\":\"correctness\",\"operation\":\"stable-sort\",\"count\":%u,\"pattern\":%u,\"passed\":true,\"repeats\":3}\n", n, pattern);
        }
    }
    void ValidateFullSize(uint32_t n)
    {
        if (!this->ValidateSort(n, 10)) throw std::runtime_error("Native large sort validation failed.");
        printf("HPJSON {\"kind\":\"fullSizeValidation\",\"count\":%u,\"passed\":true}\n", n);
    }
};
