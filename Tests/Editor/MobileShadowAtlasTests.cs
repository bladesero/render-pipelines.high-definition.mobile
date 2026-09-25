using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public partial class MobileImageTests
    {
        [TestCase(4)] [TestCase(6)]
        public void NativeVulkanLocalShadowAtlases(int face)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan) Assert.Ignore("Native input attachments require Vulkan.");
            m_Asset.mobileSettings.renderingPath = MobileRenderingPath.Deferred;
            m_Asset.mobileSettings.deferredExecution = MobileDeferredExecution.NativeRenderPass;
            LocalShadowAtlasRendersAllPointFacesAndSpot(false, face);
            AssertNativeShadowPath();
        }

        [Test]
        public void NativeVulkanSixCascadeShadows()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan) Assert.Ignore("Native input attachments require Vulkan.");
            m_Asset.mobileSettings.renderingPath = MobileRenderingPath.Deferred;
            m_Asset.mobileSettings.deferredExecution = MobileDeferredExecution.NativeRenderPass;
            SixCascadeShadowVolumeControlsRenderingAndCameraIsolation(false);
            AssertNativeShadowPath();
        }

        static void AssertNativeShadowPath()
        {
            var backend = (MobileRenderPipelineBackend)typeof(HDRenderPipeline).GetField("m_MobileBackend",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(RenderPipelineManager.currentPipeline);
            Assert.IsTrue(backend.lastCameraUsedNativeDeferred, "The shadow test must execute native subpasses rather than a fallback.");
        }

        [Test]
        public void SixCascadeShadowsUpgradeExistingTwoElementGlobalsWithoutRestart()
        {
            // Simulate the old backend's process-lifetime shader properties.
            Shader.SetGlobalVectorArray("_MobileShadowSpheres", new Vector4[2]);
            Shader.SetGlobalMatrixArray("_MobileShadowMatrices", new Matrix4x4[2]);
            SixCascadeShadowVolumeControlsRenderingAndCameraIsolation(false);
            Assert.AreEqual(6, Shader.GetGlobalVectorArray("_MobileCSMShadowSpheres6").Length);
            Assert.AreEqual(6, Shader.GetGlobalMatrixArray("_MobileCSMShadowMatrices6").Length);
            UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
        }

        Renderer ShadowReceiver(Vector3 direction, out Renderer caster)
        {
            var rotation = Quaternion.LookRotation(direction, Mathf.Abs(direction.y) > 0.9f ? Vector3.forward : Vector3.up);
            m_Camera.transform.SetPositionAndRotation(-direction * 3, rotation);
            var receiver = Quad(Material(true, Color.white));
            receiver.transform.rotation = rotation;
            receiver.transform.localScale = Vector3.one * 2;
            var cube = Own(GameObject.CreatePrimitive(PrimitiveType.Cube));
            cube.layer = 30;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cube, m_Scene);
            cube.transform.position = -direction;
            cube.transform.localScale = Vector3.one * 0.5f;
            caster = cube.GetComponent<Renderer>();
            caster.sharedMaterial = receiver.sharedMaterial;
            caster.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            return receiver;
        }

        [TestCase(false, 0)] [TestCase(false, 1)] [TestCase(false, 2)]
        [TestCase(false, 3)] [TestCase(false, 4)] [TestCase(false, 5)]
        [TestCase(true, 0)] [TestCase(true, 1)] [TestCase(true, 2)]
        [TestCase(true, 3)] [TestCase(true, 4)] [TestCase(true, 5)]
        [TestCase(false, 6)] [TestCase(true, 6)]
        public void LocalShadowAtlasRendersAllPointFacesAndSpot(bool deferred, int face)
        {
            if (deferred) UseDeferred();
            m_Sun.enabled = false;
            var directions = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back, Vector3.forward };
            var direction = directions[face];
            var receiver = ShadowReceiver(direction, out var caster);
            var light = MakeObject("Shadowed local light").AddComponent<Light>();
            light.type = face == 6 ? LightType.Spot : LightType.Point;
            light.transform.SetPositionAndRotation(-direction * 2, Quaternion.LookRotation(direction, Mathf.Abs(direction.y) > 0.9f ? Vector3.forward : Vector3.up));
            light.range = 10; light.intensity = 4; light.spotAngle = 90; light.cullingMask = 1 << 30;
            light.shadows = LightShadows.Soft; light.shadowNormalBias = 0.02f; light.shadowBias = 0.03f;
            var volume = CameraProfile().Add<MobileShadows>();
            volume.localLightShadows.Override(false);
            var lit = Center(Capture()).grayscale;
            Assert.Greater(lit, 0.01f);
            volume.localLightShadows.Override(true);
            volume.localShadowAtlasSize.Override(4096);
            for (int kernel = 1; kernel <= 4; kernel++)
            {
                volume.shadowFilterSize.Override(kernel);
                var shadowed = Center(Capture()).grayscale;
                Assert.Less(shadowed, lit * 0.3f, $"Face {face}, kernel {kernel}: shadow atlas must darken receiver");
                Assert.AreEqual(4096, Shader.GetGlobalTexture("_MobileLocalShadowAtlas").width);
            }
            light.shadowStrength = 0;
            Assert.That(Center(Capture()).grayscale, Is.EqualTo(lit).Within(0.002f));
            light.shadowStrength = 1;
            receiver.sharedMaterial.SetFloat("_MobileReceiveShadows", 0);
            HDShaderUtils.ResetMaterialKeywords(receiver.sharedMaterial);
            Assert.That(Center(Capture()).grayscale, Is.EqualTo(lit).Within(0.002f));
            receiver.sharedMaterial.SetFloat("_MobileReceiveShadows", 1);
            HDShaderUtils.ResetMaterialKeywords(receiver.sharedMaterial);
            caster.enabled = false;
            Assert.That(Center(Capture()).grayscale, Is.EqualTo(lit).Within(0.002f));
        }

        [TestCase(false)] [TestCase(true)]
        public void MixedPointSpotShadowAtlasKeepsLightsIndependent(bool deferred)
        {
            if (deferred) UseDeferred();
            m_Sun.enabled = false;
            ShadowReceiver(Vector3.forward, out _);
            Light first = null;
            for (int i = 0; i < 4; i++)
            {
                var light = MakeObject("Mixed atlas light " + i).AddComponent<Light>();
                light.type = i == 3 ? LightType.Spot : LightType.Point;
                light.transform.position = new Vector3((i - 1.5f) * 0.03f, 0, -2);
                light.range = 10; light.intensity = 1; light.spotAngle = 90; light.cullingMask = 1 << 30;
                light.shadows = LightShadows.Soft; light.shadowNormalBias = 0.02f;
                if (i == 0) first = light;
            }
            var volume = CameraProfile().Add<MobileShadows>();
            volume.localLightShadows.Override(false);
            float lit = Center(Capture()).grayscale;
            Assert.Greater(lit, 0.01f);
            volume.localLightShadows.Override(true);
            Assert.Less(Center(Capture()).grayscale, lit * 0.3f);
            Assert.AreEqual(8, Shader.GetGlobalVector("_MobileLocalShadowParams").y);
            first.shadowStrength = 0;
            float oneUnshadowed = Center(Capture()).grayscale;
            Assert.Greater(oneUnshadowed, lit * 0.15f);
            Assert.Less(oneUnshadowed, lit * 0.4f);
            volume.localLightShadows.Override(false);
            Assert.That(Center(Capture()).grayscale, Is.EqualTo(lit).Within(0.002f));
        }

        [TestCase(false)] [TestCase(true)]
        public void SixCascadeShadowVolumeControlsRenderingAndCameraIsolation(bool deferred)
        {
            if (deferred) UseDeferred();
            var receiver = ShadowReceiver(Vector3.forward, out var caster);
            m_Sun.shadows = LightShadows.Soft;
            m_Sun.shadowBias = 0.03f;
            var volume = CameraProfile().Add<MobileShadows>();
            volume.mainLightShadows.Override(false);
            var lit = Center(Capture()).grayscale;
            Assert.Greater(lit, 0.01f);
            volume.mainLightShadows.Override(true);
            volume.shadowAtlasSize.Override(4096);
            volume.cascadeSplit.Override(0.1f);
            for (int count = 1; count <= 6; count++)
            {
                volume.cascadeCount.Override(count);
                Assert.Less(Center(Capture()).grayscale, lit * 0.3f, $"Cascade count {count}");
                Assert.AreEqual(count, Shader.GetGlobalInt("_MobileCascadeCount"));
                Assert.AreEqual(4096, Shader.GetGlobalTexture("_MobileShadowAtlas").width);
                var spheres = Shader.GetGlobalVectorArray("_MobileCSMShadowSpheres6");
                for (int i = 0; i < count; i++) Assert.Greater(spheres[i].w, 0, $"Missing cascade {i}");
            }
            // Exercise all six depth intervals, including the native group boundary.
            foreach (float depth in new[] { 2f, 6f, 11f, 16f, 21f, 26f })
            {
                receiver.transform.position = Vector3.forward * (depth - 3);
                caster.transform.position = Vector3.forward * (depth - 4);
                Assert.Less(Center(Capture()).grayscale, lit * 0.3f, $"Receiver depth {depth}");
            }
            var other = MakeCamera(Width, Height);
            m_Asset.mobileSettings.cascadeCount = 2;
            Capture(other);
            Assert.AreEqual(2, Shader.GetGlobalInt("_MobileCascadeCount"));
            Capture();
            Assert.AreEqual(6, Shader.GetGlobalInt("_MobileCascadeCount"));
            volume.active = false;
            Capture();
            Assert.AreEqual(2, Shader.GetGlobalInt("_MobileCascadeCount"));
        }
    }

    public class MobileShadowAtlasTests
    {
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)]
        public void NativeCascadeGroupsCoverDistanceWithoutGaps(int count)
        {
            float previousEnd = 0;
            for (int cascade = 0; cascade < count; cascade++)
            {
                MobileRenderPipelineBackend.GetNativeCascade(count, cascade, 0.1f, out int index, out int nativeCount, out var ratios);
                Assert.That(nativeCount, Is.InRange(1, 4));
                Assert.That(index, Is.InRange(0, nativeCount - 1));
                float start = index == 0 ? 0 : ratios[index - 1];
                float end = index == nativeCount - 1 ? 1 : ratios[index];
                Assert.That(start, Is.EqualTo(previousEnd).Within(1e-6f));
                Assert.Greater(end, start);
                previousEnd = end;
            }
            Assert.AreEqual(1, previousEnd);
        }

        [TestCase(-1, 256)] [TestCase(513, 1024)] [TestCase(4096, 4096)] [TestCase(int.MaxValue, 4096)]
        public void AtlasResolutionClampsBeforePowerOfTwo(int request, int expected)
            => Assert.AreEqual(expected, MobileRenderPipelineBackend.ShadowAtlasSize(request));
    }
}
