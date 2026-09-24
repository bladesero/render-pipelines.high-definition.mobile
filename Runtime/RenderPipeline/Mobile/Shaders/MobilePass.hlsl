#pragma target 3.5
#pragma only_renderers d3d11 vulkan
#pragma vertex MobileVert
#pragma fragment MobileFrag
#pragma multi_compile_instancing
#pragma multi_compile _ LIGHTMAP_ON
#pragma multi_compile _ DIRLIGHTMAP_COMBINED
#pragma shader_feature_local _ALPHATEST_ON
#pragma shader_feature_local _DOUBLESIDED_ON
#pragma shader_feature_local _NORMALMAP
#pragma shader_feature_local _MASKMAP
#pragma shader_feature_local _DETAIL_MAP
#pragma shader_feature_local _EMISSIVE_COLOR_MAP
#pragma shader_feature _SURFACE_TYPE_TRANSPARENT
#include "MobileMaterial.hlsl"
