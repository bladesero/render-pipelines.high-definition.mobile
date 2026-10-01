    // Mobile editor passes need this keyword to match single-sided picking.
    // Desktop Unlit has no normal buffer or double-sided lighting constants.
    #undef _DOUBLESIDED_ON
    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch

    //-------------------------------------------------------------------------------------
    // Variant
    //-------------------------------------------------------------------------------------

    #if !defined(HDRP_SHADOW_ALPHA_VARIANTS)
    #pragma shader_feature_local _ALPHATEST_ON
    #endif
    // #pragma shader_feature_local _DOUBLESIDED_ON - We have no lighting, so no need to have this combination for shader, the option will just disable backface culling

    #pragma shader_feature_local_fragment _EMISSIVE_COLOR_MAP
    #pragma shader_feature_local_raytracing _EMISSIVE_COLOR_MAP

    // Keyword for transparent
    #pragma shader_feature _SURFACE_TYPE_TRANSPARENT
    #pragma shader_feature_local _ENABLE_FOG_ON_TRANSPARENT

    #pragma shader_feature_local _ADD_PRECOMPUTED_VELOCITY

    //-------------------------------------------------------------------------------------
    // Define
    //-------------------------------------------------------------------------------------

    //-------------------------------------------------------------------------------------
    // Include
    //-------------------------------------------------------------------------------------

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/FragInputs.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPass.cs.hlsl"

    //-------------------------------------------------------------------------------------
    // variable declaration
    //-------------------------------------------------------------------------------------

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Unlit/UnlitProperties.hlsl"
