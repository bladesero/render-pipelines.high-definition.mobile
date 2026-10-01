# SGSR2 on HDRP Mobile

Select the HDRP asset's Mobile renderer, then set **Upscaling** to **SGSR2Lite** or **SGSR2Ultra**. The default remains Bilinear. Render Scale controls input resolution; disable Fixed 1080p when the intended base size is the display size.

Lite reconstructs with five color taps and reads four additional corners for history rejection bounds. Both kernels record unaccumulated scene color and depth in a render-resolution scene-history pass. Ultra adds a render-resolution RGBA32UInt guides pass to precompute neighborhood bounds, dilated depth and camera motion. Half Precision is optional; measure both modes on the actual device. Ultra Sharpness changes output only, preserving unsharpened history.

The HDR reconstruction writes color and YCoCg history through MRT. RenderGraph 14 requires a transient display-resolution Depth16 attachment for this MRT pass; depth testing and writes remain disabled in the shader. The existing Mobile composite then applies bloom and grading/tonemapping at display resolution. This integration deliberately retains that composite pass: it supports existing HDRP post-processing and output orientation without attaching history to a possibly incompatible display swapchain. It therefore does not reproduce the upstream URP final-blit pass count or its measured timings.

SGSR2 owns projection jitter, disables FXAA during reconstruction, and restores the camera projection after rendering, including exception paths. Culling uses the original projection. Each camera has two RGBA16F display-resolution history textures and two RGBA16F render-resolution scene-history textures; history resets on resize, changed render scale/kernel/precision/projection, skipped frames, large camera cuts, or lost GPU textures. Destroyed/inactive camera histories are retired during subsequent rendering, and all resources are released with the backend.

Supported cameras: perspective Game cameras, including explicit RenderTextures, without stereo, at input dimensions no larger than the output. Scene View, Preview, orthographic, reflection and stereo paths retain existing behavior. Missing shader/MRT/integer-guide support falls back to the existing composite without jitter. Uses the Mobile backend's reversed-Z D3D11/D3D12 and Vulkan targets.

Camera motion is reconstructed from depth and camera matrices. There is no per-object velocity input. Current and previous unaccumulated color/depth are compared with both frames' projection jitter compensated. Filtered color and point depth are reprojected at their own sample positions. A 5x5 depth footprint marks uncertain silhouette coverage, including the background side, in the sign of cached depth. Such coverage and color ranges from both current and previous sample footprints tolerate changes caused by projection jitter. Stable history uses a bounded temporal update and avoids snapping to each frame's neighborhood box. Ultra sharpening uses a constant spatial strength and a temporally accumulated neighborhood mean, avoiding phase-dependent flicker.

To retain prompt updates for actual motion, each camera tracks Renderer transforms, bounds, skinned bone matrices and blend-shape weights. Changed renderers mark their previous and current projected bounds in a transient R8 reactive mask; removed/disabled objects mark their old bounds. Playing particle systems are conservatively reactive. With no marked bounds, the mask pass is skipped. Marked pixels, significant interior color/depth changes, and invalid reprojections clamp and reject history. This separates static sampling changes from moving silhouettes, rather than relying only on color/depth differences. Renderer discovery is shared across cameras in a player frame; pose checks remain per camera. Large animated bounds can become spatially reconstructed and show aliasing. Custom procedural draws, vertex-shader deformation and dynamically reassigned skin bones are not fully represented by this CPU tracking; per-object velocity/reactive inputs would improve those cases. Camera cuts below the automatic reset thresholds may need one frame to recover. HDR output swapchains and XR are not supported by this integration.

Source: https://github.com/saivs/com.saivs.sgsr2-optimized
The optimized shader is derived from Qualcomm's SGSR2 and Denis Zhukov's Lite/Ultra kernels. See SGSR2-LICENSE.md for the BSD 3-Clause terms and attribution.
Upstream snapshot: 2a669d5113d5acb5113d5c372eeeedaaed3f9e21 (2026-10-01).

## Temporal stability and dynamic-history rejection cost

Scene history adds one render-resolution raster pass and two render-resolution RGBA16F persistent textures: 16 * renderWidth * renderHeight bytes (about 7.9 MiB for 960x540 input). The snapshot inspects 25 local depth samples. Reconstruction adds filtered scene colors, point depths, a depth gather, four previous-color corner reads and reactive-mask sampling; Lite also reads four current-color corners. Moving bounds add an R8 render-resolution mask, viewport draws, CPU renderer discovery and pose comparisons. Ultra's still-camera guide/color optimization remains, but its display path is no longer the upstream two-fetch path. Android device rendering, GPU/CPU timings, memory allocations, power and thermal behavior must be measured; upstream timings do not describe this integration.

## Validation

Unity 2022.3.17f1 D3D11 and desktop Vulkan GPU EditMode runs pass 38/38 cases on each API (2026-10-02). No shader compilation or RenderGraph execution errors were found in either final run.
- Actual Lite/Ultra execution and history advancement, with FP16/FP32 Forward and FP32 Deferred.
- Projection restoration and disabling SGSR2.
- Textured output orientation, moving camera, render-target resize and native render scale.
- Moving foreground objects with a stationary/moving camera, Forward and Deferred, animated opaque color and depth-preserving transparent color changes.
- Off-center moving geometry checks reactive-mask viewport orientation.
- Stationary slanted thin lines at 0.5/0.75 render scale, FP16/FP32, wider silhouettes, stationary SkinnedMeshRenderer geometry, and high-contrast checker textures across a complete 32-frame Halton cycle after warm-up. The geometry must be visible; maximum frame-to-frame brightness change must stay below 0.15 and mean below 0.015.
- D3D and Android Vulkan compilation of all supported Lite/Ultra, precision and still/moving shader combinations.

The previous ghosting implementation lacked static temporal regression coverage. All six original thin-line cases failed with peak brightness changes of about 1.2–1.38 (MobileValidationRun/sgsr2-stability-before.xml). After the stability fix, both APIs pass 38/38, including the previous dynamic regressions; the 14 stationary cases have peaks of 0.028–0.043 and mean changes below 0.0045.

D3D11 results: MobileValidationRun/sgsr2-pixel-verified.xml; log: MobileValidationRun/sgsr2-pixel-verified.log.
Desktop Vulkan results: MobileValidationRun/sgsr2-pixel-vulkan.xml; log: MobileValidationRun/sgsr2-pixel-vulkan.log.
The earlier six stationary-camera dynamic cases failed before the ghosting fix (MobileValidationRun/sgsr2-ghost-before.xml) and remain covered. Shader compilation checks include Android Vulkan, but actual Android rendering and performance remain unmeasured.
