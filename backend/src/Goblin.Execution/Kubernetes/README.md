# Selected Kubernetes wire models

The execution adapter uses generated classes for the Kubernetes resources it
creates or observes. The classes have explicit `System.Text.Json` attributes;
they are not Work or public HTTP contracts. `SandboxHost` and `InspectionHost`
build typed `Sandbox`, `Secret`, `ConfigMap`, and PVC requests and read typed
Sandbox, Pod, and PVC responses.

[`selection.json`](../../../schemas/kubernetes/selection.json) lists the leaf
fields needed by those hosts. The generator includes each leaf's ancestors,
traverses arrays and references, and emits only the selected fields and their
types. A path such as `spec.podTemplate.spec.volumes.secret.secretName` selects
the nested classes required to reach that field. A path ending at a map or
array of scalar values selects the entire map or array value type. A selected
path absent from its pinned schema fails generation. The partial
`SandboxSuspendPatch` root has its own class so a merge patch does not require
the full Sandbox specification.

The checked-in schema inputs are:

- `sandbox-v1beta1.json`: the `openAPIV3Schema` extracted from the Agent
  Sandbox v1.0.4 `sandboxes.agents.x-k8s.io` CRD in the release manifest.
  The release manifest SHA-256 is
  `c4f6344b6f04e450b61c433ce6275f2a45bc30f746b55ce47ade454ffa2a1aa7`,
  matching the deployment pin.
- `kubernetes-v1.37.0-swagger.json.gz`: the official Kubernetes v1.37.0
  `api/openapi-spec/swagger.json`, compressed for the repository. The raw
  Swagger SHA-256 is
  `465276aedf437726de5a6ed23a41b86d78bb0d347edf251a755ae26a93cdd8a6`.

The complete schema inputs make adding a selected path offline and repeatable;
they do not produce unselected C# fields. The generator is a .NET 10 C#
file-based app. Normal .NET builds do not run it or download schemas.

```sh
dotnet run --file backend/scripts/GenerateKubernetes.cs
dotnet run --file backend/scripts/GenerateKubernetes.cs -- --self-test --check
dotnet test backend/tests/Goblin.Application.Tests
```

When upgrading either upstream version, refresh its checked-in schema input,
update the version and checksum notes, review the selection against changed
fields, regenerate, and run the tests. Generated files belong only in this
execution adapter. The monitoring kubelet `/stats/summary` response is a
separate API and is outside these schema inputs.
