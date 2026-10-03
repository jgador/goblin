# C# record audit

This audit covers all 91 record declarations present before the DTO cleanup,
including application code, protocol support, the generator, and test fixtures.
There are now 61 classes in place of those records: eight from the JSON-attribute
cleanup and 53 from the broader usage audit. Thirty records remain.

Names were not the criterion. The audit followed construction, serialization,
mapping, equality, collection comparisons, `with` expressions, persistence, and
message dispatch. A DTO carries input, output, configuration, or a stored snapshot
between components without owning domain state or requiring value semantics.
Those types now use classes. A derived convenience property such as `IsDefault`
does not make an application projection a domain object.

Records remain appropriate for domain values and history held by the aggregate,
production state replaced through immutable updates, and deliberately modeled
value types. Not every retained record uses `with` or equality in production;
the table distinguishes those reasons. Test-only copies and comparisons did not
justify retaining transfer objects. Record equality is shallow for array members;
this audit does not claim deep immutability or structural array equality.

## Converted types

All names in each row were converted. Existing namespaces, visibility, property
types, `init` accessors, constructor argument order/defaults, JSON policies, and
polymorphic metadata were preserved. Class constructor parameters use camelCase,
and callers using named arguments follow the same convention. Classes intentionally
have reference equality and no synthesized record cloning or deconstruction.

| Types | Usage and decision |
| --- | --- |
| `WorkspaceCheckpointWrite`, `WorkspaceFileEntry`, `SlackCredentials`, `SlackAcknowledgement`, `SlackPostMessageRequest`, `CodexWorkContext`, `RepositorySetupOutput`, `SetupCheckOutput` | Previous eight conversions: JSON/form inputs, outputs, and credential storage contracts. Explicit class properties carry the existing JSON attributes. |
| `AccountView`, `ApiKeyAccountView`, `ChatGPTAccountView`, `DeviceLogin`, `Notice`, `AuthenticationState`, `PromptResult` | Account summaries, login details, notices, and operation results passed from integration code to HTTP mapping. Type-pattern matching and JSON polymorphism work with classes; production did not use record equality or cloning. |
| `RuntimeCapabilities`, `RuntimeModel`, `ModelCatalogView` | Capability and catalog projections. The single production `RuntimeModel` copy was an `IsNew` projection into the cache; it now constructs a separate class instance and preserves the source instance. |
| `RepositoryAccount`, `RepositoryInfo`, `RepositoryOperationResult` | Discovery projections, persisted account details, and operation return values. Account/generation checks compare scalar fields. The restart test now compares the restored properties explicitly. |
| `WorkspaceCheckpoint`, `RepositorySetupMemory`, `SetupMemoryWrite`, `DispatchFailureEvidence`, `InspectionAllocation` | Persistence projections, write requests, recovery journal entries, and host allocation inputs. IDs and fields govern their use; record identity/equality did not. Test-only `with` expressions became explicit fixture construction. |
| `ExternalInstallation`, `ExternalMessage`, `ExternalReply` | Integration-to-application evidence and response messages. Installation, actor, event, and message IDs drive validation and deduplication. Tests construct distinct inputs instead of cloning records. |
| `WorkCommand`, `ConversationCommand`, `IdentityRequest`, `ReservedIdentities` | Application input and response contracts. Work command deduplication hashes serialized content, so JSON byte compatibility matters; object equality does not. |
| `WorkView`, `AgentView`, `ConnectionView`, `ConversationMessageSource`, `ConversationMessageView`, `ConversationView`, `EnabledRepository`, `ExternalLinkView`, `ExternalLinkCode`, `ExternalIdentityView`, `InspectionView`, `RepositoryOperationView` | Database/application projections mapped into HTTP responses. Their behavior is data selection and presentation. |
| `DispatchWork`, `ReconcileWork`, `StartInspection`, `StopInspection`, `PublishRepository` | Wolverine messages carrying IDs and turn numbers. Handler routing, outbox delivery, and durable state checks use the message type and its fields, not record semantics. |
| `WorkSnapshot`, `AttemptSnapshot` | Core-owned persistence/worker snapshot contracts created by `Snapshot()` and consumed by `Restore()`. The live state lives in `WorkItem` and `ExecutionAttempt`; these are transfer representations. Their fields and defaults remain identical, and Core gained no serialization dependencies. |
| `WorkerInput` | Serialized worker launch input containing the Work snapshot and runtime settings. |
| `WorkspaceLimits`, `TextHostOptions`, `SandboxOptions`, `RepositoryBrokerOptions`, `ApplicationOptions`, `FixtureOptions` | Configuration carriers. They have no production record equality/copy use; `FixtureOptions` is deserialized by the HTTP test host. |
| `RepositoryProposal` | Private discovery result carried from repository lookup into the application transaction. Its constituent domain objects retain their own semantics. |
| `CodexClient.Pending` | Private request coordination holder containing a live completion source. This is operational state rather than a serializable DTO; a class also better expresses its reference identity. |

