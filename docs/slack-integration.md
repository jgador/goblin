# Goblin Slack integration

Goblin connects to a Slack app that the customer owns. It uses outbound Socket Mode,
linked owner identities, durable intake of DMs and mentions, and delivery of
questions and outcomes back to the original thread. The code is in
`backend/src/Goblin.Integrations.Slack`.

## Ownership and connection

Each customer owns and runs their own Slack app and credentials. Goblin provides the
app definition. Each deployment connects outbound through Socket Mode, so there is no
shared Goblin app, central account, cloud relay, OAuth callback, public inbound
endpoint, or Slack-specific DNS or TLS. Goblin stays the authority for users,
permissions, Work, agents, repositories, identity mappings, history, and execution.

Slack requires durable Work (PostgreSQL). Open **Integrations** (`/integrations`;
**Settings → Integrations** links there), then select **Connect** or **Manage** on
Slack. This opens the Slack page in Settings (`/settings?section=slack`). The app is
named **Goblin** and its bot user is **goblin**.

## Automatic setup

1. Select **Connect Slack**.
2. Copy the displayed `/slackauthticket …` command. In the intended Slack workspace,
   paste it into the message box of your DM with yourself and press Enter.
3. Approve Slack's permissions dialog, enter Slack's confirmation code in Goblin, and
   select **Finish setup**.
4. Goblin creates and installs the app from its embedded manifest, collects the
   app-level and bot tokens, and tries to upload the bundled icon.
5. Goblin ends the temporary setup authorization, verifies the runtime tokens again,
   encrypts them locally, and shows the workspace and bot.

This is Slack's supported CLI authorization flow. On WSL, open Goblin's localhost URL
in the Windows browser; no browser launch or callback into WSL is needed.

Setup downloads Slack CLI **4.8.0** for Linux x64 or ARM64 from GitHub releases,
checks the pinned SHA-256 in
[SlackSetup.cs](../backend/src/Goblin.Integrations.Slack/SlackSetup.cs) (see the
[dependency rules](dependencies.md)), and runs it in a private temporary directory
with its own profile and environment and telemetry disabled. Your own Slack CLI
profile is not read or changed. The image contains no Slack CLI; runtime Socket Mode
and messaging use Goblin's .NET adapter. Setup needs outbound HTTPS to GitHub and
Slack. Normal operation needs the Slack Web API and Socket Mode.

Setup expires after 15 minutes. **Cancel setup** tries to revoke the temporary CLI
authorization, then removes the helper, profile, and captured tokens. Cancelling
never deletes the customer's app. If revocation could not be confirmed, Goblin asks
you to review the temporary CLI authorization in Slack. Startup removes data from an
interrupted setup and shows a recovery notice.

If the app was partly created, **Resume setup** continues in the same setup session.
Goblin keeps the app ID, also across restart, and does not create a second app during
recovery. A workspace approval policy can require an administrator to approve the app
before you resume. After a restart or expiry, use **Connect an existing app**. If the
creation result is unknown, review Slack's app list before starting again.

Use **Connect an existing app** when workspace policy blocks automatic setup or to
recover an app:

