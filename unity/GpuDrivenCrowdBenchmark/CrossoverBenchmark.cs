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
        public int frameIndex, visibleCount, submissionUnityFrame = -1, availabilityUnityFrame = -1;
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
        public int schema = 2, protocolVersion = 2, processId, width = 1280, height = 720, vsync, targetFrameRate = -1;
        public string runId, pairId, mode, sourceIdentity, calibrationSha256, adapter, driver, graphicsApi, unityVersion, deviceVersion;
        public string buildConfiguration, cpu, os, error = "", gpuTimingStatus = "unavailable";
        public string cpuBaseline = "managed scalar single thread, fused predicate/list, no Burst/Jobs";
        public string visibleCountSource = "frozen full-sequence CPU oracle; selected frames checked on GPU in separate validation";
        public bool correctnessPassed, completed, gpuRecorderSupported, frameTimingEnabled;
        public bool endToEndComparable = false;
        public string renderingThreadingMode;
        public string timingApi, timingLaunchMode;
        public bool supportsGraphicsFence, warmupFenceCompleted, batchFenceCompleted;
        public int firstMeasuredUnityFrame = -1, gpuMappedFrames;
        public long stopwatchFrequency, batchStartTicks, finalSubmissionTicks, lastFalseFencePollTicks, firstTrueFencePollTicks;
        public double batchCompletionMs = -1, batchCompletionMsPerFrame = -1, fenceObservationIntervalMs = -1, fenceObservationBoundMsPerFrame = -1;
        public double finalSubmissionToObservationMs = -1;
        public string endToEndCaveat = "Final-fence batch completion is a throughput/completion candidate, not individual-frame latency; requires external timing, pacing and quiet-pilot gates.";
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
        GpuTimingSource timing;
        GraphicsFence warmupFence, finalFence;
        bool warmupFenceInserted, finalFenceInserted;
        long warmupFenceSubmission, lastFencePoll;
        int mappedFrames;
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
                if (options.mode == "native-diagnostic") { gameObject.AddComponent<NativeTimingDiagnostic>().Initialize(options,culling,drawing); finished=true;return; }
                if (options.mode == "timing-diagnostic")
                {
                    gameObject.AddComponent<TimingDiagnostic>().Initialize(options, culling, drawing);
                    finished = true; return;
                }
                QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; Application.runInBackground = true;
                result = new Result { options = options, runId = options.runId, pairId = options.pairId, mode = options.mode,
                    processId = Process.GetCurrentProcess().Id, sourceIdentity = Identity,
                    adapter = SystemInfo.graphicsDeviceName, driver = RuntimeDriver(), deviceVersion = SystemInfo.graphicsDeviceVersion,
                    renderingThreadingMode = SystemInfo.renderingThreadingMode.ToString(), graphicsApi = SystemInfo.graphicsDeviceType.ToString(), unityVersion = Application.unityVersion,
                    buildConfiguration = Debug.isDebugBuild ? "Development Mono, no script debugging/deep profiling" : "Release",
                    cpu = SystemInfo.processorType, os = SystemInfo.operatingSystem,
                    gpuRecorderSupported = SystemInfo.supportsGpuRecorder, frameTimingEnabled = FrameTimingManager.IsFeatureEnabled(),
                    supportsGraphicsFence = SystemInfo.supportsGraphicsFence, timingApi = options.timingApi,
                    timingLaunchMode = Application.isBatchMode ? "batchmode" : "normal", stopwatchFrequency = Stopwatch.Frequency };
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
                if (!SystemInfo.supportsGraphicsFence) throw new NotSupportedException("GraphicsFence unavailable");
                if (options.gpuProfilerArea == "enabled") Profiler.SetAreaEnabled(ProfilerArea.GPU, true);
                result.samples = new Sample[options.frames];
                timing = new GpuTimingSource(options.timingApi);
                for (int k=0;k<3;k++) if (!timing.Valid(k) || !timing.MarkerValid(k)) throw new InvalidOperationException("Invalid stable GPU marker/recorder");
                for (int f=0;f<options.frames;f++) result.samples[f] = new Sample { frameIndex = f, visibleCount = calibration.views[f].visible };
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
            if (timing == null) return; // Correctness has no profiling or timing samples.
            if (begin) timing.Begin(commands,k); else timing.End(commands,k);
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
            if (timing != null && nextFrame == -1)
            {
                warmupFence = commands.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
                warmupFenceInserted = true;
            }
            if (timing != null && measuredFrame == options.frames - 1)
            {
                finalFence = commands.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
                finalFenceInserted = true;
            }
            Graphics.ExecuteCommandBuffer(commands);
            long end = Stopwatch.GetTimestamp();
            if (timing != null && nextFrame == -1) warmupFenceSubmission = end;
            if (finalFenceInserted && measuredFrame == options.frames - 1) { result.finalSubmissionTicks = end; lastFencePoll = end; }
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
                long now = Stopwatch.GetTimestamp();
                if (nextFrame == 0 && !result.warmupFenceCompleted)
                {
                    if (!warmupFenceInserted) throw new InvalidOperationException("Missing warmup fence");
                    if (!warmupFence.passed)
                    {
                        if ((now - warmupFenceSubmission) * TickMs > 30000) throw new InvalidOperationException("Warmup fence timeout");
                        return;
                    }
                    result.warmupFenceCompleted = true;
                    lastUpdate = now;
                    return; // Start measured CPU work on the next Update, after warmup completion.
                }
                PollMappedTiming();
                if (nextFrame >= options.frames)
                {
                    if (!finalFenceInserted) throw new InvalidOperationException("Missing final batch fence");
                    if (!result.batchFenceCompleted)
                    {
                        if (finalFence.passed)
                        {
                            long observed = Stopwatch.GetTimestamp();
                            result.batchFenceCompleted = true; result.firstTrueFencePollTicks = observed;
                            result.batchCompletionMs = (observed - result.batchStartTicks) * TickMs;
                            result.batchCompletionMsPerFrame = result.batchCompletionMs / options.frames;
                            result.fenceObservationIntervalMs = (observed - lastFencePoll) * TickMs;
                            long boundStart = result.lastFalseFencePollTicks > 0 ? result.lastFalseFencePollTicks : result.finalSubmissionTicks;
                            result.fenceObservationBoundMsPerFrame = (observed - boundStart) * TickMs / options.frames;
                            result.finalSubmissionToObservationMs = (observed - result.finalSubmissionTicks) * TickMs;
                            result.gc0Collections = GC.CollectionCount(0) - initialGc;
                        }
                        else { result.lastFalseFencePollTicks = Stopwatch.GetTimestamp(); lastFencePoll = result.lastFalseFencePollTicks; }
                        if ((now - result.finalSubmissionTicks) * TickMs > 30000) throw new InvalidOperationException("Batch fence timeout");
                    }
                    if (mappedFrames == options.frames && result.batchFenceCompleted) FinishTiming();
                    else if (++drainFrames > 120) throw new InvalidOperationException("Incomplete GPU samples during nonblocking drain");
                    return;
                }
                int index = nextFrame < 0 ? (nextFrame + options.warmup) % options.frames : nextFrame;
                if (nextFrame == 0)
                {
                    initialGc = GC.CollectionCount(0); result.firstMeasuredUnityFrame = Time.frameCount;
                    result.batchStartTicks = Stopwatch.GetTimestamp();
                }
                var sample = nextFrame < 0 ? null : result.samples[nextFrame];
                if (sample != null)
                {
                    sample.updateIntervalMs = (now - lastUpdate) * TickMs;
                    sample.submissionUnityFrame = Time.frameCount;
                }
                lastUpdate = now;
                Draw(options.mode, calibration.views[index], nextFrame, sample);
                nextFrame++;
            }
            catch (Exception e) { Fail(e); }
        }

        void PollMappedTiming()
        {
            if (result.firstMeasuredUnityFrame < 0) return;
            int index = TimingContract.MeasuredIndex(Time.frameCount, result.firstMeasuredUnityFrame, options.frames);
            if (index < 0) return;
            var s = result.samples[index];
            if (s.submissionUnityFrame != TimingContract.SubmittedUnityFrame(Time.frameCount) || s.availabilityUnityFrame >= 0 || index != mappedFrames)
                throw new InvalidOperationException("GPU timing frame attribution mismatch");
            for (int k=0;k<3;k++)
            {
                if (options.mode == "cpu" && k == 1) continue;
                long count = timing.Blocks(k), ns = timing.Nanoseconds(k);
                if (count != 1 || ns <= 0) throw new InvalidOperationException("Missing/ambiguous stable GPU timing at documented delay for frame " + index);
                if (k==0) s.gpuRangeMs=ns/1e6; else if(k==1) s.gpuCullMs=ns/1e6; else s.gpuDrawMs=ns/1e6;
            }
            s.availabilityUnityFrame=Time.frameCount; mappedFrames++; result.gpuMappedFrames=mappedFrames;
        }

        void FinishTiming()
        {
            result.gpuTimingStatus = "complete: stable " + options.timingApi + "; availability frame minus three";
            // The runner separately gates correctness, diagnostic proof, pacing, load and drift.
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
                string text;
                using (var stream = new FileStream(Application.consoleLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream)) text = reader.ReadToEnd();
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
            timing?.Dispose(); agents?.Release(); visible?.Release(); args?.Release(); commands?.Release();
            if (target != null) target.Release(); if (material != null) Destroy(material);
        }
    }
}
