using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.HighDefinition
{
    internal sealed partial class MobileRenderPipelineBackend
    {
        readonly HableCurve m_MobileToneCurve = new HableCurve();
        RTHandle m_MobileGradingLut;
        int m_MobileGradingHash;
        bool m_MobileGradingValid;

        internal static HDAdditionalCameraData.AntialiasingMode GetAntialiasingMode(Camera camera)
        {
#if UNITY_EDITOR
            if (camera.cameraType == CameraType.SceneView)
                return HDAdditionalSceneViewSettings.sceneViewAntialiasing;
#endif
            return camera.TryGetComponent<HDAdditionalCameraData>(out var additional)
                ? additional.antialiasing : HDAdditionalCameraData.AntialiasingMode.None;
        }

        internal static bool IsFXAAEnabled(Camera camera, FrameSettings frameSettings)
        {
            return frameSettings.IsEnabled(FrameSettingsField.Postprocess)
                && frameSettings.IsEnabled(FrameSettingsField.Antialiasing)
                && CoreUtils.ArePostProcessesEnabled(camera)
                && GetAntialiasingMode(camera) == HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing;
        }

        int ColorGradingHash(bool grading, bool tonemapping)
        {
            unchecked
            {
                int hash = grading ? 1 : 0;
                hash = hash * 23 + (tonemapping ? 1 : 0);
                hash = hash * 23 + m_Volumes.GetComponent<WhiteBalance>().GetHashCode();
                hash = hash * 23 + m_Volumes.GetComponent<ColorAdjustments>().GetHashCode();
                hash = hash * 23 + m_Volumes.GetComponent<ChannelMixer>().GetHashCode();
                hash = hash * 23 + m_Volumes.GetComponent<ShadowsMidtonesHighlights>().GetHashCode();
                hash = hash * 23 + m_Volumes.GetComponent<LiftGammaGain>().GetHashCode();
                hash = hash * 23 + m_Volumes.GetComponent<SplitToning>().GetHashCode();
                hash = hash * 23 + m_Volumes.GetComponent<Tonemapping>().GetHashCode();
                hash = hash * 23 + (int)(HDRenderPipelineGlobalSettings.instance?.colorGradingSpace ?? ColorGradingSpace.sRGB);
                return hash;
            }
        }

        bool HasActiveColorGrading()
        {
            return m_Volumes.GetComponent<WhiteBalance>().IsActive()
                || m_Volumes.GetComponent<ColorAdjustments>().IsActive()
                || m_Volumes.GetComponent<ChannelMixer>().IsActive()
                || m_Volumes.GetComponent<ShadowsMidtonesHighlights>().IsActive()
                || m_Volumes.GetComponent<LiftGammaGain>().IsActive()
                || m_Volumes.GetComponent<SplitToning>().IsActive()
                || m_Volumes.GetComponent<ColorCurves>().AnyPropertiesIsOverridden();
        }

        class MobileGradingData : HDRenderPipeline.ColorGradingPassData
        {
            internal Material material;
            internal string toneKeyword, gradingKeyword;
        }

        TextureHandle RecordColorGrading(bool grading, bool tonemapping)
        {
            int lutSize = m_Asset.currentPlatformRenderPipelineSettings.postProcessSettings.lutSize;
            if (m_MobileGradingLut != null && m_MobileGradingLut.rt.height != lutSize)
            {
                m_MobileGradingLut.Release();
                m_MobileGradingLut = null;
                m_MobileGradingValid = false;
            }
            if (m_MobileGradingLut == null)
                m_MobileGradingLut = RTHandles.Alloc(lutSize * lutSize, lutSize, colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                    filterMode: FilterMode.Bilinear, name: "Mobile Grading LUT");
            var lut = m_Graph.ImportTexture(m_MobileGradingLut);
            if (!m_MobileGradingLut.rt.IsCreated())
            {
                m_MobileGradingLut.rt.Create();
                m_MobileGradingValid = false;
            }
            int hash = ColorGradingHash(grading, tonemapping);
            // Curves and external texture contents can change without changing their parameter hash.
            bool dynamicLut = m_Volumes.GetComponent<ColorCurves>().AnyPropertiesIsOverridden()
                || m_Volumes.GetComponent<Tonemapping>().mode.value == TonemappingMode.External;
#if UNITY_EDITOR
            // An editor repaint / Frame Debugger replay is not a persistent GPU-content
            // guarantee. Rebuild instead of trusting the CPU hash of an imported RT.
            dynamicLut |= !Application.isPlaying || UnityEngine.FrameDebugger.enabled;
#endif
            if (m_MobileGradingValid && hash == m_MobileGradingHash && !dynamicLut)
                return lut;
            m_MobileGradingValid = false;
            using (var builder = m_Graph.AddRenderPass<MobileGradingData>("Mobile Color Grading LUT", out var data))
            {
                var tone = m_Volumes.GetComponent<Tonemapping>();
                data.tonemappingMode = tonemapping && tone.active && tone.IsActive() ? tone.mode.value : TonemappingMode.None;
                data.toneKeyword = "TONEMAPPING_NONE";
                switch (data.tonemappingMode)
                {
                    case TonemappingMode.Neutral: data.toneKeyword = "TONEMAPPING_NEUTRAL"; break;
                    case TonemappingMode.ACES: data.toneKeyword = tone.useFullACES.value ? "TONEMAPPING_ACES_FULL" : "TONEMAPPING_ACES_APPROX"; break;
                    case TonemappingMode.Custom: data.toneKeyword = "TONEMAPPING_CUSTOM"; break;
                    case TonemappingMode.External: data.toneKeyword = "TONEMAPPING_EXTERNAL"; break;
                }
                data.gradingKeyword = HDRenderPipelineGlobalSettings.instance != null && HDRenderPipelineGlobalSettings.instance.colorGradingSpace == ColorGradingSpace.AcesCg
                    ? "GRADE_IN_ACESCG" : "GRADE_IN_SRGB";
                ColorGradingUtilities.Prepare(data, m_Volumes.GetComponent<WhiteBalance>(), m_Volumes.GetComponent<ColorAdjustments>(),
                    m_Volumes.GetComponent<ChannelMixer>(), m_Volumes.GetComponent<ShadowsMidtonesHighlights>(),
                    m_Volumes.GetComponent<LiftGammaGain>(), m_Volumes.GetComponent<SplitToning>(),
                    m_Volumes.GetComponent<ColorCurves>(), tone, m_MobileToneCurve, grading);
                data.material = m_Composite;
                data.lutSize = lutSize;
                builder.UseColorBuffer(lut, 0);
                builder.SetRenderFunc((MobileGradingData data, RenderGraphContext ctx) =>
                {
                    data.material.shaderKeywords = new[] { data.toneKeyword, data.gradingKeyword };
                    int shaderPass = data.material.FindPass("MobileColorGradingLut");
#if UNITY_EDITOR
                    // A skipped async draw must never become a valid persistent LUT.
                    if (!UnityEditor.ShaderUtil.IsPassCompiled(data.material, shaderPass))
                        UnityEditor.ShaderUtil.CompilePass(data.material, shaderPass, true);
                    if (!UnityEditor.ShaderUtil.IsPassCompiled(data.material, shaderPass))
                        return;
#endif
                    ctx.cmd.SetViewport(new Rect(0, 0, data.lutSize * data.lutSize, data.lutSize));
                    ctx.cmd.SetGlobalVector(HDShaderIDs._Size, new Vector4(data.lutSize, 1f / (data.lutSize - 1f), 0, 0));
                    ctx.cmd.SetGlobalVector(HDShaderIDs._ColorBalance, data.lmsColorBalance);
                    ctx.cmd.SetGlobalVector(HDShaderIDs._ColorFilter, data.colorFilter);
                    ctx.cmd.SetGlobalVector(HDShaderIDs._ChannelMixerRed, data.channelMixerR);
                    ctx.cmd.SetGlobalVector(HDShaderIDs._ChannelMixerGreen, data.channelMixerG);
                    ctx.cmd.SetGlobalVector(HDShaderIDs._ChannelMixerBlue, data.channelMixerB);
                    ctx.cmd.SetGlobalVector(HDShaderIDs._HueSatCon, data.hueSatCon);
                    ctx.cmd.SetGlobalVector(HDShaderIDs._Lift, data.lift);
                    ctx.cmd.SetGlobalVector(HDShaderIDs._Gamma, data.gamma);
                    ctx.cmd.SetGlobalVector(HDShaderIDs._Gain, data.gain);
                    ctx.cmd.SetGlobalVector(HDShaderIDs._Shadows, data.shadows);
                    ctx.cmd.SetGlobalVector(HDShaderIDs._Midtones, data.midtones);
                    ctx.cmd.SetGlobalVector(HDShaderIDs._Highlights, data.highlights);
                    ctx.cmd.SetGlobalVector(HDShaderIDs._ShaHiLimits, data.shadowsHighlightsLimits);
                    ctx.cmd.SetGlobalVector(HDShaderIDs._SplitShadows, data.splitShadows);
                    ctx.cmd.SetGlobalVector(HDShaderIDs._SplitHighlights, data.splitHighlights);

                    // YRGB
                    ctx.cmd.SetGlobalTexture(HDShaderIDs._CurveMaster, data.curves.master.value.GetTexture());
                    ctx.cmd.SetGlobalTexture(HDShaderIDs._CurveRed, data.curves.red.value.GetTexture());
                    ctx.cmd.SetGlobalTexture(HDShaderIDs._CurveGreen, data.curves.green.value.GetTexture());
                    ctx.cmd.SetGlobalTexture(HDShaderIDs._CurveBlue, data.curves.blue.value.GetTexture());

                    // Secondary curves
                    ctx.cmd.SetGlobalTexture(HDShaderIDs._CurveHueVsHue, data.curves.hueVsHue.value.GetTexture());
                    ctx.cmd.SetGlobalTexture(HDShaderIDs._CurveHueVsSat, data.curves.hueVsSat.value.GetTexture());
                    ctx.cmd.SetGlobalTexture(HDShaderIDs._CurveLumVsSat, data.curves.lumVsSat.value.GetTexture());
                    ctx.cmd.SetGlobalTexture(HDShaderIDs._CurveSatVsSat, data.curves.satVsSat.value.GetTexture());

                    // Artist-driven tonemap curve
                    if (data.tonemappingMode == TonemappingMode.Custom)
                    {
                        ctx.cmd.SetGlobalVector(HDShaderIDs._CustomToneCurve, data.hableCurve.uniforms.curve);
                        ctx.cmd.SetGlobalVector(HDShaderIDs._ToeSegmentA, data.hableCurve.uniforms.toeSegmentA);
                        ctx.cmd.SetGlobalVector(HDShaderIDs._ToeSegmentB, data.hableCurve.uniforms.toeSegmentB);
                        ctx.cmd.SetGlobalVector(HDShaderIDs._MidSegmentA, data.hableCurve.uniforms.midSegmentA);
                        ctx.cmd.SetGlobalVector(HDShaderIDs._MidSegmentB, data.hableCurve.uniforms.midSegmentB);
                        ctx.cmd.SetGlobalVector(HDShaderIDs._ShoSegmentA, data.hableCurve.uniforms.shoSegmentA);
                        ctx.cmd.SetGlobalVector(HDShaderIDs._ShoSegmentB, data.hableCurve.uniforms.shoSegmentB);
                    }
                    else if (data.tonemappingMode == TonemappingMode.External)
                    {
                        ctx.cmd.SetGlobalTexture(HDShaderIDs._LogLut3D, data.externalLuT);
                        ctx.cmd.SetGlobalVector(HDShaderIDs._LogLut3D_Params, new Vector4(1f / data.externalLuT.width, data.externalLuT.width - 1f, data.lutContribution, 0f));
                    }

                    ctx.cmd.SetGlobalVector(HDShaderIDs._Params, data.miscParams);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, data.material, shaderPass, MeshTopology.Triangles, 3);
                    m_MobileGradingHash = hash;
                    m_MobileGradingValid = true;
                });
                return lut;
            }
        }
    }
}
