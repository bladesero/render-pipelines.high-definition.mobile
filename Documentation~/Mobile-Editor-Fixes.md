# Mobile exposure continuity and Scene View gizmos

2026-09-25, Unity 2022.3.17f1, Windows D3D11.

Two exposure resets were removed: ordinary active Mobile Asset OnValidate no longer reconstructs a backend whose shader references are unchanged, and an editor repaint gap no longer counts as a camera interruption. The renderer mode and shader-resource checks retain the rebuild path for actual backend changes. No exposure value is copied to CPU or written back to a Volume Profile.

The final editor pass copies Mobile depth with a point-sampled raster SV_Depth pass, supporting render-scale changes without requiring matching depth formats or dimensions. Both Gizmo subsets render against the final color/depth target with neutral gizmo exposure. Moving gizmos after metering and post-processing prevents editor overlays from changing automatic exposure or blooming. Scene depth is also provided when the Scene View gizmo toggle is off, for editor Handles. Custom Handles depth-test choices remain respected; transparent objects occlude only when their material writes depth.

MCP regression job `ddf7e2c6b2854c918dc788f7ee3c6616`: **87 passed, 0 failed, 0 skipped**, 24.35 seconds. New coverage checks on-demand camera history retention across a simulated 100-frame gap and parameter edits, live asset validation retaining the actual pipeline/history texture, and final-target foreground/background depth comparisons at render scales 1.0 and 0.5. The latter exercises actual SceneView camera rendering and depth-tested geometry, rather than asserting only the presence of DrawGizmos calls. Existing material, sky, environment, exposure, shadow and lifecycle regressions also pass.

This update was compiled and GPU-tested in the editor. The Android APK from the previous environment-lighting delivery was not rebuilt for this editor fix.

After the regression suite restored the Desktop preview, its shadow RenderGraph emitted invalid-resource errors outside the passing test cases. Recreating the current pipeline cleared that transient state; a fresh Scene View/player-loop repaint then produced no console errors. No scene or Profile save/restore was performed during this recovery. The current scene's unsaved edits were retained.
