using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.HighDefinition
{
    internal sealed partial class MobileRenderPipelineBackend
    {
        bool UseExtendedDeferred => m_Asset.mobileSettings.extendedLitMaterials && SystemInfo.supportedRenderTargetCount >= 4;
        Material[] m_DeferredMaterials;
        readonly MobileDeferredMaterialUsage m_DeferredMaterialUsage = new MobileDeferredMaterialUsage();
        readonly Dictionary<Camera, RTHandle> m_DeferredDepths = new Dictionary<Camera, RTHandle>();
        readonly List<Camera> m_ExpiredDepthCameras = new List<Camera>();
        Mesh m_DeferredSphere, m_DeferredCone;
        bool? m_NativeInputsSupported;
        readonly HashSet<string> m_DeferredWarnings = new HashSet<string>();
        internal bool lastCameraUsedDeferred { get; private set; }
        internal bool lastCameraUsedNativeDeferred { get; private set; }
        internal int lastCameraColorAttachmentCount { get; private set; }
        internal int lastCameraDeferredLightingDraws { get; private set; }
        internal int lastCameraDeferredVariantMask { get; private set; }

        internal static bool HasDeferredLightContribution(Vector4 color)
            => color.x > 0 || color.y > 0 || color.z > 0;

        internal static bool CompactFormatsSupported()
        {
            foreach (var format in new[] { GraphicsFormat.R8G8B8A8_SRGB, GraphicsFormat.R8G8B8A8_UNorm,
                GraphicsFormat.B10G11R11_UFloatPack32, GraphicsFormat.D24_UNorm_S8_UInt, GraphicsFormat.R32_SFloat })
                if (!SystemInfo.IsFormatSupported(format, FormatUsage.Render)) return false;
            // Unity 2022 D3D11 reports Sample/Linear=false for packed RGB and D24S8
            // even though the render-texture views are supported. Validate the RT
            // usage here; GPU tests exercise sampling of the actual created views.
            return SystemInfo.supportedRenderTargetCount >= 3
                && SystemInfo.IsFormatSupported(GraphicsFormat.B10G11R11_UFloatPack32, FormatUsage.Blend)
                && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float);
        }

        bool ResolveDeferred(ScriptableRenderContext context, Camera camera, CullingResults culling, out bool native)
        {
            native = false; lastCameraUsedDeferred = lastCameraUsedNativeDeferred = false;
            lastCameraColorAttachmentCount = 0;
            lastCameraDeferredLightingDraws = lastCameraDeferredVariantMask = 0;
            if (m_Asset.mobileSettings.renderingPath != MobileRenderingPath.Deferred) return false;
            string reason = null;
            if (m_Asset.mobileResources.deferredShader == null) reason = "HDRP built-in Mobile Deferred shader is unavailable.";
            else if (!m_Asset.mobileResources.deferredShader.isSupported) reason = "The deferred shader is unsupported on this graphics API.";
            else if (!CompactFormatsSupported()) reason = "The exact compact attachment formats or RGB111110 blending are unsupported.";
            else if (camera.targetTexture != null && camera.targetTexture.antiAliasing > 1) reason = "MSAA camera targets are unsupported by compact deferred.";
            else
            {
                foreach (var visible in culling.visibleLights)
                {
                    var light = visible.light;
                    if (light == null || light.bakingOutput.lightmapBakeType == LightmapBakeType.Baked) continue;
                    if ((light.cullingMask & camera.cullingMask) != camera.cullingMask)
                    { reason = "Per-light object layer filtering requires Forward."; break; }
                    if (light.bakingOutput.lightmapBakeType == LightmapBakeType.Mixed
                        && light.bakingOutput.mixedLightingMode != MixedLightingMode.IndirectOnly)
                    { reason = "Subtractive/Shadowmask mixed lighting cannot be represented by the compact GBuffer."; break; }
                }
            }
            bool vulkan = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan;
            if (reason == null && m_Asset.mobileSettings.deferredExecution == MobileDeferredExecution.NativeRenderPass && !vulkan)
                reason = "Native deferred requires Vulkan; D3D11/D3D12 use Auto or Sampled texture-backed deferred.";
            if (reason != null)
            {
                if (m_Asset.mobileSettings.deferredExecution == MobileDeferredExecution.NativeRenderPass)
                    throw new InvalidOperationException("HDRP Mobile forced NativeRenderPass unavailable: " + reason);
                if (m_DeferredWarnings.Add(reason)) Debug.LogWarning("HDRP Mobile deferred fallback to Forward: " + reason, camera);
                return false;
            }
            native = vulkan && (m_Asset.mobileSettings.deferredExecution == MobileDeferredExecution.NativeRenderPass
                || (m_Asset.mobileSettings.deferredExecution == MobileDeferredExecution.Auto
                    && MobileRenderCapabilities.PreferNativeDeferred(SystemInfo.graphicsDeviceType, Application.platform)));
            if (native)
            {
                // Unity 2022 exposes no native depth-input capability bit. Test the
                // actual color/depth attachment path once before enabling it.
                if (!m_NativeInputsSupported.HasValue) m_NativeInputsSupported = ProbeNativeInputs(context, camera);
                if (!m_NativeInputsSupported.Value)
                {
                    const string message = "Native color/depth input attachment probe failed.";
                    if (m_Asset.mobileSettings.deferredExecution == MobileDeferredExecution.NativeRenderPass)
                        throw new InvalidOperationException("HDRP Mobile: " + message);
                    if (m_DeferredWarnings.Add(message)) Debug.LogWarning("HDRP Mobile: " + message + " Using Sampled deferred.", camera);
                    native = false;
                }
            }
            lastCameraUsedDeferred = true; lastCameraUsedNativeDeferred = native;
            return true;
        }

        bool ProbeNativeInputs(ScriptableRenderContext context, Camera camera)
        {
            var target = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                { hideFlags = HideFlags.HideAndDontSave, name = "Mobile Native Input Probe" };
            var readback = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            var material = CoreUtils.CreateEngineMaterial(m_Asset.mobileResources.deferredShader);
            var cmd = CommandBufferPool.Get("Mobile Native Input Probe");
            var previous = RenderTexture.active;
            try
            {
                target.Create(); context.SetupCameraProperties(camera);
                var attachments = new NativeArray<AttachmentDescriptor>(4, Allocator.Temp);
                try
                {
                    var first = new AttachmentDescriptor(GraphicsFormat.R8G8B8A8_SRGB); first.ConfigureClear(new Color(0.25f, 0, 0, 1));
                    var second = new AttachmentDescriptor(GraphicsFormat.R8G8B8A8_UNorm); second.ConfigureClear(new Color(0, 0.5f, 0, 1));
                    var depth = new AttachmentDescriptor(GraphicsFormat.D24_UNorm_S8_UInt); depth.ConfigureClear(Color.clear, 1, 0);
                    var output = new AttachmentDescriptor(GraphicsFormat.R8G8B8A8_UNorm); output.ConfigureTarget(target, false, true);
                    attachments[0] = first; attachments[1] = second; attachments[2] = depth; attachments[3] = output;
                    context.BeginRenderPass(1, 1, 1, attachments, 2);
                }
                finally { attachments.Dispose(); }
                try
                {
                    using (var colors = new NativeArray<int>(new[] { 0, 1 }, Allocator.Temp)) context.BeginSubPass(colors);
                    context.EndSubPass();
                    using (var colors = new NativeArray<int>(new[] { 3 }, Allocator.Temp))
                    using (var inputs = new NativeArray<int>(new[] { 0, 1, 2 }, Allocator.Temp)) context.BeginSubPass(colors, inputs, true, true);
                    try
                    {
                        cmd.SetViewport(new Rect(0, 0, 1, 1));
                        cmd.DrawProcedural(Matrix4x4.identity, material, material.FindPass("MobileNativeInputProbe"), MeshTopology.Triangles, 3);
                        context.ExecuteCommandBuffer(cmd); cmd.Clear();
                    }
                    finally { context.EndSubPass(); }
                }
                finally { context.EndRenderPass(); }
                context.Submit(); RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); readback.Apply();
                Color value = readback.GetPixel(0, 0);
                return Mathf.Abs(value.r - 0.25f) < 0.01f && Mathf.Abs(value.g - 0.5f) < 0.01f
                    && Mathf.Abs(value.b - (SystemInfo.usesReversedZBuffer ? 0 : 1)) < 0.001f && value.a > 0.99f;
            }
            finally
            {
                RenderTexture.active = previous; CommandBufferPool.Release(cmd);
                CoreUtils.Destroy(material); CoreUtils.Destroy(readback); CoreUtils.Destroy(target);
                context.SetupCameraProperties(camera);
            }
        }

        RTHandle EnsureDeferredResources(Camera camera, int width, int height)
        {
            int materialCount = UseExtendedDeferred ? 8 : 4;
            if (m_DeferredMaterials != null && m_DeferredMaterials.Length != materialCount)
            { foreach (var material in m_DeferredMaterials) CoreUtils.Destroy(material); m_DeferredMaterials = null; }
            if (m_DeferredMaterials == null)
            {
                m_DeferredMaterials = new Material[materialCount];
                for (int entry = 0; entry < materialCount; entry++)
                {
                    int type = entry < 4 ? 1 : 4, flags = entry & 3;
                    var material = CoreUtils.CreateEngineMaterial(m_Asset.mobileResources.deferredShader);
                    material.SetInt("_MobileStencilRef", (type << 4) | flags | (type == 4 ? MobileDeferredMaterial.SpecularSetup : 0));
                    CoreUtils.SetKeyword(material, "MOBILE_RECEIVE_SHADOWS_OFF", (flags & 1) != 0);
                    CoreUtils.SetKeyword(material, "MOBILE_SPECULAR_HIGHLIGHTS_OFF", (flags & 2) != 0);
                    material.SetInt("_MobileMaterialType", type);
                    CoreUtils.SetKeyword(material, "MOBILE_EXTENDED_LIT", materialCount > 4);
                    m_DeferredMaterials[entry] = material;
                }
                if (m_DeferredSphere == null) m_DeferredSphere = BuildLightVolume(false);
                if (m_DeferredCone == null) m_DeferredCone = BuildLightVolume(true);
            }
            m_DeferredDepths.TryGetValue(camera, out var previous);
            if (previous?.rt != null && previous.rt.width == width && previous.rt.height == height && previous.rt.IsCreated())
                return previous;
            // Each camera owns its depth. A Scene View repaint must not invalidate
            // another camera's imported attachment. Publish only a valid replacement.
            var texture = new RenderTexture(new RenderTextureDescriptor(width, height)
            {
                graphicsFormat = GraphicsFormat.None, depthStencilFormat = GraphicsFormat.D24_UNorm_S8_UInt,
                msaaSamples = 1, volumeDepth = 1, dimension = TextureDimension.Tex2D
            }) { name = "Mobile Deferred D24S8", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
            if (!texture.Create())
            { CoreUtils.Destroy(texture); throw new InvalidOperationException("Could not allocate Mobile Deferred D24S8."); }
            if (texture.depthStencilFormat != GraphicsFormat.D24_UNorm_S8_UInt)
            { CoreUtils.Destroy(texture); throw new InvalidOperationException("Compact deferred requires exact D24S8; format substitution is not allowed."); }
            var replacement = RTHandles.Alloc(texture);
            m_DeferredDepths[camera] = replacement;
            previous?.Release(); // RTHandle owns and destroys the RenderTexture.
            return replacement;
        }

        static void Flush(RenderGraphContext ctx)
        { ctx.renderContext.ExecuteCommandBuffer(ctx.cmd); ctx.cmd.Clear(); }

        void DrawDeferredScene(SceneData pass, RenderGraphContext ctx)
        {
            lastCameraDeferredVariantMask = m_DeferredMaterialUsage.Gather(pass.camera, pass.extendedLit)
                | m_Instances.DeferredLightingVariantMask(pass.extendedLit);
            ctx.cmd.SetGlobalVector("_MobileBufferSize", new Vector4(pass.width, pass.height, 1f / pass.width, 1f / pass.height));
            ctx.cmd.SetGlobalFloat("_MobileUseLightVolume", 0);
            Flush(ctx);
            if (pass.nativeDeferred) DrawNativeDeferred(pass, ctx);
            else DrawSampledDeferred(pass, ctx);
        }

        void DrawSampledDeferred(SceneData pass, RenderGraphContext ctx)
        {
            RenderTargetIdentifier color = pass.color, depth = pass.depth;
            var mrt = pass.extendedLit
                ? new RenderTargetIdentifier[] { pass.gbuffer0, pass.gbuffer1, color, pass.gbuffer3 }
                : new RenderTargetIdentifier[] { pass.gbuffer0, pass.gbuffer1, color };
            lastCameraColorAttachmentCount = mrt.Length;
            ctx.cmd.SetRenderTarget(mrt, depth);
            ctx.cmd.SetViewport(new Rect(0, 0, pass.width, pass.height));
            // Unity converts the logical far value to the platform's reversed-Z clear.
            ctx.cmd.ClearRenderTarget(RTClearFlags.All, Color.clear, 1, MobileDeferredMaterial.Background);
            // Only RT2 carries the camera background. GBuffer values at background pixels are never consumed.
            ctx.cmd.SetRenderTarget(color, depth);
            ctx.cmd.ClearRenderTarget(false, true, DeferredClearColor(pass.camera));
            ctx.cmd.SetRenderTarget(mrt, depth);
            Flush(ctx);
            DrawObjects(ctx.renderContext, pass.camera, pass.culling, pass.mainLight, false, pass.extendedLit ? "MobileGBufferExtended" : "MobileGBuffer");

            // Unbind D24S8 before sampling it; retain the original for stencil tests.
            ctx.cmd.SetRenderTarget((RenderTargetIdentifier)pass.depthCopy);
            ctx.cmd.SetGlobalTexture("_MobileDepthSource", depth);
            ctx.cmd.DrawProcedural(Matrix4x4.identity, m_DeferredMaterials[0], 4, MeshTopology.Triangles, 3);
            ctx.cmd.SetGlobalTexture("_MobileGBuffer0", pass.gbuffer0);
            ctx.cmd.SetGlobalTexture("_MobileGBuffer1", pass.gbuffer1);
            ctx.cmd.SetGlobalTexture("_MobileDepthCopy", pass.depthCopy);
            if (pass.extendedLit)
                ctx.cmd.SetGlobalTexture("_MobileGBuffer3", pass.gbuffer3);
            ctx.cmd.SetRenderTarget(color, depth);
            DrawDeferredLights(pass, ctx);
            Flush(ctx);
            DrawDeferredForward(pass, ctx);
        }

        Color DeferredClearColor(Camera camera)
        {
            camera.TryGetComponent<HDAdditionalCameraData>(out var additional);
            return additional != null ? additional.backgroundColorHDR : camera.backgroundColor.linear;
        }

        void DrawNativeDeferred(SceneData pass, RenderGraphContext ctx)
        {
            // RT0/RT1 deliberately have no ConfigureTarget: native transient attachments,
            // no backing texture and no stores. Lighting never fetches its RT2 output.
            var attachments = new NativeArray<AttachmentDescriptor>(pass.extendedLit ? 5 : 4, Allocator.Temp);
            lastCameraColorAttachmentCount = attachments.Length - 1;
            try
            {
                attachments[0] = new AttachmentDescriptor(GraphicsFormat.R8G8B8A8_SRGB);
                attachments[1] = new AttachmentDescriptor(GraphicsFormat.R8G8B8A8_UNorm);
                if (pass.extendedLit)
                    attachments[4] = new AttachmentDescriptor(GraphicsFormat.R8G8B8A8_UNorm);
                var color = new AttachmentDescriptor(GraphicsFormat.B10G11R11_UFloatPack32);
                color.ConfigureTarget(pass.color, false, true);
                color.ConfigureClear(DeferredClearColor(pass.camera));
                attachments[2] = color;
                var depth = new AttachmentDescriptor(GraphicsFormat.D24_UNorm_S8_UInt);
                bool storeDepth = false;
#if UNITY_EDITOR
                storeDepth |= pass.camera.cameraType == CameraType.SceneView || UnityEditor.Handles.ShouldRenderGizmos();
#endif
                depth.ConfigureTarget(pass.depth, false, storeDepth);
                depth.ConfigureClear(Color.clear, 1, MobileDeferredMaterial.Background);
                attachments[3] = depth;
                ctx.renderContext.BeginRenderPass(pass.width, pass.height, 1, attachments, 3);
            }
            finally { attachments.Dispose(); }
            bool subpass = false;
            try
            {
                using (var colors = new NativeArray<int>(pass.extendedLit ? new[] { 0, 1, 2, 4 } : new[] { 0, 1, 2 }, Allocator.Temp))
                    ctx.renderContext.BeginSubPass(colors);
                subpass = true;
                DrawObjects(ctx.renderContext, pass.camera, pass.culling, pass.mainLight, false, pass.extendedLit ? "MobileGBufferExtended" : "MobileGBuffer");
                ctx.renderContext.EndSubPass(); subpass = false;
                using (var colors = new NativeArray<int>(new[] { 2 }, Allocator.Temp))
                using (var inputs = new NativeArray<int>(pass.extendedLit ? new[] { 0, 1, 3, 4 } : new[] { 0, 1, 3 }, Allocator.Temp))
                    ctx.renderContext.BeginSubPass(colors, inputs, true, true);
                subpass = true;
                DrawDeferredLights(pass, ctx); Flush(ctx);
                ctx.renderContext.EndSubPass(); subpass = false;
                {
                    using (var colors = new NativeArray<int>(new[] { 2 }, Allocator.Temp)) ctx.renderContext.BeginSubPass(colors);
                    subpass = true;
                    DrawDeferredForward(pass, ctx);
                    ctx.renderContext.EndSubPass(); subpass = false;
                }
            }
            finally
            {
                if (subpass) ctx.renderContext.EndSubPass();
                ctx.renderContext.EndRenderPass();
            }
        }

        void DrawDeferredForward(SceneData pass, RenderGraphContext ctx)
        {
            DrawObjects(ctx.renderContext, pass.camera, pass.culling, pass.mainLight, false,
                pass.extendedLit ? "MobileForwardOnlyExtended" : "MobileForwardOnly");
            if (pass.drawErrors) m_ErrorRenderer.Draw(ctx.cmd, pass.camera, m_Error, false);
            if (pass.sky.mode != 0 && pass.drawSky)
            {
                MobileEnvironmentCache.BindSky(ctx.cmd, pass.sky, pass.mainDirection, pass.mainColor);
                ctx.cmd.SetGlobalFloat("_MobileSkyCameraExposure", pass.sky.cameraExposure ? 1 : 0);
                ctx.cmd.DrawProcedural(Matrix4x4.identity, m_Sky, 0, MeshTopology.Triangles, 3);
            }
            Flush(ctx);
            DrawObjects(ctx.renderContext, pass.camera, pass.culling, pass.mainLight, true);
            if (pass.drawErrors) m_ErrorRenderer.Draw(ctx.cmd, pass.camera, m_Error, true);
            Flush(ctx);
        }

        void DrawDeferredLights(SceneData pass, RenderGraphContext ctx)
        {
            int variantMask = lastCameraDeferredVariantMask;
            if (variantMask == 0) return;
            int fullscreenPass = pass.nativeDeferred ? 1 : 0;
            ctx.cmd.SetGlobalFloat("_MobileUseLightVolume", 0);
            ctx.cmd.SetGlobalInt("_MobileLightIndex", -1);
            if (pass.mainLight >= 0 && HasDeferredLightContribution(pass.mainColor))
                DrawDeferredFullscreen(ctx.cmd, fullscreenPass, variantMask);

            int index = 0;
            foreach (var visible in pass.culling.visibleLights)
            {
                var light = visible.light;
                if (index == MaxVisibleLights) break;
                if (light == null || light.bakingOutput.lightmapBakeType == LightmapBakeType.Baked
                    || (visible.lightType != LightType.Point && visible.lightType != LightType.Spot)) continue;
                // Keep the packed light/shadow index even when this draw is skipped.
                int lightIndex = index++;
                if (!HasDeferredLightContribution(pass.colors[lightIndex])) continue;
                bool spot = visible.lightType == LightType.Spot;
                float range = visible.range;
                Vector3 origin = visible.localToWorldMatrix.GetColumn(3);
                Vector3 forward = ((Vector3)visible.localToWorldMatrix.GetColumn(2)).normalized;
                float radius = spot ? Mathf.Tan(visible.spotAngle * Mathf.Deg2Rad * 0.5f) * range : range;
                Vector3 center = spot ? origin + forward * range * 0.5f : origin;
                float boundRadius = spot ? Mathf.Sqrt(radius * radius + range * range * 0.25f) : range;
                boundRadius *= 1.1f;
                Vector3 cameraPosition = pass.position;
                Vector3 cameraForward = pass.forward;
                float viewDepth = Vector3.Dot(center - cameraPosition, cameraForward);
                bool fullscreen = (center - cameraPosition).sqrMagnitude <= boundRadius * boundRadius
                    || viewDepth - boundRadius <= pass.camera.nearClipPlane
                    || viewDepth + boundRadius >= pass.camera.farClipPlane;
                ctx.cmd.SetGlobalInt("_MobileLightIndex", lightIndex);
                ctx.cmd.SetGlobalFloat("_MobileUseLightVolume", fullscreen ? 0 : 1);
                if (fullscreen)
                {
                    // A near-plane intersecting sphere may project to the entire viewport.
                    // The full viewport is the conservative scissor for this fallback.
                    ctx.cmd.EnableScissorRect(new Rect(0, 0, pass.width, pass.height));
                    DrawDeferredFullscreen(ctx.cmd, fullscreenPass, variantMask);
                    ctx.cmd.DisableScissorRect();
                }
                else
                {
                    var transform = Matrix4x4.TRS(origin, spot ? Quaternion.LookRotation(forward) : Quaternion.identity,
                        spot ? new Vector3(radius, radius, range) : Vector3.one * range);
                    ctx.cmd.SetGlobalMatrix("_MobileLightVolumeTransform", transform);
                    for (int flags = 0; flags < m_DeferredMaterials.Length; flags++)
                    {
                        if ((variantMask & (1 << flags)) == 0) continue;
                        ctx.cmd.DrawMesh(spot ? m_DeferredCone : m_DeferredSphere, Matrix4x4.identity,
                            m_DeferredMaterials[flags], 0, pass.nativeDeferred ? 6 : 5);
                        lastCameraDeferredLightingDraws++;
                    }
                }
            }
            ctx.cmd.SetGlobalFloat("_MobileUseLightVolume", 0);
            if (pass.fog.x > 0)
                for (int type = 0; type < m_DeferredMaterials.Length; type += 4)
                    if ((variantMask & (15 << type)) != 0)
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, m_DeferredMaterials[type], pass.nativeDeferred ? 3 : 2, MeshTopology.Triangles, 3);
        }

        void DrawDeferredFullscreen(CommandBuffer cmd, int pass, int variantMask)
        {
            for (int flags = 0; flags < m_DeferredMaterials.Length; flags++)
            {
                if ((variantMask & (1 << flags)) == 0) continue;
                cmd.DrawProcedural(Matrix4x4.identity, m_DeferredMaterials[flags], pass, MeshTopology.Triangles, 3);
                lastCameraDeferredLightingDraws++;
            }
        }

        internal static Mesh BuildLightVolume(bool cone)
        {
            const int sides = 16, rings = 8;
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            void Triangle(int a, int b, int c)
            {
                Vector3 center = (vertices[a] + vertices[b] + vertices[c]) / 3 - (cone ? Vector3.forward * 0.5f : Vector3.zero);
                if (Vector3.Dot(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]), center) < 0)
                { int swap = b; b = c; c = swap; }
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
            }
            if (cone)
            {
                vertices.Add(Vector3.zero); vertices.Add(Vector3.forward);
                float inflate = 1 / Mathf.Cos(Mathf.PI / sides);
                for (int i = 0; i < sides; i++)
                    vertices.Add(new Vector3(Mathf.Cos(i * 2 * Mathf.PI / sides) * inflate, Mathf.Sin(i * 2 * Mathf.PI / sides) * inflate, 1));
                for (int i = 0; i < sides; i++) { Triangle(0, i + 2, (i + 1) % sides + 2); Triangle(1, (i + 1) % sides + 2, i + 2); }
            }
            else
            {
                float inflate = 1 / (Mathf.Cos(Mathf.PI / sides) * Mathf.Cos(Mathf.PI / (2 * rings)));
                for (int y = 0; y <= rings; y++) for (int x = 0; x < sides; x++)
                {
                    float latitude = Mathf.PI * y / rings, longitude = Mathf.PI * 2 * x / sides;
                    vertices.Add(new Vector3(Mathf.Sin(latitude) * Mathf.Cos(longitude), Mathf.Cos(latitude), Mathf.Sin(latitude) * Mathf.Sin(longitude)) * inflate);
                }
                for (int y = 0; y < rings; y++) for (int x = 0; x < sides; x++)
                {
                    int a = y * sides + x, b = y * sides + (x + 1) % sides;
                    Triangle(a, b, a + sides); Triangle(b, b + sides, a + sides);
                }
            }
            var mesh = new Mesh { name = cone ? "Mobile Spot Volume" : "Mobile Point Volume", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateBounds(); mesh.UploadMeshData(true);
            return mesh;
        }

        void ReleaseDeferredDepth(Camera camera)
        {
            if (!m_DeferredDepths.TryGetValue(camera, out var depth)) return;
            m_DeferredDepths.Remove(camera);
            depth.Release();
        }

        void PruneDeferredDepths()
        {
            m_ExpiredDepthCameras.Clear();
            foreach (var entry in m_DeferredDepths)
                if (entry.Key == null || !entry.Key.gameObject.activeInHierarchy
                    || m_Asset.mobileSettings.renderingPath != MobileRenderingPath.Deferred)
                    m_ExpiredDepthCameras.Add(entry.Key);
            foreach (var camera in m_ExpiredDepthCameras) ReleaseDeferredDepth(camera);
            m_ExpiredDepthCameras.Clear();
        }

        void DisposeDeferred()
        {
            foreach (var depth in m_DeferredDepths.Values) depth.Release();
            m_DeferredDepths.Clear();
            if (m_DeferredMaterials != null) foreach (var material in m_DeferredMaterials) CoreUtils.Destroy(material);
            m_DeferredMaterials = null;
            CoreUtils.Destroy(m_DeferredSphere); CoreUtils.Destroy(m_DeferredCone);
        }
    }
}
