---
name: cozy-art-direction
description: Refine World's cozy, dreamy landscape through lighting, atmosphere, color, vegetation, terrain, water, and photographic camera presentation while checking rendering cost and readability.
---

# Cozy art direction

Start by inspecting the current scene or screenshots and the user's requested change. Keep the landscape inviting, softly layered, and legible as the player explores and photographs it.

## Shape the image

- Use foreground, middle distance, and atmospheric distance to establish depth. Retain readable terrain and tree silhouettes.
- Favor natural greens, warm light, restrained highlights, and gentle atmospheric separation. Keep enough local contrast to navigate and frame a photo.
- Let dreamy softness come from coordinated lighting, fog, color, and camera depth of field. Evaluate how much detail remains at the subject and horizon.
- Keep wind and water movement calm and coherent across full-detail vegetation and impostors.
- Preserve useful lighting differences between dawn, day, dusk, and night. A single exposure or fog adjustment can flatten those differences.

## Find the owning code

- `scripts/World/DayNight.cs` owns time-keyed lighting, fog, and sky parameters.
- `scenes/Main.tscn` provides environment defaults.
- `shaders/sky.gdshader`, `water.gdshader`, and `terrain.gdshader` define the large surfaces.
- `TreeKinds.cs`, `Plants.cs`, `Forest.cs`, and the tree shaders define vegetation appearance and distribution.
- `scripts/Player/CameraModel.cs` and `PhotoMode.cs` define the photographic effects. Keep their aperture, focus, exposure, and shutter behavior physically coherent when adjusting presentation.

## Compare in the world

Use a fixed spawn, angle, time, and quality preset for before-and-after images. Check forest interiors, open meadow, shoreline, mountain distance, and crouched or prone views when relevant to the change.

Check clipped highlights, crushed shadows, fog transitions, foliage edges, water reflections, and mesh-to-impostor handover. Check both ordinary exploration and the photo viewfinder.

Measure rendering cost when adding transparency, extra samples, lights, reflection passes, denser foliage, or post-processing. Explain any visual-performance tradeoff using observed images and measurements.

For UI color changes, preserve readable text and clear focus against the landscape. Keep the established gallery palette unless the request calls for a new direction.
