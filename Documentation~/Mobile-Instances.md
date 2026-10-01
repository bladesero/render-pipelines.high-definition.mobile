# Mobile CPU culling and instancing

Mobile Instance Groups provide opt-in spatial culling and explicit GPU instancing for repeated MeshRenderers. The pipeline default remains `Disabled`. This implementation targets the existing Android Vulkan / Windows DX11/DX12 Mobile backend; it does not enable Desktop GPU-driven rendering.

## Enable

1. In the HDRP asset, select **Mobile → Rendering → Batching → Instance Submission → Cpu Instanced**.
2. Add **Rendering → HDRP Mobile Instance Group** to a scene object. Use the component context menu **Collect Child Renderers**, or populate its renderer array explicitly.
3. Enable **GPU Instancing** on the referenced HDRP/Lit or HDRP/Unlit materials. Materials are not edited by the runtime; it owns temporary copies for instance variants.
4. Set the pipeline instance distance and batch size (1–128). Group draw distance overrides the pipeline distance; zero inherits it. Bounds padding is a conservative world-space expansion.
5. Optional group LOD meshes must have the same submesh/material layout as every source mesh. Supply one descending screen-height threshold per replacement mesh. Native LODGroups retain native rendering.

Collection is an authoring operation, not a scene scan performed every frame. After changing membership, mesh, LOD configuration, or replacing lightmap data at an unchanged index, call `group.MarkDirty()`. Transform motion, active/enabled state, material property edits and lightmap index changes are observed on subsequent camera renders. A changed mesh/material reference falls back until the group is marked dirty. Shared group LOD meshes are intended for groups containing one prototype.

Each renderer must appear exactly once across active groups; duplicate membership falls back to native rendering.

## SRP Batcher

**Mobile → Rendering → Batching → SRP Batcher** controls the existing HDRP asset `enableSRPBatcher` setting (default on). It is reapplied before camera rendering, so changing this setting does not require pipeline recreation. Backend disposal restores the previous global state; an older backend cannot reset a replacement backend's setting.

