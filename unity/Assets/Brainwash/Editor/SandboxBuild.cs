using System;
using System.IO;
using System.Linq;
using Brainwash;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Brainwash.Editor
{
    public static class SandboxBuild
    {
        [MenuItem("Brainwash/Prepare sandbox scene")]
        public static void Prepare()
        {
            Directory.CreateDirectory("Assets/Brainwash/Settings");
            Directory.CreateDirectory("Assets/Brainwash/Scenes");
            const string rendererPath = "Assets/Brainwash/Settings/SandboxRenderer.asset";
            const string pipelinePath = "Assets/Brainwash/Settings/SandboxURP.asset";
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (renderer == null) { renderer = ScriptableObject.CreateInstance<UniversalRendererData>(); AssetDatabase.CreateAsset(renderer, rendererPath); }
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
            if (pipeline == null) { pipeline = UniversalRenderPipelineAsset.Create(renderer); AssetDatabase.CreateAsset(pipeline, pipelinePath); }
            pipeline.msaaSampleCount = 2;
            pipeline.shadowDistance = 35;
            pipeline.mainLightShadowmapResolution = 1024;
            GraphicsSettings.defaultRenderPipeline = pipeline;
            for (int quality = 0; quality < QualitySettings.names.Length; quality++)
            {
                QualitySettings.SetQualityLevel(quality);
                QualitySettings.renderPipeline = pipeline;
            }
            QualitySettings.vSyncCount = 1;
            // Scene objects are procedural; explicitly retain their shaders in the Player.
            var graphics = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            var shaders = graphics.FindProperty("m_AlwaysIncludedShaders");
            for (int i = shaders.arraySize - 1; i >= 0; i--)
            {
                var existing = shaders.GetArrayElementAtIndex(i).objectReferenceValue;
                if (existing != null && (existing.hideFlags & HideFlags.DontSaveInBuild) != 0)
                { shaders.GetArrayElementAtIndex(i).objectReferenceValue = null; shaders.DeleteArrayElementAtIndex(i); }
            }
            foreach (string name in new[] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Unlit", "Sprites/Default" })
            {
                var shader = Shader.Find(name);
                if (shader == null || (shader.hideFlags & HideFlags.DontSaveInBuild) != 0) continue;
                bool found = false;
                for (int i = 0; i < shaders.arraySize; i++) if (shaders.GetArrayElementAtIndex(i).objectReferenceValue == shader) found = true;
                if (!found) { shaders.InsertArrayElementAtIndex(shaders.arraySize); shaders.GetArrayElementAtIndex(shaders.arraySize - 1).objectReferenceValue = shader; }
            }
            graphics.ApplyModifiedPropertiesWithoutUndo();
            PlayerSettings.companyName = "CHL-work";
            PlayerSettings.productName = "Brainwash 3D Sandbox";
            PlayerSettings.bundleVersion = "0.4.0";
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.defaultScreenWidth = 1600; PlayerSettings.defaultScreenHeight = 1000;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard);
            EditorSettings.serializationMode = SerializationMode.ForceText;
            var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            var input = settings.FindProperty("activeInputHandler");
            if (input != null) { input.intValue = 0; settings.ApplyModifiedPropertiesWithoutUndo(); }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Brainwash sandbox").AddComponent<SandboxBootstrap>();
            const string scenePath = "Assets/Brainwash/Scenes/Sandbox.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            Debug.Log("BRAINWASH_PREPARE_OK Unity=" + Application.unityVersion);
        }

        // Plays all five demos with injected directives and writes a JSON report
        // (Logs/brainwash-demo-smoke.json) plus one screenshot per demo.
        [MenuItem("Brainwash/Run demo smoke (enters Play Mode)")]
        public static void RunDemoSmoke()
        {
            if (EditorApplication.isPlaying) { Debug.LogWarning("Stop Play Mode before running the demo smoke."); return; }
            string report = Path.GetFullPath(Path.Combine("Logs", "brainwash-demo-smoke.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            File.WriteAllText(DemoSmoke.EditorRequestFile, report);
            Debug.Log("BRAINWASH_DEMO_SMOKE_REQUESTED " + report);
            // Screenshots come from the Game view, so it must be the visible tab.
            EditorApplication.ExecuteMenuItem("Window/General/Game");
            EditorApplication.EnterPlaymode();
        }

        [MenuItem("Brainwash/Build Windows sandbox")]
        public static void BuildWindows()
        {
            Prepare();
            string output = Path.GetFullPath("Builds/Windows/BrainwashSandbox.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = output, target = BuildTarget.StandaloneWindows64,
                // Keep diagnostics in our HUD/player log without Unity's profiler listener.
                options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded) throw new Exception("Windows build failed: " + report.summary.result);
            Debug.Log("BRAINWASH_BUILD_OK " + output + " bytes=" + report.summary.totalSize);
        }
    }
}
