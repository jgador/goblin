namespace LocalCpuEmbeddings;

internal sealed record MemoryItem(long Id, string SourceType, string SourceReference, string Content, DateTimeOffset CreatedAt, int Importance, string Topic);
internal static class Corpus
{
    // Synthetic fixtures, not assertions about events in the real Goblin repository.
    private static readonly (string Topic, string Content)[] Anchors =
    [
        ("migration", "Deployment 182 failed because migration 0048 exceeded the configured PostgreSQL statement timeout. The release was rolled back; increasing the limit requires approval."),
        ("migration", "The production release could not complete because the database schema upgrade ran longer than the permitted execution period. The team restored the previous application version."),
        ("ssl", "The API startup problem was fixed by installing libssl3 in the execution image. Before that the process exited with error code 127 and could not load libssl.so.3."),
        ("ssl", "The production service would not boot because the operating system crypto dependency was absent. Adding the OpenSSL runtime package restored successful launches."),
        ("automation", "Project decision: stop automatically creating pull requests for goblinctl release changes. Maintaining the automation and its guardrails cost more than it saved. Document a manual release checklist."),
        ("authorization", "Repository authorization: https://github.com/jgador/goblin must first be explicitly enabled in the Goblin UI. A user then approves repository access for the individual Work before execution."),
        ("persistence", "WorkStore.cs in backend/src/Goblin.Application/Work handles durable Work persistence. It commits the snapshot and dispatch intent in a transaction; Wolverine coordinates delivery."),
        ("pr381", "PR #381 on branch fix/api-startup-libssl3 repairs deployment 182 by installing libssl3. Review discussion requests a smoke test before merging."),
        ("authorization", "A repository being listed in the catalog does not grant execution permission. Work-scoped consent is required, and credentials must never enter conversation history."),
        ("automation", "We agreed to maintain release steps by hand rather than open change requests every time the installer input changes. Humans will review which client versions need publishing."),
        ("migration", "Incident follow-up: migration 0048 holds an exclusive table lock while backfilling work_events. Move the backfill to small chunks and verify timeout behavior."),
        ("persistence", "The application writes durable objective history through the work storage adapter. A single atomic database commit saves both the state and the outgoing delivery request.")
    ];
    private static readonly string[] Sources = ["slack", "github_issue", "pull_request", "commit", "email", "deployment_incident", "test_failure", "build_failure", "repository_setup", "documentation", "project_decision", "runtime_observation", "technical_discussion"];
    private static readonly (string Topic, string[] Variants)[] Topics =
    [
        ("cache", ["The npm cache is cold; dependency restoration takes longer than compilation.", "Restore dependencies using the cached lockfile hash and measure cache hit ratio.", "A package cache eviction caused repeated downloads during the worker bootstrap."]),
        ("tests", ["Test WorkClaimConcurrency rejected two competing owners as expected.", "The HTTP assertion returned status 403 because the test fixture lacked an authenticated identity.", "A flaky clock assertion passed locally but failed on the slow CI runner."]),
        ("build", ["Build failed with CS0246: RepositoryCatalog was not found; check the project reference.", "TypeScript compilation returned TS2322 for the event projection. Regenerate the owned protocol definitions.", "Restore failed with NU1301 while reading the package index; the proxy endpoint was unreachable."]),
        ("workspace", ["Suspending compute preserves the workspace volume and Git checkout.", "The branch checkpoint includes the commit SHA and execution attempt provenance.", "The workspace mount was read-only; writing the build output raised EACCES."]),
        ("logging", ["VictoriaLogs ingestion lag increased after a burst of worker exceptions.", "Normalize repeated request lines before sending anomaly summaries to an agent.", "The log watcher should aggregate template counts instead of asking an LLM to read every line."]),
        ("memory", ["FTS5 indexes incoming notes before any local model has produced a vector.", "Embedding status stays pending when spare CPU capacity is unavailable.", "Short bot acknowledgements can remain searchable without spending inference time."]),
        ("ui", ["Keep the Work objective in the center panel and place conversation controls below it.", "The dropdown has inconsistent focus styling compared with the text input.", "The work list selection preserves keyboard navigation and a visible active state."]),
        ("network", ["The callback domain resolved but the TLS handshake was rejected by the proxy.", "The websocket connection closed with code 1006 during a network interruption.", "DNS returned the old service address; clearing the resolver cache restored connectivity."]),
        ("deployment", ["Deployment readiness stayed false because the HTTP health endpoint returned 503.", "A release was rolled back after its configuration file omitted the connection setting.", "The canary revision started successfully, but the routing rule still targeted the old port."]),
        ("authorization_other", ["A Slack workspace invite does not permit opening a private repository.", "The account token expired during a fetch; refresh credentials before the next manual attempt.", "The permission audit records which employee approved the operation."]),
        ("decision_other", ["The team selected a modular monolith to keep operations simple.", "Do not retry failed Work automatically. Surface attention and wait for the operator.", "A durable agent identity survives its temporary runtime sessions."]),
        ("database_other", ["The index reduced query time for pending execution attempts.", "The connection pool hit its limit while background handlers held transactions open.", "A duplicate key violation returned SQLSTATE 23505; inspect the idempotency key."])
    ];
    public static List<MemoryItem> Generate()
    {
        List<MemoryItem> items = [];
        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 2000; i++)
        {
            string topic, content;
            if (i < Anchors.Length) (topic, content) = Anchors[i];
            else if (i % 10 is 0 or 1)
            {
                string[] noise = ["thanks", "+1", "LGTM", "done", "automated health check succeeded", "build bot acknowledgement: received"];
                topic = "noise"; content = noise[(i / 10 + i % 10) % noise.Length];
            }
            else
            {
                var t = Topics[(i * 7 + i / 13) % Topics.Length];
                topic = t.Topic; content = t.Variants[(i + i / 17) % t.Variants.Length];
                string repo = new[] { "goblin", "roslynkit", "payments-api", "deploy-tools", "work-console", "relay-service" }[i % 6];
                content += $" Repository https://github.com/example/{repo}, branch experiment/{topic}-{i % 47}, deployment {900 + i}, issue #{500 + i}.";
                if (i % 3 == 0) content = $"Follow-up from {new[] { "Mira", "Leon", "Sam", "Alex" }[i % 4]} in the engineering thread: " + content + " Reproduced in the test environment. Please retain the diagnostic output with the attempt and record the next verification step.";
                if (i % 11 == 0) content += "\nInvestigation notes: checked the release manifest, compared configuration to the previous revision, inspected the process output, and repeated the reproduction with a fresh checkout. The change is reversible and needs a focused review. This finding is specific to the observed run and does not establish the cause of unrelated failures.";
                if (i % 43 == 0) content += string.Concat(Enumerable.Repeat("\nDocumentation fragment: record the objective, preserve source provenance, check access before execution, keep failed attempts visible, and use a manual review to decide the next action. ", 12));
            }
            string source = Sources[i % Sources.Length];
            items.Add(new(i + 1, source, $"synthetic://{source}/{i + 1}", content, start.AddMinutes(i * 13), topic == "noise" ? 0 : i < Anchors.Length ? 5 : 2, topic));
        }
        // Fixed shuffle prevents a benchmark prefix from selecting only the short anchor fixtures.
        Random random = new(3810048);
        for (int i = items.Count - 1; i > 0; i--) { int j = random.Next(i + 1); (items[i], items[j]) = (items[j], items[i]); }
        return items;
    }
    public static bool ShouldEmbed(MemoryItem item) => !new[] { "thanks", "+1", "lgtm", "done", "automated health check succeeded", "build bot acknowledgement: received" }.Contains(item.Content.Trim().ToLowerInvariant());
}
