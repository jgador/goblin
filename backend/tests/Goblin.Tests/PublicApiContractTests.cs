using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Goblin.Application.Work;
using Goblin.Web;
using Goblin.Web.Monitoring;
using Xunit;
using Api = Goblin.Web.Http.Contracts;

namespace Goblin.Tests;

public sealed class PublicApiContractTests
{
    private static readonly JsonSerializerOptions WorkJson = new(WorkStore.Json)
    {
        Converters = { new LongJsonConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static readonly Type[] Roots =
    [
        typeof(SessionState), typeof(ApiFailure), typeof(InspectionRequest),
        typeof(WorkspacePreferenceState), typeof(SystemOverview), typeof(Api.WorkCommand),
        typeof(Api.WorkView), typeof(Api.AgentView), typeof(Api.ConnectionView),
        typeof(Api.IdentityRequest), typeof(Api.ReservedIdentities), typeof(Api.ConversationCommand),
        typeof(Api.ConversationView), typeof(Api.AuthenticationState), typeof(Api.PromptResult),
        typeof(Api.ModelCatalogView), typeof(Api.RuntimeCapabilities), typeof(Api.GitHubState),
        typeof(Api.EnabledRepository), typeof(Api.RepositoryInfo), typeof(Api.WorkspaceView),
        typeof(Api.SlackState), typeof(Api.SlackLinkCode)
    ];

    [Fact]
    public void PublicContractGraphUsesClassesWithExplicitUnchangedJsonNames()
    {
        // Captured from the public API before replacing records with HTTP classes.
        using Stream stream = typeof(PublicApiContractTests).Assembly
            .GetManifestResourceStream("Goblin.Tests.Contracts.public-api.json")!;
        Dictionary<string, string[]> expected = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)!;
        var visited = new Dictionary<string, Type>();
        // A naming-policy change must not change a single field on the wire.
        var options = new JsonSerializerOptions(WorkJson) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseUpper };

        void Visit(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsArray) { Visit(type.GetElementType()!); return; }
            if (type.IsGenericType)
            {
                foreach (Type argument in type.GetGenericArguments()) Visit(argument);
                return;
            }
            if (type.Namespace?.StartsWith("Goblin.", StringComparison.Ordinal) != true || type.IsEnum) return;
            if (!visited.TryAdd(type.Name, type)) return;
            Assert.Equal(typeof(GoblinApplication).Assembly, type.Assembly);
            Assert.True(type.IsClass, type.FullName);
            Assert.Null(type.GetMethod("<Clone>$"));
            JsonTypeInfo info = options.GetTypeInfo(type);
            Assert.True(expected.ContainsKey(type.Name), "Missing contract snapshot: " + type.FullName);
            Assert.Equal(expected[type.Name], info.Properties.Select(property => property.Name).Order().ToArray());
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Assert.NotNull(property.GetCustomAttribute<JsonPropertyNameAttribute>());
            foreach (JsonPropertyInfo property in info.Properties) Visit(property.PropertyType);
            if (info.PolymorphismOptions is { } polymorphism)
                foreach (JsonDerivedType derived in polymorphism.DerivedTypes) Visit(derived.DerivedType);
        }

