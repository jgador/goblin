# Goblin: In the loop

A standalone, one-minute interactive concept film with Goblin at the center of
Slack, Microsoft Teams, GitHub, Discord, and Gmail conversations. Open
`index.html` directly in a modern browser; no build, server, dependencies, or
account connections are required.

For a single-file animation, open [goblin-in-the-loop.gif](goblin-in-the-loop.gif).
It loops through all five default conversations in 60 seconds at 1440 × 1000
pixels and 20 frames per second. The GIF plays automatically; use `index.html`
for interactive controls and alternate prompts.

Choose a channel or chapter to start its exchange. Each chapter offers three
scripted prompts. The player supports pause/play, replay, timeline scrubbing,
playback speed, fullscreen when available, and the Space keyboard shortcut.
Reduced-motion users start with a complete, paused conversation and can choose
chapters without moving message effects.

These conversations are illustrative. The demo makes no external requests and
does not execute Work. Teams, Discord, and email depict future possibilities,
not implemented integrations. Every proposed change or draft remains for human
review.

The Goblin icons are unchanged copies of the official artwork in
`assets/branding/svg/` at the repository root. The brand artwork and its copies
remain outside the repository's Apache license grant. Inter fonts are supplied
with the brand assets and retain their SIL Open Font License in `assets/OFL.txt`.
Slack, Teams, and GitHub artwork is copied from the existing frontend provider
assets with their license notice. The Gmail mark in `assets/email.svg` comes from the
[gilbarbara/logos collection](https://github.com/gilbarbara/logos) under the same
provider license notice. The Discord mark identifies the depicted channel;
channel names and marks belong to their respective owners.

The production application, its asset map, and its security policy are independent
of this demo. Changes to this directory can be checked with
`node --check demos/communications/demo.js` and browser checks of the controls,
chapter changes, mobile layout, and reduced motion.
