using System.Text.Json;

namespace HlslPerf.Core;

public enum OperationPerformanceStatus { Unmeasured, Confirmed }

/// <summary>Application-owned runtime identity; never populated from an incoming profile.</summary>
public sealed record OperationRuntime(string Device, string Driver, string Backend,
    string ShaderModel, string CompilerSha256, int MinimumWaveSize, int MaximumWaveSize)
{
    public void Validate()
    {
        if (new[] { Device, Driver, Backend, ShaderModel }.Any(string.IsNullOrWhiteSpace) ||
            !OperationIdentity.IsHash(CompilerSha256) || MinimumWaveSize < 4 || MaximumWaveSize < MinimumWaveSize)
            throw new InvalidDataException("An explicit device/driver/compiler/wave capability identity is required.");
    }
}

/// <summary>Separate from historical tuning profiles. Confirmation is an application trust boundary.</summary>
public sealed record OperationDeploymentProfile(string Implementation, string SemanticId, string Abi,
    string SourceAndPlanSha256, OperationRuntime Runtime, string ConfirmationSha256,
    OperationPerformanceStatus PerformanceStatus);

public sealed record OperationSelection(UnifiedOperationPlan Plan, string SourceAndPlanSha256,
    OperationPerformanceStatus PerformanceStatus, bool UsedFallback, string Reason);

public static class OperationIdentity
{
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>Portable identity: source bytes, compile options, ABI, full plan and assembly identity.
    /// All include-directory headers are conservatively included (including conditional includes).
    /// Input and oracle hashes deliberately bind this deployment to the prepared operation.</summary>
    public static string Compute(UnifiedOperationPlan plan, string assetRoot)
    {
        plan.Validate();
        string root = Path.GetFullPath(assetRoot);
        string Relative(string path)
        {
            string relative = Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new InvalidDataException("Operation sources must remain within the reviewed asset root.");
            return relative;
        }
        var shaders = plan.Shaders.OrderBy(s => s.Id, StringComparer.Ordinal).Select(shader => new
        {
            shader.Id, source = Relative(shader.SourcePath), shader.EntryPoint, shader.ShaderModel,
            shader.HlslVersion, shader.EnableStrictness,
            defines = shader.Defines.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
            includes = shader.IncludeDirectories.Select(Relative).ToArray(), shader.CompilerArguments,
            files = new[] { shader.SourcePath }.Concat(shader.IncludeDirectories.SelectMany(directory =>
                Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(path =>
                    Path.GetExtension(path) is ".h" or ".hlsl" or ".hlsli" or ".compute")))
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new { path = Relative(path), sha256 = ContentHash.Sha256(File.ReadAllBytes(path)) })
                .OrderBy(file => file.path, StringComparer.Ordinal).ToArray()
        }).ToArray();
        return ContentHash.Sha256(JsonSerializer.Serialize(new
        {
            abi = UnifiedOperationPlan.AbiId, sdk = HlslPerfSdk.Version,
            coreAssembly = ContentHash.Sha256(File.ReadAllBytes(typeof(OperationIdentity).Assembly.Location)),
            plan.Implementation, plan.LogicalCount, plan.SemanticId, plan.InputSha256,
            buffers = plan.Buffers.Select(b => new { b.Name, b.ByteLength,
                initial = b.InitialData is null ? null : ContentHash.Sha256(b.InitialData) }),
            plan.Passes, plan.Outputs, plan.ImmutableInputs, shaders
        }, JsonDefaults.Options));
    }
}
