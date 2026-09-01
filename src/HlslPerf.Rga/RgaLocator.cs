using System.Diagnostics;

namespace HlslPerf.Rga;

public sealed record RgaInstallation(string ExecutablePath, string? Version);

public static class RgaLocator
{
    public static RgaInstallation? Find(string? explicitPath = null)
    {
        foreach (string candidate in Candidates(explicitPath))
        {
            string path = Directory.Exists(candidate) ? Path.Combine(candidate, "rga.exe") : candidate;
            if (!File.Exists(path))
                continue;
            string fullPath = Path.GetFullPath(path);
            return new RgaInstallation(fullPath, ReadVersion(fullPath));
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            yield return explicitPath;
        string? environmentPath = Environment.GetEnvironmentVariable("RGA_PATH");
        if (!string.IsNullOrWhiteSpace(environmentPath))
            yield return environmentPath;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return Path.Combine(directory, OperatingSystem.IsWindows() ? "rga.exe" : "rga");

        if (OperatingSystem.IsWindows())
        {
            string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.Combine(programFiles, "AMD", "Radeon GPU Analyzer", "rga.exe");
                yield return Path.Combine(programFiles, "AMD", "Radeon Developer Tool Suite", "RGA", "rga.exe");
                string amdDirectory = Path.Combine(programFiles, "AMD");
                if (Directory.Exists(amdDirectory))
                    foreach (string directory in Directory.EnumerateDirectories(amdDirectory, "Radeon GPU Analyzer*"))
                        yield return Path.Combine(directory, "rga.exe");
            }
        }
    }

    private static string? ReadVersion(string executablePath)
    {
        try
        {
            ProcessStartInfo startInfo = new(executablePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--version");
            using Process process = Process.Start(startInfo)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(true);
                return null;
            }
            return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
