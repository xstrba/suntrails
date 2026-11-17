---
name: godot-performance
description: Investigate and improve this project's Godot frame time, chunk streaming, vegetation rendering, shader cost, or photo-gallery memory with reproducible measurements.
---

# Godot performance

Use [CONTRIBUTING.md](../../../CONTRIBUTING.md) for baseline build and verification commands.

## Establish the workload

Record the engine version, hardware when available, viewport resolution, render scale, graphics preset, spawn, yaw, pitch, and time of day. Compare the same scene after pending chunks settle and tree impostors finish capture. Include walking through chunk boundaries for streaming work.

The HUD reports FPS, triangles, draw calls, objects, and pending chunks. Use profiler frame times and allocations to distinguish CPU work, GPU cost, startup, and streaming stalls. An uncapped run can use `--no-vsync`. Save the conditions and before-and-after measurements with the result.

Useful repeatable entry points are `make shot SPAWN=x,z,yaw,pitch` and the user arguments `--spawn=x,z,yaw,pitch --time=0.5 --preset=medium`. Pass engine and game arguments in their existing order, with game arguments after `--`.

## Follow the expensive paths

- `scripts/World/ChunkManager.cs` owns worker generation, chunk upload limits, MultiMesh batches, visibility, and grass density LOD. Keep worker output as data and upload resources on the main thread.
- `TreeAssets.cs` shares meshes and materials. `TreeImpostors.cs` captures the far-tree atlas at startup. Separate that startup cost from steady rendering.
- The current tree handover avoids cross-fade after a measured transparency slowdown. Measure any proposed transparency or fading change in dense foliage.
- `Water.cs` renders a second view. Compare reflection scale and enabled state through graphics settings. Inspect shadow distance, foliage overdraw, and grass before increasing detail counts.
- `PhotoGrid.cs` creates nearby cards and loads one thumbnail at a time. Preserve bounded textures while scrolling large folders. `PhotoViewer.cs` loads the selected original for zoom.
- Full-image decoding, PNG encoding, viewport readbacks, and exposure accumulation have different costs. Check capture latency and gallery memory separately.

Debug switches `--no-trees`, `--no-tree-shadows`, and `--tree-end=<metres>` can help isolate tree cost. Verify that a switch still takes effect after Settings initialization before drawing conclusions.

## Make the comparison meaningful

Change the bottleneck supported by the measurements. Preserve scene composition, draw-distance expectations, and image quality in the comparison. Check near and distant vegetation, low camera heights, water, and day/night lighting for artifacts.

Report measured conditions, the observed improvement or regression, and any remaining graphical verification. State when profiler or graphical access is unavailable.
