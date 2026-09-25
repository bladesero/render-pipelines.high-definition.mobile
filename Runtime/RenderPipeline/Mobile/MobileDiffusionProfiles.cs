using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.HighDefinition
{
    internal sealed partial class MobileRenderPipelineBackend
    {
        readonly Vector4[] m_MobileDiffusionShape = new Vector4[16], m_MobileDiffusionTint = new Vector4[16],
            m_MobileDiffusionWorld = new Vector4[16], m_MobileDiffusionMeta = new Vector4[16];

        void BindMobileDiffusionProfiles(CommandBuffer cmd)
        {
            m_MobileDiffusionShape[0] = new Vector4(16777216, 16777216, 16777216, 0);
            m_MobileDiffusionTint[0] = new Vector4(0, 0, 0, 0.04f);
            m_MobileDiffusionWorld[0] = new Vector4(1, 0, 0, 1);
            m_MobileDiffusionMeta[0] = Vector4.zero;
            int count = 1;
            var profiles = m_Volumes.GetComponent<DiffusionProfileList>()?.diffusionProfiles.value;
            if (profiles != null)
                foreach (var settings in profiles)
                {
                    if (settings == null || settings.profile.hash == 0 || count == 16) continue;
                    uint hash = settings.profile.hash;
                    bool duplicate = false;
                    for (int i = 1; i < count; i++)
                        duplicate |= m_MobileDiffusionMeta[i].x == (hash & 65535) && m_MobileDiffusionMeta[i].y == (hash >> 16);
                    if (duplicate) continue;
                    m_MobileDiffusionShape[count] = settings.shapeParamAndMaxScatterDist;
                    m_MobileDiffusionTint[count] = settings.transmissionTintAndFresnel0;
                    m_MobileDiffusionWorld[count] = settings.worldScaleAndFilterRadiusAndThicknessRemap;
                    m_MobileDiffusionMeta[count] = new Vector4(hash & 65535, hash >> 16,
                        (int)settings.profile.texturingMode, (int)settings.profile.transmissionMode);
                    count++;
                }
            cmd.SetGlobalInt("_MobileDiffusionCount", count);
            cmd.SetGlobalVectorArray("_MobileDiffusionShape", m_MobileDiffusionShape);
            cmd.SetGlobalVectorArray("_MobileDiffusionTint", m_MobileDiffusionTint);
            cmd.SetGlobalVectorArray("_MobileDiffusionWorld", m_MobileDiffusionWorld);
            cmd.SetGlobalVectorArray("_MobileDiffusionMeta", m_MobileDiffusionMeta);
        }

    }
}
