#pragma once
#include <windows.h>
#include <stdexcept>
#include <utility>

// Profiling-only PIX support. The normal benchmark has no link-time PIX dependency.
// This uses the stable WinPixEventRuntime ABI documented by Microsoft and loads the
// runtime only when the profile-once command is requested.
class HlslPerfPixRuntime final
{
    using BeginEventOnCommandList = void(WINAPI*)(ID3D12GraphicsCommandList*, UINT64, PCSTR);
    using EndEventOnCommandList = void(WINAPI*)(ID3D12GraphicsCommandList*);

    HMODULE module = nullptr;
    BeginEventOnCommandList beginEvent = nullptr;
    EndEventOnCommandList endEvent = nullptr;

public:
    HlslPerfPixRuntime()
    {
        module = LoadLibraryW(L"WinPixEventRuntime.dll");
        if (!module)
            throw std::runtime_error("profile-once requires WinPixEventRuntime.dll next to scan.exe or on PATH.");

        beginEvent = reinterpret_cast<BeginEventOnCommandList>(GetProcAddress(module, "PIXBeginEventOnCommandList"));
        endEvent = reinterpret_cast<EndEventOnCommandList>(GetProcAddress(module, "PIXEndEventOnCommandList"));
        if (!beginEvent || !endEvent)
        {
            FreeLibrary(module);
            module = nullptr;
            throw std::runtime_error("WinPixEventRuntime.dll does not expose the stable command-list event ABI.");
        }
    }

    HlslPerfPixRuntime(const HlslPerfPixRuntime&) = delete;
    HlslPerfPixRuntime& operator=(const HlslPerfPixRuntime&) = delete;

    ~HlslPerfPixRuntime()
    {
        if (module) FreeLibrary(module);
    }

    void Begin(ID3D12GraphicsCommandList* commands) const
    {
        beginEvent(commands, 0, "HlslPerf.ScanInclusive.ProfileRange");
    }

    void End(ID3D12GraphicsCommandList* commands) const
    {
        endEvent(commands);
    }
};

// Adds a marker around exactly the existing inclusive scan recording hook.
// Input initialization, submission/fence wait and post-scan validation stay outside
// the marker. The first execution is an unmarked warmup; the second is the marked
// operation that Nsight should use for scan-isolated diagnosis.
template<class Base>
class HlslPerfProfiledScan final : public HlslPerfScanValidation<Base>
{
    using Parent = HlslPerfScanValidation<Base>;
    HlslPerfPixRuntime& pix;
    bool markNextScan = false;

public:
    template<class... Args>
    HlslPerfProfiledScan(HlslPerfPixRuntime& pixRuntime, Args&&... args)
        : Parent(std::forward<Args>(args)...), pix(pixRuntime) {}

    void ProfileOnceInclusiveInitOne(uint32_t count)
    {
        // Warm shader/device state using the identical complete scan hook, but do not
        // expose this execution as the profiling range.
        markNextScan = false;
        this->TestInclusiveScanInitOne(count, false, false);

        // Reinitialize to all ones, mark exactly one complete inclusive scan, then
        // validate outside the marker. TestInclusiveScanInitOne performs those steps
        // in that order through the inherited upstream harness.
        markNextScan = true;
        this->TestInclusiveScanInitOne(count, false, true);
        markNextScan = false;

        printf("HPJSON {\"kind\":\"profileRange\",\"name\":\"HlslPerf.ScanInclusive.ProfileRange\",\"count\":%u,\"warmupIterations\":1,\"profiledIterations\":1,\"validated\":true}\n", count);
    }

protected:
    void PrepareScanCmdListInclusive() override
    {
        if (!markNextScan)
        {
            Base::PrepareScanCmdListInclusive();
            return;
        }

        pix.Begin(this->m_cmdList.get());
        Base::PrepareScanCmdListInclusive();
        pix.End(this->m_cmdList.get());
    }
};
