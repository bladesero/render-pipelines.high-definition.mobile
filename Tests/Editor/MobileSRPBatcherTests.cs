using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public class MobileSRPBatcherTests
    {
        readonly MobileRenderTests m_Scope = new MobileRenderTests();
        [SetUp] public void Setup() => m_Scope.SuspendProjectPipeline();
        [TearDown] public void Teardown() => m_Scope.RestoreProjectPipeline();

        [TestCase(false)] [TestCase(true)]
        public void AssetSettingAppliesLiveAndRestoresGlobalOwnership(bool initial)
        {
            bool previous = GraphicsSettings.useScriptableRenderPipelineBatching;
            var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            MobileRenderPipelineBackend first = null, replacement = null;
            try
            {
                asset.enableSRPBatcher = initial;
                first = new MobileRenderPipelineBackend(asset);
                Assert.AreEqual(initial, GraphicsSettings.useScriptableRenderPipelineBatching);
                asset.enableSRPBatcher = !initial; first.ApplyBatchingSettings();
                Assert.AreEqual(!initial, GraphicsSettings.useScriptableRenderPipelineBatching);
                replacement = new MobileRenderPipelineBackend(asset);
                first.Dispose(); first.ApplyBatchingSettings();
                Assert.AreEqual(!initial, GraphicsSettings.useScriptableRenderPipelineBatching, "Old owner must not reset the replacement.");
                replacement.Dispose();
                Assert.AreEqual(previous, GraphicsSettings.useScriptableRenderPipelineBatching);
                replacement.ApplyBatchingSettings();
                Assert.AreEqual(previous, GraphicsSettings.useScriptableRenderPipelineBatching);
            }
            finally { first?.Dispose(); replacement?.Dispose(); Object.DestroyImmediate(asset); GraphicsSettings.useScriptableRenderPipelineBatching = previous; }
        }

        [TestCase("HDRP/Lit", true)] [TestCase("HDRP/Unlit", true)]
        [TestCase("HDRP/Lit", false)] [TestCase("HDRP/Unlit", false)]
        public void MobileMaterialPassesAreSRPBatcherCompatible(string shaderName, bool mobilePasses)
        {
            var shader = Shader.Find(shaderName); Assert.IsNotNull(shader);
            var material = new Material(shader);
            bool mobile = Shader.IsKeywordEnabled("HDRP_MOBILE_RENDERER"), async = ShaderUtil.allowAsyncCompilation;
            try
            {
                if (mobilePasses) Shader.EnableKeyword("HDRP_MOBILE_RENDERER"); else Shader.DisableKeyword("HDRP_MOBILE_RENDERER");
                ShaderUtil.allowAsyncCompilation = false;
                var passes = mobilePasses
                    ? new[] { "MobileForward", "MobileForwardOnly", "MobileForwardOnlyExtended", "MobileGBuffer", "MobileGBufferExtended", "ShadowCaster" }
                    : new[] { "Forward", "ForwardOnly", "GBuffer", "DepthOnly", "MotionVectors", "ShadowCaster" };
                foreach (string passName in passes)
                {
                    int pass = material.FindPass(passName); if (pass >= 0) ShaderUtil.CompilePass(material, pass, true);
                }
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                var getCode = typeof(ShaderUtil).GetMethod("GetSRPBatcherCompatibilityCode", flags);
                var getReason = typeof(ShaderUtil).GetMethod("GetSRPBatcherCompatibilityIssueReason", flags);
                Assert.IsNotNull(getCode); Assert.IsNotNull(getReason);
                int code = (int)getCode.Invoke(null, new object[] { shader, 0 });
                string reason = code == 0 ? "Compatible" : (string)getReason.Invoke(null, new object[] { shader, 0, code });
                TestContext.WriteLine(shaderName + ": " + code + ", " + reason);
                Assert.AreEqual(0, code, shaderName + ": " + reason);
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = async;
                if (mobile) Shader.EnableKeyword("HDRP_MOBILE_RENDERER"); else Shader.DisableKeyword("HDRP_MOBILE_RENDERER");
                Object.DestroyImmediate(material);
            }
        }
    }

    public partial class MobileImageTests
    {
        [TestCase(false, false, false)] [TestCase(true, false, false)]
        [TestCase(false, true, false)] [TestCase(true, true, false)]
        [TestCase(false, false, true)] [TestCase(true, false, true)]
        [TestCase(false, true, true)] [TestCase(true, true, true)]
        public void StaticBatchedCastersRetainMaterialCoverageWhileCameraMoves(bool batcher, bool unlitCaster, bool multiSubmesh)
        {
            m_Asset.enableSRPBatcher = batcher;
            m_Asset.mobileSettings.cascadeCount = 1;
            m_Asset.mobileSettings.shadowAtlasSize = 1024;
            m_Asset.mobileSettings.shadowDistance = 10;
            m_Asset.mobileSettings.shadowFilterSize = 1;
            m_Sun.shadows = LightShadows.Hard;
            m_Sun.transform.rotation = Quaternion.identity;

            var root = MakeObject("Static shadow batch");
            var receiver = Quad(Material(true, Color.white));
            receiver.transform.localScale = new Vector3(2.5f, 1.8f, 1);
            receiver.shadowCastingMode = ShadowCastingMode.Off;
            receiver.transform.SetParent(root.transform, true);

            var alpha = Own(new Texture2D(8, 8, TextureFormat.RGBA32, false, true));
            alpha.filterMode = FilterMode.Point;
            var pixels = new Color[64];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(1, 1, 1, (i % 8) < 4 ? 0 : 1);
            alpha.SetPixels(pixels); alpha.Apply();
            var cutout = Material(!unlitCaster, Color.white);
            cutout.SetTexture(unlitCaster ? "_UnlitColorMap" : "_BaseColorMap", alpha);
            cutout.SetFloat("_AlphaCutoffEnable", 1);
            cutout.SetFloat("_AlphaCutoff", 0.5f);
            HDShaderUtils.ResetMaterialKeywords(cutout);
            var leaf = Quad(cutout, -0.5f);
            leaf.transform.position = new Vector3(-0.5f, 0, -0.5f);
            leaf.transform.localScale = new Vector3(0.8f, 0.8f, 1);
            leaf.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            leaf.transform.SetParent(root.transform, true);

            var solid = Quad(Material(!unlitCaster, Color.white), -0.5f);
            solid.transform.position = new Vector3(0.5f, 0, -0.5f);
            solid.transform.localScale = new Vector3(0.8f, 0.8f, 1);
            solid.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            solid.transform.SetParent(root.transform, true);

            if (multiSubmesh)
            {
                // Tree assets commonly share one renderer between opaque bark and cutout leaves.
                // Retain separate material slots while combining their geometry.
                var mesh = Own(new Mesh());
                mesh.CombineMeshes(new[]
                {
                    new CombineInstance { mesh = leaf.GetComponent<MeshFilter>().sharedMesh, transform = Matrix4x4.identity },
                    new CombineInstance { mesh = solid.GetComponent<MeshFilter>().sharedMesh,
                        transform = leaf.transform.worldToLocalMatrix * solid.transform.localToWorldMatrix }
                }, false, true);
                leaf.GetComponent<MeshFilter>().sharedMesh = mesh;
                leaf.sharedMaterials = new[] { cutout, solid.sharedMaterial };
                solid.enabled = false;
            }

            var initial = Capture();
            cutout.SetFloat("_AlphaCutoffEnable", 0); HDShaderUtils.ResetMaterialKeywords(cutout);
            var opaque = Capture();
            int clippedPixels = 0;
            for (int i = 0; i < initial.Length; i++) if (initial[i].r - opaque[i].r > 0.01f) clippedPixels++;
            Assert.Greater(clippedPixels, 50, "Alpha clipping must change the cast shadow, not just the caster's visible surface.");
            cutout.SetFloat("_AlphaCutoffEnable", 1); HDShaderUtils.ResetMaterialKeywords(cutout);

            var positions = new[] { new Vector3(0, 0, -3), new Vector3(0.08f, 0.03f, -3), new Vector3(-0.07f, -0.02f, -3) };
            var rotations = new[] { Quaternion.identity, Quaternion.Euler(1, 2, 0), Quaternion.Euler(-1, -2, 0) };
            var reference = new Color[positions.Length][];
            for (int i = 0; i < positions.Length; i++)
            {
                m_Camera.transform.SetPositionAndRotation(positions[i], rotations[i]);
                reference[i] = Capture();
            }
            StaticBatchingUtility.Combine(multiSubmesh
                ? new[] { receiver.gameObject, leaf.gameObject }
                : new[] { receiver.gameObject, leaf.gameObject, solid.gameObject }, root);
            Assert.IsTrue(leaf.isPartOfStaticBatch, "The test must exercise actual combined geometry.");
            if (!multiSubmesh) Assert.IsTrue(solid.isPartOfStaticBatch);
            for (int i = 0; i < positions.Length; i++)
            {
                m_Camera.transform.SetPositionAndRotation(positions[i], rotations[i]);
                var actual = Capture();
                SaveImage("static-shadow-" + batcher + "-" + unlitCaster + "-" + multiSubmesh + "-" + i + "-reference", reference[i]);
                SaveImage("static-shadow-" + batcher + "-" + unlitCaster + "-" + multiSubmesh + "-" + i + "-batched", actual);
                Same(reference[i], actual);
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void ShadowBatcherRetainsDistinctMaterialsWithMatchingKeywords(bool staticBatch)
        {
            m_Asset.mobileSettings.cascadeCount = 1;
            m_Asset.mobileSettings.shadowAtlasSize = 1024;
            m_Asset.mobileSettings.shadowDistance = 10;
            m_Asset.mobileSettings.shadowFilterSize = 1;
            m_Sun.shadows = LightShadows.Hard;
            m_Sun.transform.rotation = Quaternion.identity;
            var root = MakeObject("Matching shadow keywords");
            var receiver = Quad(Material(true, Color.white));
            receiver.transform.localScale = new Vector3(2.5f, 1.8f, 1);
            receiver.shadowCastingMode = ShadowCastingMode.Off;
            var casters = new GameObject[2];
            for (int n = 0; n < 2; n++)
            {
                var texture = Own(new Texture2D(8, 8, TextureFormat.RGBA32, false, true));
                texture.filterMode = FilterMode.Point;
                var pixels = new Color[64];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color(1, 1, 1, n == 0 ? ((i % 8) < 4 ? 0.2f : 0.9f) : ((i / 8) < 4 ? 0.6f : 0.9f));
                texture.SetPixels(pixels); texture.Apply();
                var material = Material(true, Color.white);
                material.SetTexture("_BaseColorMap", texture);
                material.SetFloat("_AlphaCutoffEnable", 1);
                material.SetFloat("_UseShadowThreshold", 1);
                material.SetFloat("_AlphaCutoffShadow", n == 0 ? 0.4f : 0.8f);
                HDShaderUtils.ResetMaterialKeywords(material);
                var caster = Quad(material, -0.5f);
                caster.transform.position = new Vector3(n == 0 ? -0.5f : 0.5f, 0, -0.5f);
                caster.transform.localScale = new Vector3(0.8f, 0.8f, 1);
                caster.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                caster.transform.SetParent(root.transform, true);
                casters[n] = caster.gameObject;
            }
            if (staticBatch)
            {
                StaticBatchingUtility.Combine(casters, root);
                foreach (var caster in casters) Assert.IsTrue(caster.GetComponent<Renderer>().isPartOfStaticBatch);
            }
            for (int pose = 0; pose < 5; pose++)
            {
                m_Camera.transform.SetPositionAndRotation(new Vector3((pose - 2) * 0.04f, 0, -3), Quaternion.Euler(0, (pose - 2) * 3, 0));
                m_Asset.enableSRPBatcher = false;
                var reference = Capture();
                m_Asset.enableSRPBatcher = true;
                Same(reference, Capture());
                Same(reference, Capture());
                // Remove one caster from native culling, then restore it to exercise state changes.
                casters[0].GetComponent<Renderer>().enabled = false;
                var singleBatched = Capture();
                int removedShadowPixels = 0;
                for (int i = 0; i < reference.Length; i++)
                    if (singleBatched[i].r - reference[i].r > 0.01f) removedShadowPixels++;
                Assert.Greater(removedShadowPixels, 50, "The test must observe actual caster coverage.");
                m_Asset.enableSRPBatcher = false;
                Same(singleBatched, Capture());
                casters[0].GetComponent<Renderer>().enabled = true;
                Same(reference, Capture());
            }
        }

        [Test]
        public void SRPBatcherMaterialKeywordChanges()
        {
            var material = Material(true, Color.gray); Quad(material);
            var baseline = Capture();
            material.SetTexture("_DetailMap", Solid(new Color(0.5f, 0.5f, 0.5f, 0.5f)));
            material.SetVector("_UVDetailsMappingMask", new Vector4(1, 0, 0, 0));
            material.SetFloat("_DetailAlbedoScale", 1); material.SetFloat("_DetailSmoothnessScale", 1); material.SetFloat("_DetailNormalScale", 1);
            HDShaderUtils.ResetMaterialKeywords(material);
            var first = Capture(); var second = Capture();
            m_Asset.enableSRPBatcher = false; var legacy = Capture();
            TestContext.WriteLine($"Detail baseline={Center(baseline)}, first={Center(first)}, second={Center(second)}, legacy={Center(legacy)}");
            Same(baseline, first); Same(first, second); Same(first, legacy);
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator SRPBatcherNativeRenderLoopIsExecuted()
        {
            for (int i = 0; i < 8; ++i)
            {
                var renderer = Quad(Material(false, new Color(0.1f + i * 0.08f, 0.3f, 0.2f)));
                renderer.transform.localScale = Vector3.one * 0.2f;
                renderer.transform.position = new Vector3(-0.7f + i * 0.2f, 0, 0);
            }
            m_Asset.enableSRPBatcher = true;
            Capture(); yield return null; // Register the native profiler marker before recording it.
            using (var recorder = new Unity.Profiling.ProfilerRecorder("RenderLoop.DrawSRPBatcher", 64))
            {
                if (!recorder.Valid)
                {
                    var handles = new System.Collections.Generic.List<Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle>();
                    Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetAvailable(handles);
                    foreach (var handle in handles)
                    {
                        var description = Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetDescription(handle);
                        if (description.Name.Contains("SRP") || description.Name.Contains("Batch"))
                            TestContext.WriteLine(description.Category.Name + ": " + description.Name);
                    }
                }
                Assert.IsTrue(recorder.Valid, "Native SRP Batcher profiler marker must be available in this Editor.");
                recorder.Start();
                for (int i = 0; i < 3; ++i) { Capture(); yield return null; }
                recorder.Stop(); long enabledCalls = 0;
                for (int i = 0; i < recorder.Count; ++i) enabledCalls += recorder.GetSample(i).Count;
                TestContext.WriteLine("RenderLoop.DrawSRPBatcher enabled sample count: " + enabledCalls);
                Assert.Greater(enabledCalls, 0, "Compatibility and a global flag alone do not prove native SRP submission.");
                m_Asset.enableSRPBatcher = false; Capture(); yield return null;
                recorder.Reset(); recorder.Start();
                for (int i = 0; i < 3; ++i) { Capture(); yield return null; }
                recorder.Stop(); long disabledCalls = 0;
                for (int i = 0; i < recorder.Count; ++i) disabledCalls += recorder.GetSample(i).Count;
                TestContext.WriteLine("RenderLoop.DrawSRPBatcher disabled sample count: " + disabledCalls);
                Assert.AreEqual(0, disabledCalls);
            }
        }

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
        public void SRPBatcherTogglePreservesNativeAndInstancedRendering(bool lit, bool deferred)
        {
            m_Asset.mobileSettings.renderingPath = deferred ? MobileRenderingPath.Deferred : MobileRenderingPath.Forward;
            var material = Material(lit, new Color(0.3f, 0.5f, 0.1f));
            var left = Quad(material); left.transform.position = new Vector3(-0.5f, 0, 0);
            var otherMaterial = Material(lit, new Color(0.5f, 0.1f, 0.3f));
            var right = Quad(otherMaterial); right.transform.position = new Vector3(0.5f, 0, 0);
            m_Asset.enableSRPBatcher = false; var reference = Capture();
            Assert.IsFalse(GraphicsSettings.useScriptableRenderPipelineBatching);
            m_Asset.enableSRPBatcher = true; Same(reference, Capture());
            Assert.IsTrue(GraphicsSettings.useScriptableRenderPipelineBatching);

            // Mixed submission in the same pass: left uses explicit instancing,
            // right keeps native renderer batching and a distinct material.
            InstanceGroup(left); m_Asset.mobileSettings.instanceMode = MobileInstanceMode.CpuInstanced;
            Same(reference, Capture());
            Assert.Greater(MobileInstanceDiagnostics.lastCamera.draws, 0);
            Assert.IsTrue(GraphicsSettings.useScriptableRenderPipelineBatching);
            m_Asset.enableSRPBatcher = false; Same(reference, Capture());
            Assert.IsFalse(GraphicsSettings.useScriptableRenderPipelineBatching);
            Assert.Greater(MobileInstanceDiagnostics.lastCamera.draws, 0, "Explicit instancing must remain available with SRP Batcher off.");
        }

        [Test]
        public void SRPBatcherPropertyBlockFallbackRetainsPerRendererValues()
        {
            var material = Material(false, Color.white);
            var renderer = Quad(material);
            var block = new MaterialPropertyBlock(); block.SetColor("_UnlitColor", new Color(0.2f, 0.5f, 0.1f).gamma); renderer.SetPropertyBlock(block);
            InstanceGroup(renderer); m_Asset.mobileSettings.instanceMode = MobileInstanceMode.CpuInstanced;
            m_Asset.enableSRPBatcher = false; var reference = Capture();
            m_Asset.enableSRPBatcher = true; Same(reference, Capture());
            Assert.IsTrue(GraphicsSettings.useScriptableRenderPipelineBatching);
            Assert.AreEqual(0, MobileInstanceDiagnostics.lastCamera.draws);
            Assert.IsTrue(renderer.HasPropertyBlock());
        }
    }
}
