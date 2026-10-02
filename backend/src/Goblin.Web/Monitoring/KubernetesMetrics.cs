using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Goblin.Web.Monitoring;

public sealed class MonitoringNode
{
    [JsonPropertyName("metadata")]
    public MonitoringMetadata? Metadata { get; init; }

    [JsonPropertyName("spec")]
    public MonitoringNodeSpec? Spec { get; init; }

    [JsonPropertyName("status")]
    public MonitoringNodeStatus? Status { get; init; }
}

public sealed class MonitoringMetadata
{
    [JsonPropertyName("name")]
    public string? Name { get; init; } = "";

    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; } = "";

    [JsonPropertyName("uid")]
    public string? Uid { get; init; } = "";

    [JsonPropertyName("deletionTimestamp")]
    public string? DeletionTimestamp { get; init; }
}

public sealed class MonitoringNodeSpec
{
    [JsonPropertyName("unschedulable")]
    public bool Unschedulable { get; init; }
}

public sealed class MonitoringNodeStatus
{
    [JsonPropertyName("capacity")]
    public MonitoringCapacity? Capacity { get; init; }

    [JsonPropertyName("addresses")]
    public MonitoringAddress[]? Addresses { get; init; } = [];

    [JsonPropertyName("conditions")]
    public MonitoringCondition[]? Conditions { get; init; } = [];

    [JsonPropertyName("nodeInfo")]
    public MonitoringNodeInfo? NodeInfo { get; init; }
}

public sealed class MonitoringCapacity
{
    [JsonPropertyName("cpu")]
    public string? Cpu { get; init; } = "";

    [JsonPropertyName("memory")]
    public string? Memory { get; init; } = "";
}

public sealed class MonitoringAddress
{
    [JsonPropertyName("type")]
    public string? Type { get; init; } = "";

    [JsonPropertyName("address")]
    public string? Address { get; init; } = "";
}

public sealed class MonitoringCondition
{
    [JsonPropertyName("type")]
    public string? Type { get; init; } = "";

    [JsonPropertyName("status")]
    public string? Status { get; init; } = "";
}

public sealed class MonitoringNodeInfo
{
    [JsonPropertyName("kernelVersion")]
    public string? KernelVersion { get; init; } = "";

    [JsonPropertyName("osImage")]
    public string? OsImage { get; init; } = "";
}

public sealed class MonitoringPodList
{
    [JsonPropertyName("items")]
    public MonitoringPod[]? Items { get; init; }
}

public sealed class MonitoringPod
{
    [JsonPropertyName("metadata")]
    public MonitoringMetadata? Metadata { get; init; }

    [JsonPropertyName("spec")]
    public MonitoringPodSpec? Spec { get; init; }

    [JsonPropertyName("status")]
    public MonitoringPodStatus? Status { get; init; }
}

public sealed class MonitoringPodSpec
{
    [JsonPropertyName("nodeName")]
    public string? NodeName { get; init; } = "";
}

public sealed class MonitoringPodStatus
{
    [JsonPropertyName("phase")]
    public string? Phase { get; init; } = "";

    [JsonPropertyName("conditions")]
    public MonitoringCondition[]? Conditions { get; init; } = [];

    [JsonPropertyName("containerStatuses")]
    public MonitoringContainerStatus[]? ContainerStatuses { get; init; } = [];
}

public sealed class MonitoringContainerStatus
{
    [JsonPropertyName("restartCount")]
    [JsonConverter(typeof(MonitoringNumberConverter))]
    public double? RestartCount { get; init; }
}

public sealed class KubeletSummary
{
    [JsonPropertyName("node")]
    public KubeletNodeStats? Node { get; init; }

    [JsonPropertyName("pods")]
    public KubeletPodStats[]? Pods { get; init; } = [];
}

public sealed class KubeletNodeStats
{
    [JsonPropertyName("nodeName")]
    public string? NodeName { get; init; } = "";

    [JsonPropertyName("startTime")]
    public string? StartTime { get; init; } = "";

    [JsonPropertyName("cpu")]
    public KubeletCpuStats? Cpu { get; init; }

    [JsonPropertyName("memory")]
    public KubeletMemoryStats? Memory { get; init; }

    [JsonPropertyName("fs")]
    public KubeletFilesystemStats? Fs { get; init; }
}

public sealed class KubeletPodStats
{
    [JsonPropertyName("podRef")]
    public KubeletPodReference? PodRef { get; init; }

    [JsonPropertyName("cpu")]
    public KubeletCpuStats? Cpu { get; init; }

    [JsonPropertyName("memory")]
    public KubeletMemoryStats? Memory { get; init; }
}

public sealed class KubeletPodReference
{
    [JsonPropertyName("uid")]
    public string? Uid { get; init; } = "";
}

public sealed class KubeletCpuStats
{
    [JsonPropertyName("time")]
    public string? Time { get; init; }

    [JsonPropertyName("usageNanoCores")]
    [JsonConverter(typeof(MonitoringNumberConverter))]
    public double? UsageNanoCores { get; init; }
}

public sealed class KubeletMemoryStats
{
    [JsonPropertyName("time")]
    public string? Time { get; init; }

    [JsonPropertyName("workingSetBytes")]
    [JsonConverter(typeof(MonitoringNumberConverter))]
    public double? WorkingSetBytes { get; init; }

    [JsonPropertyName("availableBytes")]
    [JsonConverter(typeof(MonitoringNumberConverter))]
    public double? AvailableBytes { get; init; }
}

public sealed class KubeletFilesystemStats
{
    [JsonPropertyName("time")]
    public string? Time { get; init; }

    [JsonPropertyName("capacityBytes")]
    [JsonConverter(typeof(MonitoringNumberConverter))]
    public double? CapacityBytes { get; init; }

    [JsonPropertyName("usedBytes")]
    [JsonConverter(typeof(MonitoringNumberConverter))]
    public double? UsedBytes { get; init; }

    [JsonPropertyName("availableBytes")]
    [JsonConverter(typeof(MonitoringNumberConverter))]
    public double? AvailableBytes { get; init; }
}


// A missing, nonnumeric, or invalid reading remains unknown rather than zero.
internal sealed class MonitoringNumberConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double value)) return value;
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is { } number) writer.WriteNumberValue(number);
        else writer.WriteNullValue();
    }
}
