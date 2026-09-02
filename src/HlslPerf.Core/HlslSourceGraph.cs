using System.Text;
using System.Text.RegularExpressions;

namespace HlslPerf.Core;

public sealed record HlslSourceDependency(string RelativePath, string FullPath, string Sha256);

public sealed record HlslSourceGraph(
    string RootPath,
    string RootSource,
    string CombinedSha256,
    IReadOnlyList<HlslSourceDependency> Dependencies,
    IReadOnlyList<string> IncludeDirectories)
{
    private static readonly Regex IncludeRegex = new(
        "^\\s*#\\s*include\\s*[\\\"<]([^\\\">]+)[\\\">]",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static HlslSourceGraph Load(string rootPath)
    {
        string fullRoot = Path.GetFullPath(rootPath);
        if (!File.Exists(fullRoot))
            throw new FileNotFoundException("HLSL root source was not found.", fullRoot);
        string rootDirectory = Path.GetDirectoryName(fullRoot)!;
        Dictionary<string, SourceNode> nodes = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> visiting = new(StringComparer.OrdinalIgnoreCase);
        Visit(fullRoot);

        SourceNode root = nodes[fullRoot];
        HlslSourceDependency[] dependencies = nodes.Values
            .Select(node => new HlslSourceDependency(
                NormalizeRelative(rootDirectory, node.FullPath),
                node.FullPath,
                ContentHash.Sha256(Encoding.UTF8.GetBytes(node.Source))))
            .OrderBy(node => node.RelativePath, StringComparer.Ordinal)
            .ToArray();
        StringBuilder identity = new("hlsl-source-graph-v1\n");
        foreach (HlslSourceDependency dependency in dependencies)
            identity.Append(dependency.RelativePath).Append('\0').Append(dependency.Sha256).Append('\n');
        string[] includeDirectories = nodes.Values
            .Select(node => Path.GetDirectoryName(node.FullPath)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new HlslSourceGraph(
            fullRoot,
            root.Source,
            ContentHash.Sha256(identity.ToString()),
            dependencies,
            includeDirectories);

        void Visit(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (nodes.ContainsKey(fullPath))
                return;
            if (!visiting.Add(fullPath))
                throw new InvalidDataException($"Cyclic HLSL include detected at '{fullPath}'.");
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Included HLSL source was not found.", fullPath);
            string source = File.ReadAllText(fullPath);
            foreach (Match match in IncludeRegex.Matches(source))
            {
                string include = match.Groups[1].Value.Trim();
                string resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath)!, include));
                Visit(resolved);
            }
            visiting.Remove(fullPath);
            nodes.Add(fullPath, new SourceNode(fullPath, source));
        }
    }

    private static string NormalizeRelative(string rootDirectory, string path) =>
        Path.GetRelativePath(rootDirectory, path).Replace(Path.DirectorySeparatorChar, '/');

    private sealed record SourceNode(string FullPath, string Source);
}
