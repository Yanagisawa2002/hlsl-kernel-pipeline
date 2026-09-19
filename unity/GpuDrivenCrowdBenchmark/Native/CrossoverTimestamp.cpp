#include <windows.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <atomic>
#include <cstdint>
#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"
using Microsoft::WRL::ComPtr;
namespace {
constexpr unsigned Capacity=32;
struct Slot { std::atomic<int> state{0}; uint64_t id=0,fence=0; unsigned stage=0; };
Slot slots[Capacity];
IUnityGraphics* graphics=nullptr; IUnityGraphicsD3D12v7* api=nullptr;
ComPtr<ID3D12QueryHeap> heap; ComPtr<ID3D12Resource> readback; ComPtr<ID3D12Fence> fence;
uint64_t* mapped=nullptr; uint64_t frequency=0; std::atomic<int> ready{0},error{0};
void Fail(int code) { int expected=0; error.compare_exchange_strong(expected,code); }
void UNITY_INTERFACE_API DeviceEvent(UnityGfxDeviceEventType event) {
 if(event==kUnityGfxDeviceEventShutdown) { ready=0; mapped=nullptr;readback.Reset();heap.Reset();fence.Reset(); }
}
void UNITY_INTERFACE_API Event(int event,void* data) {
 if(error.load()) return;
 if(event==0) {
  if(ready.load()) return;
  if(!api || graphics->GetRenderer()!=kUnityGfxRendererD3D12) { Fail(1);return; }
  // Queue access is allowed ONLY for this one-time submission-thread event.
  auto queue=api->GetCommandQueue(); if(!queue || FAILED(queue->GetTimestampFrequency(&frequency)) || !frequency) {Fail(2);return;}
  auto device=api->GetDevice(); D3D12_QUERY_HEAP_DESC q={D3D12_QUERY_HEAP_TYPE_TIMESTAMP,Capacity*3,0};
  if(FAILED(device->CreateQueryHeap(&q,IID_PPV_ARGS(&heap)))) {Fail(3);return;}
  D3D12_HEAP_PROPERTIES hp={};hp.Type=D3D12_HEAP_TYPE_READBACK;
  D3D12_RESOURCE_DESC desc={};desc.Dimension=D3D12_RESOURCE_DIMENSION_BUFFER;desc.Width=Capacity*3*sizeof(uint64_t);desc.Height=1;desc.DepthOrArraySize=1;desc.MipLevels=1;desc.SampleDesc.Count=1;desc.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
  if(FAILED(device->CreateCommittedResource(&hp,D3D12_HEAP_FLAG_NONE,&desc,D3D12_RESOURCE_STATE_COPY_DEST,nullptr,IID_PPV_ARGS(&readback)))) {Fail(4);return;}
  D3D12_RANGE range={0,static_cast<SIZE_T>(desc.Width)};
  if(FAILED(readback->Map(0,&range,reinterpret_cast<void**>(&mapped)))) {Fail(5);return;}
  fence=api->GetFrameFence(); if(!fence) {Fail(6);return;} ready.store(1,std::memory_order_release);return;
 }
 if(event!=1 || !ready.load(std::memory_order_acquire)) {Fail(7);return;}
 uint64_t packed=reinterpret_cast<uintptr_t>(data), id=packed>>2; unsigned stage=static_cast<unsigned>(packed&3);
 if(!id || stage>2) {Fail(8);return;} Slot& slot=slots[(id-1)%Capacity];
 if(slot.state.load(std::memory_order_acquire)!=1 || slot.id!=id || slot.stage!=stage) {Fail(9);return;}
 UnityGraphicsD3D12RecordingState recording={};
 if(!api->CommandRecordingState(&recording) || !recording.commandList) {Fail(10);return;}
 unsigned base=static_cast<unsigned>((id-1)%Capacity)*3;
 recording.commandList->EndQuery(heap.Get(),D3D12_QUERY_TYPE_TIMESTAMP,base+stage);slot.stage++;
 if(stage==2) {
  recording.commandList->ResolveQueryData(heap.Get(),D3D12_QUERY_TYPE_TIMESTAMP,base,3,readback.Get(),base*sizeof(uint64_t));
  slot.fence=api->GetNextFrameFenceValue();if(!slot.fence) {Fail(11);return;}
  slot.state.store(2,std::memory_order_release);
 }
}
}
extern "C" {
UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces) {
 graphics=interfaces->Get<IUnityGraphics>();api=interfaces->Get<IUnityGraphicsD3D12v7>();
 graphics->RegisterDeviceEventCallback(DeviceEvent);
 if(!api) {Fail(12);return;}
 UnityD3D12PluginEventConfig init={kUnityD3D12GraphicsQueueAccess_Allow,0,false};api->ConfigureEvent(0,&init);
 UnityD3D12PluginEventConfig record={kUnityD3D12GraphicsQueueAccess_DontCare,0,false};api->ConfigureEvent(1,&record);
}
UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginUnload() { if(graphics) graphics->UnregisterDeviceEventCallback(DeviceEvent); }
UNITY_INTERFACE_EXPORT UnityRenderingEventAndData UNITY_INTERFACE_API CrossoverEvent() {return Event;}
UNITY_INTERFACE_EXPORT int UNITY_INTERFACE_API CrossoverStatus() {return error.load() ? -error.load() : ready.load();}
UNITY_INTERFACE_EXPORT int UNITY_INTERFACE_API CrossoverReserve(uint64_t id) {
 if(!id || !ready.load() || error.load()) return -1;
 Slot& s=slots[(id-1)%Capacity];if(s.state.load()!=0)return 0;
 s.id=id;s.fence=0;s.stage=0;s.state.store(1,std::memory_order_release);return 1;
}
// Called by the sole main-thread consumer. No wait, fence signal, or queue submission.
UNITY_INTERFACE_EXPORT int UNITY_INTERFACE_API CrossoverRead(uint64_t id,uint64_t* output) {
 if(error.load())return -error.load();Slot& s=slots[(id-1)%Capacity];
 if(s.state.load(std::memory_order_acquire)!=2 || s.id!=id)return 0;
 uint64_t completed=fence->GetCompletedValue();if(completed==UINT64_MAX) {Fail(13);return -13;}if(completed<s.fence)return 0;
 unsigned base=static_cast<unsigned>((id-1)%Capacity)*3;
 output[0]=id;output[1]=mapped[base];output[2]=mapped[base+1];output[3]=mapped[base+2];output[4]=frequency;output[5]=s.fence;output[6]=completed;
 if(!(output[1]<output[2] && output[2]<output[3])) {Fail(14);return -14;}
 s.state.store(0,std::memory_order_release);return 1;
}
}