1. Select **Download the Goblin manifest** and create an app from it in Slack
   (Goblin links to Slack's create-app page).
2. Under **Basic Information**, create an app-level token with `connections:write`.
3. Install the app into the workspace and copy its bot token.
4. Enter the `xapp-…` **App-level token** and the `xoxb-…` **Bot token**, then
   select **Verify and connect**.

## Manifest and branding

The [embedded manifest](../backend/src/Goblin.Integrations.Slack/manifest.json)
subscribes only to:

| Event or scope | Use |
| --- | --- |
| `message.im` / `im:history` | Receive direct messages to Goblin |
| `app_mention` / `app_mentions:read` | Receive explicit mentions |
| `chat:write` | Post replies in the originating thread |
| `users:read` | Use `bots.info` to check the bot token's app |
| App-level `connections:write` | Open the Socket Mode connection |

There is no channel-message subscription. Add the app to a channel before use, and
mention `@goblin` again in channel thread replies. Verification checks the bot
token's scopes and matches its app ID against the Socket Mode hello message.

<img src="../assets/branding/slack/icon-light.png" alt="Text-free Goblin app icon for light backgrounds" width="160" height="160">

The icon is [icon-light.svg](../assets/branding/svg/icon-light.svg) without text on an
opaque white 1024 × 1024 canvas, within Slack's 512–2000 pixel bounds. The source is
[assets/branding/slack/icon-light.png](../assets/branding/slack/icon-light.png). Its
copy [frontend/public/assets/branding/slack.png](../frontend/public/assets/branding/slack.png)
is served at `/assets/branding/slack.png` and embedded in the .NET assembly for upload.

Setup uploads the icon with `apps.icon.set` using the setup authorization. If Slack
rejects it, the connection still succeeds; select **Download Goblin icon** and add it
under **Basic Information → Display Information** in the app's settings. The bot never
gets app-management scopes.

## Local identity and authorization

Membership in the Slack workspace does not grant Goblin access. The deployment has
one local owner, and only explicitly linked Slack users act as that owner:

1. In Goblin's Slack settings, select **Link a Slack identity**.
2. Goblin shows a `link …` command that expires in five minutes.
3. The intended Slack user sends it as a DM to Goblin.
4. In the same browser session, check the shown Slack user ID and select **Grant local
   owner access**.

The linking code is stored only as a hash and is not saved as a conversation message.
**Remove access** revokes a linked identity. Authorization is checked when a message
is received and again when its command is applied, so pending messages from a revoked
identity cannot change Work.

The Slack project depends on Goblin-owned conversation contracts and adds no Slack
concepts to the Work core. Repository approval, retries, review, cancellation, and
execution decisions stay in the local application. See
[repository authority](git-repository-intent.md) for what Slack users can select.

## Conversations, durability, and visibility

A new DM or mention thread creates a conversation and Work and starts it. A reply in
the thread answers the pending input question if there is one; otherwise it adds
context. Naming an enabled repository can start isolated repository execution. Slack
cannot enable repositories, approve results, or retry failed Work; ordinary text such
as `yes` never approves repository access. Messages longer than 4,000 characters,
after removing the `@goblin` mention, are acknowledged to Slack and ignored: Goblin
saves nothing and sends no reply.

PostgreSQL stores external identities, link requests, thread associations, and event
receipts. Receipts are unique by event ID and by workspace, channel, and message.
Goblin acknowledges a Socket Mode envelope only after the message is saved, and
duplicate deliveries do not start another attempt. Conversation messages, Work
commands, and Wolverine dispatch intent commit in one transaction. Pending receipts
are processed after reconnect or restart. Reconnecting never retries failed Work.

Work from Slack keeps its workspace, user, channel, thread, message, and event
references. The web conversation labels these messages as Slack. Disconnecting keeps
Work and history. Reconnecting creates a new installation identity, so Slack
identities must be linked again.

When a request is saved, Slack gets a short reply with a link to the Work in Goblin.
Losing that reply cannot roll back or repeat accepted Work. The existing connection
then checks committed Work every three seconds and posts to the original thread:

- Questions: the pending question, with a link to answer in Goblin. An authorized
  reply in the thread answers it; channel replies must mention `@goblin`. Each
  conversation records the last delivered question, and questions already answered
  in Goblin are skipped.
- Outcomes: a result excerpt that is ready for review, **Work completed** after
  approval in Goblin, and separate messages for failures, uncertain execution,
  cleanup failures, and cancellation, with links to the recovery actions. Messages
  use Goblin-owned failure descriptions, never raw runtime errors or credentials.
  `external_conversation.notified_work_sequence` records the last delivered outcome.

Before posting, Goblin checks the current installation, thread, and the originating
user's enabled owner grant, and rechecks the current outcome. Superseded outcomes are
skipped; for example, a result approved while Slack was offline produces a completion
message, not a request to approve it. A cleanup failure temporarily replaces outcome
attention, and the result or failure can notify again after cleanup recovers. Failed
posts stay pending without blocking other notifications, and delivery never starts,
retries, approves, or cancels Work.

If Goblin crashes after Slack accepts a post but before the delivery marker is saved,
the notification can repeat; Work execution does not. Question and result text is
escaped for Slack and limited to 3,000 characters. The model does not decide when
notifications are sent. Rich artifact sharing and progress commentary are not sent.
Follow-ups sent while a turn is running do not reach the running agent; see
[issue #54](https://github.com/jgador/goblin/issues/54).

Runtime tokens are encrypted with AES-256-GCM under the deployment's private data
directory (`slack/`), with a locally generated key and owner-only file permissions.
Back up both the key and the encrypted file. Setup credentials are temporary and
never reach execution workspaces, Work history, or settings responses.

## Validation and remaining checks

Run `make test` and the relevant browser checks; PostgreSQL tests are opt-in, so
report their results separately. These tests do not show current Slack service
behavior or authenticated model execution. Live acceptance should cover app authorization and installation, tokens
working after setup logout and restart, identity linking, DM and channel-mention
execution, question and outcome delivery, and administrator-approval recovery.

A `goblinctl` Slack setup command and Microsoft Teams support are not implemented.
See [Work lifecycle](work-lifecycle.md) and the [architecture guide](architecture.md).

## Slack references

- [CLI authorization](https://docs.slack.dev/tools/slack-cli/guides/authorizing-the-slack-cli/)
- [Login flags](https://docs.slack.dev/tools/slack-cli/reference/commands/slack_login/)
- [CLI hooks and runtime-token handoff](https://docs.slack.dev/tools/slack-cli/reference/hooks/#deploy)
- [App manifest creation](https://docs.slack.dev/reference/methods/apps.manifest.create/)
- [App icon upload](https://docs.slack.dev/reference/methods/apps.icon.set/)
