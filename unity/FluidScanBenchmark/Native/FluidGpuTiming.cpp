// SPDX-License-Identifier: MIT
// GPU timestamps inserted into Unity's own D3D12 command list, without queue submission or flushing.
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <array>
#include <mutex>
#include <cstdint>
#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"
using Microsoft::WRL::ComPtr;
namespace {
constexpr unsigned Slots = 128, Metrics = 5, MaxCalls = 32, Queries = 258;
constexpr unsigned Offsets[Metrics] = {0, 64, 128, 256, 192};
struct Slot { uint64_t frame = 0, fence = 0; unsigned begun[Metrics]{}, ended[Metrics]{}; };
std::array<Slot, Slots> slots;
std::mutex mutex;
IUnityGraphics* graphics = nullptr;
IUnityGraphicsD3D12v7* api = nullptr;
ID3D12Fence* frameFence = nullptr;
ComPtr<ID3D12QueryHeap> heap;
ComPtr<ID3D12Resource> readback;
uint64_t* mapped = nullptr;
uint64_t frequency = 0;
int baseEvent = 0, errorCode = 0;
unsigned calls = 3, metricMask = 31;
unsigned Expected(unsigned metric) { return (metricMask & (1u << metric)) ? (metric == 3 ? 1u : calls) : 0u; }

void Initialize() {
    if (!api || heap) return;
    ID3D12Device* device = api->GetDevice();
    if (!device || FAILED(api->GetCommandQueue()->GetTimestampFrequency(&frequency)) || !frequency) { errorCode = 1; return; }
    D3D12_QUERY_HEAP_DESC hd{}; hd.Type = D3D12_QUERY_HEAP_TYPE_TIMESTAMP; hd.Count = Slots * Queries;
    if (FAILED(device->CreateQueryHeap(&hd, IID_PPV_ARGS(&heap)))) { errorCode = 2; return; }
    D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_READBACK;
    D3D12_RESOURCE_DESC rd{}; rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    rd.Width = uint64_t(Slots) * Queries * sizeof(uint64_t); rd.Height = 1; rd.DepthOrArraySize = 1;
    rd.MipLevels = 1; rd.SampleDesc.Count = 1; rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    if (FAILED(device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_COPY_DEST,
        nullptr, IID_PPV_ARGS(&readback)))) { errorCode = 3; return; }
    D3D12_RANGE range{0, size_t(rd.Width)};
    if (FAILED(readback->Map(0, &range, reinterpret_cast<void**>(&mapped)))) { errorCode = 4; return; }
    frameFence = api->GetFrameFence();
    if (!frameFence) errorCode = 14;
}

void UNITY_INTERFACE_API Event(int event, void* data) {
    std::lock_guard<std::mutex> lock(mutex);
    event -= baseEvent;
    if (event == 10) { Initialize(); return; }
    if (!api || event < 0 || event >= 10) { errorCode = 5; return; }
    // Initialization runs on the submission thread; an early warmup frame can precede it.
    if (!mapped) return;
    UnityGraphicsD3D12RecordingState state{};
    if (!api->CommandRecordingState(&state) || !state.commandList) { errorCode = 6; return; }
    uint64_t frame = reinterpret_cast<uintptr_t>(data);
    auto& slot = slots[frame % Slots];
    unsigned metric = unsigned(event / 2), end = unsigned(event & 1);
    if (metric == 3 && !end) {
        if (slot.fence && frameFence->GetCompletedValue() < slot.fence) { errorCode = 7; return; }
        slot = Slot{}; slot.frame = frame;
    }
    if (slot.frame != frame) { errorCode = 8; return; }
    unsigned occurrence = end ? slot.ended[metric]++ : slot.begun[metric]++;
    if (occurrence >= Expected(metric) || (end && slot.ended[metric] > slot.begun[metric])) { errorCode = 9; return; }
    unsigned query = unsigned(frame % Slots) * Queries + Offsets[metric] + occurrence * 2 + end;
    state.commandList->EndQuery(heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP, query);
    if (metric == 3 && end) {
        for (unsigned m = 0; m < Metrics; ++m)
            if (slot.begun[m] != Expected(m) || slot.ended[m] != slot.begun[m]) { errorCode = 10; return; }
        unsigned start = unsigned(frame % Slots) * Queries;
        // Resolve only initialized queries; disabled metrics and unused batch capacity are not queried.
        for (unsigned m = 0; m < Metrics; ++m)
            if (Expected(m)) state.commandList->ResolveQueryData(heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP,
                start + Offsets[m], 2 * Expected(m), readback.Get(), uint64_t(start + Offsets[m]) * sizeof(uint64_t));
        slot.fence = api->GetNextFrameFenceValue();
    }
}
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces) {
    graphics = interfaces->Get<IUnityGraphics>(); api = interfaces->Get<IUnityGraphicsD3D12v7>();
    if (!graphics || !api) { errorCode = 11; return; }
    baseEvent = graphics->ReserveEventIDRange(11);
    for (int i = 0; i < 11; ++i) {
        UnityD3D12PluginEventConfig config{};
        config.graphicsQueueAccess = i == 10 ? kUnityD3D12GraphicsQueueAccess_Allow : kUnityD3D12GraphicsQueueAccess_DontCare;
        // Timestamp queries do not alter render/descriptor/resource state. No flush flags.
        config.flags = 0; config.ensureActiveRenderTextureIsBound = false;
        api->ConfigureEvent(baseEvent + i, &config);
    }
}
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload() {
    // Unity finishes the Player's graphics work before unloading native plugins.
    if (mapped) readback->Unmap(0, nullptr);
    mapped = nullptr; readback.Reset(); heap.Reset();
}
extern "C" __declspec(dllexport) void* FgpEvent() { return reinterpret_cast<void*>(Event); }
extern "C" __declspec(dllexport) int FgpBase() { return baseEvent; }
extern "C" __declspec(dllexport) int FgpConfigure(int count, int mask) {
    std::lock_guard<std::mutex> lock(mutex);
    if (heap || count < 1 || count > int(MaxCalls) || mask < 0 || mask > 31 || !(mask & 8)) return -15;
    calls = unsigned(count); metricMask = unsigned(mask); return 1;
}
extern "C" __declspec(dllexport) uint64_t FgpFrequency() { std::lock_guard<std::mutex> lock(mutex); return frequency; }
extern "C" __declspec(dllexport) int FgpRead(uint64_t frame, double* ms, int* blocks) {
    std::lock_guard<std::mutex> lock(mutex);
    if (errorCode) return -errorCode;
    auto& slot = slots[frame % Slots];
    if (!mapped || slot.frame != frame || !slot.fence) return 0;
    uint64_t completed = frameFence->GetCompletedValue();
    if (completed == UINT64_MAX) return -13;
    if (completed < slot.fence) return 0;
    const uint64_t* times = mapped + (frame % Slots) * Queries;
    for (unsigned metric = 0; metric < Metrics; ++metric) {
        uint64_t ticks = 0;
        for (unsigned i = 0; i < slot.ended[metric]; ++i) {
            uint64_t begin = times[Offsets[metric] + i * 2], end = times[Offsets[metric] + i * 2 + 1];
            if (!begin || end < begin) return -12;
            ticks += end - begin;
        }
        ms[metric] = double(ticks) * 1000.0 / double(frequency); blocks[metric] = int(slot.ended[metric]);
    }
    return 1;
}