Ordinary compatible renderers use native `DrawRenderers` with SRP Batcher. Explicit instance groups use `DrawMeshInstanced` and remain available with SRP Batcher either on or off. The two submission paths can coexist in the same pass. Renderer MaterialPropertyBlocks retain the native non-SRP-batched fallback and their per-object values. Enabling SRP Batcher does not imply that every renderer or material is batchable, nor does it merge every draw into one GPU draw call. See [Unity's SRP Batcher requirements](https://docs.unity3d.com/2022.3/Documentation/Manual/SRPBatcher.html).

Lit/Unlit Desktop and Mobile passes share `ShaderVariablesPerDraw.hlsl`. This keeps the built-in constant buffer layout identical throughout their shared SubShader, including shadow/depth passes. The existing Desktop fields retain their offsets and Mobile light/reflection feature blocks are appended. Material properties continue to use the shared Lit/Unlit `UnityPerMaterial` declarations. Mobile still avoids the Desktop lighting/global buffers.

## Culling and submission

Registered objects are partitioned into sparse cells (default 16 world units), with at most 256 objects per cell block. Cell bounds enclose every member's full transformed bounds, including replacement LODs and padding. Culling uses the camera's culling matrix, layer mask, conservative bounds distance and per-instance AABBs. LOD uses projected size, orthographic camera size, quality LOD bias and 10% hysteresis stored per camera. Camera jumps reset hysteresis. Cell storage, instance payload arrays and draw command scratch storage are reused.

The existing synchronous RenderGraph execution finishes before per-camera ownership is released. Explicit draws execute inside the existing Forward, sampled GBuffer or native GBuffer subpass, including ForwardOnly materials. No new render targets, depth copies or compute dispatches are introduced.

Each draw supports up to 128 instances. Matrix and lighting buffers are separate; the seven SH vectors occupy a separate constant buffer to keep each payload buffer within 16 KiB. Batch compatibility includes mesh, submesh, source material and lightmap texture set. Layer-specific local light indices and the main-light mask are per instance. Lightmap scale/offset, SH and default-reflection bindings are supplied explicitly; HDRP reflection probes use the existing world-space atlas. Local lights are selected from the existing Mobile light table, capped by the asset's per-object limit.

## Shadows and ownership

This first backend deliberately retains the source MeshRenderers as **native shadow casters** during the camera render. Eligible sources temporarily use `ShadowsOnly`; non-casters are suppressed with `forceRenderingOff`. Their original values are restored in `finally`, after submission. No serialized renderer state is permanently changed.

Consequently the existing directional cascade and point/spot shadow culling remain independent of main-view visibility, and native `GetShadowCasterBounds` continues to work in scenes composed entirely of managed instances. Camera distance culling does not remove an in-range shadow caster. Instance shadow distance is independently configurable and clamped to Mobile shadow distance. Shadows use the original source mesh/LOD and native submission; the custom group LOD applies only to main-view geometry.

This is an implementation refinement of the design's proposed explicit shadow submission: the first version does **not** remove native renderer culling overhead or batch custom shadow draws. It avoids duplicating native shadow matrix/split generation. A future BRG or renderer-free backend must implement those contracts before removing source casters.

Scene View, preview and explicit reflection-bake cameras use the original Renderer path. Sources are restored between cameras, so editor picking, baking and Desktop mode retain their normal inputs. Destruction/disposal and failures also restore the temporary state.

## Fallbacks

The whole renderer stays native if any of its submeshes is incompatible. Current exclusions include transparent materials, unsupported shaders, disabled material instancing, MaterialPropertyBlocks, static-batched meshes, native LODGroups, negative/degenerate transforms, two-sided/shadows-only renderer casting modes, realtime lightmaps, proxy-volume probes, invalid lightmap textures and native reflection probes without active HDRP probe data. SkinnedMeshRenderers are outside group membership. Existing disabled or force-hidden objects are not made visible.

There is no custom baked occlusion culling or Hi-Z occlusion in this backend. Hidden instances may be submitted conservatively. Jobs/Burst, BRG, offline instance data assets, custom shadow LOD and renderer-free scenes remain later stages requiring measurements and separate validation.

## Diagnostics and validation

`MobileInstanceDiagnostics.lastCamera` reports the last completed camera ID, candidates, fallback count, cells/visible cells, visible instances, explicit draw count and estimated payload upload bytes. Upload bytes are a CPU payload estimate, not driver-measured bandwidth. Native shadow draws are not included. Profiler samples: `Mobile Instances.Prepare`, `.Cull`, `.Submit`.

`MobileInstanceTests` covers conservative bounds, frustum classification, distance and LOD hysteresis. Instance-specific `MobileImageTests` compare native and explicit rendering across Forward/Deferred, local lights, lightmaps, cutout/normal maps, camera isolation, movement, batch splitting, shadow-only native casters and restoration. Vulkan tests explicitly exercise native deferred subpasses. Use existing Mobile image/shadow/lifecycle tests for regressions.

`MobileSRPBatcherTests` checks the asset setting, live changes, ownership restoration and engine-reported Lit/Unlit compatibility for both Desktop and Mobile passes. Image tests compare SRP on/off, mixed native/explicit draws, keyword changes and MPB fallback. `SRPBatcherNativeRenderLoopIsExecuted` records the Editor's native `RenderLoop.DrawSRPBatcher` marker and requires calls when enabled and none when disabled.

Unity 2022.3.17f1 EditMode exhibited incorrect first-use material values when a new shader variant was first prepared inside `Camera.Render`, even with asynchronous compilation disabled. The focused diagnostic recorded Detail red 0.066 on first draw versus 0.069 on the second draw and with SRP off; explicitly compiling the pass beforehand made all draws agree. An `editor_sync_compilation` experiment did not remove the issue and was not retained. Image oracles now prepare material passes before camera rendering without relaxing pixel tolerances. The mixed-material Forward/Deferred comparison additionally establishes its baseline after one warm-up draw: with two new variants of the same shader, its first Forward draw transiently used the other material color even after pass compilation; the second Forward draw, Deferred draws and SRP-off draws agreed. This is test preparation, not a runtime fix for Editor cold-variant first-use behavior. Player/device behavior remains unverified.

For performance acceptance, measure native vs instanced scenes with 1k/10k/50k objects, low/high visibility and with/without shadows. Disable unsupported-material diagnostics, hold resolution and lighting constant, warm the device, then collect CPU/GPU p50/p95 and thermal conditions on Android Adreno and Mali. This feature does not imply a measured mobile speedup. Retaining native casters makes shadow-heavy scenes particularly important to profile.

## Validation record (2026-09-29)

Unity 2022.3.17f1, isolated `MobileValidationRun/Project`, Windows graphics devices:

| Run (under workspace `MobileValidationRun/`) | Passed | Failed | Skipped |
|---|---:|---:|---:|
| `mobile-instances-regression-dx11.xml`: Mobile render/image/deferred/shadow/lifecycle regression | 204 | 0 | 8 |
| `mobile-instances-final-vulkan.xml`: instance cases, including native deferred and extended GBuffer | 18 | 0 | 0 |
| `mobile-instances-confirm-d3d11.xml`: latest instance cases, including offscreen shadow caster | 17 | 0 | 2 |
| `mobile-instances-final-d3d12.xml`: latest instance cases | 17 | 0 | 2 |

The two skips in the final DirectX runs are Vulkan-only native subpass cases. Vulkan's run preceded addition of the offscreen-caster test; the runtime implementation was the same. Shader compilation and C# compilation succeeded. `git diff --check` passed.

One earlier final D3D11 attempt (`mobile-instances-final-d3d11.xml`) failed `InstanceCutoutNormalMapAndAmbientProbeMatchNative` at pixel 4032 (native red 0, explicit red 0.06451416). The isolated reproduction (`mobile-instance-cutout-d3d11.xml`) and complete confirmation run both passed with zero measured cutout differences; the pixel tolerance was not relaxed. The initial cause is **not identified** and remains a test instability to investigate. The original failure result is retained rather than overwritten. The test now saves native/explicit images and reports the difference region for future reproductions.

Rerun with `MobileValidationRun/run-instance-validation.ps1 -Api d3d11|d3d12|vulkan`; add `-Regression` for the broader suite. Run sequentially against the existing disposable validation project. No Android device, thermal benchmark, standalone player build or measured zero-GC/performance claim is included in this validation.

### SRP Batcher integration

Before sharing `UnityPerDraw`, the engine reported compatibility code 8 (built-in constant-buffer size differed between stages) for both Lit and Unlit. After sharing it, the Mobile and Desktop pass checks report code 0. The native recorder measured 3 `RenderLoop.DrawSRPBatcher` calls with the option enabled and 0 with it disabled on D3D11.

Initial unprepared-image runs are retained as `mobile-srp-regression-d3d11.xml`, `mobile-srp-material-repeat-d3d11.xml`, `mobile-srp-keyword-d3d11.xml` and `mobile-srp-fog-diagnostic-d3d11.xml`. They exposed the Editor first-use limitation described above. In the mixed-material diagnostic, the first Forward pixel was (0.122, 0.096, 0.071); subsequent Forward and SRP-off Forward were (0.072, 0.085, 0.112), while both Deferred draws and SRP-off Deferred were (0.071, 0.085, 0.109). Do not interpret prepared-image passes as a fix for that cold first draw.

| Final prepared-image regression | Passed | Failed | Skipped |
|---|---:|---:|---:|
| `mobile-srp-vulkan-check-vulkan.xml`: full Mobile suite, native deferred, instancing, Desktop round-trip and SRP checks | 227 | 0 | 2 |
| `mobile-srp-final-d3d11.xml`: full Mobile suite, sampled deferred, instancing, Desktop round-trip and SRP checks | 221 | 0 | 8 |

Vulkan skips are the two DirectX-only stored-texture policy cases; D3D11 skips are Vulkan-only native subpass cases. Vulkan also measured 3 native SRP calls with the setting enabled and 0 with it disabled. A background Android Editor device-scan exception was logged during that run, but no test failed. No Android device or player validation is implied by this Windows Vulkan result.
