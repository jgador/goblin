# Goblin Slack integration

Status: implemented in the working tree, 2026-10-02. The user completed live Slack
authorization: app creation, installation, automatic icon upload, and Socket Mode
all succeeded. Runtime credentials remained usable after setup logout, helper
removal, and process restart. Live identity linking and DM-to-Work delivery also
passed; channel mentions have parser coverage but were not exercised live.

## Ownership and connection

Goblin provides the standard Slack app definition and experience; each customer
owns and runs their own Slack app and credentials.

Each deployment connects outbound through Socket Mode to its customer-owned app.
There is no shared Goblin Slack app, installation secret, central Goblin account,
cloud relay, OAuth callback, public inbound endpoint, or Slack-specific DNS/TLS
requirement. Local Goblin remains authoritative for users, permissions, Work,
agents, repositories, identity mappings, history, and execution decisions.

The primary experience is **Integrations → Slack**. Settings also links to the directory. The app is named
**Goblin**, the bot is **goblin**, and the description identifies it as the
self-hosted AI coworker.

## Automatic setup

1. Select **Connect** beside Slack, then **Connect Slack** in its settings.
2. Copy the displayed `/slackauthticket …` command into the intended Slack workspace.
3. Approve Slack's setup permissions and enter its confirmation code in Goblin.
4. Goblin creates and installs the app using its embedded manifest, collects the
   app-level and bot credentials, and attempts to upload the bundled icon.
5. Goblin ends the temporary setup authorization, verifies the resulting runtime
   credentials again, encrypts them locally, and displays the workspace and bot.

This is Slack's supported CLI authorization flow, not a generic OAuth device
endpoint. WSL works with manual command/code entry: open Goblin's localhost URL
in the Windows browser; no browser launch or callback into WSL is needed.

The setup adapter downloads Slack CLI **4.8.0** on demand for Linux x64 or ARM64,
checks the pinned release SHA-256, and runs it in a private temporary directory
with an isolated profile and environment. Version and architecture checksums are
owned by [SlackSetup.cs](../backend/src/Goblin.Integrations.Slack/SlackSetup.cs),
following the [tool dependency convention](dependencies.md). The application
image contains no permanent Slack CLI. Runtime Socket Mode and messaging use
Goblin's .NET adapter directly.

The helper needs outbound HTTPS to GitHub's release download service and Slack
during setup. Routine operation needs Slack Web API and Socket Mode connectivity.
The helper's telemetry is disabled. There is no Goblin-operated intermediary.
No user-level Slack CLI profile is read or modified.

Setup expires after 15 minutes. Cancellation attempts to revoke temporary CLI
authorization, then removes the private helper, profile, and captured credentials.
Startup also removes interrupted setup data and preserves a recovery notice.
If revocation could not be confirmed, the UI directs the owner to review the
temporary CLI authorization in Slack. Cancelling never deletes the customer's app.

A known partially created app can be resumed in the same setup session. Goblin
retains its app ID, including across restart, and does not silently create another
during recovery. After restart or expiry, use the existing-app connection form.
Unknown creation outcomes require reviewing Slack's app list before starting again.
Workspace approval policies can require an administrator to approve the existing
app before setup can continue.

The advanced **Connect an existing app** section supplies the manifest download,
Slack's create-from-manifest link, app-token instructions, and private fields for
`xapp-…` and `xoxb-…`. This supports policy restrictions and recovery.

## Manifest and branding

The [embedded manifest](../backend/src/Goblin.Integrations.Slack/manifest.json)
subscribes only to:

| Event / scope | Implemented use |
| --- | --- |
| `message.im` / `im:history` | Receive direct messages sent to Goblin. |
| `app_mention` / `app_mentions:read` | Receive explicit mentions. |
| `chat:write` | Send minimal acknowledgements in the originating thread. |
| `users:read` | Use `bots.info` to verify the bot token's app identity. |
| App-level `connections:write` | Open the outbound Socket Mode connection. |

There is no broad channel-message subscription. Channel thread follow-ups must
mention `@goblin` again. The app must be added to a channel before use.
Verification checks the bot's required scopes and matches its app ID against
the Socket Mode connection's hello message.

<img src="../assets/branding/slack/icon-light.png" alt="Text-free Goblin app icon for light backgrounds" width="160" height="160">

The icon uses the official [icon-light.svg](../assets/branding/svg/icon-light.svg):
dark artwork without text, centered at 896 pixels wide on an opaque white
1024 × 1024 canvas with at least 64 pixels of padding. This fits Slack's documented
512–2000 pixel bounds. Original artwork is preserved.

