# Goblin: In the loop

A standalone interactive concept showing Goblin at the center of Slack,
Microsoft Teams, GitHub, Discord, and Gmail conversations. The layout follows
`goblin-in-the-loop-v2.png` in the repository's `demos/communications/` directory:
a pale green tool network, central Goblin mark, and a tabbed conversation panel.
The tool network has no enclosing box; the conversation panel retains its border.
Open `index.html` in a modern browser; no build, dependencies, or account
connections are required. On narrow screens, the conversation follows the tool
network vertically.

The tool cards and conversations feature Josh Aspinwall (Slack), Anders Hejlsberg
(Microsoft Teams), Stephen Toub (GitHub), Renko Karman (Discord), and Linus Torvalds
(Gmail).

Choose a tool card or chapter to play its exchange. Each chapter has three
scripted prompts in the “Try another ask” selector below the scene. The player
supports pause/play, replay, timeline scrubbing, playback speed, fullscreen when
available, and the Space keyboard shortcut. The initial Slack response is visible
on arrival. Reduced-motion users start paused with a complete conversation and
can choose chapters without moving message effects.

The Conversation, Activity, Work, and Files tabs show related example content.
Tabs support arrow keys, Home, and End. Deliverables open illustrative previews.
The composer accepts a local demo message with Enter or the send button;
Shift+Enter adds a line. Attachment selection displays a filename only. The
mention and smile controls insert text. Messages and filenames are rendered as
text, stay in memory, and are discarded on reload. No attachment contents are
read or uploaded. A custom message pauses playback; choose a channel or replay
to return to the scripted examples.

These conversations are illustrative. The demo makes no external requests and
does not execute Work. Teams, Discord, and email depict future possibilities,
not implemented integrations. Proposed changes and draft deliverables remain
for human review. The About dialog explains the demo's scope.

The Goblin icons are unchanged copies of the official artwork in the repository's
`assets/branding/svg/` directory. The white icon appears on a green surface to
match the reference. Brand artwork remains outside the repository's Apache
license grant. Inter fonts retain their SIL Open Font License in `assets/OFL.txt`.
Slack, Teams, and GitHub artwork comes from existing frontend provider assets
with their license notice. The Gmail mark in `assets/email.svg` comes from the
[gilbarbara/logos collection](https://github.com/gilbarbara/logos) under the same
provider license notice. The Discord mark identifies the depicted channel;
channel names and marks belong to their respective owners.

The production application, its asset map, and its security policy are independent
of this demo. Verify changes with `node --check demos/communications/demo.js`
from the repository root, plus browser checks of channel scenarios, tabs,
previews, composer, playback controls, narrow layouts, and reduced motion.

The repository's existing `goblin-in-the-loop.gif` is an earlier exported design;
it is not regenerated as part of this website update. Use `index.html` for the
current interactive design.

Published site: <https://goblin-in-the-loop.jesse-gador-2903.chatgpt.site/>
