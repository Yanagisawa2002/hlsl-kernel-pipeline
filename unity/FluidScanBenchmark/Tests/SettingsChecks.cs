using System;
using System.IO;
using HlslPerf.FluidBenchmark;

internal static class SettingsChecks
{
    private static int checks;
    private static void Require(bool condition)
    {
        checks++; if (!condition) throw new Exception("Settings check failed: " + checks);
    }
    private static void Reject(params string[] args)
    {
        try { FluidBenchmarkSettings.Parse(args); }
        catch (ArgumentException) { checks++; return; }
        catch (IOException) { checks++; return; }
        throw new Exception("Invalid invocation was accepted.");
    }
    public static void Main()
    {
        string path = Path.Combine(Path.GetTempPath(), "fluid-test-" + Guid.NewGuid().ToString("N"));
        string[] normal = { "--fluid-benchmark", "--fluid-arm", "original", "--fluid-output", path };
        Require(FluidBenchmarkSettings.Parse(new[] { "-batchmode" }) == null);
        Require(FluidBenchmarkSettings.Parse(normal).arm == ScanArm.Original);
        Require(FluidBenchmarkSettings.Parse(new[] { "--fluid-validate-only", "--fluid-arm", "hlsl-wave-tiled", "--fluid-output", path }).validateOnly);
        Require(FluidBenchmarkSettings.Parse(new[] { "--fluid-benchmark", "--fluid-arm", "gpuprefixsums-rts", "--fluid-output", path }).arm == ScanArm.GpuPrefixSumsRts);
        Reject("--fluid-benchmark");
        Reject("--fluid-benchmark", "--fluid-arm", "unknown", "--fluid-output", path);
        Reject("--fluid-benchmark", "--fluid-validate-only", "--fluid-arm", "original", "--fluid-output", path);
        Reject("--fluid-benchmark", "--fluid-arm", "original", "--fluid-output", path, "--fluid-frames", "0");
        Reject("--fluid-benchmark", "--fluid-arm", "original", "--fluid-output", path, "--fluid-warmup", "0");
        Reject("--fluid-benchmark", "--fluid-arm", "original", "--fluid-output", path, "--fluid-dt", "NaN");
        Reject("--fluid-benchmark", "--fluid-arm", "original", "--fluid-output", path, "--fluid-dt", "0.1");
        Reject("--fluid-benchmark", "--fluid-arm", "original", "--fluid-output", path, "--fluid-arm", "original");
        Reject("--fluid-benchmark", "--fluid-arm", "original", "--fluid-output", path, "--fluid-typo", "1");
        Reject("--fluid-benchmark", "--fluid-arm", "original", "--fluid-output", Path.GetTempPath());
        Require(!Directory.Exists(path));
        Require(!FluidBenchmarkSettings.Enabled);
        Console.WriteLine(checks + " CPU settings checks passed; no Unity/GPU invoked.");
    }
}
