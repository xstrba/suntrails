# World

A cozy, dreamy landscape to explore and photograph. Walk through procedural forests and meadows, watch the light change, and take photos with a camera that models focal length, aperture, shutter speed, ISO, focus, and handheld shake.

Built with Godot .NET, C#, and the Forward+ renderer. The interface supports English and Czech.

## Get running

Install Godot **4.7.2 .NET**, the **.NET 8 SDK**, and Make for the convenience commands. The project uses C# 12. Open `project.godot` in the .NET edition of Godot to work through the editor.

From the project directory, point Make at your engine executable.

```sh
make build
make import GODOT=/absolute/path/to/Godot_mono
make run GODOT=/absolute/path/to/Godot_mono
```

The Makefile also has a default Linux engine path. Override `GODOT` if yours is different. `dotnet build` compiles the scripts directly.

| Command | Purpose |
| --- | --- |
| `make help` | List available commands |
| `make build` | Compile C# |
| `make import GODOT=…` | Import assets |
| `make run GODOT=…` | Build and play |
| `make editor GODOT=…` | Open the editor |
| `make shot GODOT=…` | Build, save a screenshot after chunks load, then quit |

Use `SPAWN=x,z[,yaw,pitch]` with `make run` or `make shot` to return to a particular viewpoint. Angles are in degrees.

```sh
make run GODOT=/absolute/path/to/Godot_mono SPAWN=-58,-14,100,2
```

## Controls

Click the game view to capture the mouse and look around.

| Action | Control |
| --- | --- |
| Move | W A S D |
| Run | Shift |
| Jump | Space |
| Toggle crouch or stand | C |
| Toggle lying prone or stand | Z |
| Pause and Settings | Esc |
| Dawn, day, dusk, night | 1, 2, 3, 4 |
| Pause or resume the day clock | T |
| Slow down or speed up the day clock | − / = |
| Enter or leave photo mode | P |
| Open or close the gallery | G |

### Photo mode

| Action | Control |
| --- | --- |
| Change focal length | Mouse wheel |
| Lower or raise the camera | Hold Q / E |
| Choose a camera setting | Up / Down |
| Change that setting | Left / Right |
| Take a photo | Click / Enter |

Long exposures take their actual shutter time and average frames. Tripod support locks movement during the exposure. Open the gallery after the capture finishes.

### Gallery

Choose a local save folder, browse continuously, and sort by time or name. Select a photo to see the camera position, settings, date, resolution, file size, and format.

Click the preview or **Full screen** to view the original photo. Use the wheel or **+ / −** to zoom, drag to pan, and **Fit** to reset. **Esc** returns to the gallery.

**Rename photo** changes the filename. **Delete** opens a confirmation before requesting a move to the system Trash.

### Language and graphics

Press **Esc** and choose **Language / Jazyk** to switch between **English** and **Čeština**. The choice takes effect immediately and persists across restarts. On first launch, a Czech system locale selects Czech, and other system locales select English.

Settings include graphics presets, draw distances, shadows, water reflections, ambient occlusion, resolution scale, and VSync. Native folder-picker controls use the operating system's language.

## Photos and saved preferences

Photos are PNG files. The initial save location is `World Photos` under the system Pictures directory, with Godot's user-data directory as a fallback. Choose another folder from the gallery.

New photos embed a JSON `World Photo` text chunk inside the PNG. Capture settings and camera coordinates travel with the file. Older PNGs appear with their file date and an explanation when capture details are unavailable.

Godot's `user://` directory stores `settings.cfg`, `language.cfg`, and `photo-library.cfg`. The selected photo folder also supplies the gallery's contents.

## Project map

| Location | Responsibility |
| --- | --- |
| `scenes/Main.tscn` | Main scene and node wiring |
| `scripts/World/` | Terrain, streaming chunks, vegetation, lighting, and water |
| `scripts/Player/` | Movement, camera model, captures, and photo storage |
| `scripts/UI/` | HUD, Settings, gallery, full-screen viewer, and localization helpers |
| `shaders/` | Terrain, trees, grass, sky, water, and lens effects |
| `locale/cs.po` | Czech interface translations |
| `assets/`, `textures/` | Source models, textures, and import settings |
| `.agents/skills/` | Project-specific agent workflows |

Source `.uid` and `.import` files belong in Git. Godot's generated `.godot/` cache, .NET output, and local screenshots are ignored.

## Contributing and agent guidance

See [CONTRIBUTING.md](CONTRIBUTING.md) for checks and development conventions, and [AGENTS.md](AGENTS.md) for instructions for coding agents. `CLAUDE.md` links to `AGENTS.md`, and `.claude/skills` links to `.agents/skills`.

## Asset credits

The Quaternius Stylized Nature MegaKit assets include a CC0 dedication in [assets/megakit/LICENSE.txt](assets/megakit/LICENSE.txt).

The packed rock, scree, and snow texture sets have Poly Haven CC0 provenance recorded in [textures/source/README.txt](textures/source/README.txt). Preserve these source notices when changing assets.
