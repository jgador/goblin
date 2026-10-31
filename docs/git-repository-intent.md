# Git repository intent and authorization

Goblin supports GitHub repositories only, and Goblin owns and enforces repository
authority. Users enable a repository in Settings or by explicit approval in a Work
conversation. An authenticated request that names an enabled repository starts
directly. Each attempt gets a fresh, immutable grant built from that standing
permission and the user's delivery instructions. An agent cannot approve its own
request or enable a repository. Failed Work still requires an explicit retry, and
uncertain execution requires reconciliation.

## Conversation flow

Start Work with a GitHub URL, `owner/repository`, or a short repository name. In
request text, a short name must match exactly one repository known to Goblin,
including disabled ones; several matches ask the user for the full name. The
repository form also accepts a full name or GitHub URL that Goblin does not know yet.
A name without an owner in a structured selection or `PrepareRepository` text is
resolved against the repositories the connected GitHub account can access.

Goblin reads repository metadata through the connected GitHub account before it
saves the preview. This does not fetch a checkout or enable access. The base branch
defaults to the repository's default branch, and Goblin creates the Work branch
`goblin/<work>/<attempt>`. The Git commit name defaults to the connected GitHub
login and the email to `<account-id>+<login>@users.noreply.github.com`, so no
private email scope is needed. The Work UI lets the user set the base branch, Git
actions, and commit identity under **Branch, delivery, and commit identity**.

The preview shows the repository, GitHub account, base branch, Work branch, Git
author, and Git actions. For a repository that is not enabled, **Enable repository &
authorize this Work** records both decisions. Enabled repositories use their saved
permission directly. **Decline** saves the decision without enabling the repository
or dispatching an attempt. A push or PR request fails with `repository_unavailable`
when the connected GitHub account cannot push to the repository.

Examples of supported delivery wording:

- `Create a branch in owner/repo` — local changes, without publication.
- `Fix owner/repo and push the changes` — allow push to the assigned Work branch.
- `Open a PR using base branch develop` — allow push and a draft PR.
- `Use release/next as the base` — choose the requested base branch.
- `Don't push or open a PR` — keep changes on the Work volume.

Changes stay local unless the user explicitly asks for a push or draft PR. Mentions
of push notifications and requests to explain publication do not grant publication.
The parser recognizes explicit English phrases; use the **Git actions** control for
other wording. Two different base branches, or a PR request that forbids pushing,
require clarification. Later user messages override earlier delivery instructions.
Runtime output is never approval evidence. A changed delivery scope on an existing
attempt needs a new preview and attempt; retained compute must finish cleanup first.

Adding context invalidates a pending approval. Approval names the exact saved
request; the confirmation cannot substitute another repository or other actions.
Goblin checks account changes and repository disablement again at confirmation and
at execution. Once Work has a repository, answers that name a different repository
are rejected; use separate Work for another repository.

## Application commands and persistence

The authenticated `/api/work/commands` endpoint handles:

| Command | Effect |
| --- | --- |
| `Execute` / `Retry` | Start directly for an enabled repository, or save an enablement preview. Text-only Work dispatches normally. |
| `PrepareRepository` | Select a repository during setup with `repository` and optional `delivery`, or `text` containing the name. Commit identity fields are optional. |
| `AuthorizeRepository` | Confirm the saved `authorizationId`. In one transaction, enable the repository if the preview says so, create the attempt, save the receipt, and enqueue dispatch. |
| `DenyRepository` | Decline the saved `authorizationId`. |

`delivery` has `baseBranch`, `push`, and `openPullRequest` properties. A structured
selection overrides text extraction and still requires standing permission or an
enablement preview. Approval accepts an ID, not a replacement repository or delivery.
The request's `repository` object is a selection, not a grant: supplied `grant` and
`requestedBy` fields are ignored. Command IDs and expected Work versions protect
against lost responses, duplicate submissions, and stale browser actions.

The pending approval and its outcome live in the Work snapshot (`work_item.state`).
Command receipts keep the returned previews, and attempts keep their immutable
grants. `github_repository.enabled` is the durable repository setting. The
enablement write and dispatch intent share the Work transaction. GitHub discovery
runs before that transaction; the connection identity is checked again inside it.
Enabling or disabling a repository fails with `github_connection_in_use` while a
GitHub-backed attempt is queued, starting, running, uncertain, being canceled, pending
cleanup, or keeps a retained workspace.

[Slack](slack-integration.md) users with a linked owner identity can name an enabled
repository in their first request or a reply in the same thread. Slack uses the
stored enabled-repository list without calling GitHub inside the transaction. The
identity check, grant, message receipt, Work state, and dispatch intent commit
together. Plain `yes`, runtime output, and ambiguous names do not authorize setup.
Slack cannot enable a repository or retry failed Work. Claim and broker checks still
enforce the account generation, repository enablement, and actual GitHub access.
Workers receive neither workspace management credentials nor database access.

## Commit attribution

For Work that started in Slack, the attempt records `requestedBy` as
`slack:<workspace>/<user>`, also when the repository is chosen in a later reply.
Goblin checkpoint commits include a `Requested-by` trailer, and the runtime is told
to add it to its own commits. The attempt keeps the reference even if an agent
leaves out the trailer. Goblin does not add Slack names, emails, or
`Co-authored-by` trailers.

## Execution and checkpoints

Grants use policy version 2, the only supported version, with independent
`AllowPush` and `AllowPullRequest` flags. A PR requires both. Requests cannot select
or supply a policy. Fetch and local checkpoint verification are always allowed
within the approved repository. The broker checks the Work branch and operation; the
GitHub adapter checks publication permission again.

The worker pushes only when approved and opens a draft PR when requested on a result
turn. Without push permission it submits a `checkpoint` operation: the broker checks
the Git bundle and records a receipt without calling GitHub. PostgreSQL stores only
checkpoint metadata. Files, including unpublished commits and ignored outputs, stay
on the Work PVC. A new attempt uses that checkout, so its previous commit does not
need to exist on GitHub.
