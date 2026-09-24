# URP material reference

Test-only source from Unity Graphics commit `3d08aae65d6f0d39aac8f1cd2d8221c263da30c0`, whose Universal RP package version is **14.0.9**.

Source: https://github.com/Unity-Technologies/Graphics/tree/3d08aae65d6f0d39aac8f1cd2d8221c263da30c0/Packages/com.unity.render-pipelines.universal/ShaderLibrary

`BRDF.hlsl`, `Deprecated.hlsl`, and `SurfaceData.hlsl` are the upstream files. Only the two URP include paths in BRDF.hlsl are made relative; Core remains a normal package dependency. The original license is retained in LICENSE.md. Do not replace the oracle with a second copy of the mobile equations: these files provide independent GPU regression coverage against the pinned upstream implementation.
