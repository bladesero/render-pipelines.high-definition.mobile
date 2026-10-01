using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.HighDefinition
{
    internal sealed partial class MobileRenderPipelineBackend
    {
        struct SgsrRendererState
        {
            internal Matrix4x4 matrix;
            internal Bounds bounds;
            internal int seen;
            internal SgsrSkinPose skin;
        }

        sealed class SgsrSkinPose
        {
            internal Transform[] bones;
            internal Matrix4x4[] matrices;
            internal Mesh mesh;
            internal float[] weights;

            internal bool Update(SkinnedMeshRenderer renderer)
            {
                bool changed = false;
                if (bones == null || mesh != renderer.sharedMesh)
                {
                    bones = renderer.bones;
                    matrices = new Matrix4x4[bones.Length];
                    mesh = renderer.sharedMesh;
                    weights = new float[mesh == null ? 0 : mesh.blendShapeCount];
                    changed = true;
                }
                for (int i = 0; i < bones.Length; i++)
                {
                    var matrix = bones[i] == null ? Matrix4x4.identity : bones[i].localToWorldMatrix;
                    changed |= matrix != matrices[i];
                    matrices[i] = matrix;
                }
                for (int i = 0; i < weights.Length; i++)
                {
                    float weight = renderer.GetBlendShapeWeight(i);
                    changed |= weight != weights[i];
                    weights[i] = weight;
                }
                return changed;
            }
        }

        sealed class SgsrHistory
        {
            internal Camera camera;
            internal readonly Dictionary<Renderer, SgsrRendererState> renderers = new Dictionary<Renderer, SgsrRendererState>();
            internal RTHandle a, b, inputA, inputB;
            internal Vector2 previousJitter;
            internal Matrix4x4 vp, projection;
            internal Vector3 position;
            internal Quaternion rotation;
            internal int frame, lastFrame = -1;
            internal Vector2Int renderSize;
            internal MobileUpscalingMode mode;
            internal bool half, written;
            internal void Release() { a?.Release(); b?.Release(); inputA?.Release(); inputB?.Release(); a = b = inputA = inputB = null; }
        }

        readonly Dictionary<Camera, SgsrHistory> m_SgsrHistory = new Dictionary<Camera, SgsrHistory>();
        readonly List<Camera> m_SgsrExpired = new List<Camera>();
        Renderer[] m_SgsrRenderers;
        int m_SgsrRendererFrame = -1;
        Material m_SgsrMaterial;

        internal static float SgsrHalton(int index, int radix)
        {
            float value = 0, fraction = 1;
            while (index > 0) { fraction /= radix; value += fraction * (index % radix); index /= radix; }
            return value;
        }

        SgsrHistory PrepareSgsr(Camera camera, int width, int height)
        {
            foreach (var entry in m_SgsrHistory)
                if (entry.Key == null || Time.frameCount - entry.Value.lastFrame > 120)
                    m_SgsrExpired.Add(entry.Key);
            foreach (var key in m_SgsrExpired) { m_SgsrHistory[key].Release(); m_SgsrHistory.Remove(key); }
            m_SgsrExpired.Clear();
            var settings = m_Asset.mobileSettings;
            var shader = m_Asset.mobileResources.sgsr2Shader;
            // Integer guides are fetched with Load. Unity 2022 reports Sample=false for this format.
            bool enabled = camera.cameraType == CameraType.Game && !camera.orthographic
                && settings.upscalingMode != MobileUpscalingMode.Bilinear
                && shader != null && shader.isSupported && SystemInfo.supportedRenderTargetCount >= 2
                && SystemInfo.usesReversedZBuffer && width <= camera.pixelWidth && height <= camera.pixelHeight
                && (settings.upscalingMode != MobileUpscalingMode.SGSR2Ultra
                    || SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_UInt, FormatUsage.Render));
            if (!enabled)
            {
                if (m_SgsrHistory.TryGetValue(camera, out var old)) { old.Release(); m_SgsrHistory.Remove(camera); }
                return null;
            }
            if (m_SgsrMaterial == null || m_SgsrMaterial.shader != shader)
            {
                CoreUtils.Destroy(m_SgsrMaterial);
                m_SgsrMaterial = CoreUtils.CreateEngineMaterial(shader);
            }
            if (!m_SgsrHistory.TryGetValue(camera, out var state))
                m_SgsrHistory.Add(camera, state = new SgsrHistory { camera = camera });
            bool resize = state.a == null || state.a.rt.width != camera.pixelWidth || state.a.rt.height != camera.pixelHeight
                || state.inputA == null || state.inputA.rt.width != width || state.inputA.rt.height != height;
            if (resize)
            {
                state.Release();
                state.a = RTHandles.Alloc(camera.pixelWidth, camera.pixelHeight, colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                    filterMode: FilterMode.Bilinear, name: "Mobile SGSR2 History A");
                state.b = RTHandles.Alloc(camera.pixelWidth, camera.pixelHeight, colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                    filterMode: FilterMode.Bilinear, name: "Mobile SGSR2 History B");
            }
            if (state.inputA == null)
            {
                state.inputA = RTHandles.Alloc(width, height, colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                    filterMode: FilterMode.Bilinear, name: "Mobile SGSR2 Scene History A");
                state.inputB = RTHandles.Alloc(width, height, colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                    filterMode: FilterMode.Bilinear, name: "Mobile SGSR2 Scene History B");
            }
            if (resize || state.renderSize != new Vector2Int(width, height) || state.mode != settings.upscalingMode
                || state.half != settings.sgsr2HalfPrecision || (state.lastFrame != Time.frameCount - 1 && state.lastFrame != Time.frameCount)
                || state.projection != camera.projectionMatrix || Vector3.Distance(state.position, camera.transform.position) > 2f
                || Quaternion.Angle(state.rotation, camera.transform.rotation) > 30f
                || !state.a.rt.IsCreated() || !state.b.rt.IsCreated() || !state.inputA.rt.IsCreated() || !state.inputB.rt.IsCreated())
            {
                state.frame = 0;
            }
            state.written = false;
            state.a.rt.Create(); state.b.rt.Create(); state.inputA.rt.Create(); state.inputB.rt.Create();
            state.renderSize = new Vector2Int(width, height);
            state.mode = settings.upscalingMode; state.half = settings.sgsr2HalfPrecision;
            return state;
        }

        class SgsrPassData
        {
            internal SgsrHistory history;
            internal TextureHandle color, depth, previous, guides, currentInput, previousInput, motionMask;
            internal Rect[] motionRects;
            internal Material material;
            internal Vector4 render, display, jitter, parameters, previousJitter;
            internal Matrix4x4 reprojection;
            internal bool still, half, ultra, reset;
            internal int width, height, pass;
            // RenderGraph pools PassData without clearing its fields between passes/frames.
            internal void Reset()
            {
                history = null; color = depth = previous = guides = currentInput = previousInput = motionMask = default;
                motionRects = null;
                material = null; render = display = jitter = parameters = previousJitter = Vector4.zero;
                reprojection = Matrix4x4.identity;
                still = half = ultra = reset = false; width = height = pass = 0;
            }
        }

        static void AddSgsrMotionRect(List<Rect> rects, Bounds bounds, Matrix4x4 vp, int width, int height)
        {
            Vector3 min = bounds.min, max = bounds.max;
            float left = 1, bottom = 1, right = 0, top = 0;
            int behind = 0;
            for (int i = 0; i < 8; i++)
            {
                var p = new Vector4((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y,
                    (i & 4) == 0 ? min.z : max.z, 1);
                Vector4 clip = vp * p;
                if (clip.w <= 1e-5f) { behind++; continue; }
                float x = clip.x / clip.w * 0.5f + 0.5f, y = clip.y / clip.w * 0.5f + 0.5f;
                left = Mathf.Min(left, x); right = Mathf.Max(right, x);
                bottom = Mathf.Min(bottom, y); top = Mathf.Max(top, y);
            }
            if (behind == 8) return;
            if (behind > 0) { rects.Add(new Rect(0, 0, width, height)); return; }
            int x0 = Mathf.Clamp(Mathf.FloorToInt(left * width) - 2, 0, width);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(right * width) + 2, 0, width);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(bottom * height) - 2, 0, height);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(top * height) + 2, 0, height);
            if (x1 > x0 && y1 > y0) rects.Add(new Rect(x0, y0, x1 - x0, y1 - y0));
        }

        TextureHandle RecordSgsrMotionMask(Camera camera, SgsrHistory state, Matrix4x4 projection, int width, int height)
        {
            var rects = new List<Rect>();
            var vp = projection * camera.worldToCameraMatrix; // Logical viewport coordinates, without jitter.
            // Share discovery across cameras in a player frame. Editor manual camera renders
            // can change the scene without advancing Time.frameCount.
            if (!Application.isPlaying || m_SgsrRendererFrame != Time.frameCount)
            {
                m_SgsrRenderers = Application.isPlaying ? Object.FindObjectsOfType<Renderer>() : Resources.FindObjectsOfTypeAll<Renderer>();
                m_SgsrRendererFrame = Time.frameCount;
            }
            var loaded = m_SgsrRenderers;
            foreach (var renderer in loaded)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy
                    || !renderer.gameObject.scene.IsValid() || !renderer.gameObject.scene.isLoaded
                    || (camera.cullingMask & (1 << renderer.gameObject.layer)) == 0) continue;
