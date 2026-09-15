# HLSL 完整任务与 RTX 5090 迁移审阅

2026-09-15。按最新指示仅审阅；暂停新实验、构建、安装和远程操作。

> 这是恢复授权前的审阅快照。此后已获准修补调试消息、构建身份和比较设计；
> 当前修补与剩余门槛见 [新版协议](CROWD_PROTOCOL_V2.md) 和
> [复现说明](CROWD_REPRODUCTION.md)。下文的暂停状态、提交和检查结果均属于当时记录。

## 判定

- **完整任务边界成立，但属于仓库已有的受控计算渲染工作量。** 可以证明明确的渲染任务表现，不能提升为生产或通用应用证据。
- **输出正确性已有实测证据；性能审阅尚未闭合。** 正式发现、分阶段诊断、冻结注册和独立确认均未运行，当前没有应用提速结论。
- **当前程序不能直接部署到 Linux RTX 5090。** `D3D12Tuner.cs:44-45` 在非 Windows 系统直接拒绝执行；租到空闲 GPU 没有消除后端依赖。
- **建议保留 Windows/D3D12 路线。** 若只使用当前 Linux 主机，本项先保留限制；只有明确接受新增 Vulkan/CUDA 后端及新的证据范围后才考虑移植。

源码目录：`D:/CodexWork/hlsl-whole-task-20260915`。本地提交
`fa00ed3d1226d747aa3a57552f8ce2fdc676f460`，分支
`codex/whole-task-crowd-20260915`，尚未推送或创建 PR。
来源 main 为 `397f0054fa92990e39f4f217f3335ea606d0222f`。

## 1. 真正已通过的内容

以下路径均相对于上述源码目录；原始文件保留在 `.scratch`，未写入旧 checkout。

| 验证 | 实际结果 | 证据文件 |
|---|---|---|
| CPU 契约与回归 | 194 通过，0 失败/跳过 | `.scratch/cpu-lock-01/tests/whole-task.trx`、`.scratch/cpu-lock-01/tests.log` |
| 完整解决方案 | 13 个项目构建成功，0 警告/错误 | `.scratch/review-tests-01/solution-build.log` |
| GPU 边界与污染检查 | 最新一轮 432 次完整输出执行通过 | `.scratch/validation-04/correctness.json`、`.scratch/rehearsal-lock-01/validation.log` |
| 四规模完整场景 | 两个 seed、四条路径、两个动画窗口，共 64 次完整图集及列表/分桶检查通过 | `.scratch/full-scenes-01/full-scenes.json`、同目录实际 `.rgba`、`.scratch/full-check-lock-01/full-scenes.log` |
| 调用方与文件导出演练 | 四路径各完成 12 个连续图集，即各 144 帧；48 个图集逐字节通过 | `.scratch/rehearsal-01/large-<arm>/result.json`、`samples.json`、`atlas-00..11.rgba` |
| 统计/证据分析控制 | 9 项通过；含完整 128 进程合成矩阵、已知比例、输出篡改、缺失进程、错误顺序、重叠和负计时控制 | `.scratch/analysis-final-01/tests.log`、`tools/test_crowd_analysis.py` |
| 固定外部源码 | 114 个锁定文件通过 | `.scratch/full-check-lock-01/external-source.log` |

演练的 `performanceEligible=false`，后八个请求标为 `rehearsal`。其计时字段只验证记录流程，不能用于性能对照。没有把内层请求计为独立进程。

GPU 检查覆盖 N=1、31、255、1023、4095、4096、4097、8193、65539，三种可见性 mask，两种 poison，复用资源后的连续窗口；核对完整像素、稳定可见序列/数量、分桶成员与 multiplicity、exclusive offsets、游标、输入不变和容量哨兵。

环境实测为 RTX 4090、驱动 591.86 / DXGI 32.0.15.9186、LUID 99768、Windows build 26200、Core Ultra 7 265K、约 32 GiB RAM。SDK 10.0.302、runtime 10.0.10、Vortice 3.8.3；实际加载的 DXC/DXIL DLL 为 **1.9.2602.17**，哈希在 `.scratch/validation-04/runtime.json`。这不是旧原生 inclusive 实验的 DXC 1.8 配置。

上述执行发生在提交前的工作区；随后把实现整理进该本地提交。**尚缺在干净提交上重新构建、快照源码/二进制并绑定最终验证的封存步骤。** 旧轮次保留为开发证据，不能声称已经完成正式冻结。

### 新发现的验收缺口：调试消息可能截断

