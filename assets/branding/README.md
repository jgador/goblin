# Goblin brand assets

Official artwork supplied in the `GoblinBoard_Logo_Final` package. Filenames use
short, lowercase names; original file contents are preserved.

| Directory | Contents |
| --- | --- |
| `svg/` | Scalable icon-only and icon-with-text artwork, in light and dark variants |
| `png/` | Original PNG exports |
| `jpeg/` | Original JPEG exports |
| `fonts/` | Inter font files supplied with the design; not currently loaded by the UI |
| `source/` | Editable Illustrator document (`logo.ai`) and packaging report (`report.txt`) |

The same base names are used across SVG, PNG, and JPEG exports:

| Base name | Artwork |
| --- | --- |
| `icon-light` | Icon only, for light backgrounds |
| `icon-dark` | Icon only, for dark backgrounds |
| `logo-light` | Icon with the GOBLIN text, for light backgrounds |
| `logo-dark` | Icon with the GOBLIN text, for dark backgrounds |

`light` and `dark` describe the intended background. SVG and PNG exports are
transparent; JPEG exports include a white or black background. The packaged fonts
are `inter-18pt-medium.ttf`, `inter-18pt-semibold.ttf`, and
`inter-18pt-extrabold.ttf`. The source document and report retain their original
designer metadata.

The application UI asset is `../../frontend/public/assets/branding/icon.svg`, an unchanged copy
of `svg/icon-light.svg`. It supplies both the header and favicon.
The header uses CSS sizing with automatic height to preserve the SVG's proportions
and sharpness. The UI currently uses the light-background artwork only.

The installation page uses the same official `svg/icon-light.svg` artwork for its
header and favicon. `deploy/azure/build-setup-bundle.py` embeds the original file
directly in the standalone setup bundle, served at `/setup/icon.svg`, so branding
is available before the application is built. Regenerate the setup bundle and
Azure templates after changing its sources (see `deploy/azure/reference.md`).

Keep original design resources here. Put assets used by the website under
`frontend/public/assets/`, and register their browser URLs in the static-file
map in `backend/src/Goblin.Web/GoblinApplication.cs`. The frontend build copies
those runtime files into `frontend/dist/assets/` automatically.
This design library is excluded from the Docker build context.

macOS packaging metadata (`.DS_Store` and `__MACOSX`) from the import is preserved
locally under `.artifacts/branding-import-metadata/`, retaining its original
relative paths. That directory is ignored by Git and Docker.
