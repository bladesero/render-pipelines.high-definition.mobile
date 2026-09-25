using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Refresh the Mobile environment after updating a sky texture in place.</summary>
    public static class MobileEnvironmentControl
    {
        internal static MobileEnvironmentCache active;
        public static void Reset(Camera camera) => active?.Remove(camera);
    }
    // No SkyManager, compute shaders, GPU readback or RenderSettings mutation.
    // Commands run before the camera RenderGraph; each camera owns its textures.
    internal sealed class MobileEnvironmentCache : IDisposable
    {
        internal sealed class State : IDisposable
        {
            internal RenderTexture diffuse, reflection, pendingDiffuse, pendingReflection, source;
            internal int size, mips, completedHash, pendingHash, cursor = -1, generation, frame;
            internal bool ready;
            internal MobileSky sky;
            internal Vector4 sunDirection, sunColor;
            internal int totalJobs => 6 + mips * 6;
            public void Dispose()
            {
                foreach (var texture in new[] { diffuse, reflection, pendingDiffuse, pendingReflection, source })
                    if (texture != null) { texture.Release(); CoreUtils.Destroy(texture); }
            }
        }
        readonly Dictionary<Camera, State> m_States = new Dictionary<Camera, State>();
        readonly List<Camera> m_Expired = new List<Camera>();
        internal State Get(Camera camera) => camera != null && m_States.TryGetValue(camera, out var value) ? value : null;
        internal void Remove(Camera camera)
        {
            if (ReferenceEquals(camera, null) || !m_States.TryGetValue(camera, out var state)) return;
            state.Dispose(); m_States.Remove(camera);
        }
        internal void Prune()
        {
            m_Expired.Clear();
            foreach (var pair in m_States)
                if (pair.Key == null || !pair.Key.gameObject.activeInHierarchy || Time.frameCount - pair.Value.frame > 120) m_Expired.Add(pair.Key);
            foreach (var camera in m_Expired) Remove(camera);
        }
        static RenderTexture Cube(int size, bool mip, string name)
        {
            var texture = new RenderTexture(size, size, 0)
            {
                name = name, hideFlags = HideFlags.HideAndDontSave, dimension = TextureDimension.Cube,
                graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat, useMipMap = mip, autoGenerateMips = false,
                filterMode = mip ? FilterMode.Trilinear : FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            if (!texture.Create()) { CoreUtils.Destroy(texture); throw new InvalidOperationException("Cannot allocate Mobile environment cubemap."); }
            return texture;
        }
        internal static int Hash(MobileSky sky, Vector4 direction, Vector4 color)
        {
            unchecked
            {
                int h = sky.mode;
                h = h * 31 + (sky.cube != null ? sky.cube.GetInstanceID() : 0);
                h = h * 31 + (sky.cube != null ? (int)sky.cube.updateCount : 0);
                h = h * 31 + sky.top.GetHashCode(); h = h * 31 + sky.middle.GetHashCode(); h = h * 31 + sky.bottom.GetHashCode();
                h = h * 31 + sky.tint.GetHashCode(); h = h * 31 + sky.intensity.GetHashCode(); h = h * 31 + sky.rotation.GetHashCode();
                h = h * 31 + sky.diffusion.GetHashCode(); h = h * 31 + sky.sunSize.GetHashCode();
                if (sky.mode == 4)
                {
                    h = h * 31 + sky.horizonTint.GetHashCode(); h = h * 31 + sky.zenithTint.GetHashCode();
                    h = h * 31 + sky.physicalParameters.GetHashCode();
                }
                if (sky.mode == 3 || sky.mode == 4) { h = h * 31 + direction.GetHashCode(); h = h * 31 + color.GetHashCode(); }
                return h;
            }
        }
        internal static void BindSky(CommandBuffer cmd, MobileSky sky, Vector4 direction, Vector4 color)
        {
            cmd.SetGlobalTexture("_MobileSky", sky.cube != null ? sky.cube : CoreUtils.blackCubeTexture);
            cmd.SetGlobalFloat("_MobileSkyIntensity", sky.intensity);
            cmd.SetGlobalVector("_MobileSkyOptions", new Vector4(sky.mode, Mathf.Cos(sky.rotation), Mathf.Sin(sky.rotation), sky.diffusion));
            cmd.SetGlobalFloat("_MobileSkySunSize", sky.sunSize);
            cmd.SetGlobalColor("_MobileSkyTint", sky.tint); cmd.SetGlobalColor("_MobileSkyTop", sky.top);
            cmd.SetGlobalColor("_MobileSkyMiddle", sky.middle); cmd.SetGlobalColor("_MobileSkyBottom", sky.bottom);
            cmd.SetGlobalColor("_MobileSkyHorizonTint", sky.horizonTint); cmd.SetGlobalColor("_MobileSkyZenithTint", sky.zenithTint);
            cmd.SetGlobalVector("_MobileSkyPhysicalParameters", sky.physicalParameters);
            cmd.SetGlobalVector("_MobileMainDirection", direction); cmd.SetGlobalVector("_MobileMainColor", color);
        }
        internal State Prepare(CommandBuffer cmd, Camera camera, MobileSky sky, Vector4 direction, Vector4 color, MobileRenderSettings settings, Material material)
        {
            if (!settings.environmentLighting || sky.mode == 0) { Remove(camera); return null; }
            int size = settings.environmentResolution == MobileEnvironmentResolution.High ? 64 : 32;
            var state = Get(camera);
            if (state != null && state.size != size) { Remove(camera); state = null; }
            if (state == null)
            {
                state = new State { size = size, mips = (int)Mathf.Log(size, 2) + 1 };
                try
                {
                    state.source = Cube(size, true, "Mobile Environment Source");
                    state.diffuse = Cube(8, false, "Mobile Irradiance A"); state.pendingDiffuse = Cube(8, false, "Mobile Irradiance B");
                    state.reflection = Cube(size, true, "Mobile Reflection A"); state.pendingReflection = Cube(size, true, "Mobile Reflection B");
                    m_States.Add(camera, state);
                }
                catch { state.Dispose(); throw; }
            }
            state.frame = Time.frameCount;
            int hash = Hash(sky, direction, color);
            if (state.cursor < 0 && state.ready && state.completedHash == hash) return state;
            cmd.BeginSample("Mobile Environment Update");
            if (state.cursor < 0)
            {
                state.sky = sky; state.sunDirection = direction; state.sunColor = color; state.pendingHash = hash;
                BindSky(cmd, sky, direction, color);
                // Capture all six faces together. Subsequent filters see an immutable snapshot,
                // even when a weather animation changes the requested sky every frame.
                for (int face = 0; face < 6; face++)
                {
                    cmd.SetRenderTarget(state.source, 0, (CubemapFace)face);
                    cmd.SetViewport(new Rect(0, 0, size, size));
                    cmd.SetGlobalVector("_MobileEnvironmentFilter", new Vector4(face, 0, size, 0));
                    cmd.DrawProcedural(Matrix4x4.identity, material, 1, MeshTopology.Triangles, 3);
                }
                cmd.GenerateMips(state.source); state.cursor = 0;
            }
            cmd.SetGlobalTexture("_MobileEnvironmentSource", state.source);
            // Warm up once, then amortize updates. Publication is atomic at a complete generation.
            int budget = state.ready ? Mathf.Clamp(settings.environmentJobsPerFrame, 1, 48) : state.totalJobs;
            for (int i = 0; i < budget && state.cursor < state.totalJobs; i++, state.cursor++)
            {
                bool diffuse = state.cursor < 6;
                int job = diffuse ? state.cursor : state.cursor - 6;
                int mip = diffuse ? 0 : job / 6;
                int face = job % 6, width = diffuse ? 8 : Mathf.Max(1, size >> mip);
                cmd.SetRenderTarget(diffuse ? state.pendingDiffuse : state.pendingReflection, mip, (CubemapFace)face);
                cmd.SetViewport(new Rect(0, 0, width, width));
                cmd.SetGlobalVector("_MobileEnvironmentFilter", new Vector4(face, (float)mip / (state.mips - 1), size, 0));
                cmd.DrawProcedural(Matrix4x4.identity, material, diffuse ? 2 : 3, MeshTopology.Triangles, 3);
            }
            if (state.cursor == state.totalJobs)
            {
                var swap = state.diffuse; state.diffuse = state.pendingDiffuse; state.pendingDiffuse = swap;
                swap = state.reflection; state.reflection = state.pendingReflection; state.pendingReflection = swap;
                state.completedHash = state.pendingHash; state.cursor = -1; state.ready = true; state.generation++;
            }
            cmd.EndSample("Mobile Environment Update");
            return state;
        }
        public void Dispose()
        {
            foreach (var state in m_States.Values) state.Dispose(); m_States.Clear();
            if (MobileEnvironmentControl.active == this) MobileEnvironmentControl.active = null;
        }
    }
}