`validation-04/debug.json` 恰好保留 1024 条 `CreateResourceStateIgnored` 警告。
`D3D12UnifiedOperations.cs:23-32` 只读取存储消息；调用方在整个矩阵末尾读取一次，未检查丢失计数。
因此只能说已保存消息中未见错误，**不能证明整个执行期没有其他 D3D12 错误**。
应逐场景读取/清空、记录 `GetNumMessagesDiscardedByMessageCountLimit` 并在丢失消息时拒绝验收。
这是尚未实施的修复，不否定已有逐字节输出比较。[Microsoft 接口说明](https://learn.microsoft.com/en-us/windows/win32/api/d3d12sdklayers/nf-d3d12sdklayers-id3d12infoqueue-getnummessagesdiscardedbymessagecountlimit)

## 2. 最小完整任务与消费边界

```text
CPU 原有 agent seed 生成
  → 上传一次并复用
  → GPU 随动画帧变化的可见性判定
  → 稳定紧凑可见列表（exclusive 偏移）
  → tile histogram / exclusive offsets / tile lists
  → 原有整数 glow 与背景计算
  → 12 × 480 × 270 RGBA 图集
  → 完整 GPU→CPU 回读、CPU 字节复制、原始 RGBA 文件关闭
```

真实既有调用方是 `src/HlslPerf.GpuDrivenDemo/Program.cs:260-300` 的图集捕获，以及 `:212-217` 把图集交给 `CrowdVfxComposer.WriteBudgetCrossing`。本轮终点是可供该消费者使用的完整图集文件；视频编码、呈现和 Unity 不在指标内。文件写入是缓冲写入加关闭，不是持久落盘延迟。

新 `CrowdApplication` 没有调用 CPU oracle 决定工作。容量从 N 有界推导，下游读取 GPU count header，并使用有界 grid-stride 调度。参考输出哈希只作验证元数据。代码见 `CrowdApplication.cs:29-173`；单次调用计时边界见新 runner `Program.cs:201-253` 与 `D3D12CpuOutput.cs:42-66`。

旧 19% 结果属于 RTX 4090、`2^28` uint32 **inclusive** 原生 GPU 操作。本任务需要 **exclusive** 稳定紧凑偏移，使用真实下游像素消费，不能先假定该优势会转化。

## 3. 尚缺的关键实验和审阅条件

### P0：先闭合方法和可执行平台

1. 选择 Windows/D3D12，或明确把 Vulkan/CUDA 作为新后端范围；不能在现有 Linux 主机直接执行本程序。
2. 修复调试消息完整性，随后对干净提交封存源码、二进制、运行时、完整输出和正确性证据。
3. 补充 **CPU 常规路径的适用性检查**。输入本来就在 CPU，输出最终也回到 CPU；短寿命任务只比较四条 GPU 路径可能漏掉合理方案。现有 CPU oracle 是串行实现，不能直接当作经过相称优化的 CPU 基线。可按帧并行、复用缓冲、固定线程预算，保留整数输出与稳定列表；若有竞争力，应纳入确认。
4. 明确主比较和多重比较规则。现草案对四 case 的六组两两比较给未调整的 95% 区间；不能事后选任意显著项宣布胜出。发现阶段决定最终路径和合理替代方案后冻结一个主比较；其余明确标探索性，或使用预先指定的校正。

### P1：同一完整任务的发现和诊断

当前四条 GPU 路径已有实现，但性能发现一次也没有运行：

| 路径 | 对照角色与必要成本 |
|---|---|
| `hierarchical` | 原有 materialized flags → Blelloch exclusive scan → scatter；保留完整多层扫描与下游 |
| `fused` | 既有融合可见性/scan/scatter，加两副本 LDS histogram；是必须保留的强应用对照 |
| `wave-tiled` | materialized flags → 当前 wave-tiled **exclusive** → scatter；原固定参数，未为本任务调参 |
| `rts` | 同一 producer/consumer + 固定版本 RTS `Reduce/Scan/PropagateExclusive`；GPU producer 写必要零填充，无冗余上传/trim |

fused 同时改变 histogram，需用阶段诊断拆分收益来源，不能把整条路径差异归于 scan。

固定待测场景：small N=262144/mask15；medium-tail N=1048579/mask63；large N=8388608/mask511；dense-tail N=262147/mask3。均为 12 帧 480×270，连续推进窗口；发现 seed=19088743，独立确认 seed=69501203。密集场景、非对齐尾部和一次性生命周期必须保留。

分别记录 GPU classify/scan/scatter/histogram/raster、完整 GPU 操作、GPU 回读、CPU recording/submit/fence wait/copy/export。侵入式 pass timestamps 与正式样本分开。首先确认瓶颈，再决定是否值得新增优化；没有机会即可有证据地停止候选优化。

### P2：冻结与独立确认

现有脚本草案是 16 个发现进程，之后 128 个确认进程：8 轮×4 case×4 arm，Williams 顺序平衡位置与前序，case 顺序轮换。每进程 1 次 first-use、3 次 warmup、8 次单独提交/完成的请求；每请求验证全部图集。**该注册尚未发生。**

若四 GPU 路径维持不变，可使用这份草案；若 CPU 基线/后端/候选改变，必须先更新顺序、独立样本数和统计代码，不能套用硬编码的 8 进程/df=7。128 是未批准的候选实验量，不是先跑满再找结果的要求。

总成本要覆盖：CPU seed 生成、plan、设备/PSO 编译、分配/上传、提交/等待、所有 GPU passes、完整回读、CPU copy、RGBA export，以及生命周期结束的释放。一次性和复用 12 请求分别报告。现有“12 请求成本”是应用工作时间累计，排除了验证间隙，不能称为包含实验验证的连续 wall time；不能相加分阶段 p95。

最大规模已验证的资源规模如下；这是当前 Windows 分配证据，不是 5090 Linux 的实测显存：

| 路径 | logical bytes | committed default-buffer bytes | 独立 readback allocation |
|---|---:|---:|---:|
| hierarchical | 174064724 | 174718976 | 6225920 |
| fused | 106914840 | 107347968 | 6225920 |
| wave-tiled | 174023704 | 174456832 | 6225920 |
| RTS | 174010044 | 174456832 | 6225920 |

这些 committed 数字未囊括所有 query/driver/PSO 开销；还需保留进程 DXGI usage、host peak、实际 transfer bytes。占用率、真实带宽、能耗、呈现 FPS 保持 unavailable。

## 4. Ubuntu 22.04 + RTX 5090 的适用性

以下主机信息由协调任务的现场只读核验提供，本任务没有连接服务器：Docker/Ubuntu22.04.5，5090 32607 MiB，driver580.76.05；CPU 时间额度25核，cpuset0–207，memory.max90GiB；系统盘30GiB、数据盘50GiB；Vulkan ICD/GLX/EGL库存在，无 DISPLAY/X；PATH未发现 dotnet/dxc/nvcc/Unity。

| 部分 | 当前 Linux 可直接承接？ | 结论 |
|---|---|---|
| CPU scene/oracle/纯数据契约 | 代码可移植，但该主机 .NET 尚未确认可用 | 准备依赖后才可验证，不构成 GPU 结果 |
| Python 统计 | 算法部分可移植 | 当前 preflight 使用 Win32 `GetSystemTimes`，PowerShell gate 使用 Windows mutex/volume/process API，需另写 Linux/cgroup gate |
| HLSL 编译 | DXC 有 Linux/SPIR-V 支持 | 编译器不提供本仓库缺失的 Vulkan 执行器 |
| 当前 GPU 执行 | **否** | DXGI、D3D12 device/queues/resources/barriers/fences/readback/timestamps 全部依赖 Windows 后端 |
| Headless Vulkan 移植 | 技术上可行，当前未实现/验证 | 缺 X 本身不是 compute 阻碍；必须先验证实际 Vulkan device、compute queue、subgroup32、timestamp 等能力 |
| CUDA 移植 | 需要新 kernel/host 集成 | 外部合理方案应考虑固定版本 CUB；得到的是 CUDA 后端完整任务证据 |

Vulkan 最少要新增资源与 descriptor/push-constant 绑定、SPIR-V 构建、队列/同步/回读/时间戳执行器，并重新证明 wave32、跨工作组发布/读取和 lookback/fallback 的内存语义。现有整数 oracle、输入与任务输出契约可复用，**现有 DXIL/driver/资源与计时证据不可移植**。[Khronos HLSL 指南](https://github.khronos.org/Vulkan-Site/guide/latest/hlsl.html)、[Microsoft SPIR-V 说明](https://github.com/microsoft/DirectXShaderCompiler/wiki/SPIR%E2%80%90V-CodeGen)、[Khronos headless compute](https://www.khronos.org/blog/getting-started-with-vulkan-compute-acceleration)

CUDA 外部扫描可使用 CUB `DeviceScan::ExclusiveSum`，临时空间查询、分配、GPU count 与消费链都应计入真实生命周期；若改用 device selection，应保持稳定性契约并重新验证。它不能替代“当前 HLSL/D3D12 实现”的比较。[NVIDIA CUB 文档](https://nvidia.github.io/cccl/unstable/cub/api/structcub_1_1DeviceScan.html)

新主机即使 GPU 空闲，也不能把 cpuset 的208个编号当作208个独占核。CPU 基线、编译和进程级端到端数据都应固定在25核额度内并留出余量，记录 cgroup throttling/争用。Vulkan ICD 文件存在不是能力测试结果。不同硬件/OS/API/compiler/driver 的时间不能与旧4090 inclusive数字拼接为提速。

## 5. 可删减项与建议顺序

1. **本轮先决策 API 路线和上述 P0 缺口。** 不为利用已租 GPU 强行创建移植项目。
2. 保留当前全部正确性证据；后续针对最终干净版本/实际新后端做必要重验，不重复解释相同开发轮次为独立证据。
3. 若继续 Windows 路线，先少量完整任务发现、CPU 适用性检查和阶段诊断，之后才批准正式矩阵及主比较。
4. 可删去旧18进程 inclusive微基准、`2^28` scan重跑、scan重复压力网格、无关sort实验、Unity/MSVC原生host重建、wave64/跨GPU泛化及先行的大型profiler sweep。
5. 小/中/大、密集/尾部、短寿命/复用、合理原路径/强常规/外部方案、完整输出、必要传输和进程级不确定性不能为了减少实验量而删除。

当前行动状态：审阅暂停，已运行进程均结束，最近锁释放凭据为
`.scratch/analysis-final-01/released.json`。没有写 terminal handoff，也没有把等待平台/审阅当作性能完成。原任务的报告、推送和可审阅 PR 仍待审阅结论与后续交付阶段处理。