## Every retained record

| Record | Why it remains a record; evidence |
| --- | --- |
| [`ExecutionTarget`](../backend/src/Goblin.Core/Work/WorkTypes.cs) | Validates and normalizes execution requirements. Production compares entire targets in `WorkItem.TryClaimExecution`, `WorkItem.AuthorizeRepository`, and `RepositoryBroker`; reference equality would break restored authorization matching. |
| [`RepositoryChange`](../backend/src/Goblin.Core/Work/WorkTypes.cs) | Validated repository/Git identity value nested in `ExecutionTarget`. Its value equality participates in the target comparisons that enforce authorization. |
| [`RepositoryGrant`](../backend/src/Goblin.Core/Work/WorkTypes.cs) | Captured authority with an `Authorize` operation. Its values participate in nested target equality, including after restoration. It is not a passive request to grant access. |
| [`GitDeliveryIntent`](../backend/src/Goblin.Core/Work/RepositoryAuthorization.cs) | Validates branch/push/PR intent. `RepositoryIntent.Parse` repeatedly uses `with` to derive revised intent without mutating the original. |
| [`RepositoryAuthorization`](../backend/src/Goblin.Core/Work/RepositoryAuthorization.cs) | Aggregate-owned pending/authorized/denied/invalidated decision. Production transitions replace it with `with`, preserving the captured target and timestamps. |
| [`WorkDecision`](../backend/src/Goblin.Core/Work/WorkTypes.cs) | Decision history owned by `WorkItem`; answering replaces the current value using `with`, recording the answer and its time. |
| [`WorkResult`](../backend/src/Goblin.Core/Work/WorkTypes.cs) | Review history owned by `WorkItem`; change requests and approval use `with` to preserve the original result and attach the review outcome. |
| [`WorkAttention`](../backend/src/Goblin.Core/Work/WorkTypes.cs) | Aggregate-owned reason/failure value that determines which lifecycle transitions are allowed. Retained as a domain value, not because of a production clone/equality call. |
| [`WorkEvent`](../backend/src/Goblin.Core/Work/WorkTypes.cs) | Append-only domain history held in `WorkItem._history`, with ordering and provenance established by the aggregate. Serialization is a secondary use. |
| [`WorkMessage`](../backend/src/Goblin.Core/Work/WorkTypes.cs) | Accepted context stored in the aggregate; `AddContext` enforces identity/text rules and records the associated event. It is the domain entry, while `ConversationMessageView` is a transfer projection. |
| [`WorkArtifact`](../backend/src/Goblin.Core/Work/WorkTypes.cs) | Artifact provenance held by the aggregate. `AddArtifact` checks attempt ownership and deduplicates references before accepting the value. |
| [`ExecutionSession`](../backend/src/Goblin.Core/Work/WorkTypes.cs) | Runtime session reference value retained by live attempts and historical turns. It models execution provenance independently of any runtime transport object. No production `with` use is required for that role. |
| [`ExecutionTurnRecord`](../backend/src/Goblin.Core/Work/WorkTypes.cs) | A completed turn's provenance, appended to `ExecutionAttempt.PriorTurns` before continuation changes the live attempt. This is retained domain history, unlike the exported `AttemptSnapshot`. |
| [`WorkWorkspace`](../backend/src/Goblin.Core/Work/WorkWorkspace.cs) | Aggregate-owned durable workspace and last-writer identity. It survives attempts; restoration tests also compare this value after reloading. |
| [`WorkRepositoryRequest`](../backend/src/Goblin.Core/Work/WorkRepository.cs) | Pending repository requirement stored as aggregate state and interpreted by authorization/continuation rules. It is not an HTTP/application command despite the `Request` suffix. |
| [`RepositorySetup`](../backend/src/Goblin.Core/Repositories/RepositorySetup.cs) | Domain description of a learned setup recipe, validated and interpreted by `RepositorySetupRules` and the workspace verifier. The adapter already has a separate `RepositorySetupOutput` class. Retained for that domain-value role; its `with` uses are test-only. |
| [`SetupCheck`](../backend/src/Goblin.Core/Repositories/RepositorySetup.cs) | Command/expected-output verification value inside a recipe. The verifier executes its command and checks the expected result; `SetupCheckOutput` is the separate transfer representation. |
| [`SetupFile`](../backend/src/Goblin.Core/Repositories/RepositorySetup.cs) | File path/hash value whose equality is required in production: `RepositorySetupWorkspace.SelectAsync` and `VerifyAsync` use `SequenceEqual` to detect changed inputs. A class with reference equality would incorrectly reject matching files. |
| [`VerifiedRepositorySetup`](../backend/src/Goblin.Core/Repositories/RepositorySetup.cs) | Evidence value combining a recipe, checked file identities, and configuration hash. Reuse and persistence validate this evidence as one observation. No production clone/equality dependency is claimed for the wrapper itself. |
| [`ExecutionObservation`](../backend/src/Goblin.Contracts/ExecutionContracts.cs) | Shared immutable execution outcome/progress value. `LocalTextHost`, `SandboxHost`, `ExecutionWorker`, and `SandboxWorker` derive enriched copies with turn numbers, checkpoints, artifacts, and setup changes. It is serialized, but its role extends beyond transfer. |
| [`SlackConnectionView`](../backend/src/Goblin.Contracts/Conversations/ExternalConversations.cs) | Despite `View`, this is `SlackConnection`'s stored internal connection state. Recovery derives an updated state with `with`. HTTP receives a separate mapped contract. |
| [`SlackSetupView`](../backend/src/Goblin.Contracts/Conversations/ExternalConversations.cs) | Stored setup state with multiple production `with` transitions. `SlackSetup.View` also returns a session-filtered copy that hides the command without mutating shared state. |
| [`GitHubState`](../backend/src/Goblin.Integrations.GitHub/GitHubConnection.cs) | Shared connection/login state replaced under a lock. Production `with` updates preserve the other fields while updating device codes, notices, and availability. |
| [`CodexOptions`](../backend/src/Goblin.Integrations.Codex/CodexOptions.cs) | Configuration with executable discovery/stamping and process-start construction behavior. `ConfigureCodex` uses immutable copying in the production entry point and test host. |
| [`ProcessIdentity`](../backend/src/Goblin.Execution/ProcessIdentity.cs) | Process identity value with `Capture`, `CanObserve`, and `Matches` behavior, including boot/namespace/PID-reuse checks. `Capture` enriches it with `with` on Linux. |
| [`SandboxAddress`](../backend/src/Goblin.Execution/SandboxHost.cs) | Private namespace/name address value that derives Kubernetes resource paths through `Core` and `Sandboxes`. It is not serialized or exposed as a transfer contract. Retained for its address-value role, not an observed equality dependency. |
| [`ProtocolNull`](../backend/src/Goblin.Protocol/ProtocolJson.cs) | A readonly record struct representing the schema's single null value, with a dedicated converter. A class would introduce reference/null semantics into a deliberately modeled value type. |
| [`ProtocolVariant`](../backend/scripts/GenerateProtocol.cs) | Private readonly record struct describing a union alternative during code generation. It is local generator metadata used to emit converter branches, with no runtime/API/persistence transfer role. |
| [`ApprovalRecord`](../backend/tests/Goblin.Protocol.Tests/ApprovalValueEqualityTests.cs) | Deliberate test fixture proving record value equality versus generated class reference equality. Converting it would remove the behavior being tested. |
| [`ApprovalRecordWithoutConverter`](../backend/tests/Goblin.Protocol.Tests/ApprovalValueEqualityTests.cs) | Deliberate test fixture comparing record and class enum deserialization when no converter is present. The record/class distinction is the test's subject. |

## Compatibility and verification

The conversion preserves the JSON representation while removing record-specific
object behavior from transfer types. Existing round-trip tests now compare
properties rather than depending on synthesized record equality. The model cache
still constructs a separate result when adding `IsNew`.

A comparison against assemblies built before the broad conversion covered 50
converted contracts and 298 serialization cases: populated objects, round trips,
and omitted fields under both default and Web naming policies. Every serialized
string matched, including property order. This matters for command fingerprints,
stored snapshots, and persisted setup evidence. Startup callbacks/live task
holders are not serialization subjects; the test-host options are exercised by
the HTTP harness.

The .NET test projects passed 353 tests; 41 PostgreSQL tests were skipped because
their opt-in connections were unavailable. Generated contract-value checks,
their five tests, and protocol generation/self-tests also passed. This does not
claim live PostgreSQL or authenticated runtime coverage.

The HTTP integration checks passed 25 tests covering authentication, GitHub,
preferences, and Work preview/security; the PostgreSQL-backed Work command test
was skipped. The compiled harness was pointed at the new test-host assembly in
`.artifacts/record-audit/`; it exercised the converted `FixtureOptions` class.
