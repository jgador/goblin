using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Goblin.Execution;

// A workspace reference is durable state shared by execution and inspection.
// Keep its grammar and Kubernetes paths together so both hosts enforce the
// same ownership boundary after process restarts.
internal sealed record KubernetesWorkspaceAddress(string Namespace, string Name)
{
    internal string Reference => $"k8s/{Namespace}/{Name}";

    internal string Core => $"/api/v1/namespaces/{Namespace}";

    internal string Sandboxes => $"/apis/agents.x-k8s.io/v1beta1/namespaces/{Namespace}/sandboxes";

    internal static KubernetesWorkspaceAddress Create(string @namespace, long workId) =>
        new(@namespace, "work-" + workId.ToString(CultureInfo.InvariantCulture));

    internal static bool IsValidNamespace(string value) =>
        Regex.IsMatch(value, "\\A[a-z0-9](?:[-a-z0-9]{0,61}[a-z0-9])?\\z", RegexOptions.CultureInvariant);

    internal static bool TryParse(string reference, long workId,
        [NotNullWhen(true)] out KubernetesWorkspaceAddress? address)
    {
        string[] parts = reference.Split('/');
        string expectedName = "work-" + workId.ToString(CultureInfo.InvariantCulture);
        if (parts.Length == 3 && parts[0] == "k8s" && IsValidNamespace(parts[1]) && parts[2] == expectedName)
        {
            address = new(parts[1], parts[2]);
            return true;
        }

        address = null;
        return false;
    }
}
