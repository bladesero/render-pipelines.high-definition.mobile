using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    // Same octahedral encoding, mip padding and GGX mip convention as HDRP's
    // ReflectionProbeTextureCache. Fixed slots bound Mobile memory and shader ALU.
    // GGX filtering runs in the octahedral raster pass, without desktop compute.
    internal sealed class MobileReflectionAtlas : IDisposable
    {
        internal const int Capacity = 16;
        const int MipPadding = 3, TexelPadding = 16;
        readonly List<HDAdditionalReflectionData> m_Visible = new List<HDAdditionalReflectionData>();
        readonly Texture[] m_Textures = new Texture[Capacity];
        readonly uint[] m_Hashes = new uint[Capacity];
        readonly Vector4[] m_ScaleOffsets = new Vector4[Capacity], m_Extents = new Vector4[Capacity],
            m_BlendPositive = new Vector4[Capacity], m_BlendNegative = new Vector4[Capacity],
            m_Capture = new Vector4[Capacity], m_ProxyExtents = new Vector4[Capacity], m_Params = new Vector4[Capacity];
        readonly Matrix4x4[] m_WorldToInfluence = new Matrix4x4[Capacity], m_WorldToProxy = new Matrix4x4[Capacity];
        readonly MaterialPropertyBlock m_Block = new MaterialPropertyBlock();
        RenderTexture m_Atlas;

        internal void Prepare(CommandBuffer cmd, Camera camera, CullingResults culling, Material blit, int requestedSize)
        {
            m_Visible.Clear();
            bool hasHDRPProbes = false;
            foreach (var visible in culling.visibleReflectionProbes)
            {
                var native = visible.reflectionProbe;
                var probe = native != null ? native.GetComponent<HDAdditionalReflectionData>() : null;
                if (probe == null || !probe.isActiveAndEnabled) continue;
                hasHDRPProbes = true;
                if (probe.texture == null ||
                    probe.texture.dimension != TextureDimension.Cube || probe.weight <= 0) continue;
                m_Visible.Add(probe);
            }
            // Match Desktop HDRP's influence-volume hierarchy: local probes consume
            // reflection weight before larger probes. Legacy importance/distance
            // ordering lets bright exterior probes override dark interior probes.
            m_Visible.Sort((a, b) =>
            {
                int volume = ProbeVolumeKey(a).CompareTo(ProbeVolumeKey(b));
                if (volume != 0) return volume;
                var shapeA = a.influenceVolume.shape == InfluenceShape.Sphere ? LightVolumeType.Sphere : LightVolumeType.Box;
                var shapeB = b.influenceVolume.shape == InfluenceShape.Sphere ? LightVolumeType.Sphere : LightVolumeType.Box;
                int shape = shapeA.CompareTo(shapeB);
                return shape != 0 ? shape : a.GetInstanceID().CompareTo(b.GetInstanceID());
            });
            int count = Mathf.Min(Capacity, m_Visible.Count);
            cmd.SetGlobalInt("_MobileReflectionHasHDRPProbes", hasHDRPProbes ? 1 : 0);
            cmd.SetGlobalInt("_MobileReflectionCount16", count);
            if (count == 0) return;
            int size = Mathf.NextPowerOfTwo(Mathf.Clamp(requestedSize, 1024, 4096));
            if (m_Atlas == null || m_Atlas.width != size || !m_Atlas.IsCreated())
            {
                Dispose();
                m_Atlas = new RenderTexture(size, size, 0, GraphicsFormat.R16G16B16A16_SFloat)
                {
                    name = "Mobile HDRP Reflection Octahedral Atlas", hideFlags = HideFlags.HideAndDontSave,
                    dimension = TextureDimension.Tex2DArray, volumeDepth = 1,
                    useMipMap = true, autoGenerateMips = false,
                    filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp
                };
                m_Atlas.Create();
            }
            int tileSize = size / 4;
            int blitPass = blit.FindPass("MobileReflectionOctahedral");
            for (int i = 0; i < count; ++i)
            {
                var probe = m_Visible[i];
                var texture = probe.texture;
                var scaleOffset = new Vector4(.25f, .25f, (i % 4) * .25f, (i / 4) * .25f);
                uint hash = probe.GetTextureHash();
                if (m_Textures[i] != texture || m_Hashes[i] != hash || probe.mode == ProbeSettings.Mode.Realtime)
                {
                    m_Block.SetTexture("_BlitCubeTexture", texture);
                    m_Block.SetFloat("_MobileReflectionSourceSize", texture.width);
                    m_Block.SetVector("_BlitScaleBias", new Vector4(1, 1, 0, 0));
                    m_Block.SetVector("_BlitScaleBiasRt", scaleOffset);
                    // Match ReflectionProbeTextureCache.BlitTextureCube exactly.
                    m_Block.SetVector("_BlitTextureSize", new Vector4(tileSize - TexelPadding, tileSize - TexelPadding, 0, 0));
                    for (int mip = 0; mip <= 6; ++mip)
                    {
                        m_Block.SetFloat("_BlitMipLevel", mip);
                        m_Block.SetInt("_BlitPaddingSize", TexelPadding << Mathf.Max(0, mip - MipPadding));
                        cmd.SetRenderTarget(m_Atlas, mip, CubemapFace.Unknown, 0);
                        cmd.SetViewport(new Rect(0, 0, size >> mip, size >> mip));
                        cmd.DrawProcedural(Matrix4x4.identity, blit, blitPass, MeshTopology.Quads, 4, 1, m_Block);
                    }
                    m_Textures[i] = texture; m_Hashes[i] = hash;
                }
                m_ScaleOffsets[i] = scaleOffset;
                m_WorldToInfluence[i] = probe.influenceToWorld.inverse;
                m_WorldToProxy[i] = probe.proxyToWorld.inverse;
                var influence = probe.influenceVolume;
                var extents = influence.extents;
                m_Extents[i] = new Vector4(extents.x, extents.y, extents.z, influence.shape == InfluenceShape.Sphere ? 1 : 0);
                m_BlendPositive[i] = influence.shape == InfluenceShape.Sphere ? Vector3.one * influence.sphereBlendDistance : influence.boxBlendDistancePositive;
                m_BlendNegative[i] = influence.boxBlendDistanceNegative;
                var positionSettings = ProbeCapturePositionSettings.ComputeFrom(probe, camera.transform);
                HDRenderUtilities.ComputeCameraSettingsFromProbeSettings(probe.settings, positionSettings, out _, out var capture, 0);
                m_Capture[i] = new Vector4(capture.position.x, capture.position.y, capture.position.z,
                    probe.multiplier * Mathf.Max(probe.rangeCompressionFactor, 1e-6f));
                var proxy = probe.proxyExtents;
                bool sphereProxy = probe.proxyVolume != null ? probe.proxyVolume.proxyVolume.shape == ProxyShape.Sphere : influence.shape == InfluenceShape.Sphere;
                m_ProxyExtents[i] = new Vector4(proxy.x, proxy.y, proxy.z, probe.isProjectionInfinite ? -1 : sphereProxy ? 1 : 0);
                m_Params[i] = new Vector4(Mathf.Clamp01(probe.weight), 0, 0, 0);
            }
            cmd.SetGlobalTexture("_MobileReflectionAtlas", m_Atlas);
            cmd.SetGlobalVector("_MobileReflectionAtlasCubeData", new Vector4((float)TexelPadding / size, (float)TexelPadding / size, MipPadding, 0));
            cmd.SetGlobalVectorArray("_MobileReflectionScaleOffset16", m_ScaleOffsets);
            cmd.SetGlobalVectorArray("_MobileReflectionExtents16", m_Extents);
            cmd.SetGlobalVectorArray("_MobileReflectionBlendPositive16", m_BlendPositive);
            cmd.SetGlobalVectorArray("_MobileReflectionBlendNegative16", m_BlendNegative);
            cmd.SetGlobalVectorArray("_MobileReflectionCapture16", m_Capture);
            cmd.SetGlobalVectorArray("_MobileReflectionProxyExtents16", m_ProxyExtents);
            cmd.SetGlobalVectorArray("_MobileReflectionParams16", m_Params);
            cmd.SetGlobalMatrixArray("_MobileReflectionWorldToInfluence16", m_WorldToInfluence);
            cmd.SetGlobalMatrixArray("_MobileReflectionWorldToProxy16", m_WorldToProxy);
        }

        static uint ProbeVolumeKey(HDAdditionalReflectionData probe)
        {
            // Same quantization as LightLoop.CalculateProbeLogVolume. Use native
            // culling bounds, including rotation, as Desktop's visible probes do.
            var extents = probe.GetComponent<ReflectionProbe>().bounds.extents;
            float volume = 8f * extents.x * extents.y * extents.z;
            return (uint)Mathf.Clamp(Mathf.Log(1 + volume, 1.05f) * 1000, 0, 1048575);
        }

        public void Dispose()
        {
            if (m_Atlas != null) { m_Atlas.Release(); CoreUtils.Destroy(m_Atlas); m_Atlas = null; }
            Array.Clear(m_Textures, 0, m_Textures.Length);
            Array.Clear(m_Hashes, 0, m_Hashes.Length);
        }
    }
}
