using System.Text.Json.Serialization;

namespace Goblin.Execution;

public sealed class WorkspaceFilesResponse
{
    [JsonPropertyName("files")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkspaceFileEntry[]? Files { get; init; }

    [JsonPropertyName("truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Truncated { get; init; }

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }
}

public sealed class WorkspaceFileEntry
{
    public WorkspaceFileEntry(string path, long size)
    {
        Path = path;
        Size = size;
    }

    [JsonPropertyName("path")]
    public string Path { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }
}

internal sealed class WorkspaceExecStatus
{
    [JsonPropertyName("status")]
    [JsonRequired]
    public string? Status { get; init; }
}
