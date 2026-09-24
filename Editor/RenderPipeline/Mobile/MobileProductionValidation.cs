using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.HighDefinition
{
    public static class MobileProductionValidation
    {
        internal static void RequireIsolatedProject()
        {
            const string marker = ".hdrp-mobile-validation";
            if (!Application.isBatchMode || !File.Exists(marker) || !string.Equals(File.ReadAllText(marker).Trim().Replace('\\', '/'), Application.dataPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This acceptance entry point requires a disposable project with .hdrp-mobile-validation containing its absolute Assets path.");
        }
        public static void WorkflowAcceptance()
        {
            RequireIsolatedProject();
            try
            {
                const string folder = "Assets/MobileBenchmark";
                EditorSceneManager.OpenScene(folder + "/Benchmark.unity", OpenSceneMode.Single);
                var mobile = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(folder + "/Mobile.asset");
                GraphicsSettings.renderPipelineAsset = mobile; QualitySettings.renderPipeline = mobile; Prepare(mobile);
                var detail = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/Detail.asset");
                if (detail == null)
                {
                    detail = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                    detail.SetPixels(new[] { new Color(0.6f, 0.5f, 0.5f, 0.5f), new Color(0.4f, 0.5f, 0.5f, 0.5f), new Color(0.4f, 0.5f, 0.5f, 0.5f), new Color(0.6f, 0.5f, 0.5f, 0.5f) }); detail.Apply();
                    AssetDatabase.CreateAsset(detail, folder + "/Detail.asset");
                }
                var stone = AssetDatabase.LoadAssetAtPath<Material>(folder + "/Stone.mat");
                stone.SetTexture("_DetailMap", detail); stone.SetFloat("_DetailAlbedoScale", 0.25f); HDShaderUtils.ResetMaterialKeywords(stone); EditorUtility.SetDirty(stone);
                EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene()); AssetDatabase.SaveAssets();
                var originalLighting = Lightmapping.lightingSettings;
                // This fixture exists only inside the explicitly marked disposable project.
                originalLighting.lightmapResolution = 32; originalLighting.lightmapMaxSize = 1024;
                EditorUtility.SetDirty(originalLighting); AssetDatabase.SaveAssets();
                var originalData = Lightmapping.lightingDataAsset;
                var graphicsBytes = File.ReadAllBytes("ProjectSettings/GraphicsSettings.asset");
                var qualityBytes = File.ReadAllBytes("ProjectSettings/QualitySettings.asset");
                var profileBytes = File.ReadAllBytes(folder + "/Volume.asset");
                var sceneBytes = File.ReadAllBytes(folder + "/Benchmark.unity");
                MobileLightingWorkflow.Bake(); MobileLightingWorkflow.Cancel();
                if (MobileLightingWorkflow.running || GraphicsSettings.renderPipelineAsset != mobile || QualitySettings.renderPipeline != mobile || Lightmapping.lightingSettings != originalLighting || Lightmapping.lightingDataAsset != originalData || !File.ReadAllBytes(folder + "/Benchmark.unity").SequenceEqual(sceneBytes)) throw new Exception("Bake cancellation did not restore authoring state");
                Debug.Log("MOBILE_WORKFLOW_CANCEL_PASSED");
                if (!File.ReadAllBytes("ProjectSettings/GraphicsSettings.asset").SequenceEqual(graphicsBytes) || !File.ReadAllBytes("ProjectSettings/QualitySettings.asset").SequenceEqual(qualityBytes)) throw new Exception("Cancellation changed pipeline setting files");
                EditorSceneManager.OpenScene(folder + "/Benchmark.unity", OpenSceneMode.Single);
                originalLighting = Lightmapping.lightingSettings;
                MobileLightingWorkflow.Bake();
                double started = EditorApplication.timeSinceStartup;
                EditorApplication.CallbackFunction check = null;
                check = () =>
                {
                    if (MobileLightingWorkflow.running && EditorApplication.timeSinceStartup - started < 600) return;
                    EditorApplication.update -= check;
                    try
                    {
                        if (MobileLightingWorkflow.running) { MobileLightingWorkflow.Cancel(); throw new Exception("Bake workflow timed out"); }
                        if (!MobileLightingWorkflow.lastSucceeded) throw new Exception("Bake workflow reported failure");
                        if (!File.ReadAllBytes("ProjectSettings/GraphicsSettings.asset").SequenceEqual(graphicsBytes) || !File.ReadAllBytes("ProjectSettings/QualitySettings.asset").SequenceEqual(qualityBytes)) throw new Exception("Bake changed pipeline setting files");
                        if (GraphicsSettings.renderPipelineAsset != mobile || QualitySettings.renderPipeline != mobile || Lightmapping.lightingSettings != originalLighting || !File.ReadAllBytes(folder + "/Volume.asset").SequenceEqual(profileBytes)) throw new Exception("Successful bake did not restore pipeline/profile state");
                        EditorSceneManager.OpenScene(folder + "/Benchmark.unity", OpenSceneMode.Single); Prepare(mobile); Capture("Workflow");
                        Debug.Log("MOBILE_WORKFLOW_BAKE_RELOAD_PASSED"); EditorApplication.Exit(0);
                    }
                    catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
                };
                EditorApplication.update += check;
            }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
        public static void CancelActiveBakeAcceptance()
        {
            RequireIsolatedProject();
            try
            {
                const string folder = "Assets/MobileBenchmark";
                EditorSceneManager.OpenScene(folder + "/Benchmark.unity", OpenSceneMode.Single);
                var mobile = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(folder + "/Mobile.asset");
                GraphicsSettings.renderPipelineAsset = mobile; QualitySettings.renderPipeline = mobile; Prepare(mobile);
                var lighting = Lightmapping.lightingSettings; var data = Lightmapping.lightingDataAsset;
                var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
                var graphics = File.ReadAllBytes("ProjectSettings/GraphicsSettings.asset"); var quality = File.ReadAllBytes("ProjectSettings/QualitySettings.asset");
                MobileLightingWorkflow.Bake();
                double started = EditorApplication.timeSinceStartup;
                EditorApplication.CallbackFunction cancel = null;
                cancel = () =>
                {
                    double elapsed = EditorApplication.timeSinceStartup - started;
                    if (elapsed < 2 || (!Lightmapping.isRunning && elapsed < 30)) return;
                    EditorApplication.update -= cancel;
                    try
                    {
                        if (!Lightmapping.isRunning) throw new Exception("Could not exercise active lightmapper cancellation");
                        MobileLightingWorkflow.Cancel();
                        if (MobileLightingWorkflow.running || GraphicsSettings.renderPipelineAsset != mobile || QualitySettings.renderPipeline != mobile || Lightmapping.lightingSettings != lighting || Lightmapping.lightingDataAsset != data) throw new Exception("Active cancellation did not restore state");
                        foreach (var file in files) if (!File.ReadAllBytes(file.Key).SequenceEqual(file.Value)) throw new Exception("Active cancellation changed " + file.Key);
                        if (!File.ReadAllBytes("ProjectSettings/GraphicsSettings.asset").SequenceEqual(graphics) || !File.ReadAllBytes("ProjectSettings/QualitySettings.asset").SequenceEqual(quality)) throw new Exception("Active cancellation changed pipeline settings");
                        EditorSceneManager.OpenScene(folder + "/Benchmark.unity", OpenSceneMode.Single); Prepare(mobile); Capture("Cancelled");
                        Debug.Log("MOBILE_ACTIVE_BAKE_CANCEL_RELOAD_PASSED"); EditorApplication.Exit(0);
                    }
                    catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
                };
                EditorApplication.update += cancel;
            }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
        // Only the disposable validation project invokes this batch entry point.
        public static void BakeAcceptance()
        {
            RequireIsolatedProject();
            try
            {
                PlayerSettings.colorSpace = ColorSpace.Linear;
                const string folder = "Assets/MobileBenchmark";
                var initialScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (string.IsNullOrEmpty(initialScene.path)) EditorSceneManager.SaveScene(initialScene, "Assets/ValidationBootstrap.unity");
                if (!File.Exists(folder + "/Benchmark.unity")) MobileBenchmarkBuilder.CreateAt(folder);
                EditorSceneManager.OpenScene(folder + "/Benchmark.unity", OpenSceneMode.Single);
                var settings = Lightmapping.lightingSettings;
                settings.lightmapResolution = 8; settings.lightmapper = LightingSettings.Lightmapper.ProgressiveCPU;
                settings.indirectSampleCount = 32; settings.directSampleCount = 16; settings.environmentSampleCount = 32;
                settings.lightmapMaxSize = 512;
                var bakedLight = new GameObject("Baked acceptance light").AddComponent<Light>();
                bakedLight.type = LightType.Point; bakedLight.range = 8; bakedLight.transform.position = new Vector3(0, 2, 0);
                bakedLight.gameObject.AddComponent<HDAdditionalLightData>().SetIntensity(1000, LightUnit.Lumen);
                bakedLight.lightmapBakeType = LightmapBakeType.Baked;
                var desktop = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                desktop.hideFlags = HideFlags.HideAndDontSave;
                var mobile = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(folder + "/Mobile.asset");
                ShaderUtil.allowAsyncCompilation = false;
                foreach (var mode in new[] { LightmapsMode.NonDirectional, LightmapsMode.CombinedDirectional })
                {
                    GraphicsSettings.renderPipelineAsset = desktop; QualitySettings.renderPipeline = desktop;
                    Prepare(desktop);
                    LightmapSettings.lightmapsMode = mode;
                    if (!Lightmapping.Bake()) throw new Exception("Lightmap bake failed: " + mode);
                    Prepare(desktop);
                    if (!HDBakedReflectionSystem.BakeProbes(Object.FindObjectsOfType<HDAdditionalReflectionData>())) throw new Exception("Probe bake failed");
                    foreach (var probe in Object.FindObjectsOfType<ReflectionProbe>()) probe.customBakedTexture = null;
                    EditorUtility.SetDirty(settings); EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene()); AssetDatabase.SaveAssets();
                    EditorSceneManager.OpenScene(folder + "/Benchmark.unity", OpenSceneMode.Single);
                    settings = Lightmapping.lightingSettings;
                    if (LightmapSettings.lightmaps.Length == 0 || LightmapSettings.lightmaps.Any(l => l.lightmapColor == null || (mode == LightmapsMode.CombinedDirectional && l.lightmapDir == null))) throw new Exception("Lightmap references did not survive reload.");
                    if (LightmapSettings.lightProbes == null || LightmapSettings.lightProbes.count == 0) throw new Exception("Baked SH probes missing.");
                    if (Object.FindObjectsOfType<HDAdditionalReflectionData>().Any(p => p.bakedTexture == null)) throw new Exception("Baked reflection missing after reload.");
                    GraphicsSettings.renderPipelineAsset = mobile; QualitySettings.renderPipeline = mobile; Prepare(mobile);
                    Capture(mode.ToString());
                    Debug.Log("MOBILE_BAKE_ACCEPTANCE=" + mode + "; lightmaps=" + LightmapSettings.lightmaps.Length + "; probes=" + LightmapSettings.lightProbes.count);
                }
                AssetDatabase.SaveAssets(); EditorApplication.Exit(0);
            }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
        static void Prepare(RenderPipelineAsset asset) => typeof(RenderPipelineManager).GetMethod("PrepareRenderPipeline", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { asset });
        static void Capture(string name)
        {
            var camera = Object.FindObjectOfType<Camera>();
            var target = new RenderTexture(320, 180, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear); target.Create();
            var read = new Texture2D(320, 180, TextureFormat.RGBAFloat, false, true);
            var old = camera.targetTexture; var active = RenderTexture.active;
            try
            {
                camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
                read.ReadPixels(new Rect(0, 0, 320, 180), 0, 0); read.Apply();
                var pixels = read.GetPixels();
                if (pixels.Any(c => float.IsNaN(c.r) || float.IsInfinity(c.r))) throw new Exception("Non-finite baked image");
                if (pixels.Count(c => c.maxColorComponent > 0.02f && c.maxColorComponent < 0.98f) < 1000) throw new Exception("Baked image lacks visible midtones");
                File.WriteAllBytes("mobile-bake-" + name + ".exr", read.EncodeToEXR());
            }
            finally { camera.targetTexture = old; RenderTexture.active = active; target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(read); }
        }
    }
}
