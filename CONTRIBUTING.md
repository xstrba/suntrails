# Contributing

World aims for smooth exploration, a cozy and dreamy landscape, and an interface that children can understand.

## Local development

Follow [README.md](README.md) to install the engine and build the game. Use the .NET engine version and SDK versions recorded in `project.godot` and `World.csproj`.

Make focused changes and keep unrelated formatting and generated files out of a patch. Follow the existing C# namespace, naming, and readable method style. Reuse the existing camera, terrain, storage, and localization helpers when they fit.

Keep source assets, their Godot `.import` settings, and `.uid` files together. Imported glTF models may depend on adjacent `.bin` files and textures. Preserve asset licenses and provenance.

## Check your change

Run `make build` for code changes. After adding or changing imported assets, run `make import GODOT=/absolute/path/to/Godot_mono` and inspect the import output.

There is currently no checked-in automated test suite. Add a focused test when it catches a concrete regression. Exercise the feature from its ordinary in-game entry point.

For a lightweight startup check, use the engine directly.

```sh
/absolute/path/to/Godot_mono --headless --path . --quit-after 120
```

Headless checks cover startup and suitable logic. Verify rendering and input capture in a graphical run. For a screenshot after the world loads, use `make shot`.

### World and rendering changes

Compare the same spawn point, camera angle, time of day, graphics preset, and resolution before and after the change. Allow chunks and the tree atlas to finish loading. Include movement through a chunk boundary when changing streaming.

The HUD exposes FPS, triangle counts, draw calls, objects, and pending chunks. Use the Godot profiler to distinguish CPU, GPU, allocation, and upload costs when investigating a slowdown. Report the conditions alongside measurements.

Look at dawn, day, dusk, and night. Check tree silhouettes, distant foliage, water, terrain transitions, and low-angle or prone viewpoints. Keep distant detail stable enough for photography.

### UI and photo changes

Check the changed flow in both English and Czech, at a typical desktop size and a narrow window around 780 × 700. Confirm that longer text wraps, buttons remain reachable, keyboard focus is visible, and Esc returns to the expected screen.

For photo changes, include an older PNG, a new metadata-bearing PNG, a duplicate rename, and a folder that cannot be accessed. If captures are involved, try a short exposure and a longer tripod exposure. Use temporary photo folders for tests.

Keep the gallery's scrolling work bounded to nearby cards. Inspect an original image in the viewer to verify zoom and panning independently of thumbnail sizing.

### Translations

Keep English source messages and Czech entries in `locale/cs.po` together. Static Godot controls use automatic translation. Use `UiText.Get`, `UiText.Format`, and `UiText.Count` for composed text, numeric formatting, and plural counts.

Keep filenames and filesystem paths literal. Use invariant culture for machine-readable arguments and serialized data.

If gettext is installed, validate the catalog.

```sh
msgfmt --check-format -o /tmp/world-cs.mo locale/cs.po
```

## Sharing changes

Summarize the user-visible behavior and the checks performed. Include before-and-after images for visual changes and measured conditions for performance work. Mention anything that still needs graphical verification.

Use focused commits with descriptive messages. Review `git diff --check` and the changed-file list before committing.

## Project skills

The skills under [`.agents/skills/`](.agents/skills/) provide more specific workflows for performance, art direction, simple UI, and photo-library integrity. Use the skill relevant to the task.
