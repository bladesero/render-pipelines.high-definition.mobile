using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Explicit exposure history control for camera cuts and teleports.</summary>
    public static class MobileExposureControl
    {
        internal static MobileExposureHistory active;
        /// <summary>Reset the selected camera on its next Mobile render.</summary>
        public static void Reset(Camera camera) { active?.Remove(camera); }
        internal static RenderTexture GetExposureTexture(Camera camera) => active?.GetTexture(camera);
    }

    internal sealed class MobileExposureHistory : IDisposable
    {
        internal sealed class State : IDisposable
        {
            internal RTHandle read, write;
            internal bool initialized;
            internal ExposureMode mode;
            internal float lastTime;
            internal int frame;
            public void Dispose() { read?.Release(); write?.Release(); }
        }
        readonly Dictionary<Camera, State> m_States = new Dictionary<Camera, State>();
        readonly List<Camera> m_Expired = new List<Camera>();
        internal RenderTexture GetTexture(Camera camera) => camera != null && m_States.TryGetValue(camera, out var state) ? state.read.rt : null;
        internal void Remove(Camera camera)
        {
            if (ReferenceEquals(camera, null) || !m_States.TryGetValue(camera, out var state)) return;
            state.Dispose(); m_States.Remove(camera);
        }
        internal void Prune()
        {
            m_Expired.Clear();
            foreach (var pair in m_States)
                if (pair.Key == null || !pair.Key.gameObject.activeInHierarchy || (Application.isPlaying && !pair.Key.enabled && pair.Value.frame < Time.frameCount - 1)) m_Expired.Add(pair.Key);
            foreach (var camera in m_Expired) Remove(camera);
        }
        internal State Prepare(Camera camera, Exposure e, bool control)
        {
            bool automatic = control && MobileExposure.IsAutomatic(e);
#if UNITY_EDITOR
            automatic &= !(camera.cameraType == CameraType.SceneView && HDAdditionalSceneViewSettings.sceneExposureOverriden);
#endif
            if (!automatic) { Remove(camera); return null; }
            // Editor cameras repaint on demand; skipped editor frames are not camera cuts.
            if (m_States.TryGetValue(camera, out var old) && (old.mode != e.mode.value || (Application.isPlaying && old.frame < Time.frameCount - 1))) Remove(camera);
            if (!m_States.TryGetValue(camera, out var state))
            {
                state = new State { mode = e.mode.value, lastTime = Time.realtimeSinceStartup };
                try
                {
                    state.read = RTHandles.Alloc(1, 1, colorFormat: GraphicsFormat.R32G32_SFloat, filterMode: FilterMode.Point, name: "Mobile Exposure A");
                    state.write = RTHandles.Alloc(1, 1, colorFormat: GraphicsFormat.R32G32_SFloat, filterMode: FilterMode.Point, name: "Mobile Exposure B");
                    m_States.Add(camera, state);
                }
                catch { state.Dispose(); throw; }
            }
            state.frame = Time.frameCount;
            return state;
        }
        internal static void Initialize(CommandBuffer cmd, State state, Exposure e)
        {
            if (state == null || state.initialized) return;
            float ev = Mathf.Clamp(e.fixedExposure.value - e.compensation.value, Mathf.Min(e.limitMin.value, e.limitMax.value), Mathf.Max(e.limitMin.value, e.limitMax.value));
            cmd.SetRenderTarget(state.read);
            cmd.ClearRenderTarget(false, true, new Color(MobileExposure.FromEV100(ev, 0, MobileExposure.LensScale), ev, 0, 0));
            state.initialized = true;
        }
        class PassData
        {
            internal TextureHandle source, previous;
            internal Material material;
            internal Vector4 meter, adaptation, dimensions;
            internal int pass, size;
        }
        internal void Record(RenderGraph graph, TextureHandle color, State state, Exposure e, Material material, int resolution)
        {
            if (state == null) return;
            int size = resolution <= 64 ? 64 : resolution >= 256 ? 256 : 128;
            var previous = graph.ImportTexture(state.read);
            float calibration = e.targetMidGray.value == TargetMidGray.Grey18 ? 18f : e.targetMidGray.value == TargetMidGray.Grey14 ? 14f : 12.5f;
            var meter = new Vector4(e.compensation.value, Mathf.Min(e.limitMin.value, e.limitMax.value), Mathf.Max(e.limitMin.value, e.limitMax.value), calibration);
            float now = Time.realtimeSinceStartup;
            float delta = Application.isPlaying ? Time.deltaTime : Mathf.Clamp(now - state.lastTime, 0f, 0.1f);
            state.lastTime = now;
            var adaptation = new Vector4(e.adaptationSpeedDarkToLight.value, e.adaptationSpeedLightToDark.value, delta, e.adaptationMode.value == AdaptationMode.Progressive ? 1 : 0);
            var source = color;
            int sourceSize = size;
            for (int stage = 0; ; stage++)
            {
                bool final = size == 1 && stage > 0;
                var output = final ? graph.ImportTexture(state.write) : graph.CreateTexture(new TextureDesc(size, size) { colorFormat = GraphicsFormat.R32G32_SFloat, filterMode = FilterMode.Point, name = "Mobile Exposure Meter " + size });
                using (var builder = graph.AddRenderPass<PassData>(final ? "Mobile Exposure Adapt" : "Mobile Exposure Reduce", out var pass))
                {
                    pass.source = builder.ReadTexture(source); pass.previous = builder.ReadTexture(previous);
                    builder.UseColorBuffer(output, 0);
                    pass.material = material; pass.pass = stage == 0 ? 3 : final ? 5 : 4; pass.size = size;
                    pass.meter = meter; pass.adaptation = adaptation;
                    pass.dimensions = new Vector4(1f / sourceSize, 1f / sourceSize, e.meteringMode.value == MeteringMode.Average ? 0 : 1, MobileExposure.LensScale);
                    builder.SetRenderFunc((PassData data, RenderGraphContext ctx) =>
                    {
                        ctx.cmd.SetViewport(new Rect(0, 0, data.size, data.size));
                        ctx.cmd.SetGlobalTexture("_MobileSource", data.source);
                        ctx.cmd.SetGlobalTexture("_MobileExposureTexture", data.previous);
                        ctx.cmd.SetGlobalVector("_MobileMeterParams", data.meter);
                        ctx.cmd.SetGlobalVector("_MobileAdaptation", data.adaptation);
                        ctx.cmd.SetGlobalVector("_MobileMeterSize", data.dimensions);
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, data.material, data.pass, MeshTopology.Triangles, 3);
                    });
                }
                if (final) break;
                source = output; sourceSize = size; size /= 2;
            }
        }
        internal static void Commit(State state)
        {
            if (state == null) return;
            var swap = state.read; state.read = state.write; state.write = swap;
        }
        public void Dispose()
        {
            foreach (var state in m_States.Values) state.Dispose();
            m_States.Clear();
            if (MobileExposureControl.active == this) MobileExposureControl.active = null;
        }
    }
}
