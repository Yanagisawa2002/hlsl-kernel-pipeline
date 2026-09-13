#pragma once
#include "ComputeKernelBase.h"
#include <memory>
#include <array>

// Local ABI binding only. Upstream ComputeKernelBase owns DXC/PSO setup; no timing or input generation here.
class HlslPerfNativeKernel : public ComputeKernelBase
{
    static std::vector<CD3DX12_ROOT_PARAMETER1> Parameters()
    {
        std::vector<CD3DX12_ROOT_PARAMETER1> p(5);
        p[0].InitAsShaderResourceView(0); p[1].InitAsShaderResourceView(1);
        p[2].InitAsUnorderedAccessView(0); p[3].InitAsUnorderedAccessView(1);
        p[4].InitAsConstants(8, 0);
        return p;
    }
    const std::vector<CD3DX12_ROOT_PARAMETER1> CreateRootParameters() override { return Parameters(); }
public:
    template<typename DeviceInfo>
    HlslPerfNativeKernel(winrt::com_ptr<ID3D12Device> device, const DeviceInfo& info,
        const std::filesystem::path& source, const wchar_t* entry, const std::vector<std::wstring>& args)
        : ComputeKernelBase(device, info, source, entry, args, Parameters()) {}

    void Dispatch(winrt::com_ptr<ID3D12GraphicsCommandList> commands,
        ID3D12Resource* input0, ID3D12Resource* input1, ID3D12Resource* output0,
        ID3D12Resource* output1, const std::array<uint32_t, 8>& constants, uint32_t groups, uint32_t groupsY = 1)
    {
        // Resources are COMMON on entry/exit; each real resource has only one access role per pass.
        ID3D12Resource* resources[] = { input0, input1, output0, output1 };
        std::vector<D3D12_RESOURCE_BARRIER> before, after;
        for (int i = 0; i < 4; ++i)
        {
            if (!resources[i]) continue;
            auto state = i < 2 ? D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE : D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
            before.push_back(CD3DX12_RESOURCE_BARRIER::Transition(resources[i], D3D12_RESOURCE_STATE_COMMON, state));
            after.push_back(CD3DX12_RESOURCE_BARRIER::Transition(resources[i], state, D3D12_RESOURCE_STATE_COMMON));
        }
        if (!before.empty()) commands->ResourceBarrier(static_cast<UINT>(before.size()), before.data());
        SetPipelineState(commands);
        auto dummy = output0->GetGPUVirtualAddress(); // Unused descriptors are never dereferenced by the compiled entry.
        commands->SetComputeRootShaderResourceView(0, input0 ? input0->GetGPUVirtualAddress() : dummy);
        commands->SetComputeRootShaderResourceView(1, input1 ? input1->GetGPUVirtualAddress() : dummy);
        commands->SetComputeRootUnorderedAccessView(2, output0->GetGPUVirtualAddress());
        commands->SetComputeRootUnorderedAccessView(3, output1 ? output1->GetGPUVirtualAddress() : dummy);
        commands->SetComputeRoot32BitConstants(4, 8, constants.data(), 0);
        commands->Dispatch(groups, groupsY, 1);
        auto barrier = CD3DX12_RESOURCE_BARRIER::UAV(nullptr);
        commands->ResourceBarrier(1, &barrier);
        if (!after.empty()) commands->ResourceBarrier(static_cast<UINT>(after.size()), after.data());
    }
};
