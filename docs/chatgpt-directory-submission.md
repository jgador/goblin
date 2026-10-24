# Optional ChatGPT directory submission

Customer-created plugins use **Add custom MCP server** and need no public Goblin
listing. This guide covers a future, separately chosen publication effort; Goblin
does not currently have an approved directory listing.

Normal public review uses one stable, publicly reachable HTTPS endpoint.
Customer-specific template URLs are restricted to trusted developers with an
established OpenAI relationship. Resolve eligibility before assuming a listing
can route to arbitrary customer domains and OAuth providers. Secure MCP Tunnels
do not satisfy the public submission endpoint requirement.

Prepare these separately from customer installation:

1. Publisher/business identity, domain ownership, support contact, privacy policy,
   descriptions, artwork, and release ownership.
2. A stable review endpoint and demonstration environment matching the submitted
   contract. An approved template route needs a demo matching its real URL pattern.
3. A dedicated sample account usable without private networks, MFA, email/SMS
   challenges, or magic links. Keep neutral sample data isolated from customers.
4. Complete OAuth discovery, resource binding, callbacks, consent, revocation,
   permission annotations, minimal outputs, and safe errors.
5. At least five positive and three negative reviewer scenarios, a demonstration
   video, tested metadata, and validation of the complete published package.
6. Ongoing endpoint monitoring, version compatibility, and response to review
   findings or changed platform requirements.

Do not package customer secrets or use API-key/basic-auth header shortcuts for
public submission. A working private connection does not establish publication
eligibility. Review current requirements before starting:

- [Review and endpoint eligibility](https://developers.openai.com/plugins/deploy/app-review)
- [Submission requirements](https://developers.openai.com/plugins/deploy/submission)
- [Plugin guidelines](https://developers.openai.com/plugins/plugin-guidelines)
