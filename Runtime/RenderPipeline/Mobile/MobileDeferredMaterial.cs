namespace UnityEngine.Rendering.HighDefinition
{
    // The mobile stencil contract is independent of desktop HDRP StencilUsage.
    internal static class MobileDeferredMaterial
    {
        internal const int ReceiveShadowsOff = 1, SpecularHighlightsOff = 2;
        internal const int SubtractiveMixedLighting = 4, SpecularSetup = 8;
        internal const int MaterialMask = 0x70, LightingMask = 0x7f, UserMask = 0x80;
        internal const int Standard = 0x10, ForwardOnly = 0x60, Background = 0x70;

        internal static int Encode(int materialType, bool receiveShadows, bool specularHighlights, bool user)
            => ((materialType & 7) << 4) | (receiveShadows ? 0 : ReceiveShadowsOff)
                | (specularHighlights ? 0 : SpecularHighlightsOff) | (user ? UserMask : 0);

        internal static bool IsSupported(Material material)
            => material != null && material.FindPass("MobileForward") >= 0
                && (!material.HasProperty("_MaterialID") || (material.GetFloat("_MaterialID") >= 0 && material.GetFloat("_MaterialID") <= 5));

        internal static void Validate(Material material)
        {
            if (!material.HasProperty("_MobileStencilRef")) return;
            bool opaqueLit = material.HasProperty("_MaterialID") && material.GetFloat("_SurfaceType") == 0;
            int type = opaqueLit ? (int)material.GetFloat("_MaterialID") : 6;
            material.SetInt("_MobileStencilRef", Encode(type, material.GetFloat("_MobileReceiveShadows") > 0,
                material.GetFloat("_MobileSpecularHighlights") > 0, material.GetFloat("_MobileUserStencilBit") > 0) | (type == 4 ? SpecularSetup : 0));
            if (material.HasProperty("_MobileForwardStencilRef"))
                material.SetInt("_MobileForwardStencilRef", ForwardOnly | (material.GetInt("_MobileStencilRef") & (UserMask | 0x0f)));
            material.SetShaderPassEnabled("MobileGBuffer", opaqueLit && type == 1 && material.GetFloat("_CoatMask") <= 0);
            material.SetShaderPassEnabled("MobileGBufferExtended", opaqueLit && (type == 1 || type == 4));
            material.SetShaderPassEnabled("MobileForwardOnly", !material.HasProperty("_MaterialID") || opaqueLit);
        }
    }
}
