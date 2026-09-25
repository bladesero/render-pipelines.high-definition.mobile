# Mobile deferred

Select **Renderer Mode = Mobile**, then **Rendering Path = Deferred** on the HDRP asset. Existing assets remain Forward. `Auto` selects explicit native subpasses on Android Vulkan after a one-time GPU color/depth input-attachment probe. Windows D3D11/D3D12 and desktop Editor Auto use Sampled: stored GBuffer textures, a separate sampleable depth copy, ordinary render-target switches and no native/tile-storage pass. Explicit Native remains available on Vulkan for validation and is rejected on DirectX. Forced Native reports an error when unavailable. Unsupported compact formats, MSAA targets and per-light object filtering fall back to Forward in Auto/Sampled, with a reason. Alpha-preserving rendering requires Forward; deferred scene output has alpha 1.

Windows Player builds require explicit D3D11/D3D12 Graphics APIs (disable Auto Graphics API) and Deferred Execution = Auto or Sampled. Both APIs share Unity's `d3d11` shader compiler target; no `d3d12` pragma is needed. The isolated-project build entries `MobileRenderValidation.BuildDeferredD3D11` and `BuildDeferredD3D12` produce Windows validation Players using the extended layout.

## Compact contract

Mobile shaders are built-in references on `HDRenderPipelineRuntimeResources.mobile`, with HDRP `[Reload]` paths for Editor recovery. Pipeline assets no longer serialize or expose per-asset Mobile shader assignments. Existing assets with missing legacy references use the built-in group automatically; Player builds include it through HDRP Global Settings runtime resources.

| Attachment | Format | Data |
| --- | --- | --- |
| RT0 | R8G8B8A8_SRGB | linear base color through sRGB RGB storage, linear metallic alpha |
| RT1 | R8G8B8A8_UNorm | float oct world normal encoded to RG8, smoothness, AO |
| RT2 | B10G11R11_UFloatPack32 | pre-exposed emission + BRDF/AO-weighted GI, then additive direct lighting |
| Depth/stencil | D24_UNorm_S8_UInt | depth and material classification |

Color attachments total 96 bits/pixel; depth/stencil adds 32. Uncoated Standard opaque/alpha-clipped Lit writes the compact GBuffer. Other supported material types, Unlit and sorted transparency use Forward; no material is converted to Standard.

Stencil bits 0/1 are ReceiveShadowsOff/SpecularHighlightsOff; bit 2 reserves Subtractive; bit 3 denotes SpecularSetup; bits 4–6 preserve HDRP Lit IDs 0–5, with 6 ForwardOnly and 7 background. Bit 7 is the per-material user switch. Geometry replaces all eight bits; lighting compares the lower seven and never writes stencil. The mobile stencil properties are separate from desktop HDRP state. Material keyword validation updates them without changing MaterialID.

The native compact path records geometry, additive lighting/fog, then Forward/sky/transparency in three subpasses of one explicit Core RP 14 render pass. RT0/RT1 have no backing textures or stores. RT2 is stored and is never declared as an input attachment. Depth is stored for SceneView/gizmos; otherwise it can be discarded. Post effects run outside the native pass. Sampled uses the same formats and shading, with an independent R32F depth copy for sampling while D24S8 remains bound for stencil.

Up to 32 visible point/spot lights use sphere/cone volumes and exact fragment range/angle tests. Camera-inside and clipping intersections use a conservative full-viewport scissor/fullscreen fallback. Transparency retains the existing per-object four-light limit. Directional CSM (up to six cascades) and Point/Spot atlas shadows share the Mobile shadow backend, with independent atlases capped at 4096 and PCF up to 4×4. Both respect the ReceiveShadowsOff stencil bit and Lighting / Mobile Shadows Volume settings. Exposure, lightmaps, SH, reflection probes and post effects also use the existing mobile backend. Fog blends once after deferred lighting and individually in Forward.

## One optional RGBA8 attachment

