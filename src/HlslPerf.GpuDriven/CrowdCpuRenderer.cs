using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HlslPerf.GpuDriven;

public sealed record CrowdCpuTiming(double RenderMilliseconds, int ConfiguredWorkers, int ParticipatingThreads, int PeakWorkers);

/// <summary>A conventional CPU implementation of the caller's RGBA contract.
/// Immutable eligibility, motion origins, colors and background are prepared once;
/// independent frames splat visible agents directly into their own pixel ranges.</summary>
public sealed class CrowdCpuRenderer : IDisposable
{
    public const long DefaultMemoryBudget = 2L * 1024 * 1024 * 1024;
    private readonly CrowdApplicationScene scene;
    private PreparedAgent[] eligible;
    private byte[] background;
    private byte[] output;
    private bool disposed;
    private int running;
    public int Workers { get; }
    public int EligibleCount => eligible.Length;
    public long LogicalBytes { get; }
    public long MemoryBudgetBytes { get; }
    public ReadOnlyMemory<byte> Data => output;
    public static int MaximumWorkers(CrowdApplicationScene scene) => Math.Min(scene.Frames, Environment.ProcessorCount);

    private readonly record struct PreparedAgent(uint Seed, uint OriginX, uint OriginY, uint Color);

    public CrowdCpuRenderer(CrowdApplicationScene scene, byte[] input, int workers,
        long maximumBytes = DefaultMemoryBudget)
    {
        if (scene.AgentCount is < 1 or > 16_777_216 || input.Length != checked(scene.AgentCount * 4) ||
            scene.Width is < 1 or > 16_384 || scene.Height is < 1 or > 16_384 || scene.Frames is < 1 or > 24 ||
            (scene.VisibilityMask & (scene.VisibilityMask + 1)) != 0 ||
            checked(((scene.Width + 15) / 16) * ((scene.Height + 15) / 16)) > 1024 ||
            workers < 1 || workers > MaximumWorkers(scene)) throw new ArgumentException("Invalid CPU scene or worker budget.");
        this.scene = scene; Workers = workers; MemoryBudgetBytes = maximumBytes;
        ReadOnlySpan<uint> seeds = MemoryMarshal.Cast<byte, uint>(input);
        int count = 0;
        foreach (uint seed in seeds) if ((Hash(seed ^ 0xd1b54a35) & scene.VisibilityMask) == 0) count++;
        int frameBytes = checked(scene.Width * scene.Height * 4);
        int atlasBytes = checked(frameBytes * scene.Frames);
        LogicalBytes = checked((long)input.Length + 16L * count + frameBytes + atlasBytes);
        if (maximumBytes <= 0 || LogicalBytes > maximumBytes)
            throw new InvalidOperationException($"CPU logical storage {LogicalBytes} exceeds {maximumBytes}; no allocation or worker reduction was substituted.");
        eligible = new PreparedAgent[count]; background = new byte[frameBytes]; output = new byte[atlasBytes];
        int index = 0;
        foreach (uint seed in seeds)
            if ((Hash(seed ^ 0xd1b54a35) & scene.VisibilityMask) == 0)
                eligible[index++] = new(seed, Hash(seed ^ 0x9e3779b9) % (uint)(scene.Width * 4),
                    Hash(seed ^ 0x85ebca6b) % (uint)scene.Height, Hash(seed ^ 0xc2b2ae35));
        Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(background.AsSpan());
        for (int y = 0; y < scene.Height; y++)
        for (int x = 0; x < scene.Width; x++)
        {
            uint r = 3 + (uint)(y * 5 / scene.Height), g = 7 + (uint)(y * 7 / scene.Height), b = 18 + (uint)(y * 14 / scene.Height);
            if (x % 48 == 0 || y % 48 == 0) { r += 4; g += 10; b += 14; }
            if ((Hash((uint)(y * scene.Width + x) + 0xa511e9b3) & 2047) < 3) { r += 28; g += 36; b += 48; }
            pixels[y * scene.Width + x] = r | g << 8 | b << 16 | 0xff000000;
        }
    }

