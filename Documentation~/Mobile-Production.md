# Mobile production workflow

Runtime sky irradiance and GGX reflections are available through the Mobile asset's Environment Lighting group. See [Mobile environment lighting](Mobile-Environment.md) for baked-probe priority, update budgets, limitations and validation.

Open **Rendering > HDRP Mobile > Settings**. Select a camera to inspect the active pipeline, Volume mask/anchor, requested/effective exposure, shared Volume parameters, sampled GPU EV and baked-lighting state. Profile buttons select the source asset; blended values are read-only. The window samples GPU history asynchronously at most twice per second. Player rendering has no diagnostic readback.

## Exposure

Direct lighting uses HDRP physical light units: the URP-style direct diffuse and specular BRDF is normalized by `1/PI`. Environment lighting, lightmaps and emission retain their separate radiance conventions. At fixed exposure, the direct-light contribution is therefore 0.31831 times the earlier unnormalized implementation; automatic exposure can adapt to this change within its configured limits.

Mobile asset inspector edits now update the live backend when shader resources and renderer mode are unchanged. Exposure history survives ordinary parameter changes and gaps between editor repaints; mode changes, explicit reset, runtime camera interruptions and actual backend replacement still reset it. Automatic exposure continues adapting to lighting changes rather than restarting from the Fixed Exposure seed.

Scene View copies the scaled Mobile depth into the final camera target and draws both Gizmo subsets after compositing. Gizmos do not enter the exposure meter or bloom. Depth-tested editor drawing is occluded by depth-writing scene geometry at full and reduced render scale; transparent materials retain their existing depth-write behavior, and Handles configured with an Always depth test deliberately remain visible.

Automatic exposure now meters a 64/128/256 square HDR sample grid (128 by default) and reduces weighted EV values to a persistent RG32F pair per camera. The meter runs before Bloom/grading/tonemapping, removes the current pre-exposure, and updates next-frame exposure on the GPU. It uses Core's physical-camera conversion and luminance adaptation functions. Camera exposure is applied before RGBA16F storage, including sky and exposure-weighted emission.

| Requested setting | Mobile behavior |
| --- | --- |
| Fixed / Physical Camera | Immediate shared Volume/camera parameters |
| Automatic | Geometric mean, Average or Center Weighted |
| Automatic Histogram / Curve Mapping | Automatic; percentile and curve controls ignored |
| Spot / Mask / Procedural meter | Center Weighted; masks/targets ignored |
| Compensation, limits, target gray, adaptation | Shared HDRP parameters |
| Exposure Control disabled | Multiplier 1, history released |
| Scene View manual exposure | Immediate override, no automatic history |

`MobileRenderSettings.meteringResolution` selects the sample-grid size. `MobileExposureControl.Reset(Camera)` resets exposure after camera cuts/teleports. First frame uses Fixed Exposure minus Compensation, clamped to the automatic limits; the existing project default seed remains EV 10. Mode changes, pipeline replacement and interrupted camera rendering reset history. Destroyed/inactive cameras are pruned. Desktop history is not copied and its serialized mode is never changed. Metering resolution and rendering differences mean complete scenes are not pixel-identical to Desktop.

## Materials and baked lighting

Continue authoring HDRP/Lit (Standard metallic) and HDRP/Unlit. Mobile retains the existing URP-style BRDF. Supported inputs: UV0 base/emission, HDRP Mask Map remap, tangent normals, Detail Map UV0/UV1 with HDRP R/albedo, AG/normal, B/smoothness semantics and Mask Map B/detail mask, double-sided normals, alpha clipping, emission exposure weight and existing Alpha/Additive/Premultiply behavior. Premultiply expects premultiplied texture RGB. The material Inspector reports unsupported properties without converting resources. Desktop Meta passes remain responsible for baking.

Lightmaps support NonDirectional and CombinedDirectional, with HDR lightmap encoding required for Android. Dynamic meshes use SH probes. Baked lights are excluded from realtime light lists; Mixed Baked Indirect retains realtime direct lighting. Two native reflection-probe bindings are blended, with influence-box projection for box probes. Symmetric blend distance is derived conservatively from the smallest HDRP face distance; custom proxy volumes are reported as unsupported. Probe texture, box-projection and blend settings are restored when Mobile releases ownership.

Shadowmask/Subtractive, LPPV, realtime GI/reflections, Shader Graph, advanced Lit models, refraction and soft particles remain unsupported. Audit loaded scenes before building; an unsupported feature is not silently converted.

## Bake workflow

Save one scene and assign Lighting Settings. Use **Bake Saved Scene via Desktop** in Settings (or **Rendering > HDRP Mobile > Bake Loaded Scene**). Preflight checks missing UV2, lightmap references and unsupported Mixed modes. Baking temporarily selects Desktop HDRP and Progressive CPU, then bakes HDRP reflections. The workflow restores Graphics/Quality pipeline and lighting settings on success, failure or cancellation; only successful output is saved. Use **Cancel Bake** while running. Multi-scene baking and baking while the Editor targets Android are intentionally rejected; switch the Editor to a supported Desktop target first.

The lightmapper itself writes output files during baking. Existing referenced lightmaps, lighting data and baked reflection textures are backed up and restored on cancellation/failure; original renderer and probe references are restored too. New generated files are retained without adopting their scene references, so rollback cannot delete unrelated assets. The scene is saved only after success. Review any unused generated files manually after a cancelled first bake.

For reproducible acceptance, run `MobileProductionValidation.BakeAcceptance` in an isolated batch project. It creates `Assets/MobileBenchmark`, bakes both lightmap modes, reopens the scene, checks lightmap/SH/reflection references and captures linear EXR images. `MobileRenderValidation.BuildAndroid` builds the fixture as a Vulkan ARM64 IL2CPP APK. These batch entry points exit Unity and must not run in the authoring Editor.

`MobileProductionValidation.WorkflowAcceptance` exercises cancellation/restoration followed by the same asynchronous bake service used by the Settings window. It also adds the Detail material fixture and checks the successful scene reload. See [validation report](Mobile-Production-Validation.md) for measured results and boundaries.

All batch validation entry points require a `.hdrp-mobile-validation` file in the disposable project root containing that copy's absolute `Assets` path. `CancelActiveBakeAcceptance` verifies cancellation while the lightmapper is running, including existing file/import-setting hashes and reloaded output. Never create the marker in the authoring project.
