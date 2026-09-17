using System.Diagnostics;
using HlslPerf.Core;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.DXGI.DXGI;

namespace HlslPerf.D3D12;

public sealed record LivePresentTiming(
    double CpuFrameMilliseconds,
    double GpuPipelineMilliseconds,
    double GpuPresentCopyMilliseconds,
    UnifiedSubmissionTiming Submission,
    uint BackBufferIndex);

public sealed partial class D3D12Tuner
{
    /// <summary>
    /// Presents a linear RGBA8 operation buffer directly to a flip-model swapchain.
    /// The source stays GPU-resident: no readback resource, Map, System.Drawing copy,
    /// or CPU pixel conversion is involved in the presentation path.
    /// </summary>
    public sealed class LivePresenter : IDisposable
    {
        private readonly D3D12Tuner owner;
        private readonly UnifiedSession session;
        private readonly string resourceName;
        private readonly int width;
        private readonly int height;
        private readonly uint rowPitch;
        private readonly IDXGIFactory4 factory;
        private readonly IDXGISwapChain3 swapChain;
        private readonly ID3D12Resource[] backBuffers;
        private readonly ID3D12QueryHeap queries;
        private readonly ID3D12Resource queryReadback;
        private bool disposed;

        internal LivePresenter(D3D12Tuner owner, UnifiedSession session, nint hwnd,
            string resourceName, int width, int height)
        {
            if (owner.unifiedQueueType != CommandListType.Direct)
                throw new InvalidOperationException("Live presentation requires D3D12QueueMode.Direct.");
            if (hwnd == 0) throw new ArgumentException("A valid HWND is required.", nameof(hwnd));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (checked(width * 4) % D3D12.TextureDataPitchAlignment != 0)
                throw new ArgumentException("RGBA8 width must produce a 256-byte-aligned row pitch. Use a width divisible by 64 pixels.");

            this.owner = owner;
            this.session = session;
            this.resourceName = resourceName;
            this.width = width;
            this.height = height;
            rowPitch = checked((uint)width * 4);
            GpuBuffer source = session.resources.Get(resourceName);
            if (source.ByteLength != checked(width * height * 4))
                throw new ArgumentException("The live source must be exactly one tightly packed RGBA8 frame.");

            factory = CreateDXGIFactory2<IDXGIFactory4>(false);
            bool tearing = false;
            using (IDXGIFactory5? factory5 = factory.QueryInterfaceOrNull<IDXGIFactory5>())
                if (factory5 is not null) tearing = factory5.PresentAllowTearing;

            SwapChainDescription1 description = new()
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.R8G8B8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = SampleDescription.Default,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Flags = tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None
            };
            SwapChainFullscreenDescription fullscreen = new() { Windowed = true };
            using IDXGISwapChain1 created = factory.CreateSwapChainForHwnd(owner.queue, hwnd, description, fullscreen);
            swapChain = created.QueryInterface<IDXGISwapChain3>();
            factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
            backBuffers = Enumerable.Range(0, 2).Select(i => swapChain.GetBuffer<ID3D12Resource>((uint)i)).ToArray();
            queries = owner.device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, 3, 0));
            queryReadback = owner.device.CreateCommittedResource(HeapType.Readback,
                ResourceDescription.Buffer(3 * sizeof(ulong), ResourceFlags.None, 0), ResourceStates.CopyDest, null);
        }

        public LivePresentTiming Render(IReadOnlyDictionary<string, uint[]>? constantOverrides = null, bool vsync = true)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ObjectDisposedException.ThrowIf(owner.disposed, owner);
            long cpuStart = Stopwatch.GetTimestamp();
            if (constantOverrides is not null)
            {
                Dictionary<string, UnifiedPass> named = session.plan.Passes.ToDictionary(p => p.Name, StringComparer.Ordinal);
                foreach (var pair in constantOverrides)
                    if (!named.TryGetValue(pair.Key, out UnifiedPass? pass) || pass.ShaderId is null ||
                        pair.Value.Length != pass.Constants.Count || pair.Value.Length > 8)
                        throw new ArgumentException("A live constant override must match an existing shader pass.");
            }

            owner.commandList.EndQuery(queries, QueryType.Timestamp, 0);
            foreach (UnifiedPass pass in session.plan.Passes)
                session.Execute(constantOverrides is not null && constantOverrides.TryGetValue(pass.Name, out uint[]? constants)
                    ? pass with { Constants = constants } : pass);
            owner.commandList.EndQuery(queries, QueryType.Timestamp, 1);

            GpuBuffer source = session.resources.Get(resourceName);
            owner.Transition(source, ResourceStates.CopySource);
            uint index = swapChain.CurrentBackBufferIndex;
            ID3D12Resource backBuffer = backBuffers[index];
            owner.commandList.ResourceBarrierTransition(backBuffer, ResourceStates.Present, ResourceStates.CopyDest);
            var footprint = new SubresourceFootPrint(Format.R8G8B8A8_UNorm, (uint)width, (uint)height, 1, rowPitch);
            var placed = new PlacedSubresourceFootPrint { Offset = 0, Footprint = footprint };
            owner.commandList.CopyTextureRegion(new TextureCopyLocation(backBuffer, 0), 0, 0, 0,
                new TextureCopyLocation(source.Resource, placed));
            owner.commandList.ResourceBarrierTransition(backBuffer, ResourceStates.CopyDest, ResourceStates.Present);
            owner.commandList.EndQuery(queries, QueryType.Timestamp, 2);
            owner.commandList.ResolveQueryData(queries, QueryType.Timestamp, 0, 3, queryReadback, 0);

            owner.commandList.Close();
            long submitStart = Stopwatch.GetTimestamp();
            owner.queue.ExecuteCommandList(owner.commandList);
            ulong target = ++owner.fenceValue;
            owner.queue.Signal(owner.fence, target).CheckError();
            double submit = Stopwatch.GetElapsedTime(submitStart).TotalMilliseconds;

            PresentFlags flags = PresentFlags.None;
            uint interval = vsync ? 1u : 0u;
            if (!vsync && (swapChain.Description1.Flags & SwapChainFlags.AllowTearing) != 0)
                flags = PresentFlags.AllowTearing;
            swapChain.Present(interval, flags).CheckError();

            long waitStart = Stopwatch.GetTimestamp();
            if (owner.fence.CompletedValue < target)
            {
                owner.fence.SetEventOnCompletion(target, owner.fenceEvent).CheckError();
                owner.fenceEvent.WaitOne();
            }
            double wait = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;

            ulong[] stamps = queryReadback.Map<ulong>(0, 3).ToArray();
            queryReadback.Unmap(0);
            double gpuPipeline = session.Elapsed(stamps, 0, 1);
            double gpuCopy = session.Elapsed(stamps, 1, 2);

            long resetStart = Stopwatch.GetTimestamp();
            owner.allocator.Reset();
            owner.commandList.Reset(owner.allocator);
            double reset = Stopwatch.GetElapsedTime(resetStart).TotalMilliseconds;
            return new(Stopwatch.GetElapsedTime(cpuStart).TotalMilliseconds, gpuPipeline, gpuCopy,
                new UnifiedSubmissionTiming(0, submit, wait, reset), index);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            queryReadback.Dispose();
            queries.Dispose();
            foreach (ID3D12Resource buffer in backBuffers) buffer.Dispose();
            swapChain.Dispose();
            factory.Dispose();
        }
    }

    public LivePresenter CreateLivePresenter(UnifiedSession session, nint hwnd,
        string resourceName, int width, int height) =>
        new(this, session, hwnd, resourceName, width, height);
}
