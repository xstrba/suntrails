---
name: simple-game-ui
description: Build or refine World's child-friendly Godot menus, gallery, settings, navigation, and bilingual interface using clear actions, simple flows, and predictable input ownership.
---

# Simple game UI

Ground the change in what the player wants to do and the existing flow. Use plain action words, generous targets, visible feedback, and a short route back.

## Reuse the interaction model

- `PauseMenu.cs` owns Settings and pauses the world. `PhotoGallery.cs` owns the gallery and pauses exploration while browsing.
- `PhotoViewer.cs` returns to the gallery and restores the previous desktop window mode. Keep Esc behavior consistent with the active screen.
- The gallery opens after an in-progress capture finishes. Preserve input capture and pause state across entering and leaving each screen.
- Keep full-screen viewing, zoom, Fit, renaming, folder choice, and deletion discoverable through visible controls.
- Use clear confirmations for destructive photo actions. Preserve the safe cancel path and focus return.

## Keep the screen understandable

Use the existing gallery theme, button helper, and layout containers where appropriate. Aim for one clear next action. Label controls with the action they perform. Give loading, empty, error, and success states concise text.

Keep controls near the content they affect. Show camera details without crowding the photo. Keep long filenames readable through truncation and tooltips. Ensure child-friendly wording still explains the actual outcome.

## English and Czech

Static Godot controls use automatic translation through `locale/cs.po`. Dynamic messages use `UiText.Get`, `Format`, or `Count`. Use `UiText.Culture` for visible dates and numbers. Czech photo counts require singular, few, and many forms.

Keep filenames, paths, and stored photo metadata literal. Preserve the native folder picker's platform language behavior. Check translated accessibility names and dialog buttons as well as visible labels.

## Exercise the flow

Use ordinary keyboard and mouse entry points at a desktop size and around 780 × 700. Check longer Czech strings, wrapping, focus, scroll reachability, and Esc/back behavior.

Try the affected flow with no photos, one photo, a long name, a large folder, a failed file operation, and a pending image load when relevant. Verify that actions update selection and scrolling predictably. Use temporary photo folders.

Describe the result from the player's perspective and identify any desktop behavior still awaiting a graphical check.