**Deferred Fresnel / Coat RT** allows exactly one additional `R8G8B8A8_UNorm` attachment: RGB stores SpecularColor F0, A stores the clear-coat mask. Standard reconstructs F0 from base color and metallic to avoid additional F0 quantization. The extended layout uses four color targets, **128 bits/pixel for color and 160 including D24S8**. RT0–RT2 retain the compact formats.

Standard and SpecularColor (including clear coat) use this deferred layout. With the option off, only uncoated Standard is deferred. SSS, Anisotropy, Iridescence and Translucent render through MobileForwardOnly, as do SpecularColor/coated Standard when the extra attachment is disabled or four MRTs are unavailable. The material's HDRP ID and authored parameters remain intact. Unsupported HDRP shaders/features continue to be audited.

Forward fallback retains the Mobile extended BRDF, GI, diffusion-profile transmission, shadows, exposure and fog, and the existing four-local-lights-per-object limit. **Screen-space SSS diffusion is currently unavailable**: no SSS diffuse/feature targets or neighbor filter are allocated. Anisotropy and iridescence retain their forward evaluation.

Native uses RT0, RT1 and the optional RT3 as transient input attachments without backing textures or Store; lighting writes only RT2. Forward fallback stays in the third subpass. Sampled stores the same four-target layout and retains its independent depth copy. No additional material feature or HDR lighting targets are allocated.


## Validation and device acceptance

Scene View lifetime: the Mobile RenderGraph advances its own context counter, including edit-mode repaints, so the Core RP pool can retire old resize allocations even when `Time.frameCount` stays unchanged. Each camera owns its D24S8 attachment. Resize validates a new allocation before replacing the old one; lost textures are recreated even at the same size. Closing/deactivating a camera, switching to Forward, or disposing the backend releases the corresponding depth handles. RTHandle is the sole owner of each depth texture.

The resize regression alternates Scene View and a differently sized camera, checks every pixel immediately after each resize, simulates lost depth storage, and asserts that obsolete pooled color textures are destroyed. Restoring `Time.frameCount` in an isolated validation copy reproduces the stale-pool assertion at both render scales; the fixed D3D11 and D3D12 runs pass. The Vulkan Deferred image run passes 47 cases (two DirectX-only skips), including Sampled and Native resize/depth recovery at full and half resolution.

`MobileDeferredContractTests` exercises stencil encoding, resource loading and material routing. `MobileImageTests.Deferred*` exercises compact flags, local lights, cutout/cascades, Forward fallback, camera resizing, Native/Sampled agreement, material parameter preservation and transmission. GPU tests require a real graphics device; do not use `-nographics`.

The isolated-project batch entry `UnityEditor.Rendering.HighDefinition.MobileRenderValidation.ExportDeferredAndroid` enables Deferred/Auto/Extended Lit on the validation benchmark and exports a Vulkan ARM64 IL2CPP Gradle project. It requires the `.hdrp-mobile-validation` marker described in [validation](Mobile-Validation.md), and must not be run in an authoring project. Export is a compile/stripping check, not APK installation or device acceptance.

Validation on 2026-09-25 for the four-target layout: D3D11 passed 163 Mobile tests (one Vulkan-only skip); D3D12 passed 161 (one Vulkan-only skip, before the two additional mixed-material fog cases). The final desktop Vulkan Deferred run passed 56 with two DirectX-only skips. Tests assert actual submitted color attachment counts of three/four, material-only fallback, parameter preservation, cutout/occlusion/fog and Native/Sampled agreement. The final Vulkan run includes the SpecularColor fog correction. Desktop switching image regressions remain covered.

A Windows D3D12 Development Player and an Android Vulkan ARM64 IL2CPP Gradle export using the four-target layout built successfully, including shader compilation and stripping. Final Vulkan regression also covers the unique ForwardOnly pass and preserved material/user stencil flags.

Desktop Vulkan input-attachment tests are correctness evidence only. Adreno/Mali acceptance is still pending: capture each GPU family and confirm native pass/subpass continuity, input attachments, RT0/RT1 discard in compact, RT2 Store without copies, then record GPU time and external bandwidth. No Android device was connected during implementation; no mobile tile-storage or performance claim is made.
