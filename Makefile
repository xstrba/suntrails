# Godot world: common tasks. Run `make help` for the list.
# Override the engine path if needed: make run GODOT=/path/to/Godot_mono

GODOT ?= $(HOME)/Applications/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64
SHOTS ?= screenshots

# Optional start point for `make run` / `make shot`: SPAWN=x,z[,yawDeg[,pitchDeg]]
SPAWN ?=
spawn_arg = $(if $(SPAWN),--spawn=$(SPAWN))

.DEFAULT_GOAL := help
.PHONY: help build run editor import shot clean check-godot

help: ## Show this help
	@grep -E '^[a-z-]+:.*## ' $(MAKEFILE_LIST) | awk -F':.*## ' '{printf "  make %-8s %s\n", $$1, $$2}'
	@echo ""
	@echo "  Options: SPAWN=x,z[,yaw,pitch]  e.g. make run SPAWN=-58,-14,100,2"

check-godot:
	@test -x "$(GODOT)" || { echo "Godot .NET not found at $(GODOT)"; echo "Set GODOT=/path/to/Godot_mono"; exit 1; }

build: ## Compile the C# scripts
	dotnet build

import: check-godot ## Re-import assets (after adding textures/models)
	"$(GODOT)" --headless --path . --import

run: check-godot build .godot/imported ## Build and play the game
	"$(GODOT)" --path . -- $(spawn_arg)

editor: check-godot ## Open the project in the Godot editor
	"$(GODOT)" --path . -e

shot: check-godot build .godot/imported ## Save a screenshot once the world has loaded, then quit
	@mkdir -p $(SHOTS) && touch $(SHOTS)/.gdignore
	"$(GODOT)" --path . -- --screenshot=$(SHOTS)/shot-$$(date +%Y%m%d-%H%M%S).png $(spawn_arg)

# Imported asset cache; created on first run or after `make clean`.
.godot/imported:
	"$(GODOT)" --headless --path . --import

clean: ## Remove build output and the import cache (rebuilt on next run)
	rm -rf .godot bin obj
