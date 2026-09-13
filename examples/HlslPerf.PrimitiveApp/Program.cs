using HlslPerf.Core;
using HlslPerf.Workloads;
using System.Runtime.InteropServices;

namespace HlslPerf.PrimitiveApp;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--check"))
            throw new ArgumentException("Usage: HlslPerf.PrimitiveApp <asset-root> [--check]");
        string root = Path.GetFullPath(args[0]);

        // A small draw batch: counts -> offsets, material keys -> ordered draw IDs.
        uint[] visibleCounts = [3, 0, 2, 4, 1];
        uint[] materialKeys = [0x80000000, 7, 7, 0, uint.MaxValue];
        uint[] drawIds = [1001, 42, 9007, 18, 73];

        // CPU fixture ONLY: no adapter probing and no real compiler/device identity.
        // A running application must supply its own independently observed runtime.
        var runtime = new OperationRuntime("cpu-example-device", "cpu-example-driver", "D3D12", "6_7",
            ContentHash.Sha256("CPU fixture, not a DXC binary"), 32, 64);
        var offsets = PrimitiveOperations.ExclusiveScan(root, visibleCounts,
            ScanImplementation.GpuPrefixSumsReduceThenScan);
        var offsetsFallback = PrimitiveOperations.ExclusiveScan(root, visibleCounts);
        var drawOrder = PrimitiveOperations.StableSort(root, materialKeys, drawIds,
            SortImplementation.AmdParallelSort);
        var drawOrderFallback = PrimitiveOperations.StableSort(root, materialKeys, drawIds);

        // Explicit application policy: use a supported, source-verified implementation
        // without claiming a measured win. No tuning, profile synthesis or chat approval.
        var selectedOffsets = PrimitiveOperations.Select(root, offsets, offsetsFallback, runtime,
            allowUnmeasured: true);
        var selectedOrder = PrimitiveOperations.Select(root, drawOrder, drawOrderFallback, runtime,
            allowUnmeasured: true);

        Console.WriteLine("CPU plans only; fixture capabilities; no GPU execution or performance evidence.");
        Print("Tile offsets", selectedOffsets);
        Print("Material draw order", selectedOrder);
        if (args.Length == 2)
            CheckPlans(root, runtime, offsets, offsetsFallback, drawOrder, drawOrderFallback,
                selectedOffsets, selectedOrder);
    }

    private static void Print(string label, OperationSelection selection)
    {
        var plan = selection.Plan;
        Console.WriteLine($"{label}: {plan.Implementation}, {selection.PerformanceStatus}, fallback={selection.UsedFallback}");
        Console.WriteLine($"  {selection.Reason}");
        Console.WriteLine($"  identity={selection.SourceAndPlanSha256}");
        Console.WriteLine($"  {plan.LogicalCount} items; {plan.Buffers.Sum(b => (long)b.ByteLength)} logical buffer bytes " +
            $"(excludes heap alignment, upload/readback, PSOs and 256-byte dummy)");
        Console.WriteLine("  full pass sequence: " + string.Join(" -> ", plan.Passes.GroupBy(p => p.Stage)
            .Select(g => $"{g.Key}={g.Count()}")));
        Console.WriteLine("  outputs: " + string.Join(", ", plan.Outputs.Select(o => o.Resource)));
    }

    private static void CheckPlans(string root, OperationRuntime runtime,
        UnifiedOperationPlan offsets, UnifiedOperationPlan offsetsFallback,
        UnifiedOperationPlan drawOrder, UnifiedOperationPlan drawOrderFallback,
        OperationSelection selectedOffsets, OperationSelection selectedOrder)
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidDataException(message);
            checks++;
        }
        static string Hash(uint[] values) => ContentHash.Sha256(MemoryMarshal.AsBytes(values.AsSpan()).ToArray());
        Check(!selectedOffsets.UsedFallback && !selectedOrder.UsedFallback, "Explicit RTS/AMD selection");
        Check(selectedOffsets.PerformanceStatus == OperationPerformanceStatus.Unmeasured &&
            selectedOrder.PerformanceStatus == OperationPerformanceStatus.Unmeasured, "No inherited performance claim");
        Check(offsets.Outputs.Single().ExpectedSha256 == Hash([0, 3, 3, 5, 9]), "Application count offsets");
        Check(drawOrder.Outputs[0].ExpectedSha256 == Hash([0, 7, 7, 0x80000000, uint.MaxValue]) &&
            drawOrder.Outputs[1].ExpectedSha256 == Hash([18, 42, 9007, 1001, 73]), "Full-width keys and stable arbitrary draw IDs");
        Check(offsets.Passes.Last().Stage == UnifiedStage.OutputConversion, "RTS vector padding trim is included");
        Check(drawOrder.Passes.First().Stage == UnifiedStage.InputRestore &&
            drawOrder.Passes.Count(p => p.Stage == UnifiedStage.Algorithm) == 40, "AMD deinterleave/restore plus all eight digits");
        Check(PrimitiveOperations.Select(root, offsets, offsetsFallback, runtime).UsedFallback &&
            PrimitiveOperations.Select(root, drawOrder, drawOrderFallback, runtime).UsedFallback, "No profile and no opt-in uses baselines");
        Check(PrimitiveOperations.Select(root, offsets, offsetsFallback, runtime with { ShaderModel = "6_6" },
            allowUnmeasured: true).UsedFallback, "RTS requires SM 6.7; baseline supports SM 6.6");

        // Keys-only binary fallback supports wave32. The pairs fallback requires wave64;
        // Select correctly throws if neither requested nor fallback plan is supported.
        var keys = PrimitiveOperations.StableSort(root, [7, 0], implementation: SortImplementation.AmdParallelSort);
        var keysFallback = PrimitiveOperations.StableSort(root, [7, 0]);
        Check(PrimitiveOperations.Select(root, keys, keysFallback, runtime with { MaximumWaveSize = 32 },
            allowUnmeasured: true).UsedFallback, "AMD requires wave64; keys-only baseline supports wave32");

        // Deliberately invalid test profile, never emitted as deployment evidence.
        string testConfirmation = ContentHash.Sha256("CPU rejection fixture, not a confirmation record");
        var stale = new OperationDeploymentProfile(offsets.Implementation, offsets.SemanticId,
            UnifiedOperationPlan.AbiId, new string('0', 64), runtime, testConfirmation,
            OperationPerformanceStatus.Confirmed);
        Check(PrimitiveOperations.Select(root, offsets, offsetsFallback, runtime, stale, true,
            testConfirmation).UsedFallback, "Stale identity cannot be bypassed by allowUnmeasured");
        Check(PrimitiveOperations.Select(root, offsets, offsetsFallback, runtime,
            stale with { SourceAndPlanSha256 = selectedOffsets.SourceAndPlanSha256 }).UsedFallback,
            "Incoming profile cannot supply its own trusted confirmation");
        var empty = PrimitiveOperations.StableSort(root, [], [], SortImplementation.AmdParallelSort);
        Check(empty.Shaders.Count == 0 && empty.Passes.All(p => p.CopyBytes == 4) &&
            empty.Outputs.All(o => o.ExpectedSha256 == Hash([0])), "Empty pair operation restores both zero sentinels");
        Console.WriteLine($"PASS: {checks} CPU plan checks (oracle metadata and selection, not GPU output verification).");
    }
}
