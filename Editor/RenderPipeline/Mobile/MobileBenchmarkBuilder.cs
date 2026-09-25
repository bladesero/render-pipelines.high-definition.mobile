using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition
{
    public static class MobileBenchmarkBuilder
    {
        [MenuItem("Rendering/HDRP Mobile/Create Benchmark Scene")]
        public static void Create()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            string folder = AssetDatabase.GenerateUniqueAssetPath("Assets/HDRP Mobile Benchmark");
            CreateAt(folder);
        }

        internal static void CreateAt(string folder)
        {
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);
            var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            asset.rendererMode = HDRendererMode.Mobile;
            asset.mobileSettings.drawUnsupportedMaterials = false;
            AssetDatabase.CreateAsset(asset, folder + "/Mobile.asset");

            Material MakeMaterial(string name, Color color, bool transparent = false, bool cutout = false)
            {
                var material = new Material(Shader.Find("HDRP/Lit")) { name = name, enableInstancing = true };
                material.SetColor("_BaseColor", color);
                material.SetFloat("_MaterialID", 1);
                material.SetFloat("_Smoothness", 0.4f);
                if (transparent) { material.SetFloat("_SurfaceType", 1); material.SetFloat("_BlendMode", 0); }
                if (cutout)
                {
                    var texture = new Texture2D(32, 32, TextureFormat.RGBA32, true);
                    for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
                        texture.SetPixel(x, y, new Color(1, 1, 1, ((x / 4 + y / 4) % 2 == 0) ? 1 : 0));
                    texture.Apply();
                    AssetDatabase.CreateAsset(texture, folder + "/Cutout.asset");
                    material.SetTexture("_BaseColorMap", texture);
                    material.SetFloat("_AlphaCutoffEnable", 1);
                    material.SetFloat("_DoubleSidedEnable", 1);
                }
                HDShaderUtils.ResetMaterialKeywords(material);
                AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
                return material;
            }
            var stone = MakeMaterial("Stone", new Color(0.5f, 0.55f, 0.6f));
            var leaf = MakeMaterial("Cutout", new Color(0.1f, 0.6f, 0.15f), cutout: true);
            var glass = MakeMaterial("Transparent", new Color(0.1f, 0.6f, 0.8f, 0.35f), transparent: true);
            var metal = MakeMaterial("Metal", new Color(0.7f, 0.4f, 0.1f));
            metal.SetFloat("_Metallic", 0.8f);
            EditorUtility.SetDirty(metal);

            var bakedMeshes = new System.Collections.Generic.Dictionary<PrimitiveType, Mesh>();
            GameObject Primitive(string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material, bool bake)
            {
                var obj = GameObject.CreatePrimitive(type);
                obj.name = name; obj.transform.position = position; obj.transform.localScale = scale;
                obj.GetComponent<Renderer>().sharedMaterial = material;
                if (bake)
                {
                    if (!bakedMeshes.TryGetValue(type, out var bakedMesh))
                    {
                        bakedMesh = Object.Instantiate(obj.GetComponent<MeshFilter>().sharedMesh);
                        UnwrapParam.SetDefaults(out var unwrap); unwrap.packMargin = 0.08f;
                        Unwrapping.GenerateSecondaryUVSet(bakedMesh, unwrap);
                        AssetDatabase.CreateAsset(bakedMesh, folder + "/Baked" + type + ".asset");
                        bakedMeshes.Add(type, bakedMesh);
                    }
                    obj.GetComponent<MeshFilter>().sharedMesh = bakedMesh;
                    GameObjectUtility.SetStaticEditorFlags(obj, StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic);
                }
                return obj;
            }
            Primitive("Ground", PrimitiveType.Cube, new Vector3(0, -0.25f, 0), new Vector3(20, 0.5f, 20), stone, true);
            for (int z = 0; z < 6; z++) for (int x = 0; x < 6; x++)
            {
                float h = 0.5f + ((x * 7 + z * 3) % 5) * 0.2f;
                Primitive("Baked block " + x + "," + z, PrimitiveType.Cube, new Vector3(x * 2 - 5, h / 2, z * 2 - 5), new Vector3(0.8f, h, 0.8f), stone, true);
            }
            for (int i = 0; i < 8; i++)
                Primitive("Cutout foliage " + i, PrimitiveType.Quad, new Vector3(i - 3.5f, 1, 3), new Vector3(0.8f, 2, 1), leaf, true);
            Primitive("Transparency", PrimitiveType.Sphere, new Vector3(2, 1.5f, 0), Vector3.one * 2, glass, false);
            var character = Primitive("Skinned character", PrimitiveType.Capsule, new Vector3(0, 1.5f, 0), Vector3.one, metal, false);
            var mesh = Object.Instantiate(character.GetComponent<MeshFilter>().sharedMesh);
            var weights = new BoneWeight[mesh.vertexCount];
            for (int i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = mesh.vertices[i].y > 0 ? 1 : 0, weight0 = 1 };
            var bone0 = new GameObject("Root bone").transform; bone0.SetParent(character.transform, false);
            var bone1 = new GameObject("Upper bone").transform; bone1.SetParent(bone0, false);
            mesh.boneWeights = weights;
            mesh.bindposes = new[] { bone0.worldToLocalMatrix * character.transform.localToWorldMatrix, bone1.worldToLocalMatrix * character.transform.localToWorldMatrix };
            AssetDatabase.CreateAsset(mesh, folder + "/Character.asset");
            Object.DestroyImmediate(character.GetComponent<MeshRenderer>());
            Object.DestroyImmediate(character.GetComponent<MeshFilter>());
            var skin = character.AddComponent<SkinnedMeshRenderer>();
            skin.sharedMesh = mesh; skin.bones = new[] { bone0, bone1 }; skin.rootBone = bone0; skin.sharedMaterial = metal;
            skin.localBounds = new Bounds(Vector3.zero, Vector3.one * 4);

            var sun = new GameObject("Main light").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 3; sun.shadows = LightShadows.Soft;
            sun.lightmapBakeType = LightmapBakeType.Mixed;
            sun.gameObject.AddComponent<HDAdditionalLightData>().SetIntensity(100000f, LightUnit.Lux);
            sun.transform.rotation = Quaternion.Euler(45, -30, 0); RenderSettings.sun = sun;
            for (int i = 0; i < 4; i++)
            {
                var light = new GameObject("Local light " + i).AddComponent<Light>();
                light.type = i % 2 == 0 ? LightType.Point : LightType.Spot;
                light.range = 6; light.intensity = 8; light.color = Color.HSVToRGB(i / 4f, 0.6f, 1);
                light.transform.position = new Vector3(i % 2 * 4 - 2, 3, i / 2 * 4 - 2);
                light.transform.rotation = Quaternion.Euler(90, 0, 0);
                light.gameObject.AddComponent<HDAdditionalLightData>().SetIntensity(600f, LightUnit.Lumen);
            }
            var probes = new GameObject("Light probes").AddComponent<LightProbeGroup>();
            var positions = new Vector3[18]; int p = 0;
            for (int y = 0; y < 2; y++) for (int z = 0; z < 3; z++) for (int x = 0; x < 3; x++) positions[p++] = new Vector3(x * 5 - 5, y * 3 + 0.5f, z * 5 - 5);
            probes.probePositions = positions;
            var probe = new GameObject("Baked reflection").AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Baked; probe.size = Vector3.one * 22; probe.resolution = 64;
            var hdProbe = probe.gameObject.AddComponent<HDAdditionalReflectionData>();
            hdProbe.mode = ProbeSettings.Mode.Baked;
            hdProbe.influenceVolume.boxSize = Vector3.one * 22;
            hdProbe.PrepareCulling();
            probe.transform.position = new Vector3(0, 2, 0);
            var camera = new GameObject("Main Camera").AddComponent<Camera>();
            camera.tag = "MainCamera"; camera.transform.position = new Vector3(0, 3, -8); camera.transform.LookAt(Vector3.up);
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.1f, 0.15f, 0.2f);
            camera.farClipPlane = 60;
            var benchmark = new GameObject("Benchmark 1080p60").AddComponent<MobileBenchmark>();
            benchmark.benchmarkCamera = camera; benchmark.animatedBone = bone1;
            var volume = new GameObject("Fixed grading").AddComponent<Volume>(); volume.isGlobal = true;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.Add<Tonemapping>(true).mode.value = TonemappingMode.Neutral;
            var exposure = profile.Add<Exposure>(true); exposure.mode.value = ExposureMode.Fixed; exposure.fixedExposure.value = 14;
            AssetDatabase.CreateAsset(profile, folder + "/Volume.asset");
            foreach (var component in profile.components) AssetDatabase.AddObjectToAsset(component, profile);
            EditorUtility.SetDirty(profile);
            volume.sharedProfile = profile;
            var lighting = new LightingSettings { bakedGI = true, realtimeGI = false, mixedBakeMode = MixedLightingMode.IndirectOnly, lightmapResolution = 32 };
            AssetDatabase.CreateAsset(lighting, folder + "/Lighting.asset"); Lightmapping.lightingSettings = lighting;
            EditorSceneManager.SaveScene(scene, folder + "/Benchmark.unity");
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
            Debug.Log("Mobile benchmark created in " + folder + ". Assign Mobile.asset to Graphics/Quality, bake lighting, then run at 1920x1080. Existing pipeline settings were not changed.");
        }
    }
}
