using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Seb.Fluid.Simulation;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace HlslPerf.FluidBenchmark
{
    [DefaultExecutionOrder(10000)]
    public sealed class FluidBenchmarkRunner : MonoBehaviour
    {
        private FluidSim sim;
        private int firstFrame = -1, lastFrame;
        private long previousTick;
        private bool finished;
        private string fatalLog;
        private readonly List<string> observations = new List<string>();
        private readonly List<string> frameTimings = new List<string>();
        private readonly HashSet<ulong> timingIds = new HashSet<ulong>();
        private readonly FrameTiming[] timings = new FrameTiming[16];

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            try
            {
                var settings = FluidBenchmarkSettings.Parse(Environment.GetCommandLineArgs());
                if (settings == null) return;
                FluidBenchmarkSettings.Activate(settings);
                SceneManager.sceneLoaded += ConfigureScene;
            }
            catch (Exception error)
            {
                Debug.LogError("Fluid benchmark configuration failed: " + error);
                SceneManager.sceneLoaded += StopScene;
                Application.Quit(2);
            }
        }

        private static void StopScene(Scene scene, LoadSceneMode mode)
        {
            foreach (var script in Object.FindObjectsOfType<MonoBehaviour>()) script.enabled = false;
        }

        private static void ConfigureScene(Scene scene, LoadSceneMode mode)
        {
            SceneManager.sceneLoaded -= ConfigureScene;
            var settings = FluidBenchmarkSettings.Active;
            Directory.CreateDirectory(settings.outputDirectory);
            try
            {
                var sims = Object.FindObjectsOfType<FluidSim>();
                if (sims.Length != 1 || scene.name != "Fluid ScreenSpace 2" || sims[0].renderToTex3D)
                    throw new InvalidOperationException("Use the single-simulation Fluid ScreenSpace 2 scene supplied by the installer.");
                if (Resources.Load<TextAsset>("FluidBenchmarkProvenance") == null)
                    throw new InvalidOperationException("Missing prepared source provenance.");
                long projectedCount = 0;
                foreach (var region in sims[0].spawner.spawnRegions)
                {
                    int axis = region.CalculateParticleCountPerAxis(settings.spawnDensity);
                    if (axis < 2) throw new ArgumentOutOfRangeException("spawn density", "Each spawn region must have at least two points per axis.");
                    projectedCount += (long)axis * axis * axis;
                    if (projectedCount > ScanBackends.MaximumCount)
                        throw new ArgumentOutOfRangeException("spawn density", "Particle count exceeds the benchmark dispatch contract.");
                }
                // Validation is synchronous and happens before scene Start/Update.
                // Readbacks/allocations here are never part of measured frames.
                var validation = FluidScanValidation.Run(settings.arm);
                File.WriteAllText(Path.Combine(settings.outputDirectory, "correctness.json"), JsonUtility.ToJson(validation, true));
                if (validation.status != "passed") throw new InvalidOperationException(validation.failure);
                if (settings.validateOnly)
                {
                    StopScene(scene, mode); Application.Quit(0); return;
                }
                var sim = sims[0];
                sim.spawner.particleSpawnDensity = settings.spawnDensity;
                sim.normalTimeScale = 1;
                UnityEngine.Random.InitState(settings.seed);
                QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
                Screen.SetResolution(1920, 1080, FullScreenMode.Windowed);
                if (Camera.main != null)
                    foreach (var script in Camera.main.GetComponents<MonoBehaviour>()) script.enabled = false;
                FluidGpuSamples.Enable();
                var host = new GameObject("Fluid Benchmark Collector");
                host.AddComponent<FluidBenchmarkRunner>().sim = sim;
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(settings.outputDirectory, "failure.txt"), error.ToString());
                Debug.LogError(error); StopScene(scene, mode); Application.Quit(2);
            }
        }

        private void LateUpdate()
        {
            if (finished) return;
            try
            {
                if (fatalLog != null) throw new InvalidOperationException(fatalLog);
                if (sim.positionBuffer != null) Collect();
            }
            catch (Exception error)
            {
                finished = true; sim.enabled = false;
                File.WriteAllText(Path.Combine(FluidBenchmarkSettings.Active.outputDirectory, "failure.txt"), error.ToString());
                Debug.LogError(error); Application.Quit(2);
            }
        }

        private void OnEnable() { Application.logMessageReceived += CaptureError; }
        private void OnDisable() { Application.logMessageReceived -= CaptureError; }
        private void CaptureError(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Assert || type == LogType.Exception)
                fatalLog = condition + "\n" + stackTrace;
        }

        private void Collect()
        {
            var settings = FluidBenchmarkSettings.Active;
            int frame = Time.frameCount;
            long tick = Stopwatch.GetTimestamp();
            if (firstFrame < 0)
            {
                firstFrame = frame;
                lastFrame = firstFrame + settings.warmupFrames + settings.measureFrames - 1;
                observations.Add("observed_unity_frame,source_unity_frame,metric,ms,sample_blocks,status");
                frameTimings.Add("observed_unity_frame,frame_start_timestamp,cpu_frame_ms,gpu_frame_ms,window_status");
            }
            int start = firstFrame + settings.warmupFrames;
            // Wall intervals span complete consecutive LateUpdates, including rendering/presentation.
            if (previousTick != 0 && frame - 1 >= start && frame - 1 <= lastFrame)
                Add(frame, frame - 1, "wall_frame", (tick - previousTick) * 1000.0 / Stopwatch.Frequency, 1);
            previousTick = tick;

            // Unity Recorder documents a three-frame GPU delay. Keep the source frame explicit.
            // A future hardware run must verify this association and the expected block counts.
            int gpuSource = frame - 3;
            if (gpuSource >= start && gpuSource <= lastFrame)
            {
                ReadGpu(frame, gpuSource, "scan_complete", FluidGpuSamples.Scan, sim.iterationsPerFrame);
                ReadGpu(frame, gpuSource, "count_sort_complete", FluidGpuSamples.Sort, sim.iterationsPerFrame);
                ReadGpu(frame, gpuSource, "spatial_hash_complete", FluidGpuSamples.Spatial, sim.iterationsPerFrame);
                ReadGpu(frame, gpuSource, "simulation_complete", FluidGpuSamples.Simulation, 1);
            }
            FrameTimingManager.CaptureFrameTimings();
            uint available = FrameTimingManager.GetLatestTimings((uint)timings.Length, timings);
            for (int i = 0; i < available; i++)
            {
                var timing = timings[i];
                if (timing.frameStartTimestamp == 0 || !timingIds.Add(timing.frameStartTimestamp)) continue;
                // FrameTimingManager has a separate asynchronous stream. Do not pair it by row order.
                frameTimings.Add(frame + "," + timing.frameStartTimestamp + "," + Number(timing.cpuFrameTime) + "," +
                    Number(timing.gpuFrameTime) + ",unassociated_diagnostic_only");
            }
            if (frame == lastFrame) sim.enabled = false;
            if (frame >= lastFrame + 8) Finish();
        }

        private void ReadGpu(int observed, int source, string metric, CustomSampler sampler, int expectedBlocks)
        {
            Recorder recorder = sampler.GetRecorder();
            int blocks = recorder.gpuSampleBlockCount;
            double ms = blocks == expectedBlocks ? recorder.gpuElapsedNanoseconds / 1000000.0 : 0;
            Add(observed, source, metric, ms, blocks);
        }

        private void Add(int observed, int source, string metric, double ms, int blocks)
        {
            string number = Number(ms);
            observations.Add(observed + "," + source + "," + metric + "," + number + "," + blocks + "," +
                (number.Length == 0 ? "unavailable" : metric == "wall_frame" ? "observed" : "requires_gpu_delay_validation"));
        }

        private static string Number(double value)
        {
            return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value) ? value.ToString("R", CultureInfo.InvariantCulture) : "";
        }

        private void Finish()
        {
            finished = true;
            var settings = FluidBenchmarkSettings.Active;
            File.WriteAllLines(Path.Combine(settings.outputDirectory, "observations.csv"), observations);
            File.WriteAllLines(Path.Combine(settings.outputDirectory, "frame-timing-diagnostics.csv"), frameTimings);
            var metadata = new RunMetadata
            {
                settings = settings, device = SystemInfo.graphicsDeviceName, driver = SystemInfo.graphicsDeviceVersion,
                vendorId = SystemInfo.graphicsDeviceVendorID, deviceId = SystemInfo.graphicsDeviceID,
                api = SystemInfo.graphicsDeviceType.ToString(), unity = Application.unityVersion,
                particles = sim.positionBuffer.count, foamCapacity = sim.maxFoamParticleCount,
                iterationsPerFrame = sim.iterationsPerFrame, width = Screen.width, height = Screen.height,
                firstMeasuredUnityFrame = firstFrame + settings.warmupFrames, lastMeasuredUnityFrame = lastFrame,
                fixedTimestep = settings.fixedDeltaTime,
                provenance = Resources.Load<TextAsset>("FluidBenchmarkProvenance")?.text
            };
            File.WriteAllText(Path.Combine(settings.outputDirectory, "run.json"), JsonUtility.ToJson(metadata, true));
            Debug.Log("Fluid benchmark raw observations saved: " + settings.outputDirectory);
            Application.Quit(0);
        }

        [Serializable]
        private sealed class RunMetadata
        {
            public string schema = "hlslperf.fluid-scan.run.v1";
            public string performanceStatus = "raw_observations_require_hardware_review";
            public bool gpuDispatchExecuted = true, correctnessPassed = true, gpuDelayValidated = false;
            public int documentedGpuRecorderDelayFrames = 3;
            public string device, driver, api, unity, provenance;
            public int vendorId, deviceId, particles, foamCapacity, iterationsPerFrame, width, height;
            public int firstMeasuredUnityFrame, lastMeasuredUnityFrame;
            public float fixedTimestep;
            public FluidBenchmarkSettings settings;
        }
    }
}