The prepared asset is [assets/branding/slack/icon-light.png](../assets/branding/slack/icon-light.png).
Its runtime copy, [frontend/public/assets/branding/slack.png](../frontend/public/assets/branding/slack.png),
is served at `/assets/branding/slack.png` and embedded for direct multipart upload.
No publicly hosted asset service is needed.

Icon upload uses `apps.icon.set` with setup authorization, separately from the
normal App Manifest. If Slack rejects it, connection can still succeed. The UI
offers **Download Goblin icon** and the created app's **Basic Information →
Display Information** page. App-management scopes are never added to the bot.

## Local identity and authorization

Slack supplies an authenticated workspace/user identity. Membership in that
workspace does not grant Goblin permission.

The current Goblin deployment has one local owner authority. The integration maps
explicitly approved Slack users to that existing authority:

1. An authenticated owner selects **Link a Slack identity** in Goblin.
2. Goblin issues a random, five-minute linking command.
3. The intended Slack user sends it as a DM to Goblin.
4. The same local browser session sees the proven Slack user ID and explicitly
   selects **Grant local owner access**.

The linking code is stored as a hash and is excluded from conversation history.
Owner access can be removed in the integration settings. Authorization is checked
both when receiving a message and when applying its command. Pending messages
from a revoked identity cannot change Work. Future named local users must replace
the owner mapping through Goblin's local authorization model.

The boundary is:

```text
verified Slack workspace / app / user
  → Slack conversation adapter
  → approved local identity
  → existing application Work commands
  → core rules and local authorization
  → durable execution
```

The Slack project depends on Goblin-owned conversation contracts. It does not
add Slack concepts to the Work core. Repository approval, retries, review,
cancellation, and execution decisions retain their local application boundary.

## Conversations, durability, and visibility

A new DM or mention thread starts a conversation and Work. Replies continue that
Work; a reply answers an existing input question when one is pending, otherwise
it adds context. Naming an enabled repository can start its isolated execution
directly, including in an answer to a repository question. The default branch and
GitHub no-reply commit identity are automatic; push and draft PR permissions come
from explicit delivery instructions. Ordinary text such as `yes` never approves
repository access, and Slack cannot enable repositories or retry failed Work.

PostgreSQL stores external identities, link requests, thread associations, and
normalized event receipts. Receipt uniqueness covers both event identity and
workspace/channel/message identity. Socket envelopes are acknowledged only after
durable acceptance; duplicate deliveries do not dispatch replacement attempts.

Conversation messages, Work commands, and Wolverine dispatch intent commit in
one database transaction. Processing resumes from pending receipts after connection
loss or process restart. Transport reconnection does not retry failed Work.
Failures retain the core's attention, reconciliation, and explicit retry rules.

Work created through Slack keeps its workspace, user, channel, thread, message,
and event provenance. The web conversation marks Slack-originated messages.
Disconnecting retains durable Work and history. Reconnecting creates a fresh
installation identity and requires linking Slack identities again.

Slack replies contain only an acknowledgement and a link to the authenticated
local Work view when a request is saved. When Slack-originated Work needs an
answer, Goblin also posts the pending question in its original Slack thread.
The question remains visible in Goblin, and an authorized reply in the same
thread answers it through the existing application command. Channel replies
must mention `@goblin` again. Repository enablement, result approval, and retries
remain in the authenticated Goblin interface. An outgoing acknowledgement is best
effort; losing it cannot roll back or repeat accepted Work.

Slack-originated Work also posts saved outcomes in its original DM or channel
thread. Successful execution sends a result excerpt labelled **ready for review**;
approving the result in Goblin sends **Work completed**. Confirmed execution
failures, uncertain execution, cleanup failures, and cancellation have distinct
messages. Errors use Goblin-owned failure descriptions and link to the permitted
recovery actions; raw runtime errors and credentials are not shared. The model
does not decide whether or when to send these notifications. The existing
connection polls committed Work every three seconds, independently of model
commentary and without requiring a new Slack message.

`external_conversations.notified_work_sequence` records successful outcome
delivery using the durable Work history sequence. Pending outcomes survive
restarts and disconnected Slack sessions. Before posting, Goblin rechecks the
current outcome, installation, original thread, and originating user's enabled
owner grant. Superseded outcomes are skipped: for example, a result approved
while Slack is offline produces a completion notification with the saved result,
not a stale request to approve it. Cleanup failure temporarily replaces outcome
attention; when cleanup recovers, the actionable result or failure can notify
again. A failed post remains pending without blocking other outcome notifications.
No delivery operation starts, retries, approves, or cancels Work.

