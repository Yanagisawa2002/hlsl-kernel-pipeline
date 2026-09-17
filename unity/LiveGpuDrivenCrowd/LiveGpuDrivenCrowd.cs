using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace HlslPerf.LiveGpuDrivenCrowd
{
    public sealed class LiveGpuDrivenCrowd : MonoBehaviour
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct AgentGpu
        {
            public Vector2 Position;
            public float Size;
            public Vector4 Color;
        }

        [Header("Assets")]
        [SerializeField] private ComputeShader cullingShader = null!;
        [SerializeField] private Shader drawShader = null!;

        [Header("Scene")]
        [SerializeField, Min(1)] private int agentCount = 160_000;
        [SerializeField] private int seed = 69501203;
        [SerializeField] private Vector2 worldHalfExtents = new(120f, 68f);
        [SerializeField] private Vector2 viewHalfExtents = new(32f, 18f);
        [SerializeField] private float cameraOrbitRadius = 45f;
        [SerializeField] private float cameraOrbitSpeed = 0.18f;
        [SerializeField, Range(0.02f, 0.5f)] private float minimumAgentSize = 0.06f;
        [SerializeField, Range(0.02f, 0.5f)] private float maximumAgentSize = 0.16f;

        private ComputeBuffer? agents;
        private ComputeBuffer? visibleAgents;
        private ComputeBuffer? indirectArgs;
        private Material? material;
        private int cullKernel;
        private uint cullThreadsX;
        private readonly uint[] args = { 6, 0, 0, 0 };
        private int lastVisibleCount;
        private int readbackCountdown;
        private Vector4 viewRect;

        private static readonly int AgentsId = Shader.PropertyToID("_Agents");
        private static readonly int VisibleAgentsId = Shader.PropertyToID("_VisibleAgents");
        private static readonly int AgentCountId = Shader.PropertyToID("_AgentCount");
        private static readonly int ViewRectId = Shader.PropertyToID("_ViewRect");

        private void OnEnable()
        {
            if (!SystemInfo.supportsComputeShaders)
                throw new NotSupportedException("LiveGpuDrivenCrowd requires compute-shader support.");
            if (cullingShader == null || drawShader == null)
                throw new InvalidOperationException("Assign the compute and draw shaders before enabling the demo.");

            agentCount = Mathf.Clamp(agentCount, 1, 8_000_000);
            cullKernel = cullingShader.FindKernel("CullAgents");
            cullingShader.GetKernelThreadGroupSizes(cullKernel, out cullThreadsX, out _, out _);
            if (cullThreadsX == 0) throw new InvalidOperationException("CullAgents reported zero threads.");

            AgentGpu[] initial = GenerateAgents(agentCount, seed);
            int stride = Marshal.SizeOf<AgentGpu>();
            agents = new ComputeBuffer(agentCount, stride, ComputeBufferType.Structured);
            visibleAgents = new ComputeBuffer(agentCount, sizeof(uint), ComputeBufferType.Append);
            indirectArgs = new ComputeBuffer(1, sizeof(uint) * 4, ComputeBufferType.IndirectArguments);
            agents.SetData(initial);
            indirectArgs.SetData(args);

            material = new Material(drawShader) { name = "LiveGpuDrivenCrowd (runtime)" };
            material.SetBuffer(AgentsId, agents);
            material.SetBuffer(VisibleAgentsId, visibleAgents);
            cullingShader.SetBuffer(cullKernel, AgentsId, agents);
            cullingShader.SetBuffer(cullKernel, VisibleAgentsId, visibleAgents);
            cullingShader.SetInt(AgentCountId, agentCount);
        }

        private void Update()
        {
            if (agents == null || visibleAgents == null || indirectArgs == null || material == null)
                return;

            float phase = Time.time * cameraOrbitSpeed;
            Vector2 center = new(Mathf.Cos(phase) * cameraOrbitRadius, Mathf.Sin(phase * 0.73f) * cameraOrbitRadius * 0.55f);
            viewRect = new Vector4(center.x, center.y, viewHalfExtents.x, viewHalfExtents.y);

            visibleAgents.SetCounterValue(0);
            indirectArgs.SetData(args);
            cullingShader.SetVector(ViewRectId, viewRect);
            material.SetVector(ViewRectId, viewRect);

            int groups = Mathf.CeilToInt(agentCount / (float)cullThreadsX);
            cullingShader.Dispatch(cullKernel, groups, 1, 1);

            // GPU writes AppendStructuredBuffer count directly into instanceCount.
            // Rendering consumes the compacted visible-index buffer without a CPU readback.
            ComputeBuffer.CopyCount(visibleAgents, indirectArgs, sizeof(uint));

            Bounds bounds = new(Vector3.zero, new Vector3(worldHalfExtents.x * 4f, worldHalfExtents.y * 4f, 10f));
            Graphics.DrawProceduralIndirect(
                material,
                bounds,
                MeshTopology.Triangles,
                indirectArgs,
                0,
                null,
                null,
                ShadowCastingMode.Off,
                false,
                gameObject.layer);

            // Optional low-frequency telemetry only. It never feeds rendering or dispatch decisions.
            if (--readbackCountdown <= 0)
            {
                readbackCountdown = 30;
                AsyncGPUReadback.Request(indirectArgs, request =>
                {
                    if (!request.hasError && request.done)
                        lastVisibleCount = (int)request.GetData<uint>()[1];
                });
            }
        }

        private void OnGUI()
        {
            GUI.Box(new Rect(18, 18, 455, 94), GUIContent.none);
            GUI.Label(new Rect(32, 28, 430, 24), "LIVE GPU-DRIVEN CROWD");
            GUI.Label(new Rect(32, 50, 430, 24), $"{agentCount:N0} agents · {lastVisibleCount:N0} visible (async telemetry)");
            GUI.Label(new Rect(32, 72, 430, 24), "GPU cull → append/compact → indirect args → indirect draw");
        }

        private AgentGpu[] GenerateAgents(int count, int randomSeed)
        {
            var random = new System.Random(randomSeed);
            AgentGpu[] output = new AgentGpu[count];
            for (int i = 0; i < count; i++)
            {
                float x = Mathf.Lerp(-worldHalfExtents.x, worldHalfExtents.x, (float)random.NextDouble());
                float y = Mathf.Lerp(-worldHalfExtents.y, worldHalfExtents.y, (float)random.NextDouble());
                float size = Mathf.Lerp(minimumAgentSize, maximumAgentSize, (float)random.NextDouble());
                float hue = (float)random.NextDouble();
                Color color = Color.HSVToRGB(hue, 0.55f, 1f);
                output[i] = new AgentGpu
                {
                    Position = new Vector2(x, y),
                    Size = size,
                    Color = new Vector4(color.r, color.g, color.b, 1f)
                };
            }
            return output;
        }

        private void OnDisable()
        {
            agents?.Release();
            visibleAgents?.Release();
            indirectArgs?.Release();
            agents = null;
            visibleAgents = null;
            indirectArgs = null;
            if (material != null) Destroy(material);
            material = null;
        }
    }
}
