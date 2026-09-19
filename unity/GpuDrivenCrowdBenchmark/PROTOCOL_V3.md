# Protocol v3: startup activation and explicit native query identity

Diagnostic-only amendment, before native collection. No formal matrix, performance pilot, crossover or merge authorization. Preserve v1/v2 evidence.

1. Force Graphics Jobs off, record runtime renderingThreadingMode. D3D12 remains default; D3D11 is an explicit diagnostic override in the same build.
2. Compare jobs-off Recorder with startup profiler capture and Editor GPU-area-before-launch Autoconnect. Retain raw capture plus extracted GPU hierarchy. Positive hierarchy with zero Recorder only diagnoses a Recorder/activation-path issue; it is not a performance result.
3. Require the existing 96-frame repetition code to match completely before accepting legacy attribution. Nonzero samples alone are insufficient.
4. If attribution remains ambiguous, test a native D3D12 plugin: timestamp query heap, T0 before reset/cull, T1 after cull, T2 after count-copy/indirect draw; resolve into a readback ring. Explicit generation/submission IDs replace assumed lag. Read only after the Unity frame fence value captured with that resolve is complete. Queue frequency is obtained once in a queue-access callback; command recording uses a separate callback with queue access disabled, following Unity's interface contract. No per-frame flush/wait, queue execution or CPU-visible count decision.
5. Native diagnostic: 100k fixed agents, 96 deterministic submissions, 32 ring slots (exercise reuse), bounded later-Update drain, report raw IDs/ticks/frequency/fence/completion frames. Require exactly 96 unique completed IDs, positive ordered timestamps, fence completion, no slot overwrite, no native error. Run fresh normal/batch processes; this is availability/attribution evidence on an active desktop, not quiet throughput evidence. Only after this checkpoint may native integration into quality pilots be assessed; v2 measure stage remains disabled.

Native T1 changes substage accounting versus v2: count-copy moves from cull to draw. Never pool their stage costs. Timestamp instrumentation and profiler captures have overhead; no architecture win may be inferred here.

Observed launch-mode distinction: normal is MultiThreaded; batchmode is SingleThreaded on this Player. Both are Graphics-Jobs-off modes. Native validation accepts either, records the mode, and rejects Jobs modes; a transient checker assumption requiring MultiThreaded for batchmode was corrected without rerunning/discarding the completed batch evidence. Launch-mode A/B is not a pure scheduling-flag comparison.
