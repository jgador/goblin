# Goblin brand assets

Official Goblin artwork from the designer's package. Files use short, lowercase
names; their contents are unchanged.

The brand artwork and its copies are outside Goblin's Apache license grant; this
repository grants no additional copyright permission for it. The Inter fonts stay
under the [SIL Open Font License 1.1](fonts/OFL.txt). See the
[license scope and third-party notices](../../THIRD_PARTY_NOTICES.md).

| Directory | Contents |
| --- | --- |
| `svg/` | Icon-only and icon-with-text artwork, in light and dark variants |
| `png/` | Original PNG exports |
| `jpeg/` | Original JPEG exports |
| `slack/` | Derived app icon for Slack |
| `fonts/` | Inter Medium, SemiBold, and ExtraBold from the design package |
| `source/` | Editable Illustrator document (`logo.ai`) and package report (`report.txt`) |
| `providers/` | Original third-party provider artwork; sources and terms are recorded in the [third-party notices](../../THIRD_PARTY_NOTICES.md) |

SVG, PNG, and JPEG exports use the same base names:

| Base name | Artwork |
| --- | --- |
| `icon-light` | Icon only, for light backgrounds |
| `icon-dark` | Icon only, for dark backgrounds |
| `logo-light` | Icon with the GOBLIN text, for light backgrounds |
| `logo-dark` | Icon with the GOBLIN text, for dark backgrounds |

SVG and PNG exports are transparent; JPEG exports have a white or black
background. The source document and report keep the designer's metadata.

Keep original design files here; this directory is excluded from the application
Docker build context. Runtime copies are unchanged unless noted:

- Application: `frontend/public/assets/branding/icon.svg` is `svg/icon-light.svg`,
  used for the header, favicon, and other in-app marks. `frontend/public/assets/fonts/`
  holds the Medium and SemiBold fonts. Register browser URLs for files under
  `frontend/public/assets/` in `backend/src/Goblin.Web/Http/StaticAssets.cs`; the
  frontend build copies them into `frontend/dist/assets/`.
  `frontend/public/assets/providers/chatgpt.svg` is an unchanged copy of
  `providers/chatgpt.svg`, the official black OpenAI Blossom mark used to identify
  ChatGPT in Settings → Connections. Its original proportions, color, and clear
  space are preserved.
  `providers/codex.png` is the original 128 × 128 icon published in OpenAI's Codex
  app. Its browser copy, `frontend/public/assets/providers/codex.png`, removes the
  white app-icon tile and surrounding shadow, leaving the purple mark and white
  symbol on a transparent canvas. The original source is unchanged.
  Settings → Connections centers every provider in a 32 × 32 slot with a visible
  mark approximately 24 pixels wide. Codex uses a 40-pixel image and ChatGPT a
  36-pixel image to account for the clear space embedded in their artwork; the
  other images use 24 pixels. Scaling preserves proportions, colors, transparency,
  and the full image without cropping. The New Work connection control uses the
  transparent Codex image at 24 × 24 pixels. The Codex source is recorded in the
  [third-party notices](../../THIRD_PARTY_NOTICES.md).
  Connections logos render on transparent canvases. GitHub retains its original
  circular mark, and Teams retains its original T tile and people. Their original
  SVGs are retained in `providers/github.svg` and `providers/teams.svg`. The browser
  GitHub, Slack, and Teams SVGs remain unchanged upstream copies.
- Website: `website/assets/goblin.svg` is `svg/icon-light.svg`, `goblin-dark.svg`
  is `svg/icon-dark.svg`, and the Medium and SemiBold fonts are copied there too.
- Installer: `goblinctl` embeds `svg/icon-light.svg`, the Medium and SemiBold
  fonts, and `fonts/OFL.txt` directly from this directory
  (`tools/goblinctl/src/assets.rs`), so the setup page has branding before the
  application is built. These files are goblinctl shipping inputs; changing them
  requires a new goblinctl build at release time (see
  [releases](../../docs/releases.md#prepare-a-coordinated-candidate)).

The [Slack integration](../../docs/slack-integration.md) uses
[`slack/icon-light.png`](slack/icon-light.png), a 1024 × 1024 PNG made from
`svg/icon-light.svg`: the artwork is 896 pixels wide, centered on an opaque white
square, with at least 64 pixels of padding. It fits Slack's documented 512–2000
pixel icon size; the original PNGs are 4167 pixels square. Use this file for
Slack, not an original PNG or wordmark.
Its runtime copy is `frontend/public/assets/branding/slack.png`, served at
`/assets/branding/slack.png` and embedded by the Slack setup adapter for upload.
To regenerate it, render the SVG at 896 pixels wide keeping its aspect ratio,
center it on a 1024-pixel white square, and export as PNG.
