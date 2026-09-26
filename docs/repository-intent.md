# Repository intent and authorization

Repository authority is owned and enforced by Goblin. Users can enable repositories
in Settings or through an explicit approval in a Work conversation. Every new
repository attempt, including a retry or revision, requires approval of its
repository and Git actions. An agent cannot approve its own request.

## Conversation flow

Start Work with a GitHub URL, `owner/repository`, or a unique short name among known
repositories. Disabled repositories participate in short-name ambiguity checks.
Several matches require selection of the full name. The repository form also
accepts a previously unknown full name or GitHub URL, without a trip to Settings.

Goblin looks up repository metadata through its connected GitHub account before
saving the preview. Metadata discovery does not fetch a checkout or enable access.
The preview shows the connected account, full repository, base branch, generated
Work branch, Git author, push permission, and draft PR permission. For a repository
that is not enabled, **Enable repository & authorize this Work** explicitly records
both decisions. Enabled repositories use **Authorize this Work**. Declining saves
the decision without enabling a repository or dispatching an attempt.

Examples of supported delivery wording:

- `Create a branch in owner/repo` — local changes, without publication.
- `Fix owner/repo and push the changes` — allow push to the assigned Work branch.
- `Open a PR using base branch develop` — allow push and a draft PR.
- `Use release/next as the base` — choose the requested base branch.
- `Don't push or open a PR` — retain local changes on the Work volume.

Extraction uses explicit English phrases. The preview and editable Git action
controls resolve wording the parser does not recognize. Contradictory base branches
or a PR request that forbids pushing require clarification. Later user corrections
override earlier delivery instructions. Runtime output is never approval evidence.
Repository references in decision answers retain the current Work and its earlier
conversation attempt. Changes to an existing attempt's delivery scope require a
new preview and attempt; retained compute must finish cleanup first.

Adding context invalidates a pending approval. Approval names the exact saved
request; its repository or actions cannot be substituted in the confirmation.
Account changes and repository disablement are checked again at confirmation and
execution. Retries preserve failure history and require reconciliation where needed.
The persistent workspace remains bound to one repository; use separate Work for
a different repository once the workspace exists.

## Application commands and persistence

The authenticated `/api/work/commands` surface handles:

| Command | Effect |
| --- | --- |
| `Execute` / `Retry` | Resolve intent and save a preview when repository access is requested; text-only Work can dispatch normally. |
| `PrepareRepository` | Save or replace a preview during repository setup. Accept `repository` and optional `delivery`, or `text` with the repository name. Explicit short names supplied in `text` are resolved against the connected account's accessible repository list. |
| `AuthorizeRepository` | Confirm the saved `authorizationId`; atomically enable the repository when the preview says so, create the attempt, save its receipt, and enqueue dispatch. |
| `DenyRepository` | Decline the saved `authorizationId`. |

`delivery` has `baseBranch`, `push`, and `openPullRequest` properties. A structured
selection overrides text extraction and is still subject to the preview. Requests
cannot supply their own executable grant. Approval accepts an ID rather than a
replacement repository or delivery object. Command IDs and expected Work versions
protect lost responses, duplicate submissions, and stale browser actions.

Pending approval and its outcome live in the PostgreSQL Work snapshot. Command
receipts preserve returned previews; attempts preserve their immutable grants.
`github_repositories.enabled` remains the durable repository setting. The enablement
write and dispatch intent share the Work transaction. GitHub discovery runs outside
that transaction; connection identity is rechecked inside it.

The Web approval button is an authenticated user action. Plain chat text such as
`yes` is not treated as authorization. Future Slack, Teams, or management MCP
adapters can use the same application commands after implementing authenticated
actor mapping and correlation to the exact pending request. Those adapters and a
general management MCP are not implemented here. Workers receive neither workspace
management credentials nor database access.

## Execution and checkpoints

New grants use policy version 2, with independent `AllowPush` and
`AllowPullRequest` flags. PR creation requires both permissions. Fetch and local
Git checkpoint verification are always available within the approved repository.
The trusted broker validates the Work branch and operation; the GitHub adapter
also checks publication permissions. Earlier persisted policy 1 grants retain their
original behavior; new requests cannot choose that policy.

The worker publishes only when approved and creates a draft PR when requested on a
result turn. Without push permission it sends a Git object bundle for local
verification. The `checkpoint` operation has a durable receipt and never calls
GitHub to publish. PostgreSQL stores only checkpoint provenance. Files, including
unpublished commits and ignored outputs, stay on the Work PVC. A new attempt uses
that retained checkout, without requiring its previous commit to exist on GitHub.

The unreleased baseline adds `checkpoint` to the repository-operation constraint.
Existing development databases need the corresponding constraint update or a
fresh disposable database. No filesystem archive columns or new EF properties are
introduced. Core, real PostgreSQL, Git bundle, and browser tests cover these gates;
live GitHub and authenticated model execution remain separate integration checks.

Verification on 2026-09-26: `npm test` passed its build, generated-protocol checks,
.NET tests, and all 88 HTTP/deployment checks. The final .NET run after the resume
and retry regressions passed 279 tests, including 113 application tests against an
isolated PostgreSQL 16 server. Six certificate tests were skipped because that
server used a Unix socket. Seventeen relevant browser journeys passed; the six
approval/control journeys passed again after adding chat-correction coverage.
The pinned Codex initialization, isolated credential storage, replacement, and
logout checks passed. No live GitHub publication or authenticated model task was run.
