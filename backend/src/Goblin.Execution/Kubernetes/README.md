# Selected Kubernetes wire models

The execution adapter uses generated classes for the Kubernetes resources it
creates or reads. The classes have explicit `System.Text.Json` attributes; they are
not Work or public HTTP contracts. `SandboxHost` and `InspectionHost` build typed
Sandbox, Secret, ConfigMap, and PVC requests and read typed Sandbox, Pod, and PVC
responses.

[`selection.json`](../../../schemas/kubernetes/selection.json) lists the leaf fields
these hosts need, plus each schema source's version and checksum. The generator
includes each leaf's ancestors, follows arrays and references, and emits only the
selected fields. A path such as `spec.podTemplate.spec.volumes.secret.secretName`
selects the nested classes needed to reach that field. A path ending at a map or an
array of scalars selects the whole value type. A selected path missing from its
schema, or a checksum mismatch, fails generation. `SandboxSuspendPatch` is a separate
root so a merge patch does not need the full Sandbox specification.

The schema inputs in `backend/schemas/kubernetes/` are:

- `sandbox-v1beta1.json`: the `openAPIV3Schema` of the `sandboxes.agents.x-k8s.io`
  CRD from the Agent Sandbox v1.0.4 release manifest that
  `deploy/azure/setup/installer.sh` pins.
- `kubernetes-v1.37.0-swagger.json.gz`: the official Kubernetes v1.37.0
  `api/openapi-spec/swagger.json`, compressed.

Normal .NET builds do not run the generator or download schemas. Regenerate or check
the files with:

```sh
make kubernetes-generate
make kubernetes-check
dotnet test backend/tests/Goblin.Application.Tests
```

When upgrading either upstream version, replace its schema input, update its
version and checksums in `selection.json`, review the selection against changed
fields, regenerate, and run the tests. Generated files belong only in this execution
adapter. The kubelet `/stats/summary` response used by monitoring is a separate API
and is not generated from these inputs.
