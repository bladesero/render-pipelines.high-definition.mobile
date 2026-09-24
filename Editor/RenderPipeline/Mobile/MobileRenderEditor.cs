using System.Collections.Generic;
using System.Linq;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace UnityEditor.Rendering.HighDefinition
{
    internal static class MobileRenderEditor
    {
        const string Root = "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/Mobile/Shaders/";

        internal static void AssignResources(HDRenderPipelineAsset asset)
        {
            Undo.RecordObject(asset, "Assign HDRP Mobile resources");
            asset.mobileResources.compositeShader = AssetDatabase.LoadAssetAtPath<Shader>(Root + "MobileComposite.shader");
            asset.mobileResources.skyShader = AssetDatabase.LoadAssetAtPath<Shader>(Root + "MobileSky.shader");
            asset.mobileResources.errorShader = AssetDatabase.LoadAssetAtPath<Shader>(Root + "MobileError.shader");
            EditorUtility.SetDirty(asset);
        }

        internal static bool DrawInspector(SerializedObject serialized)
        {
            var mode = serialized.FindProperty("m_RendererMode");
            EditorGUILayout.PropertyField(mode);
            if (mode.hasMultipleDifferentValues || mode.enumValueIndex != (int)HDRendererMode.Mobile) return false;
            EditorGUILayout.HelpBox("Android Vulkan forward renderer. Exposure Control follows HDRP Frame Settings. Other Mobile features are configured below. Performance requires device measurement.", MessageType.Info);
            var settings = serialized.FindProperty("m_MobileSettings");
            void Group(string title, params string[] fields)
            {
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                foreach (var field in fields) EditorGUILayout.PropertyField(settings.FindPropertyRelative(field));
            }
            Group("Resolution", "fixed1080p", "renderScale");
            Group("Lighting and Shadows", "additionalLightsPerObject", "mainLightShadows", "shadowAtlasSize", "shadowDistance", "cascadeSplit", "cascadeBlend", "shadowNormalBias");
            Group("Sky and Fog", "sky", "skyFallback", "skyIntensity", "fog", "fogColor", "fogStart", "fogEnd");
            Group("Environment Lighting", "environmentLighting", "environmentResolution", "environmentDiffuseSource", "environmentJobsPerFrame");
            Group("Exposure and Post Processing", "meteringResolution", "fxaa", "bloom");
            Group("Diagnostics", "drawUnsupportedMaterials");
            if (GUILayout.Button("Open Mobile Settings and Diagnostics")) MobileSettingsWindow.Open();
            EditorGUILayout.PropertyField(serialized.FindProperty("m_MobileResources"), true);
            if (GUILayout.Button("Assign Mobile Resources"))
            {
                serialized.ApplyModifiedProperties();
                foreach (HDRenderPipelineAsset asset in serialized.targetObjects) AssignResources(asset);
                serialized.Update();
            }
            if (GUILayout.Button("Audit Loaded Scenes")) AuditLoadedScenes();
            return true;
        }

        [MenuItem("Assets/Create/Rendering/HDRP Mobile Pipeline Asset", priority = 303)]
        static void CreateAsset()
        {
            var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            asset.rendererMode = HDRendererMode.Mobile;
            AssignResources(asset);
            ProjectWindowUtil.CreateAsset(asset, "HDRP Mobile.asset");
        }

        [MenuItem("Rendering/HDRP Mobile/Audit Loaded Scenes")]
        static void AuditLoadedScenes()
        {
            var issues = new List<MobileIssue>();
            for (int i = 0; i < SceneManager.sceneCount; i++) MobileDiagnostics.Collect(SceneManager.GetSceneAt(i), issues);
            if (issues.Count == 0) Debug.Log("HDRP Mobile: no unsupported content found in loaded scenes.");
            foreach (var issue in issues)
                if (issue.severity == MessageType.Error) Debug.LogError(issue.message, issue.context);
                else Debug.LogWarning(issue.message, issue.context);
        }

        internal static void AuditMaterial(Material material, string location, List<string> issues)
        {
            if (material == null) { issues.Add(location + ": missing material."); return; }
            string name = material.shader != null ? material.shader.name : "<missing shader>";
            if (name != "HDRP/Lit" && name != "HDRP/Unlit")
            {
                issues.Add(location + ": " + material.name + " uses unsupported shader " + name + ".");
                return;
            }
            void Unsupported(string property, float allowed, string description)
            {
                if (material.HasProperty(property) && !Mathf.Approximately(material.GetFloat(property), allowed))
                    issues.Add(location + ": " + material.name + " — " + description + ".");
            }
            if (name == "HDRP/Lit")
            {
                Unsupported("_MaterialID", 1, "only Standard Lit is supported");
                Unsupported("_UVBase", 0, "only UV0 base mapping is supported");
                Unsupported("_NormalMapSpace", 0, "object-space normal maps are unsupported");
                Unsupported("_DisplacementMode", 0, "displacement is unsupported");
                Unsupported("_RefractionModel", 0, "refraction is unsupported");
                Unsupported("_CoatMask", 0, "clear coat is unsupported");
                if (material.IsKeywordEnabled("_DETAIL_MAP") && material.GetFloat("_UVDetail") > 1) issues.Add(location + ": " + material.name + " supports Detail UV0/UV1 only.");
            }
            Unsupported("_UVEmissive", 0, "only UV0 emission mapping is supported");
            Unsupported("_DistortionEnable", 0, "distortion is unsupported");
        }

        internal static void DrawMaterialSupport(MaterialEditor editor)
        {
            if (!(GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset asset) || asset.rendererMode != HDRendererMode.Mobile) return;
            var issues = new List<string>();
            foreach (var target in editor.targets) AuditMaterial(target as Material, "Mobile", issues);
            EditorGUILayout.HelpBox(issues.Count == 0 ? "Mobile: Standard Lit/Unlit, base/emission UV0, Detail UV0/UV1. Premultiply expects premultiplied RGB. Desktop material settings are preserved." : string.Join("\n", issues), issues.Count == 0 ? MessageType.Info : MessageType.Warning);
        }

        internal static void AuditScene(Scene scene, List<string> issues)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    string location = scene.path + "/" + AnimationUtility.CalculateTransformPath(renderer.transform, null);
                    foreach (var material in renderer.sharedMaterials) AuditMaterial(material, location, issues);
                    if (renderer.lightProbeUsage == LightProbeUsage.UseProxyVolume)
                        issues.Add(location + ": Light Probe Proxy Volumes are unsupported.");
                }
                foreach (var light in root.GetComponentsInChildren<Light>(true))
                {
                    if (light.lightmapBakeType == LightmapBakeType.Mixed && Lightmapping.lightingSettings != null && Lightmapping.lightingSettings.mixedBakeMode != MixedLightingMode.IndirectOnly)
                        issues.Add(scene.path + "/" + light.name + ": mixed lights require Baked Indirect.");
                    if (light.type != LightType.Directional && light.shadows != LightShadows.None && light.lightmapBakeType != LightmapBakeType.Baked)
                        issues.Add(scene.path + "/" + light.name + ": local realtime shadows are unsupported.");
                    if (light.cookie != null) issues.Add(scene.path + "/" + light.name + ": light cookies are unsupported.");
                    if (light.type == LightType.Area || light.type == LightType.Disc)
                        if (light.lightmapBakeType != LightmapBakeType.Baked) issues.Add(scene.path + "/" + light.name + ": realtime area lights are unsupported.");
                }
                foreach (var probe in root.GetComponentsInChildren<ReflectionProbe>(true))
                    if (probe.mode == ReflectionProbeMode.Realtime
                        || (probe.TryGetComponent<HDAdditionalReflectionData>(out var hdProbe) && hdProbe.mode == ProbeSettings.Mode.Realtime))
                        issues.Add(scene.path + "/" + probe.name + ": realtime reflection probes are unsupported.");
                foreach (var camera in root.GetComponentsInChildren<Camera>(true))
                    if (camera.clearFlags == CameraClearFlags.Nothing || camera.clearFlags == CameraClearFlags.Depth)
                        issues.Add(scene.path + "/" + camera.name + ": camera stacking/preserved color is unsupported; mobile clears every camera.");
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue;
                    string type = component.GetType().Name;
                    if (type == "DecalProjector" || type == "WaterSurface" || type == "CustomPassVolume" || type == "PlanarReflectionProbe" || type == "LocalVolumetricFog" || type == "Terrain" || type == "VisualEffect")
                        issues.Add(scene.path + "/" + component.name + ": " + type + " is unsupported.");
                }
                foreach (var volume in root.GetComponentsInChildren<Volume>(true))
                {
                    if (volume.sharedProfile == null) continue;
                    foreach (var component in volume.sharedProfile.components)
                    {
                        if (component == null) { issues.Add(scene.path + "/" + volume.name + ": missing Volume component."); continue; }
                        if (!component.active || !component.parameters.Any(p => p.overrideState)) continue;
                        if (component is Exposure exposure)
                        {
                            if (exposure.mode.value == ExposureMode.AutomaticHistogram || exposure.mode.value == ExposureMode.CurveMapping)
                                issues.Add(scene.path + "/" + volume.name + ": Mobile uses Automatic geometric-mean metering; histogram/curve controls are ignored.");
                        }
                        else if (component is Tonemapping tone)
                        {
                            if (tone.mode.value == TonemappingMode.Custom || tone.mode.value == TonemappingMode.External)
                                issues.Add(scene.path + "/" + volume.name + ": Custom/External tonemapping is unsupported.");
                        }
                        else if (component is HDRISky hdri)
                        {
                            if (hdri.enableBackplate.value || hdri.distortionMode.value != HDRISky.DistortionMode.None)
                                issues.Add(scene.path + "/" + volume.name + ": Mobile HDRI sky ignores backplate and distortion.");
                        }
                        else if (component is PhysicallyBasedSky)
                            issues.Add(scene.path + "/" + volume.name + ": Mobile uses a procedural sky approximation for background and cached environment IBL; full atmosphere and clouds are not evaluated.");
                        else if (component is IndirectLightingController indirect)
                        {
                            if (indirect.GetIndirectDiffuseLightingLayers() != 255 || indirect.GetReflectionLightingLayers() != 255)
                                issues.Add(scene.path + "/" + volume.name + ": Mobile applies indirect-lighting multipliers globally; layer masks are unsupported.");
                        }
                        else if (component is VisualEnvironment environment)
                        {
                            if (environment.cloudType.value != 0) issues.Add(scene.path + "/" + volume.name + ": Mobile sky fallback does not render clouds.");
                        }
                        else if (!(component is ColorAdjustments) && !(component is Bloom) && !(component is MobilePostProcessing) && !(component is GradientSky))
                            issues.Add(scene.path + "/" + volume.name + ": volume override " + component.GetType().Name + " is ignored by Mobile.");
                    }
                }
            }
            if (Lightmapping.realtimeGI) issues.Add(scene.path + ": realtime GI is unsupported.");
        }

        internal static bool ValidateBuild(BuildTarget target)
        {
            var assets = ShaderBuildPreprocessor.hdrpAssets;
            if (!assets.Any(a => a.rendererMode == HDRendererMode.Mobile)) return false;
            var includedPipelines = new List<RenderPipelineAsset>();
            target.TryGetRenderPipelineAssets<RenderPipelineAsset>(includedPipelines);
            if (target == BuildTarget.Android && includedPipelines.Any(a => !(a is HDRenderPipelineAsset hd) || hd.rendererMode != HDRendererMode.Mobile))
                throw new BuildFailedException("Android Mobile builds require Mobile HDRP assets in every included quality level.");
            if (target == BuildTarget.Android && assets.Any(a => a.rendererMode != HDRendererMode.Mobile))
                throw new BuildFailedException("Android build includes a Desktop HDRP asset. Remove it from included quality levels, scene dependencies and HDRP build labels.");
            foreach (var asset in assets.Where(a => a.rendererMode == HDRendererMode.Mobile))
            {
                if (!asset.mobileResources.isValid) throw new BuildFailedException(asset.name + ": Mobile shader resources are missing.");
                if (QualitySettings.activeColorSpace != ColorSpace.Linear) throw new BuildFailedException("Mobile requires Linear color space.");
                if (target != BuildTarget.Android) throw new BuildFailedException("Mobile player builds support Android only; desktop preview is Editor-only.");
                var apis = PlayerSettings.GetGraphicsAPIs(target);
                if (PlayerSettings.GetUseDefaultGraphicsAPIs(target) || apis.Length != 1 || apis[0] != GraphicsDeviceType.Vulkan)
                    throw new BuildFailedException("Mobile requires explicit Vulkan-only Android Graphics APIs (disable Auto Graphics API).");
                if (!HasHDRLightmaps())
                    throw new BuildFailedException("Mobile requires Android Lightmap Encoding = High Quality (Player settings) to preserve HDRP physical light intensities.");
            }
            return assets.All(a => a.rendererMode == HDRendererMode.Mobile);
        }

        // Unity 2022.3 exposes this setting in Player UI but keeps its scripting API internal,
        // as also handled by HDRP's configuration wizard.
        internal static bool HasHDRLightmaps()
        {
            var method = typeof(PlayerSettings).GetMethod("GetLightmapEncodingQualityForPlatformGroup",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            return method != null && method.Invoke(null, new object[] { BuildTargetGroup.Android }).ToString() == "High";
        }
    }

    // Runs on the build's scene copies, never edits or opens the user's authoring scenes.
    internal sealed class MobileSceneAudit : IProcessSceneWithReport
    {
        public int callbackOrder => 0;
        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null || !ShaderBuildPreprocessor.hdrpAssets.Any(a => a.rendererMode == HDRendererMode.Mobile)) return;
            var issues = new List<string>();
            MobileRenderEditor.AuditScene(scene, issues);
            if (issues.Count != 0) Debug.LogWarning("HDRP Mobile build content audit:\n" + string.Join("\n", issues));
        }
    }
}
