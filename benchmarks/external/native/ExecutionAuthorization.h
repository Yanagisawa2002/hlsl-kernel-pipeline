#pragma once
#include <cstdlib>
#include <cstring>
#include <cstdio>

// Called before even creating a D3D12 device. A build never sets this authorization.
inline bool HlslPerfExecutionAuthorized()
{
    const char* deny = std::getenv("HLSLPERF_GPU_POLICY");
    const char* authorization = std::getenv("HLSLPERF_EXECUTION_AUTHORIZATION");
    if ((deny && _stricmp(deny, "deny") == 0) || !authorization ||
        std::strcmp(authorization, "I_HAVE_NEW_USER_AUTHORIZATION") != 0)
    {
        std::fputs("GPU/benchmark execution disabled. A later run needs new explicit user authorization.\n", stderr);
        return false;
    }
    return true;
}
