# Mobile / Desktop shared post-processing

Validated 2026-09-26 with Unity 2022.3.17f1, HDRP 14.0.9, Windows GPU rendering.

## Shared implementation

- Desktop and Mobile both call ColorGradingUtilities for white balance, color adjustments, channel mixing, shadows/midtones/highlights, lift/gamma/gain, split toning and custom tone-curve parameter conversion.
- LutBuilder3D.compute and MobileComposite's raster LUT pass include the same ColorGradingCommon.hlsl. This shares curve evaluation, grading order, sRGB/ACEScg transforms and all SDR tonemapping modes, including full ACES and external LUTs.
- Mobile uses a cached RGBA16F 2D strip LUT at the asset's configured LUT resolution. Parameter changes invalidate the cache; overridden curves and external LUT contents are regenerated. Neutral cameras bypass it. Pipeline disposal releases the cache.
- Bloom shares Desktop's quadratic soft-threshold implementation and threshold conversion; intensity and tint match Desktop parameter semantics. Mobile retains its three-level downsample filter.
- Postprocess, ColorGrading, Tonemapping, Bloom and Antialiasing Frame Settings gate the corresponding work. Exposure stays independent. Reflection capture still bypasses all camera post-processing.

## Verification

| GPU backend | Scope | Result |
| --- | --- | --- |
| D3D11 | All MobileImageTests, including 21 new post-process cases | 137 passed, 0 failed, 6 Vulkan-only cases skipped |
| Vulkan | 21 new cases plus existing forward/deferred Volume post-process cases | 23 passed, 0 failed, 0 skipped |

The new cases compare the raster LUT against the actual Desktop compute LUT across seven tonemapping/color-space combinations; render every newly supported grading Volume; exercise ColorGrading and Postprocess camera overrides; verify external LUT content updates and invalid-LUT fallback; and check Bloom threshold/tint.

Reports are in the workspace at MobileValidationRun/mobile-shared-post-final-dx11.xml and MobileValidationRun/mobile-shared-post-final-vulkan.xml. Reproduce in the existing disposable validation project with MobileValidationRun/run-post-validation.ps1. The script synchronizes changed package files into that project and launches Unity hidden; it prints the process ID.

## Editor black-LUT recovery (2026-09-26)

The imported LUT's parameter hash alone does not establish that its GPU contents are still valid. A regression test clears the existing allocation without releasing it: before the fix, subsequent renders remain black (expected red 0.283447, actual 0). Edit-mode rendering and Frame Debugger replay now rebuild the LUT instead of trusting that cache. First-use LUT variants are compiled synchronously in the Editor before the draw can mark the cache valid, and the LUT viewport is explicit. Player caching is preserved.

D3D11 targeted verification: 21 passed, 0 failed, including cleared-content recovery, a cold shader variant with asynchronous compilation enabled, all grading volumes, tonemapping modes, external LUT updates and desktop/raster LUT comparisons. Report: MobileValidationRun/mobile-lut-black-fixed-d3d11.xml. This reproduces the stale-black-cache failure mechanism; it does not establish why the original editor capture first became black.

## Camera anti-aliasing configuration (2026-09-26)

Mobile FXAA follows HDAdditionalCameraData.antialiasing. Scene View follows HDAdditionalSceneViewSettings.sceneViewAntialiasing, including its post-process visibility toggle. Postprocess and Antialiasing Frame Settings must both be enabled. Legacy MobilePostProcessing.fxaa data is retained for profile deserialization but ignored. Only selecting FXAA enables the current implementation; None, SMAA and TAA do not silently activate it.

D3D11 verification: 7/7 passed, covering camera modes and Frame Settings, a real Scene View with independent camera/preferences and effect visibility, and forward/deferred image tests proving that legacy Volume values and Volume weights cannot override camera AA. Report: MobileValidationRun/mobile-camera-aa-d3d11.xml.

## Remaining limits

This shares the grading and tonemapping core, not the complete Desktop post-process graph. Mobile still lacks depth of field, motion blur, temporal AA, lens distortion, chromatic aberration, vignette, film grain, dithering, custom post effects and HDR display output. Bloom scatter, dirt, anamorphism and Desktop reconstruction are also not implemented. No Android device performance or full Desktop camera image regression is claimed by these tests.
