using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    // Actual camera/Forward-pass tests, complementing the isolated URP BRDF oracle.
    public partial class MobileImageTests
    {
        const int Width = 128, Height = 96;
        readonly List<Object> m_Owned = new List<Object>();
        readonly MobileRenderTests m_PipelineScope = new MobileRenderTests();
        UnityEngine.SceneManagement.Scene m_Scene;
        HDRenderPipelineAsset m_Asset;
        Camera m_Camera;
        Light m_Sun, m_PreviousSun;
        AmbientMode m_AmbientMode;
        Color m_Ambient;
        DefaultReflectionMode m_PreviousReflectionMode;
        Texture m_PreviousReflection;
        bool m_Async;
        T Own<T>(T obj) where T : Object { m_Owned.Add(obj); return obj; }

        [SetUp]
        public void Setup()
        {
            Assert.AreNotEqual(GraphicsDeviceType.Null, SystemInfo.graphicsDeviceType, "Image validation requires a GPU; do not use -nographics.");
            m_PipelineScope.SuspendProjectPipeline();
            m_PreviousSun = RenderSettings.sun; m_Ambient = RenderSettings.ambientLight; m_AmbientMode = RenderSettings.ambientMode;
            m_PreviousReflectionMode = RenderSettings.defaultReflectionMode; m_PreviousReflection = RenderSettings.customReflectionTexture;
            m_Scene = EditorSceneManager.NewPreviewScene();
            m_Asset = Own(ScriptableObject.CreateInstance<HDRenderPipelineAsset>());
            m_Asset.rendererMode = HDRendererMode.Mobile;
            // Existing material oracles isolate direct/baked lighting. IBL tests opt in.
            m_Asset.mobileSettings.environmentLighting = false;
            m_Asset.mobileSettings.drawUnsupportedMaterials = false;
            GraphicsSettings.renderPipelineAsset = m_Asset; QualitySettings.renderPipeline = m_Asset;
            typeof(RenderPipelineManager).GetMethod("PrepareRenderPipeline", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { m_Asset });
            m_Async = ShaderUtil.allowAsyncCompilation; ShaderUtil.allowAsyncCompilation = false;
            RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = Color.black;
            // Isolate direct/emission oracles from asynchronous default-sky probe
            // baking. Environment/probe tests explicitly provide their own sources.
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = CoreUtils.blackCubeTexture;
            m_Camera = MakeCamera(Width, Height);
            CameraProfile(-1000);
            m_Camera.GetComponent<HDAdditionalCameraData>().antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
            var sun = MakeObject("Image test sun"); m_Sun = sun.AddComponent<Light>();
            m_Sun.type = LightType.Directional; m_Sun.intensity = 0.5f; m_Sun.shadows = LightShadows.None;
            m_Sun.cullingMask = 1 << 30; RenderSettings.sun = m_Sun;
        }

        GameObject MakeObject(string name)
        {
            var obj = Own(new GameObject(name)); obj.layer = 30;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(obj, m_Scene); return obj;
        }
        Camera MakeCamera(int width, int height)
        {
            var camera = MakeObject("Image camera").AddComponent<Camera>();
            camera.scene = m_Scene; camera.enabled = false; camera.cullingMask = 1 << 30;
            camera.transform.position = new Vector3(0, 0, -3);
            camera.orthographic = true; camera.orthographicSize = 1; camera.farClipPlane = 40;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            var hd = camera.gameObject.AddComponent<HDAdditionalCameraData>();
            hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color; hd.backgroundColorHDR = Color.black;
            hd.volumeLayerMask = 0;
            camera.targetTexture = Own(new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear));
            camera.targetTexture.Create(); return camera;
        }
        Material Material(bool lit, Color color)
        {
            var material = Own(new Material(Shader.Find(lit ? "HDRP/Lit" : "HDRP/Unlit")));
            material.SetColor(lit ? "_BaseColor" : "_UnlitColor", color.gamma);
            if (lit) { material.SetFloat("_Metallic", 0); material.SetFloat("_Smoothness", 0.3f); }
            material.SetColor("_EmissiveColor", Color.black);
            HDShaderUtils.ResetMaterialKeywords(material); return material;
        }
        Renderer Quad(Material material, float z = 0)
        {
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Quad); Own(primitive);
            primitive.layer = 30; UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(primitive, m_Scene);
            primitive.transform.position = new Vector3(0, 0, z);
            primitive.GetComponent<Renderer>().sharedMaterial = material;
            return primitive.GetComponent<Renderer>();
        }
        Texture2D Solid(Color color)
        {
            var texture = Own(new Texture2D(2, 2, TextureFormat.RGBAFloat, false, true));
            texture.SetPixels(new[] { color, color, color, color }); texture.Apply(); return texture;
        }
        Color[] Capture(Camera camera = null)
        {
            camera = camera != null ? camera : m_Camera;
            // EditMode can register a newly selected variant while SRP Batcher
            // is already preparing its material buffer. Compile before entering
            // Camera.Render so image oracles test the prepared shader. This does
            // not warm a rendered frame or relax any pixel comparisons.
            if (m_Asset.rendererMode == HDRendererMode.Mobile)
            {
                bool extendedKeyword = Shader.IsKeywordEnabled("MOBILE_EXTENDED_LIT");
                try
                {
                    if (m_Asset.mobileSettings.extendedLitMaterials) Shader.EnableKeyword("MOBILE_EXTENDED_LIT");
                    else Shader.DisableKeyword("MOBILE_EXTENDED_LIT");
                    foreach (var owned in m_Owned)
                    {
                        if (!(owned is Material material)) continue;
                        foreach (string passName in s_MobileImagePasses)
                        {
                            int pass = material.FindPass(passName);
                            if (pass >= 0) ShaderUtil.CompilePass(material, pass, true);
                        }
                    }
                }
                finally
                {
                    if (extendedKeyword) Shader.EnableKeyword("MOBILE_EXTENDED_LIT");
                    else Shader.DisableKeyword("MOBILE_EXTENDED_LIT");
                }
            }
            camera.Render();
            var target = camera.targetTexture;
            var previous = RenderTexture.active;
            var read = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = target; read.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); read.Apply();
                var pixels = read.GetPixels();
                foreach (var pixel in pixels) for (int c = 0; c < 4; c++)
                    Assert.IsFalse(float.IsNaN(pixel[c]) || float.IsInfinity(pixel[c]), "Non-finite rendered output");
                return pixels;
            }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(read); }
        }
        static readonly string[] s_MobileImagePasses =
            { "MobileForward", "MobileForwardOnly", "MobileForwardOnlyExtended", "MobileGBuffer", "MobileGBufferExtended", "ShadowCaster" };
        static void Near(Color expected, Color actual, string message = "")
        {
            for (int c = 0; c < 3; c++) Assert.That(actual[c], Is.EqualTo(expected[c]).Within(0.002f * Mathf.Max(1, Mathf.Abs(expected[c]))), message + " channel " + c);
        }
        static void Same(Color[] expected, Color[] actual)
        { Assert.AreEqual(expected.Length, actual.Length); for (int i = 0; i < expected.Length; i++) Near(expected[i], actual[i], "Pixel " + i); }
        static Color Center(Color[] pixels) => pixels[Width * (Height / 2) + Width / 2];

        static void SaveImage(string name, Color[] pixels)
        {
            string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "mobile-stability-images"); Directory.CreateDirectory(folder);
            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true);
            try
            {
                var display = new Color[pixels.Length]; for (int i = 0; i < display.Length; i++) display[i] = pixels[i].gamma;
                texture.SetPixels(display); texture.Apply(); File.WriteAllBytes(Path.Combine(folder, name + ".png"), texture.EncodeToPNG());
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [TearDown]
        public void Cleanup()
        {
            GraphicsSettings.renderPipelineAsset = null; QualitySettings.renderPipeline = null;
            typeof(RenderPipelineManager).GetMethod("PrepareRenderPipeline", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { null });
            ShaderUtil.allowAsyncCompilation = m_Async;
            for (int i = m_Owned.Count - 1; i >= 0; i--) if (m_Owned[i] != null) Object.DestroyImmediate(m_Owned[i]);
            m_Owned.Clear();
            if (m_Scene.IsValid() && m_Scene.isLoaded) EditorSceneManager.ClosePreviewScene(m_Scene);
            RenderSettings.sun = m_PreviousSun; RenderSettings.ambientMode = m_AmbientMode; RenderSettings.ambientLight = m_Ambient;
            RenderSettings.defaultReflectionMode = m_PreviousReflectionMode; RenderSettings.customReflectionTexture = m_PreviousReflection;
            m_PipelineScope.RestoreProjectPipeline();
        }

        [TestCase(false, 0, false)] [TestCase(false, 1, false)] [TestCase(false, 4, false)]
        [TestCase(true, 0, false)] [TestCase(true, 0, true)]
        [TestCase(true, 1, false)] [TestCase(true, 1, true)] [TestCase(true, 4, true)]
        public void ActualForwardTransparentLayersMatchBlendEquation(bool lit, int blend, bool preserve)
        {
            m_Sun.enabled = false;
            var background = new Color(0.07f, 0.13f, 0.19f, 1);
            m_Camera.GetComponent<HDAdditionalCameraData>().backgroundColorHDR = background;
            var source = new Color(0.21f, 0.12f, 0.04f, 1);
            var material = Material(lit, lit ? Color.black : source);
            material.SetFloat("_SurfaceType", 1); material.SetFloat("_BlendMode", blend);
            if (lit) { material.SetColor("_EmissiveColor", source); material.SetFloat("_EmissiveExposureWeight", 0); material.SetFloat("_EnableBlendModePreserveSpecularLighting", preserve ? 1 : 0); }
            Quad(material, 0); Quad(material, 0.3f);
            foreach (float alpha in new[] { 0f, 0.5f, 1f })
            {
                var baseColor = lit ? Color.black : source; baseColor.a = alpha;
                material.SetColor(lit ? "_BaseColor" : "_UnlitColor", baseColor.gamma);
                HDShaderUtils.ResetMaterialKeywords(material);
                float weight = blend == 4 || (lit && preserve) ? 1 : alpha;
                float destination = blend == 1 ? 1 : 1 - alpha;
                var once = source * weight + background * destination;
                var expected = source * weight + once * destination;
                Near(expected, Center(Capture()), $"blend {blend}, alpha {alpha}");
            }
        }

        [Test]
        public void MaskNormalEmissionAndDegenerateTangentsUseRealMaterialInputs()
        {
            var material = Material(true, new Color(0.6f, 0.3f, 0.1f, 1));
            var renderer = Quad(material);
            material.SetFloat("_Metallic", 0.4f); material.SetFloat("_Smoothness", 0.6f);
            var scalar = Capture();
            material.SetTexture("_MaskMap", Solid(new Color(0.4f, 1, 0, 0.6f)));
            HDShaderUtils.ResetMaterialKeywords(material);
            Same(scalar, Capture());
            var mesh = Own(Object.Instantiate(renderer.GetComponent<MeshFilter>().sharedMesh));
            renderer.GetComponent<MeshFilter>().sharedMesh = mesh;
            mesh.tangents = null;
            Same(scalar, Capture());
            material.SetTexture("_NormalMap", Solid(new Color(1, 0.5f, 1, 0.5f)));
            HDShaderUtils.ResetMaterialKeywords(material); material.EnableKeyword("_NORMALMAP");
            Same(scalar, Capture());
            material.SetTexture("_NormalMap", Solid(new Color(1, 0.75f, 1, 0.75f)));
            var tilted = Center(Capture());
            Assert.Greater(Mathf.Abs(tilted.r - Center(scalar).r), 0.005f, "Normal texture must affect lighting");
            m_Sun.enabled = false; material.SetColor("_BaseColor", Color.black);
            material.SetFloat("_EmissiveExposureWeight", 0);
            material.SetColor("_EmissiveColor", new Color(0.4f, 0.2f, 0.1f));
            material.SetTexture("_EmissiveColorMap", Solid(new Color(0.5f, 0.25f, 1)));
            HDShaderUtils.ResetMaterialKeywords(material);
            Near(new Color(0.2f, 0.05f, 0.1f), Center(Capture()));
        }

        [Test]
        public void DoubleSidedNegativeScaleAndInstancingPreserveCoverage()
        {
            var material = Material(false, new Color(0.3f, 0.1f, 0.05f));
            material.SetFloat("_DoubleSidedEnable", 1); HDShaderUtils.ResetMaterialKeywords(material);
            var renderer = Quad(material); var reference = Capture();
            renderer.transform.localScale = new Vector3(-1, 1, 1);
            Same(reference, Capture());
            renderer.transform.rotation = Quaternion.Euler(0, 180, 0); Same(reference, Capture());
            material.enableInstancing = true;
            var other = Quad(material); other.transform.position = new Vector3(0.7f, 0, 0);
            bool batcher = m_Asset.enableSRPBatcher;
            try
            {
                // SRP Batcher takes precedence over GPU instancing. Disable it so
                // this comparison actually exercises the INSTANCING_ON variant.
                m_Asset.enableSRPBatcher = false;
                var instanced = Capture();
                material.enableInstancing = false; Same(instanced, Capture());
            }
            finally { m_Asset.enableSRPBatcher = batcher; }
        }

        [Test]
        public void SkinnedVerticesFollowBoneWithoutInvalidLighting()
        {
            var material = Material(true, Color.gray);
            var source = Quad(material); var mesh = Own(Object.Instantiate(source.GetComponent<MeshFilter>().sharedMesh));
            source.enabled = false;
            var bone = MakeObject("Bone").transform;
            var weights = new BoneWeight[mesh.vertexCount];
            for (int i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1 };
            mesh.boneWeights = weights; mesh.bindposes = new[] { Matrix4x4.identity };
            var skin = MakeObject("Skinned quad").AddComponent<SkinnedMeshRenderer>();
            skin.sharedMesh = mesh; skin.sharedMaterial = material; skin.bones = new[] { bone }; skin.rootBone = bone;
            skin.updateWhenOffscreen = true; skin.localBounds = new Bounds(Vector3.zero, Vector3.one * 8);
            var first = Capture(); Assert.Greater(Center(first).r, 0.01f);
            bone.position = new Vector3(1.5f, 0, 0);
            // BakeMesh updates deterministically in EditMode; runtime deformation is
            // additionally exercised by the PlayMode lifecycle fixture.
            var baked = Own(new Mesh()); skin.BakeMesh(baked);
            Assert.Greater(baked.vertices[0].x, mesh.vertices[0].x + 1f);
        }

        [Test]
        public void CameraABAIsolatesVolumeProjectionLightsAndViewport()
        {
            var material = Material(true, Color.gray); Quad(material);
            var local = MakeObject("Local light").AddComponent<Light>(); local.type = LightType.Point;
            local.transform.position = new Vector3(0.5f, 0, -1); local.intensity = 0.4f; local.range = 4; local.cullingMask = 1 << 30;
            var volumeObject = MakeObject("Volume"); volumeObject.layer = 28;
            var volume = volumeObject.AddComponent<Volume>(); volume.isGlobal = true;
            var profile = Own(ScriptableObject.CreateInstance<VolumeProfile>()); volume.sharedProfile = profile;
            profile.Add<ColorAdjustments>().postExposure.Override(2);
            var baseline = Capture();
            var other = MakeCamera(79, 61); other.orthographic = false;
            other.transform.position = new Vector3(0.5f, 0.2f, -4); other.transform.LookAt(Vector3.zero);
            other.GetComponent<HDAdditionalCameraData>().volumeLayerMask = (1 << 28) | (1 << 29);
            Capture(other); var restored = Capture(); Same(baseline, restored);
            SaveImage("camera-a", baseline); SaveImage("camera-aba", restored);
            var difference = new Color[baseline.Length];
            for (int i = 0; i < difference.Length; i++) difference[i] = new Color(Mathf.Abs(baseline[i].r - restored[i].r), Mathf.Abs(baseline[i].g - restored[i].g), Mathf.Abs(baseline[i].b - restored[i].b)) * 20;
            SaveImage("camera-difference-x20", difference);
            m_Camera.GetComponent<HDAdditionalCameraData>().volumeLayerMask = (1 << 28) | (1 << 29);
            Assert.Greater(Center(Capture()).r, Center(baseline).r * 2);
            m_Camera.GetComponent<HDAdditionalCameraData>().volumeLayerMask = 1 << 29;
            Same(baseline, Capture());
            var sentinel = new Color(0.13f, 0.27f, 0.39f, 1);
            var previous = RenderTexture.active; RenderTexture.active = m_Camera.targetTexture; GL.Clear(true, true, sentinel); RenderTexture.active = previous;
            m_Camera.rect = new Rect(0.25f, 0.25f, 0.5f, 0.5f);
            var partial = Capture();
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
                if (x < Width / 4 || x >= Width * 3 / 4 || y < Height / 4 || y >= Height * 3 / 4)
                    Near(sentinel, partial[y * Width + x], "Outside camera viewport");
        }

        [Test]
        public void ExplicitViewMatrixAndVolumeAnchorDoNotUseTransformAsCameraPosition()
        {
            var material = Material(true, Color.gray); material.SetFloat("_Metallic", 1); material.SetFloat("_Smoothness", 0.8f); Quad(material);
            m_Camera.orthographic = false;
            m_Camera.transform.position = new Vector3(0.4f, 0.2f, -3); m_Camera.transform.LookAt(Vector3.zero);
            var matrix = m_Camera.worldToCameraMatrix; var expected = Capture();
            m_Camera.transform.position = new Vector3(0, 0, -3); m_Camera.transform.rotation = Quaternion.identity;
            m_Camera.worldToCameraMatrix = matrix; Same(expected, Capture());
            m_Camera.ResetWorldToCameraMatrix();
            var volumeObject = MakeObject("Local volume"); volumeObject.layer = 29;
            var box = volumeObject.AddComponent<BoxCollider>(); box.size = Vector3.one * 2;
            var volume = volumeObject.AddComponent<Volume>(); volume.isGlobal = false; volume.blendDistance = 0;
            volume.sharedProfile = Own(ScriptableObject.CreateInstance<VolumeProfile>());
            volume.sharedProfile.Add<ColorAdjustments>().postExposure.Override(2);
            var data = m_Camera.GetComponent<HDAdditionalCameraData>(); data.volumeLayerMask = 1 << 29;
            var outside = Capture(); var anchor = MakeObject("Volume anchor").transform; data.volumeAnchorOverride = anchor;
            var inside = Capture(); Assert.Greater(Center(inside).r, Center(outside).r * 2);
            data.volumeAnchorOverride = null; Same(outside, Capture());
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator BakedReflectionProbeTexturesAreIsolatedAcrossCamerasAndReplacement()
        {
            var material = Material(true, Color.white); material.SetFloat("_Metallic", 1); material.SetFloat("_Smoothness", 0.8f);
            var firstQuad = Quad(material); var secondQuad = Quad(material); secondQuad.transform.position = new Vector3(5, 0, 0); m_Sun.enabled = false;
            for (int p = 0; p < 2; p++)
            {
                var cube = Own(new Cubemap(16, TextureFormat.RGBAHalf, true));
                for (int face = 0; face < 6; face++)
                {
                    var colors = new Color[256]; for (int i = 0; i < colors.Length; i++) colors[i] = p == 0 ? Color.red : Color.green;
                    cube.SetPixels(colors, (CubemapFace)face);
                }
                cube.Apply();
                var obj = MakeObject("Probe " + p); obj.transform.position = new Vector3(p * 5, 0, 0);
                var native = obj.AddComponent<ReflectionProbe>(); native.size = Vector3.one * 3;
                var hd = obj.AddComponent<HDAdditionalReflectionData>(); hd.mode = ProbeSettings.Mode.Baked;
                hd.influenceVolume.boxSize = Vector3.one * 3; hd.SetTexture(ProbeSettings.Mode.Baked, cube);
            }
            yield return null; // Native reflection-probe spatial registration runs in the player loop.
            var first = Capture();
            TestContext.WriteLine("First reflection capture: count=" + Shader.GetGlobalInt("_MobileReflectionCount16") + ", HDRP=" + Shader.GetGlobalInt("_MobileReflectionHasHDRPProbes") + ", center=" + Center(first));
            m_Asset.enableSRPBatcher = false;
            Same(first, Capture());
            m_Asset.enableSRPBatcher = true;
            Assert.Greater(Center(first).r, Center(first).g + 0.1f, "First camera: " + Center(first));
            var other = MakeCamera(Width, Height); other.transform.position = new Vector3(5, 0, -3);
            var second = Capture(other); Assert.Greater(Center(second).g, Center(second).r + 0.1f, "Second camera: " + Center(second));
            Same(first, Capture());
            var replacement = Own(Object.Instantiate(m_Asset)); GraphicsSettings.renderPipelineAsset = replacement; QualitySettings.renderPipeline = replacement;
            Same(first, Capture());
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator OverlappingBakedProbesBlendAndRestoreBoxSettings()
        {
            var material = Material(true, Color.white); material.SetFloat("_Metallic", 1); material.SetFloat("_Smoothness", 0.8f);
            var renderer = Quad(material); renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            m_Sun.enabled = false;
            for (int p = 0; p < 2; ++p)
            {
                var cube = Own(new Cubemap(16, TextureFormat.RGBAHalf, true));
                for (int face = 0; face < 6; ++face)
                {
                    var colors = new Color[256]; for (int i = 0; i < colors.Length; ++i) colors[i] = p == 0 ? Color.red : Color.green;
                    cube.SetPixels(colors, (CubemapFace)face);
                }
                cube.Apply();
                var obj = MakeObject("Blend probe " + p); obj.transform.position = new Vector3(p == 0 ? -0.5f : 0.5f, 0, 0);
                var native = obj.AddComponent<ReflectionProbe>(); native.size = Vector3.one * 4;
                var hd = obj.AddComponent<HDAdditionalReflectionData>(); hd.mode = ProbeSettings.Mode.Baked;
                hd.influenceVolume.boxSize = Vector3.one * 4;
                hd.influenceVolume.boxBlendDistancePositive = hd.influenceVolume.boxBlendDistanceNegative = Vector3.one;
                hd.useInfluenceVolumeAsProxyVolume = true;
                hd.SetTexture(ProbeSettings.Mode.Baked, cube);
            }
            yield return null;
            var pixel = Center(Capture());
            Assert.Greater(pixel.r, 0.05f); Assert.Greater(pixel.g, 0.05f);
            Assert.Less(Mathf.Abs(pixel.r - pixel.g), 0.08f);
        }

        [Test]
        public void FiftyRenderedTargetResizesAndAssetSwitchesRemainDeterministic()
        {
            Quad(Material(false, new Color(0.2f, 0.1f, 0.3f)));
            var baseline = Capture();
            var originalTarget = m_Camera.targetTexture;
            var alternate = Own(new RenderTexture(73, 57, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); alternate.Create();
            var replacement = Own(Object.Instantiate(m_Asset));
            for (int i = 0; i < 50; i++)
            {
                var asset = i % 2 == 0 ? replacement : m_Asset;
                GraphicsSettings.renderPipelineAsset = asset; QualitySettings.renderPipeline = asset;
                m_Camera.targetTexture = alternate; Capture();
                m_Camera.targetTexture = originalTarget; Same(baseline, Capture());
            }
        }

        VolumeProfile CameraProfile(float priority = 0)
        {
            var volumeObject = MakeObject("Camera profile"); volumeObject.layer = 29;
            var volume = volumeObject.AddComponent<Volume>(); volume.isGlobal = true; volume.priority = priority;
            var profile = Own(ScriptableObject.CreateInstance<VolumeProfile>()); volume.sharedProfile = profile;
            m_Camera.GetComponent<HDAdditionalCameraData>().volumeLayerMask = 1 << 29;
            return profile;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PostProcessingUsesVolumesAndCameraAntialiasing(bool deferred)
        {
            Assert.IsNull(typeof(MobileRenderSettings).GetField("bloom"));
            Assert.IsNull(typeof(MobileRenderSettings).GetField("fxaa"));
            if (deferred) UseDeferred();
            var cameraSettings = CameraProfile(-500);
            var exposure = cameraSettings.Add<Exposure>(); exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(0);
            cameraSettings.Add<Tonemapping>().mode.Override(TonemappingMode.None);
            var profile = CameraProfile();
            var bloom = profile.Add<Bloom>(); bloom.intensity.Override(0);
            var post = profile.Add<MobilePostProcessing>(); post.fxaa.Override(false);
            var cameraData = m_Camera.GetComponent<HDAdditionalCameraData>();
            var material = Material(false, Color.gray);
            material.SetColor("_EmissiveColor", Color.white * 8);
            var quad = Quad(material); quad.transform.localScale = Vector3.one * 0.4f;
            quad.transform.rotation = Quaternion.Euler(0, 0, 17);
            var baseline = Capture();
            float Difference(Color[] a, Color[] b)
            {
                float difference = 0;
                for (int i = 0; i < a.Length; i++) difference += Mathf.Abs(a[i].r - b[i].r);
                return difference;
            }
            bloom.intensity.Override(1);
            Assert.Greater(Difference(baseline, Capture()), 1f, "Bloom Volume must enable the filter without any asset flag.");
            bloom.intensity.Override(0);
            Same(baseline, Capture());
            post.fxaa.Override(true);
            Same(baseline, Capture()); // Legacy Volume cannot enable camera AA.
            cameraData.antialiasing = HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing;
            var antialiased = Capture();
            Assert.Greater(Difference(baseline, antialiased), 0.1f, "Camera FXAA must affect silhouette edges.");
            post.fxaa.Override(false);
            Same(antialiased, Capture()); // Legacy Volume cannot disable camera AA.
            cameraData.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
            Same(baseline, Capture());
            bloom.intensity.Override(1);
            cameraData.antialiasing = HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing;
            foreach (var root in m_Scene.GetRootGameObjects())
                if (root.TryGetComponent<Volume>(out var volume) && volume.sharedProfile == profile) volume.weight = 0;
            Same(antialiased, Capture()); // Volume weight does not change camera AA.
        }

        [Test]
        public void AutomaticExposureInitializesFromSharedFixedEV()
        {
            var exposure = CameraProfile().Add<Exposure>();
            exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(10);
            var material = Material(true, Color.black);
            material.SetColor("_EmissiveColor", new Color(200f, 100f, 300f));
            material.SetFloat("_EmissiveExposureWeight", 1);
            Quad(material); m_Sun.enabled = false;
            var expected = Capture();
            foreach (var mode in new[] { ExposureMode.Automatic, ExposureMode.AutomaticHistogram, ExposureMode.CurveMapping })
            {
                exposure.mode.Override(mode);
                Same(expected, Capture());
                Assert.AreEqual(mode, exposure.mode.value);
            }
        }

        [Test]
        public void DetailAlbedoUsesHDRPOverlayAndMask()
        {
            var material = Material(true, Color.gray); Quad(material);
            var baseline = Capture();
            material.SetTexture("_DetailMap", Solid(new Color(0.5f, 0.5f, 0.5f, 0.5f)));
            material.SetVector("_UVDetailsMappingMask", new Vector4(1, 0, 0, 0));
            material.SetFloat("_DetailAlbedoScale", 1); material.SetFloat("_DetailSmoothnessScale", 1); material.SetFloat("_DetailNormalScale", 1);
            HDShaderUtils.ResetMaterialKeywords(material);
            Same(baseline, Capture());
            material.SetTexture("_DetailMap", Solid(new Color(0, 0.5f, 0.5f, 0.5f)));
            Assert.Less(Center(Capture()).grayscale, Center(baseline).grayscale * 0.2f);
            material.SetTexture("_MaskMap", Solid(new Color(0, 1, 0, 0.3f)));
            HDShaderUtils.ResetMaterialKeywords(material);
            Same(baseline, Capture());
        }

        [Test]
        public void InspectorSettingsKeepTheLivePipelineAndExposureTextures()
        {
            var profile = CameraProfile(); var exposure = profile.Add<Exposure>();
            exposure.mode.Override(ExposureMode.Automatic);
            Quad(Material(false, Color.gray)); Capture();
            var pipeline = RenderPipelineManager.currentPipeline;
            var history = MobileExposureControl.active;
            var texture = history.GetTexture(m_Camera);
            m_Asset.mobileSettings.renderScale = 0.75f;
            typeof(HDRenderPipelineAsset).GetMethod("OnValidate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(m_Asset, null);
            typeof(RenderPipelineManager).GetMethod("PrepareRenderPipeline", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { m_Asset });
            Assert.AreSame(pipeline, RenderPipelineManager.currentPipeline);
            Assert.AreSame(history, MobileExposureControl.active);
            Assert.AreSame(texture, history.GetTexture(m_Camera));
            Capture();
            Assert.IsTrue(((HDRenderPipeline)pipeline).CanUpdateMobileAssetInPlace(m_Asset));
            m_Asset.rendererMode = HDRendererMode.Desktop;
            Assert.IsFalse(((HDRenderPipeline)pipeline).CanUpdateMobileAssetInPlace(m_Asset));
            m_Asset.rendererMode = HDRendererMode.Mobile;
        }

        [TestCase(1f)] [TestCase(0.5f)]
        public void SceneViewFinalDepthOccludesEditorOverlays(float scale)
        {
            m_Camera.cameraType = CameraType.SceneView;
            m_Asset.mobileSettings.renderScale = scale;
            var renderer = Quad(Material(false, Color.red));
            var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
            var overlay = Material(false, Color.blue);
            foreach (float z in new[] { 1f, -1f })
            {
                var baseline = Center(Capture());
                var cmd = new CommandBuffer(); var previous = RenderTexture.active;
                var read = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
                try
                {
                    cmd.SetRenderTarget(m_Camera.targetTexture);
                    cmd.SetViewport(new Rect(0, 0, Width, Height));
                    cmd.SetViewProjectionMatrices(m_Camera.worldToCameraMatrix, GL.GetGPUProjectionMatrix(m_Camera.projectionMatrix, true));
                    cmd.DrawMesh(mesh, Matrix4x4.Translate(new Vector3(0, 0, z)), overlay, 0, overlay.FindPass("MobileForward"));
                    Graphics.ExecuteCommandBuffer(cmd);
                    RenderTexture.active = m_Camera.targetTexture;
                    read.ReadPixels(new Rect(Width / 2, Height / 2, 1, 1), 0, 0); read.Apply();
                    var pixel = read.GetPixel(0, 0);
                    if (z > 0) Near(baseline, pixel, "Occluded overlay must not overwrite scene color");
                    else Assert.Greater(pixel.b, pixel.r + 0.1f, "Foreground overlay must be visible");
                }
                finally { cmd.Release(); RenderTexture.active = previous; Object.DestroyImmediate(read); }
            }
        }

        [Test]
        public void RuntimeEnvironmentDoesNotAddDiffuseOverLightmaps()
        {
            var profile = CameraProfile(); profile.Add<VisualEnvironment>().skyType.Override((int)SkyType.Gradient);
            var sky = profile.Add<GradientSky>(); sky.top.Override(Color.green * 4); sky.middle.Override(Color.green * 4); sky.bottom.Override(Color.green * 4);
            var exposure = profile.Add<Exposure>(); exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(0);
            profile.Add<IndirectLightingController>().reflectionLightingMultiplier.Override(0);
            m_Sun.enabled = false;
            var renderer = Quad(Material(true, Color.white));
            var previous = LightmapSettings.lightmaps; var previousMode = LightmapSettings.lightmapsMode;
            try
            {
                LightmapSettings.lightmapsMode = LightmapsMode.NonDirectional;
                LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = Solid(Color.red) } };
                renderer.lightmapIndex = 0; renderer.lightmapScaleOffset = new Vector4(1, 1, 0, 0);
                var baseline = Capture(); Assert.Greater(Center(baseline).r, 0.1f);
                m_Asset.mobileSettings.environmentLighting = true;
                m_Asset.mobileSettings.environmentDiffuseSource = MobileEnvironmentDiffuseSource.RuntimeSky;
                Same(baseline, Capture());
                renderer.lightmapIndex = -1;
                var dynamic = Center(Capture()); Assert.Greater(dynamic.g, dynamic.r + 0.2f);
            }
            finally { renderer.lightmapIndex = -1; LightmapSettings.lightmaps = previous; LightmapSettings.lightmapsMode = previousMode; }
        }

        [Test]
        public void RuntimeEnvironmentLightsDiffuseAndMetalWithoutExposureFeedback()
        {
            var profile = CameraProfile();
            profile.Add<VisualEnvironment>().skyType.Override((int)SkyType.Gradient);
            var sky = profile.Add<GradientSky>(); sky.top.Override(Color.green * 4); sky.middle.Override(Color.green * 4); sky.bottom.Override(Color.green * 4);
            var exposure = profile.Add<Exposure>(); exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(0);
            m_Sun.enabled = false;
            var material = Material(true, Color.white); Quad(material);
            var dark = Center(Capture());
            m_Asset.mobileSettings.environmentLighting = true;
            m_Asset.mobileSettings.environmentDiffuseSource = MobileEnvironmentDiffuseSource.RuntimeSky;
            var diffuse = Center(Capture()); Assert.Greater(diffuse.g, dark.g + 0.2f); Assert.Greater(diffuse.g, diffuse.r + 0.2f);
            material.SetFloat("_Metallic", 1); material.SetFloat("_Smoothness", 0.8f);
            var metal = Center(Capture()); Assert.Greater(metal.g, 0.2f);
            var state = MobileEnvironmentControl.active.Get(m_Camera); int generation = state.generation;
            exposure.fixedExposure.Override(1);
            Near(metal * 0.5f, Center(Capture())); Assert.AreEqual(generation, state.generation);
            m_Camera.transform.position += Vector3.right * 0.1f; Capture(); Assert.AreEqual(generation, state.generation);
            var indirect = profile.Add<IndirectLightingController>(); indirect.reflectionLightingMultiplier.Override(0);
            Assert.Less(Center(Capture()).maxColorComponent, 0.002f);
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator RuntimeEnvironmentFillsProbeBoundaryAndPreservesBlackLocalProbe()
        {
            var profile = CameraProfile(); profile.Add<VisualEnvironment>().skyType.Override((int)SkyType.Gradient);
            var sky = profile.Add<GradientSky>(); sky.top.Override(Color.green); sky.middle.Override(Color.green); sky.bottom.Override(Color.green);
            var exposure = profile.Add<Exposure>(); exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(0);
            m_Asset.mobileSettings.environmentLighting = true; m_Sun.enabled = false;
            var material = Material(true, Color.white); material.SetFloat("_Metallic", 1); material.SetFloat("_Smoothness", 0.8f); Quad(material);
            var cube = Own(new Cubemap(8, TextureFormat.RGBAHalf, true));
            var pixels = new Color[64]; for (int i = 0; i < pixels.Length; ++i) pixels[i] = Color.red;
            for (int f = 0; f < 6; ++f) cube.SetPixels(pixels, (CubemapFace)f); cube.Apply();
            var obj = MakeObject("Environment local probe"); obj.AddComponent<ReflectionProbe>();
            var hd = obj.AddComponent<HDAdditionalReflectionData>(); hd.mode = ProbeSettings.Mode.Baked;
            hd.influenceVolume.boxSize = Vector3.one * 4;
            hd.influenceVolume.boxBlendDistancePositive = hd.influenceVolume.boxBlendDistanceNegative = Vector3.one;
            hd.SetTexture(ProbeSettings.Mode.Baked, cube);
            yield return null;
            var inside = Center(Capture()); Assert.Greater(inside.r, 0.1f); Assert.Less(inside.g, 0.01f);
            obj.transform.position = Vector3.right * 1.5f;
            var boundary = Center(Capture()); Assert.Greater(boundary.r, 0.05f); Assert.Greater(boundary.g, 0.05f);
            obj.transform.position = Vector3.right * 5;
            var outside = Center(Capture()); Assert.Greater(outside.g, 0.1f); Assert.Less(outside.r, 0.01f);
            obj.transform.position = Vector3.zero;
            for (int i = 0; i < pixels.Length; ++i) pixels[i] = Color.black;
            for (int f = 0; f < 6; ++f) cube.SetPixels(pixels, (CubemapFace)f); cube.Apply();
            Assert.Less(Center(Capture()).maxColorComponent, 0.002f, "A valid black probe must not be mistaken for missing data.");
        }

        [Test]
        public void RuntimeEnvironmentUsesIndependentCameraVolumeStacks()
        {
            var profile = CameraProfile(); profile.Add<VisualEnvironment>().skyType.Override((int)SkyType.Gradient);
            var sky = profile.Add<GradientSky>(); sky.top.Override(Color.green); sky.middle.Override(Color.green); sky.bottom.Override(Color.green);
            var exposure = profile.Add<Exposure>(); exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(0);
            m_Asset.mobileSettings.environmentLighting = true; m_Asset.mobileSettings.environmentDiffuseSource = MobileEnvironmentDiffuseSource.RuntimeSky;
            m_Sun.enabled = false; Quad(Material(true, Color.white));
            var first = Capture(); Assert.Greater(Center(first).g, 0.1f);
            var other = MakeCamera(Width, Height); other.GetComponent<HDAdditionalCameraData>().volumeLayerMask = 1 << 28;
            var obj = MakeObject("Second environment Volume"); obj.layer = 28;
            var volume = obj.AddComponent<Volume>(); volume.isGlobal = true;
            var secondProfile = Own(ScriptableObject.CreateInstance<VolumeProfile>()); volume.sharedProfile = secondProfile;
            secondProfile.Add<VisualEnvironment>().skyType.Override((int)SkyType.Gradient);
            var secondSky = secondProfile.Add<GradientSky>(); secondSky.top.Override(Color.red); secondSky.middle.Override(Color.red); secondSky.bottom.Override(Color.red);
            var secondExposure = secondProfile.Add<Exposure>(); secondExposure.mode.Override(ExposureMode.Fixed); secondExposure.fixedExposure.Override(0);
            var second = Center(Capture(other)); Assert.Greater(second.r, second.g + 0.1f);
            Same(first, Capture());
            Assert.AreNotSame(MobileEnvironmentControl.active.Get(m_Camera).reflection, MobileEnvironmentControl.active.Get(other).reflection);
        }

        [Test]
        public void DetailUV1UsesRawMeshCoordinates()
        {
            var material = Material(true, Color.gray); var renderer = Quad(material);
            var filter = renderer.GetComponent<MeshFilter>(); var mesh = Own(Object.Instantiate(filter.sharedMesh)); filter.sharedMesh = mesh;
            var uv0 = new Vector2[mesh.vertexCount]; var uv1 = new Vector2[mesh.vertexCount];
            for (int i = 0; i < uv0.Length; ++i) { uv0[i] = new Vector2(0.25f, 0.5f); uv1[i] = new Vector2(0.75f, 0.5f); }
            mesh.uv = uv0; mesh.uv2 = uv1;
            var baseline = Capture();
            var texture = Own(new Texture2D(2, 1, TextureFormat.RGBAFloat, false, true)); texture.filterMode = FilterMode.Point;
            texture.SetPixels(new[] { new Color(0, 0.5f, 0.5f, 0.5f), new Color(0.5f, 0.5f, 0.5f, 0.5f) }); texture.Apply();
            material.SetTexture("_DetailMap", texture); material.SetFloat("_DetailAlbedoScale", 1); HDShaderUtils.ResetMaterialKeywords(material);
            material.SetVector("_UVDetailsMappingMask", new Vector4(0, 1, 0, 0));
            Same(baseline, Capture());
            material.SetVector("_UVDetailsMappingMask", new Vector4(1, 0, 0, 0));
            Assert.Less(Center(Capture()).grayscale, Center(baseline).grayscale * 0.2f);
        }

        [Test]
        public void AutomaticExposureConvergesOnUniformHDRSky()
        {
            var profile = CameraProfile();
            profile.Add<VisualEnvironment>().skyType.Override((int)SkyType.Gradient);
            var sky = profile.Add<GradientSky>();
            sky.top.Override(Color.white * 1000); sky.middle.Override(Color.white * 1000); sky.bottom.Override(Color.white * 1000);
            sky.skyIntensityMode.Override(SkyIntensityMode.Multiplier); sky.multiplier.Override(1);
            var exposure = profile.Add<Exposure>(); exposure.mode.Override(ExposureMode.Automatic);
            exposure.adaptationMode.Override(AdaptationMode.Fixed); exposure.fixedExposure.Override(10); exposure.limitMax.Override(20);
            m_Camera.GetComponent<HDAdditionalCameraData>().clearColorMode = HDAdditionalCameraData.ClearColorMode.Sky;
            Capture();
            var pixels = Capture();
            float ev = Mathf.Log(1000 * 100 / 12.5f, 2);
            Near(Color.white * (1000 * MobileExposure.FromEV100(ev, 0, MobileExposure.LensScale)), Center(pixels));
            MobileExposureControl.Reset(m_Camera);
            Near(Color.white * (1000 * MobileExposure.FromEV100(10, 0, MobileExposure.LensScale)), Center(Capture()));
        }

        [Test]
        public void ExposureControlCameraOverrideIsRespected()
        {
            var exposure = CameraProfile().Add<Exposure>();
            exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(2);
            m_Sun.enabled = false;
            var material = Material(true, Color.black);
            var source = new Color(0.2f, 0.1f, 0.3f);
            material.SetColor("_EmissiveColor", source); material.SetFloat("_EmissiveExposureWeight", 1);
            Quad(material);
            var additional = m_Camera.GetComponent<HDAdditionalCameraData>();
            additional.customRenderingSettings = true;
            var mask = additional.renderingPathCustomFrameSettingsOverrideMask;
            mask.mask[(uint)FrameSettingsField.ExposureControl] = true;
            additional.renderingPathCustomFrameSettingsOverrideMask = mask;
            additional.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.ExposureControl, false);
            Near(source, Center(Capture()));
            additional.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.ExposureControl, true);
            Near(source * MobileExposure.FromEV100(2, 0, MobileExposure.LensScale), Center(Capture()));
        }

        [Test]
        public void ExposurePrecedesHalfStorageAndRespectsUnlitAndEmissiveWeights()
        {
            var exposure = CameraProfile().Add<Exposure>(); exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(0);
            var material = Material(true, Color.gray); var renderer = Quad(material);
            m_Sun.intensity = 2;
            var baseline = Capture();
            m_Sun.intensity *= 1048576; exposure.fixedExposure.Override(20);
            Same(baseline, Capture()); // Old post-storage exposure clips at 65504 and fails this.
            m_Sun.enabled = false;
            material.SetColor("_BaseColor", Color.black); material.SetColor("_EmissiveColor", new Color(0.2f, 0.1f, 0.3f));
            material.SetFloat("_EmissiveExposureWeight", 0); Near(new Color(0.2f, 0.1f, 0.3f), Center(Capture()));
            exposure.fixedExposure.Override(2); material.SetFloat("_EmissiveExposureWeight", 1);
            Near(new Color(0.2f, 0.1f, 0.3f) * MobileExposure.FromEV100(2, 0, MobileExposure.LensScale), Center(Capture()));
            exposure.compensation.Override(1);
            Near(new Color(0.2f, 0.1f, 0.3f) * MobileExposure.FromEV100(2, 1, MobileExposure.LensScale), Center(Capture()));
            renderer.sharedMaterial = Material(false, new Color(0.2f, 0.1f, 0.3f));
            var unlit = Capture(); exposure.fixedExposure.Override(16); Same(unlit, Capture());
            renderer.sharedMaterial = material;
            m_Camera.aperture = 4; m_Camera.shutterSpeed = 0.125f; m_Camera.iso = 100;
            exposure.mode.Override(ExposureMode.UsePhysicalCamera);
            Near(new Color(0.2f, 0.1f, 0.3f) * MobileExposure.FromEV100(7, 1, MobileExposure.LensScale), Center(Capture()));
        }

        [Test]
        public void VolumeSkyFallbackRotatesExposesAndPreservesClearMode()
        {
            var profile = CameraProfile();
            var environment = profile.Add<VisualEnvironment>(); environment.skyType.Override((int)SkyType.HDRI);
            var hdri = profile.Add<HDRISky>(); hdri.skyIntensityMode.Override(SkyIntensityMode.Multiplier); hdri.multiplier.Override(1);
            var cube = Own(new Cubemap(4, TextureFormat.RGBAFloat, false));
            for (int face = 0; face < 6; face++)
            {
                var colors = new Color[16]; for (int i = 0; i < 16; i++) colors[i] = face == (int)CubemapFace.PositiveZ ? Color.red : Color.green;
                cube.SetPixels(colors, (CubemapFace)face);
            }
            cube.Apply(); hdri.hdriSky.Override(cube);
            var cameraData = m_Camera.GetComponent<HDAdditionalCameraData>();
            cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Sky;
            Near(Color.red / MobileExposure.LensScale, Center(Capture()));
            hdri.rotation.Override(90); Near(Color.green / MobileExposure.LensScale, Center(Capture()));
            var exposure = profile.Add<Exposure>(); exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(3);
            Near(Color.green * MobileExposure.FromEV100(3, 0, MobileExposure.LensScale), Center(Capture()));
            cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            cameraData.backgroundColorHDR = new Color(0.1f, 0.2f, 0.3f);
            Near(cameraData.backgroundColorHDR, Center(Capture()));
            cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Sky;
            environment.skyType.Override((int)SkyType.Gradient);
            var gradient = profile.Add<GradientSky>(); gradient.middle.Override(new Color(0.3f, 0.2f, 0.1f));
            gradient.skyIntensityMode.Override(SkyIntensityMode.Multiplier); gradient.multiplier.Override(1);
            Near(gradient.middle.value * MobileExposure.FromEV100(3, 0, MobileExposure.LensScale), Center(Capture()));
            environment.skyType.Override((int)SkyType.PhysicallyBased);
            profile.Add<PhysicallyBasedSky>();
            Assert.Greater(Center(Capture()).b, 0, "Physical sky fallback must render without desktop resources");
        }

        [Test]
        public void LegacySkyboxFallbackIsDisplayReferredAndCanBeDisabled()
        {
            var previous = RenderSettings.skybox;
            var profile = CameraProfile();
            var exposure = profile.Add<Exposure>(); exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(14);
            m_Camera.GetComponent<HDAdditionalCameraData>().clearColorMode = HDAdditionalCameraData.ClearColorMode.Sky;
            var cube = Own(new Cubemap(4, TextureFormat.RGBAHalf, false));
            for (int face = 0; face < 6; face++)
            { var colors = new Color[16]; for (int i = 0; i < 16; i++) colors[i] = Color.red; cube.SetPixels(colors, (CubemapFace)face); }
            cube.Apply();
            try
            {
                var material = Own(new Material(Shader.Find("Skybox/Cubemap")));
                material.SetTexture("_Tex", cube); material.SetColor("_Tint", Color.white); material.SetFloat("_Exposure", 0.5f);
                RenderSettings.skybox = material; Near(Color.red, Center(Capture()));
                exposure.fixedExposure.Override(0); Near(Color.red, Center(Capture()));
                RenderSettings.skybox = Own(new Material(Shader.Find("Skybox/Procedural")));
                var procedural = Capture(); Assert.Greater(Center(procedural).maxColorComponent, 0.05f);
                m_Camera.orthographic = false; m_Camera.transform.rotation = Quaternion.Euler(-15, 0, 0);
                SaveImage("skybox-procedural", Capture());
                m_Camera.orthographic = true; m_Camera.transform.rotation = Quaternion.identity;
                m_Asset.mobileSettings.skyFallback = false; Near(Color.black, Center(Capture()));
                m_Asset.mobileSettings.sky = cube; Near(Color.red / MobileExposure.LensScale, Center(Capture()));
            }
            finally { RenderSettings.skybox = previous; }
        }

        [Test]
        public void RenderTextureQuadrantsPreserveWorldUpAndRight()
        {
            var points = new[] { new Vector3(-0.6f, 0.6f, 0), new Vector3(0.6f, 0.6f, 0), new Vector3(-0.6f, -0.6f, 0), new Vector3(0.6f, -0.6f, 0) };
            var colors = new[] { Color.red, Color.green, Color.blue, Color.white };
            for (int i = 0; i < points.Length; i++)
            { var renderer = Quad(Material(false, colors[i])); renderer.transform.position = points[i]; renderer.transform.localScale = Vector3.one * 0.4f; }
            var pixels = Capture();
            for (int i = 0; i < points.Length; i++)
            {
                var viewport = m_Camera.WorldToViewportPoint(points[i]);
                Near(colors[i], pixels[(int)(viewport.y * Height) * Width + (int)(viewport.x * Width)], "World quadrant " + i);
            }
            SaveImage("orientation", pixels);
        }

        [TestCase(false)] [TestCase(true)]
        public void ForwardShadowSelectionAndPickingShareCutoutCoverage(bool lit)
        {
            var material = Material(lit, Color.white);
            var alpha = Own(new Texture2D(2, 2, TextureFormat.RGBA32, false, true)); alpha.filterMode = FilterMode.Point;
            alpha.SetPixels(new[] { new Color(1, 1, 1, 0), Color.white, new Color(1, 1, 1, 0), Color.white }); alpha.Apply();
            material.SetTexture(lit ? "_BaseColorMap" : "_UnlitColorMap", alpha);
            material.SetFloat("_AlphaCutoffEnable", 1); material.SetFloat("_AlphaCutoff", 0.5f);
            var renderer = Quad(material); renderer.enabled = false;
            var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
            var color = Own(new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); color.Create();
            var depth = Own(new RenderTexture(Width, Height, 24, RenderTextureFormat.Depth)); depth.Create();
            var output = Own(new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)); output.Create();
            var read = Own(new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true));
            var depthReader = Own(new Material(Shader.Find("Hidden/HDRP/Tests/MobileDepthCoverage")));
            Shader.EnableKeyword("HDRP_MOBILE_RENDERER");
            Color[] Coverage(string passName, Quaternion rotation)
            {
                int pass = material.FindPass(passName); Assert.GreaterOrEqual(pass, 0);
                ShaderUtil.CompilePass(material, pass, true);
                var command = new CommandBuffer { name = "Mobile auxiliary pass coverage" };
                var previous = RenderTexture.active;
                try
                {
                    var projection = GL.GetGPUProjectionMatrix(m_Camera.projectionMatrix, true);
                    command.SetRenderTarget(color, depth);
                    // ClearRenderTarget takes logical depth; Unity reverses it for
                    // the device. Passing raw reversed-Z zero clears to near depth.
                    command.ClearRenderTarget(true, true, Color.black, 1);
                    command.SetViewport(new Rect(0, 0, Width, Height));
                    command.SetViewProjectionMatrices(m_Camera.worldToCameraMatrix, m_Camera.projectionMatrix);
                    command.SetGlobalMatrix("_MobileVP", projection * m_Camera.worldToCameraMatrix);
                    command.SetGlobalFloat("_MobileShadowNormalBias", 0);
                    command.DrawMesh(mesh, Matrix4x4.TRS(Vector3.zero, rotation, Vector3.one), material, 0, pass);
                    command.SetGlobalTexture("_TestDepth", depth, RenderTextureSubElement.Depth);
                    command.SetRenderTarget(output);
                    command.DrawProcedural(Matrix4x4.identity, depthReader, 0, MeshTopology.Triangles, 3);
                    Graphics.ExecuteCommandBuffer(command);
                    RenderTexture.active = output; read.ReadPixels(new Rect(0, 0, Width, Height), 0, 0); read.Apply(); return read.GetPixels();
                }
                finally { RenderTexture.active = previous; command.Release(); }
            }
            foreach (bool doubleSided in new[] { false, true })
            {
                material.SetFloat("_DoubleSidedEnable", doubleSided ? 1 : 0); HDShaderUtils.ResetMaterialKeywords(material);
                foreach (var rotation in new[] { Quaternion.identity, Quaternion.Euler(0, 180, 0) })
                {
                    var expected = Coverage("MobileForward", rotation);
                    if (rotation == Quaternion.identity)
                    {
                        int count = 0; foreach (var pixel in expected) if (pixel.r > 0.5f) count++;
                        Assert.That(count, Is.InRange(200, 1500), "Forward must render a non-empty half-quad");
                    }
                    foreach (string pass in new[] { "ShadowCaster", "SceneSelectionPass", "ScenePickingPass" })
                    {
                        var actual = Coverage(pass, rotation);
                        for (int i = 0; i < expected.Length; i++) Near(expected[i], actual[i], pass + ", double-sided=" + doubleSided + ", rotation=" + rotation.eulerAngles + ", pixel=" + i);
                    }
                }
            }
        }

        [Test]
        public void RealCascadeSceneCastsShadowsDuringCameraMotion()
        {
            m_Camera.orthographic = false; m_Camera.transform.position = new Vector3(0, 3, -3); m_Camera.transform.LookAt(new Vector3(0, 0, 10));
            m_Sun.transform.rotation = Quaternion.Euler(45, -30, 0); m_Sun.shadows = LightShadows.Hard;
            var material = Material(true, Color.gray);
            var ground = Quad(material); ground.transform.position = new Vector3(0, -1, 12);
            ground.transform.rotation = Quaternion.Euler(90, 0, 0); ground.transform.localScale = new Vector3(20, 40, 1);
            for (int i = 0; i < 8; i++)
            {
                var cube = Own(GameObject.CreatePrimitive(PrimitiveType.Cube)); cube.layer = 30;
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cube, m_Scene);
                cube.transform.position = new Vector3((i % 2 == 0 ? -1 : 1) * 1.5f, 0, 2 + i * 3);
                cube.GetComponent<Renderer>().sharedMaterial = material;
            }
            m_Asset.mobileSettings.mainLightShadows = false; var noShadows = Capture();
            m_Asset.mobileSettings.mainLightShadows = true; m_Asset.mobileSettings.cascadeBlend = 0;
            var before = Capture(); SaveImage("cascade-before", before);
            m_Asset.mobileSettings.cascadeBlend = 0.1f; var after = Capture(); SaveImage("cascade-after", after);
            int darkened = 0; var difference = new Color[after.Length];
            for (int i = 0; i < after.Length; i++)
            {
                if (noShadows[i].r - after[i].r > 0.01f) darkened++;
                difference[i] = new Color(Mathf.Abs(before[i].r - after[i].r), Mathf.Abs(before[i].g - after[i].g), Mathf.Abs(before[i].b - after[i].b)) * 20;
            }
            Assert.Greater(darkened, 10); SaveImage("cascade-difference-x20", difference);
            for (int i = 0; i < 10; i++)
            {
                m_Camera.transform.position += new Vector3(0.05f, 0, 0.1f); Capture();
                var matrices = Shader.GetGlobalMatrixArray("_MobileCSMShadowMatrices6");
                Assert.AreEqual(6, matrices.Length);
                foreach (var matrix in matrices) for (int element = 0; element < 16; element++)
                    Assert.IsFalse(float.IsNaN(matrix[element]) || float.IsInfinity(matrix[element]));
            }
        }
    }
}
