using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.Rendering.HighDefinition;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public class MobileRenderTests
    {
        RenderPipelineAsset m_PreviousGraphics, m_PreviousQuality;
        uint m_PreviousRenderingLayerMask;
        readonly Dictionary<string, byte[]> m_AssetSnapshots = new Dictionary<string, byte[]>();
        string m_ScenePath;

        [SetUp]
        public void SuspendProjectPipeline()
        {
            m_PreviousRenderingLayerMask = GraphicsSettings.defaultRenderingLayerMask;
            m_AssetSnapshots.Clear();
            foreach (var guid in AssetDatabase.FindAssets("t:VolumeProfile", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                m_AssetSnapshots[path] = System.IO.File.ReadAllBytes(path);
            }
            m_ScenePath = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
            m_PreviousGraphics = GraphicsSettings.renderPipelineAsset;
            m_PreviousQuality = QualitySettings.renderPipeline;
            GraphicsSettings.renderPipelineAsset = null;
            QualitySettings.renderPipeline = null;
            typeof(RenderPipelineManager).GetMethod("PrepareRenderPipeline", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { null });
            // An unsupported Desktop pipeline can dispose before saving its default
            // layer mask. Keep newly created test renderers visible independently
            // of the project's current pipeline and restore the original afterwards.
            GraphicsSettings.defaultRenderingLayerMask = ShaderVariablesGlobal.DefaultRenderingLayerMask;
        }

        [TearDown]
        public void RestoreProjectPipeline()
        {
            GraphicsSettings.renderPipelineAsset = m_PreviousGraphics;
            QualitySettings.renderPipeline = m_PreviousQuality;
            GraphicsSettings.defaultRenderingLayerMask = m_PreviousRenderingLayerMask;
            foreach (var snapshot in m_AssetSnapshots)
                CollectionAssert.AreEqual(snapshot.Value, System.IO.File.ReadAllBytes(snapshot.Key), "Test changed project Profile: " + snapshot.Key);
            Assert.AreEqual(m_ScenePath, UnityEngine.SceneManagement.SceneManager.GetActiveScene().path, "Test did not restore the active scene.");
        }

        [Test]
        public void BakedHDRPProbeBindsNativeCubeAndRestoresItOnDispose()
        {
            var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            var obj = new GameObject("Baked probe test");
            var native = obj.AddComponent<ReflectionProbe>();
            var hd = obj.AddComponent<HDAdditionalReflectionData>();
            hd.mode = ProbeSettings.Mode.Baked;
            var cube = new Cubemap(16, TextureFormat.RGBAHalf, false);
            hd.SetTexture(ProbeSettings.Mode.Baked, cube);
            var original = native.customBakedTexture;
            bool originalBox = native.boxProjection;
            float originalBlend = native.blendDistance;
            hd.useInfluenceVolumeAsProxyVolume = true;
            hd.influenceVolume.boxBlendDistancePositive = hd.influenceVolume.boxBlendDistanceNegative = Vector3.one * 0.4f;
            var backend = new MobileRenderPipelineBackend(asset);
            try
            {
                backend.SynchronizeBakedReflectionProbes();
                Assert.AreSame(cube, native.customBakedTexture);
                Assert.IsTrue(native.boxProjection);
                Assert.AreEqual(0.4f, native.blendDistance);
                hd.enabled = false;
                backend.SynchronizeBakedReflectionProbes();
                Assert.AreSame(original, native.customBakedTexture);
                Assert.AreEqual(originalBox, native.boxProjection);
                Assert.AreEqual(originalBlend, native.blendDistance);
                hd.enabled = true;
                backend.SynchronizeBakedReflectionProbes();
                Assert.AreSame(cube, native.customBakedTexture);
                backend.Dispose();
                Assert.AreSame(original, native.customBakedTexture);
            }
            finally { backend.Dispose(); Object.DestroyImmediate(obj); Object.DestroyImmediate(cube); Object.DestroyImmediate(asset); }
        }

        [Test]
        public void ExplicitRenderTexturePreservesDimensionsAtFixed1080p()
        {
            var obj = new GameObject("Render size test");
            var camera = obj.AddComponent<Camera>();
            var target = new RenderTexture(320, 180, 0);
            camera.targetTexture = target;
            try
            {
                var settings = new MobileRenderSettings();
                Assert.AreEqual(new Vector2Int(320, 180), settings.GetRenderSize(camera));
                settings.renderScale = 0.5f;
                Assert.AreEqual(new Vector2Int(160, 90), settings.GetRenderSize(camera));
            }
            finally { camera.targetTexture = null; Object.DestroyImmediate(target); Object.DestroyImmediate(obj); }
        }

        [Test]
        public void MobilePipelineDoesNotConstructDesktopManagers()
        {
            var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            asset.rendererMode = HDRendererMode.Mobile;
            var pipeline = new HDRenderPipeline(asset);
            try
            {
                foreach (var name in new[] { "m_SkyManager", "m_RenderGraph", "m_TextureCaches", "m_LightLoopLightData", "m_ShadowManager" })
                    Assert.IsNull(typeof(HDRenderPipeline).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pipeline), name);
                Assert.IsNotNull(typeof(HDRenderPipeline).GetField("m_MobileBackend", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pipeline));
            }
            finally
            {
                typeof(HDRenderPipeline).GetMethod("Dispose", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null)
                    .Invoke(pipeline, new object[] { true });
                Object.DestroyImmediate(asset);
            }
        }

        [TestCase(0u)]
        [TestCase(42u)]
        public void MobilePipelineInitializesNewRendererLayerAndRestoresDefault(uint originalMask)
        {
            var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            asset.rendererMode = HDRendererMode.Mobile;
            GraphicsSettings.defaultRenderingLayerMask = originalMask;
            HDRenderPipeline pipeline = null;
            GameObject primitive = null;
            try
            {
                pipeline = new HDRenderPipeline(asset);
                primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Assert.AreEqual((uint)ShaderVariablesGlobal.DefaultRenderingLayerMask, primitive.GetComponent<Renderer>().renderingLayerMask);
            }
            finally
            {
                if (pipeline != null)
                    typeof(HDRenderPipeline).GetMethod("Dispose", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null)
                        .Invoke(pipeline, new object[] { true });
                Object.DestroyImmediate(primitive);
                Object.DestroyImmediate(asset);
            }
            Assert.AreEqual(originalMask, GraphicsSettings.defaultRenderingLayerMask);
        }

        [Test]
        public void DisposingPreviousMobileBackendDoesNotResetReplacement()
        {
            var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            string tag = Shader.globalRenderPipeline;
            bool keyword = Shader.IsKeywordEnabled("HDRP_MOBILE_RENDERER");
            MobileRenderPipelineBackend first = null, second = null;
            GraphicsSettings.defaultRenderingLayerMask = 42u;
            try
            {
                first = new MobileRenderPipelineBackend(asset);
                second = new MobileRenderPipelineBackend(asset);
                first.Dispose();
                Assert.AreEqual((uint)ShaderVariablesGlobal.DefaultRenderingLayerMask, GraphicsSettings.defaultRenderingLayerMask);
                Assert.IsTrue(Shader.IsKeywordEnabled("HDRP_MOBILE_RENDERER"));
                Assert.AreEqual("HDRenderPipeline", Shader.globalRenderPipeline);
                second.Dispose();
                Assert.AreEqual(42u, GraphicsSettings.defaultRenderingLayerMask);
                second.Dispose();
                Assert.AreEqual(42u, GraphicsSettings.defaultRenderingLayerMask);
                Assert.AreEqual(keyword, Shader.IsKeywordEnabled("HDRP_MOBILE_RENDERER"));
                Assert.AreEqual(tag, Shader.globalRenderPipeline);
            }
            finally { first?.Dispose(); second?.Dispose(); Object.DestroyImmediate(asset); }
        }

        [Test]
        public void BenchmarkPercentilesUseNearestRankAndReportMissingTimings()
        {
            Assert.AreEqual(-1, MobileBenchmark.Percentile(new List<float>(), 0.95f));
            var values = new List<float> { 5, 1, 4, 3, 2 };
            Assert.AreEqual(3, MobileBenchmark.Percentile(values, 0.5f));
            Assert.AreEqual(5, MobileBenchmark.Percentile(values, 0.95f));
        }

        [Test]
        public void LegacyAssetDefaultsToDesktop()
        {
            var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            try
            {
                EditorJsonUtility.FromJsonOverwrite("{}", asset);
                Assert.AreEqual(HDRendererMode.Desktop, asset.rendererMode);
                Assert.AreEqual(1f, asset.mobileSettings.renderScale);
            }
            finally { Object.DestroyImmediate(asset); }
        }

        [TestCase(GraphicsDeviceType.Vulkan, RuntimePlatform.Android, false, true)]
        [TestCase(GraphicsDeviceType.Direct3D11, RuntimePlatform.WindowsEditor, true, true)]
        [TestCase(GraphicsDeviceType.Direct3D12, RuntimePlatform.WindowsEditor, true, true)]
        [TestCase(GraphicsDeviceType.Direct3D11, RuntimePlatform.WindowsPlayer, false, true)]
        [TestCase(GraphicsDeviceType.Direct3D12, RuntimePlatform.WindowsPlayer, false, true)]
        [TestCase(GraphicsDeviceType.Direct3D11, RuntimePlatform.Android, false, false)]
        [TestCase(GraphicsDeviceType.OpenGLES3, RuntimePlatform.Android, false, false)]
        [TestCase(GraphicsDeviceType.Metal, RuntimePlatform.OSXPlayer, false, false)]
        public void PlatformGateDoesNotEnableOtherPlayers(GraphicsDeviceType api, RuntimePlatform platform, bool editor, bool supported)
            => Assert.AreEqual(supported, MobileRenderCapabilities.IsDeviceSupported(api, platform, editor));

        [Test]
        public void AuditReportsUnsupportedMaterialsWithoutChangingThem()
        {
            var material = new Material(Shader.Find("HDRP/Lit"));
            try
            {
                material.SetFloat("_DisplacementMode", 2);
                var issues = new List<string>();
                MobileRenderEditor.AuditMaterial(material, "Test/Object", issues);
                Assert.That(issues, Has.Some.Contains("displacement"));
                Assert.AreEqual(2, material.GetFloat("_DisplacementMode"));
            }
            finally { Object.DestroyImmediate(material); }
        }

        [Test]
        public void BackendDisposeIsIdempotentAndRestoresGlobalState()
        {
            var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            bool batcher = GraphicsSettings.useScriptableRenderPipelineBatching;
            bool keyword = Shader.IsKeywordEnabled("HDRP_MOBILE_RENDERER");
            var features = SupportedRenderingFeatures.active;
            MobileRenderPipelineBackend backend = null;
            try
            {
                backend = new MobileRenderPipelineBackend(asset);
                backend.Dispose();
                backend.Dispose();
                Assert.AreEqual(batcher, GraphicsSettings.useScriptableRenderPipelineBatching);
                Assert.AreEqual(keyword, Shader.IsKeywordEnabled("HDRP_MOBILE_RENDERER"));
                Assert.AreSame(features, SupportedRenderingFeatures.active);
            }
            finally { backend?.Dispose(); Object.DestroyImmediate(asset); }
        }
    }
}