Outcome delivery has the same crash window as question delivery: Slack can
accept a post before Goblin saves its marker, so a restart can duplicate a
notification. Markers advance monotonically, and normal polling does not repeat
an already delivered outcome. Result excerpts use the same 3,000-character
limit and Slack escaping as questions, with links to full authenticated details.
Rich artifact sharing and proactive progress commentary remain outside this flow.
Active-turn delivery of incoming follow-ups is tracked separately in
[issue #54](https://github.com/jgador/goblin/issues/54).

Question delivery uses the existing connection's polling loop and persisted
Work decisions; it adds no service bus or separate service. Each external
conversation records the last delivered decision. Failed posts leave delivery
pending, and restart resumes pending questions. Questions already answered in
Goblin are skipped. Delivery requires the current installation and the
originating Slack user's enabled local owner grant. A crash after Slack accepts
a post but before its delivery marker commits can duplicate a notification;
it cannot repeat Work execution. Question text is escaped for Slack and bounded
to 3,000 characters, with a link to the full question in Goblin.

The unreleased schema baseline adds `external_conversations.notified_decision_id`
and `external_conversations.notified_work_sequence`.
An existing development database needs schema reconciliation before running
the updated application; restarting the old installed image does not enable
notification delivery. Existing eligible outcomes without a delivery marker are
sent after the updated app connects. The migration runner still rejects edited
applied baselines.

Runtime credentials use AES-256-GCM in the deployment's private persistent data
directory, with a locally generated key and owner-only file permissions.
Back up both the key and encrypted credentials. Setup credentials are temporary
and are never exported to execution workspaces or Work history.
There is no token endpoint in public settings responses.

## Validation and remaining checks

Implemented checks cover manifest/event restrictions, authenticated actor
filtering, encrypted storage and tamper detection, interrupted-setup recovery,
browser command/code entry, linking confirmation, and logo delivery.
Notification tests cover result versus approval messages, every failure category,
uncertain execution, cleanup recovery, bounded excerpts, monotonic delivery markers,
restart recovery, access revocation, superseded outcomes, and independent delivery
when one channel fails. Real PostgreSQL tests exercise the saved outcome markers.
Question delivery tests cover Slack thread targeting, escaping, failed-post
handling, restart recovery, delivery markers, access revocation, and an answer
continuing the same attempt. Actual question delivery through Slack remains a
live validation step.
A real PostgreSQL test verifies local-session proof confirmation, concurrent
duplicate delivery, one durable dispatch, thread continuity, and revocation.

The supported Slack CLI loaded Goblin's embedded manifest through the actual
hooks. Live authorization in the user's disposable workspace verified app
creation, installation, icon upload, token survival after setup logout, encrypted
runtime storage with mode 0600, and Socket Mode with the helper removed. A separate
actual-CLI check verified ticket preparation, local-session isolation, discarded
CLI logs, and cancellation cleanup without creating an app. After a process
restart, the connection and local identity mapping survived, and a real DM created
one Work item with Slack provenance. Its first attempt entered attention because
the temporary test launcher could not start Codex's Node wrapper. Removing that
override restored Goblin's native executable lookup. The real Codex initialization,
isolated credential-storage, restart, and logout checks passed; the test instance
now reports runtime readiness. An AI account still needs to be connected before
an explicitly requested retry can execute the saved Work.

`make test` passed. The additional full PostgreSQL run passed 123 application tests
and four persistence tests; six certificate tests were skipped because that local
fixture does not provide mutual TLS. Seven browser journeys passed. The final
linking-acknowledgement change also passed the focused PostgreSQL and Slack tests.
Administrator-approval recovery and live channel mentions remain unexercised.

Run `make test` plus the relevant browser checks. PostgreSQL tests are opt-in;
report their results separately. See [Work lifecycle](work-lifecycle.md),
[repository authority](git-repository-intent.md), and the
[architecture plan](architecture-refactoring-plan.md).

Optional future `goblinctl integrations slack` can share this setup service;
the web UI remains primary. Teams and other conversation adapters are future work.

## Slack references

- [CLI authorization](https://docs.slack.dev/tools/slack-cli/guides/authorizing-the-slack-cli/)
- [Login flags](https://docs.slack.dev/tools/slack-cli/reference/commands/slack_login/)
- [CLI hooks and runtime-token handoff](https://docs.slack.dev/tools/slack-cli/reference/hooks/#deploy)
- [App manifest creation](https://docs.slack.dev/reference/methods/apps.manifest.create/)
- [App icon upload](https://docs.slack.dev/reference/methods/apps.icon.set/)
