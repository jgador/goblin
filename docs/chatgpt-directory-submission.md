# Optional ChatGPT directory submission

Customer-created plugins use **Add custom MCP server** and need no public Goblin
listing. Choose **Publish publicly** in `/settings?section=chatgpt` to work through
listing preparation, reviewer tests, submission, and publication inside Goblin.
The workflow records the operator's confirmations locally and opens OpenAI's
portals for the external actions. It does not claim that an application has been
submitted or approved based on a link click or a provider check. This reference
covers a separately chosen publication effort; Goblin does not currently have
an approved directory listing.

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
5. Exactly five positive and three negative cases for initial MCP review,
   a demonstration video, tested metadata, and validation of the complete package.
6. Ongoing endpoint monitoring, version compatibility, and response to review
   findings or changed platform requirements.

Do not package customer secrets or use API-key/basic-auth header shortcuts for
public submission. A working private connection does not establish publication
eligibility.

Enter listing details in **Prepare listing**, then the demo video and three
distinct sample Work IDs in **Review tests**. In **Submit**, select **Generate
plugin package**, review both JSON files, and save the ZIP. This generates the
initial version 1.0.0 package with OAuth configuration, a square logo, and five
positive and three negative cases using the supplied IDs. Compare those cases
with actual test results before submission. The generator does not validate
public reachability, domain ownership, or live demo permissions. Draft fields
are kept only while this settings page is open.

In the submission portal, select **Upload new or existing plugin**, choose the
verified publisher, and upload the ZIP. Open **MCPs → Connect**, provide the
client secret, allow the portal's exact callback in Keycloak, and complete the
domain-verification challenge. The host administrator must serve that exact
challenge token at the public URL shown by the portal. Wait for the tool scan,
resolve findings, and complete **Review information → Review details**, including
private reviewer access and availability. Select **Submit for review** and track
the result in the portal. After approval, select **Publish plugin** explicitly.
Open the directory link, install the published listing, connect the intended account,
and test it in a conversation with `@`. Work access still requires grants in
Goblin. The in-app workflow includes these handoffs and expected results.

Current official requirements:

- [Review and endpoint eligibility](https://developers.openai.com/plugins/deploy/app-review)
- [Submission requirements](https://developers.openai.com/plugins/deploy/submission)
- [Plugin guidelines](https://developers.openai.com/plugins/plugin-guidelines)
