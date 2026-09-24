using System;
using System.IO;
using System.Reflection;
using System.Linq;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition
{
    // Batch entry point intended for a disposable validation project, never the user's open project.
    public static class MobileRenderValidation
    {
        static int s_Frame;
        static Camera s_Camera;
        static HDRenderPipelineAsset s_Asset;
        static int s_RenderCallbacks;

        public static void Run()
        {
            MobileProductionValidation.RequireIsolatedProject();
            PlayerSettings.colorSpace = ColorSpace.Linear;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            s_Asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            s_Asset.rendererMode = HDRendererMode.Mobile;
            MobileRenderEditor.AssignResources(s_Asset);
            GraphicsSettings.renderPipelineAsset = s_Asset;
            QualitySettings.renderPipeline = s_Asset;
            var cameraObject = new GameObject("Validation Camera", typeof(Camera));
            s_Camera = cameraObject.GetComponent<Camera>();
            s_Camera.transform.position = new Vector3(0, 2, -5);
            s_Camera.transform.LookAt(Vector3.zero);
            s_Camera.clearFlags = CameraClearFlags.SolidColor;
            s_Camera.backgroundColor = Color.green;
            s_Camera.targetTexture = new RenderTexture(320, 180, 0);
            s_Camera.targetTexture.Create();
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var material = new Material(Shader.Find("HDRP/Unlit"));
            material.SetColor("_UnlitColor", Color.red);
            HDShaderUtils.ResetMaterialKeywords(material);
            cube.GetComponent<Renderer>().sharedMaterial = material;
            ShaderUtil.allowAsyncCompilation = false;
            RenderPipelineManager.beginCameraRendering += CountCamera;
            EditorApplication.update += Tick;
        }

        static void CountCamera(ScriptableRenderContext context, Camera camera) { s_RenderCallbacks++; }

        static void Tick()
        {
            if (++s_Frame < 20) { EditorApplication.QueuePlayerLoopUpdate(); return; }
            EditorApplication.update -= Tick;
            try
            {
                typeof(RenderPipelineManager).GetMethod("PrepareRenderPipeline", BindingFlags.Static | BindingFlags.NonPublic)
                    ?.Invoke(null, new object[] { s_Asset });
                foreach (string name in new[] { "HDRP/Lit", "HDRP/Unlit", "Hidden/HDRP/Mobile/Composite", "Hidden/HDRP/Mobile/Sky", "Hidden/HDRP/Mobile/Error" })
                {
                    var shader = Shader.Find(name);
                    if (shader == null) throw new Exception("Missing shader " + name);
                    var material = new Material(shader);
                    Debug.Log("Validate shader=" + name + "; supported=" + shader.isSupported + "; passes=" + material.passCount);
                    if (!shader.isSupported || material.passCount == 0) throw new Exception("Shader has no supported passes: " + name);
                    try
                    {
                        foreach (bool mobile in new[] { true, false })
                        {
                            if (mobile) Shader.EnableKeyword("HDRP_MOBILE_RENDERER"); else Shader.DisableKeyword("HDRP_MOBILE_RENDERER");
                            for (int i = 0; i < material.passCount; i++)
                            {
                                string passName = material.GetPassName(i);
                                if (passName.StartsWith("Mobile", StringComparison.Ordinal) || passName == "ShadowCaster"
                                    || passName == "SceneSelectionPass" || passName == "ScenePickingPass" || passName == "Forward" || passName == "ForwardOnly")
                                    ShaderUtil.CompilePass(material, i, true);
                            }
                        }
                        if (name == "HDRP/Lit")
                        {
                            Shader.EnableKeyword("HDRP_MOBILE_RENDERER");
                            foreach (string keyword in new[] { "_NORMALMAP", "_MASKMAP", "_ALPHATEST_ON", "_DOUBLESIDED_ON", "_EMISSIVE_COLOR_MAP", "_SURFACE_TYPE_TRANSPARENT", "LIGHTMAP_ON", "DIRLIGHTMAP_COMBINED", "INSTANCING_ON" })
                                material.EnableKeyword(keyword);
                            ShaderUtil.CompilePass(material, material.FindPass("MobileForward"), true);
                            ShaderUtil.CompilePass(material, material.FindPass("ShadowCaster"), true);
                        }
                        foreach (var message in ShaderUtil.GetShaderMessages(shader))
                        {
                            Debug.Log(name + ": " + message.severity + " " + message.file + ":" + message.line + " " + message.message);
                            if (message.severity == ShaderCompilerMessageSeverity.Error || message.message.Contains("Excluding it from compilation")
                                || message.message.Contains("skipping")) throw new Exception("Shader compilation failed or skipped: " + name);
                        }
                    }
                    finally { UnityEngine.Object.DestroyImmediate(material); }
                }
                Shader.EnableKeyword("HDRP_MOBILE_RENDERER");
                typeof(RenderPipelineManager).GetMethod("PrepareRenderPipeline", BindingFlags.Static | BindingFlags.NonPublic)
                    ?.Invoke(null, new object[] { s_Asset });
                Debug.Log("Validation pipeline=" + RenderPipelineManager.currentPipeline + "; color space=" + QualitySettings.activeColorSpace);
                s_Camera.Render();
                var texture = new Texture2D(320, 180, TextureFormat.RGBA32, false);
                var previous = RenderTexture.active;
                RenderTexture.active = s_Camera.targetTexture;
                texture.ReadPixels(new Rect(0, 0, 320, 180), 0, 0);
                texture.Apply();
                RenderTexture.active = previous;
                Color pixel = texture.GetPixel(160, 90);
                Debug.Log("Render callbacks=" + s_RenderCallbacks + "; corner=" + texture.GetPixel(1, 1));
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Application.dataPath), "mobile-smoke.png"), texture.EncodeToPNG());
                if (pixel.r < 0.3f || pixel.g > 0.1f || pixel.b > 0.1f)
                    throw new Exception("Unlit render failed, center pixel=" + pixel);
                ValidateMaterialModes();
                ValidateLightingAndShadows();
                EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), "Assets/Validation.unity");
                if (!File.Exists("Assets/MobileBenchmark/Benchmark.unity")) MobileBenchmarkBuilder.CreateAt("Assets/MobileBenchmark");
                Debug.Log("HDRP_MOBILE_VALIDATION_PASSED: shader variants, Unlit, Lit, main light and two-cascade shadow rendering; benchmark generated.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }

        static Color[] Capture(string name)
        {
            s_Camera.Render();
            var previous = RenderTexture.active;
            RenderTexture.active = s_Camera.targetTexture;
            var texture = new Texture2D(320, 180, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, 320, 180), 0, 0); texture.Apply();
            RenderTexture.active = previous;
            var pixels = texture.GetPixels();
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Application.dataPath), name + ".png"), texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            return pixels;
        }

        static void ValidateLightingAndShadows()
        {
            var cube = UnityEngine.Object.FindObjectOfType<MeshRenderer>();
            var material = new Material(Shader.Find("HDRP/Lit"));
            material.SetColor("_BaseColor", Color.white);
            HDShaderUtils.ResetMaterialKeywords(material);
            cube.sharedMaterial = material;
            cube.transform.position = new Vector3(0, 1.5f, 0);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Shadow receiver";
            floor.transform.position = new Vector3(0, -0.25f, 0);
            floor.transform.localScale = new Vector3(8, 0.5f, 8);
            floor.GetComponent<Renderer>().sharedMaterial = material;
            s_Camera.transform.position = new Vector3(4, 4, -6);
            s_Camera.transform.LookAt(Vector3.zero);
            s_Camera.backgroundColor = Color.black;
            var sun = new GameObject("Validation Sun").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 4; sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(35, 120, 0);
            RenderSettings.sun = sun;
            s_Asset.mobileSettings.mainLightShadows = false;
            var lit = Capture("mobile-lit");
            if (lit.Count(c => c.r > 0.1f && Mathf.Abs(c.r - c.g) < 0.1f) < 500)
                throw new Exception("Lit illumination did not produce a visible receiver.");
            s_Asset.mobileSettings.mainLightShadows = true;
            var shadowed = Capture("mobile-shadowed");
            var bounds = cube.bounds;
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            for (int corner = 0; corner < 8; corner++)
            {
                var offset = Vector3.Scale(bounds.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                Vector2 screen = s_Camera.WorldToScreenPoint(bounds.center + offset);
                min = Vector2.Min(min, screen); max = Vector2.Max(max, screen);
            }
            int darker = 0;
            for (int i = 0; i < lit.Length; i++)
            {
                int x = i % 320, y = i / 320;
                if (x >= min.x - 2 && x <= max.x + 2 && y >= min.y - 2 && y <= max.y + 2) continue;
                if (lit[i].r - shadowed[i].r > 0.05f) darker++;
            }
            Debug.Log("Shadow receiver changed pixels=" + darker);
            if (darker < 10) throw new Exception("Shadow pass did not darken the receiver.");
            foreach (var shader in new[] { material.shader, s_Asset.mobileResources.compositeShader })
                if (ShaderUtil.ShaderHasError(shader)) throw new Exception("Rendering exposed a shader error in " + shader.name);
        }

        static void ValidateMaterialModes()
        {
            var renderer = UnityEngine.Object.FindObjectOfType<MeshRenderer>();
            var material = renderer.sharedMaterial;
            material.SetFloat("_SurfaceType", 1);
            material.SetColor("_UnlitColor", new Color(1, 0, 0, 0.5f));
            HDShaderUtils.ResetMaterialKeywords(material);
            var transparent = Capture("mobile-transparent")[90 * 320 + 160];
            if (transparent.r < 0.1f || transparent.g < 0.1f) throw new Exception("Transparent alpha did not blend: " + transparent);
            material.SetFloat("_SurfaceType", 0);
            material.SetFloat("_AlphaCutoffEnable", 1);
            material.SetColor("_UnlitColor", new Color(1, 0, 0, 0));
            HDShaderUtils.ResetMaterialKeywords(material);
            var clipped = Capture("mobile-cutout")[90 * 320 + 160];
            if (clipped.r > 0.1f || clipped.g < 0.3f) throw new Exception("Alpha clip did not reject geometry: " + clipped);
            material.SetFloat("_AlphaCutoffEnable", 0);
            material.SetColor("_UnlitColor", Color.red);
            HDShaderUtils.ResetMaterialKeywords(material);
            var unsupported = new Material(Shader.Find("Unlit/Color"));
            renderer.sharedMaterial = unsupported;
            var error = Capture("mobile-error")[90 * 320 + 160];
            renderer.sharedMaterial = material;
            UnityEngine.Object.DestroyImmediate(unsupported);
            if (error.r < 0.3f || error.b < 0.3f || error.g > 0.1f) throw new Exception("Unsupported material did not draw magenta: " + error);
            var second = new GameObject("Secondary camera").AddComponent<Camera>();
            second.cullingMask = 0; second.clearFlags = CameraClearFlags.Color; second.backgroundColor = Color.blue;
            second.targetTexture = new RenderTexture(64, 64, 0); second.targetTexture.Create(); second.Render();
            var afterSecond = Capture("mobile-multicamera")[90 * 320 + 160];
            var secondTarget = second.targetTexture; second.targetTexture = null;
            secondTarget.Release(); UnityEngine.Object.DestroyImmediate(secondTarget);
            UnityEngine.Object.DestroyImmediate(second.gameObject);
            if (afterSecond.r < 0.3f || afterSecond.g > 0.1f || afterSecond.b > 0.1f) throw new Exception("Secondary camera leaked state: " + afterSecond);
        }

        public static void BakeBenchmark()
        {
            MobileProductionValidation.RequireIsolatedProject();
            try
            {
                const string folder = "Assets/MobileBenchmark";
                var scene = EditorSceneManager.OpenScene(folder + "/Benchmark.unity", OpenSceneMode.Single);
                if (!HDUtils.IsSupportedBuildTarget(EditorUserBuildSettings.activeBuildTarget))
                    throw new InvalidOperationException("Bake the fixture with a supported Desktop build target selected, such as Windows.");
                var desktop = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                GraphicsSettings.renderPipelineAsset = desktop; QualitySettings.renderPipeline = desktop;
                ShaderUtil.allowAsyncCompilation = false;
                typeof(RenderPipelineManager).GetMethod("PrepareRenderPipeline", BindingFlags.Static | BindingFlags.NonPublic)
                    ?.Invoke(null, new object[] { desktop });
                Lightmapping.lightingSettings.lightmapper = LightingSettings.Lightmapper.ProgressiveCPU;
                Lightmapping.lightingSettings.lightmapResolution = 32;
                EditorUtility.SetDirty(Lightmapping.lightingSettings);
                bool baked = Lightmapping.Bake();
                baked &= HDBakedReflectionSystem.BakeProbes(UnityEngine.Object.FindObjectsOfType<HDAdditionalReflectionData>());
                foreach (var probe in UnityEngine.Object.FindObjectsOfType<HDAdditionalReflectionData>())
                    probe.GetComponent<ReflectionProbe>().customBakedTexture = null;
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
                Debug.Log("HDRP_MOBILE_BAKE=" + baked + "; lightmaps=" + LightmapSettings.lightmaps.Length);
                EditorApplication.Exit(baked && LightmapSettings.lightmaps.Length > 0 ? 0 : 1);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }

        public static void BuildAndroid() => BuildAndroid(false);

        public static void ExportAndroid() => BuildAndroid(true);

        static void BuildAndroid(bool export)
        {
            MobileProductionValidation.RequireIsolatedProject();
            try
            {
                const string folder = "Assets/MobileBenchmark";
                if (!File.Exists(folder + "/Benchmark.unity"))
                {
                    var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    if (string.IsNullOrEmpty(currentScene.path)) EditorSceneManager.SaveScene(currentScene, "Assets/BuildBootstrap.unity");
                    MobileBenchmarkBuilder.CreateAt(folder);
                }
                var asset = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(folder + "/Mobile.asset");
                asset.mobileSettings.drawUnsupportedMaterials = false;
                EditorUtility.SetDirty(asset);
                var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(folder + "/Volume.asset");
                profile.components.RemoveAll(component => component == null);
                if (!profile.TryGet<Exposure>(out var exposure)) exposure = profile.Add<Exposure>(true);
                exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(14);
                if (!profile.TryGet<Tonemapping>(out var tone)) tone = profile.Add<Tonemapping>(true);
                tone.mode.Override(TonemappingMode.Neutral);
                foreach (var component in profile.components)
                    if (!AssetDatabase.Contains(component)) AssetDatabase.AddObjectToAsset(component, profile);
                EditorUtility.SetDirty(profile);
                GraphicsSettings.renderPipelineAsset = asset;
                for (int i = 0; i < QualitySettings.names.Length; i++)
                {
                    QualitySettings.SetQualityLevel(i, false);
                    QualitySettings.renderPipeline = asset;
                }
                PlayerSettings.colorSpace = ColorSpace.Linear;
                var encodingType = typeof(PlayerSettings).Assembly.GetType("UnityEditor.LightmapEncodingQuality");
                typeof(PlayerSettings).GetMethod("SetLightmapEncodingQualityForPlatformGroup", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new[] { (object)BuildTargetGroup.Android, Enum.Parse(encodingType, "High") });
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
                PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.hdrpdev.mobilebenchmark");
                PlayerSettings.productName = "HDRP Mobile Benchmark";
                PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
                PlayerSettings.MTRendering = true;
                PlayerSettings.enableFrameTimingStats = true;
                EditorSceneManager.OpenScene(folder + "/Benchmark.unity", OpenSceneMode.Single);
                AssetDatabase.SaveAssets();
                // ExecuteMethod can run during the first update after a target switch.
                // Let imported textures and lighting bindings settle, just as Run does.
                int frames = 0;
                EditorApplication.CallbackFunction finish = null;
                finish = () =>
                {
                    if (EditorApplication.isCompiling || EditorApplication.isUpdating || ++frames < 20)
                    { EditorApplication.QueuePlayerLoopUpdate(); return; }
                    EditorApplication.update -= finish;
                    FinishAndroidBuild(export);
                };
                EditorApplication.update += finish;
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }

        static void FinishAndroidBuild(bool export)
        {
            try
            {
                const string folder = "Assets/MobileBenchmark";
                var asset = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(folder + "/Mobile.asset");
                ShaderUtil.allowAsyncCompilation = false;
                typeof(RenderPipelineManager).GetMethod("PrepareRenderPipeline", BindingFlags.Static | BindingFlags.NonPublic)
                    ?.Invoke(null, new object[] { asset });
                s_Camera = UnityEngine.Object.FindObjectOfType<Camera>();
                s_Camera.targetTexture = new RenderTexture(320, 180, 0);
                s_Camera.targetTexture.Create();
                var pixels = Capture("mobile-baked-benchmark");
                if (pixels.Count(c => c.g > 0.1f && Mathf.Abs(c.r - c.g) < 0.15f && Mathf.Abs(c.g - c.b) < 0.15f) < 2000)
                    throw new Exception("Baked fixture has too few neutral lit pixels; inspect mobile-baked-benchmark.png before accepting the build.");
                var captureTarget = s_Camera.targetTexture; s_Camera.targetTexture = null;
                captureTarget.Release(); UnityEngine.Object.DestroyImmediate(captureTarget);
                Debug.Log("Benchmark lightmaps=" + LightmapSettings.lightmaps.Length);
                AssetDatabase.SaveAssets();
                EditorUserBuildSettings.exportAsGoogleAndroidProject = export;
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { folder + "/Benchmark.unity" }, target = BuildTarget.Android,
                    locationPathName = Path.Combine(Path.GetDirectoryName(Application.dataPath), export ? "mobile-android-project" : "mobile-benchmark.apk"),
                    options = BuildOptions.Development
                });
                Debug.Log("HDRP_MOBILE_ANDROID_BUILD=" + report.summary.result);
                EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
    }
}
