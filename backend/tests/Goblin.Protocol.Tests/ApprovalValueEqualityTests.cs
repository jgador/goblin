using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Goblin.Protocol.Tests;

public sealed class ApprovalValueEqualityTests
{
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, ProtocolJson.Options)!;

    [Fact]
    public void NeverDeserializesToTheSameEnumValueInRecordsAndClasses()
    {
        const string json = "\"never\"";

        ApprovalRecord recordValue = Read<ApprovalRecord>(json);
        StringAskForApproval classValue = Read<StringAskForApproval>(json);

        Assert.True(recordValue.Value == AskForApprovalValue.Never);
        Assert.True(classValue.Value == AskForApprovalValue.Never);
    }

    [Fact]
    public void WrapperComparisonUsesValueEqualityForRecordsAndReferenceEqualityForClasses()
    {
        const string json = "\"never\"";

        ApprovalRecord recordValue = Read<ApprovalRecord>(json);
        var recordNever = new ApprovalRecord(AskForApprovalValue.Never);
        AskForApproval classValue = Read<AskForApproval>(json);

        Assert.NotSame(recordNever, recordValue);
        Assert.True(recordValue == recordNever);

        Assert.True(classValue is StringAskForApproval { Value: AskForApprovalValue.Never });
        Assert.False(classValue == AskForApproval.Never);
    }

    [Fact]
    public void StringEnumMemberNameWithoutAConverterRejectsNeverInBothRecordsAndClasses()
    {
        const string json = """{"Value":"never"}""";

        JsonException recordError = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ApprovalRecordWithoutConverter>(json));
        JsonException classError = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ApprovalClassWithoutConverter>(json));

        // Both fail at the enum property, rather than at the containing object.
        Assert.Equal("$.Value", recordError.Path);
        Assert.Equal("$.Value", classError.Path);
    }

    [Fact]
    public void NumericEnumValuesWorkWithoutAConverterInBothRecordsAndClasses()
    {
        const string json = """{"Value":2}""";

        ApprovalRecordWithoutConverter recordValue = JsonSerializer.Deserialize<ApprovalRecordWithoutConverter>(json)!;
        ApprovalClassWithoutConverter classValue = JsonSerializer.Deserialize<ApprovalClassWithoutConverter>(json)!;

        Assert.True(recordValue.Value == ApprovalValueWithoutConverter.Never);
        Assert.True(classValue.Value == ApprovalValueWithoutConverter.Never);
    }

    // AskForApprovalValue has a converter attribute. This test-only equivalent
    // keeps its wire-name attributes but deliberately has no JsonConverter.
    private enum ApprovalValueWithoutConverter
    {
        [JsonStringEnumMemberName("untrusted")]
        Untrusted,
        [JsonStringEnumMemberName("on-request")]
        OnRequest,
        [JsonStringEnumMemberName("never")]
        Never,
    }

    private sealed record ApprovalRecordWithoutConverter
    {
        public ApprovalValueWithoutConverter Value { get; init; }
    }

    private sealed class ApprovalClassWithoutConverter
    {
        public ApprovalValueWithoutConverter Value { get; init; }
    }

    // Use the generated class's converter and enum; only the wrapper is a record.
    [JsonConverter(typeof(ProtocolValueConverter<ApprovalRecord, AskForApprovalValue>))]
    private sealed record ApprovalRecord(AskForApprovalValue Value)
        : IProtocolValue<ApprovalRecord, AskForApprovalValue>
    {
        public static ApprovalRecord FromValue(AskForApprovalValue value) => new(value);
    }
}
