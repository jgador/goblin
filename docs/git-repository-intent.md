# Git repository intent and authorization

Goblin currently supports GitHub repositories only. Repository authority is owned
and enforced by Goblin. Users can enable repositories in Settings or through an
explicit approval in a Work conversation. An authenticated request naming an
enabled repository can start directly. Goblin binds that standing permission and
the user's delivery instructions to a fresh, immutable grant for each attempt.
An agent cannot approve its own request or enable a repository. Failed Work still
requires an explicit retry, and uncertain execution requires reconciliation.

## Conversation flow

Start Work with a GitHub URL, `owner/repository`, or a unique short name among known
repositories. Disabled repositories participate in short-name ambiguity checks.
Several matches require selection of the full name. The repository form also
accepts a previously unknown full name or GitHub URL, without a trip to Settings.

Goblin looks up repository metadata through its connected GitHub account before
saving the preview. Metadata discovery does not fetch a checkout or enable access.
For enabled repositories, the default branch comes from the verified repository
catalog, and Goblin generates an isolated `goblin/<work>/<attempt>` branch. The Git
commit name defaults to the connected GitHub login, and its email defaults to
`<account-id>+<login>@users.noreply.github.com`. This requires no private email scope.
An explicit base branch or commit identity overrides those defaults. Branch,
delivery, and identity controls are optional advanced settings in the Work UI.

The enablement preview shows the connected account, full repository, base branch,
generated Work branch, Git author, push permission, and draft PR permission. For a repository
that is not enabled, **Enable repository & authorize this Work** explicitly records
both decisions. Enabled repositories use their saved permission directly. Declining saves
the decision without enabling a repository or dispatching an attempt.

Examples of supported delivery wording:

- `Create a branch in owner/repo` — local changes, without publication.
- `Fix owner/repo and push the changes` — allow push to the assigned Work branch.
- `Open a PR using base branch develop` — allow push and a draft PR.
- `Use release/next as the base` — choose the requested base branch.
- `Don't push or open a PR` — retain local changes on the Work volume.

Changes stay local unless the user explicitly requests a push or draft PR. Mentions
of push notifications and requests to explain publication do not grant publication.
Extraction uses explicit English phrases. Optional Git action controls resolve
wording the parser does not recognize. Contradictory base branches
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
| `Execute` / `Retry` | Resolve intent and authorize an enabled repository directly, or save an enablement preview. Text-only Work dispatches normally. Retry remains an explicit action. |
| `PrepareRepository` | Select a repository during setup. Enabled repositories start directly; others save an enablement preview. Accept `repository` and optional `delivery`, or `text` with the repository name. Commit identity fields are optional. Explicit short names supplied in `text` are resolved against the connected account's accessible repository list. |
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

The Web approval button is an authenticated user action for repository enablement.
Slack users with an explicitly linked owner identity can name an enabled repository
in their initial request or a reply in the same thread. The adapter checks that
identity again when processing the message. Repository permission, immutable grant,
message receipt, Work state, and dispatch intent are checked or committed in one
transaction. Slack uses the previously verified enabled-repository catalog, avoiding
network discovery under the transaction lock. Claim and broker checks still enforce
account generation, repository enablement, and actual GitHub access.

Plain `yes`, runtime output, and ambiguous repository names do not authorize setup.
Slack cannot enable a disabled repository or retry failed Work. A blocked selection
is explained in the originating thread. Workers receive neither workspace
management credentials nor database access. Teams and management MCP remain deferred.

The request's repository object is now an application selection, not an executable
grant. Supplied `grant` and `requestedBy` fields cannot become execution authority
or attribution. Old command fingerprints containing a repository object have a
different shape; refresh before resubmitting commands across this unreleased update.
Existing attempt grants, Work snapshots, and field names remain readable.

## Commit attribution

The originating authenticated Slack workspace/user reference is captured on the
repository attempt as `requestedBy`, including when selection happens in a later
reply. Goblin-generated checkpoint commits include a `Requested-by` trailer, and
the runtime is instructed to include it in its own commits. The durable attempt
retains the reference even if an agent omits a commit trailer.

Slack's existing `users:read` permission can provide a profile name, but Goblin does
not currently resolve names for commit attribution. Reading email additionally
requires `users:read.email`, which the installed manifest does not request. A Slack
email is not proof of a linked GitHub account. Goblin therefore does not invent an
email or add a `Co-authored-by` trailer. Explicit identity linking and consent to
publish an email would be needed for that richer attribution.

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

Verification on 2026-09-26: `make test` passed its build, generated-protocol checks,
.NET tests, and all 88 HTTP/deployment checks. The final .NET run after the resume
and retry regressions passed 279 tests, including 113 application tests against an
isolated PostgreSQL 16 server. Six certificate tests were skipped because that
server used a Unix socket. Seventeen relevant browser journeys passed; the six
approval/control journeys passed again after adding chat-correction coverage.
The pinned Codex initialization, isolated credential storage, replacement, and
logout checks passed. No live GitHub publication or authenticated model task was run.