#if UNITY_EDITOR
                if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(camera.scene)
                    && renderer.gameObject.scene != camera.scene) continue;
#endif
                var current = new SgsrRendererState { matrix = renderer.localToWorldMatrix, bounds = renderer.bounds, seen = state.frame };
                bool hadPrevious = state.renderers.TryGetValue(renderer, out var previous);
                bool deforming = false;
                if (renderer is SkinnedMeshRenderer skin)
                {
                    current.skin = previous.skin ?? new SgsrSkinPose();
                    deforming = current.skin.Update(skin);
                }
                else if (renderer is ParticleSystemRenderer particles)
                {
                    var system = particles.GetComponent<ParticleSystem>();
                    deforming = system != null && system.isPlaying;
                }
                if (state.frame > 0 && (!hadPrevious || current.matrix != previous.matrix
                    || current.bounds.center != previous.bounds.center || current.bounds.size != previous.bounds.size || deforming))
                {
                    if (hadPrevious) AddSgsrMotionRect(rects, previous.bounds, vp, width, height);
                    AddSgsrMotionRect(rects, current.bounds, vp, width, height);
                }
                state.renderers[renderer] = current;
            }
            var expired = new List<Renderer>();
            foreach (var entry in state.renderers)
                if (entry.Key == null || entry.Value.seen != state.frame)
                {
                    if (state.frame > 0) AddSgsrMotionRect(rects, entry.Value.bounds, vp, width, height);
                    expired.Add(entry.Key);
                }
            foreach (var renderer in expired) state.renderers.Remove(renderer);
            if (rects.Count == 0) return m_Graph.defaultResources.blackTexture;
            var mask = m_Graph.CreateTexture(new TextureDesc(width, height) {
                name = "Mobile SGSR2 Object Reactivity", colorFormat = GraphicsFormat.R8_UNorm,
                clearBuffer = true, clearColor = Color.clear });
            using (var builder = m_Graph.AddRenderPass<SgsrPassData>("Mobile SGSR2 Object Reactivity", out var data))
            {
                data.Reset(); data.material = m_SgsrMaterial; data.pass = 4; data.motionRects = rects.ToArray();
                builder.UseColorBuffer(mask, 0);
                builder.SetRenderFunc((SgsrPassData d, RenderGraphContext ctx) =>
                {
#if UNITY_EDITOR
                    if (!UnityEditor.ShaderUtil.IsPassCompiled(d.material, d.pass))
                        UnityEditor.ShaderUtil.CompilePass(d.material, d.pass, true);
#endif
                    foreach (var rect in d.motionRects)
                    {
                        ctx.cmd.SetViewport(rect);
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, d.pass, MeshTopology.Triangles, 3);
                    }
                });
            }
            return mask;
        }
        TextureHandle RecordSgsr(Camera camera, TextureHandle color, TextureHandle depth, SgsrHistory state,
            Matrix4x4 projection, Vector2 jitter)
        {
            int width = state.renderSize.x, height = state.renderSize.y;
            var vp = GL.GetGPUProjectionMatrix(projection, true) * camera.worldToCameraMatrix;
            bool still = state.frame > 0 && vp == state.vp;

            var reprojection = state.frame > 0 ? state.vp * vp.inverse : Matrix4x4.identity;
            var parameters = new Vector4(1, Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f) * width / height,
                0, m_Asset.mobileSettings.sgsr2Sharpness);
            // Keep unaccumulated scene color/depth at render resolution. Accumulated history cannot
            // detect a moving object when it remains inside the current neighborhood color box.
            var currentInput = m_Graph.ImportTexture(state.inputB);
            using (var builder = m_Graph.AddRenderPass<SgsrPassData>("Mobile SGSR2 Scene History", out var data))
            {
                data.Reset();
                data.color = builder.ReadTexture(color); data.depth = builder.ReadTexture(depth);
                data.material = m_SgsrMaterial; data.width = width; data.height = height; data.pass = 3;
                data.render = new Vector4(width, height, 1f / width, 1f / height);
                builder.UseColorBuffer(currentInput, 0);
                builder.SetRenderFunc((SgsrPassData d, RenderGraphContext ctx) => DrawSgsr(d, ctx));
            }
            var motionMask = RecordSgsrMotionMask(camera, state, projection, width, height);
            TextureHandle guides = default;
            if (state.mode == MobileUpscalingMode.SGSR2Ultra)
            {
                guides = m_Graph.CreateTexture(new TextureDesc(width, height) {
                    name = "Mobile SGSR2 Guides", colorFormat = GraphicsFormat.R32G32B32A32_UInt });
                using (var builder = m_Graph.AddRenderPass<SgsrPassData>("Mobile SGSR2 Guides", out var data))
                {
                    data.Reset();
                    data.color = builder.ReadTexture(color); data.depth = builder.ReadTexture(depth);
                    data.material = m_SgsrMaterial; data.render = new Vector4(width, height, 1f / width, 1f / height);
                    data.parameters = parameters; data.reprojection = reprojection;
                    data.still = still; data.half = state.half; data.ultra = true;
                    data.width = width; data.height = height; data.pass = 2;
                    builder.UseColorBuffer(guides, 0);
                    builder.SetRenderFunc((SgsrPassData d, RenderGraphContext ctx) => DrawSgsr(d, ctx));
                }
            }
            var result = m_Graph.CreateTexture(new TextureDesc(camera.pixelWidth, camera.pixelHeight) {
                name = "Mobile SGSR2 Color", colorFormat = GraphicsFormat.R16G16B16A16_SFloat, filterMode = FilterMode.Bilinear });
            var previous = m_Graph.ImportTexture(state.a);
            var next = m_Graph.ImportTexture(state.b);
            using (var builder = m_Graph.AddRenderPass<SgsrPassData>("Mobile SGSR2 Upscale", out var data))
            {
                data.Reset();
                data.color = builder.ReadTexture(color); data.depth = builder.ReadTexture(depth);
                // On reset bind black instead of uninitialized or lost history.
                data.previous = builder.ReadTexture(state.frame == 0 ? m_Graph.defaultResources.blackTexture : previous);
                data.currentInput = builder.ReadTexture(currentInput);
                data.motionMask = builder.ReadTexture(motionMask);
                data.previousInput = builder.ReadTexture(state.frame == 0 ? m_Graph.defaultResources.blackTexture : m_Graph.ImportTexture(state.inputA));
                data.previousJitter = new Vector4(state.previousJitter.x, state.previousJitter.y, 0, 0);
                if (guides.IsValid()) data.guides = builder.ReadTexture(guides);
                data.material = m_SgsrMaterial; data.render = new Vector4(width, height, 1f / width, 1f / height);
                data.width = camera.pixelWidth; data.height = camera.pixelHeight;
                data.display = new Vector4(data.width, data.height, 1f / data.width, 1f / data.height);
                data.jitter = new Vector4(jitter.x, SystemInfo.graphicsUVStartsAtTop ? -jitter.y : jitter.y, 0, 0);
                data.parameters = parameters; data.reprojection = reprojection; data.still = still;
                data.half = state.half; data.ultra = state.mode == MobileUpscalingMode.SGSR2Ultra; data.reset = state.frame == 0;
                data.history = state;
                builder.UseColorBuffer(result, 0); builder.UseColorBuffer(next, 1);
                // RenderGraph 14 requires a depth attachment for MRT, even with ZWrite Off.
                var mrtDepth = builder.CreateTransientTexture(new TextureDesc(data.width, data.height) {
                    name = "Mobile SGSR2 MRT Depth", depthBufferBits = DepthBits.Depth16 });
                builder.UseDepthBuffer(mrtDepth, DepthAccess.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((SgsrPassData d, RenderGraphContext ctx) => DrawSgsr(d, ctx));
            }
            return result;
        }

        static void DrawSgsr(SgsrPassData d, RenderGraphContext ctx)
        {
            CoreUtils.SetKeyword(d.material, "SGSR_FP16", d.half);
            CoreUtils.SetKeyword(d.material, "SGSR_LITE", !d.ultra);
            CoreUtils.SetKeyword(d.material, "SGSR_ULTRA", d.ultra);
            CoreUtils.SetKeyword(d.material, "SGSR_STILL", d.still);
            ctx.cmd.SetViewport(new Rect(0, 0, d.width, d.height));
            ctx.cmd.SetGlobalTexture("_SgsrColor", d.color);
            ctx.cmd.SetGlobalTexture("_SgsrDepth", d.depth);
            if (d.pass == 0)
            {
                ctx.cmd.SetGlobalTexture("_SgsrPrevHistory", d.previous);
                ctx.cmd.SetGlobalTexture("_SgsrCurrentInput", d.currentInput);
                ctx.cmd.SetGlobalTexture("_SgsrMotionMask", d.motionMask);
                ctx.cmd.SetGlobalTexture("_SgsrPreviousInput", d.previousInput);
                ctx.cmd.SetGlobalVector("_SgsrPreviousJitter", d.previousJitter);
            }
            if (d.guides.IsValid()) ctx.cmd.SetGlobalTexture("_SgsrGuideBox", d.guides);
            ctx.cmd.SetGlobalVector("_RenderSizeInfo", d.render);
            ctx.cmd.SetGlobalVector("_DisplaySizeInfo", d.display);
            ctx.cmd.SetGlobalVector("_JitterOffset", d.jitter);
            ctx.cmd.SetGlobalVector("_SgsrParams", d.parameters);
            for (int i = 0; i < 4; ++i) ctx.cmd.SetGlobalVector("_ClipToPrevClip" + i, d.reprojection.GetColumn(i));
            ctx.cmd.SetGlobalVector("_BlitScaleBias", new Vector4(1, 1, 0, 0));
            ctx.cmd.SetGlobalFloat("_SameCamera", d.still ? 1 : 0);
            ctx.cmd.SetGlobalFloat("_Reset", d.reset ? 1 : 0);
#if UNITY_EDITOR
            if (!UnityEditor.ShaderUtil.IsPassCompiled(d.material, d.pass))
                UnityEditor.ShaderUtil.CompilePass(d.material, d.pass, true);
#endif
            ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, d.pass, MeshTopology.Triangles, 3);
            if (d.history != null) d.history.written = true;
        }

        void DisposeSgsr()
        {
            foreach (var state in m_SgsrHistory.Values) state.Release();
            m_SgsrHistory.Clear(); CoreUtils.Destroy(m_SgsrMaterial);
        }
    }
}
