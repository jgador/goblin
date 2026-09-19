# Work experience

Open `/work` or **Open work** from the connection page, then unlock Goblin.
The screen uses authenticated APIs and PostgreSQL. Sample fixtures remain in
`frontend/src/work/sample-fixtures.ts` for design reference and are not loaded
by the application.

Create Work by describing its intended outcome, assign the Goblin agent, and
choose **Start work** for a text question. **Repository changes** requires a known
GitHub repository, an agent Git author identity, GitHub sign-in, and a configured
sandbox host. See [execution hosting](execution-hosting.md) for setup and limits.

An agent can return a question or an outcome for review. Answering a question or
requesting changes records the context and makes Work ready for another attempt.
**Approve & complete** approves the specific saved outcome. Decisions, attempts,
progress, outputs, and approvals survive refresh and appear in another browser.
Codex completing a turn does not approve or complete Work automatically.

Every observed failure requires attention. Retry creates a new attempt only after
an uncertain execution has been reconciled. Cancellation waits for stopping
confirmation. Cleanup failures also require reconciliation before continuation.
The activity view records the runtime and model reported for each attempt.

The browser distinguishes saving from a confirmed transition. If a response is
lost, it retains the original command in session storage and offers **Resend
command**. The server deduplicates it by command ID and payload; it does not
execute the action twice. **Keep saved state** dismisses that pending submission.
Polling, reconnect, and refresh retrieve authoritative state. Local navigation,
filters, selection, and unsent drafts do not change Work lifecycle.

Conversations store user context. **Track this work** creates and links a Work
item; the user can assign and execute it. Untracked conversations do not invent
assistant replies or start execution. Their classification as research,
investigation, or another product concept remains open.

The existing TypeScript, CSS, Goblin branding, responsive layout, keyboard tabs,
password access, same-origin checks, and content security policy remain in use.
User and runtime text is escaped before rendering.

Run `npm run build` and `npx playwright test tests/e2e/work.spec.ts` with
`GOBLIN_TEST_POSTGRES_ADMIN` and `GOBLIN_TEST_POSTGRES_APP` configured for a
**disposable migrated test database**. Browser tests use real APIs and PostgreSQL
with a deterministic execution fixture; they do not authenticate a real model
or push to GitHub. Without the database setting, durable browser journeys skip.
