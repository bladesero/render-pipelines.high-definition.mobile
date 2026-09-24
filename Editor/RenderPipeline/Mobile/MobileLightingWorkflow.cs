using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.HighDefinition
{
    [InitializeOnLoad]
    internal static class MobileLightingWorkflow
    {
        static RenderPipelineAsset s_Graphics, s_Quality;
        static HDRenderPipelineAsset s_Desktop;
        static LightingSettings s_OriginalLighting, s_TemporaryLighting;
        static LightingDataAsset s_OriginalData;
        static bool s_AsyncShaders, s_Cancelled;
        static Scene s_Scene;
        static BakeSnapshot s_Snapshot;
        static byte[] s_GraphicsFile, s_QualityFile;
        internal static bool running => s_Desktop != null;
        internal static bool lastSucceeded { get; private set; }
        static MobileLightingWorkflow() { AssemblyReloadEvents.beforeAssemblyReload += Cancel; EditorApplication.quitting += Cancel; }

        [MenuItem("Rendering/HDRP Mobile/Bake Loaded Scene")]
        internal static void Bake()
        {
            if (running || Lightmapping.isRunning) throw new InvalidOperationException("A bake is already running.");
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before baking.");
            if (SceneManager.sceneCount != 1) throw new InvalidOperationException("Bake one saved scene at a time. Close additive scenes first.");
            if (!HDUtils.IsSupportedBuildTarget(EditorUserBuildSettings.activeBuildTarget)) throw new InvalidOperationException("Select a supported Desktop build target for the HDRP lightmapper.");
            s_Scene = SceneManager.GetActiveScene();
            var issues = new List<MobileIssue>(); MobileDiagnostics.Collect(s_Scene, issues);
            var errors = issues.Where(i => i.severity == MessageType.Error).Select(i => i.message).ToArray();
            if (errors.Length > 0) throw new InvalidOperationException(string.Join("\n", errors));
            var asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
            if (asset == null) throw new InvalidOperationException("An HDRP asset is required.");
            s_Graphics = GraphicsSettings.renderPipelineAsset; s_Quality = QualitySettings.renderPipeline;
            s_OriginalLighting = Lightmapping.lightingSettings; s_OriginalData = Lightmapping.lightingDataAsset;
            s_AsyncShaders = ShaderUtil.allowAsyncCompilation; s_Cancelled = false;
            lastSucceeded = false;
            s_GraphicsFile = File.ReadAllBytes("ProjectSettings/GraphicsSettings.asset");
            s_QualityFile = File.ReadAllBytes("ProjectSettings/QualitySettings.asset");
            try
            {
                s_Snapshot = new BakeSnapshot(s_Scene);
                s_Desktop = Object.Instantiate(asset); s_Desktop.hideFlags = HideFlags.HideAndDontSave; s_Desktop.rendererMode = HDRendererMode.Desktop;
                s_TemporaryLighting = Object.Instantiate(s_OriginalLighting); s_TemporaryLighting.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
                s_TemporaryLighting.lightmapper = LightingSettings.Lightmapper.ProgressiveCPU;
                s_TemporaryLighting.bakedGI = true; s_TemporaryLighting.realtimeGI = false;
                s_TemporaryLighting.mixedBakeMode = MixedLightingMode.IndirectOnly;
                Lightmapping.lightingSettings = s_TemporaryLighting;
                GraphicsSettings.renderPipelineAsset = s_Desktop; QualitySettings.renderPipeline = s_Desktop;
                ShaderUtil.allowAsyncCompilation = false;
                // Wait for the editor's next render to initialize the Desktop pipeline.
                EditorApplication.delayCall += BeginBake;
            }
            catch { Restore(false); throw; }
        }
        static void BeginBake()
        {
            if (!running) return;
            try
            {
                // Obscured views and batch editors may not have rendered yet.
                typeof(RenderPipelineManager).GetMethod("PrepareRenderPipeline", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { s_Desktop });
                Lightmapping.bakeCompleted += Completed;
                EditorApplication.update += Monitor;
                if (!Lightmapping.BakeAsync()) throw new InvalidOperationException("Lightmapper could not start.");
            }
            catch (Exception e) { Debug.LogException(e); Restore(false); }
        }
        static void Monitor()
        {
            if (!running || Application.isBatchMode) return;
            if (EditorUtility.DisplayCancelableProgressBar("HDRP Mobile Baking", "Desktop lightmaps, then HDRP reflection probes", Lightmapping.buildProgress)) Cancel();
        }
        static void Completed()
        {
            if (!running) return;
            bool success = false;
            try
            {
                success = !s_Cancelled && LightmapSettings.lightmaps.Length > 0;
                if (success)
                {
                    // Lightmapping can invalidate the active SRP while importing results.
                    GraphicsSettings.renderPipelineAsset = s_Desktop; QualitySettings.renderPipeline = s_Desktop;
                    typeof(RenderPipelineManager).GetMethod("PrepareRenderPipeline", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { s_Desktop });
                    success = HDBakedReflectionSystem.BakeProbes(Object.FindObjectsOfType<HDAdditionalReflectionData>());
                }
            }
            catch (Exception e) { Debug.LogException(e); }
            finally { Restore(success); }
            if (success)
            {
                EditorSceneManager.SaveScene(s_Scene); AssetDatabase.SaveAssets();
                lastSucceeded = true;
                Debug.Log("HDRP Mobile bake completed. Reopen Settings to validate lightmaps and probes.");
            }
            else Debug.LogWarning("HDRP Mobile bake did not complete; pipeline and lighting settings restored.");
        }
        [MenuItem("Rendering/HDRP Mobile/Cancel Bake")]
        internal static void Cancel()
        {
            if (!running) return;
            s_Cancelled = true; Lightmapping.bakeCompleted -= Completed;
            Lightmapping.Cancel(); Restore(false);
        }
        static void Restore(bool success)
        {
            Lightmapping.bakeCompleted -= Completed; EditorApplication.update -= Monitor;
            EditorApplication.delayCall -= BeginBake;
            try
            {
                Lightmapping.lightingSettings = s_OriginalLighting;
                GraphicsSettings.renderPipelineAsset = s_Graphics; QualitySettings.renderPipeline = s_Quality;
                typeof(RenderPipelineManager).GetMethod("PrepareRenderPipeline", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { GraphicsSettings.currentRenderPipeline });
                ShaderUtil.allowAsyncCompilation = s_AsyncShaders;
                if (!success) { s_Snapshot?.Restore(); Lightmapping.lightingDataAsset = s_OriginalData; }
            }
            finally
            {
                Object.DestroyImmediate(s_TemporaryLighting); Object.DestroyImmediate(s_Desktop);
                s_TemporaryLighting = null; s_Desktop = null; EditorUtility.ClearProgressBar();
                s_Snapshot = null;
                if (s_GraphicsFile != null) File.WriteAllBytes("ProjectSettings/GraphicsSettings.asset", s_GraphicsFile);
                if (s_QualityFile != null) File.WriteAllBytes("ProjectSettings/QualitySettings.asset", s_QualityFile);
                s_GraphicsFile = null; s_QualityFile = null;
            }
        }

        // The lightmapper can overwrite existing output before bakeCompleted.
        // Back up only explicitly referenced bake outputs, never unrelated assets.
        // Newly generated files are retained on cancellation, without scene references.
        sealed class BakeSnapshot
        {
            readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>();
            readonly LightmapData[] maps = LightmapSettings.lightmaps;
            readonly LightmapsMode mode = LightmapSettings.lightmapsMode;
            readonly LightProbes probes = LightmapSettings.lightProbes;
            readonly Dictionary<Renderer, Vector4> scales = new Dictionary<Renderer, Vector4>();
            readonly Dictionary<Renderer, int> indices = new Dictionary<Renderer, int>();
            readonly Dictionary<HDAdditionalReflectionData, Texture> reflections = new Dictionary<HDAdditionalReflectionData, Texture>();
            internal BakeSnapshot(Scene scene)
            {
                Save(Lightmapping.lightingDataAsset);
                foreach (var map in maps) { Save(map.lightmapColor); Save(map.lightmapDir); Save(map.shadowMask); }
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true)) { scales.Add(renderer, renderer.lightmapScaleOffset); indices.Add(renderer, renderer.lightmapIndex); }
                    foreach (var probe in root.GetComponentsInChildren<HDAdditionalReflectionData>(true))
                    { reflections.Add(probe, probe.bakedTexture); Save(probe.bakedTexture); }
                }
            }
            void Save(Object asset)
            {
                string path = AssetDatabase.GetAssetPath(asset);
                if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal) && File.Exists(path) && !files.ContainsKey(path))
                {
                    files.Add(path, File.ReadAllBytes(path));
                    if (File.Exists(path + ".meta")) files.Add(path + ".meta", File.ReadAllBytes(path + ".meta"));
                }
            }
            internal void Restore()
            {
                var changed = new HashSet<string>();
                foreach (var file in files)
                {
                    if (!File.Exists(file.Key) || !File.ReadAllBytes(file.Key).SequenceEqual(file.Value))
                    {
                        File.WriteAllBytes(file.Key, file.Value);
                        changed.Add(file.Key.EndsWith(".meta", StringComparison.Ordinal) ? file.Key.Substring(0, file.Key.Length - 5) : file.Key);
                    }
                }
                foreach (var path in changed) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                LightmapSettings.lightmaps = maps; LightmapSettings.lightmapsMode = mode; LightmapSettings.lightProbes = probes;
                foreach (var renderer in scales.Keys) if (renderer != null) { renderer.lightmapScaleOffset = scales[renderer]; renderer.lightmapIndex = indices[renderer]; }
                foreach (var probe in reflections) if (probe.Key != null) probe.Key.SetTexture(ProbeSettings.Mode.Baked, probe.Value);
            }
        }
    }
}
