using System;
using System.Collections;
using System.IO;
using Seb.Fluid.Simulation;
using UnityEngine;

namespace HlslPerf.FluidBenchmark
{
    // Explicit offline visual capture. This process exports no performance observations.
    // One rendered frame per fixed simulation step; playback speed is not measured FPS.
    public sealed class FluidFrameCapture : MonoBehaviour
    {
        public FluidSim sim;
        private string fatalLog;
        private void OnEnable() { Application.logMessageReceived += ErrorLog; }
        private void OnDisable() { Application.logMessageReceived -= ErrorLog; }
        private void ErrorLog(string text, string trace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                fatalLog = text + "\n" + trace;
        }

        private IEnumerator Start()
        {
            var settings = FluidBenchmarkSettings.Active;
            string frames = Path.Combine(settings.outputDirectory, "frames");
            Directory.CreateDirectory(frames);
            Time.captureDeltaTime = settings.fixedDeltaTime;
            int first = -1, width = 0, height = 0;
            for (int index = 0; index < settings.measureFrames; ++index)
            {
                yield return new WaitForEndOfFrame();
                Texture2D image = null;
                try
                {
                    if (fatalLog != null) throw new InvalidOperationException(fatalLog);
                    if (sim.positionBuffer == null) throw new InvalidOperationException("Simulation did not initialize.");
                    if (first < 0) first = Time.frameCount;
                    image = ScreenCapture.CaptureScreenshotAsTexture();
                    if (image == null) throw new InvalidOperationException("Rendered-frame capture is unavailable.");
                    width = image.width; height = image.height;
                    if (width != 1920 || height != 1080) throw new InvalidOperationException("Capture must remain 1920x1080.");
                    File.WriteAllBytes(Path.Combine(frames, index.ToString("D6") + ".png"), image.EncodeToPNG());
                }
                catch (Exception error)
                {
                    sim.enabled = false;
                    File.WriteAllText(Path.Combine(settings.outputDirectory, "failure.txt"), error.ToString());
                    Debug.LogError(error); Application.Quit(2); yield break;
                }
                finally { if (image != null) Destroy(image); }
            }
            sim.enabled = false;
            var receipt = new CaptureReceipt
            {
                settings = settings, frames = settings.measureFrames, firstUnityFrame = first,
                lastUnityFrame = Time.frameCount, width = width, height = height,
                particles = sim.positionBuffer.count, foamCapacity = sim.maxFoamParticleCount,
                iterationsPerFrame = sim.iterationsPerFrame, unity = Application.unityVersion,
                device = SystemInfo.graphicsDeviceName, api = SystemInfo.graphicsDeviceType.ToString(),
                provenance = Resources.Load<TextAsset>("FluidBenchmarkProvenance")?.text
            };
            File.WriteAllText(Path.Combine(settings.outputDirectory, "capture.json"), JsonUtility.ToJson(receipt, true));
            Debug.Log("Offline fluid capture completed: " + settings.outputDirectory);
            Application.Quit(0);
        }

        [Serializable]
        private sealed class CaptureReceipt
        {
            public string schema = "hlslperf.fluid-scan.capture.v1";
            public string timingStatus = "offline_fixed_step_visuals_not_performance_evidence";
            public FluidBenchmarkSettings settings;
            public int frames, firstUnityFrame, lastUnityFrame, width, height, particles, foamCapacity, iterationsPerFrame;
            public string unity, device, api, provenance;
        }
    }
}
