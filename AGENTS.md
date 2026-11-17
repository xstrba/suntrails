# Project instructions

## Product intent

World is a Godot .NET landscape exploration and photography game. Prioritize responsive movement, steady frame times, a cozy and dreamy outdoor atmosphere, and UI that children can understand.

Keep changes scoped to the requested outcome. Prefer the existing project structure and APIs. Make reversible implementation decisions and state any material assumptions in the result.

## Start here

- Read [README.md](README.md) for setup, controls, and the code map.
- Follow [CONTRIBUTING.md](CONTRIBUTING.md) for verification relevant to the change.
- Check the current working tree and preserve existing user work.
- Inspect the affected code before proposing an abstraction or replacing behavior.

## Code and runtime

- The project targets Godot .NET 4.7.2, .NET 8, and C# 12. `scenes/Main.tscn` is the main entry point.
- Build C# changes with `make build`. Use the Makefile's `GODOT` override for engine commands.
- Keep scene-tree changes and rendering-resource uploads on the main thread. Chunk workers generate data using the existing streaming path.
- Treat per-frame allocations, main-thread stalls, overdraw, and extra reflection or shadow passes as performance costs to measure.
- Preserve chunk upload limits, shared meshes and materials, MultiMesh batching, grass LOD, and tree impostors when editing their callers.
- Build focused tests when needed to catch a regression. The repository currently has no checked-in test harness. Temporary checks belong outside the source tree.
- A headless build or startup check leaves rendering, native dialogs, mouse capture, and actual desktop fullscreen to verify in a graphical session.

## UI and localization

- Favor clear action words, visible focus, readable controls, and a short path back. Use the existing gallery theme and pause/input ownership.
- Keep English and Czech complete for changed UI. Static controls translate through Godot. Use `UiText` for dynamic messages, dates, numbers, and plurals.
- Disable automatic translation for filenames and paths. Keep persisted data and command-line parsing culture-independent.
- Verify the changed flow with keyboard and mouse at desktop and narrow window sizes. Preserve capture, gallery, viewer, and Settings transitions.

## Photos and assets

- Preserve older PNG support, embedded metadata, unique saves, duplicate-name protection, and cancel behavior during exposures.
- Keep original-resolution viewing separate from small scrolling thumbnails. Release stale image references when selection or visibility changes.
- Exercise file mutations using temporary test folders. Preserve the existing confirmation and system-Trash behavior.
- Track source `.import`, `.uid`, model buffers, textures, and asset license notices. Generated `.godot/`, build output, and local captures follow `.gitignore`.

## Focused skills

Read the relevant skill when it would improve the task.

| Skill | Use when |
| --- | --- |
| [godot-performance](.agents/skills/godot-performance/SKILL.md) | Investigating frame time, streaming, vegetation, shader cost, or gallery memory |
| [cozy-art-direction](.agents/skills/cozy-art-direction/SKILL.md) | Changing lighting, color, atmosphere, vegetation, terrain, or camera presentation |
| [simple-game-ui](.agents/skills/simple-game-ui/SKILL.md) | Building or refining menus, gallery interactions, controls, localization, or navigation |
| [photo-library-integrity](.agents/skills/photo-library-integrity/SKILL.md) | Changing capture, PNG metadata, file operations, photo loading, or preferences |

`CLAUDE.md` and `.claude/skills` are relative symlinks to this file and the shared skills directory. Edit their targets to keep both agents aligned.
