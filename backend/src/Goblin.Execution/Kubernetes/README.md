# Selected Kubernetes wire models

`Generated/` holds classes for the Kubernetes resources that `SandboxHost` and
`InspectionHost` create or read (Sandbox, Pod, Secret, ConfigMap, and PVC). They use
explicit `System.Text.Json` attributes and are not Work or public HTTP contracts.

[`selection.json`](../../../schemas/kubernetes/selection.json) lists the leaf fields the
hosts need and each schema source's version and checksum. The generator
(`backend/scripts/GenerateKubernetes.cs`) emits only the selected fields and their
parent classes. A path ending at a map or an array of scalars selects the whole value. A
selected path missing from its schema, or a checksum mismatch, fails generation.
`SandboxSuspendPatch` is a separate root so a merge patch does not need the full Sandbox
specification.

[Schema inputs](../../../schemas/kubernetes/) and their selected checksums are the
source of truth; do not copy version numbers into this guide.

Normal .NET builds do not run the generator. Regenerate or check with:

```sh
make kubernetes-generate
make kubernetes-check
dotnet test backend/tests/Goblin.Application.Tests
```

To upgrade either source, replace its schema input, update its version and checksums in
`selection.json`, review the selection, regenerate, and run the tests. Keep generated
models in this execution adapter. The kubelet `/stats/summary` response used by
monitoring is not generated from these inputs.
