# HDRP Mobile Renderer Benchmark

Experimental fixture for Unity 2022.3.17f1 / this HDRP 14.0.9 fork.

1. Import this sample, open `Benchmark.unity` by itself, and assign `Mobile.asset` in Graphics and every included Quality level.
2. Select Android, Linear color space, Vulkan only, ARM64/IL2CPP and High Quality Lightmap Encoding. Enable Frame Timing Stats for CPU/GPU timing queries.
3. Keep Fixed 1080p enabled, Render Scale = 1, shadows enabled and Bloom disabled. The sample uses fixed exposure EV100 14, a 100,000 lux main light and four 600 lumen local lights.
4. Bake with a separate Desktop asset after changing static content. Baked lightmaps, Light Probes and the reflection cubemap are included. Restore the Mobile asset before building.
5. Install on a test device and run without interruption. The script warms up for five minutes, measures ten minutes and writes JSON to `Application.persistentDataPath`. It records missing CPU/GPU timings as -1. Record thermal conditions separately using `temperatureObservation`.

The route repeats every 30 seconds. Contents include static blocks, one skinned capsule, cutout cards and a transparent sphere. The asset disables unsupported-material diagnostic draws; run the content audit before adding content. This is a reproducible starting fixture, not a representative production-game workload or a certification of phone coverage.

Retain the device/driver, content version, thermal observations and GPU captures together with each JSON report. Acceptance requires fixed internal 1920×1080 and P95 ≤ 16.67 ms; run separately on Adreno and Mali. No physical-device performance result is included in this sample.

See the package's `Documentation~/Mobile-Renderer.md` for feature limits and build validation.
