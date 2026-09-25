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
    using CED = CoreEditorDrawer<SerializedObject>;
    using Styles = HDRenderPipelineUI.Styles;

    internal static class MobileRenderEditor
    {

        enum Expandable
        {
            Rendering = 1 << 0, Lighting = 1 << 1, PostProcessing = 1 << 2,
            Resolution = 1 << 3, Sky = 1 << 4, Environment = 1 << 5,
            Fog = 1 << 6, Shadows = 1 << 7, DirectionalShadows = 1 << 8,
            Lights = 1 << 9, Exposure = 1 << 10, Resources = 1 << 11, Diagnostics = 1 << 12
        }

        // Keep Mobile's foldout preferences separate from Desktop's.
        static readonly ExpandedState<Expandable, HDRenderPipelineAsset> k_ExpandedState =
            new ExpandedState<Expandable, HDRenderPipelineAsset>(Expandable.Rendering | Expandable.Resolution, "HDRP:Mobile");
        const FoldoutOption SubFoldout = FoldoutOption.Indent | FoldoutOption.SubFoldout;
        static readonly CED.IDrawer k_Inspector = CED.Group(
            CED.FoldoutGroup(Styles.renderingSectionTitle, Expandable.Rendering, k_ExpandedState,
                CED.FoldoutGroup("Resolution", Expandable.Resolution, k_ExpandedState, SubFoldout, DrawResolution)),
            CED.FoldoutGroup(Styles.lightingSectionTitle, Expandable.Lighting, k_ExpandedState,
                CED.FoldoutGroup(Styles.skySubTitle, Expandable.Sky, k_ExpandedState, SubFoldout, DrawSky),
                CED.FoldoutGroup("Environment Lighting", Expandable.Environment, k_ExpandedState, SubFoldout, DrawEnvironment),
                CED.FoldoutGroup("Fog", Expandable.Fog, k_ExpandedState, SubFoldout, DrawFog),
                CED.FoldoutGroup(Styles.shadowSubTitle, Expandable.Shadows, k_ExpandedState, SubFoldout,
                    CED.FoldoutGroup(Styles.directionalLightshadowSubTitle, Expandable.DirectionalShadows, k_ExpandedState, SubFoldout, DrawShadows)),
                CED.FoldoutGroup(Styles.lightLoopSubTitle, Expandable.Lights, k_ExpandedState, SubFoldout, DrawLights)),
            CED.FoldoutGroup(Styles.postProcessSectionTitle, Expandable.PostProcessing, k_ExpandedState,
                CED.FoldoutGroup("Exposure", Expandable.Exposure, k_ExpandedState, SubFoldout, DrawExposure)),
            CED.FoldoutGroup("Diagnostics", Expandable.Diagnostics, k_ExpandedState, DrawDiagnostics));

        static SerializedProperty Setting(SerializedObject serialized, string name)
            => serialized.FindProperty("m_MobileSettings").FindPropertyRelative(name);

        static void Field(SerializedObject serialized, string name, string label, string tooltip)
            => EditorGUILayout.PropertyField(Setting(serialized, name), EditorGUIUtility.TrTextContent(label, tooltip));

        static bool Disabled(SerializedObject serialized, string name)
        {
            var property = Setting(serialized, name);
            // Mixed selections remain editable even when the first asset disables the feature.
            return !property.hasMultipleDifferentValues && !property.boolValue;
        }

        internal static bool DrawInspector(SerializedObject serialized)
        {
            var mode = serialized.FindProperty("m_RendererMode");
            EditorGUILayout.PropertyField(mode);
            if (mode.hasMultipleDifferentValues)
            {
                EditorGUILayout.HelpBox("Select assets with the same Renderer Mode to edit renderer settings together.", MessageType.Info);
                return true;
            }
            if (mode.enumValueIndex != (int)HDRendererMode.Mobile) return false;
            EditorGUILayout.HelpBox("Android Vulkan / Windows D3D11/D3D12 renderer. Deferred uses three color targets plus at most one RGBA8 Fresnel/Coat target; other Lit types use Forward. Exposure Control follows HDRP Frame Settings.", MessageType.Info);
            if (serialized.targetObjects.Cast<HDRenderPipelineAsset>().Any(asset => !asset.mobileResources.isValid || asset.mobileResources.deferredShader == null))
                EditorGUILayout.HelpBox("HDRP built-in Mobile shaders are unavailable. Check the HDRP package and Global Settings runtime resources.", MessageType.Error);
            k_Inspector.Draw(serialized, null);
            return true;
        }

        static void DrawResolution(SerializedObject serialized, Editor owner)
        {
            Field(serialized, "renderingPath", "Rendering Path", "Forward or compact deferred opaque lighting. Transparent surfaces remain Forward.");
            Field(serialized, "extendedLitMaterials", "Deferred Fresnel / Coat RT", "Allow one extra RGBA8 target for SpecularColor and clear coat. SSS, Anisotropy, Iridescence and Translucent use Forward, without screen-space SSS. Disabling this also routes SpecularColor and coated Standard to Forward.");
            if (Setting(serialized, "renderingPath").enumValueIndex == (int)MobileRenderingPath.Deferred)
                Field(serialized, "deferredExecution", "Deferred Execution", "Auto uses stored GBuffer textures on desktop and native subpasses on Android Vulkan. D3D11/D3D12 require Auto or Sampled. Forced Native is reserved for Vulkan validation.");
            Field(serialized, "fixed1080p", "Fixed 1080p Resolution", "Use a 1920 x 1080 base resolution for Game cameras targeting the display. Preview cameras and RenderTextures retain their dimensions. Render Scale applies in both modes.");
            Field(serialized, "renderScale", "Render Scale", "Scale the base width and height before upscaling. This is a fixed scale, not Desktop dynamic resolution.");
        }

        static void DrawLights(SerializedObject serialized, Editor owner)
        {
            Field(serialized, "additionalLightsPerObject", "Maximum Additional Lights per Object", "Maximum additional forward lights evaluated per object (0-4), in addition to the main directional light.");
        }

        static void DrawShadows(SerializedObject serialized, Editor owner)
        {
            EditorGUILayout.HelpBox("Lighting / Mobile Shadows Volumes override these defaults per camera.", MessageType.Info);
            Field(serialized, "mainLightShadows", "Main Light Shadows", "Enable realtime CSM for the main directional light, with up to six cascades.");
            using (new EditorGUI.DisabledScope(Disabled(serialized, "mainLightShadows")))
            {
                Field(serialized, "shadowAtlasSize", "CSM Atlas Resolution", "Total atlas size: 256-4096, rounded up to a power of two. Five or six cascades occupy a 3x3 tile grid.");
                Field(serialized, "cascadeCount", "Cascade Count", "One to six cascades.");
                Field(serialized, "cascadeSplit", "First Cascade Split", "First split / shadow distance. Remaining splits divide the remaining distance evenly.");
                Field(serialized, "cascadeBlend", "Cascade Blend", "Fraction of each cascade radius blended into the next cascade. Zero disables blending.");
                Field(serialized, "shadowNormalBias", "Normal Bias (Texels)", "Inset shadow casters along the negative surface normal to reduce self-shadowing. Measured in shadow-map texels and converted per cascade; reduced on surfaces facing the light.");
            }
            Field(serialized, "localLightShadows", "Local Light Shadows", "Render Point (six faces) and Spot (one face) shadows into a shared local atlas. Each light controls its shadow strength and bias.");
            Field(serialized, "localShadowAtlasSize", "Local Shadow Atlas Resolution", "Total local atlas size: 256-4096, rounded up to a power of two. Tiles resize to fit the visible shadowed lights.");
            Field(serialized, "shadowDistance", "Maximum Shadow Distance", "Maximum shadow distance for directional and local lights.");
            Field(serialized, "shadowFilterSize", "PCF Filter Size", "Kernel width from 1x1 to 4x4 for directional and local shadows.");
        }

        static void DrawSky(SerializedObject serialized, Editor owner)
        {
            Field(serialized, "sky", "Sky Cubemap Override", "Override the sky for both the background and environment lighting. Leave empty to use the sky fallback.");
            var sky = Setting(serialized, "sky");
            using (new EditorGUI.DisabledScope(!sky.hasMultipleDifferentValues && sky.objectReferenceValue == null))
                Field(serialized, "skyIntensity", "Sky Intensity Multiplier", "Intensity of the explicit Sky Cubemap Override only. Volume and legacy skies use their own intensity controls.");
            using (new EditorGUI.DisabledScope(!sky.hasMultipleDifferentValues && sky.objectReferenceValue != null))
                Field(serialized, "skyFallback", "Use Volume / Skybox Fallback", "Without a cubemap override, use Volume HDRI/Gradient sky, a lightweight physical sky approximation, or a supported legacy skybox.");
        }

        static void DrawEnvironment(SerializedObject serialized, Editor owner)
        {
            Field(serialized, "environmentLighting", "Sky Lighting", "Generate diffuse irradiance and GGX reflections from the resolved sky. Lightmaps retain priority.");
            using (new EditorGUI.DisabledScope(Disabled(serialized, "environmentLighting")))
            {
                Field(serialized, "environmentResolution", "Reflection Resolution", "Face resolution of the sky reflection cache: Low = 32 pixels, High = 64 pixels.");
                Field(serialized, "environmentDiffuseSource", "Diffuse Lighting Source", "Prefer Baked Probes uses native SH when the scene has baked probes, otherwise sky irradiance. Runtime Sky uses sky irradiance on all non-lightmapped objects.");
                Field(serialized, "environmentJobsPerFrame", "Environment Update Budget", "Faces/mips filtered per update after initial warm-up. The previous complete cache remains visible until the new generation is ready.");
            }
        }

        static void DrawFog(SerializedObject serialized, Editor owner)
        {
            Field(serialized, "fog", "Fog", "Enable Mobile linear distance fog. This is not Desktop volumetric fog and does not use the Fog Volume override.");
            using (new EditorGUI.DisabledScope(Disabled(serialized, "fog")))
            {
                Field(serialized, "fogColor", "Fog Color", "Color of the Mobile distance fog.");
                Field(serialized, "fogStart", "Start Distance", "Distance in world units where fog starts.");
                Field(serialized, "fogEnd", "End Distance", "Distance in world units where fog reaches full opacity. Set higher than Start Distance.");
            }
        }

        static void DrawExposure(SerializedObject serialized, Editor owner)
        {
            EditorGUILayout.HelpBox("Enable Exposure Control in HDRP Frame Settings. Configure exposure mode and adaptation in the Exposure Volume override. Mobile uses geometric-mean metering for automatic exposure.", MessageType.Info);
            Field(serialized, "meteringResolution", "Metering Resolution", "Automatic exposure meter resolution: Low = 64, Medium = 128, High = 256 pixels per side.");
        }

        static void DrawDiagnostics(SerializedObject serialized, Editor owner)
        {
            Field(serialized, "drawUnsupportedMaterials", "Highlight Unsupported Materials", "Draw magenta for shaders without MobileForward. Scans loaded renderers every frame; disable after validation when profiling production performance.");
            if (GUILayout.Button("Open Mobile Settings and Diagnostics")) MobileSettingsWindow.Open();
            if (GUILayout.Button("Audit Loaded Scenes")) AuditLoadedScenes();
        }

        [MenuItem("Assets/Create/Rendering/HDRP Mobile Pipeline Asset", priority = 303)]
        static void CreateAsset()
        {
            var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            asset.rendererMode = HDRendererMode.Mobile;
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

        internal static void AuditMaterial(Material material, string location, List<string> issues, bool? extendedLit = null)
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
                Unsupported("_UVBase", 0, "only UV0 base mapping is supported");
                Unsupported("_NormalMapSpace", 0, "object-space normal maps are unsupported");
                Unsupported("_DisplacementMode", 0, "displacement is unsupported");
                Unsupported("_RefractionModel", 0, "refraction is unsupported");
                if (material.IsKeywordEnabled("_DETAIL_MAP") && material.GetFloat("_UVDetail") > 1) issues.Add(location + ": " + material.name + " supports Detail UV0/UV1 only.");
            }
            Unsupported("_UVEmissive", 0, "only UV0 emission mapping is supported");
            Unsupported("_DistortionEnable", 0, "distortion is unsupported");
        }

        internal static void DrawMaterialSupport(MaterialEditor editor)
        {
            if (!(GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset asset) || asset.rendererMode != HDRendererMode.Mobile) return;
            var properties = MaterialEditor.GetMaterialProperties(editor.targets);
            EditorGUI.BeginChangeCheck();
            foreach (var property in properties)
                if (property.name == "_MobileReceiveShadows" || property.name == "_MobileSpecularHighlights" || property.name == "_MobileUserStencilBit")
                    editor.ShaderProperty(property, property.displayName);
            if (EditorGUI.EndChangeCheck())
                foreach (Material material in editor.targets) MobileDeferredMaterial.Validate(material);
            var issues = new List<string>();
            foreach (var target in editor.targets) AuditMaterial(target as Material, "Mobile", issues);
            EditorGUILayout.HelpBox(issues.Count == 0 ? "Mobile: Standard Lit/Unlit, base/emission UV0, Detail UV0/UV1. Premultiply expects premultiplied RGB. Desktop material settings are preserved." : string.Join("\n", issues), issues.Count == 0 ? MessageType.Info : MessageType.Warning);
        }

        internal static void AuditScene(Scene scene, List<string> issues, bool? extendedLit = null)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    string location = scene.path + "/" + AnimationUtility.CalculateTransformPath(renderer.transform, null);
                    foreach (var material in renderer.sharedMaterials) AuditMaterial(material, location, issues, extendedLit);
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
                        else if (!(component is ColorAdjustments) && !(component is Bloom) && !(component is MobilePostProcessing) && !(component is GradientSky) && !(component is DiffusionProfileList))
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
                if (asset.mobileSettings.renderingPath == MobileRenderingPath.Deferred && asset.mobileResources.deferredShader == null)
                    throw new BuildFailedException(asset.name + ": HDRP built-in Mobile Deferred shader is missing.");
                if (QualitySettings.activeColorSpace != ColorSpace.Linear) throw new BuildFailedException("Mobile requires Linear color space.");
                bool windows = target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64;
                if (target != BuildTarget.Android && !windows) throw new BuildFailedException("Mobile backend players support Android Vulkan and Windows D3D11/D3D12.");
                var apis = PlayerSettings.GetGraphicsAPIs(target);
                if (windows)
                {
                    if (PlayerSettings.GetUseDefaultGraphicsAPIs(target) || apis.Length == 0
                        || apis.Any(api => api != GraphicsDeviceType.Direct3D11 && api != GraphicsDeviceType.Direct3D12))
                        throw new BuildFailedException("Mobile Windows builds require explicit D3D11/D3D12 Graphics APIs (disable Auto Graphics API).");
                    if (asset.mobileSettings.renderingPath == MobileRenderingPath.Deferred
                        && asset.mobileSettings.deferredExecution == MobileDeferredExecution.NativeRenderPass)
                        throw new BuildFailedException("Mobile Windows Deferred uses stored textures. Select Auto or Sampled, not NativeRenderPass.");
                }
                else if (PlayerSettings.GetUseDefaultGraphicsAPIs(target) || apis.Length != 1 || apis[0] != GraphicsDeviceType.Vulkan)
                    throw new BuildFailedException("Mobile requires explicit Vulkan-only Android Graphics APIs (disable Auto Graphics API).");
                if (target == BuildTarget.Android && !HasHDRLightmaps())
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
            MobileRenderEditor.AuditScene(scene, issues, ShaderBuildPreprocessor.hdrpAssets
                .Where(a => a.rendererMode == HDRendererMode.Mobile).All(a => a.mobileSettings.extendedLitMaterials));
            if (issues.Count != 0) Debug.LogWarning("HDRP Mobile build content audit:\n" + string.Join("\n", issues));
        }
    }
}
