using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering.RenderGraphModule;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition
{
    // SessionState survives domain reload; the runner is opt-in and batch-only.
    [InitializeOnLoad]
    public static class MobilePlayModeValidation
    {
        const string Key = "HDRPMobile.PlayValidation.";
        static RenderTexture s_Target;
        static MobilePlayModeValidation()
        {
            EditorApplication.playModeStateChanged += OnState;
            EditorApplication.update += Tick;
            Application.logMessageReceived += OnLog;
        }

        public static void Run()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Use a disposable batch-mode validation project.");
            SessionState.SetBool(Key + "Enabled", EditorSettings.enterPlayModeOptionsEnabled);
            SessionState.SetInt(Key + "Options", (int)EditorSettings.enterPlayModeOptions);
            SessionState.SetInt(Key + "Cycle", 0); SessionState.SetInt(Key + "Ticks", 0);
            SessionState.SetInt(Key + "MaterialBaseline", -1); SessionState.SetInt(Key + "GraphBaseline", -1);
            SessionState.SetInt(Key + "TextureBaseline", -1); SessionState.SetInt(Key + "VolumeBaseline", -1);
            SessionState.SetString(Key + "Error", "");
            EditorSceneManager.OpenScene("Assets/MobileBenchmark/Benchmark.unity");
            var asset = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>("Assets/MobileBenchmark/Mobile.asset");
            if (asset == null) throw new InvalidOperationException("Bake/create the benchmark fixture first.");
            GraphicsSettings.renderPipelineAsset = asset; QualitySettings.renderPipeline = asset;
            SessionState.SetBool(Key + "Active", true);
        }

        static void OnLog(string condition, string trace, LogType type)
        {
            if (SessionState.GetBool(Key + "Active", false) && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert))
                SessionState.SetString(Key + "Error", condition + "\n" + trace);
        }

        static void OnState(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            SessionState.SetInt(Key + "Ticks", 0);
            if (state == PlayModeStateChange.ExitingPlayMode && s_Target != null)
            {
                if (Camera.main != null) Camera.main.targetTexture = null;
                s_Target.Release(); UnityEngine.Object.DestroyImmediate(s_Target); s_Target = null;
            }
            if (state == PlayModeStateChange.EnteredEditMode)
                SessionState.SetInt(Key + "Cycle", SessionState.GetInt(Key + "Cycle", 0) + 1);
        }

        static void Tick()
        {
            if (!SessionState.GetBool(Key + "Active", false) || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            string error = SessionState.GetString(Key + "Error", "");
            if (error.Length != 0) { Finish(error); return; }
            int ticks = SessionState.GetInt(Key + "Ticks", 0) + 1; SessionState.SetInt(Key + "Ticks", ticks);
            EditorApplication.QueuePlayerLoopUpdate();
            if (EditorApplication.isPlaying)
            {
                // Batch mode has no Game View to create/render the pipeline for us.
                if (RenderPipelineManager.currentPipeline == null)
                    typeof(RenderPipelineManager).GetMethod("PrepareRenderPipeline", BindingFlags.Static | BindingFlags.NonPublic)
                        .Invoke(null, new object[] { GraphicsSettings.currentRenderPipeline });
                var camera = Camera.main;
                if (camera == null) { Finish("Benchmark camera is missing."); return; }
                if (s_Target == null)
                {
                    s_Target = new RenderTexture(320, 180, 24, RenderTextureFormat.ARGBHalf) { name = "Mobile PlayMode Validation Target" };
                    s_Target.Create(); camera.targetTexture = s_Target;
                }
                camera.Render();
                if (ticks < 60) return;
                if (!(RenderPipelineManager.currentPipeline is HDRenderPipeline pipeline) || pipeline == null)
                { Finish("No HDRP pipeline in PlayMode."); return; }
                EditorApplication.isPlaying = false; return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode || ticks < 30) return;
            int cycle = SessionState.GetInt(Key + "Cycle", 0);
            if (cycle > 0)
            {
                // Measure after disposal, allowing the test to distinguish live
                // RenderGraph pooling from resources leaked across play sessions.
                GraphicsSettings.renderPipelineAsset = null; QualitySettings.renderPipeline = null;
                typeof(RenderPipelineManager).GetMethod("PrepareRenderPipeline", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { null });
                int materials = Resources.FindObjectsOfTypeAll<Material>().Count(m => m.shader != null && m.shader.name.StartsWith("Hidden/HDRP/Mobile/"));
                int graphs = RenderGraph.GetRegisteredRenderGraphs().Count;
                int textures = Resources.FindObjectsOfTypeAll<RenderTexture>().Count(t => t.name.StartsWith("Mobile "));
                int volumes = Resources.FindObjectsOfTypeAll<VolumeComponent>().Length;
                int baseline = SessionState.GetInt(Key + "MaterialBaseline", -1);
                if (baseline < 0)
                {
                    SessionState.SetInt(Key + "MaterialBaseline", materials); SessionState.SetInt(Key + "GraphBaseline", graphs);
                    SessionState.SetInt(Key + "TextureBaseline", textures); SessionState.SetInt(Key + "VolumeBaseline", volumes);
                }
                else if (materials != baseline || graphs != SessionState.GetInt(Key + "GraphBaseline", -1)
                    || textures != SessionState.GetInt(Key + "TextureBaseline", -1) || volumes > SessionState.GetInt(Key + "VolumeBaseline", -1))
                { Finish($"Resource count changed after PlayMode cycle {cycle}: materials={materials}, graphs={graphs}, textures={textures}, volume components={volumes}"); return; }
            }
            if (cycle >= 6) { Finish(null); return; }
            var asset = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>("Assets/MobileBenchmark/Mobile.asset");
            GraphicsSettings.renderPipelineAsset = asset; QualitySettings.renderPipeline = asset;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = cycle < 3 ? EnterPlayModeOptions.None : EnterPlayModeOptions.DisableDomainReload;
            SessionState.SetInt(Key + "Ticks", 0); EditorApplication.isPlaying = true;
        }

        static void Finish(string error)
        {
            SessionState.SetBool(Key + "Active", false);
            EditorSettings.enterPlayModeOptionsEnabled = SessionState.GetBool(Key + "Enabled", false);
            EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)SessionState.GetInt(Key + "Options", 0);
            string report = error == null ? "PASSED: 3 PlayMode cycles with domain reload, 3 without; no errors or owned material/RenderGraph/RenderTexture/VolumeComponent count growth." : "FAILED: " + error;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath), "mobile-playmode-validation.txt"), report);
            if (error == null) Debug.Log(report); else Debug.LogError(report);
            EditorApplication.Exit(error == null ? 0 : 1);
        }
    }
}
