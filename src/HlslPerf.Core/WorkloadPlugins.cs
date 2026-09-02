using System.Reflection;
using System.Runtime.Loader;

namespace HlslPerf.Core;

/// <summary>
/// Public extension point implemented by trusted external workload assemblies.
/// Providers are instantiated through a public parameterless constructor.
/// </summary>
public interface IKernelWorkloadProvider
{
    IReadOnlyCollection<string> WorkloadIds { get; }
    IKernelWorkload Create(string workloadId);
}

public sealed class WorkloadCatalog
{
    private readonly Dictionary<string, ProviderEntry> providers = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> WorkloadIds => providers.Keys.Order(StringComparer.Ordinal).ToArray();

    public void Register(IKernelWorkloadProvider provider, string? origin = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        string providerOrigin = string.IsNullOrWhiteSpace(origin)
            ? provider.GetType().Assembly.GetName().Name ?? provider.GetType().FullName ?? "unknown"
            : origin;
        if (provider.WorkloadIds.Count == 0)
            throw new InvalidDataException($"Workload provider '{providerOrigin}' exports no ids.");
        foreach (string id in provider.WorkloadIds)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidDataException($"Workload provider '{providerOrigin}' exports an empty id.");
            if (providers.TryGetValue(id, out ProviderEntry? existing))
                throw new InvalidDataException(
                    $"Workload id '{id}' is exported by both '{existing.Origin}' and '{providerOrigin}'.");
            providers.Add(id, new ProviderEntry(provider, providerOrigin));
        }
    }

    public IKernelWorkload Resolve(string workloadId)
    {
        if (!providers.TryGetValue(workloadId, out ProviderEntry? entry))
            throw new InvalidDataException(
                $"Unknown workload '{workloadId}'. Available workloads: {string.Join(", ", WorkloadIds)}.");
        IKernelWorkload workload = entry.Provider.Create(workloadId)
            ?? throw new InvalidDataException($"Provider '{entry.Origin}' returned null for '{workloadId}'.");
        if (!string.Equals(workload.Id, workloadId, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Provider '{entry.Origin}' returned workload id '{workload.Id}' for requested id '{workloadId}'.");
        return workload;
    }

    public IKernelWorkload Resolve(TuningManifest manifest)
    {
        string id = manifest.SchemaVersion == "1.0"
            ? manifest.Correctness.Kind
            : manifest.Workload?.Id ?? throw new InvalidDataException("A schema 2.0+ workload is required.");
        if (id == "cross-candidate-sha256")
            id = "uint-mix-v1";
        return Resolve(id);
    }

    private sealed record ProviderEntry(IKernelWorkloadProvider Provider, string Origin);
}

public static class WorkloadPluginLoader
{
    public static IReadOnlyList<IKernelWorkloadProvider> Load(string assemblyPath) =>
        LoadCore(assemblyPath, requireProvider: true);

    /// <summary>
    /// Inspects one assembly discovered during a directory scan. Ordinary managed
    /// dependency assemblies are ignored when they export no workload provider.
    /// </summary>
    public static IReadOnlyList<IKernelWorkloadProvider> LoadIfPresent(string assemblyPath)
    {
        try
        {
            return LoadCore(assemblyPath, requireProvider: false);
        }
        catch (BadImageFormatException)
        {
            // Native dependencies commonly sit beside managed plugin assemblies.
            return [];
        }
    }

    private static IReadOnlyList<IKernelWorkloadProvider> LoadCore(string assemblyPath, bool requireProvider)
    {
        string fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Workload plugin assembly was not found.", fullPath);

        PluginLoadContext context = new(fullPath);
        Assembly assembly = context.LoadFromAssemblyPath(fullPath);
        Type providerType = typeof(IKernelWorkloadProvider);
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            string details = string.Join(
                Environment.NewLine,
                exception.LoaderExceptions.Where(value => value is not null).Select(value => value!.Message));
            throw new InvalidDataException($"Could not inspect workload plugin '{fullPath}': {details}", exception);
        }

        List<IKernelWorkloadProvider> providers = [];
        foreach (Type type in types.Where(type =>
                     type.IsPublic && !type.IsAbstract && providerType.IsAssignableFrom(type)))
        {
            if (type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidDataException(
                    $"Workload provider '{type.FullName}' requires a public parameterless constructor.");
            providers.Add((IKernelWorkloadProvider)Activator.CreateInstance(type)!);
        }
        if (requireProvider && providers.Count == 0)
            throw new InvalidDataException(
                $"Assembly '{fullPath}' contains no public {nameof(IKernelWorkloadProvider)} implementation.");
        return providers;
    }

    public static IReadOnlyList<string> DiscoverAssemblies(string directory)
    {
        string fullDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDirectory))
            throw new DirectoryNotFoundException($"Workload plugin directory was not found: {fullDirectory}");
        return Directory.EnumerateFiles(fullDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;

        public PluginLoadContext(string pluginPath)
            : base($"hlslperf-plugin:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: false) =>
            resolver = new AssemblyDependencyResolver(pluginPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Share the SDK contract from the default context so interface identity
            // remains stable even when a plugin output folder contains its own copy.
            if (string.Equals(
                    assemblyName.Name,
                    typeof(IKernelWorkloadProvider).Assembly.GetName().Name,
                    StringComparison.OrdinalIgnoreCase))
                return null;
            string? path = resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
