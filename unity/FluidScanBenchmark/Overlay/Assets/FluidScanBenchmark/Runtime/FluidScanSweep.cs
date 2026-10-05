using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace HlslPerf.FluidBenchmark
{
    // Isolated exclusive scan scaling. No solver/rendering or application frame-time claim.
    // Each operation regenerates the same input outside both timing boundaries.
    public sealed class FluidScanSweep : MonoBehaviour
    {
        private IExclusiveScan scan;
        private ComputeShader init;
        private ComputeBuffer values, latestResult;
        private CommandBuffer cmd;
        private int sizeIndex, firstFrame, lastFrame, nextRead, readCount;
        private string fatalLog;
        private bool finished;
        private readonly List<string> rows = new List<string>();
        private readonly List<int> validated = new List<int>();
        private readonly List<Interval> intervals = new List<Interval>();
        private void OnEnable() { Application.logMessageReceived += ErrorLog; }
        private void OnDisable() { Application.logMessageReceived -= ErrorLog; values?.Dispose(); scan?.Dispose(); cmd?.Dispose(); }
        private void ErrorLog(string text, string trace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) fatalLog = text + "\n" + trace;
        }
        private void Start()
        {
            try
            {
                var settings = FluidBenchmarkSettings.Active;
                init = ScanBackends.Load("FluidScanSweepInit", "FillStructured", "FillRaw");
                scan = ScanBackends.Create(settings.arm); cmd = new CommandBuffer { name = "Isolated scan sweep" };
                FluidGpuSamples.Enable();
                FluidNativeTiming.Active = new FluidNativeTiming(settings.scanBatch, 25); // scan, batch, core
                rows.Add("observed_frame,source_frame,length,complete_ms_per_scan,core_ms_per_scan,batch_gpu_ms,complete_blocks,core_blocks");
                SetupSize();
            }
            catch (Exception error) { Fail(error); }
        }
        private void Fill(int count)
        {
            bool direct = FluidBenchmarkSettings.Active.arm == ScanArm.HlslWaveTiledDirect;
            int kernel = init.FindKernel(direct ? "FillRaw" : "FillStructured");
            cmd.SetComputeIntParam(init, "LogicalCount", count);
            cmd.SetComputeBufferParam(init, kernel, direct ? "RawValues" : "StructuredValues", values);
            cmd.DispatchCompute(init, kernel, (count + 255) / 256, 1, 1);
        }
        private void SetupSize()
        {
            var settings = FluidBenchmarkSettings.Active;
            int count = settings.scanLengths[sizeIndex];
            bool direct = settings.arm == ScanArm.HlslWaveTiledDirect;
            values?.Dispose(); values = new ComputeBuffer(count + 4, 4, direct ? ComputeBufferType.Raw : ComputeBufferType.Default);
            FluidGpuSamples.Recording = false;
            cmd.Clear(); Fill(count); var result = scan.Record(cmd, values, count);
            var guards = new uint[] { 0xa5a5a5a5, 0xa5a5a5a5, 0xa5a5a5a5, 0xa5a5a5a5 };
            values.SetData(guards, 0, count, 4); result.SetData(guards, 0, count, 4);
            Graphics.ExecuteCommandBuffer(cmd);
            Verify(count, result);
            if (fatalLog != null) throw new InvalidOperationException(fatalLog);
            validated.Add(count); FluidGpuSamples.Recording = true;
            firstFrame = Time.frameCount + 1;
            nextRead = firstFrame + settings.warmupFrames;
            lastFrame = nextRead + settings.measureFrames - 1; readCount = 0;
            intervals.Add(new Interval { length = count, firstMeasuredFrame = nextRead, lastMeasuredFrame = lastFrame });
            Debug.Log("Scan sweep length validated: " + count);
        }
        private void Verify(int count, ComputeBuffer result)
        {
            bool direct = FluidBenchmarkSettings.Active.arm == ScanArm.HlslWaveTiledDirect;
            var actual = new uint[count + 4]; result.GetData(actual, 0, 0, actual.Length);
            uint prefix = 0;
            for (int i = 0; i < count; ++i)
            {
                if (actual[i] != prefix) throw new InvalidOperationException("Full sweep output mismatch at N=" + count + ", i=" + i);
                prefix = unchecked(prefix + (uint)i * 747796405u + 2891336453u);
            }
            for (int i = count; i < actual.Length; ++i)
                if (actual[i] != 0xa5a5a5a5) throw new InvalidOperationException("Sweep output guard overwritten.");
            if (direct)
            {
                values.GetData(actual);
                for (int i = 0; i < count; ++i)
                    if (actual[i] != unchecked((uint)i * 747796405u + 2891336453u)) throw new InvalidOperationException("Direct sweep input modified.");
                for (int i = count; i < actual.Length; ++i)
                    if (actual[i] != 0xa5a5a5a5) throw new InvalidOperationException("Direct sweep input guard overwritten.");
            }
        }
        private void Update()
        {
            if (finished || scan == null) return;
            try
            {
                if (fatalLog != null) throw new InvalidOperationException(fatalLog);
                var settings = FluidBenchmarkSettings.Active;
                int frame = Time.frameCount, count = settings.scanLengths[sizeIndex];
                while (nextRead <= lastFrame && nextRead < frame && FluidNativeTiming.Active.Read(nextRead))
                {
                    var timing = FluidNativeTiming.Active;
                    if (timing.blocks[0] != settings.scanBatch || timing.blocks[4] != settings.scanBatch || timing.blocks[3] != 1)
                        throw new InvalidOperationException("Sweep timestamp block mismatch.");
                    rows.Add(frame + "," + nextRead + "," + count + "," + Number(timing.ms[0] / settings.scanBatch) + "," +
                        Number(timing.ms[4] / settings.scanBatch) + "," + Number(timing.ms[3]) + "," + timing.blocks[0] + "," + timing.blocks[4]);
                    ++nextRead; ++readCount;
                }
                if (frame >= firstFrame && frame <= lastFrame)
                {
                    cmd.Clear(); FluidGpuSamples.Begin(cmd, FluidGpuSamples.Simulation);
                    for (int operation = 0; operation < settings.scanBatch; ++operation)
                    {
                        Fill(count); // Input generation is inside batch time, outside complete/core scan time.
                        FluidGpuSamples.Begin(cmd, FluidGpuSamples.Scan);
                        latestResult = scan.Record(cmd, values, count);
                        FluidGpuSamples.End(cmd, FluidGpuSamples.Scan);
                    }
                    FluidGpuSamples.End(cmd, FluidGpuSamples.Simulation); Graphics.ExecuteCommandBuffer(cmd);
                }
                if (frame > lastFrame && readCount == settings.measureFrames)
                {
                    Verify(count, latestResult);
                    if (++sizeIndex < settings.scanLengths.Length) SetupSize();
                    else Finish();
                }
                else if (frame > lastFrame + 64) throw new InvalidOperationException("Sweep timing readback did not drain.");
            }
            catch (Exception error) { Fail(error); }
        }
        private static string Number(double value)
        {
            if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidOperationException("Unavailable sweep timestamp.");
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
        private void Finish()
        {
            finished = true;
            var settings = FluidBenchmarkSettings.Active;
            File.WriteAllLines(Path.Combine(settings.outputDirectory, "sweep-observations.csv"), rows);
            File.WriteAllText(Path.Combine(settings.outputDirectory, "sweep.json"), JsonUtility.ToJson(new Receipt
            {
                settings = settings, validatedLengths = validated.ToArray(), intervals = intervals.ToArray(), device = SystemInfo.graphicsDeviceName,
                api = SystemInfo.graphicsDeviceType.ToString(), unity = Application.unityVersion,
                gpuTimestampFrequency = FluidNativeTiming.FgpFrequency().ToString(CultureInfo.InvariantCulture),
                nativeTimingBuild = Resources.Load<TextAsset>("FluidGpuTimingBuild")?.text,
                provenance = Resources.Load<TextAsset>("FluidBenchmarkProvenance")?.text
            }, true));
            Debug.Log("Scan sweep completed."); Application.Quit(0);
        }
        private void Fail(Exception error)
        {
            finished = true;
            File.WriteAllText(Path.Combine(FluidBenchmarkSettings.Active.outputDirectory, "failure.txt"), error.ToString());
            Debug.LogError(error); Application.Quit(2);
        }
        [Serializable] private sealed class Interval
        {
            public int length, firstMeasuredFrame, lastMeasuredFrame;
        }
        [Serializable] private sealed class Receipt
        {
            public string schema = "hlslperf.fluid-scan.sweep.v1";
            public string status = "passed", timingMethod = "d3d12_query_frame_fence";
            public string coreBoundary = "complete scan algorithm including required reset; excludes pack/unpack";
            public string inputPattern = "uint32(index * 747796405 + 2891336453), regenerated before every operation outside scan ranges";
            public bool fullOutputValidated = true, gpuFrameAssociationValidated = true, applicationFrameTimeMeasured = false;
            public FluidBenchmarkSettings settings;
            public int[] validatedLengths;
            public Interval[] intervals;
            public int fullSizeValidationPassesPerLength = 2;
            public string device, api, unity, gpuTimestampFrequency, nativeTimingBuild, provenance;
        }
    }
}
