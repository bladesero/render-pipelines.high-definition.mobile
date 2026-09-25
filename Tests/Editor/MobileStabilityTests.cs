using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering.RenderGraphModule;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public class MobileStabilityTests
    {
        readonly MobileRenderTests m_PipelineScope = new MobileRenderTests();
        [SetUp] public void Setup() => m_PipelineScope.SuspendProjectPipeline();
        [TearDown] public void Cleanup() => m_PipelineScope.RestoreProjectPipeline();

        static int MaterialCount() => Resources.FindObjectsOfTypeAll<Material>().Count(m => m.shader != null && m.shader.name.StartsWith("Hidden/HDRP/Mobile/"));
        static int StackComponentCount() => Resources.FindObjectsOfTypeAll<VolumeComponent>().Length;
        static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        [Test]
        public void LegacySerializedSettingsGetDefaultBlendAndZeroRoundTrips()
        {
            var settings = JsonUtility.FromJson<MobileRenderSettings>("{\"shadowDistance\":30}");
            Assert.AreEqual(0.1f, settings.cascadeBlend);
            settings.cascadeBlend = 0;
            Assert.AreEqual(0, JsonUtility.FromJson<MobileRenderSettings>(JsonUtility.ToJson(settings)).cascadeBlend);
        }

        [TestCase("Graph")]
        [TestCase("Materials")]
        [TestCase("VolumeStack")]
        [TestCase("DefaultVolume")]
        public void FailedReplacementConstructionReleasesPartialResources(string checkpoint)
        {
            var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            var owner = new MobileRenderPipelineBackend(asset);
            try
            {
                int materials = MaterialCount(), components = StackComponentCount(), graphs = RenderGraph.GetRegisteredRenderGraphs().Count;
                Assert.Throws<InvalidOperationException>(() => new MobileRenderPipelineBackend(asset, stage =>
                { if (stage == checkpoint) throw new InvalidOperationException("Injected construction failure"); }));
                Assert.AreEqual(materials, MaterialCount());
                Assert.AreEqual(components, StackComponentCount());
                Assert.AreEqual(graphs, RenderGraph.GetRegisteredRenderGraphs().Count);
                Assert.IsTrue(Shader.IsKeywordEnabled("HDRP_MOBILE_RENDERER"));
                Assert.AreEqual("HDRenderPipeline", Shader.globalRenderPipeline);
            }
            finally { owner.Dispose(); Object.DestroyImmediate(asset); }
        }

        [Test]
        public void FiftyReplacementsAndSceneUnloadsReleaseOwnedResources()
        {
            var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            // Warm VolumeManager's shared default state before measuring owned objects.
            new MobileRenderPipelineBackend(asset).Dispose();
            int materials = MaterialCount(), components = StackComponentCount(), graphs = RenderGraph.GetRegisteredRenderGraphs().Count;
            var features = SupportedRenderingFeatures.active;
            bool keyword = Shader.IsKeywordEnabled("HDRP_MOBILE_RENDERER");
            var cube = new Cubemap(8, TextureFormat.RGBAHalf, false);
            try
            {
                for (int iteration = 0; iteration < 50; iteration++)
                {
                    var scene = EditorSceneManager.NewPreviewScene();
                    MobileRenderPipelineBackend first = null, second = null;
                    try
                    {
                        var obj = new GameObject("Lifecycle probe");
                        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(obj, scene);
                        var native = obj.AddComponent<ReflectionProbe>();
                        var hd = obj.AddComponent<HDAdditionalReflectionData>();
                        hd.mode = ProbeSettings.Mode.Baked; hd.SetTexture(ProbeSettings.Mode.Baked, cube);
                        first = new MobileRenderPipelineBackend(asset); first.SynchronizeBakedReflectionProbes();
                        second = new MobileRenderPipelineBackend(asset); first.Dispose();
                        second.SynchronizeBakedReflectionProbes();
                        Assert.AreSame(cube, native.customBakedTexture);
                        EditorSceneManager.ClosePreviewScene(scene);
                        second.SynchronizeBakedReflectionProbes();
                        second.Dispose(); second.Dispose();
                        Assert.AreEqual(0, ((System.Collections.IDictionary)Field(second, "m_OriginalProbeTextures")).Count);
                    }
                    finally
                    {
                        first?.Dispose(); second?.Dispose();
                        if (scene.IsValid() && scene.isLoaded) EditorSceneManager.ClosePreviewScene(scene);
                    }
                    Assert.AreEqual(materials, MaterialCount(), "Owned materials, iteration " + iteration);
                    Assert.AreEqual(components, StackComponentCount(), "Volume stack, iteration " + iteration);
                    Assert.AreEqual(graphs, RenderGraph.GetRegisteredRenderGraphs().Count);
                }
                Assert.AreSame(features, SupportedRenderingFeatures.active);
                Assert.AreEqual(keyword, Shader.IsKeywordEnabled("HDRP_MOBILE_RENDERER"));
            }
            finally { Object.DestroyImmediate(cube); Object.DestroyImmediate(asset); }
        }

        [TestCase(false)] [TestCase(true)]
        public void RenderingCallbacksCloseWhenBeginCallbackThrows(bool cameraCallback)
        {
            var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>(); asset.rendererMode = HDRendererMode.Mobile;
            var pipeline = new HDRenderPipeline(asset);
            var obj = new GameObject("Callback camera"); var camera = obj.AddComponent<Camera>();
            int endedCamera = 0, endedContext = 0;
            void BeginCamera(ScriptableRenderContext context, Camera current) { if (cameraCallback) throw new InvalidOperationException("Injected begin camera failure"); }
            void BeginContext(ScriptableRenderContext context, System.Collections.Generic.List<Camera> cameras) { if (!cameraCallback) throw new InvalidOperationException("Injected begin context failure"); }
            void EndCamera(ScriptableRenderContext context, Camera current) { endedCamera++; }
            void EndContext(ScriptableRenderContext context, System.Collections.Generic.List<Camera> cameras) { endedContext++; }
            RenderPipelineManager.beginCameraRendering += BeginCamera; RenderPipelineManager.endCameraRendering += EndCamera;
            RenderPipelineManager.beginContextRendering += BeginContext; RenderPipelineManager.endContextRendering += EndContext;
            try
            {
                var render = typeof(HDRenderPipeline).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(ScriptableRenderContext), typeof(System.Collections.Generic.List<Camera>) }, null);
                var error = Assert.Throws<TargetInvocationException>(() => render.Invoke(pipeline,
                    new object[] { default(ScriptableRenderContext), new System.Collections.Generic.List<Camera> { camera } }));
                Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
                Assert.AreEqual(1, endedContext); Assert.AreEqual(cameraCallback ? 1 : 0, endedCamera);
            }
            finally
            {
                RenderPipelineManager.beginCameraRendering -= BeginCamera; RenderPipelineManager.endCameraRendering -= EndCamera;
                RenderPipelineManager.beginContextRendering -= BeginContext; RenderPipelineManager.endContextRendering -= EndContext;
                typeof(HDRenderPipeline).GetMethod("Dispose", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null).Invoke(pipeline, new object[] { true });
                Object.DestroyImmediate(obj); Object.DestroyImmediate(asset);
            }
        }
    }
}