    public CrowdCpuTiming Render(uint firstFrame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (firstFrame > 100000) throw new ArgumentOutOfRangeException(nameof(firstFrame));
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("Concurrent requests require separate renderer instances.");
        long start = Stopwatch.GetTimestamp();
        try
        {
            int active = 0, peak = 0;
            var threads = new ConcurrentDictionary<int, byte>();
            Parallel.For(0, Workers, new ParallelOptions { MaxDegreeOfParallelism = Workers }, worker =>
            {
                threads.TryAdd(Environment.CurrentManagedThreadId, 0);
                int current = Interlocked.Increment(ref active), observed;
                do { observed = Volatile.Read(ref peak); }
                while (current > observed && Interlocked.CompareExchange(ref peak, current, observed) != observed);
                try
                {
                    for (int frame = worker; frame < scene.Frames; frame += Workers) RenderFrame(frame, firstFrame + (uint)frame);
                }
                finally { Interlocked.Decrement(ref active); }
            });
            return new(Stopwatch.GetElapsedTime(start).TotalMilliseconds, Workers, threads.Count, peak);
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private uint X(PreparedAgent agent, uint frame)
    {
        uint world = (uint)scene.Width * 4, camera = frame * 7 % world;
        return ((agent.OriginX + frame * (1 + ((agent.Seed >> 3) & 3))) % world + world - camera) % world;
    }
    private uint Y(PreparedAgent agent, uint frame)
    {
        uint h = (uint)scene.Height, delta = frame * (1 + ((agent.Seed >> 7) & 1));
        return (agent.Seed & 0x20) == 0 ? (agent.OriginY + delta) % h : (agent.OriginY + h - delta % h) % h;
    }

    private void RenderFrame(int localFrame, uint frame)
    {
        Span<byte> bytes = output.AsSpan(localFrame * background.Length, background.Length);
        background.CopyTo(bytes);
        Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(bytes);
        foreach (PreparedAgent agent in eligible)
        {
            uint positionX = X(agent, frame);
            if (positionX >= scene.Width) continue;
            int cx = (int)positionX, cy = (int)Y(agent, frame);
            int radius = 1 + (int)(agent.Color & 1), kind = (int)((agent.Color >> 8) % 3);
            for (int y = Math.Max(0, cy - radius); y <= Math.Min(scene.Height - 1, cy + radius); y++)
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(scene.Width - 1, cx + radius); x++)
            {
                uint power = (uint)((radius + 1 - Math.Max(Math.Abs(x - cx), Math.Abs(y - cy))) * 52);
                uint addR, addG, addB;
                if (kind == 0) { addR = power / 5; addG = power; addB = power; }
                else if (kind == 1) { addR = power; addG = power / 4; addB = power; }
                else { addR = power; addG = power / 2; addB = power / 8; }
                int at = y * scene.Width + x;
                uint packed = pixels[at];
                uint r = Math.Min(255, (packed & 255) + addR);
                uint g = Math.Min(255, ((packed >> 8) & 255) + addG);
                uint b = Math.Min(255, ((packed >> 16) & 255) + addB);
                pixels[at] = r | g << 8 | b << 16 | 0xff000000;
            }
        }
    }

    // Diagnostics are outside request timing. CPU direct splatting needs no tile list.
    public uint[] VisibleSeeds(uint frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return eligible.Where(agent => X(agent, frame) < scene.Width).Select(agent => agent.Seed).ToArray();
    }
    public void PoisonOutput(byte poison) => Array.Fill(output, poison);
    private static uint Hash(uint x)
    { x ^= x >> 16; x = unchecked(x * 0x7feb352d); x ^= x >> 15; x = unchecked(x * 0x846ca68b); return x ^ (x >> 16); }
    public void Dispose() { disposed = true; eligible = []; background = []; output = []; }
}