        foreach (Type root in Roots) Visit(root);
        Assert.Equal(expected.Keys.Order(), visited.Keys.Order());
    }

    public static IEnumerable<object[]> MappedContracts() => typeof(Api.WorkView).Assembly.GetTypes()
        .Where(type => type.Namespace == typeof(Api.WorkView).Namespace)
        .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        .Where(method => method.Name == "From")
        .Select(method => new object[] { method.DeclaringType! });

    [Theory]
    [MemberData(nameof(MappedContracts))]
    public void HttpMappingPreservesEveryValueAndRoundTrips(Type contract)
    {
        MethodInfo map = contract.GetMethod("From", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)!;
        Type sourceType = map.GetParameters()[0].ParameterType;
        object source = Example(sourceType);
        object response = map.Invoke(null, [source])!;
        foreach (JsonSerializerOptions options in new[] { new JsonSerializerOptions(JsonSerializerDefaults.Web), WorkJson })
        {
            JsonElement expected = JsonSerializer.SerializeToElement(source, sourceType, options);
            JsonElement actual = JsonSerializer.SerializeToElement(response, contract, options);
            Assert.True(JsonElement.DeepEquals(expected, actual), contract.FullName + " changed the JSON payload.");
            object roundTrip = actual.Deserialize(contract, options)!;
            Assert.True(JsonElement.DeepEquals(actual, JsonSerializer.SerializeToElement(roundTrip, contract, options)),
                contract.FullName + " did not round-trip.");
        }
    }

    [Fact]
    public void WorkRequestBindsExistingNamesAndPreservesNestedRepositoryAuthority()
    {
        const string body = """
            {
              "commandId":"9223372036854775807", "workId":"9007199254740993", "action":"PrepareRepository",
              "expectedVersion":"7", "text":"Prepare the repository", "agentId":"11", "attemptId":"12",
              "decisionId":"13", "authorizationId":"14", "model":"model", "reasoningEffort":"high",
              "modelSelectionProvided":true,
              "repository": {
                "repository":"owner/repo", "gitAuthorName":"Author", "gitAuthorEmail":"author@example.test",
                "grant": { "connectionId":"15", "generation":"generation", "accountId":"account",
                  "login":"owner", "repositoryId":"16", "baseBranch":"main", "branch":"goblin/1/2",
                  "policyVersion":2, "allowPush":true, "allowPullRequest":true }
              },
              "delivery": { "baseBranch":"main", "push":true, "openPullRequest":true }
            }
            """;
        Api.WorkCommand request = JsonSerializer.Deserialize<Api.WorkCommand>(body, WorkStore.Json)!;
        Assert.Equivalent(JsonSerializer.Deserialize<WorkCommand>(body, WorkStore.Json), request.ToApplication(), strict: true);
        JsonElement json = JsonSerializer.SerializeToElement(request, WorkJson);
        Assert.Equal("9223372036854775807", json.GetProperty("commandId").GetString());
        Assert.Equal("9007199254740993", json.GetProperty("workId").GetString());
        Assert.Equal("15", json.GetProperty("repository").GetProperty("grant").GetProperty("connectionId").GetString());
    }

    [Fact]
    public void RequestsPreserveOmittedValuesAndNumericIds()
    {
        Api.WorkCommand work = JsonSerializer.Deserialize<Api.WorkCommand>(
            """{"commandId":1,"workId":2,"action":"Create"}""", WorkStore.Json)!;
        Assert.Equivalent(new WorkCommand(1, 2, WorkAction.Create), work.ToApplication(), strict: true);
        Api.ConversationCommand conversation = JsonSerializer.Deserialize<Api.ConversationCommand>(
            """{"conversationId":"3","messageId":"4","text":"Hello"}""", WorkStore.Json)!;
        Assert.Equivalent(new ConversationCommand(3, 4, "Hello"), conversation.ToApplication(), strict: true);
        Api.IdentityRequest identities = JsonSerializer.Deserialize<Api.IdentityRequest>(
            """{"kinds":["Work","Attempt"]}""", WorkStore.Json)!;
        Assert.Equal([IdentityKind.Work, IdentityKind.Attempt], identities.ToApplication().Kinds);
        InspectionRequest inspection = JsonSerializer.Deserialize<InspectionRequest>(
            """{"id":"9007199254740993","attemptId":"9223372036854775807"}""", WorkStore.Json)!;
        Assert.Equal(9007199254740993L, inspection.Id);
        Assert.Equal(long.MaxValue, inspection.AttemptId);
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("\"9223372036854775808\"")]
    [InlineData("1.5")]
    [InlineData("\"not-an-id\"")]
    public void WorkRequestRejectsInvalidInt64Ids(string value) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Api.WorkCommand>(
            "{\"commandId\":" + value + ",\"workId\":1,\"action\":\"Create\"}", WorkStore.Json));

    [Fact]
    public void AccountDiscriminatorsAndNullStateRemainStable()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal("""{"type":"apiKey"}""", JsonSerializer.Serialize<Api.AccountView>(new Api.ApiKeyAccountView(), options));
        Assert.Equal("""{"type":"chatgpt","email":"person@example.test","planType":"plus"}""",
            JsonSerializer.Serialize<Api.AccountView>(new Api.ChatGPTAccountView("person@example.test", "plus"), options));
        Assert.Equal("""{"account":null,"login":null,"notice":null,"verification":null,"runtimeReady":false}""",
            JsonSerializer.Serialize(new Api.AuthenticationState(null, null, null, null, false), options));
    }

    private static object Example(Type type, string name = "")
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return name.Equals("repository", StringComparison.OrdinalIgnoreCase) ? "owner/repo" : "example";
        if (type == typeof(long)) return 9007199254740993L;
        if (type == typeof(int)) return 7;
        if (type == typeof(double)) return 12.5;
        if (type == typeof(bool)) return true;
        if (type == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 9, 30, 1, 2, 3, TimeSpan.Zero);
        if (type == typeof(DateTime)) return new DateTime(2026, 9, 30, 1, 2, 3, DateTimeKind.Utc);
        if (type.IsEnum) return Enum.GetValues(type).GetValue(0)!;
        if (type.IsArray)
        {
            Type element = type.GetElementType()!;
            Array values = Array.CreateInstance(element, 1);
            values.SetValue(Example(element, name), 0);
            return values;
        }
        if (type == typeof(Goblin.Contracts.AccountView)) return new Goblin.Contracts.ChatGPTAccountView("person@example.test", "plus");
        ConstructorInfo constructor = Assert.Single(type.GetConstructors());
        ParameterInfo[] parameters = constructor.GetParameters();
        object value = constructor.Invoke([.. parameters.Select(parameter => Example(parameter.ParameterType, parameter.Name!))]);
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.SetMethod is not null && !parameters.Any(parameter => property.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase)))
                property.SetValue(value, Example(property.PropertyType, property.Name));
        return value;
    }
}
