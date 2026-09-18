using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace HlslPerf.Crossover
{
    // No Unity dependency: the exact runtime population/predicate/parser are tested by xUnit.
    [StructLayout(LayoutKind.Sequential)]
    public struct Agent
    {
        public float x, y, size, r, g, b, a;
    }

    [Serializable]
    public struct View
    {
        public float x, y, halfX, halfY;
        public int visible;
    }

    [Serializable]
    public sealed class Options
    {
        public string mode = "gpu", output = "result.json", calibration = "", runId = "", pairId = "";
        public int agents = 1000000, seed = 69501203, warmup = 300, frames = 1000;
        public double density = 0.25;
        public string timingApi = "legacy";
        public string gpuProfilerArea = "unchanged";
        public const float WorldX = 120, WorldY = 68, FixedDelta = 1f / 60f, Speed = 0.18f, Radius = 45;
        public static Options Parse(string[] args)
        {
            var o = new Options(); var seen = new HashSet<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string k = args[i];
                // Unity owns single-dash arguments. Reject misspelled benchmark options.
                if (!k.StartsWith("--", StringComparison.Ordinal)) continue;
                if (!seen.Add(k) || i + 1 == args.Length) throw new ArgumentException("Duplicate/missing option: " + k);
                string v = args[++i];
                switch (k)
                {
                    case "--mode": o.mode = v; break;
                    case "--timing-api": o.timingApi = v; break;
                    case "--gpu-profiler-area": o.gpuProfilerArea = v; break;
                    case "--agents": o.agents = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--seed": o.seed = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--density": o.density = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--warmup-frames": o.warmup = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--frames": o.frames = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--output": o.output = v; break;
                    case "--calibration": o.calibration = v; break;
                    case "--run-id": o.runId = v; break;
                    case "--pair-id": o.pairId = v; break;
                    case "--cpu-workers": if (v != "1") throw new ArgumentException("Only CPU-single is implemented."); break;
                    default: throw new ArgumentException("Unknown benchmark option: " + k);
                }
            }
            if (o.mode != "cpu" && o.mode != "gpu" && o.mode != "calibrate" && o.mode != "validation" && o.mode != "timing-diagnostic")
                throw new ArgumentException("Unknown mode");
            if (o.timingApi != "legacy" && o.timingApi != "profiler-recorder") throw new ArgumentException("Unknown timing API");
            if (o.gpuProfilerArea != "unchanged" && o.gpuProfilerArea != "enabled") throw new ArgumentException("Unknown GPU profiler area mode");
            if (o.agents < 1 || o.agents > 4000000 || o.frames < 10 || o.frames > 10000 || o.warmup < 1 ||
                double.IsNaN(o.density) || o.density <= 0 || o.density >= 1 || string.IsNullOrWhiteSpace(o.output))
                throw new ArgumentException("Invalid workload bounds");
            if (o.mode != "calibrate" && o.mode != "timing-diagnostic" && string.IsNullOrWhiteSpace(o.calibration))
                throw new ArgumentException("A frozen --calibration file is required");
            return o;
        }
    }

    public static class TimingContract
    {
        public const int LegacyDelay = 3;
        public static int SubmittedUnityFrame(int availabilityUnityFrame) => availabilityUnityFrame - LegacyDelay;
        public static int MeasuredIndex(int availabilityUnityFrame, int firstMeasuredUnityFrame, int measuredFrames)
        {
            int index = SubmittedUnityFrame(availabilityUnityFrame) - firstMeasuredUnityFrame;
            return index >= 0 && index < measuredFrames ? index : -1;
        }
        // A diagnostic-only nonperiodic block-count code makes frame attribution falsifiable.
        public static int DiagnosticRepetitions(int frame)
        {
            uint x = unchecked((uint)(frame + 1) * 747796405u + 2891336453u);
            x = ((x >> (int)((x >> 28) + 4)) ^ x) * 277803737u;
            return 1 + (int)(((x >> 22) ^ x) & 3);
        }
    }

    public static class Model
    {
        private static float Next(ref uint state)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            return (state >> 8) * (1f / 16777216f);
        }
        public static Agent[] Generate(int count, int seed)
        {
            uint state = unchecked((uint)seed); if (state == 0) state = 1;
            var a = new Agent[count];
            for (int i = 0; i < count; i++) a[i] = new Agent {
                x = (Next(ref state) * 2 - 1) * Options.WorldX,
                y = (Next(ref state) * 2 - 1) * Options.WorldY,
                size = 0.06f + Next(ref state) * 0.10f,
                r = 0.45f + Next(ref state) * 0.55f,
                g = 0.45f + Next(ref state) * 0.55f,
                b = 0.45f + Next(ref state) * 0.55f, a = 1 };
            return a;
        }
        public static View At(int frame, float scale)
        {
            float phase = frame * Options.FixedDelta * Options.Speed;
            return new View { x = (float)Math.Cos(phase) * Options.Radius,
                y = (float)Math.Sin(phase * 0.73f) * Options.Radius * 0.55f,
                halfX = Options.WorldX * scale, halfY = Options.WorldY * scale };
        }
        public static bool Visible(Agent a, View v) =>
            Math.Abs(a.x - v.x) <= v.halfX + a.size && Math.Abs(a.y - v.y) <= v.halfY + a.size;

        // Fused predicate/list construction avoids an artificial second population pass.
        public static int Cull(Agent[] agents, View view, uint[] indices)
        {
            int count = 0;
            for (int i = 0; i < agents.Length; i++) if (Visible(agents[i], view)) indices[count++] = (uint)i;
            return count;
        }
        public static bool EqualSets(uint[] expected, int count, uint[] actual)
        {
            if (count != actual.Length) return false;
            Array.Sort(actual);
            for (int i = 0; i < count; i++) if (expected[i] != actual[i]) return false;
            return true;
        }
        public static int[] CheckFrames(int frames)
        {
            var indices = new int[10];
            for (int i = 0; i < indices.Length; i++) indices[i] = i * (frames - 1) / (indices.Length - 1);
            return indices;
        }
        // Histogram of the minimum view scale that admits each agent; no population changes.
        // 32 evenly spaced calibration frames, 65536 bins over [0,2]; actual full-sequence counts follow.
        public static float Calibrate(Agent[] agents, int frames, double target)
        {
            var histogram = new long[65536];
            for (int f = 0; f < 32; f++)
            {
                View v = At(f * (frames - 1) / 31, 1);
                foreach (Agent a in agents)
                {
                    float scale = Math.Max((Math.Abs(a.x - v.x) - a.size) / Options.WorldX,
                        (Math.Abs(a.y - v.y) - a.size) / Options.WorldY);
                    int bin = Math.Max(0, Math.Min(65535, (int)(scale * 32768)));
                    histogram[bin]++;
                }
            }
            long cumulative = 0; double goal = agents.Length * 32.0 * target;
            for (int b = 0; b < histogram.Length; b++) { cumulative += histogram[b]; if (cumulative >= goal) return (b + 0.5f) / 32768f; }
            throw new InvalidOperationException("Calibration failed");
        }
    }
}
