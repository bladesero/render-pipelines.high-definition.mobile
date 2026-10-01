using System;
using System.Collections.Generic;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Last completed Mobile camera's instance submission counters.</summary>
    public struct MobileInstanceStatistics
    {
        public int cameraId, candidates, fallbackRenderers, cells, visibleCells, visibleInstances, draws;
        public int uploadedBytes;
    }

    public static class MobileInstanceDiagnostics
    {
        public static MobileInstanceStatistics lastCamera { get; internal set; }
    }

    // Camera-scoped ownership: original renderers remain native shadow casters. They are
    // restored in Render's finally block, including exceptions, captures and pipeline switches.
    // The RenderGraph is executed/submitted synchronously before this storage is reused.
    internal sealed class MobileInstanceRenderer : IDisposable
    {
        internal const int Capacity = 128;
        internal const string Keyword = "MOBILE_INSTANCE_DATA";
        sealed class Entry
        {
            internal MobileInstanceGroup group;
            internal MeshRenderer renderer;
            internal Mesh mesh;
            internal Material[] materials;
            internal Matrix4x4 matrix;
            internal Bounds bounds;
            internal bool eligible;
            internal Vector4 lights, info, lightmapST;
            internal SphericalHarmonicsL2 sh;
            internal int lightmapIndex = int.MinValue;
        }
        sealed class Cell
        {
            internal Bounds bounds;
            internal readonly List<int> entries = new List<int>();
            internal float distance;
        }
        struct Saved
        {
            internal MeshRenderer renderer;
            internal ShadowCastingMode shadow;
            internal bool forceOff;
        }
        readonly struct Key : IEquatable<Key>
        {
            internal readonly Mesh mesh;
            internal readonly Material material;
            internal readonly int submesh, lightmap;
            internal Key(Mesh mesh, Material material, int submesh, int lightmap)
            { this.mesh = mesh; this.material = material; this.submesh = submesh; this.lightmap = lightmap; }
            public bool Equals(Key k) => mesh == k.mesh && material == k.material && submesh == k.submesh && lightmap == k.lightmap;
            public override bool Equals(object obj) => obj is Key k && Equals(k);
            public override int GetHashCode() => ((mesh.GetInstanceID() * 397 ^ material.GetInstanceID()) * 397 ^ submesh) * 397 ^ lightmap;
        }
        sealed class Batch
        {
            internal readonly List<int> entries = new List<int>();
            internal Material material;
        }
        sealed class CameraState
        {
            internal int generation = -1;
            internal int[] lods = Array.Empty<int>();
            internal Vector3 position;
            internal Quaternion rotation;
        }
        readonly Dictionary<MobileInstanceGroup, int> m_Revisions = new Dictionary<MobileInstanceGroup, int>();
        readonly List<Entry> m_Entries = new List<Entry>();
        readonly List<Cell> m_Cells = new List<Cell>(), m_VisibleCells = new List<Cell>();
        readonly List<Cell> m_CellPool = new List<Cell>();
        readonly Dictionary<(MobileInstanceGroup, Vector3Int), Cell> m_Grid = new Dictionary<(MobileInstanceGroup, Vector3Int), Cell>();
        readonly Dictionary<Key, Batch> m_Batches = new Dictionary<Key, Batch>();
        readonly Dictionary<Camera, CameraState> m_Cameras = new Dictionary<Camera, CameraState>();
        readonly List<Camera> m_Expired = new List<Camera>();
        readonly List<Saved> m_Saved = new List<Saved>();
        readonly HashSet<MeshRenderer> m_Owners = new HashSet<MeshRenderer>();
        readonly HashSet<MeshRenderer> m_DuplicateOwners = new HashSet<MeshRenderer>();
        readonly List<Material> m_Materials = new List<Material>();
        readonly List<ReflectionProbeBlendInfo> m_Probes = new List<ReflectionProbeBlendInfo>();
        readonly Plane[] m_Planes = new Plane[6];
        readonly Matrix4x4[] m_Matrices = new Matrix4x4[Capacity];
        readonly Vector4[] m_Lights = new Vector4[Capacity], m_Info = new Vector4[Capacity], m_ST = new Vector4[Capacity];
        readonly Vector4[][] m_SH = new Vector4[7][];
        readonly float[] m_LightScores = new float[4];
        readonly MaterialPropertyBlock m_Block = new MaterialPropertyBlock();
        static readonly int LightsId = Shader.PropertyToID("_MobileInstanceLights"), InfoId = Shader.PropertyToID("_MobileInstanceInfo"), STId = Shader.PropertyToID("_MobileInstanceLightmapST");
        static readonly int[] SHIds = { Shader.PropertyToID("_MobileInstanceSHAr"), Shader.PropertyToID("_MobileInstanceSHAg"), Shader.PropertyToID("_MobileInstanceSHAb"), Shader.PropertyToID("_MobileInstanceSHBr"), Shader.PropertyToID("_MobileInstanceSHBg"), Shader.PropertyToID("_MobileInstanceSHBb"), Shader.PropertyToID("_MobileInstanceSHC") };
        static readonly Comparison<Cell> s_NearFirst = (a, b) => a.distance.CompareTo(b.distance);
        int m_Generation, m_BatchSize;
        LightmapData[] m_Lightmaps = Array.Empty<LightmapData>();
        internal MobileInstanceStatistics statistics;

        internal MobileInstanceRenderer() { for (int i = 0; i < 7; ++i) m_SH[i] = new Vector4[Capacity]; }

        void Rebuild()
        {
            foreach (var batch in m_Batches.Values) CoreUtils.Destroy(batch.material);
            m_Batches.Clear(); m_Entries.Clear(); m_Revisions.Clear(); m_Owners.Clear(); m_DuplicateOwners.Clear();
            foreach (var group in MobileInstanceGroup.active)
            {
                if (group == null || !group.isActiveAndEnabled) continue;
                m_Revisions[group] = group.revision;
                if (group.renderers == null) continue;
                foreach (var renderer in group.renderers)
                {
                    if (renderer == null) continue;
                    if (!m_Owners.Add(renderer)) { m_DuplicateOwners.Add(renderer); continue; }
                    var filter = renderer.GetComponent<MeshFilter>();
                    m_Entries.Add(new Entry { group = group, renderer = renderer, mesh = filter != null ? filter.sharedMesh : null,
                        materials = renderer.sharedMaterials });
                }
            }
            ++m_Generation;
        }

        bool Eligible(Entry entry, Camera camera)
        {
            var r = entry.renderer;
            if (r == null || entry.group == null || !entry.group.isActiveAndEnabled || !r.enabled || !r.gameObject.activeInHierarchy || r.forceRenderingOff
                || (camera.cullingMask & (1 << r.gameObject.layer)) == 0) return false;
            if (camera.scene.IsValid() && r.gameObject.scene != camera.scene) return false;
            if (entry.mesh == null || m_DuplicateOwners.Contains(r) || r.isPartOfStaticBatch || r.HasPropertyBlock()
                || r.shadowCastingMode == ShadowCastingMode.ShadowsOnly || r.shadowCastingMode == ShadowCastingMode.TwoSided
                || r.GetComponentInParent<LODGroup>() != null || (r.lightProbeUsage != LightProbeUsage.Off && r.lightProbeUsage != LightProbeUsage.BlendProbes)
                || r.realtimeLightmapIndex >= 0 && r.realtimeLightmapIndex < 65534) return false;
            var filter = r.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh != entry.mesh || entry.mesh.subMeshCount != entry.materials.Length) return false;
            var matrix = r.localToWorldMatrix;
            float determinant = matrix.determinant;
            if (float.IsNaN(determinant) || float.IsInfinity(determinant) || determinant <= 1e-8f) return false;
            r.GetSharedMaterials(m_Materials);
            if (m_Materials.Count != entry.materials.Length) return false;
            for (int i = 0; i < m_Materials.Count; ++i)
            {
                var material = m_Materials[i];
                if (material == null || material != entry.materials[i] || !material.enableInstancing || !material.shader.isSupported
                    || (material.shader.name != "HDRP/Lit" && material.shader.name != "HDRP/Unlit")
                    || material.renderQueue > 2500 || material.GetFloat("_SurfaceType") != 0
                    || material.FindPass("MobileForward") < 0 || !material.GetShaderPassEnabled("MobileForward")) return false;
            }
            if (entry.group.lodMeshes == null || entry.group.lodScreenHeights == null
                || entry.group.lodMeshes.Length != entry.group.lodScreenHeights.Length) return false;
            float previous = float.PositiveInfinity;
            for (int i = 0; i < entry.group.lodMeshes.Length; ++i)
            {
                float threshold = entry.group.lodScreenHeights[i];
                if (entry.group.lodMeshes[i] == null || entry.group.lodMeshes[i].subMeshCount != entry.mesh.subMeshCount
                    || !(threshold > 0 && threshold < previous)) return false;
                previous = threshold;
            }
            int lm = r.lightmapIndex;
            if (lm >= 0 && lm < 65534 && (lm >= m_Lightmaps.Length || m_Lightmaps[lm].lightmapColor == null
                || (LightmapSettings.lightmapsMode == LightmapsMode.CombinedDirectional && m_Lightmaps[lm].lightmapDir == null))) return false;
            // Native non-HDRP probes have per-renderer blending/box projection. Leave them native.
            m_Probes.Clear(); r.GetClosestReflectionProbes(m_Probes);
            foreach (var blend in m_Probes)
            {
                var probe = blend.probe != null ? blend.probe.GetComponent<HDAdditionalReflectionData>() : null;
                if (probe == null || !probe.isActiveAndEnabled) return false;
            }
            return true;
        }

        internal void Begin(Camera camera, MobileRenderSettings settings, float shadowDistance)
        {
            Restore();
            statistics = new MobileInstanceStatistics { cameraId = camera.GetInstanceID() };
            foreach (var batch in m_Batches.Values) batch.entries.Clear();
            if (settings.instanceMode != MobileInstanceMode.CpuInstanced || !SystemInfo.supportsInstancing
                || camera.cameraType != CameraType.Game || camera == MobileRenderPipelineBackend.reflectionCaptureCamera) return;
            Profiler.BeginSample("Mobile Instances.Prepare");
            try
            {
                bool rebuild = m_Revisions.Count != MobileInstanceGroup.active.Count;
                foreach (var group in MobileInstanceGroup.active)
                    if (group != null && (!m_Revisions.TryGetValue(group, out int revision) || revision != group.revision)) rebuild = true;
                if (rebuild) Rebuild();
                bool refreshLightmaps = rebuild;
                foreach (var e in m_Entries)
                {
                    int index = e.renderer != null ? e.renderer.lightmapIndex : -1;
                    refreshLightmaps |= e.lightmapIndex != index; e.lightmapIndex = index;
                }
                if (refreshLightmaps) m_Lightmaps = LightmapSettings.lightmaps;
                bool cellsDirty = rebuild;
                statistics.candidates = m_Entries.Count;
                m_BatchSize = Mathf.Clamp(settings.instanceBatchSize, 1, Capacity);
                Vector3 cameraPosition = camera.transform.position;
                for (int i = 0; i < m_Entries.Count; ++i)
                {
                    var e = m_Entries[i];
                    bool eligible = Eligible(e, camera);
                    cellsDirty |= eligible != e.eligible;
                    e.eligible = eligible;
                    if (!eligible) { ++statistics.fallbackRenderers; continue; }
                    var matrix = e.renderer.localToWorldMatrix;
                    Bounds bounds = MobileInstanceCulling.TransformBounds(e.mesh.bounds, matrix, e.group.boundsPadding);
                    foreach (var lodMesh in e.group.lodMeshes)
                        bounds.Encapsulate(MobileInstanceCulling.TransformBounds(lodMesh.bounds, matrix, e.group.boundsPadding));
                    cellsDirty |= matrix != e.matrix || bounds != e.bounds;
                    e.matrix = matrix; e.bounds = bounds;
                    e.lightmapST = e.renderer.lightmapScaleOffset;
                    if (e.renderer.lightProbeUsage == LightProbeUsage.Off) e.sh = RenderSettings.ambientProbe;
                    else LightProbes.GetInterpolatedProbe(e.renderer.probeAnchor != null ? e.renderer.probeAnchor.position : bounds.center, e.renderer, out e.sh);
                    var saved = new Saved { renderer = e.renderer, shadow = e.renderer.shadowCastingMode, forceOff = e.renderer.forceRenderingOff };
                    m_Saved.Add(saved);
                    float distance = settings.instanceShadowDistance > 0 ? Mathf.Min(settings.instanceShadowDistance, shadowDistance) : shadowDistance;
                    // Keep native shadow bounds/splits even when every visible object is managed.
                    if (saved.shadow == ShadowCastingMode.On && MobileInstanceCulling.InRange(bounds, cameraPosition, distance))
                        e.renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                    else e.renderer.forceRenderingOff = true;
                }
                if (cellsDirty) BuildCells();
                Cull(camera, settings);
            }
            finally { Profiler.EndSample(); }
        }

        void BuildCells()
        {
            // Rebuilt only for dirty transforms, membership or eligibility, never scanned scene-wide.
            m_Cells.Clear();
            m_Grid.Clear();
            for (int i = 0; i < m_Entries.Count; ++i)
            {
                var e = m_Entries[i]; if (!e.eligible) continue;
                Vector3 p = e.bounds.center / Mathf.Max(1, e.group.cellSize);
                var key = (e.group, new Vector3Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z)));
                if (!m_Grid.TryGetValue(key, out var cell) || cell.entries.Count >= 256)
                {
                    if (m_CellPool.Count <= m_Cells.Count) m_CellPool.Add(new Cell());
                    cell = m_CellPool[m_Cells.Count]; cell.entries.Clear(); cell.bounds = e.bounds;
                    m_Grid[key] = cell; m_Cells.Add(cell);
                }
                cell.entries.Add(i); cell.bounds.Encapsulate(e.bounds);
            }
        }

        void Cull(Camera camera, MobileRenderSettings settings)
        {
            Profiler.BeginSample("Mobile Instances.Cull");
            try
            {
                GeometryUtility.CalculateFrustumPlanes(camera.cullingMatrix, m_Planes);
                Vector3 position = camera.transform.position;
                if (!m_Cameras.TryGetValue(camera, out var state)) { state = new CameraState(); m_Cameras.Add(camera, state); }
                bool reset = state.generation != m_Generation || Vector3.Distance(state.position, position) > 10
                    || Quaternion.Angle(state.rotation, camera.transform.rotation) > 45;
                if (state.generation != m_Generation) { state.lods = new int[m_Entries.Count]; state.generation = m_Generation; }
                if (reset) for (int i = 0; i < state.lods.Length; ++i) state.lods[i] = -1;
                state.position = position; state.rotation = camera.transform.rotation;
                m_VisibleCells.Clear(); statistics.cells = m_Cells.Count;
                foreach (var cell in m_Cells)
                {
                    var group = m_Entries[cell.entries[0]].group;
                    float distance = group.drawDistance > 0 ? group.drawDistance : settings.instanceDrawDistance;
                    if (MobileInstanceCulling.InRange(cell.bounds, position, distance > 0 ? distance : camera.farClipPlane)
                        && MobileInstanceCulling.Classify(m_Planes, cell.bounds) >= 0)
                    { cell.distance = cell.bounds.SqrDistance(position); m_VisibleCells.Add(cell); }
                }
                m_VisibleCells.Sort(s_NearFirst); statistics.visibleCells = m_VisibleCells.Count;
                foreach (var cell in m_VisibleCells)
                {
                    int classification = MobileInstanceCulling.Classify(m_Planes, cell.bounds);
                    foreach (int index in cell.entries)
                    {
                        var e = m_Entries[index];
                        float distance = e.group.drawDistance > 0 ? e.group.drawDistance : settings.instanceDrawDistance;
                        if (!MobileInstanceCulling.InRange(e.bounds, position, distance > 0 ? distance : camera.farClipPlane)
                            || (classification == 0 && MobileInstanceCulling.Classify(m_Planes, e.bounds) < 0)) continue;
                        float size = 2 * e.bounds.extents.magnitude;
                        float height = camera.orthographic ? size / (2 * camera.orthographicSize)
                            : size / (2 * Mathf.Max(camera.nearClipPlane, Vector3.Dot(e.bounds.center - position, camera.transform.forward)) * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad / 2));
                        int lod = MobileInstanceCulling.SelectLOD(height * QualitySettings.lodBias, e.group.lodScreenHeights, state.lods[index]);
                        state.lods[index] = lod;
                        Mesh mesh = lod == 0 ? e.mesh : e.group.lodMeshes[lod - 1];
                        ++statistics.visibleInstances;
                        for (int submesh = 0; submesh < e.materials.Length; ++submesh)
                        {
                            int lightmap = e.renderer.lightmapIndex;
                            if (lightmap < 0 || lightmap >= 65534) lightmap = -1;
                            var key = new Key(mesh, e.materials[submesh], submesh, lightmap);
                            if (!m_Batches.TryGetValue(key, out var batch))
                            { batch = new Batch { material = new Material(key.material) { hideFlags = HideFlags.HideAndDontSave, name = key.material.name + " (Mobile Instances)" } }; m_Batches.Add(key, batch); }
                            batch.entries.Add(index);
                        }
                    }
                }
                foreach (var pair in m_Batches)
                {
                    if (pair.Value.entries.Count == 0) continue;
                    var material = pair.Value.material;
                    if (material.shader != pair.Key.material.shader) material.shader = pair.Key.material.shader;
                    material.CopyPropertiesFromMaterial(pair.Key.material);
                    material.enableInstancing = true; material.EnableKeyword(Keyword);
                    material.SetShaderPassEnabled("MobileForward", pair.Key.material.GetShaderPassEnabled("MobileForward"));
                    material.SetShaderPassEnabled("MobileForwardOnly", pair.Key.material.GetShaderPassEnabled("MobileForwardOnly"));
                    material.SetShaderPassEnabled("MobileForwardOnlyExtended", pair.Key.material.GetShaderPassEnabled("MobileForwardOnlyExtended"));
                    material.SetShaderPassEnabled("MobileGBuffer", pair.Key.material.GetShaderPassEnabled("MobileGBuffer"));
                    material.SetShaderPassEnabled("MobileGBufferExtended", pair.Key.material.GetShaderPassEnabled("MobileGBufferExtended"));
                    CoreUtils.SetKeyword(material, "LIGHTMAP_ON", pair.Key.lightmap >= 0);
                    CoreUtils.SetKeyword(material, "DIRLIGHTMAP_COMBINED", pair.Key.lightmap >= 0 && LightmapSettings.lightmapsMode == LightmapsMode.CombinedDirectional);
                }
            }
            finally { Profiler.EndSample(); }
        }

        internal void PrepareLighting(CullingResults culling, int mainLight, int[] localIndices, int localCount, int limit)
        {
            var lights = culling.visibleLights;
            foreach (var e in m_Entries)
            {
                if (!e.eligible || m_Saved.Count == 0) continue;
                e.lights = new Vector4(-1, -1, -1, -1);
                for (int i = 0; i < 4; ++i) m_LightScores[i] = float.NegativeInfinity;
                int layer = 1 << e.renderer.gameObject.layer;
                e.info = new Vector4(mainLight >= 0 && (lights[mainLight].light.cullingMask & layer) != 0 ? 1 : 0, 0, 0, 0);
                for (int i = 0; i < localCount; ++i)
                {
                    var light = lights[localIndices[i]];
                    if ((light.light.cullingMask & layer) == 0) continue;
                    float distance = e.bounds.SqrDistance(light.localToWorldMatrix.GetColumn(3));
                    if (distance > light.range * light.range) continue;
                    if (light.lightType == LightType.Spot)
                    {
                        Vector3 delta = e.bounds.center - (Vector3)light.localToWorldMatrix.GetColumn(3);
                        float depth = Vector3.Dot(delta, ((Vector3)light.localToWorldMatrix.GetColumn(2)).normalized);
                        float radius = e.bounds.extents.magnitude;
                        float angle = light.spotAngle * Mathf.Deg2Rad * 0.5f;
                        float radial = Mathf.Sqrt(Mathf.Max(0, delta.sqrMagnitude - depth * depth));
                        if (depth + radius < 0 || depth - radius > light.range
                            || radial > Mathf.Max(0, depth) * Mathf.Tan(angle) + radius / Mathf.Max(0.001f, Mathf.Cos(angle))) continue;
                    }
                    float score = light.finalColor.maxColorComponent / Mathf.Max(0.01f, distance);
                    for (int slot = 0; slot < Mathf.Clamp(limit, 0, 4); ++slot)
                    {
                        if (score <= m_LightScores[slot]) continue;
                        for (int shift = 3; shift > slot; --shift) { m_LightScores[shift] = m_LightScores[shift - 1]; e.lights[shift] = e.lights[shift - 1]; }
                        m_LightScores[slot] = score; e.lights[slot] = i; break;
                    }
                }
            }
        }

        internal int DeferredLightingVariantMask(bool extended)
        {
            int mask = 0;
            foreach (var batch in m_Batches.Values)
                if (batch.entries.Count != 0)
                    mask |= MobileDeferredMaterial.LightingVariantMask(batch.material, extended);
            return mask;
        }

        internal void Draw(CommandBuffer cmd, string passName)
        {
            Profiler.BeginSample("Mobile Instances.Submit");
            try
            {
                foreach (var pair in m_Batches)
                {
                    var key = pair.Key; var batch = pair.Value; var material = batch.material;
                    if (batch.entries.Count == 0 || !material.GetShaderPassEnabled(passName)) continue;
                    int pass = material.FindPass(passName); if (pass < 0) continue;
                    for (int start = 0; start < batch.entries.Count; start += m_BatchSize)
                    {
                        int count = Mathf.Min(m_BatchSize, batch.entries.Count - start);
                        for (int i = 0; i < count; ++i)
                        {
                            var e = m_Entries[batch.entries[start + i]];
                            m_Matrices[i] = e.matrix; m_Lights[i] = e.lights; m_Info[i] = e.info; m_ST[i] = e.lightmapST;
                            for (int c = 0; c < 3; ++c)
                            {
                                m_SH[c][i] = new Vector4(e.sh[c, 3], e.sh[c, 1], e.sh[c, 2], e.sh[c, 0] - e.sh[c, 6]);
                                m_SH[c + 3][i] = new Vector4(e.sh[c, 4], e.sh[c, 5], 3 * e.sh[c, 6], e.sh[c, 7]);
                            }
                            m_SH[6][i] = new Vector4(e.sh[0, 8], e.sh[1, 8], e.sh[2, 8], 1);
                        }
                        m_Block.Clear();
                        m_Block.SetVectorArray(LightsId, m_Lights); m_Block.SetVectorArray(InfoId, m_Info); m_Block.SetVectorArray(STId, m_ST);
                        for (int i = 0; i < 7; ++i) m_Block.SetVectorArray(SHIds[i], m_SH[i]);
                        if (key.lightmap >= 0)
                        {
                            var lm = m_Lightmaps[key.lightmap];
                            m_Block.SetTexture("unity_Lightmap", lm.lightmapColor);
                            if (lm.lightmapDir != null) m_Block.SetTexture("unity_LightmapInd", lm.lightmapDir);
                        }
                        m_Block.SetTexture("_MobileInstanceDefaultReflection", ReflectionProbe.defaultTexture != null ? ReflectionProbe.defaultTexture : CoreUtils.blackCubeTexture);
                        m_Block.SetVector("_MobileInstanceDefaultReflectionHDR", ReflectionProbe.defaultTextureHDRDecodeValues);
                        cmd.DrawMeshInstanced(key.mesh, key.submesh, material, pass, m_Matrices, count, m_Block);
                        ++statistics.draws;
                        statistics.uploadedBytes += Capacity * 160 + count * 128; // property arrays + object/inverse matrices
                    }
                }
            }
            finally { Profiler.EndSample(); }
        }

        internal void Restore()
        {
            foreach (var saved in m_Saved)
                if (saved.renderer != null) { saved.renderer.shadowCastingMode = saved.shadow; saved.renderer.forceRenderingOff = saved.forceOff; }
            m_Saved.Clear();
        }
        internal void EndCamera()
        { Restore(); MobileInstanceDiagnostics.lastCamera = statistics; }
        internal void Prune()
        {
            m_Expired.Clear();
            foreach (var pair in m_Cameras) if (pair.Key == null || !pair.Key.gameObject.activeInHierarchy) m_Expired.Add(pair.Key);
            foreach (var camera in m_Expired) m_Cameras.Remove(camera);
        }
        public void Dispose()
        {
            Restore();
            foreach (var batch in m_Batches.Values) CoreUtils.Destroy(batch.material);
            m_Batches.Clear(); m_Cameras.Clear(); m_Entries.Clear(); m_Cells.Clear(); m_Revisions.Clear();
        }
    }
}
