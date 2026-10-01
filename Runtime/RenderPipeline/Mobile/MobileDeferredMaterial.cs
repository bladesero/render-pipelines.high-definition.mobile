using System.Collections.Generic;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering.HighDefinition
{
    // The mobile stencil contract is independent of desktop HDRP StencilUsage.
    internal static class MobileDeferredMaterial
    {
        internal const int ReceiveShadowsOff = 1, SpecularHighlightsOff = 2;
        internal const int SubtractiveMixedLighting = 4, SpecularSetup = 8;
        internal const int MaterialMask = 0x70, LightingMask = 0x7f, UserMask = 0x80;
        internal const int Standard = 0x10, ForwardOnly = 0x60, Background = 0x70;

        internal static int Encode(int materialType, bool receiveShadows, bool specularHighlights, bool user)
            => ((materialType & 7) << 4) | (receiveShadows ? 0 : ReceiveShadowsOff)
                | (specularHighlights ? 0 : SpecularHighlightsOff) | (user ? UserMask : 0);

        internal static bool IsSupported(Material material)
            => material != null && material.FindPass("MobileForward") >= 0
                && (!material.HasProperty("_MaterialID") || (material.GetFloat("_MaterialID") >= 0 && material.GetFloat("_MaterialID") <= 5));

        internal static int LightingVariantMask(Material material, bool extended)
        {
            string pass = extended ? "MobileGBufferExtended" : "MobileGBuffer";
            if (material == null || material.renderQueue < 0 || material.renderQueue > 2500
                || !material.GetShaderPassEnabled(pass) || material.FindPass(pass) < 0) return 0;
            // Unknown GBuffer writers may implement a different stencil setup.
            if (material.shader.name != "HDRP/Lit") return extended ? 255 : 15;
            // Match the fixed-function stencil reference actually written, including
            // serialized materials whose keyword validation has not run recently.
            int stencil = material.GetInt("_MobileStencilRef") & LightingMask;
            int type = stencil & ~3;
            if (type == Standard) return 1 << (stencil & 3);
            if (extended && type == ((4 << 4) | SpecularSetup)) return 1 << (4 + (stencil & 3));
            return 0;
        }

        internal static void Validate(Material material)
        {
            if (!material.HasProperty("_MobileStencilRef")) return;
            bool opaqueLit = material.HasProperty("_MaterialID") && material.GetFloat("_SurfaceType") == 0;
            int type = opaqueLit ? (int)material.GetFloat("_MaterialID") : 6;
            material.SetInt("_MobileStencilRef", Encode(type, material.GetFloat("_MobileReceiveShadows") > 0,
                material.GetFloat("_MobileSpecularHighlights") > 0, material.GetFloat("_MobileUserStencilBit") > 0) | (type == 4 ? SpecularSetup : 0));
            if (material.HasProperty("_MobileForwardStencilRef"))
                material.SetInt("_MobileForwardStencilRef", ForwardOnly | (material.GetInt("_MobileStencilRef") & (UserMask | 0x0f)));
            bool compact = opaqueLit && type == 1 && material.GetFloat("_CoatMask") <= 0;
            bool extended = opaqueLit && (type == 1 || type == 4);
            bool forwardOnly = !material.HasProperty("_MaterialID") || opaqueLit;
            material.SetShaderPassEnabled("MobileGBuffer", compact);
            material.SetShaderPassEnabled("MobileGBufferExtended", extended);
            // Keep routing independent of the active camera/asset. Each deferred
            // layout selects its own complementary forward pass before submission.
            material.SetShaderPassEnabled("MobileForwardOnly", forwardOnly && !compact);
            material.SetShaderPassEnabled("MobileForwardOnlyExtended", forwardOnly && !extended);
        }
    }

    // Conservative camera candidates: frustum/layers, not GPU occlusion or LOD.
    // Rebuilt for every camera so runtime material changes and same-frame spawns
    // cannot suppress lighting. Never use Renderer.isVisible (shared by cameras).
    internal sealed class MobileDeferredMaterialUsage
    {
        readonly List<GameObject> m_Roots = new List<GameObject>();
        readonly List<Renderer> m_Renderers = new List<Renderer>();
        readonly List<Material> m_Materials = new List<Material>();
        readonly Dictionary<Material, int> m_Masks = new Dictionary<Material, int>();
        readonly Plane[] m_Planes = new Plane[6];

        internal int Gather(Camera camera, bool extended)
        {
            Profiler.BeginSample("Mobile Deferred.Material Usage");
            try
            {
                m_Masks.Clear();
                GeometryUtility.CalculateFrustumPlanes(camera.cullingMatrix, m_Planes);
                int mask = 0, all = extended ? 255 : 15;
                // Isolated cameras include material previews; FindObjectsByType
                // excludes preview scenes. Normal discovery includes DontDestroyOnLoad.
                if (camera.scene.IsValid())
                {
                    m_Roots.Clear(); camera.scene.GetRootGameObjects(m_Roots);
                    foreach (var root in m_Roots)
                    {
                        m_Renderers.Clear(); root.GetComponentsInChildren(false, m_Renderers);
                        foreach (var renderer in m_Renderers)
                        {
                            mask |= GatherRenderer(renderer, camera, extended);
                            if (mask == all) return mask;
                        }
                    }
                }
                else
                {
                    // Loaded material states bound the possible stencil references.
                    // Most scenes use one combination: stop at its first candidate
                    // rather than inspecting hundreds of renderers' bounds/materials.
                    int possible = 0;
                    foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
                    {
                        int bits = MobileDeferredMaterial.LightingVariantMask(material, extended);
                        m_Masks[material] = bits; possible |= bits;
                    }
                    if (possible == 0) return 0;
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var scene = SceneManager.GetSceneAt(i);
                        if (!scene.isLoaded) continue;
                        m_Roots.Clear(); scene.GetRootGameObjects(m_Roots);
                        foreach (var root in m_Roots)
                        {
                            m_Renderers.Clear(); root.GetComponentsInChildren(false, m_Renderers);
                            foreach (var renderer in m_Renderers)
                            {
                                mask |= GatherRenderer(renderer, camera, extended);
                                if ((mask & possible) == possible) return mask;
                            }
                        }
                    }
                    // Unity exposes no visible-renderer list on CullingResults.
                    // Fallback includes DontDestroyOnLoad objects absent from the
                    // loaded-scene list. The common case exits before this allocation.
                    foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                    {
                        mask |= GatherRenderer(renderer, camera, extended);
                        if ((mask & possible) == possible) return mask;
                    }
                }
                return mask;
            }
            finally { Profiler.EndSample(); }
        }

        int GatherRenderer(Renderer renderer, Camera camera, bool extended)
        {
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.forceRenderingOff
                || renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly
                || (camera.cullingMask & (1 << renderer.gameObject.layer)) == 0
                || !GeometryUtility.TestPlanesAABB(m_Planes, renderer.bounds)) return 0;
            m_Materials.Clear(); renderer.GetSharedMaterials(m_Materials);
            int mask = 0;
            foreach (var material in m_Materials)
            {
                if (material == null) continue;
                // Property blocks affect surface inputs, not fixed-function stencil
                // render state; classify the shared material's stencil reference.
                if (!m_Masks.TryGetValue(material, out int bits))
                {
                    bits = MobileDeferredMaterial.LightingVariantMask(material, extended);
                    m_Masks.Add(material, bits);
                }
                mask |= bits;
            }
            return mask;
        }
    }
}
