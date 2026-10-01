using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public partial class MobileImageTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void MobileReflectionBakeDoesNotInjectRuntimeSkyIntoDarkSurfaces(bool lightmapped)
        {
            m_Sun.enabled = false;
            m_Asset.mobileSettings.environmentLighting = true;
            m_Asset.mobileSettings.environmentDiffuseSource = MobileEnvironmentDiffuseSource.RuntimeSky;
            var profile = CameraProfile();
            profile.Add<VisualEnvironment>().skyType.Override((int)SkyType.Gradient);
            var sky = profile.Add<GradientSky>();
            sky.top.Override(Color.white * 4096); sky.middle.Override(Color.white * 4096); sky.bottom.Override(Color.white * 4096);
            var probe = AtlasProbe(null, ProbeSettings.Mode.Baked);
            probe.settingsRaw.cameraSettings.culling.cullingMask = 1 << 30;
            probe.settingsRaw.cameraSettings.volumes.layerMask = 1 << 29;
            var material = Material(true, Color.white);
            material.SetFloat("_Smoothness", .9f);
            var card = Quad(material, 2);
            GameObjectUtility.SetStaticEditorFlags(card.gameObject, StaticEditorFlags.ReflectionProbeStatic);
            var previous = LightmapSettings.lightmaps;
            var previousMode = LightmapSettings.lightmapsMode;
            try
            {
                LightmapSettings.lightmapsMode = LightmapsMode.NonDirectional;
                LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = Solid(Color.black) } };
                card.lightmapIndex = lightmapped ? 0 : -1;
                card.lightmapScaleOffset = new Vector4(1, 1, 0, 0);
                var capture = Own(HDRenderUtilities.CreateReflectionProbeRenderTarget(32, UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat));
                MobileReflectionBaking.Render(probe, capture, out _, out _);
                Assert.Less(BakedFacePixel(capture, (int)CubemapFace.PositiveZ).maxColorComponent, .01f,
                    "Probe baking must not add unoccluded runtime sky diffuse or sky specular to a dark surface.");
                if (lightmapped)
                {
                    material.SetFloat("_Metallic", 1);
                    LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = Solid(Color.red * 4) } };
                    MobileReflectionBaking.Render(probe, capture, out _, out _);
                    var pixel = BakedFacePixel(capture, (int)CubemapFace.PositiveZ);
                    Assert.That(pixel.r, Is.EqualTo(4).Within(.08f), "ReplaceDiffuseForIndirect must preserve baked GI on metals.");
                    Assert.Less(pixel.g, .01f);
                    LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = Solid(Color.black) } };
                    m_Sun.enabled = true;
                    MobileReflectionBaking.Render(probe, capture, out _, out _);
                    Assert.Less(BakedFacePixel(capture, (int)CubemapFace.PositiveZ).maxColorComponent, .01f,
                        "Default baked probe settings must exclude direct specular highlights.");
                    probe.settingsRaw.cameraSettings.customRenderingSettings = true;
                    probe.settingsRaw.cameraSettings.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.DirectSpecularLighting, true);
                    probe.settingsRaw.cameraSettings.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.DirectSpecularLighting] = true;
                    MobileReflectionBaking.Render(probe, capture, out _, out _);
                    Assert.Greater(BakedFacePixel(capture, (int)CubemapFace.PositiveZ).maxColorComponent, .1f,
                        "An explicit direct specular override must still work.");
                    m_Sun.enabled = false;
                    probe.settingsRaw.cameraSettings.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.SkyReflection, true);
                    probe.settingsRaw.cameraSettings.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.SkyReflection] = true;
                    MobileReflectionBaking.Render(probe, capture, out _, out _);
                    Assert.Greater(BakedFacePixel(capture, (int)CubemapFace.PositiveZ).maxColorComponent, 10,
                        "An explicit sky reflection override must still work.");
                }
            }
            finally { card.lightmapIndex = -1; LightmapSettings.lightmaps = previous; LightmapSettings.lightmapsMode = previousMode; }
        }

        Color BakedFacePixel(Texture cube, int face)
        {
            var output = RenderTexture.GetTemporary(cube.width, cube.height, 0, RenderTextureFormat.ARGBHalf);
            var pixels = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            var previous = RenderTexture.active;
            try
            {
                Graphics.CopyTexture(cube, face, 0, output, 0, 0);
                RenderTexture.active = output;
                pixels.ReadPixels(new Rect(cube.width / 2, cube.height / 2, 1, 1), 0, 0); pixels.Apply();
                return pixels.GetPixel(0, 0);
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(output); UnityEngine.Object.DestroyImmediate(pixels); }
        }

        [Test]
        public void MobileReflectionBakeCapturesSixHdrFacesAndImportsThroughBothEntrypoints()
        {
            if (!Application.isBatchMode) Assert.Ignore("Saved-scene bake integration runs in an isolated batchmode project, never over interactive unsaved scenes.");
            string folder = "Assets/MobileReflectionBakeTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            // Texture import reloads ProjectSettings; the pipeline reference must
            // be a real asset, as it is in an authoring project.
            AssetDatabase.CreateAsset(m_Asset, folder + "/Mobile.asset");
            GraphicsSettings.renderPipelineAsset = m_Asset; QualitySettings.renderPipeline = m_Asset;
            AssetDatabase.SaveAssets();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                m_Sun.enabled = false;
                m_Asset.mobileSettings.renderScale = .5f; // capture must retain requested dimensions
                m_Asset.mobileSettings.renderingPath = MobileRenderingPath.Deferred;
                var probe = AtlasProbe(null, ProbeSettings.Mode.Baked);
                SceneManager.MoveGameObjectToScene(probe.gameObject, scene);
                probe.settingsRaw.cameraSettings.culling.cullingMask = 1 << 30;
                probe.settingsRaw.cameraSettings.bufferClearing.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                probe.settingsRaw.cameraSettings.bufferClearing.backgroundColorHDR = Color.black;
                probe.rangeCompressionFactor = 2;
                probe.settingsRaw.cameraSettings.volumes.layerMask = 1 << 29;
                var post = CameraProfile();
                post.Add<Exposure>().fixedExposure.Override(8);
                post.Add<Tonemapping>().mode.Override(TonemappingMode.ACES);
                post.Add<Bloom>().intensity.Override(1);
                var directions = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
                var colors = new[] { Color.red * 4, Color.green * 4, Color.blue * 4, Color.yellow * 4, Color.cyan * 4, Color.magenta * 4 };
                for (int f = 0; f < 6; ++f)
                {
                    var card = Quad(Material(false, colors[f]));
                    card.transform.position = directions[f] * 2;
                    card.transform.rotation = Quaternion.LookRotation(directions[f], f == 2 || f == 3 ? Vector3.forward : Vector3.up);
                    SceneManager.MoveGameObjectToScene(card.gameObject, scene);
                    GameObjectUtility.SetStaticEditorFlags(card.gameObject, StaticEditorFlags.ReflectionProbeStatic);
                }
                var dynamicCard = Quad(Material(false, Color.white * 10));
                var upperMarker = Quad(Material(false, Color.white * 4));
                upperMarker.transform.position = new Vector3(0, .6f, 2);
                upperMarker.transform.localScale = Vector3.one * .3f;
                SceneManager.MoveGameObjectToScene(upperMarker.gameObject, scene);
                GameObjectUtility.SetStaticEditorFlags(upperMarker.gameObject, StaticEditorFlags.ReflectionProbeStatic);
                dynamicCard.transform.position = Vector3.forward;
                SceneManager.MoveGameObjectToScene(dynamicCard.gameObject, scene);
                Assert.IsTrue(EditorSceneManager.SaveScene(scene, folder + "/Bake.unity"));
                var options = LightmappingHDRP.BakeProbeOptions.NewDefault();
                Assert.AreEqual((int)probe.cubeResolution, options.textureSize.Evaluate(probe));
                options.textureSize.mode = LightmappingHDRP.BakeProbeOptions.TextureSize.Mode.CustomValue;
                options.textureSize.customValue = 32;
                string path = folder + "/Probe.exr";
                Assert.IsNull(LightmappingHDRP.BakeProbe(probe, path, options));
                Assert.IsNotNull(probe.bakedTexture);
                Assert.AreEqual(TextureDimension.Cube, probe.bakedTexture.dimension);
                Assert.AreEqual(32, probe.bakedTexture.width);
                Assert.IsFalse(dynamicCard.forceRenderingOff, "Dynamic renderer state must be restored.");
                Assert.IsNull(MobileRenderPipelineBackend.reflectionCaptureCamera);
                Assert.AreSame(m_Asset, GraphicsSettings.currentRenderPipeline);
                // Read the capture RT directly: imported textures can use a compressed format.
                var capture = Own(HDRenderUtilities.CreateReflectionProbeRenderTarget(32, UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat));
                MobileReflectionBaking.Render(probe, capture, out _, out _);
                for (int f = 0; f < 6; ++f)
                {
                    var pixel = BakedFacePixel(capture, f);
                    for (int c = 0; c < 3; ++c) Assert.That(pixel[c], Is.EqualTo(colors[f][c] / 2).Within(.03f), "HDR face " + f + ", channel " + c);
                }
                Assert.IsTrue(HDBakedReflectionSystem.BakeProbes(new[] { probe }), "Inspector bake entrypoint must accept Mobile HDRP.");
                Assert.IsNotNull(probe.bakedTexture);
                if (RenderPipelineManager.currentPipeline is HDRenderPipeline pipeline) Assert.IsFalse(pipeline.reflectionProbeBaking);
                m_Camera.scene = scene;
                m_Camera.targetTexture = Own(new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear));
                m_Camera.targetTexture.Create();
                Capture();
                Assert.Greater(Shader.GetGlobalInt("_MobileReflectionCount16"), 0);
                var reader = Own(new Material(Shader.Find("Hidden/HDRP/Tests/MobileReflectionAtlas")));
                for (int f = 0; f < 6; ++f)
                {
                    var pixel = SampleAtlas(reader, 0, directions[f], 0);
                    for (int c = 0; c < 3; ++c) Assert.That(pixel[c], Is.EqualTo(colors[f][c]).Within(.15f), "Imported atlas face " + f);
                }
                Assert.Greater(SampleAtlas(reader, 0, new Vector3(0, .3f, 1).normalized, 0).r, 3.5f, "Cubemap upper marker must retain world-space orientation.");
                Assert.Less(SampleAtlas(reader, 0, new Vector3(0, -.3f, 1).normalized, 0).r, .1f, "Cubemap must not be vertically inverted.");
            }
            finally
            {
                m_Camera.scene = m_Scene;
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
