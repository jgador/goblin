# Work experience preview

Run Goblin with `npm ci` followed by `npm start`, then open
http://localhost:8787/work. The connection page also links to the preview, and
**Connection settings** returns to the existing account setup.

The preview explores the smallest native work surface for Goblin:

- Start with a conversation and choose **Track this work** when it should become
  a work item.
- Assign work to Goblin, keeping the underlying executor out of the assignment
  experience.
- Keep the request, conversation, decisions, activity, and outputs together.
- Let Goblin ask for input, then bring back a result to approve or revise.

The [architecture plan](architecture-refactoring-plan.md) treats Work as durable
and independent of a conversation; **Track this work** is one proposed way to
create it. Codex is the current primary agent integration, with Claude and
GitHub Copilot as future targets. The assignment experience above explores a
stable Goblin identity across runtime choices. How users choose a runtime or
hand Work to another agent remains open, and this preview does not implement
those behaviors.

## Try the experience

1. Open **Make Goblin easier to set up**, choose an approach, and select
   **Continue with this**.
2. Select **Preview a finished result**, open **Outputs**, and read the proposal.
3. Choose **Ask for changes** and send feedback, or **Approve & complete**.
4. Open **Activity** to see the decisions and handoffs.
5. Start a **New conversation**, send a request, and select **Track this work**.

On narrow screens, select a work item to open its details and use **Work** to
return to the list.

## Scope

All work, replies, timestamps, and outputs are examples. State lives only in the
current page and resets on refresh or **Reset preview**. The preview does not
call any agent runtime, create GitHub issues, or store user input on the server.

The preview is a public static route, like the connection page. It contains no
account or workspace data and needs no provider connection. Existing session
checks and authenticated APIs are unchanged. Future integration of real work
must use authenticated APIs and durable server-side state.

The browser code lives in `frontend/src/work/app.ts`. The existing TypeScript build
emits its JavaScript; `frontend/scripts/copy-assets.mts` copies the HTML and CSS.
`GoblinApplication` serves the explicit `/work`, `/work/`,
`/work/app.js`, and `/work/styles.css` routes.

It reuses the existing vector logo and local font stack, with no external font
requests, inline scripts, or inline styles. The existing content-security policy
remains unchanged.

## Verification

`npm run typecheck` checks TypeScript and the .NET build. After a successful
`npm run build`, run
`npx playwright test tests/e2e/work.spec.ts` for the preview journeys,
mobile navigation, content-security-policy checks, and escaped user input.
The browser fixture uses the existing simulated backend and never contacts
OpenAI.
