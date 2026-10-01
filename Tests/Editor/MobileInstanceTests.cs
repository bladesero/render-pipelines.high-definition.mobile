using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public class MobileInstanceTests
    {
        [Test]
        public void TransformedBoundsContainEveryCornerWithNonuniformMirroredScale()
        {
            var local = new Bounds(new Vector3(1, 2, 3), new Vector3(2, 4, 6));
            var matrix = Matrix4x4.TRS(new Vector3(5, -2, 4), Quaternion.Euler(23, 61, 14), new Vector3(-2, 3, 0.5f));
            var world = MobileInstanceCulling.TransformBounds(local, matrix, 0.01f);
            for (int i = 0; i < 8; ++i)
            {
                var corner = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Assert.IsTrue(world.Contains(matrix.MultiplyPoint3x4(corner)));
            }
        }
        [Test]
        public void DistanceUsesBoundsAndLodHysteresisPreventsOscillation()
        {
            Assert.IsTrue(MobileInstanceCulling.InRange(new Bounds(new Vector3(100, 0, 0), new Vector3(190, 1, 1)), Vector3.zero, 6));
            var thresholds = new[] { 0.2f, 0.05f };
            Assert.AreEqual(0, MobileInstanceCulling.SelectLOD(0.19f, thresholds, 0));
            Assert.AreEqual(1, MobileInstanceCulling.SelectLOD(0.17f, thresholds, 0));
            Assert.AreEqual(1, MobileInstanceCulling.SelectLOD(0.21f, thresholds, 1));
            Assert.AreEqual(0, MobileInstanceCulling.SelectLOD(0.23f, thresholds, 1));
            Assert.AreEqual(2, MobileInstanceCulling.SelectLOD(0.01f, thresholds, -1));
        }
        [Test]
        public void FrustumRejectsOutsideButKeepsIntersectingBounds()
        {
            var obj = new GameObject("Instance culling test");
            try
            {
                var camera = obj.AddComponent<Camera>(); camera.nearClipPlane = 0.1f; camera.farClipPlane = 10;
                var planes = GeometryUtility.CalculateFrustumPlanes(camera);
                Assert.AreEqual(1, MobileInstanceCulling.Classify(planes, new Bounds(new Vector3(0, 0, 3), Vector3.one * 0.1f)));
                Assert.AreEqual(-1, MobileInstanceCulling.Classify(planes, new Bounds(new Vector3(50, 0, 3), Vector3.one)));
                Assert.AreEqual(0, MobileInstanceCulling.Classify(planes, new Bounds(new Vector3(0, 0, 10), Vector3.one)));
            }
            finally { Object.DestroyImmediate(obj); }
        }
    }

    public partial class MobileImageTests
    {
        MobileInstanceGroup InstanceGroup(params Renderer[] renderers)
        {
            var group = MakeObject("Mobile instance test group").AddComponent<MobileInstanceGroup>();
            group.renderers = new MeshRenderer[renderers.Length];
            for (int i = 0; i < renderers.Length; ++i)
            { group.renderers[i] = (MeshRenderer)renderers[i]; foreach (var material in renderers[i].sharedMaterials) material.enableInstancing = true; }
            group.MarkDirty(); return group;
        }

        [TestCase(false, false, false)] [TestCase(true, false, false)]
        [TestCase(false, true, false)] [TestCase(true, true, false)]
        [TestCase(false, true, true)] [TestCase(true, true, true)]
        public void InstancedForwardAndDeferredMatchNativeAndRestoreOwnership(bool lit, bool deferred, bool extended)
        {
            m_Asset.mobileSettings.renderingPath = deferred ? MobileRenderingPath.Deferred : MobileRenderingPath.Forward;
            m_Asset.mobileSettings.extendedLitMaterials = extended;
            var material = Material(lit, new Color(0.4f, 0.2f, 0.1f));
            var left = Quad(material); left.transform.position = new Vector3(-0.5f, 0, 0);
            var right = Quad(material); right.transform.position = new Vector3(0.5f, 0, 0);
            InstanceGroup(left, right);
            var reference = Capture();
            m_Asset.mobileSettings.instanceMode = MobileInstanceMode.CpuInstanced;
            Same(reference, Capture());
            Assert.AreEqual(2, MobileInstanceDiagnostics.lastCamera.visibleInstances);
            Assert.AreEqual(1, MobileInstanceDiagnostics.lastCamera.draws, "Geometry must be submitted once, without an extra ForwardOnly draw after GBuffer.");
            Assert.AreEqual(ShadowCastingMode.On, left.shadowCastingMode);
            Assert.IsFalse(left.forceRenderingOff);
            m_Asset.mobileSettings.instanceMode = MobileInstanceMode.Disabled;
            Same(reference, Capture());
            Assert.AreEqual(0, MobileInstanceDiagnostics.lastCamera.draws);
        }

        [Test]
        public void InstanceDistanceCameraIsolationAndUnsupportedPropertyBlockFallback()
        {
            var material = Material(false, new Color(0.4f, 0.2f, 0.1f));
            var renderer = Quad(material); var group = InstanceGroup(renderer);
            var reference = Capture();
            m_Asset.mobileSettings.instanceMode = MobileInstanceMode.CpuInstanced;
            group.drawDistance = 1;
            Capture(); Assert.AreEqual(0, MobileInstanceDiagnostics.lastCamera.visibleInstances);
            group.drawDistance = 100;
            Same(reference, Capture()); Assert.AreEqual(1, MobileInstanceDiagnostics.lastCamera.visibleInstances);
            var other = MakeCamera(Width, Height); other.transform.position = new Vector3(50, 0, -3);
            Capture(other); Assert.AreEqual(0, MobileInstanceDiagnostics.lastCamera.visibleInstances);
            Same(reference, Capture()); Assert.AreEqual(1, MobileInstanceDiagnostics.lastCamera.visibleInstances);
            var block = new MaterialPropertyBlock(); block.SetFloat("_UnusedTestProperty", 1); renderer.SetPropertyBlock(block);
            Same(reference, Capture()); Assert.AreEqual(1, MobileInstanceDiagnostics.lastCamera.fallbackRenderers);
            Assert.AreEqual(0, MobileInstanceDiagnostics.lastCamera.draws);
            Assert.IsFalse(renderer.forceRenderingOff);
        }

        [Test]
        public void InstancedLocalLightAndBakedLightmapMatchNative()
        {
            m_Sun.enabled = false;
            var material = Material(true, new Color(0.5f, 0.3f, 0.2f));
            var renderer = Quad(material); InstanceGroup(renderer);
            var light = MakeObject("Instance point").AddComponent<Light>();
            light.type = LightType.Point; light.range = 10; light.intensity = 1; light.cullingMask = 1 << 30;
            light.transform.position = new Vector3(0, 0, -1);
            var reference = Capture();
            m_Asset.mobileSettings.instanceMode = MobileInstanceMode.CpuInstanced;
            Same(reference, Capture()); Assert.AreEqual(1, MobileInstanceDiagnostics.lastCamera.visibleInstances);
            var previous = LightmapSettings.lightmaps; var mode = LightmapSettings.lightmapsMode;
            try
            {
                LightmapSettings.lightmapsMode = LightmapsMode.NonDirectional;
                LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = Solid(new Color(0.2f, 0.3f, 0.1f, 1)) } };
                renderer.lightmapIndex = 0; renderer.lightmapScaleOffset = new Vector4(0.5f, 0.5f, 0.2f, 0.1f);
                m_Asset.mobileSettings.instanceMode = MobileInstanceMode.Disabled; reference = Capture();
                m_Asset.mobileSettings.instanceMode = MobileInstanceMode.CpuInstanced; Same(reference, Capture());
            }
            finally { renderer.lightmapIndex = -1; LightmapSettings.lightmaps = previous; LightmapSettings.lightmapsMode = mode; }
        }

        [Test]
        public void InstanceBatchesSplitAndMovingTransformsRebuildCells()
        {
            var material = Material(false, Color.white);
            var renderers = new Renderer[5];
            for (int i = 0; i < renderers.Length; ++i)
            { renderers[i] = Quad(material); renderers[i].transform.localScale = Vector3.one * 0.1f; renderers[i].transform.position = new Vector3(-0.4f + 0.2f * i, 0, 0); }
            InstanceGroup(renderers);
            m_Asset.mobileSettings.instanceMode = MobileInstanceMode.CpuInstanced;
            m_Asset.mobileSettings.instanceBatchSize = 2;
            Capture(); Assert.AreEqual(5, MobileInstanceDiagnostics.lastCamera.visibleInstances); Assert.AreEqual(3, MobileInstanceDiagnostics.lastCamera.draws);
            renderers[0].transform.position = new Vector3(100, 0, 0);
            Capture(); Assert.AreEqual(4, MobileInstanceDiagnostics.lastCamera.visibleInstances); Assert.AreEqual(2, MobileInstanceDiagnostics.lastCamera.draws);
            renderers[1].transform.localScale = new Vector3(-0.1f, 0.1f, 0.1f);
            Capture(); Assert.AreEqual(1, MobileInstanceDiagnostics.lastCamera.fallbackRenderers);
        }

        [Test]
        public void InstanceNativeCastersPreserveDirectionalAndLocalShadows()
        {
            m_Camera.orthographic = false; m_Camera.transform.position = new Vector3(0, 3, -3); m_Camera.transform.LookAt(new Vector3(0, 0, 8));
            m_Sun.transform.rotation = Quaternion.Euler(45, -30, 0); m_Sun.shadows = LightShadows.Hard;
            var material = Material(true, Color.gray);
            var ground = Quad(material); ground.transform.position = new Vector3(0, -1, 8);
            ground.transform.rotation = Quaternion.Euler(90, 0, 0); ground.transform.localScale = new Vector3(20, 30, 1);
            var renderers = new Renderer[5]; renderers[0] = ground;
            for (int i = 1; i < renderers.Length; ++i)
            {
                var cube = Own(GameObject.CreatePrimitive(PrimitiveType.Cube)); cube.layer = 30;
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cube, m_Scene);
                cube.transform.position = new Vector3(i % 2 == 0 ? -1 : 1, 0, 2 + i * 2);
                renderers[i] = cube.GetComponent<Renderer>(); renderers[i].sharedMaterial = material;
            }
            InstanceGroup(renderers);
            var local = MakeObject("Instance shadow point").AddComponent<Light>();
            local.type = LightType.Point; local.range = 20; local.intensity = 5; local.shadows = LightShadows.Hard;
            local.cullingMask = 1 << 30; local.transform.position = new Vector3(0, 3, 7);
            var reference = Capture();
            m_Asset.mobileSettings.instanceMode = MobileInstanceMode.CpuInstanced;
            Same(reference, Capture()); Assert.Greater(MobileInstanceDiagnostics.lastCamera.visibleInstances, 0);
            m_Asset.mobileSettings.mainLightShadows = false; m_Asset.mobileSettings.localLightShadows = false;
            var unshadowed = Capture(); int darker = 0;
            for (int i = 0; i < reference.Length; ++i) if (unshadowed[i].r - reference[i].r > 0.01f) ++darker;
            Assert.Greater(darker, 10, "Only managed objects must still cast actual shadows.");
            foreach (var renderer in renderers) Assert.AreEqual(ShadowCastingMode.On, renderer.shadowCastingMode);
        }

        [Test]
        public void InstanceOwnershipRestoresWhenPreparationScopeIsDisposed()
        {
            var renderer = Quad(Material(false, Color.white)); var group = InstanceGroup(renderer);
            m_Asset.mobileSettings.instanceMode = MobileInstanceMode.CpuInstanced;
            var instances = new MobileInstanceRenderer();
            try
            {
                instances.Begin(m_Camera, m_Asset.mobileSettings, 30);
                Assert.AreEqual(ShadowCastingMode.ShadowsOnly, renderer.shadowCastingMode);
                group.enabled = false;
            }
            finally { instances.Dispose(); }
            Assert.AreEqual(ShadowCastingMode.On, renderer.shadowCastingMode); Assert.IsFalse(renderer.forceRenderingOff);
        }

        [TestCase(false)] [TestCase(true)]
        public void InstanceNativeDeferredSubpassMatchesNativeRenderer(bool extended)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan) Assert.Ignore("Native subpass validation requires Vulkan.");
            m_Asset.mobileSettings.renderingPath = MobileRenderingPath.Deferred;
            m_Asset.mobileSettings.deferredExecution = MobileDeferredExecution.NativeRenderPass;
            m_Asset.mobileSettings.extendedLitMaterials = extended;
            var material = Material(true, new Color(0.4f, 0.2f, 0.1f));
            if (extended) { material.SetFloat("_MaterialID", 4); material.SetColor("_SpecularColor", Color.gray); HDShaderUtils.ResetMaterialKeywords(material); }
            var renderer = Quad(material); InstanceGroup(renderer);
            var reference = Capture();
            m_Asset.mobileSettings.instanceMode = MobileInstanceMode.CpuInstanced;
            Same(reference, Capture()); Assert.Greater(MobileInstanceDiagnostics.lastCamera.draws, 0);
        }

        [Test]
        public void InstanceCutoutNormalMapAndAmbientProbeMatchNative()
        {
            var material = Material(true, Color.gray);
            var alpha = Own(new Texture2D(2, 2, TextureFormat.RGBA32, false, true)); alpha.filterMode = FilterMode.Point;
            alpha.SetPixels(new[] { new Color(1, 1, 1, 0), Color.white, new Color(1, 1, 1, 0), Color.white }); alpha.Apply();
            material.SetTexture("_BaseColorMap", alpha); material.SetFloat("_AlphaCutoffEnable", 1); material.SetFloat("_AlphaCutoff", 0.5f);
            material.SetTexture("_NormalMap", Solid(new Color(0.6f, 0.5f, 1, 0.6f))); HDShaderUtils.ResetMaterialKeywords(material);
            var renderer = Quad(material); renderer.transform.localScale = new Vector3(1.5f, 0.7f, 1);
            InstanceGroup(renderer);
            var previous = RenderSettings.ambientProbe;
            try
            {
                var sh = new SphericalHarmonicsL2(); sh.AddAmbientLight(new Color(0.2f, 0.1f, 0.3f)); RenderSettings.ambientProbe = sh;
                var reference = Capture(); m_Asset.mobileSettings.instanceMode = MobileInstanceMode.CpuInstanced;
                var actual = Capture();
                SaveImage("instance-cutout-native", reference); SaveImage("instance-cutout-explicit", actual);
                int different = 0; int minX = Width, maxX = -1, minY = Height, maxY = -1;
                for (int i = 0; i < reference.Length; ++i)
                    if (Mathf.Abs(reference[i].r - actual[i].r) > 0.002f)
                    { ++different; minX = Mathf.Min(minX, i % Width); maxX = Mathf.Max(maxX, i % Width); minY = Mathf.Min(minY, i / Width); maxY = Mathf.Max(maxY, i / Width); }
                TestContext.WriteLine($"Cutout differences: {different}, bounds {minX},{minY}..{maxX},{maxY}");
                Same(reference, actual); Assert.AreEqual(1, MobileInstanceDiagnostics.lastCamera.visibleInstances);
            }
            finally { RenderSettings.ambientProbe = previous; }
        }

        [Test]
        public void InstanceLodDrawsReplacementMeshAndDisablingGroupRestoresSource()
        {
            var renderer = Quad(Material(false, Color.white));
            var filter = renderer.GetComponent<MeshFilter>(); var source = filter.sharedMesh;
            var lod = Own(Object.Instantiate(source)); var vertices = lod.vertices;
            for (int i = 0; i < vertices.Length; ++i) vertices[i] *= 0.5f;
            lod.vertices = vertices; lod.RecalculateBounds();
            filter.sharedMesh = lod; var expected = Capture(); filter.sharedMesh = source;
            var fullSize = Capture();
            var group = InstanceGroup(renderer); group.lodMeshes = new[] { lod }; group.lodScreenHeights = new[] { 2f }; group.MarkDirty();
            m_Asset.mobileSettings.instanceMode = MobileInstanceMode.CpuInstanced;
            Same(expected, Capture()); Assert.AreEqual(1, MobileInstanceDiagnostics.lastCamera.visibleInstances);
            group.enabled = false; Same(fullSize, Capture()); Assert.AreEqual(0, MobileInstanceDiagnostics.lastCamera.draws);
        }

        [Test]
        public void InstanceDuplicateMembershipFallsBackWithoutDoubleDrawing()
        {
            var renderer = Quad(Material(false, Color.white)); var reference = Capture();
            InstanceGroup(renderer); InstanceGroup(renderer);
            m_Asset.mobileSettings.instanceMode = MobileInstanceMode.CpuInstanced;
            Same(reference, Capture());
            Assert.AreEqual(0, MobileInstanceDiagnostics.lastCamera.draws);
            Assert.AreEqual(1, MobileInstanceDiagnostics.lastCamera.fallbackRenderers);
        }

        [Test]
        public void InstanceHDRPReflectionAtlasMatchesNative()
        {
            m_Sun.enabled = false;
            var material = Material(true, Color.gray); material.SetFloat("_Metallic", 1); material.SetFloat("_Smoothness", 0.7f);
            var renderer = Quad(material); InstanceGroup(renderer);
            var cube = Own(new Cubemap(16, TextureFormat.RGBAHalf, true)); var colors = new Color[256];
            for (int i = 0; i < colors.Length; ++i) colors[i] = new Color(0.7f, 0.2f, 0.1f, 1);
            for (int i = 0; i < 6; ++i) cube.SetPixels(colors, (CubemapFace)i);
            cube.Apply();
            var obj = MakeObject("Instance HDRP reflection probe"); obj.AddComponent<ReflectionProbe>().size = Vector3.one * 10;
            var hd = obj.AddComponent<HDAdditionalReflectionData>(); hd.mode = ProbeSettings.Mode.Baked;
            hd.SetTexture(ProbeSettings.Mode.Baked, cube);
            var reference = Capture();
            m_Asset.mobileSettings.instanceMode = MobileInstanceMode.CpuInstanced;
            Same(reference, Capture()); Assert.AreEqual(1, MobileInstanceDiagnostics.lastCamera.visibleInstances);
            Assert.Greater(Center(reference).maxColorComponent, 0.01f);
        }

        [Test]
        public void InstanceOffscreenCasterStillShadowsVisibleReceiver()
        {
            m_Sun.transform.rotation = Quaternion.Euler(0, 45, 0); m_Sun.shadows = LightShadows.Hard;
            m_Asset.mobileSettings.cascadeCount = 1; m_Asset.mobileSettings.shadowAtlasSize = 2048;
            var material = Material(true, Color.gray);
            var receiver = Quad(material); receiver.transform.localScale = new Vector3(2, 2, 1); receiver.shadowCastingMode = ShadowCastingMode.Off;
            var cube = Own(GameObject.CreatePrimitive(PrimitiveType.Cube)); cube.layer = 30;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cube, m_Scene);
            cube.transform.position = new Vector3(-2, 0, -2); cube.transform.localScale = Vector3.one * 0.5f;
            var caster = cube.GetComponent<Renderer>(); caster.sharedMaterial = material;
            InstanceGroup(receiver, caster);
            var reference = Capture();
            m_Asset.mobileSettings.instanceMode = MobileInstanceMode.CpuInstanced;
            Same(reference, Capture());
            Assert.AreEqual(1, MobileInstanceDiagnostics.lastCamera.visibleInstances, "The caster must be outside the main view.");
            m_Asset.mobileSettings.mainLightShadows = false;
            var unshadowed = Capture(); int darker = 0;
            for (int i = 0; i < reference.Length; ++i) if (unshadowed[i].r - reference[i].r > 0.01f) ++darker;
            Assert.Greater(darker, 10, "Offscreen managed caster must affect the visible receiver.");
            Assert.AreEqual(ShadowCastingMode.Off, receiver.shadowCastingMode);
            Assert.IsFalse(receiver.forceRenderingOff);
        }
    }
}
