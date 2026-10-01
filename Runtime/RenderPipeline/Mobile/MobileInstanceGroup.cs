using System;
using System.Collections.Generic;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Opt-in static mesh instances. Unsupported renderers keep their native rendering path.</summary>
    [ExecuteAlways, DisallowMultipleComponent, AddComponentMenu("Rendering/HDRP Mobile Instance Group")]
    public sealed class MobileInstanceGroup : MonoBehaviour
    {
        internal static readonly HashSet<MobileInstanceGroup> active = new HashSet<MobileInstanceGroup>();
        [Tooltip("Explicit renderer ownership. Use Collect Child Renderers after changing the hierarchy.")]
        public MeshRenderer[] renderers = Array.Empty<MeshRenderer>();
        [Min(1)] public float cellSize = 16;
        [Min(0), Tooltip("Zero uses the pipeline's instance distance.")]
        public float drawDistance;
        [Min(0), Tooltip("Conservative world-space expansion for vertex deformation.")]
        public float boundsPadding;
        [Tooltip("Optional lower-detail meshes shared by this group. Each must have the same submesh count as the source mesh. Existing LODGroups use native rendering.")]
        public Mesh[] lodMeshes = Array.Empty<Mesh>();
        [Tooltip("Screen-height thresholds for lodMeshes, descending, e.g. 0.15, 0.05.")]
        public float[] lodScreenHeights = Array.Empty<float>();
        internal int revision;

        void OnEnable() { active.Add(this); MarkDirty(); }
        void OnDisable() { active.Remove(this); MarkDirty(); }
        void OnValidate() { cellSize = Mathf.Max(1, cellSize); MarkDirty(); }
        /// <summary>Call after changing membership, mesh, lighting, materials or LOD configuration. Moving transforms are detected automatically.</summary>
        public void MarkDirty() { unchecked { ++revision; } }
        [ContextMenu("Collect Child Renderers")]
        public void CollectChildRenderers()
        {
            renderers = GetComponentsInChildren<MeshRenderer>(true);
            MarkDirty();
        }
        void Reset() { CollectChildRenderers(); }
    }
}
