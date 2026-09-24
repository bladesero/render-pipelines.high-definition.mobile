using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace UnityEditor.Rendering.HighDefinition
{
    internal sealed class MobileSettingsWindow : EditorWindow
    {
        Camera m_Camera;
        VolumeStack m_Stack;
        Vector2 m_Scroll;
        readonly List<MobileIssue> m_Issues = new List<MobileIssue>();
        double m_NextRead;
        bool m_Pending;
        bool m_HasSample;
        float m_EV;
        int m_Generation;

        [MenuItem("Rendering/HDRP Mobile/Settings")]
        internal static void Open() => GetWindow<MobileSettingsWindow>("HDRP Mobile");
        void OnEnable() { m_Stack = VolumeManager.instance.CreateStack(); }
        void OnDisable() { ++m_Generation; if (m_Stack != null) VolumeManager.instance.DestroyStack(m_Stack); }
        void OnInspectorUpdate() { Repaint(); }
        void OnGUI()
        {
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            var asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
            EditorGUILayout.ObjectField("Active Pipeline", asset, typeof(HDRenderPipelineAsset), false);
            var selectedCamera = (Camera)EditorGUILayout.ObjectField("Camera", m_Camera != null ? m_Camera : Camera.main, typeof(Camera), true);
            if (selectedCamera != m_Camera) { ++m_Generation; m_Pending = false; m_HasSample = false; m_Camera = selectedCamera; }
            if (asset != null && m_Camera != null && m_Stack != null)
            {
                MobileExposure.GetVolumeContext(m_Camera, asset, out var anchor, out var mask);
                VolumeManager.instance.Update(m_Stack, anchor, mask);
                EditorGUILayout.LabelField("Renderer", asset.rendererMode.ToString());
                EditorGUILayout.LabelField("Volume Layer Mask", mask.ToString());
                EditorGUILayout.ObjectField("Volume Anchor", anchor, typeof(Transform), true);
                var e = m_Stack.GetComponent<Exposure>();
                EditorGUILayout.LabelField("Requested Exposure", e.mode.value.ToString());
                EditorGUILayout.LabelField("Seed / Fixed EV", e.fixedExposure.value.ToString("F3"));
                EditorGUILayout.LabelField("Compensation / Limits", $"{e.compensation.value:F2} / {e.limitMin.value:F2} … {e.limitMax.value:F2}");
                EditorGUILayout.LabelField("Adaptation", $"{e.adaptationMode.value} / {e.adaptationSpeedDarkToLight.value:F2} / {e.adaptationSpeedLightToDark.value:F2}");
                EditorGUILayout.LabelField("Target Gray", e.targetMidGray.value.ToString());
                FrameSettings frame = new FrameSettings();
                FrameSettings.AggregateFrameSettings(ref frame, m_Camera, m_Camera.GetComponent<HDAdditionalCameraData>(), asset);
                EditorGUILayout.LabelField("Exposure Control", frame.IsEnabled(FrameSettingsField.ExposureControl).ToString());
                bool sceneManual = m_Camera.cameraType == CameraType.SceneView && HDAdditionalSceneViewSettings.sceneExposureOverriden;
                string effective = !frame.IsEnabled(FrameSettingsField.ExposureControl) ? "Disabled (multiplier 1)" : sceneManual ? "Scene View manual" : MobileExposure.IsAutomatic(e) ? (e.meteringMode.value == MeteringMode.Average ? "Automatic / Average" : "Automatic / Center Weighted") : e.mode.value.ToString();
                EditorGUILayout.LabelField("Effective Mobile Exposure", effective);
                if (MobileExposure.IsAutomatic(e) && (e.mode.value != ExposureMode.Automatic || (e.meteringMode.value != MeteringMode.Average && e.meteringMode.value != MeteringMode.CenterWeighted)))
                    EditorGUILayout.HelpBox("Mobile uses geometric mean metering. Histogram percentiles, curves, masks and procedural metering are not evaluated.", MessageType.Info);
                var texture = MobileExposureControl.GetExposureTexture(m_Camera);
                if (texture != null && !m_Pending && SystemInfo.supportsAsyncGPUReadback && EditorApplication.timeSinceStartup >= m_NextRead)
                {
                    m_Pending = true; m_NextRead = EditorApplication.timeSinceStartup + 0.5;
                    int generation = m_Generation;
                    AsyncGPUReadback.Request(texture, 0, request =>
                    {
                        if (this == null || generation != m_Generation) return;
                        m_Pending = false;
                        if (!request.hasError) { m_EV = request.GetData<float>()[1]; m_HasSample = true; }
                    });
                }
                EditorGUILayout.LabelField("GPU EV (sampled)", texture != null ? (m_HasSample ? m_EV.ToString("F3") : "Waiting for sample") : "Fixed / no history");
                if (GUILayout.Button("Reset Camera Exposure")) MobileExposureControl.Reset(m_Camera);
                var sky = MobileSky.Resolve(asset.mobileSettings, m_Stack, RenderSettings.skybox, RenderSettings.sun != null ? RenderSettings.sun.intensity : 0);
                string skySource = sky.mode == 0 ? "Clear color" : asset.mobileSettings.sky != null ? "Pipeline cubemap" : !sky.cameraExposure ? "Legacy skybox" : sky.mode == 1 ? "Volume HDRI" : sky.mode == 2 ? "Volume Gradient" : "Physical sky approximation";
                var cameraData = m_Camera.GetComponent<HDAdditionalCameraData>();
                if ((cameraData != null && cameraData.clearColorMode != HDAdditionalCameraData.ClearColorMode.Sky) || (cameraData == null && m_Camera.clearFlags != CameraClearFlags.Skybox)) skySource = "Camera clear color (sky not drawn)";
                EditorGUILayout.LabelField("Sky Source", skySource);
                if (sky.cube != null) EditorGUILayout.ObjectField("Sky Cubemap", sky.cube, typeof(Texture), false);
                var environment = MobileEnvironmentControl.active?.Get(m_Camera);
                EditorGUILayout.LabelField("Environment IBL", !asset.mobileSettings.environmentLighting ? "Disabled" : environment == null ? "No resolved sky / camera not rendered" : $"{environment.size}² / generation {environment.generation} / " + (environment.cursor < 0 ? "Ready (cached)" : $"Updating {environment.cursor}/{environment.totalJobs}"));
                EditorGUILayout.LabelField("Environment Diffuse", asset.mobileSettings.environmentDiffuseSource.ToString());
                if (asset.mobileSettings.environmentLighting)
                    EditorGUILayout.HelpBox("Prefer Baked Probes preserves native per-object SH whenever the scene has a baked probe set; otherwise it uses sky irradiance. Runtime Sky replaces SH on all non-lightmapped objects. Lightmaps always retain priority. This is a scene-wide policy, not per-renderer probe validity detection.", MessageType.Info);
                if (GUILayout.Button("Rebuild Camera Environment")) MobileEnvironmentControl.Reset(m_Camera);
                EditorGUILayout.LabelField("Lightmaps / Mode", $"{LightmapSettings.lightmaps.Length} / {LightmapSettings.lightmapsMode}");
                EditorGUILayout.LabelField("Light Probes", LightmapSettings.lightProbes != null ? LightmapSettings.lightProbes.count.ToString() : "Missing");
                EditorGUILayout.LabelField("Volume Profiles (click to edit)", EditorStyles.boldLabel);
                var defaults = HDRenderPipelineGlobalSettings.instance?.volumeProfile;
                if (defaults != null && GUILayout.Button("Default: " + defaults.name)) Selection.activeObject = defaults;
                foreach (var volume in VolumeManager.instance.GetVolumes(mask))
                    if (volume != null && volume.sharedProfile != null && GUILayout.Button(volume.name + " → " + volume.sharedProfile.name)) Selection.activeObject = volume.sharedProfile;
            }
            EditorGUILayout.Space();
            if (GUILayout.Button(MobileLightingWorkflow.running ? "Cancel Bake" : "Bake Saved Scene via Desktop"))
            {
                if (MobileLightingWorkflow.running) MobileLightingWorkflow.Cancel();
                else
                    try { MobileLightingWorkflow.Bake(); }
                    catch (System.Exception e) { Debug.LogError(e.Message); }
            }
            if (GUILayout.Button("Check Loaded Scenes"))
            {
                m_Issues.Clear();
                for (int i = 0; i < SceneManager.sceneCount; ++i) MobileDiagnostics.Collect(SceneManager.GetSceneAt(i), m_Issues);
            }
            foreach (var issue in m_Issues)
            {
                EditorGUILayout.HelpBox(issue.message, issue.severity);
                if (issue.context != null && GUILayout.Button("Locate " + issue.context.name)) { Selection.activeObject = issue.context; EditorGUIUtility.PingObject(issue.context); }
            }
            EditorGUILayout.EndScrollView();
        }
    }

    internal readonly struct MobileIssue
    {
        internal readonly Object context;
        internal readonly string message;
        internal readonly MessageType severity;
        internal MobileIssue(Object context, string message, MessageType severity = MessageType.Warning)
        { this.context = context; this.message = message; this.severity = severity; }
    }

    internal static class MobileDiagnostics
    {
        internal static void Collect(Scene scene, List<MobileIssue> issues)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            if (string.IsNullOrEmpty(scene.path) || scene.isDirty) issues.Add(new MobileIssue(null, scene.name + ": save the scene before baking.", MessageType.Error));
            var messages = new List<string>();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (var material in r.sharedMaterials)
                    {
                        messages.Clear(); MobileRenderEditor.AuditMaterial(material, r.name, messages);
                        foreach (var message in messages) issues.Add(new MobileIssue(material != null ? (Object)material : r, message));
                    }
                    if (r.lightProbeUsage == LightProbeUsage.UseProxyVolume) issues.Add(new MobileIssue(r, "LPPV is unsupported."));
                    var mesh = r is SkinnedMeshRenderer skin ? skin.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                    if (GameObjectUtility.AreStaticEditorFlagsSet(r.gameObject, StaticEditorFlags.ContributeGI) && mesh != null && !mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1))
                        issues.Add(new MobileIssue(r, "GI mesh has no UV2. Generate lightmap UVs before baking.", MessageType.Error));
                    if (r.lightmapIndex >= 0 && r.lightmapIndex < 65534 && (r.lightmapIndex >= LightmapSettings.lightmaps.Length || LightmapSettings.lightmaps[r.lightmapIndex].lightmapColor == null))
                        issues.Add(new MobileIssue(r, "Missing assigned lightmap.", MessageType.Error));
                }
                foreach (var light in root.GetComponentsInChildren<Light>(true))
                    if (light.lightmapBakeType == LightmapBakeType.Mixed && Lightmapping.lightingSettings != null && Lightmapping.lightingSettings.mixedBakeMode != MixedLightingMode.IndirectOnly)
                        issues.Add(new MobileIssue(light, "Mixed lights require Baked Indirect; Shadowmask and Subtractive are unsupported.", MessageType.Error));
                foreach (var probe in root.GetComponentsInChildren<ReflectionProbe>(true))
                {
                    if (probe.mode == ReflectionProbeMode.Realtime) issues.Add(new MobileIssue(probe, "Realtime reflection probes are unsupported."));
                    else if (probe.TryGetComponent<HDAdditionalReflectionData>(out var hd) && hd.bakedTexture == null) issues.Add(new MobileIssue(probe, "No baked reflection texture. Bake reflections before Mobile validation."));
                    if (probe.TryGetComponent<HDAdditionalReflectionData>(out var data) && data.proxyVolume != null) issues.Add(new MobileIssue(probe, "Mobile box projection uses the influence box only; a custom proxy volume is not evaluated."));
                }
            }
            messages.Clear(); MobileRenderEditor.AuditScene(scene, messages);
            foreach (var message in messages)
                if (!issues.Exists(i => i.message == message))
                {
                    Object context = null;
                    int longest = 0;
                    foreach (var root in scene.GetRootGameObjects())
                        foreach (var component in root.GetComponentsInChildren<Component>(true))
                        {
                            if (component == null) continue;
                            string prefix = scene.path + "/" + component.name + ":";
                            if (message.StartsWith(prefix) && prefix.Length > longest) { context = component; longest = prefix.Length; }
                        }
                    issues.Add(new MobileIssue(context, message));
                }
            if (Lightmapping.lightingSettings == null) issues.Add(new MobileIssue(null, "Assign Lighting Settings before baking.", MessageType.Error));
        }
    }
}
