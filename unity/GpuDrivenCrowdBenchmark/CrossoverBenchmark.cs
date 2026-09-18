using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace HlslPerf.Crossover
{
    [Serializable] public sealed class Calibration
    {
        public int schema = 1, seed, agentCount, frames;
        public string sourceIdentity;
        public double targetVisibility, actualVisibilityMean;
        public float scale, fixedDelta = Options.FixedDelta, radius = Options.Radius, speed = Options.Speed;
        public float worldX = Options.WorldX, worldY = Options.WorldY;
        public View[] views;
    }
    [Serializable] public sealed class Sample
    {
        public int frameIndex, visibleCount;
        public double cpuCullAndListMs, cpuUploadMs, cpuSubmitMs, cpuTotalMs, updateIntervalMs;
        // -1 means unavailable, never zero-filled missing GPU measurements.
        public double gpuCullMs = -1, gpuDrawMs = -1, gpuRangeMs = -1;
    }
    [Serializable] public sealed class Check
    {
        public int frameIndex, cpuCount, gpuCount;
        public bool setEqual, imageEqual, nonEmptyImage;
        public string cpuImageSha256, gpuImageSha256;
    }
    [Serializable] public sealed class Result
    {
        public int schema = 1, processId, width = 1280, height = 720, vsync, targetFrameRate = -1;
        public string runId, pairId, mode, sourceIdentity, calibrationSha256, adapter, driver, graphicsApi, unityVersion, deviceVersion;
        public string buildConfiguration, cpu, os, error = "", gpuTimingStatus = "unavailable";
        public string cpuBaseline = "managed scalar single thread, fused predicate/list, no Burst/Jobs";
        public string visibleCountSource = "frozen full-sequence CPU oracle; selected frames checked on GPU in separate validation";
        public bool correctnessPassed, completed, gpuRecorderSupported, frameTimingEnabled;
        public bool endToEndComparable = false;
        public string endToEndCaveat = "Update intervals include engine scheduling and are diagnostic only; no validated GPU completion latency metric.";
        public Options options;
        public Sample[] samples = Array.Empty<Sample>();
        public Check[] checks = Array.Empty<Check>();
        public double actualVisibilityMean;
        public int gc0Collections;
    }

    public sealed class CrossoverBenchmark : MonoBehaviour
    {
        public ComputeShader culling;
        public Shader drawing;
        Options options; Calibration calibration; Result result;
        Agent[] population; uint[] cpuIds;
        ComputeBuffer agents, visible, args;
        Material material; RenderTexture target; CommandBuffer commands;
        CustomSampler[,] samplers; Recorder[,] recorders;
        readonly List<int> pending = new List<int>();
        int nextFrame, drainFrames, initialGc; long lastUpdate;
        bool measuring, finished;
        static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
        static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static string Identity => Resources.Load<TextAsset>("crossover-source-identity").text.Trim();

        void Start()
        {
            try
            {
                options = Options.Parse(Environment.GetCommandLineArgs());
                QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; Application.runInBackground = true;
                result = new Result { options = options, runId = options.runId, pairId = options.pairId, mode = options.mode,
                    processId = Process.GetCurrentProcess().Id, sourceIdentity = Identity,
                    adapter = SystemInfo.graphicsDeviceName, driver = RuntimeDriver(), deviceVersion = SystemInfo.graphicsDeviceVersion,
                    graphicsApi = SystemInfo.graphicsDeviceType.ToString(), unityVersion = Application.unityVersion,
                    buildConfiguration = Debug.isDebugBuild ? "Development Mono, no script debugging/deep profiling" : "Release",
                    cpu = SystemInfo.processorType, os = SystemInfo.operatingSystem,
                    gpuRecorderSupported = SystemInfo.supportsGpuRecorder, frameTimingEnabled = FrameTimingManager.IsFeatureEnabled() };
                population = Model.Generate(options.agents, options.seed); cpuIds = new uint[options.agents];
                if (options.mode == "calibrate") { WriteCalibration(); return; }
                byte[] calibrationBytes = File.ReadAllBytes(options.calibration);
                calibration = JsonUtility.FromJson<Calibration>(System.Text.Encoding.UTF8.GetString(calibrationBytes));
                if (calibration.schema != 1 || calibration.seed != options.seed || calibration.agentCount != options.agents ||
                    calibration.frames != options.frames || calibration.targetVisibility != options.density || calibration.sourceIdentity != Identity ||
                    calibration.views.Length != options.frames) throw new InvalidOperationException("Calibration identity mismatch");
                result.calibrationSha256 = Hash(calibrationBytes); result.actualVisibilityMean = calibration.actualVisibilityMean;
                Allocate();
                if (options.mode == "validation") { StartCoroutine(ValidateSafe()); return; }
                if (!SystemInfo.supportsGpuRecorder) throw new NotSupportedException("GPU Recorder unavailable: stop before interpreting performance.");
                result.samples = new Sample[options.frames];
                samplers = new CustomSampler[options.frames, 3]; recorders = new Recorder[options.frames, 3];
                for (int f = 0; f < options.frames; f++)
                {
                    result.samples[f] = new Sample { frameIndex = f, visibleCount = calibration.views[f].visible };
                    for (int k = 0; k < 3; k++)
                    {
                        samplers[f, k] = CustomSampler.Create("Crossover/" + f + "/" + k, true);
                        recorders[f, k] = samplers[f, k].GetRecorder(); recorders[f, k].enabled = true;
                    }
                }
                pending.Capacity = options.frames;
                nextFrame = -options.warmup; measuring = true;
            }
            catch (Exception e) { Fail(e); }
        }

        void Allocate()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new NotSupportedException("A rendering GPU is required; do not use -nographics.");
            agents = new ComputeBuffer(options.agents, 28, ComputeBufferType.Structured); agents.SetData(population);
            visible = new ComputeBuffer(options.agents, 4, ComputeBufferType.Append);
            args = new ComputeBuffer(1, 16, ComputeBufferType.IndirectArguments); args.SetData(new uint[] {6, 0, 0, 0});
            material = new Material(drawing); material.SetBuffer("_Agents", agents); material.SetBuffer("_VisibleAgents", visible);
            culling.SetBuffer(0, "_Agents", agents); culling.SetBuffer(0, "_VisibleAgents", visible); culling.SetInt("_AgentCount", options.agents);
            var descriptor = new RenderTextureDescriptor(1280, 720) {
                graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,
                depthStencilFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.D32_SFloat,
                msaaSamples = 1, sRGB = false };
            target = new RenderTexture(descriptor); target.Create();
            if (!target.IsCreated()) throw new InvalidOperationException("Render target allocation failed");
            commands = new CommandBuffer { name = "Crossover offscreen" };
        }

        void WriteCalibration()
        {
            float scale = Model.Calibrate(population, options.frames, options.density);
            var c = new Calibration { seed = options.seed, agentCount = options.agents, frames = options.frames,
                targetVisibility = options.density, scale = scale, sourceIdentity = Identity, views = new View[options.frames] };
            long count = 0;
            for (int f = 0; f < options.frames; f++) { var v = Model.At(f, scale); v.visible = Model.Cull(population, v, cpuIds); c.views[f] = v; count += v.visible; }
            c.actualVisibilityMean = count / (double)(options.agents * (long)options.frames);
            Write(options.output, JsonUtility.ToJson(c, true)); finished = true; Application.Quit(0);
        }

        void Mark(int f, int k, bool begin)
        {
            if (f < 0) return;
            if (begin) commands.BeginSample(samplers[f,k]); else commands.EndSample(samplers[f,k]);
        }

        void Draw(string mode, View view, int measuredFrame, Sample sample)
        {
            long start = Stopwatch.GetTimestamp(); int count = 0;
            if (mode == "cpu") count = Model.Cull(population, view, cpuIds);
            long culled = Stopwatch.GetTimestamp();
            if (mode == "cpu" && count > 0) visible.SetData(cpuIds, 0, 0, count);
            long uploaded = Stopwatch.GetTimestamp();
            if (mode == "cpu" && count != view.visible) throw new InvalidOperationException("CPU/calibration count disagreement");
            commands.Clear();
            commands.SetRenderTarget(target);
            // Unity converts this logical far depth for reversed-Z backends itself.
            commands.ClearRenderTarget(true, true, Color.black, 1f);
            commands.SetViewport(new Rect(0, 0, 1280, 720));
            var rect = new Vector4(view.x, view.y, view.halfX, view.halfY);
            material.SetVector("_ViewRect", rect);
            Mark(measuredFrame, 0, true);
            if (mode == "gpu")
            {
                Mark(measuredFrame, 1, true);
                commands.SetBufferCounterValue(visible, 0);
                commands.SetComputeVectorParam(culling, "_ViewRect", rect);
                commands.DispatchCompute(culling, 0, (options.agents + 255) / 256, 1, 1);
                commands.CopyCounterValue(visible, args, 4);
                Mark(measuredFrame, 1, false);
            }
            Mark(measuredFrame, 2, true);
            if (mode == "gpu") commands.DrawProceduralIndirect(Matrix4x4.identity, material, 0, MeshTopology.Triangles, args);
            else if (count > 0) commands.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 6, count);
            Mark(measuredFrame, 2, false); Mark(measuredFrame, 0, false);
            Graphics.ExecuteCommandBuffer(commands);
            long end = Stopwatch.GetTimestamp();
            if (sample != null)
            {
                sample.cpuCullAndListMs = (culled - start) * TickMs;
                sample.cpuUploadMs = (uploaded - culled) * TickMs;
                sample.cpuSubmitMs = (end - uploaded) * TickMs; sample.cpuTotalMs = (end - start) * TickMs;
            }
        }

        void Update()
        {
            if (!measuring || finished) return;
            try
            {
                Poll();
                if (nextFrame >= options.frames)
                {
                    if (pending.Count == 0 || ++drainFrames > 120) FinishTiming();
                    return;
                }
                int index = nextFrame < 0 ? (nextFrame + options.warmup) % options.frames : nextFrame;
                if (nextFrame == 0) initialGc = GC.CollectionCount(0);
                long now = Stopwatch.GetTimestamp();
                var sample = nextFrame < 0 ? null : result.samples[nextFrame];
                if (sample != null) sample.updateIntervalMs = lastUpdate == 0 ? -1 : (now - lastUpdate) * TickMs;
                lastUpdate = now;
                Draw(options.mode, calibration.views[index], nextFrame, sample);
                if (nextFrame >= 0) pending.Add(nextFrame);
                nextFrame++;
            }
            catch (Exception e) { Fail(e); }
        }

        void Poll()
        {
            // Each sample name is issued once: no assumed three-frame mapping and no reused stale query.
            // Never wait or force GPU completion. If a sample disappears/misses the polling window, fail closed.
            for (int p = pending.Count - 1; p >= 0; p--)
            {
                int f = pending[p]; Sample s = result.samples[f];
                for (int k = 0; k < 3; k++)
                {
                    var r = recorders[f,k];
                    if (r.gpuSampleBlockCount > 1) throw new InvalidOperationException("GPU sample executed more than once");
                    if (r.gpuSampleBlockCount == 1 && r.gpuElapsedNanoseconds > 0)
                    {
                        double ms = r.gpuElapsedNanoseconds / 1e6;
                        if (k == 0) s.gpuRangeMs = ms; else if (k == 1) s.gpuCullMs = ms; else s.gpuDrawMs = ms;
                    }
                }
                if (s.gpuRangeMs >= 0 && s.gpuDrawMs >= 0 && (options.mode == "cpu" || s.gpuCullMs >= 0)) pending.RemoveAt(p);
                else if (nextFrame - f > 120) throw new InvalidOperationException("GPU query unresolved after 120 frames; no forced synchronization fallback");
            }
        }

        void FinishTiming()
        {
            if (pending.Count != 0) throw new InvalidOperationException("Incomplete delayed GPU samples");
            result.gpuTimingStatus = "complete: distinct per-frame GPU Recorder markers";
            result.gc0Collections = GC.CollectionCount(0) - initialGc;
            result.completed = true; Finish(0);
        }

        IEnumerator ValidateSafe()
        {
            var iterator = Validate();
            while (true)
            {
                bool more;
                try { more = iterator.MoveNext(); }
                catch (Exception e) { Fail(e); yield break; }
                if (!more) yield break;
                yield return iterator.Current;
            }
        }
        IEnumerator Validate()
        {
            var checks = new List<Check>();
            foreach (int frame in Model.CheckFrames(options.frames))
            {
                var v = calibration.views[frame];
                Draw("cpu", v, -1, null);
                // Correctness-only synchronous readbacks, in a distinct process with no timing samples.
                byte[] cpuPixels = ReadImage(); int expectedCount = Model.Cull(population, v, cpuIds);
                Draw("gpu", v, -1, null);
                var argumentData = new uint[4]; args.GetData(argumentData);
                int gpuCount = checked((int)argumentData[1]);
                if (gpuCount > options.agents) { Fail(new InvalidOperationException("GPU count exceeds population")); yield break; }
                var ids = new uint[gpuCount]; if (gpuCount > 0) visible.GetData(ids, 0, 0, gpuCount);
                byte[] gpuPixels = ReadImage();
                var check = new Check { frameIndex = frame, cpuCount = expectedCount, gpuCount = gpuCount,
                    setEqual = Model.EqualSets(cpuIds, expectedCount, ids), cpuImageSha256 = Hash(cpuPixels), gpuImageSha256 = Hash(gpuPixels),
                    nonEmptyImage = HasColor(cpuPixels) };
                check.imageEqual = check.cpuImageSha256 == check.gpuImageSha256;
                checks.Add(check); result.checks = checks.ToArray();
                if (!check.setEqual || !check.imageEqual || !check.nonEmptyImage)
                { Fail(new InvalidOperationException("CPU/GPU set or image disagreement at frame " + frame)); yield break; }
                yield return null;
            }
            result.correctnessPassed = true; result.completed = true; Finish(0);
        }
        static bool HasColor(byte[] pixels) { for (int i = 0; i < pixels.Length; i += 4) if (pixels[i] != 0 || pixels[i+1] != 0 || pixels[i+2] != 0) return true; return false; }
        static string RuntimeDriver()
        {
            // Unity's graphicsDeviceVersion describes the API, not the installed driver.
            // The player writes its runtime-selected adapter driver before scene initialization.
            try {
                string text = File.ReadAllText(Application.consoleLogPath);
                var match = System.Text.RegularExpressions.Regex.Match(text, @"(?m)^\s*Driver:\s*([^\r\n]+)");
                return match.Success ? match.Groups[1].Value.Trim() : "unavailable (see external launch snapshot)";
            } catch (IOException) { return "unavailable (see external launch snapshot)"; }
        }
        byte[] ReadImage()
        {
            var previous = RenderTexture.active; RenderTexture.active = target;
            var texture = new Texture2D(1280, 720, TextureFormat.RGBA32, false, true);
            texture.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); texture.Apply();
            byte[] bytes = texture.GetRawTextureData<byte>().ToArray(); Destroy(texture); RenderTexture.active = previous;
            return bytes;
        }
        static void Write(string path, string json) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))); File.WriteAllText(path, json); }
        void Fail(Exception e)
        {
            Debug.LogException(e);
            if (result == null) result = new Result(); result.error = e.ToString();
            Finish(2);
        }
        void Finish(int code)
        {
            finished = true; measuring = false;
            Write(options == null ? "argument-error.json" : options.output, JsonUtility.ToJson(result, true));
            Application.Quit(code);
        }
        void OnDestroy()
        {
            agents?.Release(); visible?.Release(); args?.Release(); commands?.Release();
            if (target != null) target.Release(); if (material != null) Destroy(material);
        }
    }
}
