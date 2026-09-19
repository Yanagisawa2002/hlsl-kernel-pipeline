using System;
using System.IO;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Profiling;
using Unity.Profiling;
using Unity.Profiling.LowLevel;

namespace HlslPerf.Crossover
{
    // Stable markers, exactly the same names in both API diagnostics. One API per process.
    public sealed class GpuTimingSource : IDisposable
    {
        public static readonly string[] Names = { "Crossover/Range", "Crossover/Cull", "Crossover/Draw" };
        readonly CustomSampler[] legacyMarkers = new CustomSampler[3];
        readonly Recorder[] legacy = new Recorder[3];
        readonly ProfilerMarker[] markers = new ProfilerMarker[3];
        readonly ProfilerRecorder[] modern = new ProfilerRecorder[3];
        readonly bool useModern;
        public GpuTimingSource(string api)
        {
            useModern = api == "profiler-recorder";
            for (int i = 0; i < 3; i++)
            {
                if (useModern)
                {
                    markers[i] = new ProfilerMarker(ProfilerCategory.Render, Names[i], MarkerFlags.SampleGPU);
                    modern[i] = new ProfilerRecorder(markers[i], 1, ProfilerRecorderOptions.StartImmediately |
                        ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.WrapAroundWhenCapacityReached | ProfilerRecorderOptions.GpuRecorder);
                }
                else
                {
                    legacyMarkers[i] = CustomSampler.Create(Names[i], true);
                    legacy[i] = legacyMarkers[i].GetRecorder(); legacy[i].enabled = true;
                }
            }
        }
        public bool Valid(int k) => useModern ? modern[k].Valid : legacy[k].isValid;
        public bool MarkerValid(int k) => useModern ? markers[k].Handle != IntPtr.Zero : legacyMarkers[k].isValid;
        public long Nanoseconds(int k) => useModern ? modern[k].LastValue : legacy[k].gpuElapsedNanoseconds;
        public long Blocks(int k) => useModern ? (modern[k].Count > 0 ? modern[k].GetSample(0).Count : 0) : legacy[k].gpuSampleBlockCount;
        public long CpuBlocks(int k) => useModern ? -1 : legacy[k].sampleBlockCount;
        public void Begin(CommandBuffer command, int k) { if (useModern) command.BeginSample(markers[k]); else command.BeginSample(legacyMarkers[k]); }
        public void End(CommandBuffer command, int k) { if (useModern) command.EndSample(markers[k]); else command.EndSample(legacyMarkers[k]); }
        public void Dispose() { for (int k = 0; k < 3; k++) { if (useModern) modern[k].Dispose(); else legacy[k].enabled = false; } }
    }

