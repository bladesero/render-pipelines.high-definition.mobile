using System.Collections.Generic;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering.HighDefinition
{
    // DrawingSettings.fallbackMaterial does not cover a valid shader with no matching LightMode.
    // Cache classifications once per frame, without mutating the original materials/pass enables.
    internal sealed class MobileErrorRenderer
    {
        struct Entry { internal Renderer renderer; internal int submesh, queue; }
        readonly List<GameObject> m_Roots = new List<GameObject>();
        readonly List<Renderer> m_Renderers = new List<Renderer>();
        readonly List<Material> m_Materials = new List<Material>();
        readonly Dictionary<Material, bool> m_Supported = new Dictionary<Material, bool>();
        readonly List<Entry> m_Entries = new List<Entry>();
        readonly HashSet<int> m_Scenes = new HashSet<int>();
        readonly Plane[] m_Planes = new Plane[6];
        int m_Frame = -1;

        internal void Prepare(Camera camera)
        {
            if (m_Frame != Time.frameCount || !Application.isPlaying)
            {
                m_Frame = Time.frameCount;
                m_Entries.Clear(); m_Supported.Clear(); m_Scenes.Clear();
                for (int i = 0; i < SceneManager.sceneCount; i++) AddScene(SceneManager.GetSceneAt(i));
            }
            // Also covers material-preview scenes and a camera in DontDestroyOnLoad's scene.
            AddScene(camera.gameObject.scene);
        }

        void AddScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || !m_Scenes.Add(scene.handle)) return;
            m_Roots.Clear(); scene.GetRootGameObjects(m_Roots);
            foreach (var root in m_Roots)
            {
                m_Renderers.Clear(); root.GetComponentsInChildren(false, m_Renderers);
                foreach (var renderer in m_Renderers)
                {
                    if (!renderer.enabled || renderer.forceRenderingOff || renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                    m_Materials.Clear(); renderer.GetSharedMaterials(m_Materials);
                    for (int i = 0; i < m_Materials.Count; i++)
                    {
                        var material = m_Materials[i];
                        bool supported = false;
                        if (material != null && !m_Supported.TryGetValue(material, out supported))
                        {
                            supported = MobileDeferredMaterial.IsSupported(material);
                            m_Supported.Add(material, supported);
                        }
                        if (!supported) m_Entries.Add(new Entry { renderer = renderer, submesh = i, queue = material != null ? material.renderQueue : 2000 });
                    }
                }
            }
        }

        internal void Draw(CommandBuffer cmd, Camera camera, Material error, bool transparent)
        {
            if (m_Entries.Count == 0) return;
            GeometryUtility.CalculateFrustumPlanes(camera, m_Planes);
            foreach (var entry in m_Entries)
            {
                var renderer = entry.renderer;
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy
                    || ((entry.queue > 2500) != transparent) || (camera.cullingMask & (1 << renderer.gameObject.layer)) == 0
                    || !GeometryUtility.TestPlanesAABB(m_Planes, renderer.bounds)) continue;
                cmd.DrawRenderer(renderer, error, entry.submesh, 0);
            }
        }
    }
}
