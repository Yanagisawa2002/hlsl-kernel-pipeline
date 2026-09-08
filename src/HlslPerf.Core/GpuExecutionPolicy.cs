namespace HlslPerf.Core;

/// <summary>Fail-closed entry guard. Compilation and CPU plan construction need no authorization.</summary>
public static class GpuExecutionPolicy
{
    public const string AuthorizationVariable = "HLSLPERF_EXECUTION_AUTHORIZATION";
    public const string AuthorizationValue = "I_HAVE_NEW_USER_AUTHORIZATION";
    public const string DenyVariable = "HLSLPERF_GPU_POLICY";

    public static void RequireAuthorized() => Check(
        Environment.GetEnvironmentVariable(DenyVariable),
        Environment.GetEnvironmentVariable(AuthorizationVariable));

    public static void Check(string? policy, string? authorization)
    {
        if (string.Equals(policy, "deny", StringComparison.OrdinalIgnoreCase) ||
            authorization != AuthorizationValue)
            throw new InvalidOperationException("GPU execution, benchmarking, tuning and profiling are disabled. " +
                "Use compile-only / CPU validation. A later run requires new explicit user authorization; " +
                $"only then may {AuthorizationVariable} be set. {DenyVariable}=deny always takes precedence.");
    }
}
