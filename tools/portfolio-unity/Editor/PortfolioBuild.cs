using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using HlslPerf.LiveGpuDrivenCrowd;

public static class PortfolioBuild
{
    public static void Build()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var camera = new GameObject("Camera").AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.025f, .035f, .055f);
        camera.orthographic = true;
        camera.transform.position = new Vector3(0, 0, -10);
        var go = new GameObject("Repository crowd sample");
        go.SetActive(false);
        var crowd = go.AddComponent<LiveGpuDrivenCrowd>();
        var so = new SerializedObject(crowd);
        so.FindProperty("cullingShader").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LiveGpuDrivenCrowd/LiveGpuDrivenCrowd.compute");
        so.FindProperty("drawShader").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Shader>("Assets/LiveGpuDrivenCrowd/LiveGpuDrivenCrowd.shader");
        so.FindProperty("agentCount").intValue = 1000000;
        so.FindProperty("cameraOrbitSpeed").floatValue = .06f;
        so.FindProperty("cameraOrbitRadius").floatValue = 20f;
        so.FindProperty("viewHalfExtents").vector2Value = new Vector2(8f, 4.5f);
        so.FindProperty("minimumAgentSize").floatValue = .025f;
        so.FindProperty("maximumAgentSize").floatValue = .07f;
        so.ApplyModifiedPropertiesWithoutUndo();
        go.SetActive(true);
        go.AddComponent<PortfolioRecorder>();
        EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), "Assets/Portfolio.unity");
        PlayerSettings.defaultScreenWidth = 960;
        PlayerSettings.defaultScreenHeight = 540;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.runInBackground = true;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 });
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = new[] { "Assets/Portfolio.unity" },
            locationPathName = Environment.GetEnvironmentVariable("HLSL_PORTFOLIO_PLAYER"),
            target = BuildTarget.StandaloneWindows64, options = BuildOptions.None });
        if (report.summary.result != BuildResult.Succeeded)
            throw new Exception("Portfolio player build failed: " + report.summary.result);
    }
}
