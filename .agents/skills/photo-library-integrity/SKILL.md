---
name: photo-library-integrity
description: Modify World's photo captures, embedded PNG metadata, file operations, image loading, or gallery folder preferences while preserving older photos and consistent state during cancellation or failure.
---

# Photo library integrity

Inspect `scripts/Player/PhotoLibrary.cs`, `PhotoMode.cs`, and the affected gallery callers before changing the storage or capture path.

## Preserve the file contract

Photos are PNG files. New captures place a JSON `World Photo` text chunk immediately after IHDR. `PngPhotoDetails` reads that location and verifies its CRC. `PhotoDetails` records the capture timestamp, camera coordinates, and settings.

Older PNGs remain browsable with file-date fallback and missing-details feedback. Read dimensions and file size without decoding every original during folder enumeration. Account for compatibility with earlier JSON when changing the record.

The current save writes a temporary sibling and moves it to an unused name. Keep successful files complete and avoid overwriting existing photos, including captures with the same timestamp. Renaming moves the PNG with its embedded details and rejects duplicate destinations.

Deletion confirms the selected photo and asks the OS to move it to Trash. Surface failures while keeping gallery state accurate.

## Keep asynchronous state consistent

Capture settings are snapshotted at shutter time. Short captures and accumulated exposures validate the shot identity after awaiting a frame. Exiting photo mode cancels pending work and releases exposure state.

Folder scans use cancellation. Thumbnail and preview results apply only to the matching selection or card. Full-screen viewing uses the original image, while the grid uses bounded thumbnail work.

Check pending loads during close, rename, deletion, and folder changes. Keep selected entries, counts, sorting, previews, and texture references consistent after each operation.

## Verify with temporary photos

Exercise the changed behavior using a temporary folder and a mixture of new metadata-bearing photos and older PNGs.

Useful cases include two saves at one timestamp, an existing destination name, a Unicode filename, a missing file, an unavailable folder, a canceled capture, and a folder change during loading. Test only cases relevant to the modification.

For PNG encoding or metadata changes, decode the output with an independent image reader when available and check dimensions, metadata, and file size. Test original-resolution zoom separately from thumbnail decoding.

Preferences live in `user://photo-library.cfg`. Keep the saved folder choice persistent and report a failed write without silently claiming success.
