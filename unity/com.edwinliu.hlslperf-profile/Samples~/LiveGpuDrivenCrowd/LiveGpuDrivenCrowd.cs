using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace EdwinLiu.HlslPerf.Samples
{
    /// <summary>
    /// Live GPU-resident crowd path:
    /// immutable agent buffer -> compute frustum cull -> append/compact visible agents
    /// -> GPU counter copied into indirect draw args -> DrawMeshInstancedIndirect.
    /// The full visible set is never read back to the CPU. A tiny delayed args readback
    /// is optional and is used only for the on-screen visible-count diagnostic.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LiveGpuDrivenCrowd : MonoBehaviour
    {
        [Header("Required")]
        [SerializeField] private ComputeShader cullingShader;
        [SerializeField] private Shader drawShader;
        [SerializeField] private Camera targetCamera;

        [Header("Workload")]
        [Min(1024)] [SerializeField] private int agentCount = 1_048_576;
        [Min(10f)] [SerializeField] private float worldHalfExtent = 500f;
        [SerializeField] private Vector2 radiusRange = new Vector2(0.35f, 1.2f);
        [SerializeField] private int seed = 69501203;

        [Header("Diagnostics")]
        [SerializeField] private bool showOverlay = true;
        [Min(0.1f)] [SerializeField] private float telemetryIntervalSeconds = 0.25f;

        private const int ThreadsPerGroup = 256;
        private static readonly int AgentsId = Shader.PropertyToID("_Agents");
        private static readonly int VisibleAgentsId = Shader.PropertyToID("_VisibleAgents");
        private static readonly int AgentCountId = Shader.PropertyToID("_AgentCount");
        private static readonly int TimeSecondsId = Shader.PropertyToID("_TimeSeconds");
        private static readonly int FrustumPlanesId = Shader.PropertyToID("_FrustumPlanes");
        private static readonly int CameraRightId = Shader.PropertyToID("_CameraRight");
        private static readonly int CameraUpId = Shader.PropertyToID("_CameraUp");

        private ComputeBuffer agentBuffer;
        private ComputeBuffer visibleBuffer;
        private ComputeBuffer argsBuffer;
        private Material drawMaterial;
        private Mesh quad;
        private int cullKernel;
        private Bounds drawBounds;
        private readonly Vector4[] frustumPlaneVectors = new Vector4[6];
        private uint visibleCount;
        private float nextTelemetryTime;
        private bool telemetryPending;
        private float smoothedFrameMs;

        private void OnEnable()
        {
            if (!SystemInfo.supportsComputeShaders)
                throw new NotSupportedException("LiveGpuDrivenCrowd requires compute shader support.");
            if (!SystemInfo.supportsInstancing)
                throw new NotSupportedException("LiveGpuDrivenCrowd requires GPU instancing support.");
            if (cullingShader == null || drawShader == null)
                throw new InvalidOperationException("Assign the sample compute shader and draw shader before running.");

            targetCamera ??= Camera.main;
            if (targetCamera == null)
                throw new InvalidOperationException("Assign a target camera or tag one camera as MainCamera.");

            agentCount = Mathf.Max(1024, agentCount);
            worldHalfExtent = Mathf.Max(10f, worldHalfExtent);
            radiusRange.x = Mathf.Max(0.05f, radiusRange.x);
            radiusRange.y = Mathf.Max(radiusRange.x, radiusRange.y);

            cullKernel = cullingShader.FindKernel("CullAgents");
            CreateResources();
        }

        private void CreateResources()
        {
            Vector4[] agents = new Vector4[agentCount];
            var random = new System.Random(seed);
            for (int i = 0; i < agents.Length; ++i)
            {
                float x = Mathf.Lerp(-worldHalfExtent, worldHalfExtent, (float)random.NextDouble());
                float z = Mathf.Lerp(-worldHalfExtent, worldHalfExtent, (float)random.NextDouble());
                float y = Mathf.Lerp(-2f, 18f, Mathf.Pow((float)random.NextDouble(), 6f));
                float radius = Mathf.Lerp(radiusRange.x, radiusRange.y, (float)random.NextDouble());
                agents[i] = new Vector4(x, y, z, radius);
            }

            agentBuffer = new ComputeBuffer(agentCount, sizeof(float) * 4, ComputeBufferType.Structured);
            agentBuffer.SetData(agents);
            visibleBuffer = new ComputeBuffer(agentCount, sizeof(float) * 4, ComputeBufferType.Append);
            argsBuffer = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);

            quad = new Mesh { name = "HlslPerf Live GPU Crowd Quad" };
            quad.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f),
            };
            quad.triangles = new[] { 0, 1, 2, 2, 3, 0 };
            quad.RecalculateBounds();

            uint[] args =
            {
                quad.GetIndexCount(0),
                0,
                quad.GetIndexStart(0),
                (uint)quad.GetBaseVertex(0),
                0,
            };
            argsBuffer.SetData(args);

            drawMaterial = new Material(drawShader) { name = "HlslPerf Live GPU Crowd Material" };
            drawMaterial.enableInstancing = true;
            drawMaterial.SetBuffer(VisibleAgentsId, visibleBuffer);

            cullingShader.SetBuffer(cullKernel, AgentsId, agentBuffer);
            cullingShader.SetBuffer(cullKernel, VisibleAgentsId, visibleBuffer);
            cullingShader.SetInt(AgentCountId, agentCount);

            float extent = worldHalfExtent + 64f;
            drawBounds = new Bounds(Vector3.zero, new Vector3(extent * 2f, 128f, extent * 2f));
        }

        private void Update()
        {
            if (agentBuffer == null)
                return;

            float dtMs = Time.unscaledDeltaTime * 1000f;
            smoothedFrameMs = smoothedFrameMs <= 0f ? dtMs : Mathf.Lerp(smoothedFrameMs, dtMs, 0.08f);

            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(targetCamera);
            for (int i = 0; i < 6; ++i)
            {
                Plane plane = planes[i];
                frustumPlaneVectors[i] = new Vector4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
            }

            visibleBuffer.SetCounterValue(0);
            cullingShader.SetFloat(TimeSecondsId, Time.time);
            cullingShader.SetVectorArray(FrustumPlanesId, frustumPlaneVectors);
            int groups = (agentCount + ThreadsPerGroup - 1) / ThreadsPerGroup;
            cullingShader.Dispatch(cullKernel, groups, 1, 1);

            // The append counter becomes instanceCount at byte offset 4 in the
            // standard five-uint indexed indirect-argument layout. No CPU count
            // participates in draw submission.
            ComputeBuffer.CopyCount(visibleBuffer, argsBuffer, sizeof(uint));

            Transform cameraTransform = targetCamera.transform;
            drawMaterial.SetVector(CameraRightId, cameraTransform.right);
            drawMaterial.SetVector(CameraUpId, cameraTransform.up);
            Graphics.DrawMeshInstancedIndirect(
                quad,
                0,
                drawMaterial,
                drawBounds,
                argsBuffer,
                0,
                null,
                ShadowCastingMode.Off,
                false,
                gameObject.layer,
                targetCamera,
                LightProbeUsage.Off);

            if (showOverlay && !telemetryPending && Time.unscaledTime >= nextTelemetryTime)
            {
                telemetryPending = true;
                nextTelemetryTime = Time.unscaledTime + telemetryIntervalSeconds;
                AsyncGPUReadback.Request(argsBuffer, request =>
                {
                    telemetryPending = false;
                    if (!request.hasError)
                    {
                        var data = request.GetData<uint>();
                        if (data.Length >= 2)
                            visibleCount = data[1];
                    }
                });
            }
        }

        private void OnGUI()
        {
            if (!showOverlay)
                return;

            const int width = 390;
            GUI.Box(new Rect(12, 12, width, 104), GUIContent.none);
            GUI.Label(new Rect(24, 22, width - 24, 22), "LIVE GPU-DRIVEN CROWD");
            GUI.Label(new Rect(24, 44, width - 24, 22), $"Agents: {agentCount:N0}   Visible*: {visibleCount:N0}");
            GUI.Label(new Rect(24, 66, width - 24, 22), $"CPU frame: {smoothedFrameMs:0.00} ms   Draw: indirect");
            GUI.Label(new Rect(24, 88, width - 24, 22), "* delayed tiny args readback; no visible-set/atlas readback");
        }

        private void OnDisable()
        {
            agentBuffer?.Release();
            visibleBuffer?.Release();
            argsBuffer?.Release();
            agentBuffer = null;
            visibleBuffer = null;
            argsBuffer = null;

            if (Application.isPlaying)
            {
                if (drawMaterial != null) Destroy(drawMaterial);
                if (quad != null) Destroy(quad);
            }
            else
            {
                if (drawMaterial != null) DestroyImmediate(drawMaterial);
                if (quad != null) DestroyImmediate(quad);
            }
        }
    }
}