    [Serializable] public sealed class TimingObservation
    {
        public int availabilityUnityFrame, expectedSubmissionUnityFrame, submittedRepetitions;
        public double updateIntervalMs;
        public long[] gpuNs = new long[3], blocks = new long[3], cpuBlocks = new long[3];
    }
    [Serializable] public sealed class TimingDiagnosticResult
    {
        public int schema = 2, protocolVersion = 2, firstSubmissionUnityFrame, submissionFrames = 96;
        public string renderingThreadingMode;
        public string timingApi, sourceIdentity, adapter, deviceVersion, unityVersion, graphicsApi, error = "";
        public bool development, batchmode, supportsGpuRecorder, supportsGraphicsFence, profilerEnabled, gpuProfilerAreaEnabled;
        public bool gpuProfilerAreaBefore;
        public string gpuProfilerAreaRequest;
        public bool[] markerValid = new bool[3], recorderValid = new bool[3];
        public TimingObservation[] observations = new TimingObservation[112];
        public bool completed;
        public string interpretation = "API availability/frame attribution diagnostic only; not a clean performance pilot";
    }
    public sealed class TimingDiagnostic : MonoBehaviour
    {
        Options options; ComputeShader compute; Material material; ComputeBuffer agents, visible, arguments;
        RenderTexture target; CommandBuffer command; GpuTimingSource timing;
        TimingDiagnosticResult result; int frame; bool ready; long previous;
        public void Initialize(Options o, ComputeShader shader, Shader draw)
        {
            options = o;
            result = new TimingDiagnosticResult { timingApi = o.timingApi,
                sourceIdentity = Resources.Load<TextAsset>("crossover-source-identity").text.Trim(),
                adapter = SystemInfo.graphicsDeviceName, deviceVersion = SystemInfo.graphicsDeviceVersion,
                renderingThreadingMode = SystemInfo.renderingThreadingMode.ToString(), graphicsApi = SystemInfo.graphicsDeviceType.ToString(), unityVersion = Application.unityVersion,
                development = UnityEngine.Debug.isDebugBuild, batchmode = Application.isBatchMode,
                supportsGpuRecorder = SystemInfo.supportsGpuRecorder, supportsGraphicsFence = SystemInfo.supportsGraphicsFence,
                profilerEnabled = Profiler.enabled, gpuProfilerAreaBefore = Profiler.GetAreaEnabled(ProfilerArea.GPU), gpuProfilerAreaRequest = o.gpuProfilerArea };
            try
            {
                if (o.gpuProfilerArea == "enabled") Profiler.SetAreaEnabled(ProfilerArea.GPU, true);
                result.gpuProfilerAreaEnabled = Profiler.GetAreaEnabled(ProfilerArea.GPU);
                QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; Application.runInBackground = true;
                compute = shader;
                agents = new ComputeBuffer(100000, 28); agents.SetData(Model.Generate(100000, 69501203));
                visible = new ComputeBuffer(100000, 4, ComputeBufferType.Append);
                arguments = new ComputeBuffer(1, 16, ComputeBufferType.IndirectArguments); arguments.SetData(new uint[] {6,0,0,0});
                compute.SetBuffer(0, "_Agents", agents); compute.SetBuffer(0, "_VisibleAgents", visible); compute.SetInt("_AgentCount", 100000);
                material = new Material(draw); material.SetBuffer("_Agents", agents); material.SetBuffer("_VisibleAgents", visible);
                var rect = new Vector4(0, 0, 60, 34); material.SetVector("_ViewRect", rect); compute.SetVector("_ViewRect", rect);
                target = new RenderTexture(new RenderTextureDescriptor(1280,720) {
                    graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,
                    depthStencilFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.D32_SFloat, msaaSamples = 1, sRGB = false });
                target.Create(); command = new CommandBuffer { name = "Crossover timing diagnostic" };
                timing = new GpuTimingSource(o.timingApi);
                for (int k=0;k<3;k++) { result.markerValid[k]=timing.MarkerValid(k); result.recorderValid[k]=timing.Valid(k); }
                ready = true;
            }
            catch (Exception e) { Finish(e); }
        }
        void Update()
        {
            if (!ready) return;
            try
            {
                int unityFrame = Time.frameCount; long now = Stopwatch.GetTimestamp();
                if (frame == 0) result.firstSubmissionUnityFrame = unityFrame;
                var row = new TimingObservation { availabilityUnityFrame = unityFrame,
                    expectedSubmissionUnityFrame = TimingContract.SubmittedUnityFrame(unityFrame),
                    updateIntervalMs = previous == 0 ? -1 : (now - previous) * 1000.0 / Stopwatch.Frequency,
                    submittedRepetitions = frame < 96 ? TimingContract.DiagnosticRepetitions(frame) : 0 };
                previous = now;
                for (int k=0;k<3;k++) { row.gpuNs[k]=timing.Nanoseconds(k); row.blocks[k]=timing.Blocks(k); row.cpuBlocks[k]=timing.CpuBlocks(k); }
                result.observations[frame] = row;
                if (frame < 96)
                {
                    command.Clear(); command.SetRenderTarget(target); command.SetViewport(new Rect(0,0,1280,720));
                    command.ClearRenderTarget(true,true,Color.black,1);
                    for (int r=0;r<row.submittedRepetitions;r++)
                    {
                        timing.Begin(command,0); timing.Begin(command,1);
                        command.SetBufferCounterValue(visible,0); command.DispatchCompute(compute,0,391,1,1); command.CopyCounterValue(visible,arguments,4);
                        timing.End(command,1); timing.Begin(command,2);
                        command.DrawProceduralIndirect(Matrix4x4.identity,material,0,MeshTopology.Triangles,arguments);
                        timing.End(command,2); timing.End(command,0);
                    }
                    Graphics.ExecuteCommandBuffer(command);
                }
                if (++frame == result.observations.Length) { result.completed = true; Finish(null); }
            }
            catch (Exception e) { Finish(e); }
        }
        void Finish(Exception error)
        {
            ready = false; if (error != null) result.error = error.ToString();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.output)));
            File.WriteAllText(options.output, JsonUtility.ToJson(result,true)); Application.Quit(error == null ? 0 : 2);
        }
        void OnDestroy() { timing?.Dispose(); agents?.Release(); visible?.Release(); arguments?.Release(); command?.Release(); if(target!=null) target.Release(); if(material!=null) Destroy(material); }
    }
}
