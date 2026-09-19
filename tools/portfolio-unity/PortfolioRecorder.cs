using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;
using HlslPerf.LiveGpuDrivenCrowd;
using UnityEngine.UI;

// Presentation/capture helper only. Sample compute/render code is copied unchanged.
public sealed class PortfolioRecorder : MonoBehaviour
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    LiveGpuDrivenCrowd crowd;
    Text countLabel;
    Camera captureCamera;
    RenderTexture target;
    string output;
    int Visible => (int)typeof(LiveGpuDrivenCrowd).GetField("lastVisibleCount", Private).GetValue(crowd);
    IEnumerator Start()
    {
        crowd = GetComponent<LiveGpuDrivenCrowd>();
        captureCamera = FindFirstObjectByType<Camera>();
        target = new RenderTexture(960, 540, 24);
        captureCamera.targetTexture = target;
        BuildOverlay();
        output = Environment.GetEnvironmentVariable("HLSL_PORTFOLIO_FRAMES");
        if (string.IsNullOrEmpty(output)) yield break; // Interactive manual presentation.
        Directory.CreateDirectory(output);
        Time.captureFramerate = 24; // Offline capture cadence, never an achieved-FPS claim.
        for (int i = 0; i < 72; i++) yield return null;
        File.WriteAllText(Path.Combine(output, "runtime.txt"),
            $"Unity={Application.unityVersion}\nGPU={SystemInfo.graphicsDeviceName}\nAPI={SystemInfo.graphicsDeviceType}\nTotalAgents=1000000\nVisibleCount=asynchronous telemetry only\nCaptureCadence=24 frames/s (offline)\n");
        for (int i = 0; i < 288; i++)
        {
            yield return new WaitForEndOfFrame();
            countLabel.text = $"1,000,000 total agents  /  {Visible:N0} visible";
            Canvas.ForceUpdateCanvases();
            captureCamera.Render();
            RenderTexture.active = target;
            var frame = new Texture2D(960, 540, TextureFormat.RGB24, false);
            frame.ReadPixels(new Rect(0, 0, 960, 540), 0, 0);
            frame.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(output, $"frame-{i:D4}.png"), frame.EncodeToPNG());
            Destroy(frame);
            if (i % 24 == 0)
                File.AppendAllText(Path.Combine(output, "runtime.txt"), $"frame={i}; visible={Visible}; view={typeof(LiveGpuDrivenCrowd).GetField("viewRect", Private).GetValue(crowd)}\n");
        }
        File.WriteAllText(Path.Combine(output, "complete.txt"), "288 real Unity rendered frames\n");
        Application.Quit();
    }
    void BuildOverlay()
    {
        var canvas = new GameObject("Capture overlay").AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = captureCamera;
        canvas.planeDistance = 1;
        var scaler = canvas.gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(960, 540);
        Panel(canvas.transform, "Header", 0, 0, 960, 118);
        Panel(canvas.transform, "Footer", 0, 494, 960, 46);
        Label(canvas.transform, "GPU-Driven Crowd", 28, 14, 600, 40, 27, true);
        countLabel = Label(canvas.transform, "", 28, 57, 600, 28, 16);
        Label(canvas.transform, "Visible count: live asynchronous telemetry", 28, 88, 600, 23, 13);
        Label(canvas.transform, "GPU culling + compaction       ON", 650, 18, 300, 28, 13);
        Label(canvas.transform, "GPU draw count + indirect     ON", 650, 46, 300, 28, 13);
        Label(canvas.transform, "CPU visibility list                 NONE", 650, 74, 300, 28, 13);
        Label(canvas.transform, "GPU-resident architecture  /  real Unity sample  /  architecture validation, not a performance benchmark", 28, 505, 915, 28, 13);
    }
    static RectTransform Rect(Transform parent, string name, float x, float y, float w, float h)
    {
        var obj = new GameObject(name, typeof(RectTransform));
        var rect = obj.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(w, h);
        return rect;
    }
    static void Panel(Transform parent, string name, float x, float y, float w, float h)
    {
        var image = Rect(parent, name, x, y, w, h).gameObject.AddComponent<Image>();
        image.color = new Color(.025f, .035f, .055f);
    }
    static Text Label(Transform parent, string value, float x, float y, float w, float h, int size, bool bold = false)
    {
        var text = Rect(parent, value, x, y, w, h).gameObject.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = size;
        text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        text.color = size >= 16 ? Color.white : new Color(.68f, .77f, .86f);
        text.text = value;
        return text;
    }
}
