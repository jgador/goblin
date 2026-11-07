# Goblin brand assets

Keep designer originals under this directory and required runtime copies under the
owning application's public assets. The original editable artwork and export metadata
are preserved. Filenames are lowercase; `icon` denotes artwork without text and `logo`
includes the wordmark, each in light/dark variants.

The artwork and derivatives are outside Goblin's Apache license grant. Fonts retain the
[SIL Open Font License](fonts/OFL.txt); provider marks keep their own rights and
provenance in [third-party notices](../../THIRD_PARTY_NOTICES.md). Do not infer
redistribution rights from inclusion here.

## Runtime copies

[StaticAssets.cs](../../backend/src/Goblin.Web/Http/StaticAssets.cs) maps browser
assets, [installer assets](../../tools/goblinctl/src/assets.rs) embed the setup mark and
fonts, and [website assets](../../website/assets/) serve the public site. Installer
assets are shipping inputs and require an
[installer release](../../docs/releases.md#prepare-a-coordinated-candidate).

Preserve provider artwork's proportions, color, and clear space. The original Codex app
icon is retained; its browser derivative removes the white tile and shadow. Other
provider-copy provenance and permissions are in the notices. Keep original assets
unchanged and review required copies together.

## Integration icons

Slack uses [slack/icon-light.png](slack/icon-light.png), derived from
[the light icon](svg/icon-light.svg): center artwork 896 pixels wide on an opaque 1024 ×
1024 white canvas with at least 64 pixels padding. Use this icon rather than the
original large export or a wordmark. Its browser copy is
[slack.png](../../frontend/public/assets/branding/slack.png), also embedded for upload.

ChatGPT uses [chatgpt/icon-light.png](chatgpt/icon-light.png). Fit the same SVG within
448 × 448 pixels on an opaque 512 × 512 white RGB canvas, with at least 32 pixels
padding. Compress losslessly to at most 10,000 bytes. Copy the result to
[chatgpt.png](../../frontend/public/assets/branding/chatgpt.png); the installation
download journey checks dimensions and size. FFmpeg's PNG encoder with
`-pix_fmt rgb24 -compression_level 9 -pred mixed` fits this artwork within the limit
without pixel changes.

The [Slack guide](../../docs/slack-integration.md) and
[ChatGPT guide](../../docs/chatgpt-integration.md) own upload procedures.
